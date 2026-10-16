using System.Collections.Concurrent;
using System.Threading.Channels;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Messaging;
using Iwasug.WhatsApp.Sdk.Safety;
using Iwasug.WhatsApp.Sdk.Stores;
using Microsoft.Extensions.Logging;

namespace Iwasug.WhatsApp.Sdk.Accounts;

/// <summary>Per-account composition root: owns the full account-scoped graph.</summary>
internal sealed class AccountRuntime : IAsyncDisposable
{
    private readonly TimeProvider _timeProvider;
    private int _disposed;
    private long _recentFailures;
    private long _lastConnectedTicks;
    private long _lastActivityTicks;
    private string? _lastErrorRuleName;

    public AccountRuntime(
        AccountId accountId,
        StoreSession storeSession,
        WaConnection connection,
        Messenger messenger,
        ISafetyGate safetyGate,
        Channel<OutboundEnvelope> outboundQueue,
        Func<StoreSession> resolveStores,
        ILogger? logger,
        CancellationTokenSource linkedCts,
        ConnectionOptions? connectionOptions = null,
        SafetyOptions? safetyOptions = null,
        TimeProvider? timeProvider = null,
        StoreRegistry? ownedStoreRegistry = null,
        OutboxPump? outboxPump = null)
    {
        AccountId = accountId;
        StoreSession = storeSession;
        Connection = connection;
        Messenger = messenger;
        SafetyGate = safetyGate;
        OutboundQueue = outboundQueue;
        _resolveStores = resolveStores;
        Logger = logger;
        LinkedCts = linkedCts;
        OwnedStoreRegistry = ownedStoreRegistry;
        OutboxPump = outboxPump;
        ConnectionOptions = connectionOptions ?? connection.Options;
        SafetyOptions = safetyOptions ?? (safetyGate as Safety.SafetyGate)?.Options;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // 07 WR-01: every status fact has a real writer — no fabricated seeds. Activity
        // and connection facts come from the connection's transitions; fault facts come
        // from the single real safety gate (07 CR-02) and from settled outbound sends.
        connection.StateChanged += OnConnectionStateChanged;
        messenger.SendSettled += OnSendSettled;
        if (safetyGate is Safety.SafetyGate gate)
        {
            gate.BudgetExhausted += OnBudgetExhausted;
            gate.ThrottleEngaged += OnThrottleEngaged;
            gate.AccountRestricted += OnAccountRestricted;
        }
    }

    private readonly Func<StoreSession> _resolveStores;

    public AccountId AccountId { get; }
    public StoreSession StoreSession { get; }
    public WaConnection Connection { get; }
    public Messenger Messenger { get; }
    public ISafetyGate SafetyGate { get; }

    /// <summary>
    /// Bounded outbound queue for this account. Scaffolding, NOT yet wired (07 IN-04):
    /// nothing under <c>src/</c> produces or drains entries yet — only the queue depth
    /// surfaces via <see cref="AccountStatus.QueuedOutbound"/>. Kept as the planned pump
    /// seam; remove or wire when the outbound pump lands.
    /// </summary>
    public Channel<OutboundEnvelope> OutboundQueue { get; }

    public ILogger? Logger { get; }

    /// <summary>
    /// Per-account cancellation source. Scaffolding, NOT yet wired (07 IN-04): it is
    /// never linked to a manager stop token and never handed to any consumer — it is
    /// only cancelled and disposed with this runtime. Kept as the planned per-account
    /// token seam.
    /// </summary>
    public CancellationTokenSource LinkedCts { get; }

    /// <summary>
    /// The per-account <see cref="StoreRegistry"/> this runtime OWNS and disposes with
    /// itself (07 WR-06); <c>null</c> when the registry is host-supplied (D-03) and
    /// therefore host-owned.
    /// </summary>
    public StoreRegistry? OwnedStoreRegistry { get; }

