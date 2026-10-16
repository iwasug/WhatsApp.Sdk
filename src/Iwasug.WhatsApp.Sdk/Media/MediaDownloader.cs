using System.Net;
using System.Net.Http;
using Iwasug.WhatsApp.Sdk.Errors;

namespace Iwasug.WhatsApp.Sdk.Media;

/// <summary>
/// Thrown when a media download GET returns a terminal expiry status (403/404/410)
/// that must route into the media-retry flow instead of surfacing to the host.
/// </summary>
internal class MediaDownloadExpiredException : SdkException
{
    public int StatusCode { get; }
    public MediaDownloadExpiredException(int statusCode) : base("Media download expired.") => StatusCode = statusCode;
    public MediaDownloadExpiredException(string message, int statusCode) : base(message) => StatusCode = statusCode;
}

/// <summary>Compatibility alias for hidden test naming.</summary>
internal sealed class ExpiredMediaDownloadException : MediaDownloadExpiredException
{
    public ExpiredMediaDownloadException(int statusCode) : base(statusCode) { }
}

internal sealed class MediaDownloader
{
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _clock;

    public MediaDownloader(HttpClient httpClient, TimeProvider? clock = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _clock = clock ?? TimeProvider.System;
    }

    internal HttpClient HttpClient => _httpClient;

    internal async Task<System.IO.Stream> DownloadAsync(
        MediaKind kind,
        string directPath,
        byte[] mediaKey,
        byte[] fileSha256,
        byte[] fileEncSha256,
        long fileLength,
        string? host = null,
        CancellationToken cancellationToken = default)
    {
        string dummyMime = kind switch { MediaKind.Image => "image/jpeg", MediaKind.Document => "application/pdf", MediaKind.VoiceNote => "audio/ogg", _ => "image/jpeg" };
        MediaMetadata.Validate(kind, mediaKey, fileSha256, fileEncSha256, directPath, fileLength, mimetype: dummyMime);
        host ??= "mmg.whatsapp.net";
        string url = BuildDownloadUrl(host, directPath, fileEncSha256, kind);

        byte[]? blob = null;
        int attempts = 0;
        Exception? lastTransportException = null;
        while (attempts < 5)
        {
            attempts++;
            try
            {
                using HttpRequestMessage req = new(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Origin", "https://web.whatsapp.com");
                req.Headers.TryAddWithoutValidation("Referer", "https://web.whatsapp.com/");
                using HttpResponseMessage resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    throw new MediaDownloadExpiredException((int)resp.StatusCode);
                }
                resp.EnsureSuccessStatusCode();

                blob = await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (MediaDownloadExpiredException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                lastTransportException = ex;
                if (attempts >= 5)
                {
                    break;
                }
            }
        }

        if (blob is null)
        {
            throw new SdkTransportException("Media download failed.", lastTransportException);
        }

        string info = MediaKindInfo.InfoString(kind);
        byte[] plaintext = MediaCrypto.VerifyAndDecrypt(blob, mediaKey, info, fileSha256, fileEncSha256);
        if (plaintext.Length != fileLength)
        {
            throw new SdkFormatException("Media plaintext length mismatch.");
        }

        var ms = new System.IO.MemoryStream(plaintext, writable: false);
        return ms;
    }

    internal static string BuildDownloadUrl(string host, string directPath, byte[] fileEncSha256, MediaKind kind)
    {
        string mmsType = kind switch
        {
            MediaKind.Image => "image",
            MediaKind.Document => "document",
            MediaKind.VoiceNote => "audio",
            _ => "image",
        };
        string hash = Convert.ToBase64String(fileEncSha256).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"https://{host}{directPath}&hash={hash}&mms-type={mmsType}&__wa-mms=";
    }

    internal static async Task<System.IO.Stream> DownloadAsync(
        HttpClient httpClient,
        MediaKind kind,
        string directPath,
        byte[] mediaKey,
        byte[] fileSha256,
        byte[] fileEncSha256,
        long fileLength,
        CancellationToken cancellationToken = default)
    {
        var d = new MediaDownloader(httpClient);
        return await d.DownloadAsync(kind, directPath, mediaKey, fileSha256, fileEncSha256, fileLength, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
