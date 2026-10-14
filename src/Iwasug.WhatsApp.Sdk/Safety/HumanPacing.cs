using System.Security.Cryptography;

namespace Iwasug.WhatsApp.Sdk.Safety;

/// <summary>
/// Best-effort presence seam for human-like pacing (05-04, SAF-06): emits one
/// composing/recording signal before the send. Phase 5 ships the no-op fallback;
/// Phase 8 upgrades the implementation to real presence nodes without touching
/// the gate API (05-RESEARCH Pattern 6 + Open Question 2).
/// </summary>
internal interface IPacingPresence
{
    /// <summary>Sends one composing/recording presence signal, best-effort when <paramref name="bestEffort"/> is set.</summary>
    /// <param name="bestEffort">When <c>true</c>, the implementation must never fail the send promise.</param>
    /// <param name="cancellationToken">Caller cancellation (unwrapped).</param>
    Task SendComposingAsync(bool bestEffort, CancellationToken cancellationToken);
}

/// <summary>
/// No-op <see cref="IPacingPresence"/> (05-04, SAF-06): the Phase 5 default when
/// presence nodes are unavailable. The pacing pause still applies; only the wire
/// signal is skipped.
/// </summary>
internal sealed class NoOpPacingPresence : IPacingPresence
{
    /// <summary>The shared no-op instance.</summary>
    public static NoOpPacingPresence Instance { get; } = new();

    private NoOpPacingPresence()
    {
    }

