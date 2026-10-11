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
using Iwasug.WhatsApp.Sdk.Testing.Rules;
using Iwasug.WhatsApp.Sdk.Tests.Pairing;
using Iwasug.WhatsApp.Sdk.Tests.Transport;
using Iwasug.WhatsApp.Sdk.Transport;
using NSec.Cryptography;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Connection;

/// <summary>
/// Tracer proofs (03-01, CONN-03 mechanism half): a restarted process resumes from its
/// own store — same Noise static key, login ClientPayload in message C,
/// <see cref="ConnectionState.Open"/> only after the server's <c>success</c>.
/// Plan 03-06 Task 1 adds the full durability lifecycle beside them: a
/// kill/restart resume over a genuinely paired row, 428/515 drop survival, and
/// the concurrent backstops (seed-06 grep anchors: kill_restart, drop_428,
/// drop_515, concurrent).
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

    // The paired identity the kill/restart lifecycle produces through the
    // verified pair-success drive (PairingSuccessFixture).
    private const string LifecyclePairedUser = "15551234567";
    private const uint LifecyclePairedDevice = 3;
    private const uint LifecyclePairedRegistrationId = 77;

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

    // ------------------------------------------------------------------
    // Plan 03-06 Task 1 helpers: one full connect over the shared store
    // image (fresh composition every time — the kill/restart unit), the
    // scripted peer echo loop for drop survival, and row byte-equality.
    // ------------------------------------------------------------------

    /// <summary>
    /// One full connect over the shared store: fresh payload provider +
    /// connection (nothing carried in memory), scripted loopback peer that
    /// captures the message-C payload server-side, <c>success</c> → Open, then
    /// client-owned teardown. The kill/restart lifecycle repeats this unit.
    /// </summary>
    private static async Task<ResumeHandshakeResult> ConnectOnceOverStoreAsync(
        PairSource pairs,
        AccountId account,
        ICredentialStore store,
        ConcurrentQueue<ConnectionStateChangedEventArgs> states,
        CancellationToken cancellationToken)
    {
        var payloads = new HandshakePayloadProvider(account, store, DummyRegistrationDefaults());
        var connection = new WaConnection(account, pairs.CreateClient, payloads, TracerOptions());
        connection.StateChanged += (_, e) => states.Enqueue(e);

        var captured = new TaskCompletionSource<ResumeHandshakeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, cancellationToken);
            ResumeHandshakeResult handshake = await CompleteResumePeerHandshakeAsync(
                peer,
                Encoding.ASCII.GetBytes("server-hello"),
                cancellationToken);
            captured.TrySetResult(handshake);
            await SendPeerSuccessAsync(peer, handshake.Transport, cancellationToken);
            // Hold the session until the client tears it down: EOF arrives when
            // the test disposes its connection, which completes this task.
            ReadOnlyMemory<byte>? tail = await peer.ReceiveAsync(cancellationToken);
            tail.Should().BeNull("the client owns teardown — the peer only observes it");
            handshake.Transport.Dispose();
            await peer.DisposeAsync();
        }, cancellationToken);

        await connection.ConnectAsync(cancellationToken);
        ResumeHandshakeResult result = await captured.Task.WaitAsync(cancellationToken);
        await connection.DisposeAsync();
        await peerTask;
        return result;
    }

    /// <summary>
    /// Serves up to <paramref name="maxQueries"/> framed queries with an echo
    /// reply over the established Noise session; returns on EOF (client
    /// teardown) or after the quota — the drop-injection scripts use the
    /// quota to close with a status code at an exact point.
    /// </summary>
    private static async Task RunPeerEchoLoopAsync(
        IFrameTransport peer,
        NoiseTransport noise,
        int maxQueries,
        CancellationToken cancellationToken)
    {
        int served = 0;
        while (served < maxQueries)
        {
            ReadOnlyMemory<byte>? frame = await peer.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                return;
            }

            ReadOnlySpan<byte> framed = WaFrame.Deframe(frame.Value.Span);
            byte[] plain = noise.Decrypt(framed.Slice(1));
            if (!QueryRouter.TryParseFrame(plain, out string? id, out byte[]? body) || id is null || body is null)
            {
                throw new InvalidOperationException("Peer received an unparsable query frame.");
            }

            byte[] response = QueryRouter.BuildFrame(id, body);
            byte[] encrypted = noise.Encrypt(response);
            byte[] flagged = new byte[encrypted.Length + 1];
            encrypted.CopyTo(flagged.AsSpan(1));
            await peer.SendAsync(WaFrame.Frame(flagged), cancellationToken).ConfigureAwait(false);
            served++;
        }
    }

    /// <summary>
    /// Byte-equality for the must-not-lose credential row (D-05): every field,
    /// with each key/secret compared as bytes — a record == would compare the
    /// arrays by reference and prove nothing.
    /// </summary>
    private static void AssertRowByteEqual(CredentialRecord? before, CredentialRecord? after, string because)
    {
        after.Should().NotBeNull(because);
        before.Should().NotBeNull(because);
        after!.CreatedAt.Should().Be(before!.CreatedAt, because);
        after.Platform.Should().Be(before.Platform, because);
        after.NoiseKeyPrivate.Should().Equal(before.NoiseKeyPrivate, because);
        after.IdentityKeyPrivate.Should().Equal(before.IdentityKeyPrivate, because);
        after.SigningKeyPrivate.Should().Equal(before.SigningKeyPrivate, because);
        after.AdvSecret.Should().Equal(before.AdvSecret, because);
        after.PairedUser.Should().Be(before.PairedUser, because);
        after.Device.Should().Be(before.Device, because);
        after.RegistrationId.Should().Be(before.RegistrationId, because);
    }

    private static CredentialRecord PairedRow(string user, int device, int registrationId, byte[] advSecret) =>
        new(
            new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero),
            "android",
            FixedBytes(0x01),
            FixedBytes(0x21),
            FixedBytes(0x41),
            advSecret,
            user,
            device,
            registrationId);

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

    /// <summary>
    /// Missing-row registration bootstrap (03-03 Task 3, CONN-01/CONN-03): with an
    /// empty store, loading credentials generates fresh identity material,
    /// persists it through <c>ICredentialStore</c> (awaited, D-06), hands the
    /// handshake the persisted static (never a throwaway), and builds the
    /// registration payload from the persisted row.
    /// </summary>
    [Fact]
    public async Task Resume_missing_row_generates_persists_and_builds_registration_from_row()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        AccountId account = NewAccount("registration");
        ICredentialStoreFactory factory = new InMemoryStoreFactory();
        ICredentialStore store = await factory.CreateAsync(account, testToken);
        var payloads = new HandshakePayloadProvider(account, store, DummyRegistrationDefaults());

        byte[]? staticKey = await payloads.LoadCredentialsAsync(testToken);
        staticKey.Should().NotBeNull("a missing row generates fresh identity instead of minting a throwaway");

        // The row is observable by an independent read before any payload exists.
        CredentialRecord? row = await store.GetAsync(testToken);
        row.Should().NotBeNull("generation persists before the handshake proceeds");
        staticKey.Should().Equal(row!.NoiseKeyPrivate, "the handshake uses the persisted static");
        row.PairedUser.Should().BeNull("fresh material is unpaired until pair-success");

        byte[] finish = payloads.GetClientFinish();
        ClientPayload payload = ClientPayload.Parser.ParseFrom(finish);
        payload.Passive.Should().BeFalse("registration is not passive");
        payload.Pull.Should().BeFalse("registration does not pull");
        payload.DevicePairingData.Should().NotBeNull("registration carries device pairing data");
        payload.DevicePairingData!.EIdent.Should().Equal(
            ExpectedStaticPublic(row.IdentityKeyPrivate),
            "registration carries the persisted identity public (never the injected default)");
        payload.DevicePairingData.ERegid.Should().Equal(
            ToBigEndian4(row.RegistrationId),
            "registration carries the persisted registration id");
        payload.DevicePairingData.ESkeyVal.Should().Equal(
            FixedBytes(0x31),
            "the signed-prekey triple stays on the injected defaults until prekey lifecycle lands");
        payload.Username.Should().Be(0, "registration carries no paired username");

        await store.DisposeAsync();
    }

    private static byte[] ExpectedStaticPublic(byte[] staticPrivate)
    {
        byte[] copy = (byte[])staticPrivate.Clone();
        try
        {
            using Key key = Key.Import(KeyAgreementAlgorithm.X25519, copy, KeyBlobFormat.RawPrivateKey);
            return key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    private static byte[] ToBigEndian4(int value) =>
        [(byte)((value >> 24) & 0xFF), (byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF)];

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

    /// <summary>
    /// Plan 03-06 Task 1, Tests 1+2 (kill_restart, CONN-03): the first run
    /// registers — the ONE registration payload of the whole lifecycle — the
    /// verified pair-success path pairs the row, then a kill (dispose) and a
    /// fresh composition over the same store resume as login, twice in a row.
    /// Registration payload count across the three connects is exactly one;
    /// pairing is never re-entered and the paired row survives byte-equal.
    /// </summary>
    [Fact]
    public async Task Resume_after_kill_restart_reselects_login_with_exactly_one_registration_payload()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        AccountId account = NewAccount("kill-restart");
        ICredentialStoreFactory factory = new InMemoryStoreFactory();
        ICredentialStore store = await factory.CreateAsync(account, testToken);

        // Run 1: no row exists — this connect carries the lifecycle's single
        // registration payload and persists the unpaired row.
        ResumeHandshakeResult first = await ConnectOnceOverStoreAsync(pairs, account, store, states, testToken);
        ClientPayload firstPayload = ClientPayload.Parser.ParseFrom(first.ClientFinishPayload);
        firstPayload.DevicePairingData.Should().NotBeNull("an empty store can only register");
        int registrationPayloads = 1;
        CredentialRecord? unpaired = await store.GetAsync(testToken);
        unpaired.Should().NotBeNull("the first run persists its row before the restart");
        unpaired!.PairedUser.Should().BeNull("the first run persists an unpaired row");

        // The verified pair-success path over the SAME store image — the shared
        // drive pairs through the production coordinator's verify/write path.
        PairingStateChangedEventArgs success = await PairingSuccessFixture.DrivePairSuccessAsync(
            account,
            store,
            LifecyclePairedUser,
            LifecyclePairedDevice,
            LifecyclePairedRegistrationId,
            testToken);
        success.Payload.Should().Be(LifecyclePairedUser, "the Success event carries the verified paired user");

        CredentialRecord? paired = await store.GetAsync(testToken);
        paired.Should().NotBeNull();
        paired!.PairedUser.Should().Be(LifecyclePairedUser, "pair-success wrote the paired row");
        paired.Device.Should().Be((int)LifecyclePairedDevice, "pair-success wrote the paired device");
        string resumedStaticHex = ExpectedStaticPublicHex(paired.NoiseKeyPrivate);

        // The kill: both restarts are fresh compositions over the same durable
        // state — no pairing coordinator, no in-memory carry-over.
        ResumeHandshakeResult second = await ConnectOnceOverStoreAsync(pairs, account, store, states, testToken);
        ResumeHandshakeResult third = await ConnectOnceOverStoreAsync(pairs, account, store, states, testToken);

        ClientPayload secondPayload = ClientPayload.Parser.ParseFrom(second.ClientFinishPayload);
        ClientPayload thirdPayload = ClientPayload.Parser.ParseFrom(third.ClientFinishPayload);
        secondPayload.DevicePairingData.Should().BeNull("restart 1 resumes from the paired row — never re-registers");
        thirdPayload.DevicePairingData.Should().BeNull("restart 2 resumes from the paired row — never re-registers");
        registrationPayloads += (secondPayload.DevicePairingData is null ? 0 : 1)
            + (thirdPayload.DevicePairingData is null ? 0 : 1);
        registrationPayloads.Should().Be(1, "registration payload count must be exactly ONE across the three connects");

        foreach ((ResumeHandshakeResult result, ClientPayload payload) in new[]
        {
            (second, secondPayload),
            (third, thirdPayload),
        })
        {
            payload.Username.Should().Be(15551234567UL, "every restart re-selects the paired login");
            payload.Device.Should().Be(LifecyclePairedDevice, "the login carries the paired device");
            Convert.ToHexString(result.InitiatorStaticPublic).Should().Be(
                resumedStaticHex, "every restart resumes on the same persisted static identity");
        }

        states.Count(e => e.State == ConnectionState.Open).Should().Be(3, "one open per connect");
        pairs.Created.Should().Be(3, "one handshake per connect — pairing never opened a second channel");

        CredentialRecord? after = await store.GetAsync(testToken);
        AssertRowByteEqual(paired, after, "restarts never rewrite the paired row");

        await store.DisposeAsync();
    }

    /// <summary>
    /// Plan 03-06 Task 1, Test 3 (drop_428): a 428 while Open follows the
    /// locked Phase-2 sequence — Closing(428), never LoggedOut — then
    /// reconnects with a fresh handshake; the credential row is byte-equal
    /// across the drop and the post-drop round-trip proves the session
    /// survived. The pre-existing Phase-C 428-drop timeout (STATE.md open
    /// item) is exactly what this scripted wait exercises deterministically.
    /// </summary>
    [Fact]
    public async Task Resume_survives_drop_428_with_closing_428_fresh_handshake_and_byte_equal_row()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        AccountId account = NewAccount("drop-428");
        ICredentialStoreFactory factory = new InMemoryStoreFactory();
        ICredentialStore store = await factory.CreateAsync(account, testToken);
        CredentialRecord seed = SeedRow();
        await store.PutAsync(seed, testToken);

        var payloads = new HandshakePayloadProvider(account, store, DummyRegistrationDefaults());
        var connection = new WaConnection(account, pairs.CreateClient, payloads, TracerOptions());
        connection.StateChanged += (_, e) => states.Enqueue(e);

        var handshakes = new ConcurrentQueue<ResumeHandshakeResult>();
        Task peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport first = await TakePeerAsync(pairs, testToken);
            ResumeHandshakeResult firstHandshake = await CompleteResumePeerHandshakeAsync(
                first,
                Encoding.ASCII.GetBytes("server-hello"),
                testToken);
            handshakes.Enqueue(firstHandshake);
            await SendPeerSuccessAsync(first, firstHandshake.Transport, testToken);
            await RunPeerEchoLoopAsync(first, firstHandshake.Transport, 1, testToken);
            await first.CloseAsync(428, testToken);

            LoopbackFrameTransport second = await TakePeerAsync(pairs, testToken);
            ResumeHandshakeResult secondHandshake = await CompleteResumePeerHandshakeAsync(
                second,
                Encoding.ASCII.GetBytes("server-hello"),
                testToken);
            handshakes.Enqueue(secondHandshake);
            await SendPeerSuccessAsync(second, secondHandshake.Transport, testToken);
            await RunPeerEchoLoopAsync(second, secondHandshake.Transport, int.MaxValue, testToken);
            await second.DisposeAsync();
        }, testToken);

        await connection.ConnectAsync(testToken);
        byte[] beforeDrop = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("before-drop"), testToken);
        beforeDrop.Should().Equal(Encoding.ASCII.GetBytes("before-drop"));

        await WaitForQueuedAsync(
            states,
            e => e.State == ConnectionState.Open && states.Count(s => s.State == ConnectionState.Open) >= 2,
            "re-open after the 428 drop");

        byte[] afterDrop = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("after-drop"), testToken);
        afterDrop.Should().Equal(Encoding.ASCII.GetBytes("after-drop"), "the session survives the 428 drop");

        // The locked Phase-2 428 semantics: Closing(428) then retry, never a
        // logout outcome — and the retry lands on a fresh transport.
        states.Single(e => e.State == ConnectionState.Closing).StatusCode.Should().Be(428, "428 surfaces as Closing(428)");
        states.Any(e => e.State == ConnectionState.LoggedOut).Should().BeFalse("428 is a retry outcome — never a logout");
        ConnectionStateChangedEventArgs[] connectings = states.Where(e => e.State == ConnectionState.Connecting).ToArray();
        connectings.Should().HaveCount(2, "one connecting per handshake attempt");
        connectings[1].Attempt.Should().Be(1, "428 retries with an advanced backoff attempt");
        states.Count(e => e.State == ConnectionState.Open).Should().Be(2, "open, drop, re-open");
        pairs.Created.Should().Be(2, "the reconnect mints a fresh transport and handshake");

        int registrationPayloads = 0;
        foreach (ResumeHandshakeResult handshake in handshakes)
        {
            if (ClientPayload.Parser.ParseFrom(handshake.ClientFinishPayload).DevicePairingData is not null)
            {
                registrationPayloads++;
            }
        }

        registrationPayloads.Should().Be(0, "every handshake on this paired account logs in — the drop never re-registers");

        CredentialRecord? rowAfter = await store.GetAsync(testToken);
        AssertRowByteEqual(seed, rowAfter, "the 428 drop and its reconnect must not disturb the credential row");

        await connection.DisposeAsync();
        await peerTask;
        await store.DisposeAsync();
    }

    /// <summary>
    /// Plan 03-06 Task 1, Test 6 (drop_515): a 515 while Open restarts
    /// immediately from persisted state — Closing(515) with the backoff
    /// counter reset (the second Connecting carries attempt 0, unlike 428's
    /// attempt 1), zero re-registration, a byte-equal credential row, and a
    /// post-restart round-trip. This is the VALIDATION row 03-06-T1
    /// "<c>+ 515</c>" half.
    /// </summary>
    [Fact]
    public async Task Resume_survives_drop_515_with_immediate_restart_persisted_state_and_byte_equal_row()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        AccountId account = NewAccount("drop-515");
        ICredentialStoreFactory factory = new InMemoryStoreFactory();
        ICredentialStore store = await factory.CreateAsync(account, testToken);
        CredentialRecord seed = SeedRow();
        await store.PutAsync(seed, testToken);

        var payloads = new HandshakePayloadProvider(account, store, DummyRegistrationDefaults());
        var connection = new WaConnection(account, pairs.CreateClient, payloads, TracerOptions());
        connection.StateChanged += (_, e) => states.Enqueue(e);

        var handshakes = new ConcurrentQueue<ResumeHandshakeResult>();
        Task peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport first = await TakePeerAsync(pairs, testToken);
            ResumeHandshakeResult firstHandshake = await CompleteResumePeerHandshakeAsync(
                first,
                Encoding.ASCII.GetBytes("server-hello"),
                testToken);
            handshakes.Enqueue(firstHandshake);
            await SendPeerSuccessAsync(first, firstHandshake.Transport, testToken);
            await RunPeerEchoLoopAsync(first, firstHandshake.Transport, 1, testToken);
            await first.CloseAsync(515, testToken);

            LoopbackFrameTransport second = await TakePeerAsync(pairs, testToken);
            ResumeHandshakeResult secondHandshake = await CompleteResumePeerHandshakeAsync(
                second,
                Encoding.ASCII.GetBytes("server-hello"),
                testToken);
            handshakes.Enqueue(secondHandshake);
            await SendPeerSuccessAsync(second, secondHandshake.Transport, testToken);
            await RunPeerEchoLoopAsync(second, secondHandshake.Transport, int.MaxValue, testToken);
            await second.DisposeAsync();
        }, testToken);

        await connection.ConnectAsync(testToken);
        byte[] beforeDrop = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("before-drop"), testToken);
        beforeDrop.Should().Equal(Encoding.ASCII.GetBytes("before-drop"));

        await WaitForQueuedAsync(
            states,
            e => e.State == ConnectionState.Open && states.Count(s => s.State == ConnectionState.Open) >= 2,
            "immediate restart after the 515 drop");

        byte[] afterDrop = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("after-drop"), testToken);
        afterDrop.Should().Equal(Encoding.ASCII.GetBytes("after-drop"), "the restarted session round-trips");

        // The Phase-2 515 → RestartImmediate classification: closing carries
        // 515 and the counter resets — the second Connecting is attempt 0.
        states.Single(e => e.State == ConnectionState.Closing).StatusCode.Should().Be(515, "515 surfaces as Closing(515)");
        ConnectionStateChangedEventArgs[] connectings = states.Where(e => e.State == ConnectionState.Connecting).ToArray();
        connectings.Should().HaveCount(2, "one connecting per handshake attempt");
        connectings[1].Attempt.Should().Be(0, "515 restarts immediately with the backoff counter reset");
        states.Count(e => e.State == ConnectionState.Open).Should().Be(2, "open, 515 restart, re-open");
        pairs.Created.Should().Be(2, "the restart mints a fresh transport and handshake from persisted state");

        int registrationPayloads = 0;
        foreach (ResumeHandshakeResult handshake in handshakes)
        {
            if (ClientPayload.Parser.ParseFrom(handshake.ClientFinishPayload).DevicePairingData is not null)
            {
                registrationPayloads++;
            }
        }

        registrationPayloads.Should().Be(0, "the 515 restart re-handshakes from the paired row — pairing is never re-entered");

        CredentialRecord? rowAfter = await store.GetAsync(testToken);
        AssertRowByteEqual(seed, rowAfter, "the 515 restart must not disturb the credential row");

        await connection.DisposeAsync();
        await peerTask;
        await store.DisposeAsync();
    }

    /// <summary>
    /// Plan 03-06 Task 1, Test 5 (concurrent, concurrency_backstop): two
    /// overlapping ConnectAsync calls race through the startup gate from a
    /// shared start gate — exactly one runs (single handshake, single open),
    /// the loser settles with the descriptive already-connected outcome rather
    /// than a wedge, and no duplicate registration payload is ever emitted.
    /// </summary>
    [Fact]
    public async Task Resume_concurrent_connects_complete_one_handshake_without_duplicate_registration()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        AccountId account = NewAccount("concurrent-connect");
        ICredentialStoreFactory factory = new InMemoryStoreFactory();
        ICredentialStore store = await factory.CreateAsync(account, testToken);
        CredentialRecord seed = SeedRow();
        await store.PutAsync(seed, testToken);

        var payloads = new HandshakePayloadProvider(account, store, DummyRegistrationDefaults());
        var connection = new WaConnection(account, pairs.CreateClient, payloads, TracerOptions());
        connection.StateChanged += (_, e) => states.Enqueue(e);

        var captured = new TaskCompletionSource<ResumeHandshakeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            ResumeHandshakeResult handshake = await CompleteResumePeerHandshakeAsync(
                peer,
                Encoding.ASCII.GetBytes("server-hello"),
                testToken);
            captured.TrySetResult(handshake);
            await SendPeerSuccessAsync(peer, handshake.Transport, testToken);
            ReadOnlyMemory<byte>? tail = await peer.ReceiveAsync(testToken);
            tail.Should().BeNull("the client owns teardown — the peer only observes it");
            handshake.Transport.Dispose();
            await peer.DisposeAsync();
        }, testToken);

        // Both callers leave the shared gate together, so the startup-gate
        // race is real rather than sequential-by-accident.
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task ConnectRacingAsync() => Task.Run(async () =>
        {
            await startGate.Task.WaitAsync(testToken);
            await connection.ConnectAsync(testToken);
        }, testToken);

        Task racerA = ConnectRacingAsync();
        Task racerB = ConnectRacingAsync();
        startGate.TrySetResult();

        int opened = 0;
        int alreadyConnected = 0;
        foreach (Task racer in new[] { racerA, racerB })
        {
            try
            {
                await racer;
                opened++;
            }
            catch (InvalidOperationException ex)
            {
                ex.Message.Should().Contain(
                    "already been called", "the losing caller gets a descriptive outcome, not a wedge");
                alreadyConnected++;
            }
        }

        opened.Should().Be(1, "exactly one caller wins the startup gate");
        alreadyConnected.Should().Be(1, "both callers settle — one opens, one is told it already connected");

        ResumeHandshakeResult handshakeResult = await captured.Task.WaitAsync(testToken);
        ClientPayload payload = ClientPayload.Parser.ParseFrom(handshakeResult.ClientFinishPayload);
        payload.DevicePairingData.Should().BeNull(
            "zero-duplicate registration: the raced connects re-handshake from the paired row only");

        states.Count(e => e.State == ConnectionState.Open).Should().Be(1, "a single handshake opened exactly once");
        pairs.Created.Should().Be(1, "overlapping connects share one in-flight handshake");
        connection.State.Should().Be(ConnectionState.Open, "no wedge — the winner stays open");

        await connection.DisposeAsync();
        await peerTask;
        await store.DisposeAsync();
    }

    /// <summary>
    /// Plan 03-06 Task 1, Test 4 (concurrent, concurrency_backstop): the store
    /// contract's real-parallelism helper against the credential binding on
    /// POST-PAIRING rows — account A's paired row is written 100 times while
    /// account B's instance reads concurrently; B must never observe A's row
    /// or a torn row of its own (D-11/T-01-10).
    /// </summary>
    [Fact]
    public async Task Resume_concurrent_store_access_on_paired_rows_has_no_cross_reads_or_torn_rows()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        CancellationToken testToken = testCts.Token;
        ICredentialStoreFactory factory = new InMemoryStoreFactory();
        AccountId accountA = NewAccount("concurrent-store-a");
        AccountId accountB = NewAccount("concurrent-store-b");

        // B starts from its own post-pairing row; A writes a DIFFERENT paired
        // row (distinct user, device, and adv secret) throughout the run.
        const string ownUser = "999988887777";
        const int ownDevice = 9;
        byte[] ownAdvSecret = FixedBytes(0x77);
        ICredentialStore seedB = await factory.CreateAsync(accountB, testToken);
        await seedB.PutAsync(PairedRow(ownUser, ownDevice, 7777, ownAdvSecret), testToken);
        await seedB.DisposeAsync();

        await StoreContractRules.AssertParallelAccessNoCrossReadsAsync<ICredentialStore>(
            createStoreAsync: accountId => factory.CreateAsync(accountId, CancellationToken.None),
            accountA: accountA,
            accountB: accountB,
            writeOnAAsync: (writer, i) => writer.PutAsync(
                PairedRow("15551234567", 1 + (i % 50), 42 + (i % 100), FixedBytes(0x01)),
                CancellationToken.None),
            readLeakOnBAsync: async reader =>
            {
                CredentialRecord? row = await reader.GetAsync(CancellationToken.None);
                return row is null
                    || row.PairedUser != ownUser
                    || row.Device != ownDevice
                    || row.RegistrationId != 7777
                    || row.AdvSecret is null
                    || !row.AdvSecret.SequenceEqual(ownAdvSecret);
            },
            iterations: 100);

        // B's row is still intact after the interleaved run — the helper proved
        // no cross-reads; this proves no deferred corruption either.
        ICredentialStore verifyB = await factory.CreateAsync(accountB, testToken);
        CredentialRecord? finalB = await verifyB.GetAsync(testToken);
        finalB.Should().NotBeNull();
        finalB!.PairedUser.Should().Be(ownUser, "B's post-pairing row survives concurrent foreign writes");
        finalB.Device.Should().Be(ownDevice);
        finalB.AdvSecret.Should().Equal(ownAdvSecret);
        await verifyB.DisposeAsync();
    }
}
