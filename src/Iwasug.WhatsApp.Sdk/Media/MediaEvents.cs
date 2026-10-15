using Iwasug.WhatsApp.Sdk.Domain;

namespace Iwasug.WhatsApp.Sdk.Messaging;

/// <summary>
/// Raised for a fully verified inbound media message. <see cref="Content"/> is a
/// read-only <see cref="System.IO.MemoryStream"/> over the verified plaintext; the
/// host owns its disposal.
/// </summary>
public sealed record MediaReceivedEventArgs(
    Domain.MessageKeyRecord Key,
    Domain.Jid Chat,
    Domain.Jid Sender,
    bool IsFromMe,
    DateTimeOffset Timestamp,
    Iwasug.WhatsApp.Sdk.Media.MediaKind Kind,
    string Mimetype,
    long FileLength,
    string? Caption = null,
    string? FileName = null,
    int? Seconds = null,
    bool Ptt = false,
    System.IO.Stream? Content = null);

/// <summary>
/// Stream event twin of <see cref="MediaReceivedEventArgs"/>. <see cref="Content"/>
/// ownership is the same as the handler twin: the host disposes it.
/// </summary>
public sealed record MediaReceivedEvent(
    Domain.MessageKeyRecord Key,
    Domain.Jid Chat,
    Domain.Jid Sender,
    bool IsFromMe,
    DateTimeOffset Timestamp,
    Iwasug.WhatsApp.Sdk.Media.MediaKind Kind,
    string Mimetype,
    long FileLength,
    string? Caption = null,
    string? FileName = null,
    int? Seconds = null,
    bool Ptt = false,
    System.IO.Stream? Content = null) : WhatsAppEvent(Timestamp);

/// <summary>
/// Raised when inbound media download recovery is exhausted or terminally failed —
/// the host's only signal for an unrecoverable inbound media message (MED-02). Carries
/// no key material: only the message identity and a rule-only reason string.
/// </summary>
/// <remarks>
/// Handler-only delivery by design: unlike the receive-success facts this failure
/// signal is NOT published to the <see cref="Messenger.ReadEventsAsync"/> stream (it
/// adds no wire vocabulary a stream consumer could act on beyond what the handler twin
/// offers). Subscribe <see cref="Messenger.MediaDownloadFailed"/> for it.
/// </remarks>
public sealed class MediaDownloadFailedEventArgs : EventArgs
{
    /// <summary>Key of the media message that could not be recovered.</summary>
    public Domain.MessageKeyRecord Key { get; init; }

    /// <summary>Chat the media message belongs to.</summary>
    public Domain.Jid Chat { get; init; }

    /// <summary>Id of the media message that could not be recovered.</summary>
    public string? MessageId { get; init; }

    /// <summary>Rule-only reason (e.g. <c>media-retry error 2</c>, <c>media-retry request cap exhausted</c>); never key material.</summary>
    public string? Reason { get; init; }

    /// <summary>Creates the failure fact.</summary>
    /// <param name="key">Key of the media message that could not be recovered.</param>
    /// <param name="chat">Chat the media message belongs to.</param>
    /// <param name="messageId">Id of the media message that could not be recovered.</param>
    /// <param name="reason">Rule-only reason; never key material.</param>
    public MediaDownloadFailedEventArgs(Domain.MessageKeyRecord key, Domain.Jid chat, string? messageId = null, string? reason = null)
    {
        Key = key;
        Chat = chat;
        MessageId = messageId;
        Reason = reason;
    }
}