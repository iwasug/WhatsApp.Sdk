using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Noise;
using Iwasug.WhatsApp.Sdk.Pairing;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.InMemory;
using Iwasug.WhatsApp.Sdk.Tests.Transport;
using Iwasug.WhatsApp.Sdk.Transport;
using NSec.Cryptography;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Connection;

/// <summary>
/// Tracer proofs (03-01, CONN-03 mechanism half): a restarted process resumes from its
/// own store — same Noise static key, login ClientPayload in message C,
/// <see cref="ConnectionState.Open"/> only after the server's <c>success</c>.
/// </summary>
/// <remarks>
/// <para>
/// The scripted loopback peer stands in for the server: it drives the production
/// responder role, captures message C through the production envelope parser, and
/// decrypts the initiator static + finish payload through the production raw-message
/// path — so every assertion observes what a real server would see. The peer's fixed
/// test keys and the seeded credential rows below are loopback-only fixture material,
/// never production secrets.
/// </para>
/// <para>
/// Output discipline (V6/T-2-10): assertions compare hex strings of public values
/// (static publics, serialized payloads) — private key bytes never enter an assertion
/// message, so even a failing run cannot print key material.
/// </para>
/// </remarks>
public sealed class ResumeTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PreSuccessWindow = TimeSpan.FromMilliseconds(300);

    private const string PairedUsername = "1234567890";
    private const int PairedDevice = 1;
    private const int FixtureRegistrationId = 42;

    private static byte[] FixedBytes(int start)
    {
        byte[] bytes = new byte[32];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(start + i);
        }

        return bytes;
    }

    private static byte[] FixedSignature()
    {
        byte[] bytes = new byte[64];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(0xA0 + i);
        }

        return bytes;
    }

    private static CredentialRecord SeedRow() =>
        new(
            new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero),
            "android",
            FixedBytes(0x01),
            FixedBytes(0x21),
            FixedBytes(0x41),
            FixedBytes(0x61),
            PairedUsername,
            PairedDevice,
            FixtureRegistrationId);

    private static RegistrationDefaults DummyRegistrationDefaults() =>
        new(
            FixedBytes(0x11),
            7,
            FixedBytes(0x31),
            FixedSignature(),
            FixtureRegistrationId,
            "android",
            2,
            3000,
            1043857760);

    private sealed class PairSource
    {
        internal readonly ConcurrentQueue<LoopbackFrameTransport> Peers = new();
        internal int Created;

        internal IFrameTransport CreateClient()
        {
            (LoopbackFrameTransport left, LoopbackFrameTransport right) = LoopbackFrameTransport.CreatePair();
            Peers.Enqueue(right);
            Interlocked.Increment(ref Created);
            return left;
        }
    }

    private sealed class ResumeHandshakeResult
    {
        internal required NoiseTransport Transport { get; init; }

        internal required byte[] InitiatorStaticPublic { get; init; }

        internal required byte[] ClientFinishPayload { get; init; }
    }

    private static AccountId NewAccount(string hint) =>
        AccountId.Create($"resume-{hint}-" + Guid.NewGuid().ToString("N"));

    private static ConnectionOptions TracerOptions() => new()
    {
        ConnectTimeoutMs = 5_000,
        QueryTimeoutMs = 5_000,
        RetryBaseDelayMs = 0,
        RetryMaxDelayMs = 0,
    };

    private static async Task<LoopbackFrameTransport> TakePeerAsync(PairSource pairs, CancellationToken cancellationToken)
    {
        LoopbackFrameTransport? peer;
        while (!pairs.Peers.TryDequeue(out peer))
        {
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }

        return peer!;
    }

    private static async Task<byte[]> ReceiveOrThrowAsync(IFrameTransport transport, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte>? frame = await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Peer transport closed unexpectedly.");

        return frame.Value.ToArray();
    }

    private static async Task<ResumeHandshakeResult> CompleteResumePeerHandshakeAsync(
        IFrameTransport peer,
        byte[] serverHello,
        CancellationToken cancellationToken)
    {
        byte[] header = await ReceiveOrThrowAsync(peer, cancellationToken).ConfigureAwait(false);
        NoiseHandshake.ValidateIntroHeader(header);

        byte[] messageAFrame = await ReceiveOrThrowAsync(peer, cancellationToken).ConfigureAwait(false);
        using var responder = new NoiseHandshakeResponder(serverHello);
        byte[] messageA = WaFrame.Deframe(messageAFrame).ToArray();
        responder.ReadMessageA(messageA);
        byte[] messageB = responder.WriteMessageB();
        await peer.SendAsync(WaFrame.Frame(messageB), cancellationToken).ConfigureAwait(false);

        // Message C through the production envelope parser + raw-message path (the
        // same bytes a real server decrypts): the initiator static public and the
        // opaque finish blob are both recovered server-side.
        byte[] messageCFrame = await ReceiveOrThrowAsync(peer, cancellationToken).ConfigureAwait(false);
        byte[] messageC = WaFrame.Deframe(messageCFrame).ToArray();
        HandshakeMessage envelope = HandshakeMessage.Parser.ParseFrom(messageC);
        ClientFinish? finish = envelope.ClientFinish
            ?? throw new InvalidOperationException("Peer received message C without a client-finish envelope.");
        byte[] raw3 = new byte[finish.StaticKey.Length + finish.Payload.Length];
        finish.StaticKey.ToByteArray().CopyTo(raw3.AsSpan(0, finish.StaticKey.Length));
        finish.Payload.ToByteArray().CopyTo(raw3.AsSpan(finish.StaticKey.Length));
        (byte[] sendKey, byte[] receiveKey, byte[] finishPayload) = responder.CompleteRawMessage3(raw3);
        byte[] initiatorStatic = responder.InitiatorStaticPublic
            ?? throw new InvalidOperationException("Peer did not recover the initiator static key.");
        var transport = new NoiseTransport(sendKey, receiveKey);
        CryptographicOperations.ZeroMemory(sendKey);
        CryptographicOperations.ZeroMemory(receiveKey);
        return new ResumeHandshakeResult
        {
            Transport = transport,
            InitiatorStaticPublic = initiatorStatic,
            ClientFinishPayload = finishPayload,
        };
    }

    private static async Task SendPeerSuccessAsync(
        IFrameTransport peer,
        NoiseTransport noise,
        CancellationToken cancellationToken)
    {
        byte[] nodeBytes = BinaryNodeCodec.Encode(BinaryNode.Create("success"));
        byte[] encrypted = noise.Encrypt(nodeBytes);
        byte[] flagged = new byte[encrypted.Length + 1];
        encrypted.CopyTo(flagged.AsSpan(1));
        await peer.SendAsync(WaFrame.Frame(flagged), cancellationToken).ConfigureAwait(false);
    }

    private static string ExpectedStaticPublicHex(byte[] staticPrivate)
    {
        byte[] copy = (byte[])staticPrivate.Clone();
        try
        {
            using Key key = Key.Import(KeyAgreementAlgorithm.X25519, copy, KeyBlobFormat.RawPrivateKey);
            return Convert.ToHexString(key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    private static void AssertLoginPayload(byte[] finishPayload, string because)
    {
        ClientPayload payload = ClientPayload.Parser.ParseFrom(finishPayload);
        payload.Username.Should().Be(1234567890UL, because + " (login carries the paired username)");
        payload.Device.Should().Be((uint)PairedDevice, because + " (login carries the paired device)");
        payload.Passive.Should().BeTrue(because + " (login is passive)");
        payload.Pull.Should().BeTrue(because + " (login pulls)");
        payload.DevicePairingData.Should().BeNull(because + " (the registration branch is unreachable while a row exists)");
    }

    private static async Task WaitForQueuedAsync(
        ConcurrentQueue<ConnectionStateChangedEventArgs> states,
        Func<ConnectionStateChangedEventArgs, bool> predicate,
        string what)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);
        while (!timeout.IsCancellationRequested)
        {
            foreach (ConnectionStateChangedEventArgs state in states)
            {
                if (predicate(state))
                {
                    return;
                }
            }

            await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for connection event: {what}.");
    }

    [Fact]
    public async Task Resume_seeded_credentials_login_opens_after_success()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        AccountId account = NewAccount("login");

        ICredentialStoreFactory factory = new InMemoryStoreFactory();
        ICredentialStore store = await factory.CreateAsync(account, testToken);
        CredentialRecord seed = SeedRow();
        await store.PutAsync(seed, testToken);

        var payloads = new HandshakePayloadProvider(account, store, DummyRegistrationDefaults());
        await using var connection = new WaConnection(
            account,
            pairs.CreateClient,
            payloads,
            TracerOptions());
        connection.StateChanged += (_, e) => states.Enqueue(e);

        // The success gate: the peer holds <success> until the test releases it, so
        // the pre-success window deterministically proves Open never fires early.
        var releaseSuccess = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerResult = new TaskCompletionSource<ResumeHandshakeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            ResumeHandshakeResult handshake = await CompleteResumePeerHandshakeAsync(
                peer,
                Encoding.ASCII.GetBytes("server-hello"),
                testToken);
            peerResult.TrySetResult(handshake);
            await releaseSuccess.Task.WaitAsync(testToken);
            await SendPeerSuccessAsync(peer, handshake.Transport, testToken);
            // Hold the session until the client tears it down: EOF arrives when the
            // test disposes its connection, which completes this task deterministically.
            ReadOnlyMemory<byte>? tail = await peer.ReceiveAsync(testToken);
            tail.Should().BeNull("the client owns teardown — the peer only observes it");
            handshake.Transport.Dispose();
            await peer.DisposeAsync();
        }, testToken);

        Task connectTask = connection.ConnectAsync(testToken);

        // Open must NOT fire before the fake sends <success> — even though the
        // handshake completed and the credential row exists.
        await Task.Delay(PreSuccessWindow, testToken);
        connectTask.IsCompleted.Should().BeFalse("Open fires only after <success>, never on handshake completion alone");
        states.Any(e => e.State == ConnectionState.Open).Should().BeFalse("Open fires only after <success>");

        releaseSuccess.TrySetResult();
        await connectTask;
        await WaitForQueuedAsync(states, e => e.State == ConnectionState.Open, "open after success");

        ResumeHandshakeResult captured = await peerResult.Task.WaitAsync(testToken);

        // (a) The handshake used the persisted static identity, observed server-side.
        Convert.ToHexString(captured.InitiatorStaticPublic).Should().Be(
            ExpectedStaticPublicHex(seed.NoiseKeyPrivate),
            "resume reuses the persisted Noise static key instead of minting a fresh identity");

        // (b) Message C carries the login ClientPayload for the seeded row.
        AssertLoginPayload(captured.ClientFinishPayload, "the seeded row selects the login payload");

        // (d) exactly one Open; (e) the row is still observable after open.
        states.Count(e => e.State == ConnectionState.Open).Should().Be(1);
        CredentialRecord? after = await store.GetAsync(testToken);
        after.Should().NotBeNull("the credential row stays durable across the open");
        after!.PairedUser.Should().Be(PairedUsername);
        connection.State.Should().Be(ConnectionState.Open);

        await connection.DisposeAsync();
        await peerTask;
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Resume_missing_row_builds_registration_payload()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        AccountId account = NewAccount("registration");

        // No row seeded: the provider takes the registration branch with the injected
        // defaults, and the handshake would mint a fresh identity (null static).
        ICredentialStoreFactory factory = new InMemoryStoreFactory();
        ICredentialStore store = await factory.CreateAsync(account, testToken);
        var payloads = new HandshakePayloadProvider(account, store, DummyRegistrationDefaults());

        byte[]? staticKey = await payloads.LoadCredentialsAsync(testToken);
        staticKey.Should().BeNull("a missing row keeps the fresh-identity registration path");

        byte[] finish = payloads.GetClientFinish();
        ClientPayload payload = ClientPayload.Parser.ParseFrom(finish);
        payload.Passive.Should().BeFalse("registration is not passive");
        payload.Pull.Should().BeFalse("registration does not pull");
        payload.DevicePairingData.Should().NotBeNull("registration carries device pairing data");
        payload.DevicePairingData!.EIdent.Should().Equal(FixedBytes(0x11), "registration carries the injected identity");
        payload.Username.Should().Be(0, "registration carries no paired username");

        await store.DisposeAsync();
    }

    [Fact]
    public async Task Resume_second_connect_sends_login_payload_again()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        AccountId account = NewAccount("reconnect");

        // One factory (one process image): both connections read the same persisted row.
        ICredentialStoreFactory factory = new InMemoryStoreFactory();
        ICredentialStore store = await factory.CreateAsync(account, testToken);
        CredentialRecord seed = SeedRow();
        await store.PutAsync(seed, testToken);
        string expectedStaticHex = ExpectedStaticPublicHex(seed.NoiseKeyPrivate);

        async Task<ResumeHandshakeResult> ConnectOnceAsync(ConcurrentQueue<ConnectionStateChangedEventArgs> states)
        {
            var payloads = new HandshakePayloadProvider(account, store, DummyRegistrationDefaults());
            var connection = new WaConnection(account, pairs.CreateClient, payloads, TracerOptions());
            connection.StateChanged += (_, e) => states.Enqueue(e);

            var captured = new TaskCompletionSource<ResumeHandshakeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var peerTask = Task.Run(async () =>
            {
                LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
                ResumeHandshakeResult handshake = await CompleteResumePeerHandshakeAsync(
                    peer,
                    Encoding.ASCII.GetBytes("server-hello"),
                    testToken);
                captured.TrySetResult(handshake);
                await SendPeerSuccessAsync(peer, handshake.Transport, testToken);
                // Hold the session until the client tears it down: EOF arrives when the
                // test disposes its connection, which completes this task deterministically.
                ReadOnlyMemory<byte>? tail = await peer.ReceiveAsync(testToken);
                tail.Should().BeNull("the client owns teardown — the peer only observes it");
                handshake.Transport.Dispose();
                await peer.DisposeAsync();
            }, testToken);

            await connection.ConnectAsync(testToken);
            ResumeHandshakeResult result = await captured.Task.WaitAsync(testToken);
            await connection.DisposeAsync();
            await peerTask;
            return result;
        }

        var firstStates = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        ResumeHandshakeResult first = await ConnectOnceAsync(firstStates);
        firstStates.Count(e => e.State == ConnectionState.Open).Should().Be(1);

        // A deploy never unlinks the account: the second connect (same store, fresh
        // provider + connection) again selects the login payload with the same static.
        var secondStates = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        ResumeHandshakeResult second = await ConnectOnceAsync(secondStates);
        secondStates.Count(e => e.State == ConnectionState.Open).Should().Be(1);

        AssertLoginPayload(first.ClientFinishPayload, "the first connect selects login while a row exists");
        AssertLoginPayload(second.ClientFinishPayload, "the second connect again selects login while a row exists");
        Convert.ToHexString(first.InitiatorStaticPublic).Should().Be(expectedStaticHex, "the first handshake reuses the persisted static");
        Convert.ToHexString(second.InitiatorStaticPublic).Should().Be(expectedStaticHex, "the restarted handshake reuses the same persisted static");

        pairs.Created.Should().Be(2);
        await store.DisposeAsync();
    }
}
