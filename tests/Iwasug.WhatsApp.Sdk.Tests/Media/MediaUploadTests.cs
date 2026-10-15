using System.Net;
using System.Net.Http;
using System.Text;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Media;
using Microsoft.Extensions.Time.Testing;
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

    [Fact]
    public async Task UploadRotatesHostOnTransportFailureAndReuploadsIdenticalBytes()
    {
        var bodies = new List<byte[]>();
        var hosts = new List<string>();
        var handler = new DelegatingHandler2(async (req, ct) =>
        {
            hosts.Add(req.RequestUri!.Host);
            bodies.Add(await req.Content!.ReadAsByteArrayAsync(ct));
            if (hosts.Count <= 2)
            {
                throw new HttpRequestException("boom");
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new StringContent("{\"url\":\"https://cdn.example/x\",\"direct_path\":\"/v1/dp\"}", Encoding.UTF8, "application/json");
            return response;
        });
        using var http = new HttpClient(handler);

        var snapshot = new MediaConn.Snapshot(
            "auth-token",
            new[] { "host-a.example.net", "host-b.example.net" },
            DateTimeOffset.UtcNow,
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(1),
            MaxBuckets: 1);

        Func<bool, CancellationToken, Task<MediaConn.Snapshot>> connProvider = (_, __) => Task.FromResult(snapshot);
        var uploader = new MediaUploader(http, connProvider);

        using var content = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        var result = await uploader.UploadAsync(MediaKind.Image, content, cancellationToken: TestContext.Current.CancellationToken);

        result.Url.Should().Be("https://cdn.example/x");
        hosts.Should().HaveCount(3);
        hosts[0].Should().Be("host-a.example.net");
        hosts[2].Should().Be("host-b.example.net");
        bodies[0].Should().Equal(bodies[2]);
    }

    [Fact]
    public async Task TtlExpiryForceRefreshesBeforeHttp()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        int queryCount = 0;
        QueryRouter? router = null;
        router = new QueryRouter((id, payload, ct) =>
        {
            queryCount++;
            var host = BinaryNode.FromChildren("host", Array.Empty<BinaryNode>(), new Dictionary<string, string>(StringComparer.Ordinal) { ["hostname"] = "mmg.example.net" });
            var mediaConn = BinaryNode.FromChildren("media_conn", new[] { host }, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["auth"] = "auth-token",
                ["ttl"] = "1",
                ["auth_ttl"] = "1",
            });
            var iq = BinaryNode.FromChildren("iq", new[] { mediaConn }, new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = "result" });
            router!.TryDeliver(id, BinaryNodeCodec.Encode(iq));
            return ValueTask.CompletedTask;
        }, new ConnectionOptions { QueryTimeoutMs = 5_000 }, clock);

        var mediaConn = new MediaConn(() => router, clock);

        var handler = new CaptureHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new StringContent("{\"url\":\"https://cdn.example/x\",\"direct_path\":\"/v1/dp\"}", Encoding.UTF8, "application/json");
            return response;
        });
        using var http = new HttpClient(handler);
        var uploader = new MediaUploader(http, mediaConn, clock);

        using var content1 = new MemoryStream(new byte[] { 1, 2, 3 });
        await uploader.UploadAsync(MediaKind.Image, content1, cancellationToken: TestContext.Current.CancellationToken);

        int afterFirst = queryCount;
        clock.Advance(TimeSpan.FromSeconds(2));

        using var content2 = new MemoryStream(new byte[] { 1, 2, 3 });
        await uploader.UploadAsync(MediaKind.Image, content2, cancellationToken: TestContext.Current.CancellationToken);

        queryCount.Should().BeGreaterThan(afterFirst);
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

    private sealed class DelegatingHandler2 : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _f;
        public DelegatingHandler2(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) => _f = f;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _f(request, cancellationToken);
    }
}
