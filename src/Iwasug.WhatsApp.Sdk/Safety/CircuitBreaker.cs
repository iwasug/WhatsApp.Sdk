using Iwasug.WhatsApp.Sdk.Messaging;

namespace Iwasug.WhatsApp.Sdk.Safety;

/// <summary>
/// In-process circuit breaker over the send decision (05-04, SAF-05): trips on
/// repeated <see cref="Messaging.SendOutcome"/> failures with exponential backoff,
/// admits exactly one half-open probe, and closes on a successful probe.
/// </summary>
/// <remarks>
/// <para>
/// Signal mapping is deliberately coarse (05-RESEARCH A6): any
/// <see cref="SendOutcomeStatus.ServerError"/> or
/// <see cref="SendOutcomeStatus.TimedOut"/> counts as a failure. The breaker trips
/// when either the consecutive-failure threshold or the consecutive-timeout
/// threshold accumulates; a <see cref="SendOutcomeStatus.Sent"/> outcome resets
/// both streaks.
/// </para>
/// <para>
/// Backoff is time-based gating, not a waited delay: <see cref="ThrowIfOpen"/>
/// refuses fast with <see cref="SafetyRule.CircuitOpen"/> (05-RESEARCH Pattern 5:
/// "all application sends throw/refuse immediately") and the next probe becomes
/// admissible at <see cref="NextProbeAtUtc"/>. The first opening waits the base
/// open duration (30s); every failed probe re-opens with the backoff doubled,
/// capped at the max open duration (10min). A successful probe closes and resets
/// the doubling.
/// </para>
/// <para>
/// Event idempotency: the injected <c>onOpened</c> delegate fires
/// exactly once per Open transition (<c>Closed → Open</c> and
/// <c>HalfOpen → Open</c>). A failure recorded while already Open stays Open
/// without re-firing. Recovery (<c>Open/HalfOpen → Closed</c>) fires nothing.
/// </para>
/// <para>
/// Read-only state (<see cref="State"/>, <see cref="CurrentBackoff"/>,
/// <see cref="NextProbeAtUtc"/>): observing never mutates. Only
/// <see cref="ThrowIfOpen"/> advances <c>Open → HalfOpen</c> when the backoff has
/// elapsed. Cancellation passes through unwrapped (repo discipline).
/// </para>
/// </remarks>
internal sealed class CircuitBreaker
{
    /// <summary>Consecutive-failure trip threshold default (05-04 plan).</summary>
    public const int DefaultFailureThreshold = 5;

    /// <summary>Consecutive-timeout trip threshold default (05-04 plan).</summary>
    public const int DefaultTimeoutThreshold = 3;

    /// <summary>First-open backoff default: 30 seconds.</summary>
    public static readonly TimeSpan DefaultBaseOpenDuration = TimeSpan.FromSeconds(30);

    /// <summary>Backoff cap default: 10 minutes.</summary>
    public static readonly TimeSpan DefaultMaxOpenDuration = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _clock;
    private readonly int _failureThreshold;
    private readonly int _timeoutThreshold;
    private readonly TimeSpan _baseOpen;
    private readonly TimeSpan _maxOpen;
    private readonly Action? _onOpened;
    private readonly object _gate = new();
    private CircuitState _state;
    private int _failureCount;
    private int _timeoutStreak;
    private int _openCount;
    private bool _probeInFlight;
    private TimeSpan _currentBackoff;
    private DateTimeOffset _nextProbeAt;

    /// <summary>
    /// Creates a breaker over <paramref name="clock"/> with trip thresholds and
    /// backoff bounds.
    /// </summary>
    /// <param name="clock">Clock for backoff scheduling; <c>null</c> resolves to <see cref="TimeProvider.System"/>.</param>
    /// <param name="failureThreshold">Consecutive <c>ServerError</c>/<c>TimedOut</c> outcomes that trip the breaker. Must be positive.</param>
    /// <param name="timeoutThreshold">Consecutive <c>TimedOut</c> outcomes that trip the breaker on their own. Must be positive.</param>
    /// <param name="baseOpenDuration">Backoff after the first opening. Must be positive.</param>
    /// <param name="maxOpenDuration">Backoff cap; doubling never exceeds it. Must be at least <paramref name="baseOpenDuration"/>.</param>
    /// <param name="onOpened">Fired exactly once per Open transition, outside the internal lock. May be <c>null</c> when the caller observes <see cref="RecordOutcome"/>'s return value instead.</param>
    /// <exception cref="ArgumentOutOfRangeException">A threshold or bound is out of range.</exception>
    public CircuitBreaker(
        TimeProvider? clock = null,
        int failureThreshold = DefaultFailureThreshold,
        int timeoutThreshold = DefaultTimeoutThreshold,
        TimeSpan? baseOpenDuration = null,
        TimeSpan? maxOpenDuration = null,
        Action? onOpened = null)
    {
        if (failureThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(failureThreshold), "Failure threshold must be positive.");
        }

