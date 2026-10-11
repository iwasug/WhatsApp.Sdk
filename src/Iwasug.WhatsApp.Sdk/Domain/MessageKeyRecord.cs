namespace Iwasug.WhatsApp.Sdk.Domain;

/// <summary>
/// Public projection of the wire <c>MessageKey</c> (generated proto types stay
/// internal, so the public surface never mentions them). Used by the send/receive
/// path to carry message-id metadata across layers without exposing protobuf
/// types.
/// </summary>
/// <remarks>
/// The protobuf <c>MessageKey</c> has fields: remote_jid (1), from_me (2), id (3),
/// participant (4). This record mirrors those fields with the same semantics:
/// - remoteJid: the recipient's canonical JID
/// - fromMe: whether this message was sent by the local account
/// - id: the client-generated message id (3EB0 + 18 CSPRNG bytes, 40-char uppercase hex)
/// - participant: the group participant JID (empty for 1:1 chats)
/// </remarks>
/// <param name="RemoteJid">The recipient's canonical JID.</param>
/// <param name="FromMe"><c>true</c> if the message was sent by the local account.</param>
/// <param name="Id">The client-generated message id.</param>
/// <param name="Participant">The group participant JID; empty string for 1:1 chats.</param>
public sealed record MessageKeyRecord(
    Jid RemoteJid,
    bool FromMe,
    string Id,
    string Participant);