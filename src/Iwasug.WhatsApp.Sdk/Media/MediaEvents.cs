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