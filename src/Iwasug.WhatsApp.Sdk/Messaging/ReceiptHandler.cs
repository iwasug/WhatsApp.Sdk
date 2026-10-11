using System.Collections.Concurrent;
using System.Globalization;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;

namespace Iwasug.WhatsApp.Sdk.Messaging;

/// <summary>
/// Receipt node construction and emission (04-04, MSG-04): Pattern-4 verbatim node
/// shapes over the connection's <c>SendNodeAsync</c> seam (pairing-frame precedent).
/// Outbound: the per-chat opt-in delivery receipt and <c>MarkRead</c> read receipts
/// with per-message keys (one receipt per sender — the same-sender-only rule).
/// </summary>
/// <remarks>
/// <para>
/// Wire shape (whatsmeow <c>receipt.go</c> behavioral reference — 04-RESEARCH
/// Pattern 4): <c>&lt;receipt id="{firstId}" type="read|delivery" to="{chatJid}"
/// t="{unix}" [participant="{senderJid}"]&gt;</c> with
/// <c>&lt;list&gt;&lt;item id="…"/&gt;&lt;/list&gt;</c> children only when more than one
/// id — the first id rides the attribute, the rest ride the list. <c>t</c> is always
/// present; <c>participant</c> appears only for group senders.
/// </para>
/// <para>
/// Receipts are stateless protocol nodes with no client-side dedup machine: calling
/// twice sends two identical receipts, and that is the documented contract (MSG-04
/// idempotency edge). No log call on this class ever sees message content.
/// </para>
/// </remarks>
internal sealed class ReceiptHandler
{
    private readonly Messenger _owner;
    private readonly ConcurrentDictionary<string, bool> _deliveryReceiptChats = new(StringComparer.Ordinal);

    internal ReceiptHandler(Messenger owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    /// <summary>
    /// Per-chat delivery-receipt opt-in (Open Question 4 resolved: no presence coupling,
    /// off until the host opts in). Process-local by design — this is host policy, not
    /// durable state (the D-02 store set is unchanged).
    /// </summary>
    internal void SetDeliveryReceipts(Jid chat, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (enabled)
        {
            _deliveryReceiptChats[chat.ToString()] = true;
        }
        else
        {
            _deliveryReceiptChats.TryRemove(chat.ToString(), out _);
        }
    }

    /// <summary>Whether delivery receipts are enabled for <paramref name="chat"/> (default off).</summary>
    internal bool IsDeliveryReceiptsEnabled(Jid chat) =>
        chat is not null && _deliveryReceiptChats.ContainsKey(chat.ToString());

    /// <summary>Sends the opt-in delivery receipt for one decrypted inbound message.</summary>
    internal async Task SendDeliveryReceiptAsync(Jid chat, string messageId, DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        BinaryNode node = BuildDeliveryReceiptNode(chat, messageId, timestamp);
        await _owner.Connection.SendNodeAsync(node, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends Pattern-4 read receipts for <paramref name="keys"/>, grouped per
    /// (chat, participant) sender pair — one wire receipt per sender, in first-seen order.
    /// </summary>
    internal async Task SendReadReceiptsAsync(IReadOnlyList<MessageKeyRecord> keys, CancellationToken cancellationToken)
    {
        var groups = new Dictionary<(string Chat, string Participant), List<string>>();
        foreach (MessageKeyRecord key in keys)
        {
            var groupKey = (key.RemoteJid.ToString(), key.Participant ?? string.Empty);
            if (!groups.TryGetValue(groupKey, out List<string>? ids))
            {
                ids = new List<string>();
                groups[groupKey] = ids;
            }

            ids.Add(key.Id);
        }

        foreach (((string chat, string participant), List<string> ids) in groups)
        {
            BinaryNode node = BuildReadReceiptNode(
                Jid.Parse(chat),
                ids,
                participant.Length == 0 ? null : participant,
                _owner.Clock.GetUtcNow());
            await _owner.Connection.SendNodeAsync(node, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Pattern-4 read receipt (fixture seam: internal static, pinned by message-wire-fixtures.json).</summary>
    internal static BinaryNode BuildReadReceiptNode(Jid chat, IReadOnlyList<string> messageIds, string? participant, DateTimeOffset timestamp) =>
        BuildReceiptNode("read", chat, messageIds, participant, timestamp);

    /// <summary>Pattern-4 delivery receipt for a single message id (fixture seam).</summary>
    internal static BinaryNode BuildDeliveryReceiptNode(Jid chat, string messageId, DateTimeOffset timestamp) =>
        BuildReceiptNode("delivery", chat, [messageId], participant: null, timestamp);

    private static BinaryNode BuildReceiptNode(string type, Jid chat, IReadOnlyList<string> messageIds, string? participant, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(messageIds);
        if (messageIds.Count == 0)
        {
            throw new SdkFormatException("A receipt needs at least one message id.");
        }

        foreach (string id in messageIds)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new SdkFormatException("Receipt message ids must be non-empty.");
            }
        }

        var attrs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = messageIds[0],
            ["type"] = type,
            ["to"] = chat.ToString(),
            ["t"] = timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(participant))
        {
            attrs["participant"] = participant;
        }

        if (messageIds.Count == 1)
        {
            return BinaryNode.Create("receipt", attrs);
        }

        var items = new BinaryNode[messageIds.Count - 1];
        for (int i = 1; i < messageIds.Count; i++)
        {
            items[i - 1] = BinaryNode.Create(
                "item",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["id"] = messageIds[i] });
        }

        return BinaryNode.FromChildren("receipt", [BinaryNode.FromChildren("list", items)], attrs);
    }
}