    /// <inheritdoc />
    public Task SendComposingAsync(bool bestEffort, CancellationToken cancellationToken)
    {
        _ = bestEffort;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Human-like pacing (05-04, SAF-06): the active-hours window check plus the
/// bounded typing/recording presence pause. Both stages run on the injected
/// <see cref="TimeProvider"/> so tests stay deterministic.
/// </summary>
/// <remarks>
/// <para>
/// Active hours are evaluated before the inner send: outside the window the send
/// is rejected with <see cref="SafetyRule.InactiveHours"/> (no silent buffering
/// in Phase 5). Bounds are interpreted in
/// <see cref="SafetyOptions.ActiveHoursTimeZone"/> (UTC when unset). A midnight
/// wrap (start &gt;= end) means an overnight window; equal bounds mean a full-day
/// window (no restriction).
/// </para>
/// <para>
/// The presence pause draws a composing duration inside
/// [<see cref="SafetyOptions.PacingPresenceMinMs"/>,
/// <see cref="SafetyOptions.PacingPresenceMaxMs"/>] via
/// <see cref="RandomNumberGenerator"/>, signals presence best-effort (a throwing
/// presence never fails the pause — only <see cref="OperationCanceledException"/>
/// propagates), then waits the drawn duration cancellation-aware.
/// </para>
/// </remarks>
internal sealed class HumanPacing
{
    private readonly TimeProvider _clock;
    private readonly SafetyOptions _options;
    private readonly IPacingPresence _presence;
    private readonly Func<int, int, int> _nextMsDraw;

    /// <summary>
    /// Creates pacing over <paramref name="clock"/> and <paramref name="options"/>
    /// with the no-op presence fallback and a CSPRNG duration draw.
    /// </summary>
    /// <param name="clock">Clock for window evaluation and pause waits; <c>null</c> resolves to <see cref="TimeProvider.System"/>.</param>
    /// <param name="options">Active-hours window, zone, and pause bounds; <c>null</c> resolves to <see cref="SafetyOptions.SafeDefaults"/>.</param>
    public HumanPacing(TimeProvider? clock = null, SafetyOptions? options = null)
        : this(clock, options, NoOpPacingPresence.Instance, nextMsDraw: null)
    {
    }

    /// <summary>
    /// Creates pacing with an explicit presence seam (Phase 8 upgrade path; tests
    /// inject fakes here).
    /// </summary>
    /// <param name="clock">Clock for window evaluation and pause waits; <c>null</c> resolves to <see cref="TimeProvider.System"/>.</param>
    /// <param name="options">Active-hours window, zone, and pause bounds; <c>null</c> resolves to <see cref="SafetyOptions.SafeDefaults"/>.</param>
    /// <param name="presence">Presence seam; <c>null</c> resolves to the no-op fallback.</param>
    public HumanPacing(TimeProvider? clock, SafetyOptions? options, IPacingPresence? presence)
        : this(clock, options, presence, nextMsDraw: null)
    {
    }

    /// <summary>
    /// Creates pacing with an explicit presence seam and duration draw (deterministic tests).
    /// </summary>
    /// <param name="clock">Clock for window evaluation and pause waits; <c>null</c> resolves to <see cref="TimeProvider.System"/>.</param>
    /// <param name="options">Active-hours window, zone, and pause bounds; <c>null</c> resolves to <see cref="SafetyOptions.SafeDefaults"/>.</param>
    /// <param name="presence">Presence seam; <c>null</c> resolves to the no-op fallback.</param>
    /// <param name="nextMsDraw">Draws the pause milliseconds inside <c>(minMs, maxMs)</c>; <c>null</c> uses <see cref="RandomNumberGenerator"/>.</param>
    public HumanPacing(TimeProvider? clock, SafetyOptions? options, IPacingPresence? presence, Func<int, int, int>? nextMsDraw)
    {
        _clock = clock ?? TimeProvider.System;
        _options = SafetyOptions.Validate(options);
        _presence = presence ?? NoOpPacingPresence.Instance;
        _nextMsDraw = nextMsDraw ?? ((minMs, maxMs) => RandomNumberGenerator.GetInt32(minMs, maxMs + 1));
    }

    /// <summary>
    /// Draws one pause duration in milliseconds, clamped into the configured
    /// bounds (a custom draw that escapes the range is pulled back in).
    /// </summary>
    /// <returns>Milliseconds in [<c>PacingPresenceMinMs</c>, <c>PacingPresenceMaxMs</c>].</returns>
    public int NextPauseMs()
    {
        int drawn = _nextMsDraw(_options.PacingPresenceMinMs, _options.PacingPresenceMaxMs);
        return Math.Clamp(drawn, _options.PacingPresenceMinMs, _options.PacingPresenceMaxMs);
    }

    /// <summary>
    /// Rejects sends outside the active-hours window with
    /// <see cref="SafetyRule.InactiveHours"/>; passes inside the window.
    /// </summary>
    /// <param name="cancellationToken">Caller cancellation (unwrapped; checked before the clock is read).</param>
    /// <exception cref="SafetyRuleViolationException">The current time (in the configured zone) is outside the window.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public void AssertInsideActiveHours(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset nowUtc = _clock.GetUtcNow();
        TimeZoneInfo zone = _options.ActiveHoursTimeZone ?? TimeZoneInfo.Utc;
        TimeSpan now = TimeZoneInfo.ConvertTime(nowUtc, zone).TimeOfDay;
        TimeSpan start = _options.ActiveHoursStart;
        TimeSpan end = _options.ActiveHoursEnd;

        bool inside = start == end
            ? true
            : start < end
                ? now >= start && now < end
                : now >= start || now < end;

        if (!inside)
        {
            throw new SafetyRuleViolationException(SafetyRule.InactiveHours, "Safety rule violated.");
        }
    }

    /// <summary>
    /// Applies the human-like presence pause: draws the composing duration,
    /// signals presence best-effort, then waits the duration cancellation-aware.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pause (unwrapped; propagates).</param>
    /// <exception cref="OperationCanceledException">The caller cancelled (from the presence seam or the pause wait).</exception>
    public async Task ApplyPresencePauseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int pauseMs = NextPauseMs();

        try
        {
            await _presence.SendComposingAsync(bestEffort: true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Best-effort presence: a failing signal never fails the send promise.
        }

        if (pauseMs <= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(pauseMs), _clock, cancellationToken).ConfigureAwait(false);
    }
}
