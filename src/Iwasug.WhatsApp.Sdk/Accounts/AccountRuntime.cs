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
    private int _disposed;

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
        SafetyOptions? safetyOptions = null)
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
        ConnectionOptions = connectionOptions ?? connection.Options;
        SafetyOptions = safetyOptions ?? (safetyGate as Safety.SafetyGate)?.Options;
    }

    private readonly Func<StoreSession> _resolveStores;

    public AccountId AccountId { get; }
    public StoreSession StoreSession { get; }
    public WaConnection Connection { get; }
    public Messenger Messenger { get; }
    public ISafetyGate SafetyGate { get; }
    public Channel<OutboundEnvelope> OutboundQueue { get; }
    public ILogger? Logger { get; }
    public CancellationTokenSource LinkedCts { get; }
    public Iwasug.WhatsApp.Sdk.Connection.ConnectionOptions ConnectionOptions { get; }
    public Iwasug.WhatsApp.Sdk.Safety.SafetyOptions? SafetyOptions { get; }
    public DateTimeOffset? LastConnectedAt { get; set; }
    public DateTimeOffset? LastActivityAt { get; set; }
    public string? LastErrorRuleName { get; set; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { LinkedCts.Cancel(); } catch { }
        try { await Connection.DisposeAsync().ConfigureAwait(false); } catch { }
        try { await StoreSession.DisposeAsync().ConfigureAwait(false); } catch { }
        LinkedCts.Dispose();
    }
}