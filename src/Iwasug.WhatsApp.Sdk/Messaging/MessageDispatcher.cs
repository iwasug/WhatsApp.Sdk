using System.Globalization;
using Google.Protobuf;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Signal;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.Runtime;

namespace Iwasug.WhatsApp.Sdk.Messaging;

/// <summary>
/// Inbound stanza dispatch over <see cref="WaConnection.StanzaReceived"/> (04-03):
/// <c>message</c> nodes run the decrypt → persist → ack → event → cache ordering
/// contract; <c>ack</c> nodes correlate pending sends; <c>receipt</c> nodes parse
/// bounds-checked into typed <see cref="ReceiptReceivedEventArgs"/> events (04-04,
/// MSG-04) and the opt-in delivery receipt rides out after the message's ack + event.
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
/// Decryption failure is a typed outcome, not an event: no ack claims success, no event
/// fires, the pump keeps running (plan 04-04 owns the retry receipt). A cache-write
/// failure never breaks the path (D-20): the event is already raised and the ack already
/// sent, so the failure is swallowed after the fact.
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
    private int _disposed;

    internal MessageDispatcher(Messenger owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

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
                HandleAck(node);
                break;
            case "receipt":
                HandleReceipt(node);
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

        string address = DoubleRatchet.SignalSessionRepository.AddressFor(from);
        var repository = new DoubleRatchet.SignalSessionRepository(stores.SignalSessions, new DurableWriteRetryPolicy());
        DoubleRatchet? session = await repository.LoadAsync(address, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return; // no session for this sender device: typed no-op (04-04 owns the retry receipt)
        }

        SignalDecryptOutcome outcome = session.Decrypt(wire, isPreKey, cancellationToken);
        if (outcome is not SignalDecryptOutcome.Decrypted decrypted)
        {
            return; // decryption failure: no ack, no event, pump continues
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
        await repository.SaveAsync(address, session, cancellationToken).ConfigureAwait(false);

        Jid chat = Jid.Parse($"{DeviceDirectory.BaseUser(from)}@{from.Server}");
        bool isFromMe = string.Equals(DeviceDirectory.BaseUser(from), row.PairedUser, StringComparison.Ordinal);
        DateTimeOffset timestamp = ParseTimestamp(node) ?? _owner.Clock.GetUtcNow();
        await SendAckAsync(messageId, from, cancellationToken).ConfigureAwait(false);

        string? text = message.Conversation.Length == 0 ? null : message.Conversation;
        var key = new MessageKeyRecord(chat, isFromMe, messageId, string.Empty);
        _owner.RaiseMessageReceived(new MessageReceivedEventArgs(key, chat, from, isFromMe, timestamp, text));

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

    private async Task SendAckAsync(string messageId, Jid from, CancellationToken cancellationToken)
    {
        BinaryNode ack = BinaryNode.Create(
            "ack",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["class"] = "message",
                ["id"] = messageId,
                ["to"] = from.ToString(),
            });
        await _owner.Connection.SendNodeAsync(ack, cancellationToken).ConfigureAwait(false);
    }

    private void HandleAck(BinaryNode node)
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
        _owner.Sender.CompleteFromAck(messageId, error, t, serverId, phash);
    }

    /// <summary>
    /// Inbound receipt parse (Pattern 4, bounds-checked): ids ride the <c>id</c>
    /// attribute plus <c>list/item</c> children, groups carry <c>participant</c>, and
    /// the parsed values surface as the typed <see cref="ReceiptReceivedEventArgs"/>
    /// event. Malformed or unknown-kind receipts are silently ignored (T-4-14:
    /// informational events that gate no SDK behavior). Receipt nodes are not acked in
    /// 04-04 scope; the <c>retry</c> kind's response path lands with Task 2's exchange.
    /// </summary>
    private void HandleReceipt(BinaryNode node)
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
        _owner.RaiseReceiptReceived(new ReceiptReceivedEventArgs(kind, ids, chat, participant, timestamp));
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
