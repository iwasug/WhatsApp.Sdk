using Iwasug.WhatsApp.Sdk.Domain;

namespace Iwasug.WhatsApp.Sdk.Messaging;

internal sealed record MediaReceivedEventArgs(
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

internal sealed record MediaReceivedEvent(
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