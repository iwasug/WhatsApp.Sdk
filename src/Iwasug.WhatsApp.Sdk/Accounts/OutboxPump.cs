using System.Threading.Channels;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Messaging;
using Iwasug.WhatsApp.Sdk.Options;
using Iwasug.WhatsApp.Sdk.Safety;
using Iwasug.WhatsApp.Sdk.Stores;
using Microsoft.Extensions.Logging;

namespace Iwasug.WhatsApp.Sdk.Accounts;

/// <summary>
/// Per-account durable outbox pump (EXT-01): drains store-backed due rows through the
/// safety gate, scheduling retries with exponential backoff and dead-lettering permanent
/// failures. The pump owns no shadow list — every state transition is a store row
/// transition, so rows survive restarts and the pump recomputes due rows from the store.
/// </summary>
internal sealed class OutboxPump : IAsyncDisposable
{
    private static readonly Random _jitter = new();

    private readonly IOutboxStore _store;
    private readonly Func<OutboxEntry, CancellationToken, Task<SendOutcome>>? _sendFactory;
    private readonly Messenger? _messenger;
    private readonly TimeProvider _clock;
    private readonly OutboxOptions _options;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _ownedCts = new();
    private int _disposed;
    private Task? _runTask;
    private CancellationTokenSource? _runCts;

    /// <summary>The durable store this pump drains.</summary>
    public IOutboxStore Store => _store;

    /// <summary>The policy options driving this pump.</summary>
    public OutboxOptions Options => _options;

    /// <summary>Creates a pump over <paramref name="store"/> and an explicit send delegate.</summary>
    public OutboxPump(
        IOutboxStore store,
        Func<OutboxEntry, CancellationToken, Task<SendOutcome>> send,
        TimeProvider? clock = null,
        OutboxOptions? options = null,
        ILogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sendFactory = send ?? throw new ArgumentNullException(nameof(send));
        _clock = clock ?? TimeProvider.System;
        _options = OutboxOptions.Validate(options);
        _logger = logger;
    }

    /// <summary>Creates a pump over <paramref name="store"/> using a delegate typed as a value function.</summary>
    public OutboxPump(
        IOutboxStore store,
        Func<OutboxEntry, CancellationToken, ValueTask<SendOutcome>> send,
        TimeProvider? clock = null,
        OutboxOptions? options = null,
        ILogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sendFactory = (entry, ct) => send(entry, ct).AsTask();
        _clock = clock ?? TimeProvider.System;
        _options = OutboxOptions.Validate(options);
        _logger = logger;
    }

    /// <summary>Creates a pump over <paramref name="store"/> and a <see cref="Messenger"/> facade.</summary>
    public OutboxPump(
        IOutboxStore store,
        Messenger messenger,
        TimeProvider? clock = null,
        OutboxOptions? options = null,
        ILogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _messenger = messenger ?? throw new ArgumentNullException(nameof(messenger));
        _clock = clock ?? messenger.Connection.Clock ?? TimeProvider.System;
        _options = OutboxOptions.Validate(options);
        _logger = logger;
        _sendFactory = DefaultSendAsync;
    }

    /// <summary>Creates a pump over <paramref name="store"/> with an explicit safety gate and optional messenger.</summary>
    public OutboxPump(
        IOutboxStore store,
        ISafetyGate safetyGate,
        Messenger? messenger = null,
        TimeProvider? clock = null,
        OutboxOptions? options = null,
        ILogger? logger = null,
        Func<OutboxEntry, CancellationToken, Task<SendOutcome>>? send = null)
    {
        _ = safetyGate;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _messenger = messenger;
        _clock = clock ?? messenger?.Connection?.Clock ?? TimeProvider.System;
        _options = OutboxOptions.Validate(options);
        _logger = logger;
        _sendFactory = send ?? (_messenger is not null ? DefaultSendAsync : null);
        if (_sendFactory is null)
        {
            throw new ArgumentException("A send delegate or messenger is required.", nameof(send));
        }
    }

    /// <summary>Clock-first convenience constructor.</summary>
    public OutboxPump(
        TimeProvider clock,
        IOutboxStore store,
        Func<OutboxEntry, CancellationToken, Task<SendOutcome>> send,
        OutboxOptions? options = null,
        ILogger? logger = null)
        : this(store, send, clock, options, logger)
    {
    }

    /// <summary>Clock-first with store and messenger.</summary>
    public OutboxPump(
        TimeProvider clock,
        IOutboxStore store,
        Messenger messenger,
        OutboxOptions? options = null,
        ILogger? logger = null)
        : this(store, messenger, clock, options, logger)
    {
    }

