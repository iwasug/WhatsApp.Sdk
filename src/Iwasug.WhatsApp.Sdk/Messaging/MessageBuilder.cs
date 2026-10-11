using System.Globalization;
using Google.Protobuf;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;

namespace Iwasug.WhatsApp.Sdk.Messaging;

/// <summary>
/// Typed inputs identifying the message being replied to (04-02, internal).
/// Plan 04-04 supplies these values from the extended recent-message cache
/// entry; this record only carries them into <c>ContextInfo</c>.
/// </summary>
/// <param name="StanzaId">Id of the message being replied to.</param>
/// <param name="Participant">Sender JID of the quoted message (groups); null for 1:1 chats.</param>
/// <param name="QuotedText">Render source of the quoted message (its former plaintext).</param>
/// <param name="RemoteJid">Chat JID the quoted message belongs to; null when it equals the reply chat.</param>
internal sealed record QuotedMessageRef(
    string StanzaId,
    string? Participant,
    string QuotedText,
    string? RemoteJid);

/// <summary>
/// One per-device ciphertext for message-node assembly (04-02, internal).
/// </summary>
/// <param name="Device">Target device JID (canonical form reaches the <c>jid</c> attribute).</param>
/// <param name="EncType">Enc type token (<c>msg</c> for ratchet messages, <c>pkmsg</c> for first-contact prekey messages).</param>
/// <param name="Ciphertext">Wire ciphertext bytes (ride <c>enc</c> node <c>Data</c>, never <c>Text</c>).</param>
internal sealed record DeviceCiphertext(Jid Device, string EncType, byte[] Ciphertext);

/// <summary>
/// Builds the plaintext <c>Message</c> protobuf payloads and assembles the
/// <c>message</c> stanza nodes that carry per-device ciphertexts (04-02, internal).
/// </summary>
/// <remarks>
/// <para>
/// Shape provenance (behavioral reference, independently written C#): plain
/// text rides <c>Message.conversation</c> (field 1) while quote-carrying
/// replies ride <c>Message.extendedTextMessage</c> (field 6) with
/// <c>ContextInfo</c> (stanzaId = 1, participant = 2, quotedMessage = 3,
/// remoteJID = 4, mentionedJID = 15) — learned read-only from the
/// MIT-licensed Baileys sources at commit 0af2386
/// (<c>WAProto/WAProto.proto</c>; 04-RESEARCH Assumption A2). The node
/// assembly seam (<c>message</c> attrs <c>id</c>/<c>to</c>/<c>type</c>/<c>t</c>
/// with a per-device <c>to jid</c> wrapping <c>enc v="2" type</c>) follows
/// 04-RESEARCH Pattern 2 (whatsmeow <c>send.go</c> behavioral read).
/// </para>
/// <para>
/// Discipline (ClientPayloadFactory precedent): this factory never logs (it
/// handles message text); inputs are validated fail-closed with rule-only
/// <see cref="SdkFormatException"/> messages before any byte is emitted; all
/// human text lives inside the protobuf payloads and node attributes carry
/// only ids/JIDs/tokens (Pitfall 8); text is UTF-8 inside protobuf string
/// fields with no normalization anywhere.
/// </para>
/// </remarks>
internal static class MessageBuilder
{
    /// <summary>Protobuf field number of <c>Message.conversation</c> (Baileys WAProto role).</summary>
    internal const int ConversationFieldNumber = 1;

    /// <summary>Protobuf field number of <c>Message.extendedTextMessage</c> (Baileys WAProto role).</summary>
    internal const int ExtendedTextMessageFieldNumber = 6;

    /// <summary>Protobuf field number of <c>ExtendedTextMessage.contextInfo</c> (Baileys WAProto role).</summary>
    internal const int ContextInfoFieldNumber = 17;

    /// <summary>Enc version attribute carried on every <c>enc</c> child (whatsmeow send.go behavior).</summary>
    internal const string EncVersion = "2";

    /// <summary>Enc type token for Double Ratchet messages.</summary>
    internal const string EncTypeMessage = "msg";

    /// <summary>Enc type token for first-contact prekey messages.</summary>
    internal const string EncTypePreKey = "pkmsg";

