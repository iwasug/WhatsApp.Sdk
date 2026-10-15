using System.Net;
using System.Net.Http;
using System.Text;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Media;
using Xunit;

public class MediaUploadTests
{
    [Fact]
    public async Task UploadMapsResponseAndCapturesRequest()
    {
        HttpRequestMessage? captured = null;
        long capturedContentLength = 0;
        var handler = new CaptureHandler(req =>
        {
            captured = req;
            capturedContentLength = req.Content?.Headers.ContentLength ?? 0;
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new StringContent("{\"url\":\"https://cdn.example/x\",\"direct_path\":\"/v1/dp\"}", Encoding.UTF8, "application/json");
            return response;
        });
        using var http = new HttpClient(handler);

        var snapshot = new MediaConn.Snapshot(
            "auth-token",
            new[] { "mmg.example.net" },
            DateTimeOffset.UtcNow,
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(1),
            MaxBuckets: 1);

        Func<bool, CancellationToken, Task<MediaConn.Snapshot>> connProvider = (_, __) => Task.FromResult(snapshot);
        var uploader = new MediaUploader(http, connProvider);

        using var content = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        var result = await uploader.UploadAsync(MediaKind.Image, content, cancellationToken: TestContext.Current.CancellationToken);

        result.Url.Should().Be("https://cdn.example/x");
        result.DirectPath.Should().Be("/v1/dp");
        result.FileLength.Should().Be(4);
        result.MediaKey.Should().HaveCount(MediaCrypto.MediaKeyLength);

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.Should().StartWith("/mms/image/");
        captured.RequestUri.Query.Should().Contain("auth=auth-token");
        captured.RequestUri.Query.Should().Contain("token=");
        captured.Headers.GetValues("Origin").Should().ContainSingle("https://web.whatsapp.com");
        captured.Content!.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
        capturedContentLength.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task OversizeContentThrowsBeforeUpload()
    {
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler);

        var snapshot = new MediaConn.Snapshot(
            "auth-token",
            new[] { "mmg.example.net" },
            DateTimeOffset.UtcNow,
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(1),
            MaxBuckets: 1);

        Func<bool, CancellationToken, Task<MediaConn.Snapshot>> connProvider = (_, __) => Task.FromResult(snapshot);
        var uploader = new MediaUploader(http, connProvider, options: new Iwasug.WhatsApp.Sdk.Safety.SafetyOptions { MaxImageBytes = 2 });

        using var content = new MemoryStream(new byte[] { 1, 2, 3 });
        Func<Task> act = async () => await uploader.UploadAsync(MediaKind.Image, content, cancellationToken: TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<Iwasug.WhatsApp.Sdk.Safety.SafetyRuleViolationException>();

        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task EmptyContentThrows()
    {
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler);

        var snapshot = new MediaConn.Snapshot(
            "auth-token",
            new[] { "mmg.example.net" },
            DateTimeOffset.UtcNow,
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(1),
            MaxBuckets: 1);

        Func<bool, CancellationToken, Task<MediaConn.Snapshot>> connProvider = (_, __) => Task.FromResult(snapshot);
        var uploader = new MediaUploader(http, connProvider);

        using var content = new MemoryStream(Array.Empty<byte>());
        Func<Task> act = async () => await uploader.UploadAsync(MediaKind.Image, content, cancellationToken: TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<Iwasug.WhatsApp.Sdk.Errors.SdkFormatException>();
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _f;
        public int CallCount { get; private set; }
        public CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> f) => _f = f;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_f(request));
        }
    }
}