    private Task<SendOutcome> DefaultSendAsync(OutboxEntry entry, CancellationToken cancellationToken)
    {
        if (_messenger is null)
        {
            throw new InvalidOperationException("This pump has no messenger facade.");
        }

        OutboxPayload payload = entry.Payload;
        return payload.Kind switch
        {
            OutboxPayloadKind.Text or OutboxPayloadKind.GroupText =>
                payload.Kind == OutboxPayloadKind.GroupText
                    ? _messenger.SendGroupMessageAsync(entry.Destination, payload.Text ?? string.Empty, cancellationToken)
                    : _messenger.SendMessageAsync(entry.Destination, payload.Text ?? string.Empty, cancellationToken),

            _ => throw new SdkFormatException($"Unsupported outbox payload kind: {payload.Kind}."),
        };
    }

    private Func<OutboxEntry, CancellationToken, Task<SendOutcome>> ResolveSend()
        => _sendFactory ?? throw new InvalidOperationException("No send delegate configured.");

    /// <summary>Processes all due rows once. Cancellation propagates unwrapped.</summary>
    public async Task ProcessOnceAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<OutboxEntry> due = await _store.GetDueAsync(_options.MaxBatchSize, cancellationToken).ConfigureAwait(false);
            foreach (OutboxEntry entry in due)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ProcessEntryAsync(entry, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Alias: processes all due rows once.</summary>
    public Task ProcessDueAsync(CancellationToken cancellationToken = default) => ProcessOnceAsync(cancellationToken);

    /// <summary>Alias: processes all due rows once.</summary>
    public Task ProcessDueEntriesAsync(CancellationToken cancellationToken = default) => ProcessOnceAsync(cancellationToken);

    /// <summary>Alias: processes all due rows once.</summary>
    public Task PumpOnceAsync(CancellationToken cancellationToken = default) => ProcessOnceAsync(cancellationToken);

    /// <summary>Alias: processes all due rows once.</summary>
    public Task TickAsync(CancellationToken cancellationToken = default) => ProcessOnceAsync(cancellationToken);

    private async Task ProcessEntryAsync(OutboxEntry entry, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.GetUtcNow();

        // Permanent validation failures dead-letter immediately, before any send.
        if (IsPermanentFailure(entry, out string? permanentReason))
        {
            await _store.DeadLetterAsync(entry.Id, permanentReason, cancellationToken).ConfigureAwait(false);
            return;
        }

        SendOutcome? outcome = null;
        Exception? failure = null;
        try
        {
            outcome = await ResolveSend()(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (outcome is not null && outcome.Status == SendOutcomeStatus.Sent)
        {
            await _store.MarkSentAsync(entry.Id, cancellationToken).ConfigureAwait(false);
            return;
        }

        string? ruleName = outcome is not null
            ? outcome.Status.ToString()
            : failure is SafetyRuleViolationException safety
                ? safety.Rule.ToString()
                : failure?.GetType().Name;

        // Safety-rule violations (inactive hours, etc.) schedule against the next
        // window rather than dead-lettering.
        bool safetyViolation = failure is SafetyRuleViolationException;

        int nextAttempt = entry.Attempt + 1;
        if (nextAttempt >= _options.MaxAttempts && !safetyViolation)
        {
            await _store.DeadLetterAsync(entry.Id, ruleName ?? "dead-lettered", cancellationToken).ConfigureAwait(false);
            return;
        }

        TimeSpan delay = safetyViolation
            ? _options.InactiveHoursRetryDelay
            : Backoff(nextAttempt);

        if (delay < _options.PollFloor)
        {
            delay = _options.PollFloor;
        }

        // Safety violations never accumulate toward the dead-letter ceiling; keep the
        // attempt counter pinned so they keep scheduling.
        int recordedAttempt = safetyViolation ? entry.Attempt : nextAttempt;

        await _store.ScheduleRetryAsync(
            entry.Id,
            recordedAttempt,
            now + delay,
            ruleName,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsPermanentFailure(OutboxEntry entry, out string? reason)
    {
        reason = null;
        if (entry.Destination is null)
        {
            reason = "missing-destination";
            return true;
        }

        OutboxPayload payload = entry.Payload;
        if (payload.Kind is OutboxPayloadKind.Text or OutboxPayloadKind.GroupText or OutboxPayloadKind.Reply)
        {
            if (string.IsNullOrEmpty(payload.Text))
            {
                reason = "empty-text";
                return true;
            }
        }

        if (payload.Kind is OutboxPayloadKind.Reaction && string.IsNullOrEmpty(payload.TargetMessageId))
        {
            reason = "missing-target";
            return true;
        }

        if (payload.Kind is OutboxPayloadKind.Edit && (string.IsNullOrEmpty(payload.TargetMessageId) || string.IsNullOrEmpty(payload.Text)))
        {
            reason = "invalid-edit";
            return true;
        }

        if (payload.Kind is OutboxPayloadKind.Delete && string.IsNullOrEmpty(payload.TargetMessageId))
        {
            reason = "missing-target";
            return true;
        }

        if (payload.Kind == OutboxPayloadKind.None)
        {
            reason = "unsupported-payload";
            return true;
        }

        if (payload.Kind == OutboxPayloadKind.Media &&
            (payload.Media is null || string.IsNullOrEmpty(payload.Media.DirectPath) && string.IsNullOrEmpty(payload.Media.Url)))
        {
            reason = "missing-media-descriptor";
            return true;
        }

        if (!Enum.IsDefined(typeof(OutboxPayloadKind), payload.Kind))
        {
            reason = "unknown-payload-kind";
            return true;
        }

        return false;
    }

    private TimeSpan Backoff(int attempt)
    {
        double ms = _options.InitialBackoff.TotalMilliseconds;
        if (ms < 0)
        {
            ms = 0;
        }

        int safeAttempt = Math.Max(1, attempt);
        double exponent = Math.Min(safeAttempt - 1, 10);
        double computed = ms * Math.Pow(2, exponent);
        if (computed > _options.MaxBackoff.TotalMilliseconds && _options.MaxBackoff.TotalMilliseconds > 0)
        {
            computed = _options.MaxBackoff.TotalMilliseconds;
        }

        // Bounded jitter, +/- 20 percent, using the attempt as a deterministic seed input.
        int seed = HashCode.Combine(_store.GetType(), attempt);
        lock (_jitter)
        {
            // Keep Random usage deterministic-ish but not hot-contended; Random is not thread-safe.
            var local = new Random(seed);
            double factor = 0.8 + (local.NextDouble() * 0.4);
            computed *= factor;
        }

        if (computed < 0)
        {
            computed = 0;
        }

        return TimeSpan.FromMilliseconds(computed);
    }

    /// <summary>Runs the pump loop until <paramref name="cancellationToken"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessOnceAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Outbox pump pass failed.");
                }

                try
                {
                    TimeSpan delay = _options.PollInterval;
                    if (delay < _options.PollFloor)
                    {
                        delay = _options.PollFloor;
                    }

                    await Task.Delay(delay, _clock, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }
        }
        finally
        {
            // no-op
        }
    }

    /// <summary>Alias: runs the pump loop.</summary>
    public Task RunLoopAsync(CancellationToken cancellationToken = default) => RunAsync(cancellationToken);

    /// <summary>Alias: starts the pump loop as a background task owned by this pump.</summary>
    /// <remarks>
    /// CR-01/WR-06: the loop observes a token linked from BOTH the caller's token and the
    /// pump's owned stop token, so <see cref="StopAsync"/> and <see cref="DisposeAsync"/>
    /// can always stop a background loop even when the caller's token never fires.
    /// </remarks>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_runTask is { IsCompleted: false })
        {
            return _runTask;
        }

        _runCts?.Dispose();
        CancellationTokenSource runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _ownedCts.Token);
        _runCts = runCts;
        _runTask = Task.Run(() => RunAsync(runCts.Token), CancellationToken.None);
        return _runTask;
    }

    /// <summary>Signals a background loop started via <see cref="StartAsync"/> to stop.</summary>
    /// <remarks>
    /// Cancels the linked run token created by <see cref="StartAsync"/> (CR-01) so the loop
    /// stops even when the token passed to <see cref="StartAsync"/> never fires. The owned
    /// stop token is left intact so the pump can be started again afterwards.
    /// </remarks>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _runCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        return _runTask ?? Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _ownedCts.Cancel();
        }
        catch
        {
            // ignore
        }

        // CR-01: the run loop observes only the linked token StartAsync created.
        // Cancelling the owned CTS already propagates through it; cancel the linked
        // source directly too so a stop is never missed.
        try
        {
            _runCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        if (_runTask is not null)
        {
            try
            {
                await _runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
            catch
            {
                // ignore
            }
        }

        try
        {
            _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // ignore
        }

        _gate.Dispose();
        _ownedCts.Dispose();
        _runCts?.Dispose();
    }
}

// Extension to avoid editing error enum sources; SafetyRuleViolationException.RetryNeeded is
// represented by the rule value itself.
internal static class SafetyRuleExtensions
{
    internal static string? RuleName(this SafetyRuleViolationException ex) => ex.Rule.ToString();
}
