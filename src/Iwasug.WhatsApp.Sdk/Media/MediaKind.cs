namespace Iwasug.WhatsApp.Sdk.Media;

/// <summary>
/// The media kinds supported by the tracer slice (06-01): image, document, and
/// voice note. Voice notes are the only audio shape in v1 (a raw audio-file
/// sender is deliberately out of scope).
/// </summary>
public enum MediaKind
{
    /// <summary>Image media (PNG/JPEG/...).</summary>
    Image = 0,

    /// <summary>Document media.</summary>
    Document = 1,

    /// <summary>Voice note (push-to-talk audio).</summary>
    VoiceNote = 2,
}

internal static class MediaKindInfo
{
    internal static string InfoString(MediaKind kind) => kind switch
    {
        MediaKind.Image => MediaCrypto.ImageKeysInfo,
        MediaKind.Document => MediaCrypto.DocumentKeysInfo,
        MediaKind.VoiceNote => MediaCrypto.AudioKeysInfo,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static string MmsType(MediaKind kind) => kind switch
    {
        MediaKind.Image => "image",
        MediaKind.Document => "document",
        MediaKind.VoiceNote => "audio",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static string MediaType(MediaKind kind) => kind switch
    {
        MediaKind.Image => "image",
        MediaKind.Document => "document",
        MediaKind.VoiceNote => "ptt",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

internal sealed record MediaUploadResult(
    string Url,
    string DirectPath,
    byte[] MediaKey,
    byte[] FileSha256,
    byte[] FileEncSha256,
    long FileLength,
    long MediaKeyTimestamp);

internal static class MediaMimeAllowList
{
    internal static readonly string[] Images = { "image/jpeg", "image/png" };
    internal static readonly string[] Documents = { "application/pdf", "text/plain" };
    internal const string VoiceNotePrefix = "audio/ogg";
    internal const long MaxThumbnailBytes = 65536;
}

internal static class MediaFieldNumbers
{
    internal const int ImageMessage = 3;
    internal const int DocumentMessage = 7;
    internal const int AudioMessage = 8;
}