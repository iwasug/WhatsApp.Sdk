using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Safety;
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
    /// <summary>
    /// The bounded event-stream capacity (04-05, MSG-07 — T-4-17): when
    /// <see cref="ReadEventsAsync"/> consumers fall behind, the channel accepts up to
    /// this many events before the SDK's receive path blocks on the next write. The
    /// pump is SLOWED, never dropped — the backpressure fact asserts the delivered
    /// count equals the scripted count with the channel observed AT this capacity.
    /// </summary>
    public const int EventChannelCapacity = 128;

    private readonly WaConnection _connection;
    private readonly DeviceDirectory _directory;
    private readonly GroupDirectory _groups;
    private readonly SenderKeyRepository _senderKeys;
    private readonly SafetyGate _safety;
    private readonly MessageSender _sender;
    private readonly PresenceManager _presence;
    private Media.MediaUploader _uploader;
    private Media.MediaDownloader _downloader;
    private readonly MessageDispatcher _dispatcher;
    private readonly ReceiptHandler _receipts;
    private readonly SemaphoreSlim _prekeyGate = new(1, 1);
    private readonly Channel<WhatsAppEvent> _events;
    private bool _prekeysEnsured;
    private int _disposed;

    internal Messenger(AccountId accountId, WaConnection connection, int eventChannelCapacity = EventChannelCapacity)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        // 07 WR-02: the bounded stream capacity is host-configurable (WhatsAppSdkOptions
        // .EventChannelCapacity); this constant is only the default.
        _events = Channel.CreateBounded<WhatsAppEvent>(
            new BoundedChannelOptions(eventChannelCapacity <= 0 ? EventChannelCapacity : eventChannelCapacity)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
        _directory = new DeviceDirectory(
            RouterProvider,
            connection.Clock);
        _groups = new GroupDirectory(RouterProvider, connection.Clock);
        _senderKeys = new SenderKeyRepository();
        _sender = new MessageSender(this, _directory, _groups, _senderKeys);
        _uploader = new Iwasug.WhatsApp.Sdk.Media.MediaUploader(
            new System.Net.Http.HttpClient(new System.Net.Http.SocketsHttpHandler()),
            new Iwasug.WhatsApp.Sdk.Media.MediaConn(RouterProvider, connection.Clock),
            connection.Clock,
            connection.SafetyPolicy);
        _downloader = new Iwasug.WhatsApp.Sdk.Media.MediaDownloader(
            new System.Net.Http.HttpClient(new System.Net.Http.SocketsHttpHandler()),
            connection.Clock);
        _dispatcher = new MessageDispatcher(this, _senderKeys, _downloader);
        _receipts = new ReceiptHandler(this);
        _presence = new PresenceManager(_connection);
        _safety = new SafetyGate(
            connection.Clock,
            SafetyOptions.Validate(connection.SafetyPolicy),
            logger: connection.SafetyLogger,
            ct => ResolveStoresAsync(ct),
            (evt, ct) => PublishAsync(evt, ct),
            _presence);
        _safety.BudgetExhausted += (_, args) => _ = RaiseBudgetExhaustedAsync(args, CancellationToken.None);
        _safety.ThrottleEngaged += (_, args) => _ = RaiseThrottleEngagedAsync(args, CancellationToken.None);
        _safety.AccountRestricted += (_, args) => _ = RaiseAccountRestrictedAsync(args, CancellationToken.None);
    }

    /// <summary>
    /// Raised (on the dispatcher's async path) once per successfully decrypted inbound
    /// message, after the durable session write and the ack have completed (D-06).
    /// </summary>
    public event EventHandler<MessageReceivedEventArgs>? MessageReceived;

    /// <summary>Raised for inbound peer presence updates (EXT-02).</summary>
    public event EventHandler<PresenceUpdatedEventArgs>? PresenceUpdated;

    /// <summary>Raised after an outbound presence state is applied (EXT-02).</summary>
    public event EventHandler<PresenceAppliedEventArgs>? PresenceChanged;

    /// <summary>Alias for <see cref="PresenceChanged"/> (applied state).</summary>
    public event EventHandler<PresenceAppliedEventArgs>? PresenceApplied;

    /// <summary>
    /// Raised (on the dispatcher's async path) once per successfully verified inbound
    /// media message, after the durable session write, the ack, and the full verify chain
    /// (hash-swap → MAC → plain hash) have completed. The host owns disposal of
    /// <see cref="MediaReceivedEventArgs.Content"/>.
    /// </summary>
    public event EventHandler<MediaReceivedEventArgs>? MediaReceived;

    /// <summary>
    /// Raised when an inbound media download cannot be recovered: the per-id request cap
    /// is exhausted or an rmr notification answered terminally (<c>&lt;error code="2"/&gt;</c>
    /// or a non-success result). The host's only signal for unrecoverable inbound media
    /// (MED-02); carries no key material. Handler-only by design — this fact is not
    /// published to the <see cref="ReadEventsAsync"/> stream.
    /// </summary>
    public event EventHandler<MediaDownloadFailedEventArgs>? MediaDownloadFailed;
    /// <summary>
    /// Raised (on the dispatcher's async path) when an inbound <c>receipt</c> node parses
    /// (04-04, MSG-04): the kind, ids, chat, and optional group participant, parsed
    /// bounds-checked. Informational only — receipts gate no SDK behavior and no security
    /// decision (T-4-14); a duplicate receipt raises the event again and the host decides
    /// what to trust.
    /// </summary>
    public event EventHandler<ReceiptReceivedEventArgs>? ReceiptReceived;

    /// <summary>Raised (on the dispatcher's async path) for an inbound reaction (EXT-03).</summary>
    public event EventHandler<ReactionReceivedEventArgs>? ReactionReceived;

    /// <summary>Raised (on the dispatcher's async path) for an inbound edited message (EXT-04).</summary>
    public event EventHandler<MessageEditedEventArgs>? MessageEdited;

    /// <summary>Raised (on the dispatcher's async path) for an inbound delete-for-everyone (EXT-04).</summary>
    public event EventHandler<MessageDeletedEventArgs>? MessageDeleted;

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

    /// <summary>Raised when an account safety budget is exhausted (SafetyEvent vocabulary, 05-01).</summary>
    public event EventHandler<BudgetExhaustedEventArgs>? BudgetExhausted;

    /// <summary>Raised when throttling is engaged (SafetyEvent vocabulary, 05-01).</summary>
    public event EventHandler<ThrottleEngagedEventArgs>? ThrottleEngaged;

    /// <summary>Raised when an account is restricted (SafetyEvent vocabulary, 05-01).</summary>
    public event EventHandler<AccountRestrictedEventArgs>? AccountRestricted;

    /// <summary>
    /// The typed event stream (04-05, MSG-07): one vocabulary, two consumption shapes.
    /// Every message, receipt, ack, and decryption-failure fact raised through the
    /// <c>EventHandler</c> events above is ALSO written to a bounded in-box
    /// <see cref="System.Threading.Channels.Channel{T}"/> of
    /// <see cref="EventChannelCapacity"/> events, and this method enumerates it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Backpressure contract (T-4-17): the SDK writes with
    /// <c>WriteAsync</c> — a consumer that falls behind SLOWS the receive pump once
    /// the channel is full; events are never silently dropped or coalesced. Consuming
    /// code must not block the drain indefinitely.
    /// </para>
    /// <para>
    /// Cancellation contract: <paramref name="cancellationToken"/> cancels enumeration
    /// promptly — the pending read aborts and <see cref="OperationCanceledException"/>
    /// propagates unwrapped (repo cancel-passes-through discipline); no event is
    /// observed after cancellation. The same token should be passed to the sends and
    /// queries the host awaits alongside the stream. Disposing the messenger completes
    /// the stream (enumeration ends, it does not throw).
    /// </para>
    /// <para>
    /// Connection-state and logout are NOT part of the stream: they remain
    /// <c>EventHandler</c> events on <see cref="Connection.WaConnection"/> (CONN-05
    /// compatibility). Handler exceptions never cross the SDK boundary — enumeration
    /// runs on the host's own task.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Cancels enumeration (unwrapped; no event is observed after cancellation).</param>
    /// <returns>The ordered stream of typed events for this account.</returns>
    public async IAsyncEnumerable<WhatsAppEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Async-iterator shape (not a passthrough) so [EnumeratorCancellation] combines
        // this token with the GetAsyncEnumerator token — either cancels the enumeration.
        await foreach (WhatsAppEvent evt in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

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
        return await _safety.ExecuteSendAsync(
            new SendContext(chat, IsReply: false),
            cancellationToken,
            ct => _sender.SendAsync(chat, text, ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one E2EE text message to a group via the sender-key path (04-05, MSG-02):
    /// members resolve through <see cref="GroupDirectory"/> (participants-for-fanout
    /// only — GRPM-01 stays deferred), an SKDM fans out pairwise per sender-key
    /// generation, and ONE signed <c>skmsg</c> ciphertext rides the same node with the
    /// deterministic <c>phash</c>. One logical send = one network send, exactly like
    /// <see cref="SendMessageAsync"/>; on timeout the SDK returns
    /// <see cref="SendOutcomeStatus.TimedOut"/> and drops — the host may re-call (a NEW
    /// send with a NEW id; a repeat with a live sender key issues NO duplicate SKDM).
    /// </summary>
    /// <param name="group">The group JID (e.g. <c>123-456@g.us</c>).</param>
    /// <param name="text">The message text (non-empty; UTF-8 inside the protobuf payload).</param>
    /// <param name="cancellationToken">Caller cancellation (unwrapped; a cancelled send never touches the wire).</param>
    /// <returns>The terminal <see cref="SendOutcome"/> for the generated message id.</returns>
    /// <exception cref="SdkFormatException">The group JID is malformed, the text is null/empty, the account is not a member (or the participant list resolves empty), or no member devices resolve.</exception>
    /// <exception cref="SdkConnectionException">The connection is not open.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public async Task<SendOutcome> SendGroupMessageAsync(Jid group, string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        await EnsureOwnPrekeysAsync(cancellationToken).ConfigureAwait(false);
        return await _safety.ExecuteSendAsync(
            new SendContext(group, IsReply: false),
            cancellationToken,
            ct => _sender.SendGroupAsync(group, text, ct)).ConfigureAwait(false);
    }

    /// <summary>Sends media. See plan 06-02.</summary>
    public async Task<SendOutcome> SendImageAsync(Iwasug.WhatsApp.Sdk.Domain.Jid chat, System.IO.Stream content, string mimetype, string? caption = null, int? width = null, int? height = null, byte[]? thumbnail = null, System.Threading.CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        await EnsureOwnPrekeysAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(mimetype) || !Iwasug.WhatsApp.Sdk.Media.MediaMimeAllowList.Images.Contains(mimetype, System.StringComparer.OrdinalIgnoreCase))
        {
            throw new SdkFormatException("Media mimetype is not on the allow list.");
        }
        return await _safety.ExecuteSendAsync(
            new SendContext(chat, IsReply: false),
            cancellationToken,
            async ct =>
            {
                var upload = await _uploader.UploadAsync(Iwasug.WhatsApp.Sdk.Media.MediaKind.Image, content, null, ct).ConfigureAwait(false);
                byte[] plaintext = MessageBuilder.BuildImageMessage(upload, caption, width, height, thumbnail, mimetype);
                return await _sender.SendMediaAsync(chat, plaintext, caption ?? string.Empty, Iwasug.WhatsApp.Sdk.Media.MediaKindInfo.MediaType(Iwasug.WhatsApp.Sdk.Media.MediaKind.Image), ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <summary>Sends media. See plan 06-02.</summary>
    public async Task<SendOutcome> SendDocumentAsync(Iwasug.WhatsApp.Sdk.Domain.Jid chat, System.IO.Stream content, string mimetype, string fileName, string? title = null, string? caption = null, byte[]? thumbnail = null, System.Threading.CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        await EnsureOwnPrekeysAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(mimetype) || !Iwasug.WhatsApp.Sdk.Media.MediaMimeAllowList.Documents.Contains(mimetype, System.StringComparer.OrdinalIgnoreCase))
        {
            throw new SdkFormatException("Media mimetype is not on the allow list.");
        }
        return await _safety.ExecuteSendAsync(
            new SendContext(chat, IsReply: false),
            cancellationToken,
            async ct =>
            {
                var upload = await _uploader.UploadAsync(Iwasug.WhatsApp.Sdk.Media.MediaKind.Document, content, null, ct).ConfigureAwait(false);
                byte[] plaintext = MessageBuilder.BuildDocumentMessage(upload, mimetype, title, fileName, caption, thumbnail);
                return await _sender.SendMediaAsync(chat, plaintext, fileName ?? caption ?? string.Empty, Iwasug.WhatsApp.Sdk.Media.MediaKindInfo.MediaType(Iwasug.WhatsApp.Sdk.Media.MediaKind.Document), ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <summary>Sends media. See plan 06-02.</summary>
    public async Task<SendOutcome> SendVoiceNoteAsync(Iwasug.WhatsApp.Sdk.Domain.Jid chat, System.IO.Stream content, int seconds, byte[]? waveform = null, System.Threading.CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        await EnsureOwnPrekeysAsync(cancellationToken).ConfigureAwait(false);
        return await _safety.ExecuteSendAsync(
            new SendContext(chat, IsReply: false),
            cancellationToken,
            async ct =>
            {
                var upload = await _uploader.UploadAsync(Iwasug.WhatsApp.Sdk.Media.MediaKind.VoiceNote, content, null, ct).ConfigureAwait(false);
                byte[] plaintext = MessageBuilder.BuildVoiceNoteMessage(upload, "audio/ogg; codecs=opus", seconds, waveform);
                return await _sender.SendMediaAsync(chat, plaintext, "audio", Iwasug.WhatsApp.Sdk.Media.MediaKindInfo.MediaType(Iwasug.WhatsApp.Sdk.Media.MediaKind.VoiceNote), ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <summary>Sends media. See plan 06-02.</summary>
    public async Task<SendOutcome> SendGroupImageAsync(Iwasug.WhatsApp.Sdk.Domain.Jid group, System.IO.Stream content, string mimetype, string? caption = null, int? width = null, int? height = null, byte[]? thumbnail = null, System.Threading.CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        await EnsureOwnPrekeysAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(mimetype) || !Iwasug.WhatsApp.Sdk.Media.MediaMimeAllowList.Images.Contains(mimetype, System.StringComparer.OrdinalIgnoreCase))
            throw new SdkFormatException("Media mimetype is not on the allow list.");
        return await _safety.ExecuteSendAsync(
            new SendContext(group, IsReply: false),
            cancellationToken,
            async ct =>
            {
                var upload = await _uploader.UploadAsync(Iwasug.WhatsApp.Sdk.Media.MediaKind.Image, content, null, ct).ConfigureAwait(false);
                byte[] plaintext = MessageBuilder.BuildImageMessage(upload, caption, width, height, thumbnail, mimetype);
                return await _sender.SendGroupMediaAsync(group, plaintext, caption ?? string.Empty, Iwasug.WhatsApp.Sdk.Media.MediaKindInfo.MediaType(Iwasug.WhatsApp.Sdk.Media.MediaKind.Image), ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <summary>Sends media. See plan 06-02.</summary>
    public async Task<SendOutcome> SendGroupDocumentAsync(Iwasug.WhatsApp.Sdk.Domain.Jid group, System.IO.Stream content, string mimetype, string fileName, string? title = null, string? caption = null, byte[]? thumbnail = null, System.Threading.CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        await EnsureOwnPrekeysAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(mimetype) || !Iwasug.WhatsApp.Sdk.Media.MediaMimeAllowList.Documents.Contains(mimetype, System.StringComparer.OrdinalIgnoreCase))
            throw new SdkFormatException("Media mimetype is not on the allow list.");
        return await _safety.ExecuteSendAsync(
            new SendContext(group, IsReply: false),
            cancellationToken,
            async ct =>
            {
                var upload = await _uploader.UploadAsync(Iwasug.WhatsApp.Sdk.Media.MediaKind.Document, content, null, ct).ConfigureAwait(false);
                byte[] plaintext = MessageBuilder.BuildDocumentMessage(upload, mimetype, title, fileName, caption, thumbnail);
                return await _sender.SendGroupMediaAsync(group, plaintext, fileName ?? caption ?? string.Empty, Iwasug.WhatsApp.Sdk.Media.MediaKindInfo.MediaType(Iwasug.WhatsApp.Sdk.Media.MediaKind.Document), ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <summary>Sends media. See plan 06-02.</summary>
    public async Task<SendOutcome> SendGroupVoiceNoteAsync(Iwasug.WhatsApp.Sdk.Domain.Jid group, System.IO.Stream content, int seconds, byte[]? waveform = null, System.Threading.CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        await EnsureOwnPrekeysAsync(cancellationToken).ConfigureAwait(false);
        return await _safety.ExecuteSendAsync(
            new SendContext(group, IsReply: false),
            cancellationToken,
            async ct =>
            {
                var upload = await _uploader.UploadAsync(Iwasug.WhatsApp.Sdk.Media.MediaKind.VoiceNote, content, null, ct).ConfigureAwait(false);
                byte[] plaintext = MessageBuilder.BuildVoiceNoteMessage(upload, "audio/ogg; codecs=opus", seconds, waveform);
                return await _sender.SendGroupMediaAsync(group, plaintext, "audio", Iwasug.WhatsApp.Sdk.Media.MediaKindInfo.MediaType(Iwasug.WhatsApp.Sdk.Media.MediaKind.VoiceNote), ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one E2EE reply that quotes a cached message (04-04, MSG-05): the quoted
    /// id, sender, chat, and former plaintext all come from the recent-message cache
    /// entry written when that message was sent or received — the cache is the ONLY
    /// quote source (Pitfall 6), never a caller-supplied text. The payload is a
    /// quote-carrying extended-text message; everything else about the send (per-account
    /// gate, one logical send = one network send, terminal <see cref="SendOutcome"/>)
    /// is identical to <see cref="SendMessageAsync"/>.
    /// </summary>
    /// <remarks>
    /// The quote's <c>remoteJid</c> is always the cached chat JID and its
    /// <c>participant</c> is the cached sender when present (the wire fixture shows a
    /// participant on 1:1 quotes too, so it is not group-gated). A cache miss, an
    /// entry without retained text, or an unparseable cached chat JID fails closed
    /// with <see cref="SdkFormatException"/> BEFORE any node is built or sent.
    /// </remarks>
    /// <param name="chat">The destination chat JID.</param>
    /// <param name="quotedMessageId">The id of the cached message to quote (e.g. <see cref="MessageReceivedEventArgs.QuotedMessageId"/> or a prior <see cref="SendOutcome.MessageId"/>).</param>
    /// <param name="text">The reply text (non-empty; UTF-8 inside the protobuf payload).</param>
    /// <param name="cancellationToken">Caller cancellation (unwrapped; a cancelled send never touches the wire).</param>
    /// <returns>The terminal <see cref="SendOutcome"/> for the generated message id.</returns>
    /// <exception cref="SdkFormatException">The quoted id is unknown to the cache, the reply text is null/empty, the cached chat JID is malformed, or the chat JID is malformed.</exception>
    /// <exception cref="SdkConnectionException">The connection is not open.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public async Task<SendOutcome> ReplyAsync(Jid chat, string quotedMessageId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (string.IsNullOrWhiteSpace(quotedMessageId))
        {
            throw new SdkFormatException("The quoted message id must not be null, empty, or whitespace.");
        }

        if (string.IsNullOrEmpty(text))
        {
            throw new SdkFormatException("Message text must not be null or empty.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        await EnsureOwnPrekeysAsync(cancellationToken).ConfigureAwait(false);

        StoreSession stores = await ResolveStoresAsync(cancellationToken).ConfigureAwait(false);
        RecentMessageEntry? entry = await stores.RecentMessages.GetAsync(quotedMessageId, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            throw new SdkFormatException("The quoted message id is not in the recent-message cache: the reply was not sent.");
        }

        if (!Jid.TryParse(entry.ChatJid, out Jid? quotedChat) || quotedChat is null)
        {
            throw new SdkFormatException("The cached chat for the quoted message is malformed: the reply was not sent.");
        }

        var quoted = new QuotedMessageRef(
            entry.MessageKey,
            string.IsNullOrEmpty(entry.Sender) ? null : entry.Sender,
            entry.Text ?? string.Empty,
            entry.ChatJid);
        byte[] payload = MessageBuilder.BuildQuotedText(text, quoted);
        return await _safety.ExecuteSendAsync(
            new SendContext(chat, IsReply: true),
            cancellationToken,
            ct => _sender.SendPayloadAsync(chat, text, payload, ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a reaction to <paramref name="targetMessageId"/> in <paramref name="chat"/>
    /// (EXT-03): an ordinary gated E2EE send whose plaintext is a
    /// <c>Message { ReactionMessage { key, text } }</c>. A null or empty
    /// <paramref name="emoji"/> removes the reaction and is still a real gated send.
    /// </summary>
    public async Task<SendOutcome> SendReactionAsync(Jid chat, string targetMessageId, string? emoji, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (string.IsNullOrWhiteSpace(targetMessageId))
        {
            throw new SdkFormatException("The target message id must not be null, empty, or whitespace.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        await EnsureOwnPrekeysAsync(cancellationToken).ConfigureAwait(false);

        string safeEmoji = emoji ?? string.Empty;
        byte[] payload = MessageBuilder.BuildReaction(targetMessageId, safeEmoji, chat.ToString(), null);
        string displayText = string.IsNullOrEmpty(safeEmoji) ? "reaction" : safeEmoji;
        return await _safety.ExecuteSendAsync(
            new SendContext(chat, IsReply: false),
            cancellationToken,
            ct => _sender.SendPayloadAsync(chat, displayText, payload, ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// Edits a message originally sent by this account (EXT-04): a protocol message
    /// (type MESSAGE_EDIT) riding the same gated E2EE send path. Ownership and
    /// target presence are validated against the recent-message cache BEFORE node
    /// assembly; mismatched ownership throws <see cref="SdkFormatException"/>.
    /// </summary>
    public async Task<SendOutcome> EditOwnMessageAsync(Jid chat, string originalMessageId, string newText, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (string.IsNullOrWhiteSpace(originalMessageId))
        {
            throw new SdkFormatException("The original message id must not be null, empty, or whitespace.");
        }
        if (string.IsNullOrEmpty(newText))
        {
            throw new SdkFormatException("Message text must not be null or empty.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        await EnsureOwnPrekeysAsync(cancellationToken).ConfigureAwait(false);

        StoreSession stores = await ResolveStoresAsync(cancellationToken).ConfigureAwait(false);
        RecentMessageEntry? entry = await stores.RecentMessages.GetAsync(originalMessageId, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            throw new SdkFormatException("The target message id is not in the recent-message cache: the edit was not sent.");
        }
        if (!string.Equals(entry.ChatJid, chat.ToString(), StringComparison.Ordinal))
        {
            throw new SdkFormatException("The target message does not belong to this chat: the edit was not sent.");
        }
        if (!string.IsNullOrEmpty(entry.Sender))
        {
            CredentialRecord? cred = await stores.Credentials.GetAsync(cancellationToken).ConfigureAwait(false);
            string? paired = cred?.PairedUser;
            if (!string.Equals(entry.Sender, paired, StringComparison.Ordinal))
            {
                throw new SdkFormatException("Cannot edit a message sent by another account: the edit was not sent.");
            }
        }

        byte[] payload = MessageBuilder.BuildProtocolEdit(originalMessageId, newText, chat.ToString(), null);
        return await _safety.ExecuteSendAsync(
            new SendContext(chat, IsReply: false),
            cancellationToken,
            ct => _sender.SendPayloadAsync(chat, newText, payload, ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a message for everyone (EXT-04): a protocol message (type REVOKE)
    /// riding the same gated E2EE send path. Ownership and target presence are
    /// validated against the recent-message cache BEFORE node assembly.
    /// </summary>
    public async Task<SendOutcome> DeleteForEveryoneAsync(Jid chat, string targetMessageId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (string.IsNullOrWhiteSpace(targetMessageId))
        {
            throw new SdkFormatException("The target message id must not be null, empty, or whitespace.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        await EnsureOwnPrekeysAsync(cancellationToken).ConfigureAwait(false);

        StoreSession stores = await ResolveStoresAsync(cancellationToken).ConfigureAwait(false);
        RecentMessageEntry? entry = await stores.RecentMessages.GetAsync(targetMessageId, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            throw new SdkFormatException("The target message id is not in the recent-message cache: the delete was not sent.");
        }
        if (!string.Equals(entry.ChatJid, chat.ToString(), StringComparison.Ordinal))
        {
            throw new SdkFormatException("The target message does not belong to this chat: the delete was not sent.");
        }
        if (!string.IsNullOrEmpty(entry.Sender))
        {
            CredentialRecord? cred = await stores.Credentials.GetAsync(cancellationToken).ConfigureAwait(false);
            string? paired = cred?.PairedUser;
            if (!string.Equals(entry.Sender, paired, StringComparison.Ordinal))
            {
                throw new SdkFormatException("Cannot delete a message sent by another account: the delete was not sent.");
            }
        }

        byte[] payload = MessageBuilder.BuildProtocolRevoke(targetMessageId, chat.ToString(), null);
        return await _safety.ExecuteSendAsync(
            new SendContext(chat, IsReply: false),
            cancellationToken,
            ct => _sender.SendPayloadAsync(chat, "delete", payload, ct)).ConfigureAwait(false);
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

    /// <summary>
    /// The single per-account safety gate (07 CR-02): every send through
    /// <see cref="SendMessageAsync"/> traverses this gate, so status/health surfaces
    /// must read its real state. There is exactly one gate per account.
    /// </summary>
    internal ISafetyGate SafetyGate => _safety;

    /// <summary>
    /// Internal observer of every settled outbound send (07 WR-01): the accounts layer
    /// drives its status activity/failure counters from it. Handlers must return quickly;
    /// two identical events are never raised for one send.
    /// </summary>
    internal event Action<SendOutcome>? SendSettled;

    /// <summary>Tests/host seam: attempts to publish a synthetic event to this account's stream (backpressure, no drop).</summary>
    internal bool TryPublishEvent(WhatsAppEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return _events.Writer.TryWrite(evt);
    }

    /// <summary>
    /// Sends one text payload to multiple recipients as independent, safety-gated 1:1
    /// sends (BCT-01). Returns a per-recipient outcome report in host order; cancellation
    /// stops the loop without synthesizing outcomes for remaining recipients.
    /// </summary>
    public async Task<IReadOnlyList<BroadcastRecipientOutcome>> SendBroadcastAsync(
        IEnumerable<Jid> recipients,
        Func<Jid, string> messageFactory,
        BroadcastOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(messageFactory);

        // IN-03: resolve and validate the inter-recipient delay at the boundary. A
        // negative delay is an options error — reject it here instead of letting it
        // surface as an ArgumentOutOfRange from Task.Delay mid-fan-out.
        TimeSpan delay = options?.InterRecipientDelay ?? TimeSpan.Zero;
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options!.InterRecipientDelay,
                "Inter-recipient delay must be zero or positive.");
        }

        // Enumerate recipients exactly once and de-duplicate by ordinal JID string
        // comparison without invoking messageFactory.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<Jid>();
        foreach (Jid recipient in recipients)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (recipient is null)
            {
                throw new ArgumentNullException(nameof(recipients));
            }
            if (seen.Add(recipient.ToString()))
            {
                ordered.Add(recipient);
            }
        }

        var result = new List<BroadcastRecipientOutcome>(ordered.Count);
        if (ordered.Count == 0)
        {
            return result;
        }

        await EnsureOwnPrekeysAsync(cancellationToken).ConfigureAwait(false);

        bool first = true;
        foreach (Jid recipient in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!first && delay > TimeSpan.Zero)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(delay, _connection.Clock, cancellationToken).ConfigureAwait(false);
            }

            SendOutcome outcome;
            try
            {
                // WR-04: messageFactory and the gated send are isolated per recipient —
                // one recipient's safety violation, connection fault, or factory
                // exception is recorded as that recipient's Failed outcome and the
                // fan-out continues, instead of the first throw aborting the loop and
                // discarding every outcome already computed.
                string text = messageFactory(recipient);
                outcome = await _safety.ExecuteSendAsync(
                    new SendContext(recipient, IsReply: false),
                    cancellationToken,
                    ct => _sender.SendAsync(recipient, text, ct)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw; // cancellation remains the only documented early-exit
            }
            catch
            {
                // Recorded on the outcome row, never propagated (WR-04).
                outcome = new SendOutcome(string.Empty, SendOutcomeStatus.Failed);
            }

            // Host cancellation is re-checked before recording so the loop stops
            // cleanly without synthesizing outcomes for remaining recipients.
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new BroadcastRecipientOutcome(recipient, outcome));
            first = false;
        }

        return result;
    }

    /// <summary>
    /// Sends one presence state (EXT-02). Account-level states are targeted at the
    /// default destination; chat states carry the chat JID. The wire node is one-way,
    /// so no ack is awaited; explicit host calls fail closed through the safety gate.
    /// </summary>
    public async Task SendPresenceAsync(PresenceState state, Jid? chat = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        Jid destination = chat ?? Jid.Parse("presence@s.whatsapp.net");

        await _safety.ExecuteSendAsync(
            new SendContext(destination, IsReply: false),
            cancellationToken,
            async ct =>
            {
                await _presence.SendAsync(state, chat, ct).ConfigureAwait(false);
                return new SendOutcome(Guid.NewGuid().ToString("N").ToUpperInvariant(), SendOutcomeStatus.Sent);
            }).ConfigureAwait(false);

        var applied = new PresenceAppliedEventArgs(state, chat, _connection.Clock.GetUtcNow());
        PresenceChanged?.Invoke(this, applied);
        PresenceApplied?.Invoke(this, applied);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _dispatcher.Dispose();
        _events.Writer.TryComplete(); // stream ends for consumers; DisposeAsync semantics unchanged
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// The single emit point for one received-message fact (04-05, MSG-07): raises the
    /// <see cref="MessageReceived"/> EventHandler, then writes the same facts to the
    /// event stream — one vocabulary, two consumption shapes. The stream write applies
    /// backpressure (T-4-17): a full channel slows the caller's pump, never drops.
    /// </summary>
    internal async ValueTask RaiseMessageReceivedAsync(MessageReceivedEventArgs args, CancellationToken cancellationToken)
    {
        // WR-05: handler exceptions are logged-and-swallowed by the dispatcher contract
        // (same guarantee the Phase-8 raise methods give); isolating the invoke keeps a
        // throwing host handler from aborting the stream write below or skipping the
        // dispatcher work that depends on this raise completing.
        try
        {
            MessageReceived?.Invoke(this, args);
        }
        catch
        {
            // Best-effort by design (dispatcher contract).
        }

        await PublishAsync(
            new MessageReceivedEvent(args.Key, args.Chat, args.Sender, args.IsFromMe, args.Timestamp, args.Text, args.QuotedMessageId),
            cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask RaisePresenceUpdatedAsync(PresenceUpdatedEventArgs args, CancellationToken cancellationToken)
    {
        try
        {
            PresenceUpdated?.Invoke(this, args);
        }
        catch
        {
            // Best-effort: handler exceptions are swallowed (dispatcher contract).
        }

        return ValueTask.CompletedTask;
    }

    internal ValueTask RaiseReactionReceivedAsync(ReactionReceivedEventArgs args, CancellationToken cancellationToken)
    {
        try { ReactionReceived?.Invoke(this, args); } catch { }
        return ValueTask.CompletedTask;
    }

    internal ValueTask RaiseMessageEditedAsync(MessageEditedEventArgs args, CancellationToken cancellationToken)
    {
        try { MessageEdited?.Invoke(this, args); } catch { }
        return ValueTask.CompletedTask;
    }

    internal ValueTask RaiseMessageDeletedAsync(MessageDeletedEventArgs args, CancellationToken cancellationToken)
    {
        try { MessageDeleted?.Invoke(this, args); } catch { }
        return ValueTask.CompletedTask;
    }

    internal async ValueTask RaiseMediaReceivedAsync(MediaReceivedEventArgs args, CancellationToken cancellationToken)
    {
        MediaReceived?.Invoke(this, args);
        System.IO.Stream content = args.Content;
        if (args.Content is System.IO.MemoryStream owned && owned.TryGetBuffer(out ArraySegment<byte> buf)
            && buf.Array is not null)
        {
            content = new System.IO.MemoryStream(buf.Array, buf.Offset, buf.Count, writable: false);
        }
        await PublishAsync(
            new MediaReceivedEvent(
                args.Key, args.Chat, args.Sender, args.IsFromMe, args.Timestamp, args.Kind, args.Mimetype,
                args.FileLength, args.Caption, args.FileName, args.Seconds, args.Ptt, content),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The single emit point for the media-download-failure fact (MED-02/WR-03):
    /// raises the <see cref="MediaDownloadFailed"/> handler twin. Deliberately
    /// handler-only — no stream twin is published, documented on the event itself.
    /// </summary>
    internal ValueTask RaiseMediaDownloadFailedAsync(MediaDownloadFailedEventArgs args, CancellationToken cancellationToken = default)
    {
        MediaDownloadFailed?.Invoke(this, args);
        return ValueTask.CompletedTask;
    }

    internal void RaiseDeviceSetChanged(DeviceSetChangedEventArgs args) => DeviceSetChanged?.Invoke(this, args);

    /// <summary>The single emit point for one receipt fact: EventHandler twin, then the stream (backpressure, no drop).</summary>
    internal async ValueTask RaiseReceiptReceivedAsync(ReceiptReceivedEventArgs args, CancellationToken cancellationToken)
    {
        ReceiptReceived?.Invoke(this, args);
        await PublishAsync(
            new ReceiptReceivedEvent(args.Kind, args.MessageIds, args.Chat, args.Participant, args.Timestamp),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The single emit point for one decryption-failure fact: EventHandler twin, then the stream (backpressure, no drop).</summary>
    internal async ValueTask RaiseDecryptionFailedAsync(DecryptionFailedEventArgs args, CancellationToken cancellationToken)
    {
        DecryptionFailed?.Invoke(this, args);
        await PublishAsync(
            new DecryptionFailedEvent(args.Chat, args.Sender, args.MessageId, args.RetryCount, args.Timestamp),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The single emit point for one terminal send-ack fact (04-05, MSG-07): the same
    /// <see cref="SendOutcome"/> the awaited send call settles with also reaches the
    /// stream as <see cref="MessageAckedEvent"/>. No pending send, no event (a late or
    /// duplicate ack is a no-op on both surfaces).
    /// </summary>
    /// <summary>The single emit point for BudgetExhausted: EventHandler twin, then stream (backpressure, no drop).</summary>
    internal async ValueTask RaiseBudgetExhaustedAsync(BudgetExhaustedEventArgs args, CancellationToken cancellationToken)
    {
        BudgetExhausted?.Invoke(this, args);
        await PublishAsync(
            new BudgetExhaustedEvent(args.AccountKey, args.Trigger, args.NextAllowedUtc, args.SafetyStage, _connection.Clock.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The single emit point for ThrottleEngaged: EventHandler twin, then stream (backpressure, no drop).</summary>
    internal async ValueTask RaiseThrottleEngagedAsync(ThrottleEngagedEventArgs args, CancellationToken cancellationToken)
    {
        ThrottleEngaged?.Invoke(this, args);
        await PublishAsync(
            new ThrottleEngagedEvent(args.AccountKey, args.Trigger, args.NextAllowedUtc, args.SafetyStage, _connection.Clock.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The single emit point for AccountRestricted: EventHandler twin, then stream (backpressure, no drop).</summary>
    internal async ValueTask RaiseAccountRestrictedAsync(AccountRestrictedEventArgs args, CancellationToken cancellationToken)
    {
        AccountRestricted?.Invoke(this, args);
        await PublishAsync(
            new AccountRestrictedEvent(args.AccountKey, args.Trigger, args.NextAllowedUtc, args.SafetyStage, _connection.Clock.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask RaiseMessageAckedAsync(SendOutcome outcome, Jid chat, CancellationToken cancellationToken)
    {
        SendSettled?.Invoke(outcome);
        return PublishAsync(
            new MessageAckedEvent(
                outcome.MessageId, chat, outcome.Status, outcome.Error, outcome.T, outcome.ServerId,
                _connection.Clock.GetUtcNow()),
            cancellationToken);
    }

    /// <summary>
    /// Writes one fact to the bounded event channel. <see cref="System.Threading.Channels.ChannelClosedException"/>
    /// is swallowed: after <see cref="DisposeAsync"/> the stream is complete and a
    /// post-dispose fact has no consumer (not a silent drop — the host disposed the stream).
    /// </summary>
    private async ValueTask PublishAsync(WhatsAppEvent evt, CancellationToken cancellationToken)
    {
        try
        {
            await _events.Writer.WriteAsync(evt, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // Stream completed by DisposeAsync: nothing can observe this fact anymore.
        }
    }

    internal ReceiptHandler Receipts => _receipts;

    /// <summary>
    /// Test-visible buffered-event count (04-05 MSG-07 backpressure fact): proves a
    /// non-draining consumer fills the bounded channel exactly to
    /// <see cref="EventChannelCapacity"/> while the pump is slowed, not dropping.
    /// </summary>
    internal int BufferedEventCount => _events.Reader.Count;

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
            // one-time batch starts past the checkpoint's contiguous run, then BUMPS
            // past every occupied slot (WR-05) — a retry-path spend leaves a hole the
            // contiguous count cannot see, and overwriting a live stored id would
            // destroy a key already served to a peer.
            int oneTimeStart = await FindFreeOneTimeStartAsync(
                stores.PreKeys,
                (bundle?.OneTimePreKeyId ?? 0) + remaining + 1,
                PreKeyManager.UploadBatchSize,
                cancellationToken).ConfigureAwait(false);
            var manager = new PreKeyManager(
                RouterProvider() ?? throw new SdkConnectionException("The connection is not open: own prekeys were not uploaded."),
                new DurableWriteRetryPolicy());
            OwnPreKeyMaterial material = manager.GenerateOwnMaterial(
                // Signed by the IDENTITY private, not the device-signing key: the
                // documented XEdDSA approximation is "identity and signing key are
                // the same Ed25519 pair". Verification still imports the wire
                // identity (X25519 bytes) as Ed25519, so first-contact verify only
                // truly works once XEdDSA lands (CR-01, tracked for 04-05 UAT).
                row.IdentityKeyPrivate,
                (bundle?.SignedPreKeyId ?? 0) + 1,
                oneTimeStart,
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

    /// <summary>
    /// Scan headroom for the free-window probe (WR-05): bounds how far past the
    /// requested floor the batch start may bump when live stored ids block the
    /// window. Realistic layouts never approach it; exceeding it surfaces as the
    /// send path's typed id-range failure, never as a silent overwrite.
    /// </summary>
    private const int MaxOneTimeScanHeadroom = 1024;

    /// <summary>
    /// Finds the first one-time-prekey id window <c>[start, start + count - 1]</c> at
    /// or above <paramref name="floorId"/> whose every slot is free in the store
    /// (WR-05): the readiness recompute probes only ids contiguous from the bundle
    /// checkpoint, so a retry-path spend can leave a hole that makes the contiguous
    /// count miss still-live stored keys — deriving the batch start from that count
    /// alone would overwrite ids already served to peers, destroying keys their
    /// holders' prekey messages still need. Bumping past every occupied slot
    /// guarantees a fresh batch never overwrites a live key.
    /// </summary>
    private static async Task<int> FindFreeOneTimeStartAsync(
        IPreKeyStore preKeys,
        int floorId,
        int count,
        CancellationToken cancellationToken)
    {
        long start = floorId < 0 ? 0 : floorId;
        long scanLimit = start + count + MaxOneTimeScanHeadroom;
        while (start + count - 1 <= PreKeyManager.MaxPreKeyId && start < scanLimit)
        {
            long blockedAt = -1;
            for (long id = start; id < start + count; id++)
            {
                PreKeyRecordEntry? entry = await preKeys.GetPreKeyAsync((int)id, cancellationToken).ConfigureAwait(false);
                if (entry is not null)
                {
                    blockedAt = id;
                    break;
                }
            }

            if (blockedAt < 0)
            {
                return (int)start; // the whole window is free
            }

            start = blockedAt + 1; // bump past the occupied slot, re-check the window
        }

        return (int)start; // no free window within the headroom: GenerateOwnMaterial validates the range loudly
    }

    internal ValueTask<StoreSession> ResolveStoresAsync(CancellationToken cancellationToken) =>
        _connection.ResolveStoreSessionAsync(cancellationToken);

    internal Func<QueryRouter?> RouterProvider => () => _connection.CurrentRouter;

    internal TimeProvider Clock => _connection.Clock;

    internal MessageSender Sender => _sender;

    internal WaConnection Connection => _connection;

    /// <summary>Receive-side media downloader seam (tests may substitute a stub-backed instance).</summary>
    internal Media.MediaDownloader MediaDownloader
    {
        get => _downloader;
        set
        {
            _downloader = value ?? throw new ArgumentNullException(nameof(value));
            _dispatcher.MediaDownloader = _downloader;
        }
    }

    /// <summary>Compatibility alias for the downloader seam.</summary>
    internal Media.MediaDownloader Downloader { get => MediaDownloader; set => MediaDownloader = value; }

    /// <summary>Upload-side media uploader seam (tests may substitute a stub-backed instance).</summary>
    internal Media.MediaUploader MediaUploader
    {
        get => _uploader;
        set => _uploader = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Compatibility alias for the uploader seam.</summary>
    internal Media.MediaUploader Uploader { get => MediaUploader; set => MediaUploader = value; }

    /// <summary>The pump seam: the connection invokes this once per inbound stanza.</summary>
    internal void OnStanzaReceived(object? sender, BinaryNode node) => _dispatcher.OnStanzaReceived(sender, node);
}
