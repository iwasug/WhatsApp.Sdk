using System.Globalization;
using Google.Protobuf;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Media;
using Iwasug.WhatsApp.Sdk.Signal;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.Runtime;

namespace Iwasug.WhatsApp.Sdk.Messaging;

/// <summary>
/// Inbound stanza dispatch over <see cref="WaConnection.StanzaReceived"/> (04-03):
/// <c>message</c> nodes run the decrypt → persist → ack → event → cache ordering
/// contract; <c>ack</c> nodes correlate pending sends; <c>receipt</c> nodes parse
/// bounds-checked into typed <see cref="ReceiptReceivedEventArgs"/> events (04-04,
/// MSG-04), the opt-in delivery receipt rides out after the message's ack + event, and
/// <c>retry</c> receipts drive the MSG-06 exchange in both directions: a failed decrypt
/// emits the Pattern-5 retry receipt (capped), and a peer retry for our message answers
/// with the same-id cache resend (T-4-12 requester check).
/// </summary>
/// <remarks>
/// <para>
/// The ordering contract is absolute (D-06, Pitfall 4): persist the ratchet state through
/// <see cref="DoubleRatchet.SignalSessionRepository"/> → send the
/// <c>&lt;ack class="message" id to&gt;</c> node (awaited) → raise
/// <see cref="MessageReceivedEventArgs"/> → write the recent-cache entry (D-20
/// log-and-continue). Ack only after durable writes complete.
/// </para>
/// <para>
/// Decryption failure is a typed outcome, not an event through the pump: no ack claims
/// success, no message event fires, the pump keeps running, and the Pattern-5 retry
/// receipt goes out best-effort (a retry-receipt fault never breaks the receive path;
/// the typed <see cref="DecryptionFailedEventArgs"/> event is raised before the wire
/// work, so the host learns of the failure either way). A cache-write failure never
/// breaks the path (D-20): the event is already raised and the ack already sent, so the
/// failure is swallowed after the fact.
/// </para>
/// <para>
/// Handler shape (PairingCoordinator precedent): <see cref="OnStanzaReceived"/> returns
/// quickly, kicking async work fire-and-forget with its own try/catch — a handler
/// exception can never kill the pump. No log call on this class ever sees plaintext,
/// ciphertext, or key bytes.
/// </para>
/// </remarks>
internal sealed class MessageDispatcher
{
    /// <summary>
    /// Recent-cache retention horizon (D-08 policy, SDK-owned): entries older than this
    /// are purged through the store's primitive on the write path. 30 days covers every
    /// realistic retry window (Pattern 5 caps retry receipts at 5).
    /// </summary>
    internal static readonly TimeSpan RetentionHorizon = TimeSpan.FromDays(30);

    private readonly Messenger _owner;
    private readonly SenderKeyRepository _senderKeys;
    private Iwasug.WhatsApp.Sdk.Media.MediaDownloader? _mediaDownloader;
    private int _disposed;

