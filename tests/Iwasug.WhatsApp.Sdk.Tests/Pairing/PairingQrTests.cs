using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Noise;
using Iwasug.WhatsApp.Sdk.Pairing;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.InMemory;
using Iwasug.WhatsApp.Sdk.Tests.Transport;
using Iwasug.WhatsApp.Sdk.Transport;
using Microsoft.Extensions.Time.Testing;
using NSec.Cryptography;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Pairing;

/// <summary>
/// Pairing-surface proofs (03-03 Task 1, CONN-01): the QR payload composes the
/// exact wire string, rotation/timeout follow the shared constants on an injected
/// clock (never wall time), the reference stays opaque on the public surface, and
/// the status vocabulary is complete.
/// </summary>
/// <remarks>
/// <para>
/// Fixture discipline: the expected wire string below is a literal pinned against
/// independently-computed base64 (not produced by the type under test) — a join
/// order or prefix edit fails here, not on a live phone. Fixed inputs are public
/// fixture material only; assertion messages never carry key bytes.
/// </para>
/// </remarks>
public sealed class PairingQrTests
{
    private const string ExpectedWireString =
        "https://wa.me/settings/linked_devices#" +
        "TEST-REF-01," +
        "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=," +
        "ISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+P0A=," +
        "QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVpbXF1eX2A=," +
        "android";

