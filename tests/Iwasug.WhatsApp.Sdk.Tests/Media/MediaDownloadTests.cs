using Iwasug.WhatsApp.Sdk;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Media;
using Iwasug.WhatsApp.Sdk.Messaging;
using Iwasug.WhatsApp.Sdk.Signal;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.InMemory;
using Iwasug.WhatsApp.Sdk.Tests.Messaging;
using Iwasug.WhatsApp.Sdk.Tests.Transport;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using AwesomeAssertions;
using Xunit;
using MessagingLoopbackTestsAlias = global::Iwasug.WhatsApp.Sdk.Tests.Messaging.MessagingLoopbackTests;

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

    internal static async Task RunReceiveAsync(
        MediaKind kind,
        string mimetype,
        string? fileName,
        int? seconds,
        bool ptt,
        CancellationToken token)    {
        AccountId account = MessagingLoopbackTestsAlias.NewAccount("recv-" + kind);
        var own = MessagingLoopbackTestsAlias.CreateOwnKeys(789);
        var peers = new ConcurrentQueue<LoopbackFrameTransport>();

        await using var factory = new InMemoryStoreFactory();
        ISignalSessionStore sessions = await factory.CreateAsync(account, token);
        IIdentityStore identities = await ((IIdentityStoreFactory)factory).CreateAsync(account, token);
        IPreKeyStore preKeys = await ((IPreKeyStoreFactory)factory).CreateAsync(account, token);
        IRecentMessageCacheStore cache = await ((IRecentMessageCacheStoreFactory)factory).CreateAsync(account, token);
        ISafetyStateStore safety = await ((ISafetyStateStoreFactory)factory).CreateAsync(account, token);
        ICredentialStore credentials = await ((ICredentialStoreFactory)factory).CreateAsync(account, token);
        var payloads = await MessagingLoopbackTestsAlias.CreateSeededPayloadsAsync(account, credentials, own, token);

        // Upload to obtain real artifacts.
        var uploadHandler = new DelegateTestHandler(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Content = new StringContent("{\"url\":\"https://cdn.example/x\",\"direct_path\":\"/v1/dp\"}", Encoding.UTF8, "application/json");
            return resp;
        });
        using var uploadHttp = new HttpClient(uploadHandler);
        var snapshot = new MediaConn.Snapshot("auth-token", new[] { "mmg.example.net" }, DateTimeOffset.UtcNow, TimeSpan.FromHours(1), TimeSpan.FromHours(1), MaxBuckets: 1);
        Func<bool, CancellationToken, Task<MediaConn.Snapshot>> connProvider = (_, __) => Task.FromResult(snapshot);
        var uploader = new MediaUploader(uploadHttp, connProvider);
        using var content = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });        var upload = await uploader.UploadAsync(kind == MediaKind.VoiceNote ? MediaKind.VoiceNote : kind, content, cancellationToken: token);

        // Compute encrypted blob matching upload metadata.
        var info = kind == MediaKind.Image ? MediaCrypto.ImageKeysInfo
                 : kind == MediaKind.Document ? MediaCrypto.DocumentKeysInfo
                 : MediaCrypto.AudioKeysInfo;
        // Use original content bytes for encryption match.
        byte[] plaintextBytes = new byte[] { 1, 2, 3, 4, 5 };
        var (blob, fsha, fenc) = MediaCrypto.Encrypt(plaintextBytes, upload.MediaKey, info);

        // Download stub.
        var downloadHandler = new DelegateTestHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK);
            r.Content = new ByteArrayContent(blob);
            r.Content.Headers.ContentLength = blob.Length; // real-server invariant: Content-Length is the body length
            return r;
        });        var downloadHttp = new HttpClient(downloadHandler);
        var downloader = new MediaDownloader(downloadHttp);

        var device = MessagingLoopbackTestsAlias.CreatePeerDevice(MessagingLoopbackTestsAlias.DeviceJid(MessagingLoopbackTestsAlias.PeerUser, 52), 22, 1101, 1234);        var peerAlice = await MessagingLoopbackTestsAlias.SeedReceivePairAsync(sessions, own, device, token);

        var fixedStores = new MessagingLoopbackTestsAlias.FixedStores        {
            Sessions = sessions, Identity = identities, PreKeys = preKeys,
            RecentMessages = cache, Safety = safety, Credentials = credentials,
        };
        var registryOptions = new StoreRegistryOptions        {
            SignalSessionFactory = fixedStores,
            IdentityFactory = fixedStores,
            PreKeyFactory = fixedStores,
            RecentMessageCacheFactory = fixedStores,
            SafetyStateFactory = fixedStores,
            CredentialFactory = fixedStores,
        };
        await using var connection = new WaConnection(account, MessagingLoopbackTestsAlias.CreateClient(peers), payloads, MessagingLoopbackTestsAlias.TracerOptions());
        connection.StoreRegistry = new StoreRegistry(registryOptions);

        MessagingLoopbackTestsAlias.ScriptedPeer? peer = null;        var peerTask = Task.Run(async () =>
        {
            peer = await MessagingLoopbackTestsAlias.CompletePeerHandshakeAsync(peers, token);
            await peer!.RunAsync(token);
        }, token);

        await connection.ConnectAsync(token);
        Messenger messenger = connection.Messaging;
        messenger.Downloader = downloader;
        var events = new ConcurrentQueue<MediaReceivedEventArgs>();
        messenger.MediaReceived += (_, e) => events.Enqueue(e);

        // Build inbound media message plaintext from upload (subtype info).
        byte[] messagePlain;
        if (kind == MediaKind.Image)
        {
            messagePlain = MessageBuilder.BuildImageMessage(upload, "cap", 10, 20);
        }
        else if (kind == MediaKind.Document)
        {
            messagePlain = MessageBuilder.BuildDocumentMessage(upload, mimetype, "title", fileName ?? "doc.pdf", "cap");
        }
        else
        {
            messagePlain = MessageBuilder.BuildVoiceNoteMessage(upload, mimetype, seconds ?? 7, null);
        }

        try
        {
            using var manual = await downloader.DownloadAsync(kind, upload.DirectPath, upload.MediaKey, upload.FileSha256, upload.FileEncSha256, upload.FileLength, cancellationToken: token);        }
        catch (Exception e)
        {
            throw new Exception("manual download failed: " + e);
        }

        SignalEncryptOutcome eo = peerAlice.Encrypt(messagePlain, token);        byte[] wire = eo is SignalEncryptOutcome.PreKeyMessageSent pk ? pk.WireBytes : ((SignalEncryptOutcome.MessageSent)eo).WireBytes;
        string encType = eo is SignalEncryptOutcome.PreKeyMessageSent ? "pkmsg" : "msg";

        string msgId = "media-" + kind;
        string from = MessagingLoopbackTestsAlias.DeviceJid(MessagingLoopbackTestsAlias.PeerUser, 52);
        await peer!.SendStanzaAsync(MessagingLoopbackTestsAlias.BuildInboundMessageNode(msgId, from, wire, encType));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (events.IsEmpty && !timeout.IsCancellationRequested)
        {
            await Task.Delay(10);
        }
        if (events.IsEmpty)
        {
            throw new Exception("No media event. PeerLog=" + string.Join(";", peer!.Log) + " eventsCount=" + events.Count);
        }
        events.Should().HaveCount(1);        var ev = events.Single();
        ev.Kind.Should().Be(kind);
        ev.Mimetype.Should().Be(mimetype);
        ev.FileLength.Should().Be(upload.FileLength);
        if (kind == MediaKind.Document) ev.FileName.Should().Be(MediaMetadata.SanitizeFileName(fileName!));        if (kind == MediaKind.VoiceNote)
        {
            ev.Ptt.Should().Be(ptt);
            ev.Seconds.Should().Be(seconds);
        }
        if (kind == MediaKind.Image) ev.Caption.Should().Be("cap");

        // Content bytes.
        using var ms = new MemoryStream();
        await ev.Content!.CopyToAsync(ms, token);
        ms.ToArray().Should().Equal(plaintextBytes);

        await connection.DisposeAsync();
        await peerTask;
    }

    [Fact]
    public async Task InboundImageRaisesMediaReceivedWithBytes()    {
        using var cts = new CancellationTokenSource(MessagingLoopbackTests.WaitTimeout);
        await RunReceiveAsync(MediaKind.Image, "image/jpeg", null, null, false, cts.Token);
    }

    [Fact]
    public async Task InboundDocumentCarriesMetadata()
    {
        using var cts = new CancellationTokenSource(MessagingLoopbackTests.WaitTimeout);
        await RunReceiveAsync(MediaKind.Document, "application/pdf", "my doc.pdf", null, false, cts.Token);
    }

    [Fact]
    public async Task InboundVoiceNoteCarriesPttAndSeconds()
    {
        using var cts = new CancellationTokenSource(MessagingLoopbackTests.WaitTimeout);
        await RunReceiveAsync(MediaKind.VoiceNote, "audio/ogg; codecs=opus", null, 7, true, cts.Token);
    }
}

file sealed class DelegateTestHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _f;
    public DelegateTestHandler(Func<HttpRequestMessage, HttpResponseMessage> f) => _f = f;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(_f(request));
}