    internal MessageDispatcher(Messenger owner, SenderKeyRepository senderKeys, Iwasug.WhatsApp.Sdk.Media.MediaDownloader? mediaDownloader = null)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _senderKeys = senderKeys ?? throw new ArgumentNullException(nameof(senderKeys));
        _mediaDownloader = mediaDownloader;
    }

    /// <summary>Receive-side media downloader seam.</summary>
    internal Iwasug.WhatsApp.Sdk.Media.MediaDownloader MediaDownloader
    {
        get => _mediaDownloader ?? _owner.MediaDownloader;
        set => _mediaDownloader = value;
    }

    /// <summary>Compatibility alias for the downloader seam.</summary>
    internal Iwasug.WhatsApp.Sdk.Media.MediaDownloader Downloader { get => MediaDownloader; set => MediaDownloader = value; }

    /// <summary>
    /// The stanza-pump handler (one subscription per connection, made when the
    /// <see cref="Messenger"/> is created): return quickly, never throw.
    /// </summary>
    internal void OnStanzaReceived(object? sender, BinaryNode node)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await HandleAsync(node, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Swallowed by design: a dispatcher fault must never kill the pump.
            }
        });
    }

    private async Task HandleAsync(BinaryNode node, CancellationToken cancellationToken)
    {
        switch (node.Tag)
        {
            case "message":
                await HandleMessageAsync(node, cancellationToken).ConfigureAwait(false);
                break;
            case "ack":
                await HandleAckAsync(node, cancellationToken).ConfigureAwait(false);
                break;
            case "receipt":
                await HandleReceipt(node, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleMessageAsync(BinaryNode node, CancellationToken cancellationToken)
    {
        StoreSession stores = await _owner.ResolveStoresAsync(cancellationToken).ConfigureAwait(false);
        CredentialRecord? row = await stores.Credentials.GetAsync(cancellationToken).ConfigureAwait(false);
        if (row?.PairedUser is null)
        {
            return; // unpaired account: nothing to decrypt against
        }

        if (!node.Attrs.TryGetValue("id", out string? messageId) || string.IsNullOrEmpty(messageId))        {
            throw new SdkFormatException("Inbound message nodes must carry a non-empty id attribute.");
        }

        if (!node.Attrs.TryGetValue("from", out string? fromAttr)
            || !Jid.TryParse(fromAttr, out Jid? from)
            || from is null)
        {
            throw new SdkFormatException("Inbound message nodes must carry a parseable from attribute.");
        }

        BinaryNode? enc = FirstChild(node, "enc");
        if (enc is null)
        {
            return; // no encrypted content for this device in this stanza
        }

        string encType = enc.Attrs.TryGetValue("type", out string? type) ? type : MessageBuilder.EncTypeMessage;
        bool isPreKey = string.Equals(encType, MessageBuilder.EncTypePreKey, StringComparison.Ordinal);
        byte[] wire = enc.Data ?? throw new SdkFormatException("Inbound enc children must carry byte content.");

        Jid chat = Jid.Parse($"{DeviceDirectory.BaseUser(from)}@{from.Server}");
        DateTimeOffset timestamp = ParseTimestamp(node) ?? _owner.Clock.GetUtcNow();

        if (string.Equals(encType, MessageBuilder.EncTypeSenderKey, StringComparison.Ordinal))
        {
            // Group path (04-05, MSG-02): an skmsg decrypts through the stored sender
            // key keyed by (group, participant) — the 1:1 session path never sees it.
            await HandleGroupMessageAsync(node, from, chat, messageId, wire, timestamp, row, cancellationToken).ConfigureAwait(false);
            return;
        }

        bool isFromMe = string.Equals(DeviceDirectory.BaseUser(from), row.PairedUser, StringComparison.Ordinal);

        // CONCURRENCY CONTRACT (04-04 MSG-06): the full load → decrypt → persist
        // operation holds the per-address session gate, and a retry receipt for the
        // same sender rides the same gate — two ratchet mutations for one address
        // never interleave. The repository's locked variants are used under the held
        // gate (the gate is not reentrant).
        string address = DoubleRatchet.SignalSessionRepository.AddressFor(from);
        var repository = new DoubleRatchet.SignalSessionRepository(stores.SignalSessions, new DurableWriteRetryPolicy());
        using (IDisposable sessionGate = await SignalSessionGate.AcquireAsync(address, cancellationToken).ConfigureAwait(false))
        {
            DoubleRatchet? session = await repository.LoadUnlockedAsync(address, cancellationToken).ConfigureAwait(false);
            int? spentOneTimePreKeyId = null;
            if (session is null && isPreKey)
            {
                // First-contact inbound pkmsg (plan 04-03): the peer fetched OUR
                // prekey bundle, ran X3DH, and sent a prekey message — build the Bob
                // session here and fall through to the normal decrypt flow. Any
                // failure degrades exactly like "no session": retry receipt, no ack.
                (DoubleRatchet Bob, int? OneTimeId)? initialized =
                    await TryInitializeBobSessionAsync(row, stores, address, wire, cancellationToken).ConfigureAwait(false);
                if (initialized is not null)
                {
                    session = initialized.Value.Bob;
                    spentOneTimePreKeyId = initialized.Value.OneTimeId;
                }
            }

            if (session is null)
            {
                await TryEmitRetryReceiptAsync(row, from, chat, messageId, timestamp, hasSession: false, cancellationToken).ConfigureAwait(false);
                return; // no session for this sender device: Pattern-5 retry receipt (keys block), no ack, no event
            }

            SignalDecryptOutcome outcome = session.Decrypt(wire, isPreKey, cancellationToken);
            if (outcome is not SignalDecryptOutcome.Decrypted decrypted)
            {
                // A just-initialized Bob session is discarded unpersisted, so nothing
                // durable changed: the sender's retry rebuilds it and decrypts cleanly.
                await TryEmitRetryReceiptAsync(row, from, chat, messageId, timestamp, hasSession: true, cancellationToken).ConfigureAwait(false);
                return; // decryption failure: no ack, no event, Pattern-5 retry receipt (capped), pump continues
            }

            Message message;
            try
            {
                message = Message.Parser.ParseFrom(decrypted.Plaintext);
            }
            catch (InvalidProtocolBufferException ex)
            {
                throw new SdkFormatException("Inbound message plaintext failed protobuf validation.", ex);
            }

            // ORDERING CONTRACT: persist → ack → event → cache (D-06/Pitfall 4).
            if (spentOneTimePreKeyId is int spentOneTimeId)
            {
                // The one-time prekey that fed this first-contact session is spent
                // before the wire claims the message (D-06): single-use by contract.
                await stores.PreKeys.RemovePreKeyAsync(spentOneTimeId, cancellationToken).ConfigureAwait(false);
            }

            await repository.SaveUnlockedAsync(address, session, cancellationToken).ConfigureAwait(false);

            if (message.SenderKeyDistributionMessage is not null)
            {
                // SKDM branch (04-05, MSG-02): the sender key is written BEFORE the ack
                // (D-06), keyed by (group, sender identity) — the SKDM only ever arrives
                // inside a successfully decrypted 1:1 message from its sender (T-4-15).
                // "Written" is process-local durability: SenderKeyRepository is in-memory
                // (ISenderKeyStore seam deferred to v2 — WR-02).
                // No message event and no cache write: a distribution is not a message.
                StoreSenderKeyDistribution(message.SenderKeyDistributionMessage, from);
                await SendAckAsync(messageId, from, cancellationToken).ConfigureAwait(false);
                return;
            }

            // MEDIA BRANCH (06-04): image / document / audio. ValidateReceive runs
            // fail-closed BEFORE any download; ack precedes download (D-67).
            if (message.ImageMessage is not null)
            {
                await HandleInboundMediaAsync(
                    chat, from, isFromMe, key: new MessageKeyRecord(chat, isFromMe, messageId, string.Empty),
                    timestamp, Media.MediaKind.Image, image: message.ImageMessage, cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            }
            if (message.DocumentMessage is not null)
            {
                var doc = message.DocumentMessage;
                await HandleInboundMediaAsync(
                    chat, from, isFromMe, key: new MessageKeyRecord(chat, isFromMe, messageId, string.Empty),
                    timestamp, Media.MediaKind.Document, document: doc, cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            }
            if (message.AudioMessage is not null)
            {
                var aud = message.AudioMessage;
                await HandleInboundMediaAsync(
                    chat, from, isFromMe, key: new MessageKeyRecord(chat, isFromMe, messageId, string.Empty),
                    timestamp, Media.MediaKind.VoiceNote, audio: aud, cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            }

            await SendAckAsync(messageId, from, cancellationToken).ConfigureAwait(false);

            string? text = message.Conversation.Length == 0 ? null : message.Conversation;
            var key = new MessageKeyRecord(chat, isFromMe, messageId, string.Empty);
            await _owner.RaiseMessageReceivedAsync(
                new MessageReceivedEventArgs(key, chat, from, isFromMe, timestamp, text, messageId),
                cancellationToken).ConfigureAwait(false);

            // Opt-in delivery receipt (MSG-04): after the ack + event, best-effort — a
            // receipt failure never breaks the receive path (the message is already acked).
            if (_owner.IsDeliveryReceiptsEnabled(chat))
            {
                try
                {
                    await _owner.Receipts.SendDeliveryReceiptAsync(
                        chat, messageId, _owner.Clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Log-and-continue: the receipt is best-effort by contract.
                }
            }

            try
            {
                await stores.RecentMessages.PutAsync(
                    new RecentMessageEntry(
                        messageId,
                        chat.ToString(),
                        timestamp,
                        text,
                        from.ToString(),
                        decrypted.Plaintext),
                    cancellationToken).ConfigureAwait(false);
                await stores.RecentMessages.PurgeExpiredAsync(timestamp - RetentionHorizon, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // D-20 log-and-continue: the store may throw; the event and ack are already done.
            }
        }
    }

    /// <summary>
    /// First-contact inbound <c>pkmsg</c> handling (plan 04-03, "pkmsg → X3DH
    /// session init"): parses the prekey envelope fail-closed, pins the sender
    /// identity TOFU-style (a changed pinned identity is refused), loads our
    /// signed-prekey private from the own-address bundle checkpoint matching the
    /// id the envelope names, and requires the named one-time prekey to be ours.
    /// Returns the initialized (unpersisted) Bob session with the one-time prekey
    /// id the caller must spend before the wire (D-06), or null on any rule
    /// violation — the message then degrades to the Pattern-5 retry path exactly
    /// like "no session". The session is not persisted here: the normal
    /// decrypt → persist → ack flow owns the D-06 ordering, so an undecryptable
    /// envelope changes nothing durable and the sender's retry can rebuild.
    /// </summary>
    private async Task<(DoubleRatchet Session, int? OneTimePreKeyId)?> TryInitializeBobSessionAsync(
        CredentialRecord row,
        StoreSession stores,
        string address,
        byte[] wire,
        CancellationToken cancellationToken)
    {
        if (SignalCipher.DecryptPreKeyEnvelope(wire) is not PreKeyEnvelopeOutcome.Parsed parsed)
        {
            return null; // unparseable prekey envelope: typed, rule-only
        }

        PreKeyWireContent content = parsed.Content;
        if (content.RegistrationId > X3dh.MaxRegistrationId)
        {
            return null; // out-of-range remote registration id (untrusted wire input)
        }

        // TOFU: a pinned sender identity must match; an absent pin lands here
        // before any use of the identity (EnsureSessionAsync precedent).
        SignalIdentityRecord? pinned = await stores.Identity.GetAsync(address, cancellationToken).ConfigureAwait(false);
        if (pinned?.IdentityKeyDjb is not null
            && !pinned.IdentityKeyDjb.AsSpan().SequenceEqual(content.IdentityKeyDjb))
        {
            return null; // sender identity changed under an existing pin: refuse the session
        }

        string ownAddress = string.Create(
            CultureInfo.InvariantCulture, $"{row.PairedUser}:{row.Device}@{DeviceDirectory.UsyncServer}");
        PreKeyBundle? ownBundle = await stores.PreKeys.GetBundleAsync(ownAddress, cancellationToken).ConfigureAwait(false);
        if (ownBundle?.SignedPreKeyPrivate is not { Length: X3dh.KeyLength }
            || ownBundle.SignedPreKeyId != (int)content.SignedPreKeyId)
        {
            return null; // no own signed-prekey for the id this envelope names
        }

        byte[]? oneTimePrivate = null;
        if (content.PreKeyId is uint oneTimeId)
        {
            PreKeyRecordEntry? entry = await stores.PreKeys.GetPreKeyAsync((int)oneTimeId, cancellationToken).ConfigureAwait(false);
            if (entry?.PrivateKey is not { Length: X3dh.KeyLength })
            {
                return null; // the named one-time prekey is not ours to decrypt with
            }

            oneTimePrivate = entry.PrivateKey;
        }

        if (pinned is null)
        {
            byte[] pin = (byte[])content.IdentityKeyDjb.Clone();
            await stores.Identity.PutAsync(
                address, new SignalIdentityRecord(null, pin, true), cancellationToken).ConfigureAwait(false);
        }

        BobSession bobSession = X3dh.InitializeBob(
            row.IdentityKeyPrivate,
            ownBundle.SignedPreKeyPrivate,
            oneTimePrivate,
            content.PreKeyId is not null,
            content.IdentityKeyDjb,
            content.BaseKey);
        byte[] ownIdentityDjb = SignalCipher.ToDjbIdentity(X3dh.DeriveX25519Public(row.IdentityKeyPrivate));
        DoubleRatchet session = DoubleRatchet.InitializeBob(
            bobSession,
            ownBundle.SignedPreKeyPrivate,
            ownIdentityDjb,
            content.IdentityKeyDjb,
            content.BaseKey,
            (uint)row.RegistrationId,
            content.RegistrationId);
        return (session, content.PreKeyId is uint spent ? (int)spent : null);
    }

    /// <summary>
    /// Inbound group <c>skmsg</c> dispatch (04-05, MSG-02): decrypts through the
    /// sender key stored for (group, participant); on success the 1:1 ordering
    /// contract resumes unchanged — persist the advanced sender key (D-06) → ack
    /// (with <c>participant</c>) → raise <see cref="MessageReceivedEventArgs"/>
    /// carrying the group chat and the participant → recent cache. A missing key
    /// or a failed signature is a typed outcome: the capped Pattern-5 retry
    /// receipt goes out, NO ack claims success, and NO message event fires (the
    /// signature verified before any event — T-4-15).
    /// </summary>
    private async Task HandleGroupMessageAsync(
        BinaryNode node,
        Jid from,
        Jid chat,
        string messageId,
        byte[] wire,
        DateTimeOffset timestamp,
        CredentialRecord row,
        CancellationToken cancellationToken)
    {
        if (!node.Attrs.TryGetValue("participant", out string? participantAttr)
            || string.IsNullOrEmpty(participantAttr)
            || !Jid.TryParse(participantAttr, out Jid? participant)
            || participant is null)
        {
            throw new SdkFormatException("Inbound group message nodes must carry a parseable participant attribute.");
        }

        string senderKeyAddress = SenderKeyRepository.InboundAddress(chat, participant);
        SenderKey? senderKey = _senderKeys.TryLoad(senderKeyAddress);
        if (senderKey is null)
        {
            await TryEmitRetryReceiptAsync(row, participant, chat, messageId, timestamp, hasSession: false, cancellationToken).ConfigureAwait(false);
            return; // no sender key yet (SKDM pending): retry receipt, no ack, no event
        }

        if (senderKey.Decrypt(wire) is not SenderKeyDecryptOutcome.Decrypted decrypted)
        {
            await TryEmitRetryReceiptAsync(row, participant, chat, messageId, timestamp, hasSession: true, cancellationToken).ConfigureAwait(false);
            return; // tampered or undecryptable skmsg: typed failure, no ack, no event
        }

        Message message;
        try
        {
            message = Message.Parser.ParseFrom(decrypted.Plaintext);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new SdkFormatException("Inbound group message plaintext failed protobuf validation.", ex);
        }

        // ORDERING CONTRACT: persist → ack → event → cache (D-06/Pitfall 4) — the
        // sender-key chain advanced during the decrypt and must be written first
        // (process-local durability: SenderKeyRepository is in-memory, WR-02).
        _senderKeys.Save(senderKeyAddress, senderKey);

        if (message.ImageMessage is not null
            || message.DocumentMessage is not null
            || message.AudioMessage is not null)
        {
            var mgKey = new MessageKeyRecord(chat, FromMe: false, messageId, participant.ToString());
            if (message.ImageMessage is not null)
            {
                await HandleInboundGroupMediaAsync(chat, participant, mgKey, timestamp, MediaKind.Image, image: message.ImageMessage, cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            }
            if (message.DocumentMessage is not null)
            {
                await HandleInboundGroupMediaAsync(chat, participant, mgKey, timestamp, MediaKind.Document, document: message.DocumentMessage, cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            }
            if (message.AudioMessage is not null)
            {
                await HandleInboundGroupMediaAsync(chat, participant, mgKey, timestamp, MediaKind.VoiceNote, audio: message.AudioMessage, cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        await SendAckAsync(messageId, from, cancellationToken, participant).ConfigureAwait(false);

        string? text = message.Conversation.Length == 0 ? null : message.Conversation;
        var key = new MessageKeyRecord(chat, FromMe: false, messageId, participant.ToString());
        await _owner.RaiseMessageReceivedAsync(
            new MessageReceivedEventArgs(key, chat, participant, IsFromMe: false, timestamp, text, messageId),
            cancellationToken).ConfigureAwait(false);

        // Opt-in delivery receipt (MSG-04): after the ack + event, best-effort.
        if (_owner.IsDeliveryReceiptsEnabled(chat))
        {
            try
            {
                await _owner.Receipts.SendDeliveryReceiptAsync(
                    chat, messageId, _owner.Clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Log-and-continue: the receipt is best-effort by contract.
            }
        }

        try
        {
            StoreSession stores = await _owner.ResolveStoresAsync(cancellationToken).ConfigureAwait(false);
            await stores.RecentMessages.PutAsync(
                new RecentMessageEntry(
                    messageId,
                    chat.ToString(),
                    timestamp,
                    text,
                    participant.ToString(),
                    decrypted.Plaintext),
                cancellationToken).ConfigureAwait(false);
            await stores.RecentMessages.PurgeExpiredAsync(timestamp - RetentionHorizon, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // D-20 log-and-continue: the store may throw; the event and ack are already done.
        }
    }

    private async Task HandleInboundMediaAsync(
        Jid chat,
        Jid from,
        bool isFromMe,
        MessageKeyRecord key,
        DateTimeOffset timestamp,
        MediaKind kind,
        ImageMessage? image = null,
        DocumentMessage? document = null,
        AudioMessage? audio = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            byte[] mediaKey;
            byte[] fileSha;
            byte[] fileEnc;
            string directPath;
            long fileLength;
            string mimetype;
            string? caption = null;
            string? fileName = null;
            int? seconds = null;
            bool ptt = false;
            int? width = null;
            int? height = null;

            if (kind == MediaKind.Image && image is not null)
            {
                mediaKey = image.MediaKey.ToByteArray();
                fileSha = image.FileSha256.ToByteArray();
                fileEnc = image.FileEncSha256.ToByteArray();
                directPath = image.DirectPath;
                fileLength = image.FileLength;
                mimetype = image.Mimetype;
                caption = image.Caption;
                width = image.Width;
                height = image.Height;
            }
            else if (kind == MediaKind.Document && document is not null)
            {
                mediaKey = document.MediaKey.ToByteArray();
                fileSha = document.FileSha256.ToByteArray();
                fileEnc = document.FileEncSha256.ToByteArray();
                directPath = document.DirectPath;
                fileLength = document.FileLength;
                mimetype = document.Mimetype;
                caption = document.Caption;
                fileName = document.FileName;
            }
            else if (kind == MediaKind.VoiceNote && audio is not null)
            {
                mediaKey = audio.MediaKey.ToByteArray();
                fileSha = audio.FileSha256.ToByteArray();
                fileEnc = audio.FileEncSha256.ToByteArray();
                directPath = audio.DirectPath;
                fileLength = audio.FileLength;
                mimetype = string.IsNullOrEmpty(audio.Mimetype) ? "audio/ogg; codecs=opus" : audio.Mimetype;
                seconds = audio.Seconds;
                ptt = audio.Ptt;
            }
            else
            {
                return;
            }

            try
            {
                MediaMetadata.ValidateReceive(kind, mediaKey, fileSha, fileEnc, directPath, fileLength, mimetype, fileName, seconds, width, height);
            }
            catch
            {
                return; // fail closed before ack/download
            }

            await SendAckAsync(key.Id, from, cancellationToken).ConfigureAwait(false);

            string? safeFileName = fileName;
            if (kind == MediaKind.Document && !string.IsNullOrEmpty(fileName))
            {
                try { safeFileName = MediaMetadata.SanitizeFileName(fileName); }
                catch { return; }
            }

            System.IO.Stream? content = null;
            try
            {
                content = await MediaDownloader.DownloadAsync(kind, directPath, mediaKey, fileSha, fileEnc, fileLength, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return; // download fault: no event
            }

            var args = new MediaReceivedEventArgs(key, chat, from, isFromMe, timestamp, kind, mimetype, fileLength, caption, safeFileName, seconds, ptt, content);
            await _owner.RaiseMediaReceivedAsync(args, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Pump survival: never throw.
        }
    }

    private async Task HandleInboundGroupMediaAsync(
        Jid chat,
        Jid participant,
        MessageKeyRecord key,
        DateTimeOffset timestamp,
        MediaKind kind,
        ImageMessage? image = null,
        DocumentMessage? document = null,
        AudioMessage? audio = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            byte[] mediaKey;
            byte[] fileSha;
            byte[] fileEnc;
            string directPath;
            long fileLength;
            string mimetype;
            string? caption = null;
            string? fileName = null;
            int? seconds = null;
            bool ptt = false;

            if (kind == MediaKind.Image && image is not null)
            {
                mediaKey = image.MediaKey.ToByteArray();
                fileSha = image.FileSha256.ToByteArray();
                fileEnc = image.FileEncSha256.ToByteArray();
                directPath = image.DirectPath;
                fileLength = image.FileLength;
                mimetype = image.Mimetype;
                caption = image.Caption;
            }
            else if (kind == MediaKind.Document && document is not null)
            {
                mediaKey = document.MediaKey.ToByteArray();
                fileSha = document.FileSha256.ToByteArray();
                fileEnc = document.FileEncSha256.ToByteArray();
                directPath = document.DirectPath;
                fileLength = document.FileLength;
                mimetype = document.Mimetype;
                caption = document.Caption;
                fileName = document.FileName;
            }
            else if (kind == MediaKind.VoiceNote && audio is not null)
            {
                mediaKey = audio.MediaKey.ToByteArray();
                fileSha = audio.FileSha256.ToByteArray();
                fileEnc = audio.FileEncSha256.ToByteArray();
                directPath = audio.DirectPath;
                fileLength = audio.FileLength;
                mimetype = string.IsNullOrEmpty(audio.Mimetype) ? "audio/ogg; codecs=opus" : audio.Mimetype;
                seconds = audio.Seconds;
                ptt = audio.Ptt;
            }
            else
            {
                return;
            }

            try
            {
                MediaMetadata.ValidateReceive(kind, mediaKey, fileSha, fileEnc, directPath, fileLength, mimetype, fileName, seconds);
            }
            catch
            {
                return;
            }

            string? safeFileName = fileName;
            if (kind == MediaKind.Document && !string.IsNullOrEmpty(fileName))
            {
                try { safeFileName = MediaMetadata.SanitizeFileName(fileName); }
                catch { return; }
            }

            await SendAckAsync(key.Id, chat, cancellationToken, participant).ConfigureAwait(false);

            System.IO.Stream? content = null;
            try
            {
                content = await MediaDownloader.DownloadAsync(kind, directPath, mediaKey, fileSha, fileEnc, fileLength, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            var args = new MediaReceivedEventArgs(key, chat, participant, IsFromMe: false, timestamp, kind, mimetype, fileLength, caption, safeFileName, seconds, ptt, content);
            await _owner.RaiseMediaReceivedAsync(args, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Pump survival.
        }
    }

    /// <summary>
    /// Stores one SKDM from a decrypted 1:1 message (04-05, MSG-02): a malformed
    /// wrapper or unparsable group id is discarded fail-closed — the message is
    /// still acked (it decrypted authentically), and nothing is stored under a
    /// forged identity (T-4-15).
    /// </summary>
    private void StoreSenderKeyDistribution(Messaging.SenderKeyDistributionMessage wrapper, Jid from)
    {
        if (string.IsNullOrEmpty(wrapper.GroupId)
            || !Jid.TryParse(wrapper.GroupId, out Jid? group)
            || group is null)
        {
            return; // unparsable group id: discard fail-closed, never store
        }

        if (!SenderKey.TryParseDistribution(wrapper.AxolotlSenderKeyDistributionMessage.Span, out SenderKeyDistribution? distribution)
            || distribution is null)
        {
            return; // malformed SKDM: discard fail-closed, never store
        }

        _senderKeys.PutDistribution(SenderKeyRepository.InboundAddress(group, from), distribution);
    }

    /// <summary>
    /// Emits the Pattern-5 retry receipt for a failed decrypt, best-effort (MSG-06):
    /// the typed event is raised inside the handler before any wire work, so a
    /// retry-receipt fault never breaks the receive path (the caller holds the
    /// per-address session gate; emission itself never throws through the pump).
    /// </summary>
    private async Task TryEmitRetryReceiptAsync(
        CredentialRecord row,
        Jid from,
        Jid chat,
        string messageId,
        DateTimeOffset timestamp,
        bool hasSession,
        CancellationToken cancellationToken)
    {
        try
        {
            await _owner.Receipts.EmitRetryReceiptAsync(row, from, chat, messageId, timestamp, hasSession, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Log-and-continue: the DecryptionFailed event already fired; the pump survives.
        }
    }

    private async Task SendAckAsync(string messageId, Jid from, CancellationToken cancellationToken, Jid? participant = null)
    {
        var attrs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["class"] = "message",
            ["id"] = messageId,
            ["to"] = from.ToString(),
        };
        if (participant is not null)
        {
            // Pattern 7: group messages carry the participant attribute on the ack.
            attrs["participant"] = participant.ToString();
        }

        BinaryNode ack = BinaryNode.Create("ack", attrs);
        await _owner.Connection.SendNodeAsync(ack, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleAckAsync(BinaryNode node, CancellationToken cancellationToken)
    {
        if (!node.Attrs.TryGetValue("class", out string? ackClass)
            || !string.Equals(ackClass, "message", StringComparison.Ordinal)
            || !node.Attrs.TryGetValue("id", out string? messageId)
            || string.IsNullOrEmpty(messageId))
        {
            return; // not a message ack (iq acks belong to the router)
        }

        node.Attrs.TryGetValue("error", out string? errorAttr);
        int? error = int.TryParse(errorAttr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
        node.Attrs.TryGetValue("t", out string? t);
        node.Attrs.TryGetValue("server_id", out string? serverId);
        node.Attrs.TryGetValue("phash", out string? phash);
        (SendOutcome Outcome, Jid Chat)? ack = _owner.Sender.CompleteFromAck(messageId, error, t, serverId, phash);
        if (ack is not null)
        {
            // MSG-07: the terminal ack fact reaches the stream from the SAME emit
            // point that settles the awaited send — a late/duplicate ack raises nothing.
            await _owner.RaiseMessageAckedAsync(ack.Value.Outcome, ack.Value.Chat, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Inbound receipt parse (Pattern 4, bounds-checked): ids ride the <c>id</c>
    /// attribute plus <c>list/item</c> children, groups carry <c>participant</c>, and
    /// the parsed values surface as the typed <see cref="ReceiptReceivedEventArgs"/>
    /// event. Malformed or unknown-kind receipts are silently ignored (T-4-14:
    /// informational events that gate no SDK behavior). Receipt nodes are not acked in
    /// 04-04 scope; the <c>retry</c> kind additionally dispatches the MSG-06 respond
    /// path (the same-id cache resend), best-effort — a respond fault never breaks
    /// the receive path.
    /// </summary>
    private async Task HandleReceipt(BinaryNode node, CancellationToken cancellationToken)
    {
        string typeAttr = node.Attrs.TryGetValue("type", out string? type) && !string.IsNullOrEmpty(type)
            ? type
            : "delivery"; // the historical no-type receipt form is a delivery receipt
        if (!TryParseReceiptKind(typeAttr, out ReceiptKind kind)
            || !node.Attrs.TryGetValue("id", out string? idAttr)
            || string.IsNullOrEmpty(idAttr)
            || !node.Attrs.TryGetValue("from", out string? fromAttr)
            || !Jid.TryParse(fromAttr, out Jid? from)
            || from is null)
        {
            return; // unknown kind or malformed receipt: silently ignored
        }

        var ids = new List<string> { idAttr };
        foreach (BinaryNode child in node.Children ?? (IReadOnlyList<BinaryNode>)[])
        {
            if (!string.Equals(child.Tag, "list", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (BinaryNode item in child.Children ?? (IReadOnlyList<BinaryNode>)[])
            {
                if (string.Equals(item.Tag, "item", StringComparison.Ordinal)
                    && item.Attrs.TryGetValue("id", out string? itemId)
                    && !string.IsNullOrEmpty(itemId))
                {
                    ids.Add(itemId);
                }
            }
        }

        Jid? participant = null;
        if (node.Attrs.TryGetValue("participant", out string? participantAttr)
            && !string.IsNullOrEmpty(participantAttr)
            && Jid.TryParse(participantAttr, out Jid? parsedParticipant))
        {
            participant = parsedParticipant;
        }

        Jid chat = Jid.Parse($"{DeviceDirectory.BaseUser(from)}@{from.Server}");
        DateTimeOffset timestamp = ParseTimestamp(node) ?? _owner.Clock.GetUtcNow();
        await _owner.RaiseReceiptReceivedAsync(
            new ReceiptReceivedEventArgs(kind, ids, chat, participant, timestamp),
            cancellationToken).ConfigureAwait(false);

        if (kind is ReceiptKind.Retry)
        {
            // MSG-06 respond path: parse the Pattern-5 children bounds-checked, then
            // answer with the same-id cache resend, best-effort (never throws through
            // the pump). The typed receipt event above already fired either way.
            uint? registrationId = ParseRegistrationId(FirstChild(node, "registration"));
            BinaryNode? keys = FirstChild(node, "keys");
            await TryResendForRetryReceiptAsync(from, idAttr, registrationId, keys, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Dispatches the MSG-06 respond to one peer retry receipt, best-effort: the
    /// sender's resend path degrades (Pitfall 6) rather than throws, and any residual
    /// fault is swallowed here so the receive pump keeps running (OperationCanceled
    /// propagates as the only legitimate stop).
    /// </summary>
    private async Task TryResendForRetryReceiptAsync(
        Jid requester,
        string messageId,
        uint? registrationId,
        BinaryNode? keys,
        CancellationToken cancellationToken)
    {
        try
        {
            await _owner.Sender.ResendForRetryReceiptAsync(requester, messageId, registrationId, keys, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Log-and-continue: the resend is best-effort by contract.
        }
    }

    /// <summary>Parses the 4-byte big-endian <c>&lt;registration&gt;</c> payload; anything missized or unparsable is null.</summary>
    private static uint? ParseRegistrationId(BinaryNode? registration)
    {
        byte[]? data = registration?.Data;
        if (data is not { Length: PreKeyManager.RegistrationIdWidth })
        {
            return null;
        }

        uint value = 0;
        foreach (byte b in data)
        {
            value = (value << 8) | b;
        }

        return value;
    }

    private static bool TryParseReceiptKind(string type, out ReceiptKind kind)
    {
        switch (type)
        {
            case "delivery": kind = ReceiptKind.Delivery; return true;
            case "read": kind = ReceiptKind.Read; return true;
            case "played": kind = ReceiptKind.Played; return true;
            case "sender": kind = ReceiptKind.Sender; return true;
            case "inactive": kind = ReceiptKind.Inactive; return true;
            case "retry": kind = ReceiptKind.Retry; return true;
            default: kind = default; return false;
        }
    }

    private static DateTimeOffset? ParseTimestamp(BinaryNode node)
    {
        if (!node.Attrs.TryGetValue("t", out string? t)
            || !long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds)
            || seconds < 0)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    private static BinaryNode? FirstChild(BinaryNode parent, string tag)
    {
        IReadOnlyList<BinaryNode>? children = parent.Children;
        if (children is null)
        {
            return null;
        }

        foreach (BinaryNode child in children)
        {
            if (string.Equals(child.Tag, tag, StringComparison.Ordinal))
            {
                return child;
            }
        }

        return null;
    }

    internal void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
