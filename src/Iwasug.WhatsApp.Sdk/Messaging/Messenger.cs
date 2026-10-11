using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Stores;

namespace Iwasug.WhatsApp.Sdk.Messaging;

/// <summary>
/// Public messaging facade (04-03 CONN-05 / MSG-01/03): <c>SendMessageAsync</c> with the
/// documented idempotency contract and the typed <see cref="MessageReceived"/> event.
/// One instance per account, exposed as <see cref="WaConnection.Messaging"/>.
/// </summary>
/// <remarks>
/// <para>
/// Created lazily under a lock on first <see cref="WaConnection.Messaging"/> access (the
/// <c>Pairing</c> precedent); the dispatcher subscribes to the connection's internal
/// stanza event exactly once at construction.
/// </para>
/// <para>
/// Handler contract for <see cref="MessageReceived"/>: return quickly (copy to your own
/// queue — Channels precedent), never call <see cref="WaConnection.DisposeAsync"/> from
/// the handler, handler exceptions are logged and swallowed.
/// </para>
/// <para>
/// The per-account send gate lives in <see cref="MessageSender"/>; every send settles
/// exactly one <see cref="SendOutcome"/> — the SDK never re-sends on its own.
/// </para>
/// </remarks>
public sealed class Messenger : IAsyncDisposable
{
    private readonly WaConnection _connection;
    private readonly DeviceDirectory _directory;
    private readonly MessageSender _sender;
    private readonly MessageDispatcher _dispatcher;
    private int _disposed;

    internal Messenger(AccountId accountId, WaConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _directory = new DeviceDirectory(
            RouterProvider,
            connection.Clock);
        _sender = new MessageSender(this, _directory);
        _dispatcher = new MessageDispatcher(this);
    }

    /// <summary>
    /// Raised (on the dispatcher's async path) once per successfully decrypted inbound
    /// message, after the durable session write and the ack have completed (D-06).
    /// </summary>
    public event EventHandler<MessageReceivedEventArgs>? MessageReceived;

    /// <summary>
    /// Sends one E2EE text message to the chat: one logical send = one network send. On
    /// timeout the SDK returns the <see cref="SendOutcomeStatus.TimedOut"/> outcome with
    /// the id and drops — the host may re-call, which is a NEW send with a NEW id; the
    /// SDK never re-sends a message on its own.
    /// </summary>
    /// <param name="chat">The destination chat JID.</param>
    /// <param name="text">The message text (non-empty; UTF-8 inside the protobuf payload).</param>
    /// <param name="cancellationToken">Caller cancellation (unwrapped; a cancelled send never touches the wire).</param>
    /// <returns>The terminal <see cref="SendOutcome"/> for the generated message id.</returns>
    /// <exception cref="SdkFormatException">The chat JID is malformed or the text is null/empty.</exception>
    /// <exception cref="SdkConnectionException">The connection is not open.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public async Task<SendOutcome> SendMessageAsync(Jid chat, string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        return await _sender.SendAsync(chat, text, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _dispatcher.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    internal void RaiseMessageReceived(MessageReceivedEventArgs args) => MessageReceived?.Invoke(this, args);

    internal ValueTask<StoreSession> ResolveStoresAsync(CancellationToken cancellationToken) =>
        _connection.ResolveStoreSessionAsync(cancellationToken);

    internal Func<QueryRouter?> RouterProvider => () => _connection.CurrentRouter;

    internal TimeProvider Clock => _connection.Clock;

    internal MessageSender Sender => _sender;

    internal WaConnection Connection => _connection;

    /// <summary>The pump seam: the connection invokes this once per inbound stanza.</summary>
    internal void OnStanzaReceived(object? sender, BinaryNode node) => _dispatcher.OnStanzaReceived(sender, node);
}