    /// <summary>
    /// Builds the serialized plaintext for a plain-text message:
    /// <c>Message { Conversation = text }</c>.
    /// </summary>
    /// <param name="text">Message text (UTF-8 inside the protobuf field, never normalized).</param>
    /// <returns>The serialized <c>Message</c> plaintext bytes.</returns>
    /// <exception cref="SdkFormatException">The text is null or empty.</exception>
    internal static byte[] BuildPlainText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new SdkFormatException("Message text must not be null or empty.");
        }

        return new Message { Conversation = text }.ToByteArray();
    }

    /// <summary>
    /// Builds the serialized plaintext for a quote-carrying reply:
    /// <c>Message { ExtendedTextMessage { Text, ContextInfo { … } } }</c>.
    /// </summary>
    /// <param name="text">Reply text (UTF-8 inside the protobuf field, never normalized).</param>
    /// <param name="quote">Quoted-message reference (plan 04-04 supplies it from the cache).</param>
    /// <returns>The serialized <c>Message</c> plaintext bytes.</returns>
    /// <exception cref="SdkFormatException">The text or any required quote field is null or empty.</exception>
    internal static byte[] BuildQuotedText(string text, QuotedMessageRef quote)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new SdkFormatException("Message text must not be null or empty.");
        }

        ArgumentNullException.ThrowIfNull(quote);
        if (string.IsNullOrEmpty(quote.StanzaId))
        {
            throw new SdkFormatException("Quoted stanza ids must not be null or empty.");
        }

        if (string.IsNullOrEmpty(quote.QuotedText))
        {
            throw new SdkFormatException("Quoted message text must not be null or empty.");
        }

        var context = new ContextInfo
        {
            StanzaId = quote.StanzaId,
            QuotedMessage = new Message { Conversation = quote.QuotedText },
        };
        if (quote.Participant is not null)
        {
            context.Participant = quote.Participant;
        }

        if (quote.RemoteJid is not null)
        {
            context.RemoteJID = quote.RemoteJid;
        }

        return new Message
        {
            ExtendedTextMessage = new ExtendedTextMessage
            {
                Text = text,
                ContextInfo = context,
            },
        }.ToByteArray();
    }

    /// <summary>
    /// Parses serialized plaintext back into its typed <c>Message</c> form
    /// (fixture-test seam: lets facts assert on parsed fields, never substrings).
    /// </summary>
    /// <param name="plaintext">Serialized <c>Message</c> bytes.</param>
    /// <returns>The parsed message.</returns>
    /// <exception cref="SdkFormatException">The bytes are null, empty, or not a well-formed message.</exception>
    internal static Message ParseMessage(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (plaintext.Length == 0)
        {
            throw new SdkFormatException("Message plaintext must not be empty.");
        }

        try
        {
            return Message.Parser.ParseFrom(plaintext);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new SdkFormatException("Message plaintext failed validation.", ex);
        }
    }

    /// <summary>
    /// Assembles the <c>message</c> stanza node carrying one <c>enc</c> child
    /// per device: <c>message</c> attrs <c>id</c>/<c>to</c>/<c>type</c>/<c>t</c>
    /// with a per-device <c>to jid</c> child wrapping
    /// <c>enc v="2" type</c> whose ciphertext rides <c>Data</c> (Pitfall 8).
    /// Encryption itself is plan 04-03's work — callers pass the ciphertexts.
    /// </summary>
    /// <param name="destination">Destination chat JID (canonical form reaches the <c>to</c> attribute).</param>
    /// <param name="messageId">Client-generated message id.</param>
    /// <param name="messageType">Message type token.</param>
    /// <param name="timestampSeconds">Send-time unix seconds for the <c>t</c> attribute.</param>
    /// <param name="devices">Per-device ciphertexts (at least one).</param>
    /// <returns>The assembled stanza node (encode with <see cref="BinaryNodeCodec"/>).</returns>
    /// <exception cref="SdkFormatException">Any input violates its shape rule.</exception>
    internal static BinaryNode AssembleMessageNode(
        Jid destination,
        string messageId,
        string messageType,
        long timestampSeconds,
        IReadOnlyList<DeviceCiphertext> devices)
    {
        if (destination is null)
        {
            throw new SdkFormatException("Message destination JIDs must not be null.");
        }

        if (string.IsNullOrEmpty(messageId))
        {
            throw new SdkFormatException("Message ids must not be null or empty.");
        }

        if (string.IsNullOrEmpty(messageType))
        {
            throw new SdkFormatException("Message type tokens must not be null or empty.");
        }

        if (timestampSeconds < 0)
        {
            throw new SdkFormatException("Message timestamps must not be negative.");
        }

        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Count == 0)
        {
            throw new SdkFormatException("Message device sets must not be empty.");
        }

        var recipients = new List<BinaryNode>(devices.Count);
        foreach (DeviceCiphertext device in devices)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(device.Device);
            if (string.IsNullOrEmpty(device.EncType))
            {
                throw new SdkFormatException("Device enc types must not be null or empty.");
            }

            ArgumentNullException.ThrowIfNull(device.Ciphertext);
            if (device.Ciphertext.Length == 0)
            {
                throw new SdkFormatException("Device ciphertexts must not be empty.");
            }

            recipients.Add(BinaryNode.FromChildren(
                "to",
                [BinaryNode.FromBytes(
                    "enc",
                    device.Ciphertext,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["v"] = EncVersion,
                        ["type"] = device.EncType,
                    })],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["jid"] = device.Device.ToString(),
                }));
        }

        return BinaryNode.FromChildren(
            "message",
            recipients,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["id"] = messageId,
                ["to"] = destination.ToString(),
                ["type"] = messageType,
                ["t"] = timestampSeconds.ToString(CultureInfo.InvariantCulture),
            });
    }
}
