using System;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Safety;

namespace Iwasug.WhatsApp.Sdk.Media;

/// <summary>
/// Receive-side media metadata validation (D-67, Pitfall 4/Pitfall 8): every field
/// arriving from the wire is untrusted. Validation is fail-closed and runs BEFORE any
/// download or decryption; a violation delivers nothing.
/// </summary>
internal static class MediaMetadata
{
    internal const int MediaKeyLength = MediaCrypto.MediaKeyLength;
    internal const int HashLength = MediaCrypto.HashLength;
    internal const long MaxSeconds = 36000;
    internal const int MaxWaveformLength = 64;
    internal const int MaxWaveformValue = 100;

    /// <summary>Validate all received media fields fail-closed.</summary>
    internal static void Validate(
        MediaKind kind,
        byte[]? mediaKey,
        byte[]? fileSha256,
        byte[]? fileEncSha256,
        string? directPath,
        long fileLength,
        string? mimetype,
        string? fileName = null,
        int? seconds = null,
        int? width = null,
        int? height = null,
        byte[]? waveform = null,
        long imageCapBytes = 16 * 1024 * 1024,
        long documentCapBytes = 64 * 1024 * 1024,
        long audioCapBytes = 16 * 1024 * 1024)
    {
        if (mediaKey is null || mediaKey.Length != MediaKeyLength)
        {
            throw new SdkFormatException("Received media key must be 32 bytes.");
        }
        if (fileSha256 is null || fileSha256.Length != HashLength)
        {
            throw new SdkFormatException("Received file SHA-256 must be 32 bytes.");
        }
        if (fileEncSha256 is null || fileEncSha256.Length != HashLength)
        {
            throw new SdkFormatException("Received file encrypted SHA-256 must be 32 bytes.");
        }
        if (string.IsNullOrEmpty(directPath) || !directPath.StartsWith("/", StringComparison.Ordinal))
        {
            throw new SdkFormatException("Received direct path must be non-empty and start with '/'.");
        }
        if (fileLength <= 0)
        {
            throw new SdkFormatException("Received file length must be positive.");
        }

        long cap = kind switch
        {
            MediaKind.Image => imageCapBytes,
            MediaKind.Document => documentCapBytes,
            MediaKind.VoiceNote => audioCapBytes,
            _ => throw new SdkFormatException("Unknown media kind."),
        };
        if (fileLength > cap)
        {
            throw new SdkFormatException("Received media exceeds the byte cap.");
        }

        ValidateMimetype(kind, mimetype);
        if (fileName is not null)
        {
            SanitizeFileName(fileName);
        }
        if (seconds.HasValue)
        {
            if (seconds.Value < 0 || seconds.Value > MaxSeconds)
            {
                throw new SdkFormatException("Received media seconds out of range.");
            }
        }
        if (width.HasValue && (width.Value < 0 || width.Value > 100000))
        {
            throw new SdkFormatException("Received media width out of range.");
        }
        if (height.HasValue && (height.Value < 0 || height.Value > 100000))
        {
            throw new SdkFormatException("Received media height out of range.");
        }
        if (waveform is not null)
        {
            if (waveform.Length > MaxWaveformLength)
            {
                throw new SdkFormatException("Received media waveform too long.");
            }
            foreach (byte v in waveform)
            {
                if (v > MaxWaveformValue)
                {
                    throw new SdkFormatException("Received media waveform value out of range.");
                }
            }
        }
    }

    internal static void ValidateReceive(
        MediaKind kind,
        byte[]? mediaKey,
        byte[]? fileSha256,
        byte[]? fileEncSha256,
        string? directPath,
        long fileLength,
        string? mimetype,
        string? fileName = null,
        int? seconds = null,
        int? width = null,
        int? height = null,
        byte[]? waveform = null)
        => Validate(kind, mediaKey, fileSha256, fileEncSha256, directPath, fileLength, mimetype, fileName, seconds, width, height, waveform);

    internal static void ValidateReceiveMetadata(
        MediaKind kind,
        byte[]? mediaKey,
        byte[]? fileSha256,
        byte[]? fileEncSha256,
        string? directPath,
        long fileLength,
        string? mimetype,
        string? fileName = null,
        int? seconds = null,
        int? width = null,
        int? height = null,
        byte[]? waveform = null)
        => Validate(kind, mediaKey, fileSha256, fileEncSha256, directPath, fileLength, mimetype, fileName, seconds, width, height, waveform);

    internal static void ValidateMimetype(MediaKind kind, string? mimetype)
    {
        if (string.IsNullOrEmpty(mimetype))
        {
            throw new SdkFormatException("Received media mimetype must not be empty.");
        }
        // Executable-ish reject list (Pitfall 4) — checked everywhere.
        string lower = mimetype.ToLowerInvariant();
        string[] executableLabels = { "application/x-msdownload", "application/x-executable", "application/x-dosexec", "application/octet-stream", "text/html", "text/javascript" };
        foreach (string label in executableLabels)
        {
            if (string.Equals(lower, label, StringComparison.Ordinal))
            {
                throw new SdkFormatException("Received media mimetype rejected.");
            }
        }

        bool allowed = kind switch
        {
            MediaKind.Image => MediaMimeAllowList.Images.Contains(mimetype, StringComparer.OrdinalIgnoreCase),
            MediaKind.Document => MediaMimeAllowList.Documents.Contains(mimetype, StringComparer.OrdinalIgnoreCase),
            MediaKind.VoiceNote => mimetype.StartsWith(MediaMimeAllowList.VoiceNotePrefix, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        if (!allowed)
        {
            throw new SdkFormatException("Received media mimetype not on allow list.");
        }
    }

    internal static string SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return string.Empty;
        }
        // Reject traversal sequences.
        if (fileName.Contains("..", StringComparison.Ordinal) || fileName.Contains('/', StringComparison.Ordinal) || fileName.Contains('\\', StringComparison.Ordinal))
        {
            throw new SdkFormatException("Received media filename contains traversal or separators.");
        }
        string bare = Path.GetFileName(fileName);
        if (string.IsNullOrEmpty(bare) || bare != fileName)
        {
            throw new SdkFormatException("Received media filename must be a bare name.");
        }
        return bare;
    }

    internal static bool IsValidMimetype(MediaKind kind, string? mimetype)
    {
        try { ValidateMimetype(kind, mimetype); return true; }
        catch { return false; }
    }
}
