using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Messaging;
using Iwasug.WhatsApp.Sdk.Noise;
using Iwasug.WhatsApp.Sdk.Pairing;
using Iwasug.WhatsApp.Sdk.Signal;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.InMemory;
using Iwasug.WhatsApp.Sdk.Tests.Transport;
using Iwasug.WhatsApp.Sdk.Transport;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Messaging;

/// <summary>
/// The 04-03 phase tracer (MSG-01): one scripted 1:1 send encrypts per device (own-device
/// exclusion included), is acked by the fake server, and decrypts through the peer's seeded
/// sessions to the exact plaintext; one scripted inbound message decrypts, persists BEFORE
/// its ack (D-06 store-instrumented ordering), raises exactly one typed event, and lands in
/// the recent cache; a decrypt failure produces no ack and no event while the pump continues.
/// </summary>
/// <remarks>
/// <para>
/// Composition (LoopbackRoundTripTests precedent): the scripted peer is the Noise responder
/// over the loopback transport pair, speaks the SDK's own router envelope helpers, and holds
/// synthetic Bob ratchets mirrored to the SDK-side seeded Alice sessions — the single source
/// of truth, so wire changes update both sides together. All keys are loopback-only fixture
/// material; assertions never print raw secrets.
/// </para>
/// </remarks>
public sealed class MessagingLoopbackTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    private const string OwnUser = "15551234567";
    private const int OwnDevice = 42;
    private const string PeerUser = "15551239999";
    private static readonly string PeerServer = "s.whatsapp.net";

    private static AccountId NewAccount(string hint) =>
        AccountId.Create($"msg-loopback-{hint}-" + Guid.NewGuid().ToString("N"));

    private static string DeviceJid(string user, int device) =>
        $"{user}:{device}@{PeerServer}";

    private static readonly RegistrationDefaults SeededRegistrationDefaults = new(
        new byte[32], 7, new byte[32], new byte[64], 42, "android", 2, 3000, 1043857760);

    private sealed record OwnKeys(byte[] IdentityPrivate, byte[] IdentityDjb, uint RegistrationId);

    private static OwnKeys CreateOwnKeys(uint registrationId)
    {
        (byte[] priv, byte[] pub) = X3dh.GenerateX25519KeyPair();
        return new OwnKeys(priv, SignalCipher.ToDjbIdentity(pub), registrationId);
    }

    private static async Task<HandshakePayloadProvider> CreateSeededPayloadsAsync(AccountId account, ICredentialStore credentials, OwnKeys own, CancellationToken cancellationToken)
    {
        var row = new CredentialRecord(
            DateTimeOffset.UtcNow,
            "android",
            RandomNumberGenerator.GetBytes(32),
            own.IdentityPrivate,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32),
            OwnUser,
            OwnDevice,
            (int)own.RegistrationId);
        await credentials.PutAsync(row, cancellationToken);
        return new HandshakePayloadProvider(account, credentials, SeededRegistrationDefaults);
    }

    /// <summary>Creates one synthetic Bob-side peer device (SignalSessionTests precedent).</summary>
    private sealed record PeerDevice(
        string Jid,
        byte[] XIdentityPrivate,
        byte[] XIdentityDjb,
        byte[] EdPrivate,
        byte[] EdPublicDjb,
        byte[] SignedPreKeyPrivate,
        byte[] SignedPreKeyPublic,
        byte[] SignedPreKeySignature,
        uint SignedPreKeyId,
        byte[] OneTimePrivate,
        byte[] OneTimePublic,
        uint OneTimePreKeyId,
        uint RegistrationId);

    private static PeerDevice CreatePeerDevice(string jid, uint signedPreKeyId, uint oneTimePreKeyId, uint registrationId)
    {
        (byte[] xPriv, byte[] xPub) = X3dh.GenerateX25519KeyPair();
        (byte[] edPriv, byte[] edPubDjb) = X3dh.GenerateIdentityKeyPair();
        (byte[] spkPriv, byte[] spkPub) = X3dh.GenerateX25519KeyPair();
        byte[] spkSig = X3dh.SignWithIdentity(edPriv, SignalCipher.ToDjbIdentity(spkPub));
        (byte[] otPriv, byte[] otPub) = X3dh.GenerateX25519KeyPair();
        return new PeerDevice(
            jid, xPriv, SignalCipher.ToDjbIdentity(xPub),
            edPriv, edPubDjb,
            spkPriv, spkPub, spkSig, signedPreKeyId,
            otPriv, otPub, oneTimePreKeyId,
            registrationId);
    }

    /// <summary>
    /// Seeds the SDK-side Alice session for one peer device and returns the mirrored Bob
    /// ratchet the scripted peer decrypts with.
    /// </summary>
    private static async Task<DoubleRatchet> SeedSessionPairAsync(
        ISignalSessionStore sessions, OwnKeys own, PeerDevice device, CancellationToken cancellationToken)
    {
        var bundle = new PeerBundle(
            device.XIdentityDjb,
            device.SignedPreKeyPublic,
            device.SignedPreKeySignature,
            device.SignedPreKeyId,
            device.OneTimePublic,
            device.OneTimePreKeyId,
            device.RegistrationId);
        (byte[] ephPriv, _) = X3dh.GenerateX25519KeyPair();
        AliceSession aliceSession = X3dh.InitializeAlice(own.IdentityPrivate, ephPriv, bundle, device.EdPublicDjb);
        (byte[] sendPriv, byte[] sendPub) = X3dh.GenerateX25519KeyPair();
        DoubleRatchet alice = DoubleRatchet.InitializeAlice(
            aliceSession, sendPriv, sendPub, bundle, own.IdentityDjb, device.XIdentityDjb, own.RegistrationId);
        await sessions.PutAsync(
            device.Jid,
            new SignalSessionRecord(device.Jid, alice.Serialize()),
            cancellationToken);

        BobSession bobSession = X3dh.InitializeBob(
            device.XIdentityPrivate, device.SignedPreKeyPrivate, device.OneTimePrivate, true,
            own.IdentityDjb, aliceSession.BasePublic);
        return DoubleRatchet.InitializeBob(
            bobSession, device.SignedPreKeyPrivate, device.XIdentityDjb, own.IdentityDjb,
            aliceSession.BasePublic, device.RegistrationId, own.RegistrationId);
    }

    /// <summary>Six fixed store instances behind the registry factory seams.</summary>
    private sealed class FixedStores : ISignalSessionStoreFactory, IIdentityStoreFactory, IPreKeyStoreFactory, IRecentMessageCacheStoreFactory, ISafetyStateStoreFactory, ICredentialStoreFactory
    {
        internal required ISignalSessionStore Sessions { get; init; }
        internal required IIdentityStore Identity { get; init; }
        internal required IPreKeyStore PreKeys { get; init; }
        internal required IRecentMessageCacheStore RecentMessages { get; init; }
        internal required ISafetyStateStore Safety { get; init; }
        internal required ICredentialStore Credentials { get; init; }

        ValueTask<ISignalSessionStore> ISignalSessionStoreFactory.CreateAsync(AccountId accountId, CancellationToken cancellationToken) => ValueTask.FromResult(Sessions);
        ValueTask<IIdentityStore> IIdentityStoreFactory.CreateAsync(AccountId accountId, CancellationToken cancellationToken) => ValueTask.FromResult(Identity);
        ValueTask<IPreKeyStore> IPreKeyStoreFactory.CreateAsync(AccountId accountId, CancellationToken cancellationToken) => ValueTask.FromResult(PreKeys);
        ValueTask<IRecentMessageCacheStore> IRecentMessageCacheStoreFactory.CreateAsync(AccountId accountId, CancellationToken cancellationToken) => ValueTask.FromResult(RecentMessages);
        ValueTask<ISafetyStateStore> ISafetyStateStoreFactory.CreateAsync(AccountId accountId, CancellationToken cancellationToken) => ValueTask.FromResult(Safety);
        ValueTask<ICredentialStore> ICredentialStoreFactory.CreateAsync(AccountId accountId, CancellationToken cancellationToken) => ValueTask.FromResult(Credentials);
    }

    /// <summary>Persist-instrumenting session-store decorator (D-06 ordering proof).</summary>
    private sealed class InstrumentedSessionStore : ISignalSessionStore
    {
        private readonly ISignalSessionStore _inner;

        /// <summary>Optional shared timeline: cross-queue ordering proof against peer events.</summary>
        internal ConcurrentQueue<string>? Timeline { get; init; }

        internal ConcurrentQueue<string> Log { get; } = new();

        internal InstrumentedSessionStore(ISignalSessionStore inner) => _inner = inner;

        public Task<SignalSessionRecord?> GetAsync(string address, CancellationToken cancellationToken) => _inner.GetAsync(address, cancellationToken);

        public Task<bool> RemoveAsync(string address, CancellationToken cancellationToken) => _inner.RemoveAsync(address, cancellationToken);

        public async Task PutAsync(string address, SignalSessionRecord session, CancellationToken cancellationToken)
        {
            await _inner.PutAsync(address, session, cancellationToken);
            Log.Enqueue("persist:" + address);
            Timeline?.Enqueue("persist:" + address);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    /// <summary>The scripted loopback server: Noise responder + usync/encrypt IQ responder + message acker.</summary>
    private sealed class ScriptedPeer
    {
        private readonly LoopbackFrameTransport _transport;
        private readonly NoiseTransport _noise;
        private readonly CancellationToken _token;

        internal ConcurrentQueue<BinaryNode> MessageNodes { get; } = new();
        internal ConcurrentQueue<string> Log { get; } = new();

        /// <summary>Optional shared timeline (D-06 cross-queue ordering proof): ack arrivals append here.</summary>
        internal ConcurrentQueue<string>? Timeline { get; set; }
        internal ConcurrentDictionary<string, DoubleRatchet> BobRatchets { get; } = new(StringComparer.Ordinal);
        internal string[] UsyncDevices { get; set; } = [];
        internal int UploadQueries;

        internal ScriptedPeer(LoopbackFrameTransport transport, NoiseTransport noise, CancellationToken token)
        {
            _transport = transport;
            _noise = noise;
            _token = token;
        }

        internal async Task RunAsync(CancellationToken endToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_token, endToken);
            while (!linked.IsCancellationRequested)
            {
                ReadOnlyMemory<byte>? frame = await _transport.ReceiveAsync(linked.Token);
                if (frame is null)
                {
                    return;
                }

                byte[] plain = _noise.Decrypt(WaFrame.Deframe(frame.Value.Span).Slice(1));
                BinaryNode node = BinaryNodeCodec.Decode(plain);
                if (node.Tag == "iq"
                    && node.Attrs.TryGetValue("id", out string? queryId)
                    && queryId is not null
                    && queryId.StartsWith("q-", StringComparison.Ordinal)
                    && node.Data is not null)
                {
                    await ServeQueryAsync(queryId, node.Data);
                    continue;
                }

                switch (node.Tag)
                {
                    case "message":
                        MessageNodes.Enqueue(node);
                        Log.Enqueue("message:" + node.Attrs.GetValueOrDefault("id"));
                        if (node.Attrs.TryGetValue("id", out string? messageId) && !string.IsNullOrEmpty(messageId))
                        {
                            await SendStanzaAsync(BinaryNode.Create(
                                "ack",
                                new Dictionary<string, string>(StringComparer.Ordinal)
                                {
                                    ["class"] = "message",
                                    ["id"] = messageId,
                                    ["to"] = node.Attrs.GetValueOrDefault("from") ?? "",
                                }));
                            Log.Enqueue("ack-sent:" + messageId);
                        }

                        break;
                    case "ack":
                        Log.Enqueue("ack:" + node.Attrs.GetValueOrDefault("id"));
                        Timeline?.Enqueue("ack:" + node.Attrs.GetValueOrDefault("id"));
                        break;
                }
            }
        }

        private async Task ServeQueryAsync(string queryId, byte[] content)
        {
            BinaryNode inner = BinaryNodeCodec.Decode(content);
            string? xmlns = inner.Attrs.GetValueOrDefault("xmlns");
            if (xmlns == "usync")
            {
                var devices = new List<BinaryNode>();
                foreach (string deviceJid in UsyncDevices)
                {
                    devices.Add(BinaryNode.Create(
                        "device",
                        new Dictionary<string, string>(StringComparer.Ordinal) { ["jid"] = deviceJid }));
                }

                BinaryNode user = BinaryNode.FromChildren(
                    "user",
                    [BinaryNode.FromChildren("devices", devices)],
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["jid"] = PeerUser });
                BinaryNode response = BinaryNode.FromChildren(
                    "iq",
                    [BinaryNode.FromChildren("usync", [BinaryNode.FromChildren("list", [user])], new Dictionary<string, string>(StringComparer.Ordinal) { ["index"] = "0" })],
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = "result" });
                Log.Enqueue("usync");
                await ReplyAsync(queryId, BinaryNodeCodec.Encode(response));
                return;
            }

            if (xmlns == "encrypt" && inner.Attrs.GetValueOrDefault("type") == "set")
            {
                Interlocked.Increment(ref UploadQueries);
                Log.Enqueue("upload");
                await ReplyAsync(queryId, BinaryNodeCodec.Encode(BinaryNode.Create("iq", new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = "result" })));
            }
        }

        private async Task ReplyAsync(string queryId, byte[] body)
        {
            byte[] response = QueryRouter.BuildFrame(queryId, body);
            await SendFrameAsync(response);
        }

        internal async Task SendStanzaAsync(BinaryNode node)
        {
            await SendFrameAsync(BinaryNodeCodec.Encode(node));
        }

        private async Task SendFrameAsync(byte[] encoded)
        {
            byte[] encrypted = _noise.Encrypt(encoded);
            byte[] flagged = new byte[encrypted.Length + 1];
            encrypted.CopyTo(flagged.AsSpan(1));
            await _transport.SendAsync(WaFrame.Frame(flagged), _token);
        }
    }

    private static async Task<LoopbackFrameTransport> TakePeerAsync(ConcurrentQueue<LoopbackFrameTransport> peers, CancellationToken cancellationToken)
    {
        LoopbackFrameTransport? peer;
        while (!peers.TryDequeue(out peer))
        {
            await Task.Delay(5, cancellationToken);
        }

        return peer!;
    }

    private static async Task WaitForAsync(ConcurrentQueue<string> log, Func<string, bool> predicate, string what)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);
        while (!timeout.IsCancellationRequested)
        {
            if (log.Any(predicate))
            {
                return;
            }

            try
            {
                await Task.Delay(10, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        throw new TimeoutException($"Timed out waiting for: {what}.");
    }

    private static async Task WaitForCountAsync(ConcurrentQueue<MessageReceivedEventArgs> events, int count, string what)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);
        while (!timeout.IsCancellationRequested)
        {
            if (events.Count >= count)
            {
                return;
            }

            await Task.Delay(10, timeout.Token);
        }

        throw new TimeoutException($"Timed out waiting for {count} events: {what}.");
    }

    private static int IndexOf(ConcurrentQueue<string> log, string entry)
    {
        int i = 0;
        foreach (string item in log)
        {
            if (item == entry)
            {
                return i;
            }

            i++;
        }

        return -1;
    }

    /// <summary>Completes the scripted peer handshake and sends the open-gating success stanza.</summary>
    private static async Task<ScriptedPeer> CompletePeerHandshakeAsync(
        ConcurrentQueue<LoopbackFrameTransport> peers,
        CancellationToken token)
    {
        LoopbackFrameTransport transport = await TakePeerAsync(peers, token);
        ReadOnlyMemory<byte> header = await transport.ReceiveAsync(token)
            ?? throw new InvalidOperationException("Peer transport closed before the intro header.");
        NoiseHandshake.ValidateIntroHeader(header.ToArray());

        ReadOnlyMemory<byte> messageAFrame = await transport.ReceiveAsync(token)
            ?? throw new InvalidOperationException("Peer transport closed before message A.");
        using var responder = new NoiseHandshakeResponder(Encoding.ASCII.GetBytes("server-hello"));
        responder.ReadMessageA(WaFrame.Deframe(messageAFrame.Span).ToArray());
        await transport.SendAsync(WaFrame.Frame(responder.WriteMessageB()), token);

        ReadOnlyMemory<byte> messageCFrame = await transport.ReceiveAsync(token)
            ?? throw new InvalidOperationException("Peer transport closed before message C.");
        NoiseTransport noise = responder.ReadMessageC(WaFrame.Deframe(messageCFrame.Span).ToArray());

        byte[] successBytes = BinaryNodeCodec.Encode(BinaryNode.Create("success"));
        byte[] encrypted = noise.Encrypt(successBytes);
        byte[] flagged = new byte[encrypted.Length + 1];
        encrypted.CopyTo(flagged.AsSpan(1));
        await transport.SendAsync(WaFrame.Frame(flagged), token);

        return new ScriptedPeer(transport, noise, token);
    }

    private static ConnectionOptions TracerOptions() => new()
    {
        ConnectTimeoutMs = 5_000,
        QueryTimeoutMs = 5_000,
        RetryBaseDelayMs = 0,
        RetryMaxDelayMs = 0,
    };

    [Fact]
    public async Task send_encrypts_per_device_and_peer_decrypts_exact_plaintext()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        AccountId account = NewAccount("send");
        OwnKeys own = CreateOwnKeys(777);
        var peers = new ConcurrentQueue<LoopbackFrameTransport>();

        await using var factory = new InMemoryStoreFactory();
        ISignalSessionStore sessions = await factory.CreateAsync(account, token);
        IIdentityStore identities = await ((IIdentityStoreFactory)factory).CreateAsync(account, token);
        IPreKeyStore preKeys = await ((IPreKeyStoreFactory)factory).CreateAsync(account, token);
        IRecentMessageCacheStore cache = await ((IRecentMessageCacheStoreFactory)factory).CreateAsync(account, token);
        ISafetyStateStore safety = await ((ISafetyStateStoreFactory)factory).CreateAsync(account, token);
        ICredentialStore credentials = await ((ICredentialStoreFactory)factory).CreateAsync(account, token);
        HandshakePayloadProvider payloads = await CreateSeededPayloadsAsync(account, credentials, own, token);

        PeerDevice deviceA = CreatePeerDevice(DeviceJid(PeerUser, 52), 22, 1101, 1234);
        PeerDevice deviceB = CreatePeerDevice(DeviceJid(PeerUser, 53), 23, 1102, 1235);
        DoubleRatchet bobA = await SeedSessionPairAsync(sessions, own, deviceA, token);
        DoubleRatchet bobB = await SeedSessionPairAsync(sessions, own, deviceB, token);

        var fixedStores = new FixedStores
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
        await using var connection = new WaConnection(account, CreateClient(peers), payloads, TracerOptions());
        connection.StoreRegistry = new StoreRegistry(registryOptions);

        ScriptedPeer? peer = null;
        var peerTask = Task.Run(async () =>
        {
            peer = await CompletePeerHandshakeAsync(peers, token);
            peer!.BobRatchets[deviceA.Jid] = bobA;
            peer!.BobRatchets[deviceB.Jid] = bobB;
            // Fanout set as the server sees it: both peer companions, the own device
            // (T-4-11: must never enter the fanout), and the phone (Pattern 3 exclusion).
            peer!.UsyncDevices = [deviceA.Jid, deviceB.Jid, DeviceJid(OwnUser, OwnDevice), DeviceJid(PeerUser, 0)];
            await peer!.RunAsync(testCts.Token);
        }, token);

        await connection.ConnectAsync(token);
        Messenger messenger = connection.Messaging;
        var events = new ConcurrentQueue<MessageReceivedEventArgs>();
        messenger.MessageReceived += (_, e) => events.Enqueue(e);

        Jid chat = Jid.Parse($"{PeerUser}@{PeerServer}");
        SendOutcome outcome = await messenger.SendMessageAsync(chat, "hello", token);

        outcome.Status.Should().Be(SendOutcomeStatus.Sent);
        outcome.MessageId.Should().HaveLength(40);
        outcome.MessageId.Should().StartWith("3EB0");
        outcome.MessageId[4..].Should().MatchRegex("^[0-9A-F]{36}$", "the id is 3EB0 + 36 uppercase hex chars");

        await WaitForAsync(peer!.Log, entry => entry.StartsWith("message:", StringComparison.Ordinal), "peer received the message node");
        peer.MessageNodes.TryDequeue(out BinaryNode? messageNode).Should().BeTrue("the scripted server saw exactly one message node");

        long maxT = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60;
        messageNode!.Attrs["id"].Should().Be(outcome.MessageId);
        messageNode.Attrs["to"].Should().Be(chat.ToString());
        messageNode.Attrs["type"].Should().Be("text");
        long t = long.Parse(messageNode.Attrs["t"], CultureInfo.InvariantCulture);
        t.Should().BeGreaterThanOrEqualTo(1_700_000_000);
        t.Should().BeLessThanOrEqualTo(maxT, "the t attribute is sender-stamped unix seconds (A4)");

        var toChildren = (messageNode.Children ?? []).Where(c => c.Tag == "to").ToList();
        toChildren.Should().HaveCount(2, "one to-wrapped ciphertext per fanout device");
        var wrapped = new List<(string Jid, BinaryNode Enc)>();
        foreach (BinaryNode child in toChildren)
        {
            child.Tag.Should().Be("to", "per-device ciphertexts ride to-wrapped enc children");
            BinaryNode enc = (child.Children ?? [])[0];
            enc.Tag.Should().Be("enc");
            enc.Attrs["v"].Should().Be("2");
            wrapped.Add((child.Attrs["jid"], enc));
        }

        // Own-device and phone entries are excluded from the fanout (T-4-11).
        wrapped.Select(w => w.Jid).Should().Equal(deviceA.Jid, deviceB.Jid);
        wrapped.Should().OnlyContain(w => w.Enc.Attrs["type"] == "pkmsg", "fresh prekey sessions always emit pkmsg");
        (messageNode.Children ?? []).Should().Contain(c => c.Tag == "device-identity", "a pkmsg fanout carries the device-identity child");

        foreach ((string jid, BinaryNode enc) in wrapped)
        {
            DoubleRatchet bob = peer.BobRatchets[jid];
            SignalDecryptOutcome decrypted = bob.Decrypt(enc.Data!, isPreKey: true, token);
            SignalDecryptOutcome.Decrypted plain = decrypted.Should().BeOfType<SignalDecryptOutcome.Decrypted>().Which;
            Message parsed = Message.Parser.ParseFrom(plain.Plaintext);
            parsed.Conversation.Should().Be("hello", "the peer decrypts the exact sent text out of the protobuf envelope");
        }

        events.Should().BeEmpty("an outbound send never raises the inbound-message event");
        peer.UploadQueries.Should().Be(0, "Task 1's send path issues no prekey upload");

        await connection.DisposeAsync();
        await peerTask;
    }

    [Fact]
    public async Task receive_ack_after_persist_event_once_then_cache_and_duplicate_ignored()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        AccountId account = NewAccount("receive");
        OwnKeys own = CreateOwnKeys(778);
        var peers = new ConcurrentQueue<LoopbackFrameTransport>();

        await using var factory = new InMemoryStoreFactory();
        ISignalSessionStore innerSessions = await factory.CreateAsync(account, token);
        var timeline = new ConcurrentQueue<string>();
        var instrumented = new InstrumentedSessionStore(innerSessions) { Timeline = timeline };
        IIdentityStore identities = await ((IIdentityStoreFactory)factory).CreateAsync(account, token);
        IPreKeyStore preKeys = await ((IPreKeyStoreFactory)factory).CreateAsync(account, token);
        IRecentMessageCacheStore cache = await ((IRecentMessageCacheStoreFactory)factory).CreateAsync(account, token);
        ISafetyStateStore safety = await ((ISafetyStateStoreFactory)factory).CreateAsync(account, token);
        ICredentialStore credentials = await ((ICredentialStoreFactory)factory).CreateAsync(account, token);
        HandshakePayloadProvider payloads = await CreateSeededPayloadsAsync(account, credentials, own, token);

        // SDK-side Bob (receiver role) at the peer's device address; the peer holds
        // Alice. The SDK owns the signed/one-time prekeys Alice X3DHs against
        // (canonical CreatePair construction — Bob's identity and bundle match).
        PeerDevice peerDevice = CreatePeerDevice(DeviceJid(PeerUser, 52), 22, 1101, 1234);
        (byte[] ownSpkPriv, byte[] ownSpkPub) = X3dh.GenerateX25519KeyPair();
        (byte[] ownEdPriv, byte[] ownEdPubDjb) = X3dh.GenerateIdentityKeyPair();
        byte[] ownSpkSig = X3dh.SignWithIdentity(ownEdPriv, SignalCipher.ToDjbIdentity(ownSpkPub));
        (byte[] ownOtPriv, byte[] ownOtPub) = X3dh.GenerateX25519KeyPair();
        var sdkBundle = new PeerBundle(
            own.IdentityDjb, ownSpkPub, ownSpkSig,
            22u, ownOtPub, 1101u, own.RegistrationId);
        (byte[] aliceEph, _) = X3dh.GenerateX25519KeyPair();
        AliceSession peerAliceSession = X3dh.InitializeAlice(peerDevice.XIdentityPrivate, aliceEph, sdkBundle, ownEdPubDjb);
        (byte[] peerSendPriv, byte[] peerSendPub) = X3dh.GenerateX25519KeyPair();
        DoubleRatchet peerAlice = DoubleRatchet.InitializeAlice(
            peerAliceSession, peerSendPriv, peerSendPub, sdkBundle,
            peerDevice.XIdentityDjb, own.IdentityDjb, peerDevice.RegistrationId);
        BobSession bobSession = X3dh.InitializeBob(
            own.IdentityPrivate, ownSpkPriv, ownOtPriv, true,
            peerDevice.XIdentityDjb, peerAliceSession.BasePublic);
        DoubleRatchet sdkBob = DoubleRatchet.InitializeBob(
            bobSession, ownSpkPriv, own.IdentityDjb, peerDevice.XIdentityDjb,
            peerAliceSession.BasePublic, own.RegistrationId, peerDevice.RegistrationId);
        string peerAddress = peerDevice.Jid;
        await innerSessions.PutAsync(
            peerAddress,
            new SignalSessionRecord(peerAddress, sdkBob.Serialize()),
            token);

        var fixedStores = new FixedStores
        {
            Sessions = instrumented, Identity = identities, PreKeys = preKeys,
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
        await using var connection = new WaConnection(account, CreateClient(peers), payloads, TracerOptions());
        connection.StoreRegistry = new StoreRegistry(registryOptions);

        ScriptedPeer? peer = null;
        var peerTask = Task.Run(async () =>
        {
            peer = await CompletePeerHandshakeAsync(peers, token);
            peer!.Timeline = timeline;
            await peer!.RunAsync(testCts.Token);
        }, token);

        await connection.ConnectAsync(token);
        Messenger messenger = connection.Messaging;
        var events = new ConcurrentQueue<MessageReceivedEventArgs>();
        messenger.MessageReceived += (_, e) => events.Enqueue(e);

        // The scripted peer sends one encrypted message, then the SAME wire bytes again
        // (replay): the second must decrypt-fail with no second ack and no second event.
        SignalEncryptOutcome first = peerAlice.Encrypt(MessageBuilder.BuildPlainText("hi there"), token);
        byte[] wire = ((SignalEncryptOutcome.PreKeyMessageSent)first).WireBytes;
        byte[] wireCopy = (byte[])wire.Clone();

        await peer!.SendStanzaAsync(BuildInboundMessageNode("peer-msg-1", peerDevice.Jid, wire, "pkmsg"));
        await WaitForAsync(peer.Log, entry => entry == "ack:peer-msg-1", "the SDK acked the inbound message");

        events.Should().HaveCount(1);
        MessageReceivedEventArgs received = events.Single();
        received.Key.RemoteJid.ToString().Should().Be($"{PeerUser}@{PeerServer}");
        received.Key.Id.Should().Be("peer-msg-1");
        received.Key.FromMe.Should().BeFalse();
        received.Key.Participant.Should().BeEmpty();
        received.Sender.ToString().Should().Be(peerDevice.Jid);
        received.IsFromMe.Should().BeFalse();
        received.Text.Should().Be("hi there");

        // D-06: the session persist is observable BEFORE the ack leaves — one shared
        // timeline orders the store write against the peer's ack arrival.
        int persistIndex = IndexOf(timeline, "persist:" + peerAddress);
        int ackIndex = IndexOf(timeline, "ack:peer-msg-1");
        persistIndex.Should().BeGreaterThanOrEqualTo(0, "the session write was instrumented");
        ackIndex.Should().BeGreaterThanOrEqualTo(0);
        persistIndex.Should().BeLessThan(ackIndex, "persist strictly precedes the ack (D-06, Pitfall 4)");

        RecentMessageEntry? cached = await cache.GetAsync("peer-msg-1", token);
        cached.Should().NotBeNull("a successful cache write is observable by an independent read");
        cached!.ChatJid.Should().Be($"{PeerUser}@{PeerServer}");
        cached.Text.Should().Be("hi there");
        cached.Sender.Should().Be(peerDevice.Jid);
        cached.ResendPayload.Should().NotBeNull();

        // Replay of the identical node: decrypt failure, no second ack, no second event.
        await peer.SendStanzaAsync(BuildInboundMessageNode("peer-msg-1", peerDevice.Jid, wireCopy, "pkmsg"));
        await Task.Delay(600, token);
        events.Should().HaveCount(1, "a replayed message never raises a second event");
        peer.Log.Count(entry => entry == "ack:peer-msg-1").Should().Be(1, "a replayed message is never acked again");

        // The pump continues: a fresh (ratchet-advanced) message flows normally.
        SignalEncryptOutcome second = peerAlice.Encrypt(MessageBuilder.BuildPlainText("second"), token);
        byte[] wire2 = second is SignalEncryptOutcome.PreKeyMessageSent pk2 ? pk2.WireBytes : ((SignalEncryptOutcome.MessageSent)second).WireBytes;
        string type2 = second is SignalEncryptOutcome.PreKeyMessageSent ? "pkmsg" : "msg";
        await peer.SendStanzaAsync(BuildInboundMessageNode("peer-msg-2", peerDevice.Jid, wire2, type2));
        await WaitForAsync(peer.Log, entry => entry == "ack:peer-msg-2", "the pump processes a later message");
        await WaitForCountAsync(events, 2, "the second message event");
        events.Last().Text.Should().Be("second");

        await connection.DisposeAsync();
        await peerTask;
    }

    [Fact]
    public async Task decrypt_failure_no_ack_no_event_and_pump_continues()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        AccountId account = NewAccount("decryptfail");
        OwnKeys own = CreateOwnKeys(779);
        var peers = new ConcurrentQueue<LoopbackFrameTransport>();

        await using var factory = new InMemoryStoreFactory();
        ISignalSessionStore sessions = await factory.CreateAsync(account, token);
        IIdentityStore identities = await ((IIdentityStoreFactory)factory).CreateAsync(account, token);
        IPreKeyStore preKeys = await ((IPreKeyStoreFactory)factory).CreateAsync(account, token);
        IRecentMessageCacheStore cache = await ((IRecentMessageCacheStoreFactory)factory).CreateAsync(account, token);
        ISafetyStateStore safety = await ((ISafetyStateStoreFactory)factory).CreateAsync(account, token);
        ICredentialStore credentials = await ((ICredentialStoreFactory)factory).CreateAsync(account, token);
        HandshakePayloadProvider payloads = await CreateSeededPayloadsAsync(account, credentials, own, token);

        PeerDevice peerDevice = CreatePeerDevice(DeviceJid(PeerUser, 52), 22, 1101, 1234);
        // SDK-side Bob owns the prekeys Alice X3DHs against (canonical CreatePair construction).
        (byte[] ownSpkPriv, byte[] ownSpkPub) = X3dh.GenerateX25519KeyPair();
        (byte[] ownEdPriv, byte[] ownEdPubDjb) = X3dh.GenerateIdentityKeyPair();
        byte[] ownSpkSig = X3dh.SignWithIdentity(ownEdPriv, SignalCipher.ToDjbIdentity(ownSpkPub));
        (byte[] ownOtPriv, byte[] ownOtPub) = X3dh.GenerateX25519KeyPair();
        var sdkBundle = new PeerBundle(
            own.IdentityDjb, ownSpkPub, ownSpkSig,
            22u, ownOtPub, 1101u, own.RegistrationId);
        (byte[] aliceEph, _) = X3dh.GenerateX25519KeyPair();
        AliceSession peerAliceSession = X3dh.InitializeAlice(peerDevice.XIdentityPrivate, aliceEph, sdkBundle, ownEdPubDjb);
        (byte[] peerSendPriv, byte[] peerSendPub) = X3dh.GenerateX25519KeyPair();
        DoubleRatchet peerAlice = DoubleRatchet.InitializeAlice(
            peerAliceSession, peerSendPriv, peerSendPub, sdkBundle,
            peerDevice.XIdentityDjb, own.IdentityDjb, peerDevice.RegistrationId);
        BobSession bobSession = X3dh.InitializeBob(
            own.IdentityPrivate, ownSpkPriv, ownOtPriv, true,
            peerDevice.XIdentityDjb, peerAliceSession.BasePublic);
        DoubleRatchet sdkBob = DoubleRatchet.InitializeBob(
            bobSession, ownSpkPriv, own.IdentityDjb, peerDevice.XIdentityDjb,
            peerAliceSession.BasePublic, own.RegistrationId, peerDevice.RegistrationId);
        await sessions.PutAsync(
            peerDevice.Jid,
            new SignalSessionRecord(peerDevice.Jid, sdkBob.Serialize()),
            token);

        var fixedStores = new FixedStores
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
        await using var connection = new WaConnection(account, CreateClient(peers), payloads, TracerOptions());
        connection.StoreRegistry = new StoreRegistry(registryOptions);

        ScriptedPeer? peer = null;
        var peerTask = Task.Run(async () =>
        {
            peer = await CompletePeerHandshakeAsync(peers, token);
            await peer!.RunAsync(testCts.Token);
        }, token);

        await connection.ConnectAsync(token);
        Messenger messenger = connection.Messaging;
        var events = new ConcurrentQueue<MessageReceivedEventArgs>();
        messenger.MessageReceived += (_, e) => events.Enqueue(e);

        byte[] garbage = RandomNumberGenerator.GetBytes(64);
        await peer!.SendStanzaAsync(BuildInboundMessageNode("bad-1", peerDevice.Jid, garbage, "pkmsg"));
        await Task.Delay(900, token);

        peer.Log.Where(entry => entry.StartsWith("ack:bad-1", StringComparison.Ordinal)).Should().BeEmpty("no ack claims a failed decrypt");
        events.Should().BeEmpty("a decryption failure never raises the message event");

        // The pump continues: a valid message after the failure flows normally.
        SignalEncryptOutcome valid = peerAlice.Encrypt(MessageBuilder.BuildPlainText("after failure"), token);
        byte[] wire = valid is SignalEncryptOutcome.PreKeyMessageSent pk ? pk.WireBytes : ((SignalEncryptOutcome.MessageSent)valid).WireBytes;
        string encType = valid is SignalEncryptOutcome.PreKeyMessageSent ? "pkmsg" : "msg";
        await peer.SendStanzaAsync(BuildInboundMessageNode("good-1", peerDevice.Jid, wire, encType));
        await WaitForAsync(peer.Log, entry => entry == "ack:good-1", "the pump survives the failed decrypt");
        await WaitForCountAsync(events, 1, "the post-failure message event");
        events.Single().Text.Should().Be("after failure");

        await connection.DisposeAsync();
        await peerTask;
    }

    private static BinaryNode BuildInboundMessageNode(string messageId, string fromDeviceJid, byte[] wire, string encType)
    {
        return BinaryNode.FromChildren(
            "message",
            [BinaryNode.FromBytes(
                "enc",
                wire,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["v"] = "2",
                    ["type"] = encType,
                })],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["id"] = messageId,
                ["from"] = fromDeviceJid,
                ["type"] = "text",
                ["t"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            });
    }

    private static Func<IFrameTransport> CreateClient(ConcurrentQueue<LoopbackFrameTransport> peers) => () =>
    {
        (LoopbackFrameTransport left, LoopbackFrameTransport right) = LoopbackFrameTransport.CreatePair();
        peers.Enqueue(right);
        return left;
    };
}