    /// <summary>Optional durable per-account outbox pump (EXT-01); null until wired by the factory.</summary>
    public OutboxPump? OutboxPump { get; }

    /// <summary>Compatibility alias for <see cref="OutboxPump"/>.</summary>
    public OutboxPump? Pump => OutboxPump;

    public Iwasug.WhatsApp.Sdk.Connection.ConnectionOptions ConnectionOptions { get; }
    public Iwasug.WhatsApp.Sdk.Safety.SafetyOptions? SafetyOptions { get; }

    /// <summary>Surfaced ban-safety faults since registration: failed sends, breaker opens, budget/throttle refusals.</summary>
    public int RecentFailures => (int)Math.Min(int.MaxValue, Interlocked.Read(ref _recentFailures));

    /// <summary>The last time the connection reported <see cref="ConnectionState.Open"/>; null until it has (never seeded).</summary>
    public DateTimeOffset? LastConnectedAt => ToTimestamp(Volatile.Read(ref _lastConnectedTicks));

    /// <summary>The last observed activity (state transition, settled send, or safety fault); null until one occurs.</summary>
    public DateTimeOffset? LastActivityAt => ToTimestamp(Volatile.Read(ref _lastActivityTicks));

    /// <summary>The last surfaced safety-rule name (rule-only — never payloads, never JIDs); null until one occurs.</summary>
    public string? LastErrorRuleName => Volatile.Read(ref _lastErrorRuleName);

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs args)
    {
        MarkActivity();
        if (args.State == ConnectionState.Open)
        {
            Volatile.Write(ref _lastConnectedTicks, _timeProvider.GetUtcNow().UtcTicks);
        }
    }

    private void OnSendSettled(SendOutcome outcome)
    {
        MarkActivity();
        if (outcome.Status is SendOutcomeStatus.ServerError or SendOutcomeStatus.TimedOut)
        {
            Interlocked.Increment(ref _recentFailures);
        }
    }

    private void OnBudgetExhausted(object? sender, BudgetExhaustedEventArgs args) => RecordSafetyFault(args.SafetyStage);

    private void OnThrottleEngaged(object? sender, ThrottleEngagedEventArgs args) => RecordSafetyFault(args.SafetyStage);

    private void OnAccountRestricted(object? sender, AccountRestrictedEventArgs args) => RecordSafetyFault(args.SafetyStage);

    private void RecordSafetyFault(int safetyStage)
    {
        Interlocked.Increment(ref _recentFailures);
        Volatile.Write(ref _lastErrorRuleName, ((SafetyRule)safetyStage).ToString());
        MarkActivity();
    }

    private void MarkActivity() => Volatile.Write(ref _lastActivityTicks, _timeProvider.GetUtcNow().UtcTicks);

    private static DateTimeOffset? ToTimestamp(long ticks) =>
        ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Detach status observers first so events during teardown mutate nothing.
        Connection.StateChanged -= OnConnectionStateChanged;
        Messenger.SendSettled -= OnSendSettled;
        if (SafetyGate is Safety.SafetyGate gate)
        {
            gate.BudgetExhausted -= OnBudgetExhausted;
            gate.ThrottleEngaged -= OnThrottleEngaged;
            gate.AccountRestricted -= OnAccountRestricted;
        }
        try { LinkedCts.Cancel(); } catch { }
        if (OutboxPump is not null)
        {
            try { await OutboxPump.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        try { await Connection.DisposeAsync().ConfigureAwait(false); } catch { }
        try { await StoreSession.DisposeAsync().ConfigureAwait(false); } catch { }
        if (OwnedStoreRegistry is not null)
        {
            // 07 WR-06: the factory created this registry for this account only, so the
            // runtime is its owner — disposing it runs the registry's own teardown
            // (session drain is a no-op here, fallback factory disposal is not).
            try { await OwnedStoreRegistry.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        LinkedCts.Dispose();
    }
}
