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

    /// <summary>Protobuf field number of <c>Message.imageMessage</c> (Baileys WAProto role).</summary>
    internal const int ImageMessageFieldNumber = 3;

    /// <summary>Protobuf field number of <c>Message.documentMessage</c> (Baileys WAProto role).</summary>
    internal const int DocumentMessageFieldNumber = 7;

    /// <summary>Protobuf field number of <c>Message.audioMessage</c> (Baileys WAProto role).</summary>
    internal const int AudioMessageFieldNumber = 8;

    /// <summary>Protobuf field number of <c>Message.protocolMessage</c> (Baileys WAProto role, Phase 8).</summary>
    internal const int ProtocolMessageFieldNumber = 12;

    /// <summary>Protobuf field number of <c>Message.reactionMessage</c> (Baileys WAProto role, Phase 8).</summary>
    internal const int ReactionMessageFieldNumber = 46;

    /// <summary>ProtocolMessage.Type value for delete-for-everyone (Baileys WAProto role).</summary>
    internal const int ProtocolTypeRevoke = 0;

    /// <summary>ProtocolMessage.Type value for edit (Baileys WAProto role).</summary>
    internal const int ProtocolTypeMessageEdit = 14;

    internal const string MediaTypeImage = "image";
    internal const string MediaTypeDocument = "document";
    internal const string MediaTypeAudio = "audio";
    internal const string MediaTypePtt = "ptt";

    /// <summary>Enc version attribute carried on every <c>enc</c> child (whatsmeow send.go behavior).</summary>
    internal const string EncVersion = "2";

    /// <summary>Enc type token for Double Ratchet messages.</summary>
    internal const string EncTypeMessage = "msg";

    /// <summary>Enc type token for first-contact prekey messages.</summary>
    internal const string EncTypePreKey = "pkmsg";

    /// <summary>Enc type token for sender-key group messages (04-05, MSG-02).</summary>
    internal const string EncTypeSenderKey = "skmsg";

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
    /// Builds the serialized plaintext for one sender-key distribution (04-05,
    /// MSG-02): <c>Message { SenderKeyDistributionMessage { GroupId,
    /// AxolotlSenderKeyDistributionMessage } }</c>. The wrapper replaces the message
    /// payload for the pairwise SKDM fanout — it is delivered 1:1-encrypted to each
    /// member device and never appears as a message event on the receiving side.
    /// </summary>
    /// <param name="groupId">The group chat JID the distribution belongs to (canonical form).</param>
    /// <param name="distributionWire">The complete SKDM wire (<c>version || serialized axolotl SKDM</c>).</param>
    /// <returns>The serialized <c>Message</c> plaintext bytes.</returns>
    /// <exception cref="SdkFormatException">The group id is null/empty or the distribution bytes are null/empty.</exception>
    internal static byte[] BuildSenderKeyDistribution(string groupId, byte[] distributionWire)
    {
        if (string.IsNullOrEmpty(groupId))
        {
            throw new SdkFormatException("Sender-key distribution group ids must not be null or empty.");
        }

        ArgumentNullException.ThrowIfNull(distributionWire);
        if (distributionWire.Length == 0)
        {
            throw new SdkFormatException("Sender-key distribution bytes must not be empty.");
        }

        return new Message
        {
            SenderKeyDistributionMessage = new SenderKeyDistributionMessage
            {
                GroupId = groupId,
                AxolotlSenderKeyDistributionMessage = ByteString.CopyFrom(distributionWire),
            },
        }.ToByteArray();
    }

    /// <summary>
    /// Builds the serialized plaintext for an image message using an upload result.
    /// </summary>
    internal static byte[] BuildImageMessage(Iwasug.WhatsApp.Sdk.Media.MediaUploadResult upload, string? caption, int? width = null, int? height = null, byte[]? thumbnail = null, string? mimetype = null)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ValidateUpload(upload);
        if (thumbnail is not null && thumbnail.Length > Iwasug.WhatsApp.Sdk.Media.MediaMimeAllowList.MaxThumbnailBytes)
        {
            throw new SdkFormatException("Media thumbnails must not exceed 64 KiB.");
        }
        var image = new ImageMessage
        {
            Url = upload.Url,
            Mimetype = string.IsNullOrEmpty(mimetype) ? "image/jpeg" : mimetype,
            FileSha256 = ByteString.CopyFrom(upload.FileSha256),
            FileLength = upload.FileLength,
            MediaKey = ByteString.CopyFrom(upload.MediaKey),
            FileEncSha256 = ByteString.CopyFrom(upload.FileEncSha256),
            DirectPath = upload.DirectPath,
            MediaKeyTimestamp = upload.MediaKeyTimestamp,
        };
        if (!string.IsNullOrEmpty(caption)) image.Caption = caption;
        if (width.HasValue) { if (width < 0 || width > 10000) throw new SdkFormatException("Media dimensions out of range."); image.Width = width.Value; }
        if (height.HasValue) { if (height < 0 || height > 10000) throw new SdkFormatException("Media dimensions out of range."); image.Height = height.Value; }
        if (thumbnail is not null) image.JpegThumbnail = ByteString.CopyFrom(thumbnail);
        return new Message { ImageMessage = image }.ToByteArray();
    }

    /// <summary>
    /// Builds the serialized plaintext for a document message using an upload result.
    /// </summary>
    internal static byte[] BuildDocumentMessage(Iwasug.WhatsApp.Sdk.Media.MediaUploadResult upload, string mimetype, string? title, string? fileName, string? caption, byte[]? thumbnail = null)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ValidateUpload(upload);
        if (thumbnail is not null && thumbnail.Length > Iwasug.WhatsApp.Sdk.Media.MediaMimeAllowList.MaxThumbnailBytes)
        {
            throw new SdkFormatException("Media thumbnails must not exceed 64 KiB.");
        }
        if (string.IsNullOrWhiteSpace(mimetype) || (!mimetype.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) && !mimetype.Equals("text/plain", StringComparison.OrdinalIgnoreCase)))
        {
            throw new SdkFormatException("Media mimetype is not on the allow list.");
        }
        if (fileName is not null && (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..")))
        {
            throw new SdkFormatException("Media filenames must not contain path separators or traversal.");
        }
        var doc = new DocumentMessage
        {
            Url = upload.Url,
            Mimetype = mimetype,
            FileSha256 = ByteString.CopyFrom(upload.FileSha256),
            FileLength = upload.FileLength,
            MediaKey = ByteString.CopyFrom(upload.MediaKey),
            FileName = fileName ?? string.Empty,
            FileEncSha256 = ByteString.CopyFrom(upload.FileEncSha256),
            DirectPath = upload.DirectPath,
            MediaKeyTimestamp = upload.MediaKeyTimestamp,
        };
        if (!string.IsNullOrEmpty(title)) doc.Title = title;
        if (!string.IsNullOrEmpty(caption)) doc.Caption = caption;
        if (thumbnail is not null) doc.JpegThumbnail = ByteString.CopyFrom(thumbnail);
        return new Message { DocumentMessage = doc }.ToByteArray();
    }

    /// <summary>
    /// Builds the serialized plaintext for a voice note using an upload result.
    /// </summary>
    internal static byte[] BuildVoiceNoteMessage(Iwasug.WhatsApp.Sdk.Media.MediaUploadResult upload, string mimetype, int seconds, byte[]? waveform)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ValidateUpload(upload);
        if (string.IsNullOrWhiteSpace(mimetype) || !mimetype.StartsWith("audio/ogg", StringComparison.OrdinalIgnoreCase))
        {
            throw new SdkFormatException("Media mimetype is not on the allow list.");
        }
        if (seconds < 0 || seconds > 3600)
        {
            throw new SdkFormatException("Media seconds out of range.");
        }
        if (waveform is not null)
        {
            if (waveform.Length != 64) throw new SdkFormatException("Media waveform must be 64 entries.");
            if (waveform.Any(b => b > 100)) throw new SdkFormatException("Media waveform values out of range.");
        }
        var audio = new AudioMessage
        {
            Url = upload.Url,
            Mimetype = mimetype,
            FileSha256 = ByteString.CopyFrom(upload.FileSha256),
            FileLength = upload.FileLength,
            Seconds = seconds,
            Ptt = true,
            MediaKey = ByteString.CopyFrom(upload.MediaKey),
            FileEncSha256 = ByteString.CopyFrom(upload.FileEncSha256),
            DirectPath = upload.DirectPath,
            MediaKeyTimestamp = upload.MediaKeyTimestamp,
        };
        if (waveform is not null) audio.Waveform.AddRange(waveform.Select(b => (int)b));
        return new Message { AudioMessage = audio }.ToByteArray();
    }

    private static void ValidateUpload(Iwasug.WhatsApp.Sdk.Media.MediaUploadResult upload)
    {
        if (upload.MediaKey.Length != 32) throw new SdkFormatException("Media keys must be 32 bytes.");
        if (upload.FileSha256.Length != 32 || upload.FileEncSha256.Length != 32) throw new SdkFormatException("Media hashes must be 32 bytes.");
        if (string.IsNullOrEmpty(upload.DirectPath) || !upload.DirectPath.StartsWith('/')) throw new SdkFormatException("Media direct paths must start with '/'.");
        if (upload.FileLength <= 0) throw new SdkFormatException("Media file length must be positive.");
        if (string.IsNullOrEmpty(upload.Url)) throw new SdkFormatException("Media urls must not be empty.");
    }

    /// <summary>
    /// Builds the serialized plaintext for a reaction (EXT-03): a <c>Message</c>
    /// carrying a <c>ReactionMessage</c> branch. An empty <paramref name="emoji"/>
    /// is a valid removal and is preserved verbatim.
    /// </summary>
    internal static byte[] BuildReaction(string? targetMessageId, string? emoji, string? remoteJid = null, string? participant = null)
    {
        var reaction = new ReactionMessage
        {
            Text = emoji ?? string.Empty,
        };
        if (!string.IsNullOrEmpty(targetMessageId))
        {
            reaction.Key = new MessageKey { Id = targetMessageId };
            if (!string.IsNullOrEmpty(remoteJid)) reaction.Key.RemoteJid = remoteJid;
            if (!string.IsNullOrEmpty(participant)) reaction.Key.Participant = participant;
        }
        return new Message { ReactionMessage = reaction }.ToByteArray();
    }

    /// <summary>
    /// Builds the serialized plaintext for an edit (EXT-04): a <c>Message</c>
    /// carrying a <c>ProtocolMessage</c> with type MESSAGE_EDIT and the new
    /// body in <c>editedMessage</c>.
    /// </summary>
    internal static byte[] BuildProtocolEdit(string? originalMessageId, string newText, string? remoteJid = null, string? participant = null)
    {
        if (string.IsNullOrEmpty(newText))
        {
            throw new SdkFormatException("Message text must not be null or empty.");
        }

        var protocol = new ProtocolMessage
        {
            Type = ProtocolTypeMessageEdit,
            EditedMessage = new Message { Conversation = newText },
        };
        if (!string.IsNullOrEmpty(originalMessageId))
        {
            protocol.Key = new MessageKey { Id = originalMessageId };
            if (!string.IsNullOrEmpty(remoteJid)) protocol.Key.RemoteJid = remoteJid;
            if (!string.IsNullOrEmpty(participant)) protocol.Key.Participant = participant;
        }
        return new Message { ProtocolMessage = protocol }.ToByteArray();
    }

    /// <summary>
    /// Builds the serialized plaintext for a delete-for-everyone (EXT-04): a
    /// <c>Message</c> carrying a <c>ProtocolMessage</c> with type REVOKE keying
    /// the target message.
    /// </summary>
    internal static byte[] BuildProtocolRevoke(string? targetMessageId, string? remoteJid = null, string? participant = null)
    {
        var protocol = new ProtocolMessage
        {
            Type = ProtocolTypeRevoke,
        };
        if (!string.IsNullOrEmpty(targetMessageId))
        {
            protocol.Key = new MessageKey { Id = targetMessageId };
            if (!string.IsNullOrEmpty(remoteJid)) protocol.Key.RemoteJid = remoteJid;
            if (!string.IsNullOrEmpty(participant)) protocol.Key.Participant = participant;
        }
        return new Message { ProtocolMessage = protocol }.ToByteArray();
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
