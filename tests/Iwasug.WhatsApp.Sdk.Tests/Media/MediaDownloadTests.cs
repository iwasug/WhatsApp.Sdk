using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Media;
using System.Net;
using System.Net.Http;
using Xunit;

public class MediaDownloadTests
{
    [Fact]
    public void ExpiredStatusThrows()
    {
        var handler = new DelegateTestHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var http = new HttpClient(handler);
        var d = new MediaDownloader(http);
        byte[] key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        byte[] plain = [1, 2, 3];
        var (blob, fsha, fenc) = MediaCrypto.Encrypt(plain, key, MediaCrypto.ImageKeysInfo);
        Assert.Throws<MediaDownloadExpiredException>(() =>
            d.DownloadAsync(MediaKind.Image, "/v", key, fsha, fenc, plain.Length, cancellationToken: CancellationToken.None).GetAwaiter().GetResult());
    }

    [Fact]
    public void SanitizeRejectsTraversal()
    {
        Assert.Throws<SdkFormatException>(() => MediaMetadata.SanitizeFileName("../x"));
    }
}

file sealed class DelegateTestHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _f;
    public DelegateTestHandler(Func<HttpRequestMessage, HttpResponseMessage> f) => _f = f;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(_f(request));
}