    private static byte[] FixedBytes(int start, int length = 32)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(start + i);
        }

        return bytes;
    }

    [Fact]
    public void Build_composes_the_exact_wire_string()
    {
        string actual = PairingQrPayload.Build(
            "TEST-REF-01",
            FixedBytes(0x01),
            FixedBytes(0x21),
            FixedBytes(0x41),
            "android");

        actual.Should().Be(
            ExpectedWireString,
            "the host renders this exact string — join order and prefix are the wire contract");
    }

    [Fact]
    public void Rotation_policy_first_code_60s_then_20s_with_160s_timeout()
    {
        // The clock is injected (FakeTimeProvider): instants flow into the pure
        // schedule — this test never sleeps real time.
        var clock = new FakeTimeProvider();
        DateTimeOffset start = clock.GetUtcNow();

        PairingQrPayload.FirstCodeLifetime.Should().Be(TimeSpan.FromSeconds(60));
        PairingQrPayload.RotationInterval.Should().Be(TimeSpan.FromSeconds(20));
        PairingQrPayload.AttemptTimeout.Should().Be(TimeSpan.FromSeconds(160));

        PairingQrPayload.RotationFor(0).Should().Be(TimeSpan.FromSeconds(60));
        PairingQrPayload.RotationFor(1).Should().Be(TimeSpan.FromSeconds(20));
        PairingQrPayload.RotationFor(5).Should().Be(TimeSpan.FromSeconds(20));

        PairingQrPayload.CodeExpiry(start, 0).Should().Be(start + TimeSpan.FromSeconds(60));
        PairingQrPayload.CodeExpiry(start, 1).Should().Be(start + TimeSpan.FromSeconds(20));

        PairingQrPayload.AttemptExpired(start, start + TimeSpan.FromSeconds(159)).Should().BeFalse();
        PairingQrPayload.AttemptExpired(start, start + TimeSpan.FromSeconds(160)).Should().BeTrue();
    }

    [Fact]
    public void Build_passes_the_reference_through_opaque_with_no_interpreting_member()
    {
        const string trickyRef = "ref,with,commas#and-fragments";
        string payload = PairingQrPayload.Build(
            trickyRef,
            FixedBytes(0x01),
            FixedBytes(0x21),
            FixedBytes(0x41),
            "android");

        payload.Should().Contain(
            PairingQrPayload.WirePrefix + trickyRef + ",",
            "the reference travels byte-for-byte from input to string");

        // The opacity boundary (T-3-06, CONN-01 requirement): no public member may
        // split, decode, or parse the reference — asserted on the public surface.
        string[] forbiddenFragments = ["Split", "Decode", "Parse"];
        MemberInfo[] members = typeof(PairingQrPayload).GetMembers(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
        foreach (MemberInfo member in members)
        {
            foreach (string fragment in forbiddenFragments)
            {
                member.Name.Should().NotContain(
                    fragment,
                    $"public member {member.Name} must not interpret the QR reference");
            }
        }
    }

    [Fact]
    public void PairingStatus_carries_exactly_five_states_with_status_and_payload()
    {
        string[] names = Enum.GetNames<PairingStatus>();
        names.Should().HaveCount(5, "the host observes every pairing outcome through this one vocabulary");
        foreach (string expected in new[] { "Code", "Success", "Timeout", "LoggedOut", "ClientOutdated" })
        {
            names.Should().Contain(expected);
        }

        var args = new PairingStateChangedEventArgs(PairingStatus.Code, ExpectedWireString);
        args.Status.Should().Be(PairingStatus.Code);
        args.Payload.Should().Be(ExpectedWireString);

        var terminal = new PairingStateChangedEventArgs(PairingStatus.Timeout, null);
        terminal.Status.Should().Be(PairingStatus.Timeout);
        terminal.Payload.Should().BeNull();
    }

    [Fact]
    public void Rotation_over_time_issues_codes_on_schedule_and_ends_in_timeout()
    {
        // Drive the injected clock through 0+60+20+20+20+20+20 = 160 seconds: six
        // codes on schedule, then the attempt ends at the timeout boundary.
        var clock = new FakeTimeProvider();
        DateTimeOffset start = clock.GetUtcNow();
        DateTimeOffset now = start;

        TimeSpan[] expectedLifetimes =
        [
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(20),
        ];

        for (int codeIndex = 0; codeIndex < expectedLifetimes.Length; codeIndex++)
        {
            PairingQrPayload.AttemptExpired(start, now).Should().BeFalse(
                $"code {codeIndex} is still inside the attempt window");
            TimeSpan lifetime = PairingQrPayload.RotationFor(codeIndex);
            lifetime.Should().Be(expectedLifetimes[codeIndex]);
            DateTimeOffset expiry = PairingQrPayload.CodeExpiry(now, codeIndex);
            expiry.Should().Be(now + expectedLifetimes[codeIndex]);

            // A code payload builds at every step of the schedule.
            string payload = PairingQrPayload.Build(
                $"REF-{codeIndex}",
                FixedBytes(0x01),
                FixedBytes(0x21),
                FixedBytes(0x41),
                "android");
            payload.Should().StartWith(PairingQrPayload.WirePrefix);

            clock.Advance(lifetime);
            now = clock.GetUtcNow();
        }

        now.Should().Be(start + PairingQrPayload.AttemptTimeout);
        PairingQrPayload.AttemptExpired(start, now).Should().BeTrue(
            "the attempt ends at the timeout boundary with the Timeout status");
    }

    [Fact]
    public void Build_rejects_malformed_inputs_without_echoing_material()
    {
        Action emptyRef = () => PairingQrPayload.Build(
            string.Empty, FixedBytes(0x01), FixedBytes(0x21), FixedBytes(0x41), "android");
        emptyRef.Should().Throw<SdkFormatException>();

        Action shortNoise = () => PairingQrPayload.Build(
            "ref", FixedBytes(0x01, 16), FixedBytes(0x21), FixedBytes(0x41), "android");
        shortNoise.Should().Throw<SdkFormatException>();

        Action shortAdv = () => PairingQrPayload.Build(
            "ref", FixedBytes(0x01), FixedBytes(0x21), FixedBytes(0x41, 16), "android");
        shortAdv.Should().Throw<SdkFormatException>();

        Action emptyPlatform = () => PairingQrPayload.Build(
            "ref", FixedBytes(0x01), FixedBytes(0x21), FixedBytes(0x41), string.Empty);
        emptyPlatform.Should().Throw<SdkFormatException>();
    }

    // ------------------------------------------------------------------
    // Task 2 (tracer): PairingCoordinator on the pump seam — code emission,
    // state stream, single-attempt rule, 405/503 pairing outcomes.
    //
    // Scripted stanza shapes (repo-local, A1): request
    // <iq id type="get"><pair-device/></iq>, ref reply
    // <iq id type="result"><pair-device ref="…"/></iq> (ref as attr: content-slot
    // raw strings decode as Data bytes, attrs roundtrip losslessly as strings),
    // pairing-level error <iq id type="error"><error code/></iq>, success
    // <notification><pair-success user/></notification>. The loopback fake is
    // the executable definition; the end-of-phase human proof confirms them.
    // ------------------------------------------------------------------

    private static readonly TimeSpan PairingWaitTimeout = TimeSpan.FromSeconds(20);

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

    private sealed class CountingFetcher : IWaVersionFetcher
    {
        internal int Calls;

        private long _revision = 1043857760;

        public Task<string> FetchSwJsAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            long revision = Interlocked.Increment(ref _revision);
            return Task.FromResult($"{{\"client_revision\":{revision}}}");
        }
    }

    private static AccountId NewPairingAccount(string hint) =>
        AccountId.Create($"pairing-{hint}-" + Guid.NewGuid().ToString("N"));

    private static ConnectionOptions PairingOptions() => new()
    {
        ConnectTimeoutMs = 3_600_000,
        QueryTimeoutMs = 5_000,
        RetryBaseDelayMs = 1_000,
        RetryMaxDelayMs = 60_000,
    };

    private static RegistrationDefaults PairingRegistrationDefaults() =>
        new(
            FixedBytes(0x11),
            7,
            FixedBytes(0x31),
            FixedBytes(0xA0, 64),
            42,
            "android",
            2,
            3000,
            1043857760);

    private static CredentialRecord UnpairedSeedRow() =>
        new(
            DateTimeOffset.UtcNow,
            "android",
            FixedBytes(0x01),
            FixedBytes(0x21),
            FixedBytes(0x41),
            FixedBytes(0x61),
            null,
            0,
            42);

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
        ReadOnlyMemory<byte>? frame = await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Peer transport closed unexpectedly.");

        return frame.Value.ToArray();
    }

    private static async Task<NoiseTransport> CompletePeerHandshakeAsync(
        IFrameTransport peer,
        CancellationToken cancellationToken)
    {
        byte[] header = await ReceiveOrThrowAsync(peer, cancellationToken).ConfigureAwait(false);
        NoiseHandshake.ValidateIntroHeader(header);

        byte[] messageAFrame = await ReceiveOrThrowAsync(peer, cancellationToken).ConfigureAwait(false);
        using var responder = new NoiseHandshakeResponder(Encoding.ASCII.GetBytes("server-hello"));
        byte[] messageA = WaFrame.Deframe(messageAFrame).ToArray();
        responder.ReadMessageA(messageA);
        byte[] messageB = responder.WriteMessageB();
        await peer.SendAsync(WaFrame.Frame(messageB), cancellationToken).ConfigureAwait(false);

        byte[] messageCFrame = await ReceiveOrThrowAsync(peer, cancellationToken).ConfigureAwait(false);
        byte[] messageC = WaFrame.Deframe(messageCFrame).ToArray();
        return responder.ReadMessageC(messageC);
    }

    private static async Task<BinaryNode> ReceivePeerNodeAsync(
        IFrameTransport peer,
        NoiseTransport noise,
        CancellationToken cancellationToken)
    {
        byte[] encrypted = await ReceiveOrThrowAsync(peer, cancellationToken).ConfigureAwait(false);
        ReadOnlySpan<byte> framed = WaFrame.Deframe(encrypted);
        byte[] plain = noise.Decrypt(framed.Slice(1));
        return BinaryNodeCodec.Decode(plain);
    }

    private static async Task SendPeerNodeAsync(
        IFrameTransport peer,
        NoiseTransport noise,
        BinaryNode node,
        CancellationToken cancellationToken)
    {
        byte[] encrypted = noise.Encrypt(BinaryNodeCodec.Encode(node));
        byte[] flagged = new byte[encrypted.Length + 1];
        encrypted.CopyTo(flagged.AsSpan(1));
        await peer.SendAsync(WaFrame.Frame(flagged), cancellationToken).ConfigureAwait(false);
    }

    private static BinaryNode RefReply(string id, string pairingRef) =>
        BinaryNode.FromChildren(
            "iq",
            [BinaryNode.Create("pair-device", new Dictionary<string, string> { ["ref"] = pairingRef })],
            new Dictionary<string, string> { ["id"] = id, ["type"] = "result" });

    private static BinaryNode ErrorReply(string id, string code) =>
        BinaryNode.FromChildren(
            "iq",
            [BinaryNode.Create("error", new Dictionary<string, string> { ["code"] = code })],
            new Dictionary<string, string> { ["id"] = id, ["type"] = "error" });

    private static BinaryNode PairSuccessNotification(string user) =>
        BinaryNode.FromChildren(
            "notification",
            [BinaryNode.Create("pair-success", new Dictionary<string, string> { ["user"] = user })]);

    private static BinaryNode FailureNode(string reason) =>
        BinaryNode.Create("failure", new Dictionary<string, string> { ["reason"] = reason });

    private static async Task<PairingStateChangedEventArgs> WaitForPairingAsync(
        ConcurrentQueue<PairingStateChangedEventArgs> events,
        Func<PairingStateChangedEventArgs, bool> predicate,
        string what)
    {
        using var timeout = new CancellationTokenSource(PairingWaitTimeout);
        while (!timeout.IsCancellationRequested)
        {
            foreach (PairingStateChangedEventArgs e in events)
            {
                if (predicate(e))
                {
                    return e;
                }
            }

            await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for pairing event: {what}.");
    }

    private static byte[] DeriveX25519Public(byte[] privateKey)
    {
        byte[] copy = (byte[])privateKey.Clone();
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

    private static string ExpectedQrFor(CredentialRecord seed, string pairingRef) =>
        PairingQrPayload.Build(
            pairingRef,
            DeriveX25519Public(seed.NoiseKeyPrivate),
            DeriveX25519Public(seed.IdentityKeyPrivate),
            seed.AdvSecret,
            seed.Platform);

    private static async Task<(WaConnection Connection, PairingCoordinator Pairing, ICredentialStore Store, CredentialRecord Seed)>
        CreatePairingConnectionAsync(
            AccountId account,
            PairSource pairs,
            FakeTimeProvider clock,
            CancellationToken cancellationToken)
    {
        ICredentialStoreFactory factory = new InMemoryStoreFactory();
        ICredentialStore store = await factory.CreateAsync(account, cancellationToken).ConfigureAwait(false);
        CredentialRecord seed = UnpairedSeedRow();
        await store.PutAsync(seed, cancellationToken).ConfigureAwait(false);
        var payloads = new HandshakePayloadProvider(account, store, PairingRegistrationDefaults());
        var connection = new WaConnection(account, pairs.CreateClient, payloads, PairingOptions(), clock);
        return (connection, connection.Pairing, store, seed);
    }

    private static async Task TeardownPairingAsync(
        Task pairingTask,
        Task connectTask,
        Task peerTask,
        WaConnection connection,
        ICredentialStore store,
        CancellationTokenSource testCts)
    {
        testCts.Cancel();
        try
        {
            await pairingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (SdkException)
        {
        }

        try
        {
            await connectTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (SdkException)
        {
        }

        await peerTask.ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
        await store.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task WaitForConnectionAsync(
        ConcurrentQueue<ConnectionStateChangedEventArgs> states,
        Func<ConnectionStateChangedEventArgs, bool> predicate,
        string what)
    {
        using var timeout = new CancellationTokenSource(PairingWaitTimeout);
        while (!timeout.IsCancellationRequested)
        {
            foreach (ConnectionStateChangedEventArgs e in states)
            {
                if (predicate(e))
                {
                    return;
                }
            }

            await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for connection event: {what}.");
    }

    private static async Task HoldPeerTillDoneAsync(IFrameTransport peer, CancellationToken cancellationToken)
    {
        try
        {
            await peer.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        await peer.DisposeAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task Pairing_first_request_emits_code_with_server_ref_opaque()
    {
        var clock = new FakeTimeProvider();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var events = new ConcurrentQueue<PairingStateChangedEventArgs>();
        AccountId account = NewPairingAccount("first-code");

        var composed = await CreatePairingConnectionAsync(account, pairs, clock, testToken);
        await using WaConnection connection = composed.Connection;
        PairingCoordinator pairing = composed.Pairing;
        ICredentialStore store = composed.Store;
        pairing.StateChanged += (_, e) => events.Enqueue(e);

        var handshakeDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            NoiseTransport noise = await CompletePeerHandshakeAsync(peer, testToken);
            handshakeDone.TrySetResult();
            try
            {
                BinaryNode request = await ReceivePeerNodeAsync(peer, noise, testToken);
                request.Tag.Should().Be("iq", "the coordinator sends a pair-device iq stanza");
                await SendPeerNodeAsync(peer, noise, RefReply(request.Attrs["id"], "REF-1"), testToken);
                await HoldPeerTillDoneAsync(peer, testToken);
            }
            finally
            {
                noise.Dispose();
            }
        }, testToken);

        Task connectTask = connection.ConnectAsync(testToken);
        Task pairingTask = pairing.StartAsync(testToken);
        await handshakeDone.Task.WaitAsync(testToken);

        PairingStateChangedEventArgs code = await WaitForPairingAsync(
            events, e => e.Status == PairingStatus.Code, "first code");
        code.Payload.Should().StartWith(
            PairingQrPayload.WirePrefix, "the host renders the documented wire string");
        code.Payload.Should().Contain(
            "REF-1", "the server-scripted ref travels opaque from server to host");
        code.Payload.Should().Be(
            ExpectedQrFor(composed.Seed, "REF-1"),
            "the QR carries the persisted row's keys, never invented material");

        await TeardownPairingAsync(pairingTask, connectTask, peerTask, connection, store, testCts);
    }

    [Fact]
    public async Task Pairing_rotation_reissues_on_shared_constants_with_new_refs()
    {
        var clock = new FakeTimeProvider();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var events = new ConcurrentQueue<PairingStateChangedEventArgs>();
        AccountId account = NewPairingAccount("rotation");

        var composed = await CreatePairingConnectionAsync(account, pairs, clock, testToken);
        await using WaConnection connection = composed.Connection;
        PairingCoordinator pairing = composed.Pairing;
        ICredentialStore store = composed.Store;
        pairing.StateChanged += (_, e) => events.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            NoiseTransport noise = await CompletePeerHandshakeAsync(peer, testToken);
            try
            {
                int served = 0;
                while (!testToken.IsCancellationRequested)
                {
                    BinaryNode request;
                    try
                    {
                        request = await ReceivePeerNodeAsync(peer, noise, testToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }

                    served++;
                    await SendPeerNodeAsync(peer, noise, RefReply(request.Attrs["id"], $"REF-{served}"), testToken);
                }

                await HoldPeerTillDoneAsync(peer, testToken);
            }
            finally
            {
                noise.Dispose();
            }
        }, testToken);

        Task connectTask = connection.ConnectAsync(testToken);
        Task pairingTask = pairing.StartAsync(testToken);

        PairingStateChangedEventArgs first = await WaitForPairingAsync(
            events, e => e.Status == PairingStatus.Code, "first code");
        first.Payload.Should().Contain("REF-1");

        clock.Advance(PairingQrPayload.FirstCodeLifetime);
        PairingStateChangedEventArgs second = await WaitForPairingAsync(
            events,
            e => e.Status == PairingStatus.Code && e.Payload != first.Payload,
            "second code after the 60s first lifetime");
        second.Payload.Should().Contain("REF-2");

        clock.Advance(PairingQrPayload.RotationInterval);
        PairingStateChangedEventArgs third = await WaitForPairingAsync(
            events,
            e => e.Status == PairingStatus.Code && e.Payload != first.Payload && e.Payload != second.Payload,
            "third code after the 20s rotation interval");
        third.Payload.Should().Contain("REF-3");

        events.Count(e => e.Status == PairingStatus.Code).Should().Be(3);
        PairingQrPayload.FirstCodeLifetime.Should().Be(TimeSpan.FromSeconds(60));
        PairingQrPayload.RotationInterval.Should().Be(TimeSpan.FromSeconds(20));

        await TeardownPairingAsync(pairingTask, connectTask, peerTask, connection, store, testCts);
    }

    [Fact]
    public async Task Pairing_timeout_fires_once_after_six_codes_with_no_further_codes()
    {
        var clock = new FakeTimeProvider();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var events = new ConcurrentQueue<PairingStateChangedEventArgs>();
        AccountId account = NewPairingAccount("timeout");

        var composed = await CreatePairingConnectionAsync(account, pairs, clock, testToken);
        await using WaConnection connection = composed.Connection;
        PairingCoordinator pairing = composed.Pairing;
        ICredentialStore store = composed.Store;
        pairing.StateChanged += (_, e) => events.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            NoiseTransport noise = await CompletePeerHandshakeAsync(peer, testToken);
            try
            {
                int served = 0;
                while (!testToken.IsCancellationRequested)
                {
                    BinaryNode request;
                    try
                    {
                        request = await ReceivePeerNodeAsync(peer, noise, testToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }

                    served++;
                    await SendPeerNodeAsync(peer, noise, RefReply(request.Attrs["id"], $"REF-{served}"), testToken);
                }

                await HoldPeerTillDoneAsync(peer, testToken);
            }
            finally
            {
                noise.Dispose();
            }
        }, testToken);

        Task connectTask = connection.ConnectAsync(testToken);
        Task pairingTask = pairing.StartAsync(testToken);

        await WaitForPairingAsync(events, e => e.Status == PairingStatus.Code, "first code");
        clock.Advance(PairingQrPayload.FirstCodeLifetime);
        await WaitForPairingAsync(
            events, e => e.Status == PairingStatus.Code && events.Count(c => c.Status == PairingStatus.Code) >= 2,
            "second code");
        for (int i = 0; i < 4; i++)
        {
            clock.Advance(PairingQrPayload.RotationInterval);
            int expected = 3 + i;
            await WaitForPairingAsync(
                events,
                e => e.Status == PairingStatus.Code && events.Count(c => c.Status == PairingStatus.Code) >= expected,
                $"code {expected}");
        }

        events.Count(e => e.Status == PairingStatus.Code).Should().Be(6);
        clock.Advance(PairingQrPayload.RotationInterval);
        await WaitForPairingAsync(events, e => e.Status == PairingStatus.Timeout, "timeout terminal");
        await pairingTask.WaitAsync(testToken);

        clock.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(50, testToken);
        events.Count(e => e.Status == PairingStatus.Code).Should().Be(6, "no Code event follows the terminal");
        events.Count(e => e.Status == PairingStatus.Timeout).Should().Be(1, "the timeout terminal fires exactly once");

        await TeardownPairingAsync(pairingTask, connectTask, peerTask, connection, store, testCts);
    }

    [Fact]
    public async Task Pairing_second_start_while_live_is_rejected_and_first_attempt_continues()
    {
        var clock = new FakeTimeProvider();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var events = new ConcurrentQueue<PairingStateChangedEventArgs>();
        AccountId account = NewPairingAccount("single-attempt");

        var composed = await CreatePairingConnectionAsync(account, pairs, clock, testToken);
        await using WaConnection connection = composed.Connection;
        PairingCoordinator pairing = composed.Pairing;
        ICredentialStore store = composed.Store;
        pairing.StateChanged += (_, e) => events.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            NoiseTransport noise = await CompletePeerHandshakeAsync(peer, testToken);
            try
            {
                int served = 0;
                while (!testToken.IsCancellationRequested)
                {
                    BinaryNode request;
                    try
                    {
                        request = await ReceivePeerNodeAsync(peer, noise, testToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }

                    served++;
                    await SendPeerNodeAsync(peer, noise, RefReply(request.Attrs["id"], $"REF-{served}"), testToken);
                }

                await HoldPeerTillDoneAsync(peer, testToken);
            }
            finally
            {
                noise.Dispose();
            }
        }, testToken);

        Task connectTask = connection.ConnectAsync(testToken);
        Task pairingTask = pairing.StartAsync(testToken);

        PairingStateChangedEventArgs first = await WaitForPairingAsync(
            events, e => e.Status == PairingStatus.Code, "first code");

        Func<Task> second = () => pairing.StartAsync(testToken);
        (await second.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("already live");

        clock.Advance(PairingQrPayload.FirstCodeLifetime);
        PairingStateChangedEventArgs rotated = await WaitForPairingAsync(
            events,
            e => e.Status == PairingStatus.Code && e.Payload != first.Payload,
            "rotation on the first attempt");
        rotated.Payload.Should().Contain("REF-2", "the first attempt continues uninterrupted");

        await TeardownPairingAsync(pairingTask, connectTask, peerTask, connection, store, testCts);
    }

    [Fact]
    public async Task Pairing_success_is_terminal_and_one_shot_with_unverified_passthrough()
    {
        // BOUNDARY (plan 03-04 owns the rest): the Success payload below is passed
        // through UNVERIFIED — no HMAC/signature check, no credential write. This
        // test asserts only the event shape (Status + passthrough payload) and the
        // terminal one-shot rule.
        var clock = new FakeTimeProvider();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var events = new ConcurrentQueue<PairingStateChangedEventArgs>();
        AccountId account = NewPairingAccount("success");

        var composed = await CreatePairingConnectionAsync(account, pairs, clock, testToken);
        await using WaConnection connection = composed.Connection;
        PairingCoordinator pairing = composed.Pairing;
        ICredentialStore store = composed.Store;
        pairing.StateChanged += (_, e) => events.Enqueue(e);

        var sendSuccess = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            NoiseTransport noise = await CompletePeerHandshakeAsync(peer, testToken);
            try
            {
                BinaryNode request = await ReceivePeerNodeAsync(peer, noise, testToken);
                await SendPeerNodeAsync(peer, noise, RefReply(request.Attrs["id"], "REF-1"), testToken);
                await sendSuccess.Task.WaitAsync(testToken);
                await SendPeerNodeAsync(peer, noise, PairSuccessNotification("1234567890"), testToken);

                // Post-terminal noise the coordinator must ignore: a duplicate
                // success and a stale correlated reply.
                await Task.Delay(50, testToken);
                await SendPeerNodeAsync(peer, noise, PairSuccessNotification("1234567890"), testToken);
                await SendPeerNodeAsync(peer, noise, RefReply(request.Attrs["id"], "REF-STALE"), testToken);
                await HoldPeerTillDoneAsync(peer, testToken);
            }
            finally
            {
                noise.Dispose();
            }
        }, testToken);

        Task connectTask = connection.ConnectAsync(testToken);
        Task pairingTask = pairing.StartAsync(testToken);

        await WaitForPairingAsync(events, e => e.Status == PairingStatus.Code, "first code");
        sendSuccess.TrySetResult();
        PairingStateChangedEventArgs success = await WaitForPairingAsync(
            events, e => e.Status == PairingStatus.Success, "success terminal");
        success.Payload.Should().Be("1234567890", "the success payload passes through (unverified in this plan)");
        await pairingTask.WaitAsync(testToken);

        await Task.Delay(100, testToken);
        events.Count(e => e.Status == PairingStatus.Success).Should().Be(1);
        events.Should().HaveCount(2, "no event follows the terminal — one Code plus one Success");

        await TeardownPairingAsync(pairingTask, connectTask, peerTask, connection, store, testCts);
    }

    [Fact]
    public async Task Pairing_connection_405_reaches_host_as_terminal_client_outdated()
    {
        var clock = new FakeTimeProvider();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var events = new ConcurrentQueue<PairingStateChangedEventArgs>();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        AccountId account = NewPairingAccount("client-outdated");

        var composed = await CreatePairingConnectionAsync(account, pairs, clock, testToken);
        await using WaConnection connection = composed.Connection;
        PairingCoordinator pairing = composed.Pairing;
        ICredentialStore store = composed.Store;
        pairing.StateChanged += (_, e) => events.Enqueue(e);
        connection.StateChanged += (_, e) => states.Enqueue(e);

        var sendFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            NoiseTransport noise = await CompletePeerHandshakeAsync(peer, testToken);
            try
            {
                BinaryNode request = await ReceivePeerNodeAsync(peer, noise, testToken);
                await SendPeerNodeAsync(peer, noise, RefReply(request.Attrs["id"], "REF-1"), testToken);
                await sendFailure.Task.WaitAsync(testToken);
                await SendPeerNodeAsync(peer, noise, FailureNode("405"), testToken);
                await HoldPeerTillDoneAsync(peer, testToken);
            }
            finally
            {
                noise.Dispose();
            }
        }, testToken);

        Task connectTask = connection.ConnectAsync(testToken);
        Task pairingTask = pairing.StartAsync(testToken);

        await WaitForPairingAsync(events, e => e.Status == PairingStatus.Code, "first code");
        sendFailure.TrySetResult();
        PairingStateChangedEventArgs terminal = await WaitForPairingAsync(
            events,
            e => e.Status == PairingStatus.ClientOutdated,
            "client-outdated terminal through the 03-02 seam");
        terminal.Payload.Should().BeNull();
        await pairingTask.WaitAsync(testToken);

        Func<Task> connect = () => connectTask;
        (await connect.Should().ThrowAsync<SdkConnectionException>())
            .Which.Message.Should().Contain("logged out");

        clock.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50, testToken);
        pairs.Created.Should().Be(1, "a 405 is terminal — zero further reconnect attempts");
        events.Count(e => e.Status == PairingStatus.ClientOutdated).Should().Be(1);
        events.Should().HaveCount(2, "one Code plus one terminal, nothing after");

        testCts.Cancel();
        await peerTask;
        await connection.DisposeAsync();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Pairing_connection_401_reaches_host_as_terminal_logged_out()
    {
        var clock = new FakeTimeProvider();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var events = new ConcurrentQueue<PairingStateChangedEventArgs>();
        AccountId account = NewPairingAccount("logged-out");

        var composed = await CreatePairingConnectionAsync(account, pairs, clock, testToken);
        await using WaConnection connection = composed.Connection;
        PairingCoordinator pairing = composed.Pairing;
        ICredentialStore store = composed.Store;
        pairing.StateChanged += (_, e) => events.Enqueue(e);

        var sendFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            NoiseTransport noise = await CompletePeerHandshakeAsync(peer, testToken);
            try
            {
                BinaryNode request = await ReceivePeerNodeAsync(peer, noise, testToken);
                await SendPeerNodeAsync(peer, noise, RefReply(request.Attrs["id"], "REF-1"), testToken);
                await sendFailure.Task.WaitAsync(testToken);
                await SendPeerNodeAsync(peer, noise, FailureNode("401"), testToken);
                await HoldPeerTillDoneAsync(peer, testToken);
            }
            finally
            {
                noise.Dispose();
            }
        }, testToken);

        Task connectTask = connection.ConnectAsync(testToken);
        Task pairingTask = pairing.StartAsync(testToken);

        await WaitForPairingAsync(events, e => e.Status == PairingStatus.Code, "first code");
        sendFailure.TrySetResult();
        PairingStateChangedEventArgs terminal = await WaitForPairingAsync(
            events, e => e.Status == PairingStatus.LoggedOut, "logged-out terminal");
        terminal.Payload.Should().BeNull();
        await pairingTask.WaitAsync(testToken);

        clock.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50, testToken);
        events.Count(e => e.Status == PairingStatus.LoggedOut).Should().Be(1);
        events.Should().HaveCount(2, "one Code plus one terminal, nothing after");

        testCts.Cancel();
        try
        {
            await connectTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (SdkException)
        {
        }

        await peerTask;
        await connection.DisposeAsync();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Pairing_level_405_refetches_version_bounded_then_client_outdated()
    {
        const int maxRefetches = 3;
        PairingCoordinator.MaxVersionRefetches.Should().Be(
            maxRefetches, "the refetch ceiling is a single named constant asserted literally");

        var clock = new FakeTimeProvider();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var events = new ConcurrentQueue<PairingStateChangedEventArgs>();
        AccountId account = NewPairingAccount("405-bounded");

        var composed = await CreatePairingConnectionAsync(account, pairs, clock, testToken);
        await using WaConnection connection = composed.Connection;
        PairingCoordinator pairing = composed.Pairing;
        ICredentialStore store = composed.Store;
        pairing.StateChanged += (_, e) => events.Enqueue(e);

        var fetcher = new CountingFetcher();
        connection.VersionResolver = new WaVersionResolver(fetcher, clock);

        int requests = 0;
        var handshakeDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            NoiseTransport noise = await CompletePeerHandshakeAsync(peer, testToken);
            handshakeDone.TrySetResult();
            try
            {
                while (!testToken.IsCancellationRequested)
                {
                    BinaryNode request;
                    try
                    {
                        request = await ReceivePeerNodeAsync(peer, noise, testToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }

                    Interlocked.Increment(ref requests);
                    await SendPeerNodeAsync(peer, noise, ErrorReply(request.Attrs["id"], "405"), testToken);
                }

                await HoldPeerTillDoneAsync(peer, testToken);
            }
            finally
            {
                noise.Dispose();
            }
        }, testToken);

        Task connectTask = connection.ConnectAsync(testToken);
        await handshakeDone.Task.WaitAsync(testToken);
        int baselineFetches = fetcher.Calls;

        Task pairingTask = pairing.StartAsync(testToken);
        await WaitForPairingAsync(events, e => e.Status == PairingStatus.ClientOutdated, "bounded client-outdated");
        await pairingTask.WaitAsync(testToken);

        int refetches = fetcher.Calls - baselineFetches;
        refetches.Should().Be(
            maxRefetches,
            "every pairing-level 405 re-resolves exactly once, then the bound stops the loop");
        requests.Should().Be(1 + maxRefetches, "one initial request plus one retry per refetch");
        events.Any(e => e.Status == PairingStatus.Code).Should().BeFalse("no Code event follows a 405 run");
        events.Count(e => e.Status == PairingStatus.ClientOutdated).Should().Be(1);

        await TeardownPairingAsync(pairingTask, connectTask, peerTask, connection, store, testCts);
    }

    [Fact]
    public async Task Pairing_503_survives_reconnect_with_no_terminal_and_code_after_recovery()
    {
        var clock = new FakeTimeProvider();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var events = new ConcurrentQueue<PairingStateChangedEventArgs>();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        AccountId account = NewPairingAccount("503-survives");

        var composed = await CreatePairingConnectionAsync(account, pairs, clock, testToken);
        await using WaConnection connection = composed.Connection;
        PairingCoordinator pairing = composed.Pairing;
        ICredentialStore store = composed.Store;
        pairing.StateChanged += (_, e) => events.Enqueue(e);
        connection.StateChanged += (_, e) => states.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport first = await TakePeerAsync(pairs, testToken);
            NoiseTransport firstNoise = await CompletePeerHandshakeAsync(first, testToken);
            try
            {
                await ReceivePeerNodeAsync(first, firstNoise, testToken);
                await first.CloseAsync(503, testToken);
            }
            finally
            {
                firstNoise.Dispose();
            }

            LoopbackFrameTransport second = await TakePeerAsync(pairs, testToken);
            NoiseTransport secondNoise = await CompletePeerHandshakeAsync(second, testToken);
            try
            {
                int served = 0;
                while (!testToken.IsCancellationRequested)
                {
                    BinaryNode request;
                    try
                    {
                        request = await ReceivePeerNodeAsync(second, secondNoise, testToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }

                    served++;
                    await SendPeerNodeAsync(second, secondNoise, RefReply(request.Attrs["id"], $"REF-R{served}"), testToken);
                }

                await HoldPeerTillDoneAsync(second, testToken);
            }
            finally
            {
                secondNoise.Dispose();
            }
        }, testToken);

        Task connectTask = connection.ConnectAsync(testToken);
        Task pairingTask = pairing.StartAsync(testToken);

        await WaitForConnectionAsync(
            states, e => e.State == ConnectionState.Closing && e.StatusCode == 503, "closing with 503");

        // The retry waits a full-jitter draw of at most the 1s base on the injected
        // clock — advancing past it fires the single scheduled reconnect.
        clock.Advance(TimeSpan.FromSeconds(2));

        PairingStateChangedEventArgs code = await WaitForPairingAsync(
            events, e => e.Status == PairingStatus.Code, "code after 503 recovery");
        code.Payload.Should().Contain("REF-R1", "the retried pairing request succeeds after reconnection");

        events.Any(e => e.Status is PairingStatus.Timeout or PairingStatus.LoggedOut or PairingStatus.ClientOutdated)
            .Should().BeFalse("a 503 never raises a terminal pairability event");
        pairs.Created.Should().Be(2, "the transient 503 reconnects on a fresh channel");

        await TeardownPairingAsync(pairingTask, connectTask, peerTask, connection, store, testCts);
    }
}