        if (timeoutThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutThreshold), "Timeout threshold must be positive.");
        }

        TimeSpan baseOpen = baseOpenDuration ?? DefaultBaseOpenDuration;
        TimeSpan maxOpen = maxOpenDuration ?? DefaultMaxOpenDuration;
        if (baseOpen <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(baseOpenDuration), "Base open duration must be positive.");
        }

        if (maxOpen < baseOpen)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOpenDuration), "Max open duration must be at least the base open duration.");
        }

        _clock = clock ?? TimeProvider.System;
        _failureThreshold = failureThreshold;
        _timeoutThreshold = timeoutThreshold;
        _baseOpen = baseOpen;
        _maxOpen = maxOpen;
        _onOpened = onOpened;
        _nextProbeAt = DateTimeOffset.MinValue;
    }

    /// <summary>Current breaker position (read-only; observing never mutates).</summary>
    public CircuitState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Backoff computed at the most recent opening; <see cref="TimeSpan.Zero"/> when never opened.</summary>
    public TimeSpan CurrentBackoff
    {
        get
        {
            lock (_gate)
            {
                return _currentBackoff;
            }
        }
    }

    /// <summary>UTC instant the next probe becomes admissible; <see cref="DateTimeOffset.MinValue"/> when never opened.</summary>
    public DateTimeOffset NextProbeAtUtc
    {
        get
        {
            lock (_gate)
            {
                return _nextProbeAt;
            }
        }
    }

    /// <summary>
    /// Guards one send: returns when the send may proceed (Closed, or an Open
    /// whose backoff elapsed — which advances to HalfOpen and admits exactly one
    /// probe), otherwise throws <see cref="SafetyRuleViolationException"/> with
    /// <see cref="SafetyRule.CircuitOpen"/>. Never emits an event.
    /// </summary>
    /// <param name="cancellationToken">Caller cancellation (unwrapped; checked before any state is touched).</param>
    /// <exception cref="SafetyRuleViolationException">The breaker is Open with backoff remaining, or a HalfOpen probe is already in flight.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public void ThrowIfOpen(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_state == CircuitState.Closed)
            {
                return;
            }

            if (_state == CircuitState.Open)
            {
                if (_clock.GetUtcNow() < _nextProbeAt)
                {
                    throw new SafetyRuleViolationException(SafetyRule.CircuitOpen, "Safety rule violated.");
                }

                _state = CircuitState.HalfOpen;
                _probeInFlight = true;
                return;
            }

            // HalfOpen: exactly one probe is ever in flight.
            if (_probeInFlight)
            {
                throw new SafetyRuleViolationException(SafetyRule.CircuitOpen, "Safety rule violated.");
            }

            _probeInFlight = true;
        }
    }

    /// <summary>
    /// Records one settled inner outcome: success closes and resets the streaks
    /// (firing nothing, even when recovering from Open/HalfOpen); failure
    /// increments the streaks and trips to Open on threshold.
    /// </summary>
    /// <param name="outcome">The settled inner outcome (including <c>TimedOut</c>, which feeds the breaker).</param>
    /// <param name="cancellationToken">Caller cancellation (unwrapped; checked before any state is touched).</param>
    /// <returns><c>true</c> exactly on a fresh Open transition (<c>Closed → Open</c> or failed-probe <c>HalfOpen → Open</c>); <c>false</c> otherwise, including failures recorded while already Open.</returns>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public bool RecordOutcome(SendOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        cancellationToken.ThrowIfCancellationRequested();

        bool opened;
        lock (_gate)
        {
            if (outcome.Status == SendOutcomeStatus.Sent)
            {
                _state = CircuitState.Closed;
                _failureCount = 0;
                _timeoutStreak = 0;
                _probeInFlight = false;
                _openCount = 0;
                opened = false;
            }
            else
            {
                _failureCount++;
                _timeoutStreak = outcome.Status == SendOutcomeStatus.TimedOut ? _timeoutStreak + 1 : 0;

                if (_state == CircuitState.Open)
                {
                    // Already open: no duplicate event.
                    opened = false;
                }
                else if (_state == CircuitState.HalfOpen)
                {
                    // Failed probe: re-open with the next (doubled) backoff.
                    _probeInFlight = false;
                    OpenLocked(_clock.GetUtcNow());
                    opened = true;
                }
                else if (_failureCount >= _failureThreshold || _timeoutStreak >= _timeoutThreshold)
                {
                    OpenLocked(_clock.GetUtcNow());
                    opened = true;
                }
                else
                {
                    opened = false;
                }
            }
        }

        // Outside the lock: a re-entrant observer can never deadlock the machine.
        if (opened)
        {
            _onOpened?.Invoke();
        }

        return opened;
    }

    private void OpenLocked(DateTimeOffset now)
    {
        _openCount++;
        double scaled = _baseOpen.TotalMilliseconds * Math.Pow(2, _openCount - 1);
        double capped = Math.Min(scaled, _maxOpen.TotalMilliseconds);
        _currentBackoff = TimeSpan.FromMilliseconds(capped);
        _state = CircuitState.Open;
        _nextProbeAt = now + _currentBackoff;
    }
}
