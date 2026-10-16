using System.Text;
using AwesomeAssertions;
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
using Google.Protobuf;
using Xunit;
public class MediaTracerTests
{
    [Fact]
    public void CryptoRoundTrip()
    {
        byte[] mediaKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        byte[] plaintext = [1, 2, 3, 4, 5];
        var (blob, fsha, fenc) = MediaCrypto.Encrypt(plaintext, mediaKey, MediaCrypto.ImageKeysInfo);
        var decoded = MediaCrypto.VerifyAndDecrypt(blob, mediaKey, MediaCrypto.ImageKeysInfo, fsha, fenc);
        Assert.Equal(plaintext, decoded);
    }

    [Fact]
    public async Task FullSendReceiveRoundTrip()
    {
        using var testCts = new CancellationTokenSource(MessagingLoopbackTests.WaitTimeout);
        CancellationToken token = testCts.Token;
        AccountId account = MessagingLoopbackTests.NewAccount("tracer");
        var own = MessagingLoopbackTests.CreateOwnKeys(790);
        var peers = new ConcurrentQueue<LoopbackFrameTransport>();

        await using var factory = new InMemoryStoreFactory();
        ISignalSessionStore sessions = await factory.CreateAsync(account, token);
        IIdentityStore identities = await ((IIdentityStoreFactory)factory).CreateAsync(account, token);
        IPreKeyStore preKeys = await ((IPreKeyStoreFactory)factory).CreateAsync(account, token);
        IRecentMessageCacheStore cache = await ((IRecentMessageCacheStoreFactory)factory).CreateAsync(account, token);
        ISafetyStateStore safety = await ((ISafetyStateStoreFactory)factory).CreateAsync(account, token);
        ICredentialStore credentials = await ((ICredentialStoreFactory)factory).CreateAsync(account, token);
        var payloads = await MessagingLoopbackTests.CreateSeededPayloadsAsync(account, credentials, own, token);

        await MessagingLoopbackTests.SeedOwnPrekeysAsync(preKeys, count: 6, firstId: 200, token);

        string deviceS = MessagingLoopbackTests.DeviceJid(MessagingLoopbackTests.PeerUser, 52);
        string deviceR = MessagingLoopbackTests.DeviceJid(MessagingLoopbackTests.PeerUser, 53);
        var peerS = MessagingLoopbackTests.CreatePeerDevice(deviceS, 22, 1101, 1234);
        var peerR = MessagingLoopbackTests.CreatePeerDevice(deviceR, 23, 1102, 1235);
        var bobS = await MessagingLoopbackTests.SeedSessionPairAsync(sessions, own, peerS, token);
        var peerAliceR = await MessagingLoopbackTests.SeedReceivePairAsync(sessions, own, peerR, token);

        var fixedStores = new MessagingLoopbackTests.FixedStores
        {
            Sessions = sessions, Identity = identities, PreKeys = preKeys,
            RecentMessages = cache, Safety = safety, Credentials = credentials,
        };
        var registryOptions = new StoreRegistryOptions
        {
            SignalSessionFactory = fixedStores,
            IdentityFactory = fixedStores,
            PreKeyFactory = fixedStores,
            RecentMessageCacheFactory = fixedStores,
            SafetyStateFactory = fixedStores,
            CredentialFactory = fixedStores,
        };
        await using var connection = new WaConnection(account, MessagingLoopbackTests.CreateClient(peers), payloads, MessagingLoopbackTests.TracerOptions());
        connection.StoreRegistry = new StoreRegistry(registryOptions);

        // Stub uploader (no media-conn IQ from peer needed).
        var uploadHandler = new DelegateTestHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Content = new StringContent("{\"url\":\"https://cdn.example/x\",\"direct_path\":\"/v1/dp\"}", Encoding.UTF8, "application/json");
            return resp;
        });
        using var uploadHttp = new HttpClient(uploadHandler);
        var snapshot = new MediaConn.Snapshot("auth-token", new[] { "mmg.example.net" }, DateTimeOffset.UtcNow, TimeSpan.FromHours(1), TimeSpan.FromHours(1), MaxBuckets: 1);
        Func<bool, CancellationToken, Task<MediaConn.Snapshot>> connProvider = (_, __) => Task.FromResult(snapshot);
        var uploader = new MediaUploader(uploadHttp, connProvider);

        byte[]? downloadBlob = null;
        var downloadHandler = new DelegateTestHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK);
            r.Content = new ByteArrayContent(downloadBlob!);
            r.Content.Headers.ContentLength = (downloadBlob?.Length ?? 0);
            return r;
        });
        using var downloadHttp = new HttpClient(downloadHandler);
        var downloader = new MediaDownloader(downloadHttp);

        MessagingLoopbackTests.ScriptedPeer? peer = null;
        var peerTask = Task.Run(async () =>
        {
            peer = await MessagingLoopbackTests.CompletePeerHandshakeAsync(peers, token);
            peer!.BobRatchets[deviceS] = bobS;
            peer!.UsyncDevices = new[] { deviceS };
            await peer!.RunAsync(testCts.Token);
        }, token);

        await connection.ConnectAsync(token);
        Messenger messenger = connection.Messaging;
        messenger.MediaUploader = uploader;
        messenger.MediaDownloader = downloader;

        var mediaEvents = new ConcurrentQueue<MediaReceivedEventArgs>();
        messenger.MediaReceived += (_, e) => mediaEvents.Enqueue(e);

        byte[] originalBytes = new byte[] { 1, 2, 3, 4, 5 };

        // Send image via public API.
        using var content = new MemoryStream(originalBytes);
        SendOutcome outcome = await messenger.SendImageAsync(
            Jid.Parse(MessagingLoopbackTests.PeerUser + "@" + MessagingLoopbackTests.PeerServer),
            content, "image/jpeg", cancellationToken: token);
        outcome.Status.Should().Be(SendOutcomeStatus.Sent);

        if (peer == null) { await Task.Delay(100, token); }
        await MessagingLoopbackTests.WaitForAsync(peer!.Log,
            e => e.StartsWith("message:", StringComparison.Ordinal), "peer received the message node");
        peer!.MessageNodes.TryDequeue(out BinaryNode? outboundNode).Should().BeTrue();

        // Decrypt on peer to capture metadata.
        BinaryNode encChild = FirstEnc(outboundNode!);
        string encType = encChild.Attrs.TryGetValue("type", out string? t) ? t : "msg";
        SignalDecryptOutcome dec = bobS.Decrypt(encChild.Data!, encType == "pkmsg", token);
        (dec is SignalDecryptOutcome.Decrypted).Should().BeTrue();
        var decrypted = (SignalDecryptOutcome.Decrypted)dec;
        var outboundMsg = Message.Parser.ParseFrom(decrypted.Plaintext);
        outboundMsg.ImageMessage.Should().NotBeNull();
        outboundMsg.ImageMessage!.Mimetype.Should().Be("image/jpeg");
        byte[] mediaKey = outboundMsg.ImageMessage.MediaKey.ToByteArray();
        byte[] fileEncSha = outboundMsg.ImageMessage.FileEncSha256.ToByteArray();
        long fileLength = outboundMsg.ImageMessage.FileLength;

        // Build encrypted blob matching upload.
        var info = MediaCrypto.ImageKeysInfo;
        var (blob, fsha, fenc) = MediaCrypto.Encrypt(originalBytes, mediaKey, info);
        fenc.Should().Equal(fileEncSha);
        fileLength.Should().Be(originalBytes.Length);
        downloadBlob = blob;

        // Peer creates inbound image node reusing the same plaintext and sends it.
        byte[] inboundPlain = outboundMsg.ToByteArray();
        SignalEncryptOutcome eo = peerAliceR.Encrypt(inboundPlain, token);
        byte[] wire = eo is SignalEncryptOutcome.PreKeyMessageSent pk ? pk.WireBytes : ((SignalEncryptOutcome.MessageSent)eo).WireBytes;
        string inboundEncType = eo is SignalEncryptOutcome.PreKeyMessageSent ? "pkmsg" : "msg";
        await peer.SendStanzaAsync(MessagingLoopbackTests.BuildInboundMessageNode("inbound-1", deviceR, wire, inboundEncType));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (mediaEvents.IsEmpty && !timeout.IsCancellationRequested)
        {
            await Task.Delay(10, token);
        }
        mediaEvents.Should().HaveCount(1);
        var ev = mediaEvents.Single();
        ev.Kind.Should().Be(MediaKind.Image);
        using var ms = new MemoryStream();
        await ev.Content!.CopyToAsync(ms, token);
        ms.ToArray().Should().Equal(originalBytes);

        await connection.DisposeAsync();
        await peerTask;
    }

    private static BinaryNode FirstEnc(BinaryNode node)
    {
        // to-wrapped per device; find first enc.
        foreach (BinaryNode c in node.Children ?? (IReadOnlyList<BinaryNode>)[])
        {
            if (c.Tag == "enc") return c;
            if (c.Children is not null)
            {
                foreach (BinaryNode cc in c.Children)
                {
                    if (cc.Tag == "enc") return cc;
                }
            }
        }
        throw new InvalidOperationException("no enc child");
    }
}

file sealed class DelegateTestHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _f;
    public DelegateTestHandler(Func<HttpRequestMessage, HttpResponseMessage> f) => _f = f;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(_f(request));
}
