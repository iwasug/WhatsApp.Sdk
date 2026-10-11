using System.Globalization;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Signal;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.Runtime;

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
    private readonly ReceiptHandler _receipts;
    private readonly SemaphoreSlim _prekeyGate = new(1, 1);
    private bool _prekeysEnsured;
    private int _disposed;

    internal Messenger(AccountId accountId, WaConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _directory = new DeviceDirectory(
            RouterProvider,
            connection.Clock);
        _sender = new MessageSender(this, _directory);
        _dispatcher = new MessageDispatcher(this);
        _receipts = new ReceiptHandler(this);
    }

    /// <summary>
    /// Raised (on the dispatcher's async path) once per successfully decrypted inbound
    /// message, after the durable session write and the ack have completed (D-06).
    /// </summary>
    public event EventHandler<MessageReceivedEventArgs>? MessageReceived;

    /// <summary>
    /// Raised (on the dispatcher's async path) when an inbound <c>receipt</c> node parses
    /// (04-04, MSG-04): the kind, ids, chat, and optional group participant, parsed
    /// bounds-checked. Informational only — receipts gate no SDK behavior and no security
    /// decision (T-4-14); a duplicate receipt raises the event again and the host decides
    /// what to trust.
    /// </summary>
    public event EventHandler<ReceiptReceivedEventArgs>? ReceiptReceived;

    /// <summary>
    /// Raised (on the dispatcher's async path) when an inbound message fails to decrypt
    /// (04-04, MSG-06): on every failure, with the 1-based per-(sender, message id)
    /// retry count. While the count stays within the Pattern-5 cap the SDK also sends
    /// the wire retry receipt; past the cap this event is the host's only signal.
    /// Informational and rule-only — no plaintext, ciphertext, or key bytes surface.
    /// </summary>
    public event EventHandler<DecryptionFailedEventArgs>? DecryptionFailed;

    /// <summary>
    /// Raised when a server ack carries a <c>phash</c> that no longer matches the device
    /// set this SDK fanned the message out to (Pitfall 3): the peer's device set moved.
    /// The device-list cache for the chat is invalidated before the event; the next send
    /// re-queries USync. The affected send itself still settles its normal outcome.
    /// </summary>
    public event EventHandler<DeviceSetChangedEventArgs>? DeviceSetChanged;

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
        await EnsureOwnPrekeysAsync(cancellationToken).ConfigureAwait(false);
        return await _sender.SendAsync(chat, text, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opts in (or out) to sending delivery receipts for <paramref name="chat"/> (04-04
    /// MSG-04, Open Question 4): when enabled, every successfully decrypted inbound
    /// message in that chat triggers one wire-exact <c>&lt;receipt type="delivery"&gt;</c>
    /// node after its ack and event. The gate is process-local host policy (not durable
    /// state), off by default, with no presence coupling.
    /// </summary>
    /// <param name="chat">The chat to opt in (or out) for.</param>
    /// <param name="enabled">Whether delivery receipts should be sent for this chat.</param>
    /// <exception cref="ArgumentNullException">The chat is null.</exception>
    public void SetDeliveryReceipts(Jid chat, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(chat);
        _receipts.SetDeliveryReceipts(chat, enabled);
    }

    /// <summary>Whether delivery receipts are enabled for <paramref name="chat"/> (see <see cref="SetDeliveryReceipts"/>).</summary>
    public bool IsDeliveryReceiptsEnabled(Jid chat) => _receipts.IsDeliveryReceiptsEnabled(chat);

    /// <summary>
    /// Marks <paramref name="keys"/> read (04-04 MSG-04, Pattern 4 verbatim): sends one
    /// <c>&lt;receipt type="read"&gt;</c> per sender group (the same-sender-only rule —
    /// keys are grouped by chat and participant automatically), with the first id in the
    /// <c>id</c> attribute, the rest as <c>&lt;list&gt;&lt;item/&gt;</c> children, the
    /// <c>participant</c> attribute only for group senders, and <c>t</c> always present.
    /// </summary>
    /// <remarks>
    /// Idempotent at the wire level (MSG-04 idempotency edge): calling twice with the
    /// same keys sends two identical receipts — receipts are stateless protocol nodes
    /// with no client-side dedup machine.
    /// </remarks>
    /// <param name="keys">The message keys to mark read; one call may span multiple senders.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <exception cref="SdkFormatException">The key list is empty or a key is malformed.</exception>
    /// <exception cref="SdkConnectionException">The connection is not open.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public async Task MarkReadAsync(IReadOnlyList<MessageKeyRecord> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (keys.Count == 0)
        {
            throw new SdkFormatException("MarkRead needs at least one message key.");
        }

        await _receipts.SendReadReceiptsAsync(keys, cancellationToken).ConfigureAwait(false);
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

    internal void RaiseDeviceSetChanged(DeviceSetChangedEventArgs args) => DeviceSetChanged?.Invoke(this, args);

    internal void RaiseReceiptReceived(ReceiptReceivedEventArgs args) => ReceiptReceived?.Invoke(this, args);

    internal void RaiseDecryptionFailed(DecryptionFailedEventArgs args) => DecryptionFailed?.Invoke(this, args);

    internal ReceiptHandler Receipts => _receipts;

    /// <summary>
    /// Prekey-readiness trigger (04-03 Task 3, 04-RESEARCH Pattern 3): before the first
    /// send of this connection, ensure own one-time prekeys are uploaded when the store's
    /// remaining batch is below the Pattern-3 re-upload floor
    /// (<see cref="PreKeyManager.ReuploadThreshold"/> — whatsmeow <c>prekeys.go</c>:
    /// re-upload when the server-side one-time batch drops below 5). The remaining count
    /// is derived from the own-bundle checkpoint (<c>GetBundleAsync</c> at the account's
    /// own <c>user:device</c> address) by probing consecutive one-time ids in the store.
    /// </summary>
    /// <remarks>
    /// Single flight per connection: one successful check (healthy or uploaded) latches
    /// for the connection's lifetime, and the gate keeps concurrent sends to one flight.
    /// A failure surfaces as a typed send-path error — never a silent skip — and leaves
    /// the check unlatched so the next send retries. An unpaired account defers to the
    /// send path's own loud pairing rule.
    /// </remarks>
    private async Task EnsureOwnPrekeysAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _prekeysEnsured))
        {
            return;
        }

        await _prekeyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _prekeysEnsured))
            {
                return;
            }

            StoreSession stores = await ResolveStoresAsync(cancellationToken).ConfigureAwait(false);
            CredentialRecord? row = await stores.Credentials.GetAsync(cancellationToken).ConfigureAwait(false);
            if (row?.PairedUser is null)
            {
                return; // unpaired: the send path fails loudly on its own pairing rule
            }

            string ownAddress = string.Create(
                CultureInfo.InvariantCulture, $"{row.PairedUser}:{row.Device}@{DeviceDirectory.UsyncServer}");
            PreKeyBundle? bundle = await stores.PreKeys.GetBundleAsync(ownAddress, cancellationToken).ConfigureAwait(false);
            int remaining = await CountRemainingOneTimeKeysAsync(stores.PreKeys, bundle, cancellationToken).ConfigureAwait(false);
            if (bundle is not null && remaining >= PreKeyManager.ReuploadThreshold)
            {
                Volatile.Write(ref _prekeysEnsured, true);
                return; // healthy batch: zero upload IQs
            }

            // New ids never reuse a served id: signed-prekey ids advance by one; the
            // one-time batch starts one past the highest probed id (probe counts only
            // ids contiguous from the checkpoint, so this is collision-free).
            var manager = new PreKeyManager(
                RouterProvider() ?? throw new SdkConnectionException("The connection is not open: own prekeys were not uploaded."),
                new DurableWriteRetryPolicy());
            OwnPreKeyMaterial material = manager.GenerateOwnMaterial(
                row.SigningKeyPrivate,
                (bundle?.SignedPreKeyId ?? 0) + 1,
                (bundle?.OneTimePreKeyId ?? 0) + remaining + 1,
                PreKeyManager.UploadBatchSize);
            await manager.UploadOwnKeysAsync(
                (uint)row.RegistrationId,
                X3dh.DeriveX25519Public(row.IdentityKeyPrivate),
                material,
                ownAddress,
                stores.PreKeys,
                stores.Identity,
                cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _prekeysEnsured, true);
        }
        finally
        {
            _prekeyGate.Release();
        }
    }

    /// <summary>
    /// Counts the one-time keys remaining in the store by probing consecutive ids from
    /// the checkpoint's first-unserved id; the probe stops at the floor — callers only
    /// need to know "below the floor or not".
    /// </summary>
    private static async Task<int> CountRemainingOneTimeKeysAsync(IPreKeyStore preKeys, PreKeyBundle? bundle, CancellationToken cancellationToken)
    {
        if (bundle is null || bundle.OneTimePreKeyId <= 0)
        {
            return 0;
        }

        int count = 0;
        while (count < PreKeyManager.ReuploadThreshold)
        {
            PreKeyRecordEntry? entry = await preKeys.GetPreKeyAsync(bundle.OneTimePreKeyId + count, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                break;
            }

            count++;
        }

        return count;
    }

    internal ValueTask<StoreSession> ResolveStoresAsync(CancellationToken cancellationToken) =>
        _connection.ResolveStoreSessionAsync(cancellationToken);

    internal Func<QueryRouter?> RouterProvider => () => _connection.CurrentRouter;

    internal TimeProvider Clock => _connection.Clock;

    internal MessageSender Sender => _sender;

    internal WaConnection Connection => _connection;

    /// <summary>The pump seam: the connection invokes this once per inbound stanza.</summary>
    internal void OnStanzaReceived(object? sender, BinaryNode node) => _dispatcher.OnStanzaReceived(sender, node);
}
