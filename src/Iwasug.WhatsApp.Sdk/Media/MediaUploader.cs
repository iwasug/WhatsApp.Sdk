using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Safety;

namespace Iwasug.WhatsApp.Sdk.Media;

internal sealed class MediaUploader
{
    public static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly Func<bool, CancellationToken, Task<MediaConn.Snapshot>> _getConnAsync;
    private readonly TimeProvider _clock;
    private readonly SafetyOptions _options;

    public MediaUploader(HttpClient httpClient, MediaConn conn, TimeProvider? timeProvider = null, SafetyOptions? options = null)
        : this(httpClient, (force, ct) => conn.GetAsync(force, ct), timeProvider, options)
    {
    }

    public MediaUploader(HttpClient httpClient, Func<MediaConn> connProvider, TimeProvider? timeProvider = null, SafetyOptions? options = null)
        : this(httpClient, (force, ct) => connProvider().GetAsync(force, ct), timeProvider, options)
    {
    }

    public MediaUploader(HttpClient httpClient, Func<MediaConn.Snapshot?> connProvider, TimeProvider? timeProvider = null, SafetyOptions? options = null)
        : this(httpClient, (force, ct) =>
        {
            var snapshot = connProvider();
            if (snapshot is null) throw new SdkConnectionException("The media-conn snapshot is unavailable: the upload was not performed.");
            return Task.FromResult(snapshot);
        }, timeProvider, options)
    {
    }

    public MediaUploader(HttpClient httpClient, Func<bool, CancellationToken, Task<MediaConn.Snapshot>> connProvider, TimeProvider? timeProvider = null, SafetyOptions? options = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _getConnAsync = connProvider ?? throw new ArgumentNullException(nameof(connProvider));
        _clock = timeProvider ?? TimeProvider.System;
        _options = SafetyOptions.Validate(options);
    }

    public async Task<MediaUploadResult> UploadAsync(MediaKind kind, Stream content, CancellationToken cancellationToken = default)
        => await UploadAsync(kind, content, null, cancellationToken).ConfigureAwait(false);

    public async Task<MediaUploadResult> UploadAsync(MediaKind kind, Stream content, IProgress<long>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();

        long cap = kind switch
        {
            MediaKind.Image => _options.MaxImageBytes,
            MediaKind.Document => _options.MaxDocumentBytes,
            MediaKind.VoiceNote => _options.MaxAudioBytes,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        byte[] data = await ReadAllAsync(content, cap, cancellationToken).ConfigureAwait(false);
        if (data.Length == 0)
        {
            throw new SdkFormatException("Media content must not be empty.");
        }

        byte[] mediaKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(MediaCrypto.MediaKeyLength);
        try
        {
            var info = MediaKindInfo.InfoString(kind);
            var (blob, fileSha256, fileEncSha256) = MediaCrypto.Encrypt(data, mediaKey, info);
            try
            {
                string token = Base64UrlNoPad(fileEncSha256);
                string mmsType = MediaKindInfo.MmsType(kind);

                MediaConn.Snapshot snapshot = await _getConnAsync(false, cancellationToken).ConfigureAwait(false);
                string auth = snapshot.Auth;
                string[] hosts = snapshot.Hosts.ToArray();

                Exception? last = null;
                for (int hostIndex = 0; hostIndex < hosts.Length; hostIndex++)
                {
                    if (hostIndex > 0)
                    {
                        try
                        {
                            snapshot = await _getConnAsync(true, cancellationToken).ConfigureAwait(false);
                            auth = snapshot.Auth;
                            if (snapshot.Hosts.Count > 0) hosts = snapshot.Hosts.ToArray();
                        }
                        catch (Exception ex)
                        {
                            last = ex;
                            continue;
                        }
                    }

                    string host = (hostIndex < hosts.Length) ? hosts[Math.Min(hostIndex, hosts.Length - 1)] : hosts[0];
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            timeoutCts.CancelAfter(UploadTimeout);
                            string url = $"https://{host}/mms/{mmsType}/{token}?auth={Uri.EscapeDataString(auth)}&token={token}";

                            using var httpContent = new ByteArrayContent(blob);
                            httpContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = httpContent };
                            request.Headers.TryAddWithoutValidation("Origin", "https://web.whatsapp.com");
                            request.Headers.Referrer = new Uri("https://web.whatsapp.com/");

                            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
                            response.EnsureSuccessStatusCode();
                            string json = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

                            using var doc = JsonDocument.Parse(json);
                            string resultUrl = doc.RootElement.TryGetProperty("url", out var urlProp) ? (urlProp.GetString() ?? string.Empty) : string.Empty;
                            string directPath = doc.RootElement.TryGetProperty("direct_path", out var dpProp) ? (dpProp.GetString() ?? string.Empty)
                                : (doc.RootElement.TryGetProperty("directPath", out var dp2) ? (dp2.GetString() ?? string.Empty) : string.Empty);

                            progress?.Report(data.Length);

                            return new MediaUploadResult(
                                resultUrl,
                                directPath,
                                (byte[])mediaKey.Clone(),
                                (byte[])fileSha256.Clone(),
                                (byte[])fileEncSha256.Clone(),
                                data.Length,
                                (long)_clock.GetUtcNow().ToUnixTimeSeconds());
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            last = new TimeoutException("Media upload timed out.");
                        }
                        catch (HttpRequestException ex)
                        {
                            last = new SdkTransportException("Media upload failed.", ex);
                        }
                        catch (TaskCanceledException ex)
                        {
                            last = new SdkTransportException("Media upload failed.", ex);
                        }
                    }
                }

                throw last ?? new SdkTransportException("Media upload failed.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(blob);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(mediaKey);
        }
    }

    private static async Task<byte[]> ReadAllAsync(Stream content, long cap, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        byte[] buffer = new byte[8192];
        int read;
        long total = 0;
        while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > cap)
            {
                throw new SafetyRuleViolationException(SafetyRule.RateLimit, "Safety rule violated.");
            }
            await ms.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return ms.ToArray();
    }

    private static string Base64UrlNoPad(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
