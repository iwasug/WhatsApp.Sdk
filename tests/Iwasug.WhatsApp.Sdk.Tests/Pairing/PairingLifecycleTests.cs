using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Google.Protobuf;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Noise;
using Iwasug.WhatsApp.Sdk.Pairing;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.InMemory;
using Iwasug.WhatsApp.Sdk.Testing.FaultInjection;
using Iwasug.WhatsApp.Sdk.Testing.Rules;
using Iwasug.WhatsApp.Sdk.Tests.Transport;
using Iwasug.WhatsApp.Sdk.Transport;
using Microsoft.Extensions.Time.Testing;
using NSec.Cryptography;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Pairing;

/// <summary>
/// Registration-branch bootstrap proofs (03-03 Task 3, CONN-01 + CONN-03): fresh
/// pairing generates and persists identity material before any frame leaves the
/// client, a restart reuses it byte-identically, seeded rows are never
/// overwritten, and a store write failure aborts pairing loudly.
/// </summary>
/// <remarks>
/// Output discipline (V6/T-3-07): assertions compare public values and
/// row-equality only — private key bytes never enter an assertion message, and
/// the failure-path asserts carry rule names, never material.
/// </remarks>
public sealed class PairingLifecycleTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    private static byte[] FixedBytes(int start, int length = 32)
    {
        byte[] bytes = new byte[length];
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

    private static RegistrationDefaults DummyRegistrationDefaults() =>
        new(
            FixedBytes(0x11),
            7,
            FixedBytes(0x31),
            FixedSignature(),
            42,
            "android",
            2,
            3000,
            1043857760);

    private static AccountId NewAccount(string hint) =>
        AccountId.Create($"lifecycle-{hint}-" + Guid.NewGuid().ToString("N"));

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

    private static byte[] ToBigEndian4(int value) =>
        [(byte)((value >> 24) & 0xFF), (byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF)];

    private sealed class RecordingStore : ICredentialStore
    {
        internal readonly ConcurrentQueue<string> Calls = new();

        internal int Puts;

        internal Action<string>? PhaseSink;

        private readonly ICredentialStore _inner;

        internal RecordingStore(ICredentialStore inner)
        {
            _inner = inner;
        }

        public async Task<CredentialRecord?> GetAsync(CancellationToken cancellationToken)
        {
            Calls.Enqueue("Get");
            CredentialRecord? result = await _inner.GetAsync(cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref Puts) > 0)
            {
                // A read after the write completed is the independent-read
                // step of the success ordering (earlier reads are loads).
                PhaseSink?.Invoke("Read");
            }

            return result;
        }

        public async Task PutAsync(CredentialRecord credentials, CancellationToken cancellationToken)
        {
            Calls.Enqueue("Put");
            Interlocked.Increment(ref Puts);
            await _inner.PutAsync(credentials, cancellationToken).ConfigureAwait(false);
            PhaseSink?.Invoke("Put");
        }

        public async Task<bool> RemoveAsync(CancellationToken cancellationToken)
        {
            Calls.Enqueue("Remove");
            return await _inner.RemoveAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class FailPutStore : ICredentialStore
    {
        private readonly ICredentialStore _inner;

        internal FailPutStore(ICredentialStore inner)
        {
            _inner = inner;
        }

        public Task<CredentialRecord?> GetAsync(CancellationToken cancellationToken) =>
            _inner.GetAsync(cancellationToken);

        public Task PutAsync(CredentialRecord credentials, CancellationToken cancellationToken) =>
            throw new SdkStoreException("PairingLifecycle fixture rejects credential writes.");

        public Task<bool> RemoveAsync(CancellationToken cancellationToken) =>
            _inner.RemoveAsync(cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    /// <summary>
    /// Pass-through store whose NEXT <c>GetAsync</c> (after <see cref="Arm"/>)
    /// parks until the attempt token cancels — pins a code request inside the
    /// registration bootstrap, before any iq leaves the fake (WR-03's
    /// cancellation-before-any-iq scenario).
    /// </summary>
    private sealed class GateFirstGetStore : ICredentialStore
    {
        private readonly ICredentialStore _inner;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        internal GateFirstGetStore(ICredentialStore inner)
        {
            _inner = inner;
        }

        internal Task Entered => _entered.Task;

        internal void Arm() => Interlocked.Exchange(ref _armed, 1);

        public async Task<CredentialRecord?> GetAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            return await _inner.GetAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task PutAsync(CredentialRecord credentials, CancellationToken cancellationToken) =>
            _inner.PutAsync(credentials, cancellationToken);

        public Task<bool> RemoveAsync(CancellationToken cancellationToken) =>
            _inner.RemoveAsync(cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    /// <summary>
    /// Delegates the write to the inner store, then (once) jumps the fake
    /// clock past the attempt window and holds the call briefly — a persist
    /// that outlives the watchdog deadline, so the Timeout fires while the
    /// coordinator's persist window is open (WR-02).
    /// </summary>
    private sealed class SlowPersistStore : ICredentialStore
    {
        private readonly ICredentialStore _inner;
        private readonly FakeTimeProvider _clock;
        private readonly TimeSpan _window;
        private int _advanced;

        internal SlowPersistStore(ICredentialStore inner, FakeTimeProvider clock, TimeSpan window)
        {
            _inner = inner;
            _clock = clock;
            _window = window;
        }

        public Task<CredentialRecord?> GetAsync(CancellationToken cancellationToken) =>
            _inner.GetAsync(cancellationToken);

        public async Task PutAsync(CredentialRecord credentials, CancellationToken cancellationToken)
        {
            await _inner.PutAsync(credentials, cancellationToken).ConfigureAwait(false);
            if (Interlocked.Exchange(ref _advanced, 1) == 0)
            {
                // The attempt deadline elapses mid-write: the watchdog's
                // delay completes while this call is still in flight.
                _clock.Advance(_window);
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }

        public Task<bool> RemoveAsync(CancellationToken cancellationToken) =>
            _inner.RemoveAsync(cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private static async Task<ICredentialStore> CreateEmptyStoreAsync(AccountId account, CancellationToken cancellationToken)
    {
        ICredentialStoreFactory factory = new InMemoryStoreFactory();
        return await factory.CreateAsync(account, cancellationToken).ConfigureAwait(false);
    }

    [Fact]
    public async Task Fresh_pairing_persists_row_before_handshake_payload_builds()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        AccountId account = NewAccount("ordering");

        ICredentialStore inner = await CreateEmptyStoreAsync(account, testToken);
        var recording = new RecordingStore(inner);
        var payloads = new HandshakePayloadProvider(account, recording, DummyRegistrationDefaults());

        byte[]? staticKey = await payloads.LoadCredentialsAsync(testToken);
        staticKey.Should().NotBeNull("fresh pairing generates identity instead of failing");

        // Ordering (D-06): the write is observed before the outbound registration
        // payload bytes exist — the Put lands during the load, the finish bytes
        // are built only after, and an independent read observes the row.
        recording.Calls.Should().ContainInOrder("Get", "Put");
        recording.Puts.Should().Be(1, "generation persists exactly once — no silent retries");
        CredentialRecord? row = await inner.GetAsync(testToken);
        row.Should().NotBeNull("an independent read observes the write before any payload exists");
        staticKey.Should().Equal(row!.NoiseKeyPrivate, "the handshake uses the persisted static");

        byte[] finish = payloads.GetClientFinish();
        ClientPayload payload = ClientPayload.Parser.ParseFrom(finish);
        payload.Passive.Should().BeFalse();
        payload.Pull.Should().BeFalse();
        payload.DevicePairingData.Should().NotBeNull();
        payload.DevicePairingData!.EIdent.Should().Equal(
            DeriveX25519Public(row.IdentityKeyPrivate),
            "the payload carries the persisted identity — proof the write preceded the bytes");
        payload.DevicePairingData.ERegid.Should().Equal(ToBigEndian4(row.RegistrationId));

        await inner.DisposeAsync();
    }

    [Fact]
    public async Task Generated_material_survives_fresh_composition_byte_identical()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        AccountId account = NewAccount("crash-window");

        // One store image shared across the "crash": the first composition
        // generates and persists, then is discarded before any pairing frame.
        ICredentialStore store = await CreateEmptyStoreAsync(account, testToken);
        var first = new HandshakePayloadProvider(account, store, DummyRegistrationDefaults());
        byte[]? firstStatic = await first.LoadCredentialsAsync(testToken);
        firstStatic.Should().NotBeNull();

        CredentialRecord? persisted = await store.GetAsync(testToken);
        persisted.Should().NotBeNull("the row is durable once observed");
        byte[] firstFinish = first.GetClientFinish();

        // Fresh composition against the same store (the restarted process):
        // no regeneration, same static, same registration bytes.
        var second = new HandshakePayloadProvider(account, store, DummyRegistrationDefaults());
        byte[]? secondStatic = await second.LoadCredentialsAsync(testToken);
        secondStatic.Should().NotBeNull();
        secondStatic.Should().Equal(firstStatic, "a restart reuses the persisted static — never re-pairs");
        second.GetClientFinish().Should().Equal(firstFinish, "the resumed registration bytes are identical");

        await store.DisposeAsync();
    }

    [Fact]
    public async Task Seeded_row_is_never_overwritten_by_pairing_start()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        AccountId account = NewAccount("no-regen");

        ICredentialStore inner = await CreateEmptyStoreAsync(account, testToken);
        var seed = new CredentialRecord(
            new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero),
            "android",
            FixedBytes(0x01),
            FixedBytes(0x21),
            FixedBytes(0x41),
            FixedBytes(0x61),
            null,
            0,
            42);
        await inner.PutAsync(seed, testToken);
        var recording = new RecordingStore(inner);
        var payloads = new HandshakePayloadProvider(account, recording, DummyRegistrationDefaults());

        // Both pairing-start paths (the coordinator's ensure and the handshake's
        // load) must leave a present row untouched.
        await payloads.EnsureRegistrationMaterialAsync(testToken);
        byte[]? staticKey = await payloads.LoadCredentialsAsync(testToken);

        recording.Puts.Should().Be(0, "a present row is never overwritten — generation runs only when missing");
        CredentialRecord? after = await inner.GetAsync(testToken);
        after.Should().NotBeNull();
        after!.NoiseKeyPrivate.Should().Equal(seed.NoiseKeyPrivate);
        after.IdentityKeyPrivate.Should().Equal(seed.IdentityKeyPrivate);
        after.AdvSecret.Should().Equal(seed.AdvSecret, "no byte of the seeded row may change");
        staticKey.Should().Equal(seed.NoiseKeyPrivate);

        await inner.DisposeAsync();
    }

    [Fact]
    public async Task Put_failure_aborts_pairing_start_with_no_frame_and_no_code()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        AccountId account = NewAccount("fail-closed");

        ICredentialStore inner = await CreateEmptyStoreAsync(account, testToken);
        var failing = new FailPutStore(inner);
        var payloads = new HandshakePayloadProvider(account, failing, DummyRegistrationDefaults());

        int transports = 0;
        IFrameTransport CreateClient()
        {
            Interlocked.Increment(ref transports);
            throw new InvalidOperationException("No channel may exist before the bootstrap write.");
        }

        await using var connection = new WaConnection(
            account, CreateClient, payloads, new ConnectionOptions());
        PairingCoordinator pairing = connection.Pairing;
        var events = new ConcurrentQueue<PairingStateChangedEventArgs>();
        pairing.StateChanged += (_, e) => events.Enqueue(e);

        Func<Task> start = () => pairing.StartAsync(testToken);
        (await start.Should().ThrowAsync<SdkStoreException>())
            .Which.Message.Should().Contain("rejects credential writes");

        events.Should().BeEmpty("no Code event fires when the write fails — the failure is not swallowed");
        Volatile.Read(ref transports).Should().Be(0, "no pairing frame is sent — no channel ever existed");

        // The attempt is cleared, not stuck live: a retry surfaces the same
        // store failure rather than a spurious single-attempt rejection.
        Func<Task> retry = () => pairing.StartAsync(testToken);
        await retry.Should().ThrowAsync<SdkStoreException>();

        await inner.DisposeAsync();
    }

    [Fact]
    public async Task Persisted_registration_payload_carries_generated_values()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        AccountId account = NewAccount("values");

        ICredentialStore store = await CreateEmptyStoreAsync(account, testToken);
        var payloads = new HandshakePayloadProvider(account, store, DummyRegistrationDefaults());

        // The coordinator's own call: ensure material, then build the payload a
        // handshake would carry — both must observe the persisted row.
        await payloads.EnsureRegistrationMaterialAsync(testToken);
        CredentialRecord? row = await store.GetAsync(testToken);
        row.Should().NotBeNull();

        ClientPayload payload = ClientPayload.Parser.ParseFrom(payloads.GetClientFinish());
        payload.Passive.Should().BeFalse("registration is not passive");
        payload.Pull.Should().BeFalse("registration does not pull");
        payload.Username.Should().Be(0, "registration carries no paired username");
        payload.DevicePairingData.Should().NotBeNull();
        payload.DevicePairingData!.EIdent.Should().Equal(
            DeriveX25519Public(row!.IdentityKeyPrivate),
            "eIdent is the stored identity's public part");
        payload.DevicePairingData.ERegid.Should().Equal(
            ToBigEndian4(row.RegistrationId),
            "the registration id comes from the stored row");
        payload.DevicePairingData.EKeytype.Should().Equal(
            new byte[] { 0x05 }, "key-bundle type byte");
        payload.DevicePairingData.ESkeyVal.Should().Equal(
            FixedBytes(0x31),
            "the signed-prekey triple stays on the injected defaults until prekey lifecycle lands");
        payload.DevicePairingData.ESkeySig.Should().Equal(FixedSignature());
        payload.WebInfo.Should().BeNull("web_info stays absent on android companions");

        await store.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // Task 2 (03-04): success ordering — verify → awaited write →
    // independent read → reply → event, driven through the coordinator's own
    // seams (direct construction over a recording sender + verify wrapper +
    // recording store). Only the crash-recovery proof composes a real
    // connection; everything else observes the exact seam traffic.
    // ------------------------------------------------------------------

    private const string OrderingUser = "15551234567";
    private const uint OrderingDevice = 3;
    private const uint OrderingRegistrationId = 77;

    private static CredentialRecord KnownUnpairedRow() =>
        new(
            new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero),
            "android",
            FixedBytes(0x01),
            FixedBytes(0x21),
            FixedBytes(0x41),
            FixedBytes(0x61),
            null,
            0,
            42);

    // The success container and its ref/success frames are built by the shared
    // fixture (plan 03-06 Task 1): the resume lifecycle proof and these
    // ordering proofs must share ONE construction of these security-critical
    // bytes, so this file delegates instead of keeping a private copy.
    private static byte[] BuildSuccessContainer(CredentialRecord row, string user, uint device, uint registrationId) =>
        PairingSuccessFixture.BuildSuccessContainer(row, user, device, registrationId);

    private static BinaryNode BuildRefReply(string id, string pairingRef) =>
        PairingSuccessFixture.BuildRefReply(id, pairingRef);

    private static BinaryNode BuildSuccessNotification(string id, byte[] container) =>
        PairingSuccessFixture.BuildSuccessNotification(id, container);

    private static bool HasChild(BinaryNode node, string tag) =>
        node.Children is not null && node.Children.Any(child => string.Equals(child.Tag, tag, StringComparison.Ordinal));

    private static async Task<PairingStateChangedEventArgs> WaitForPairingEventAsync(
        ConcurrentQueue<PairingStateChangedEventArgs> events,
        Func<PairingStateChangedEventArgs, bool> predicate,
        string what)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);
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

    private static async Task<BinaryNode> WaitForSentAsync(ConcurrentQueue<BinaryNode> sent, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        while (!linked.IsCancellationRequested)
        {
            if (sent.TryDequeue(out BinaryNode? node) && node is not null)
            {
                return node;
            }

            await Task.Delay(10, linked.Token).ConfigureAwait(false);
        }

        throw new TimeoutException("Timed out waiting for a pairing frame.");
    }

    private static async Task WaitForPutsAsync(RecordingStore recording, int expected, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        while (Volatile.Read(ref recording.Puts) < expected)
        {
            await Task.Delay(10, linked.Token).ConfigureAwait(false);
        }
    }

    private sealed class DirectSender
    {
        internal readonly ConcurrentQueue<BinaryNode> Requests = new();
        internal readonly ConcurrentQueue<BinaryNode> CodeRequests = new();
        internal readonly ConcurrentQueue<BinaryNode> HelloAcks = new();
        internal readonly ConcurrentQueue<BinaryNode> Replies = new();
        internal readonly List<string> Phases = new();
        internal readonly object PhaseGate = new();
        internal TaskCompletionSource? BlockReplies;

        internal async ValueTask SendAsync(BinaryNode node, CancellationToken cancellationToken)
        {
            if (HasChild(node, "link-code-request"))
            {
                CodeRequests.Enqueue(node);
                return;
            }

            if (HasChild(node, "companion-hello-ack"))
            {
                HelloAcks.Enqueue(node);
                return;
            }

            if (HasChild(node, "pair-device"))
            {
                Requests.Enqueue(node);
                return;
            }

            if (HasChild(node, "pair-success-ack"))
            {
                lock (PhaseGate)
                {
                    Phases.Add(BlockReplies is null ? "Reply" : "Reply-blocked");
                }

                Replies.Enqueue(node);
                if (BlockReplies is not null)
                {
                    await BlockReplies.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    lock (PhaseGate)
                    {
                        Phases.Add("Reply-sent");
                    }
                }
            }
        }
    }

    private sealed class OrderingHarness
    {
        internal readonly ConcurrentQueue<PairingStateChangedEventArgs> Events = new();
        internal readonly DirectSender Sender = new();
        internal readonly HandshakePayloadProvider Payloads;
        internal readonly PairingCoordinator Pairing;

        internal OrderingHarness(
            AccountId account,
            ICredentialStore store,
            bool wrapVerifyPhases,
            TimeProvider? timeProvider = null,
            Func<int, byte[]>? randomBytes = null)
        {
            Payloads = new HandshakePayloadProvider(account, store, DummyRegistrationDefaults());
            Func<byte[], byte[], byte[], PairSuccessResult>? verify = null;
            if (wrapVerifyPhases)
            {
                verify = (container, secret, devicePublic) =>
                {
                    lock (Sender.PhaseGate)
                    {
                        Sender.Phases.Add("Verify");
                    }

                    return PairSuccessVerifier.Verify(container, secret, devicePublic);
                };
            }

            Pairing = new PairingCoordinator(
                account,
                Payloads,
                store,
                Sender.SendAsync,
                () => true,
                pairSuccessVerifier: verify,
                timeProvider: timeProvider,
                resolver: null,
                logger: null,
                randomBytes: randomBytes);
            Pairing.StateChanged += (_, e) =>
            {
                Events.Enqueue(e);
                if (e.Status == PairingStatus.Success)
                {
                    lock (Sender.PhaseGate)
                    {
                        Sender.Phases.Add("Success");
                    }
                }
            };
        }

        internal List<string> CopyPhases()
        {
            lock (Sender.PhaseGate)
            {
                return [.. Sender.Phases];
            }
        }
    }

    [Fact]
    public async Task Verified_success_runs_verify_write_read_reply_event_in_order()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        AccountId account = NewAccount("ordering");

        ICredentialStore inner = await CreateEmptyStoreAsync(account, testToken);
        var recording = new RecordingStore(inner);
        var harness = new OrderingHarness(account, recording, wrapVerifyPhases: true);
        recording.PhaseSink = phase =>
        {
            lock (harness.Sender.PhaseGate)
            {
                harness.Sender.Phases.Add(phase);
            }
        };

        CredentialRecord seed = KnownUnpairedRow();
        await inner.PutAsync(seed, testToken);

        Task pairingTask = harness.Pairing.StartAsync(testToken);

        BinaryNode request = await WaitForSentAsync(harness.Sender.Requests, testToken);
        harness.Pairing.OnStanzaReceived(null, BuildRefReply(request.Attrs["id"], "REF-1"));
        await WaitForPairingEventAsync(harness.Events, e => e.Status == PairingStatus.Code, "first code");

        byte[] container = BuildSuccessContainer(seed, OrderingUser, OrderingDevice, OrderingRegistrationId);
        harness.Pairing.OnStanzaReceived(null, BuildSuccessNotification("n-1", container));

        PairingStateChangedEventArgs success = await WaitForPairingEventAsync(
            harness.Events, e => e.Status == PairingStatus.Success, "verified success");
        success.Payload.Should().Be(OrderingUser, "the Success event carries the verified paired user");
        await pairingTask.WaitAsync(testToken);

        // The mandated ordering as an ordered list — not five booleans.
        harness.CopyPhases().Should().Equal("Verify", "Put", "Read", "Reply", "Success");
        recording.Puts.Should().Be(1, "exactly one credential write lands on the success path");

        harness.Sender.Replies.Should().HaveCount(1, "exactly one reply answers the success");
        BinaryNode reply = harness.Sender.Replies.Single();
        reply.Tag.Should().Be("iq");
        reply.Attrs["id"].Should().Be("n-1", "the reply correlates to the success stanza's id");
        reply.Attrs["type"].Should().Be("result");
        HasChild(reply, "pair-success-ack").Should().BeTrue("the reply is the scripted success acknowledgement");

        CredentialRecord? row = await inner.GetAsync(testToken);
        row.Should().NotBeNull();
        row!.PairedUser.Should().Be(OrderingUser);
        row.Device.Should().Be((int)OrderingDevice);

        await inner.DisposeAsync();
    }

    [Fact]
    public async Task FailWrites_surfaces_failure_with_no_success_and_no_reply()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        AccountId account = NewAccount("failwrites");

        ICredentialStoreFactory innerFactory = new InMemoryStoreFactory();
        var faulting = new FaultingStoreFactory(credential: innerFactory);
        ICredentialStoreFactory factory = faulting;
        ICredentialStore store = await factory.CreateAsync(account, testToken);

        CredentialRecord seed = KnownUnpairedRow();
        await store.PutAsync(seed, testToken);

        var harness = new OrderingHarness(account, store, wrapVerifyPhases: true);
        Task pairingTask = harness.Pairing.StartAsync(testToken);

        BinaryNode request = await WaitForSentAsync(harness.Sender.Requests, testToken);
        harness.Pairing.OnStanzaReceived(null, BuildRefReply(request.Attrs["id"], "REF-1"));
        await WaitForPairingEventAsync(harness.Events, e => e.Status == PairingStatus.Code, "first code");

        faulting.Mode = FaultMode.FailWrites;
        byte[] container = BuildSuccessContainer(seed, OrderingUser, OrderingDevice, OrderingRegistrationId);
        harness.Pairing.OnStanzaReceived(null, BuildSuccessNotification("n-1", container));

        Func<Task> start = () => pairingTask;
        (await start.Should().ThrowAsync<SdkStoreException>())
            .Which.Message.Should().Contain("FailWrites");
        harness.Events.Should().NotContain(
            e => e.Status == PairingStatus.Success, "no Success event fires without a durable write");
        harness.Sender.Replies.Should().BeEmpty("no reply is sent when the write fails");
        harness.CopyPhases().Should().Equal(
            ["Verify"], "verify runs before the write is attempted — and nothing follows it");

        await store.DisposeAsync();
    }

    [Fact]
    public async Task Verified_success_is_visible_to_an_independent_read()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        AccountId account = NewAccount("independent-read");

        ICredentialStoreFactory factory = new InMemoryStoreFactory();
        CredentialRecord seed = KnownUnpairedRow();
        await using (ICredentialStore seeder = await factory.CreateAsync(account, testToken))
        {
            await seeder.PutAsync(seed, testToken);
        }

        // StoreContractRules D-06 semantics, literally: the writer is the
        // coordinator's own store, the reader is a second instance over the
        // same backing state — the UPDATED row must be visible there.
        await StoreContractRules.AssertWriteVisibleToIndependentReadAsync<ICredentialStore>(
            createStore: () => factory.CreateAsync(account, testToken),
            writeAsync: async writer =>
            {
                var harness = new OrderingHarness(account, writer, wrapVerifyPhases: false);
                Task pairingTask = harness.Pairing.StartAsync(testToken);
                BinaryNode request = await WaitForSentAsync(harness.Sender.Requests, testToken);
                harness.Pairing.OnStanzaReceived(null, BuildRefReply(request.Attrs["id"], "REF-1"));
                byte[] container = BuildSuccessContainer(seed, OrderingUser, OrderingDevice, OrderingRegistrationId);
                harness.Pairing.OnStanzaReceived(null, BuildSuccessNotification("n-1", container));
                await WaitForPairingEventAsync(
                    harness.Events, e => e.Status == PairingStatus.Success, "verified success");
                await pairingTask.WaitAsync(testToken);
            },
            assertVisibleAsync: async reader =>
            {
                CredentialRecord? row = await reader.GetAsync(testToken);
                row.Should().NotBeNull("the write is durable once observed");
                row!.PairedUser.Should().Be(OrderingUser, "the independent read sees the UPDATED row");
                row.Device.Should().Be((int)OrderingDevice);
                row.RegistrationId.Should().Be((int)OrderingRegistrationId);
            });
    }

    [Fact]
    public async Task Rejected_container_writes_nothing_replies_nothing_and_continues()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        AccountId account = NewAccount("reject-path");

        ICredentialStore inner = await CreateEmptyStoreAsync(account, testToken);
        var recording = new RecordingStore(inner);
        var harness = new OrderingHarness(account, recording, wrapVerifyPhases: true);

        CredentialRecord seed = KnownUnpairedRow();
        await inner.PutAsync(seed, testToken);

        Task pairingTask = harness.Pairing.StartAsync(testToken);

        BinaryNode request = await WaitForSentAsync(harness.Sender.Requests, testToken);
        harness.Pairing.OnStanzaReceived(null, BuildRefReply(request.Attrs["id"], "REF-1"));
        await WaitForPairingEventAsync(harness.Events, e => e.Status == PairingStatus.Code, "first code");

        byte[] valid = BuildSuccessContainer(seed, OrderingUser, OrderingDevice, OrderingRegistrationId);
        PairSuccessContainer parsed = PairSuccessContainer.Parser.ParseFrom(valid);
        byte[] tamperedKey = parsed.Details.AccountPublic.ToByteArray();
        tamperedKey[0] ^= 0xFF;
        parsed.Details.AccountPublic = ByteString.CopyFrom(tamperedKey);
        byte[] forged = parsed.ToByteArray();

        harness.Pairing.OnStanzaReceived(null, BuildSuccessNotification("n-9", forged));
        await Task.Delay(300, testToken);

        recording.Puts.Should().Be(0, "verify runs first — a rejected container never touches the store");
        recording.Calls.Should().NotContain("Put");
        harness.Sender.Replies.Should().BeEmpty("no reply without verification");
        harness.Events.Should().NotContain(e => e.Status == PairingStatus.Success);

        // The attempt continues after a rejection: a later valid container still succeeds.
        harness.Pairing.OnStanzaReceived(null, BuildSuccessNotification("n-2", valid));
        PairingStateChangedEventArgs success = await WaitForPairingEventAsync(
            harness.Events, e => e.Status == PairingStatus.Success, "later valid success");
        success.Payload.Should().Be(OrderingUser);
        await pairingTask.WaitAsync(testToken);
        harness.Events.Should().HaveCount(2, "one Code plus one Success — the rejection emitted nothing");

        await inner.DisposeAsync();
    }

    private sealed class CrashPairSource
    {
        internal readonly ConcurrentQueue<LoopbackFrameTransport> Peers = new();

        internal IFrameTransport CreateClient()
        {
            (LoopbackFrameTransport left, LoopbackFrameTransport right) = LoopbackFrameTransport.CreatePair();
            Peers.Enqueue(right);
            return left;
        }
    }

    private sealed class CrashHandshakeResult
    {
        internal required NoiseTransport Transport { get; init; }

        internal required byte[] ClientFinishPayload { get; init; }
    }

    private static ConnectionOptions CrashOptions() => new()
    {
        ConnectTimeoutMs = 5_000,
        QueryTimeoutMs = 5_000,
        RetryBaseDelayMs = 0,
        RetryMaxDelayMs = 0,
    };

    private static async Task<LoopbackFrameTransport> TakeCrashPeerAsync(CrashPairSource pairs, CancellationToken cancellationToken)
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

    private static async Task<CrashHandshakeResult> CompleteCrashPeerHandshakeAsync(
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
        HandshakeMessage envelope = HandshakeMessage.Parser.ParseFrom(messageC);
        ClientFinish? finish = envelope.ClientFinish
            ?? throw new InvalidOperationException("Peer received message C without a client-finish envelope.");
        byte[] raw3 = new byte[finish.StaticKey.Length + finish.Payload.Length];
        finish.StaticKey.ToByteArray().CopyTo(raw3.AsSpan(0, finish.StaticKey.Length));
        finish.Payload.ToByteArray().CopyTo(raw3.AsSpan(finish.StaticKey.Length));
        (byte[] sendKey, byte[] receiveKey, byte[] finishPayload) = responder.CompleteRawMessage3(raw3);
        var transport = new NoiseTransport(sendKey, receiveKey);
        CryptographicOperations.ZeroMemory(sendKey);
        CryptographicOperations.ZeroMemory(receiveKey);
        return new CrashHandshakeResult { Transport = transport, ClientFinishPayload = finishPayload };
    }

    private static async Task SendCrashPeerSuccessAsync(
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

    [Fact]
    public async Task Crash_after_write_resumes_paired_without_repair()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken testToken = testCts.Token;
        AccountId account = NewAccount("crash-recovery");

        ICredentialStoreFactory factory = new InMemoryStoreFactory();
        ICredentialStore store = await factory.CreateAsync(account, testToken);
        CredentialRecord seed = KnownUnpairedRow();
        await store.PutAsync(seed, testToken);

        var recording = new RecordingStore(store);
        var harness = new OrderingHarness(account, recording, wrapVerifyPhases: false);
        harness.Sender.BlockReplies = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var pairingCts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        Task pairingTask = harness.Pairing.StartAsync(pairingCts.Token);

        BinaryNode request = await WaitForSentAsync(harness.Sender.Requests, testToken);
        harness.Pairing.OnStanzaReceived(null, BuildRefReply(request.Attrs["id"], "REF-1"));
        await WaitForPairingEventAsync(harness.Events, e => e.Status == PairingStatus.Code, "first code");

        byte[] container = BuildSuccessContainer(seed, OrderingUser, OrderingDevice, OrderingRegistrationId);
        harness.Pairing.OnStanzaReceived(null, BuildSuccessNotification("n-1", container));

        // The crash window: the write completed and is independently
        // observable, but the reply is still blocked — the server was never
        // told success.
        await WaitForPutsAsync(recording, 1, testToken);
        harness.Sender.Replies.Should().HaveCount(1, "the reply was attempted");
        harness.Events.Should().NotContain(e => e.Status == PairingStatus.Success, "no Success before the reply completes");

        await using (ICredentialStore reader = await factory.CreateAsync(account, testToken))
        {
            CredentialRecord? row = await reader.GetAsync(testToken);
            row.Should().NotBeNull();
            row!.PairedUser.Should().Be(OrderingUser);
        }

        // Crash: the attempt dies with the reply unsent.
        pairingCts.Cancel();
        Func<Task> pairing = () => pairingTask;
        await pairing.Should().ThrowAsync<OperationCanceledException>();
        harness.Events.Should().NotContain(e => e.Status == PairingStatus.Success);

        // Fresh composition over the same store: the login path sees the
        // paired row (resume branch) — no re-pair needed.
        var pairs = new CrashPairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        var payloads = new HandshakePayloadProvider(account, store, DummyRegistrationDefaults());
        await using var connection = new WaConnection(account, pairs.CreateClient, payloads, CrashOptions());
        connection.StateChanged += (_, e) => states.Enqueue(e);

        var captured = new TaskCompletionSource<CrashHandshakeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakeCrashPeerAsync(pairs, testToken);
            CrashHandshakeResult handshake = await CompleteCrashPeerHandshakeAsync(peer, testToken);
            captured.TrySetResult(handshake);
            await SendCrashPeerSuccessAsync(peer, handshake.Transport, testToken);
            ReadOnlyMemory<byte>? tail = await peer.ReceiveAsync(testToken);
            tail.Should().BeNull("the client owns teardown — the peer only observes it");
            handshake.Transport.Dispose();
            await peer.DisposeAsync();
        }, testToken);

        await connection.ConnectAsync(testToken);
        CrashHandshakeResult result = await captured.Task.WaitAsync(testToken);
        ClientPayload finish = ClientPayload.Parser.ParseFrom(result.ClientFinishPayload);
        finish.Username.Should().Be(15551234567UL, "the restarted process resumes with the login payload");
        finish.Passive.Should().BeTrue("login is passive");
        finish.Pull.Should().BeTrue("login pulls");

        await connection.DisposeAsync();
        await peerTask;
        await store.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // Task 3 (03-05): companion handshake routing — companion_hello → ack →
    // companion notification → GCM bundle authentication → the SAME
    // verify-then-persist drain. The five plan-mandated behaviors: the full
    // flow, the sequence bypass proof, the stall-then-typed-timeout proof,
    // the tampered/foreign bundle proof, and the rotated-epoch rejection.
    // ------------------------------------------------------------------

    private const string CodePhone = "15551234567";

    /// <summary>
    /// Scripted pairing randomness for the code path: records every call so the
    /// test re-derives exactly the code bytes and salt the coordinator consumed
    /// (plan 03-03's injectable-randomness seam — the fake never supplies the
    /// code, it only pins the coordinator's own inputs).
    /// </summary>
    /// <remarks>
    /// The recorder keeps a defensive COPY of every buffer it hands out: the
    /// coordinator owns what it is given and zeroizes salts/IVs/seeds when the
    /// attempt unwinds (secret hygiene, <see cref="PairingCoordinator"/>), so a
    /// recorded live array degenerates to zeros before the re-derivations below
    /// run. That aliasing is why the companion-finish facts previously died in
    /// a 20s wait: the test sealed the finish under a bundle key derived from a
    /// zeroized salt while the coordinator held the real one.
    /// </remarks>
    private sealed class RecordingRandomness
    {
        private readonly List<byte[]> _calls = [];

        /// <summary>Index of the current attempt's first consumption (set per request).</summary>
        private int _attemptStart;

        internal void BeginAttempt() => _attemptStart = _calls.Count;

        internal byte[] Next(int count)
        {
            byte[] bytes = new byte[count];
            for (int i = 0; i < count; i++)
            {
                bytes[i] = (byte)(0x30 + (_calls.Count * 7) + i);
            }

            // The caller takes ownership of the returned buffer (and may
            // zeroize it); the record must survive that, so it is a copy.
            _calls.Add((byte[])bytes.Clone());
            return bytes;
        }

        /// <summary>The live attempt's bundle key, re-derived exactly as the coordinator does.</summary>
        internal byte[] BundleKey()
        {
            byte[] codeBytes = _calls[_attemptStart];
            byte[] salt = _calls[_attemptStart + 1];
            string rawCode = PairingCodeGenerator.GenerateCode(_ => codeBytes);
            byte[] codeAscii = Encoding.ASCII.GetBytes(rawCode);
            try
            {
                byte[] linkKey = LinkCodeCrypto.DeriveKey(codeAscii, salt);
                return LinkCodeCrypto.DeriveBundleKey(linkKey, salt);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(codeAscii);
            }
        }

        /// <summary>
        /// The display string the coordinator must have derived from this
        /// attempt's injected code bytes — the happy path's determinism proof.
        /// </summary>
        internal string ExpectedDisplay() =>
            PairingCodeGenerator.ToDisplay(PairingCodeGenerator.GenerateCode(_ => _calls[_attemptStart]));
    }

    private static BinaryNode BuildCodeRefReply(string id, string codeRef) =>
        BinaryNode.FromChildren(
            "iq",
            [BinaryNode.Create("link-code-ref", new Dictionary<string, string> { ["ref"] = codeRef })],
            new Dictionary<string, string> { ["id"] = id, ["type"] = "result" });

    /// <summary>
    /// A correlated server rejection of the link-code request (the failure
    /// reply that must reset the attempt — never 405, which is the bounded
    /// version-refetch path rather than a terminal rejection).
    /// </summary>
    private static BinaryNode BuildCodeErrorReply(string id, string code) =>
        BinaryNode.FromChildren(
            "iq",
            [BinaryNode.Create("error", new Dictionary<string, string> { ["code"] = code })],
            new Dictionary<string, string> { ["id"] = id, ["type"] = "error" });

    private static BinaryNode BuildCompanionHello(string id) =>
        BinaryNode.FromChildren(
            "iq",
            [BinaryNode.Create("companion-hello")],
            new Dictionary<string, string> { ["id"] = id, ["type"] = "get" });

    private static BinaryNode BuildCompanionFinish(string id, byte[] bundle) =>
        BinaryNode.FromChildren(
            "notification",
            [BinaryNode.FromBytes("companion-finish", bundle)],
            new Dictionary<string, string> { ["id"] = id });

    /// <summary>
    /// Issues one pairing code through the coordinator's public entry and
    /// answers the request with a scripted ref reply (the fake never
    /// supplies the code itself).
    /// </summary>
    private static async Task<string> RequestCodeAsync(
        OrderingHarness harness,
        RecordingRandomness randomness,
        CancellationToken cancellationToken)
    {
        randomness.BeginAttempt();
        Task<string> request = harness.Pairing.RequestPairingCodeAsync(CodePhone, cancellationToken);
        if (request.IsFaulted)
        {
            // An immediate rejection (single-attempt rule) surfaces here —
            // no wire traffic follows, so never wait for a reply.
            await request;
        }

        BinaryNode sent = await WaitForSentAsync(harness.Sender.CodeRequests, cancellationToken);
        harness.Pairing.OnStanzaReceived(null, BuildCodeRefReply(sent.Attrs["id"], "CODEREF-SCRIPTED"));
        return await request.WaitAsync(cancellationToken);
    }

    private static byte[] ProtectUnder(byte[] bundleKey, byte[] container) =>
        LinkCodeCrypto.ProtectBundle(bundleKey, FixedBytes(0x90, LinkCodeCrypto.NonceSize), container);

    [Fact]
    public async Task Companion_hello_ack_finish_reaches_verified_success()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        AccountId account = NewAccount("companion");

        ICredentialStore inner = await CreateEmptyStoreAsync(account, testToken);
        var recording = new RecordingStore(inner);
        var randomness = new RecordingRandomness();
        var harness = new OrderingHarness(
            account, recording, wrapVerifyPhases: true, randomBytes: randomness.Next);
        recording.PhaseSink = phase =>
        {
            lock (harness.Sender.PhaseGate)
            {
                harness.Sender.Phases.Add(phase);
            }
        };

        CredentialRecord seed = KnownUnpairedRow();
        await inner.PutAsync(seed, testToken);

        string display = await RequestCodeAsync(harness, randomness, testToken);
        display.Should().MatchRegex("^[0-9A-Z]{4}-[0-9A-Z]{4}$", "the caller gets the XXXX-XXXX display string");
        await WaitForPairingEventAsync(harness.Events, e => e.Status == PairingStatus.Code, "code issued");

        // Step 1: the hello is acknowledged, id-correlated to the hello stanza.
        harness.Pairing.OnStanzaReceived(null, BuildCompanionHello("hello-1"));
        BinaryNode ack = await WaitForSentAsync(harness.Sender.HelloAcks, testToken);
        ack.Tag.Should().Be("iq");
        ack.Attrs["id"].Should().Be("hello-1", "the acknowledgement correlates to the hello stanza");
        ack.Attrs["type"].Should().Be("result");
        HasChild(ack, "companion-hello-ack").Should().BeTrue();

        // Step 2: the finish authenticates under the live code's bundle key
        // and hands the container to the shared verified path.
        byte[] container = BuildSuccessContainer(seed, OrderingUser, OrderingDevice, OrderingRegistrationId);
        byte[] bundle = ProtectUnder(randomness.BundleKey(), container);
        harness.Pairing.OnStanzaReceived(null, BuildCompanionFinish("fin-1", bundle));

        PairingStateChangedEventArgs success = await WaitForPairingEventAsync(
            harness.Events, e => e.Status == PairingStatus.Success, "companion success");
        success.Payload.Should().Be(OrderingUser, "Success carries the verified paired user");

        // The mandated ordering — the code flow reaches Success only through
        // plan 03-04's verify → write → read → reply → event sequence.
        harness.CopyPhases().Should().Equal("Verify", "Put", "Read", "Reply", "Success");
        recording.Puts.Should().Be(1, "exactly one credential write lands on the code success path");
        harness.Sender.Replies.Should().HaveCount(1, "exactly one pair-success ack answers the finish");
        harness.Sender.Replies.Single().Attrs["id"].Should().Be("fin-1");

        CredentialRecord? row = await inner.GetAsync(testToken);
        row.Should().NotBeNull();
        row!.PairedUser.Should().Be(OrderingUser);
        row.Device.Should().Be((int)OrderingDevice);

        await inner.DisposeAsync();
    }

    [Fact]
    public async Task Persist_window_suppresses_watchdog_timeout_until_durable_success()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        AccountId account = NewAccount("persist-window");

        ICredentialStore inner = await CreateEmptyStoreAsync(account, testToken);
        var recording = new RecordingStore(inner);
        var clock = new FakeTimeProvider();
        var slowStore = new SlowPersistStore(
            recording, clock, PairingQrPayload.AttemptTimeout + TimeSpan.FromSeconds(1));
        var randomness = new RecordingRandomness();
        var harness = new OrderingHarness(
            account, slowStore,
            wrapVerifyPhases: true,
            timeProvider: clock,
            randomBytes: randomness.Next);
        recording.PhaseSink = phase =>
        {
            lock (harness.Sender.PhaseGate)
            {
                harness.Sender.Phases.Add(phase);
            }
        };

        CredentialRecord seed = KnownUnpairedRow();
        await inner.PutAsync(seed, testToken);

        await RequestCodeAsync(harness, randomness, testToken);
        await WaitForPairingEventAsync(harness.Events, e => e.Status == PairingStatus.Code, "code issued");

        // The companion handshake runs to the finish; the persist store then
        // lets the whole attempt window elapse WHILE the write is in flight.
        harness.Pairing.OnStanzaReceived(null, BuildCompanionHello("hello-1"));
        await WaitForSentAsync(harness.Sender.HelloAcks, testToken);

        byte[] container = BuildSuccessContainer(seed, OrderingUser, OrderingDevice, OrderingRegistrationId);
        byte[] bundle = ProtectUnder(randomness.BundleKey(), container);
        harness.Pairing.OnStanzaReceived(null, BuildCompanionFinish("fin-1", bundle));

        // The watchdog fires mid-write (WR-02): the durable outcome wins —
        // Success is reported, the racing Timeout is discarded.
        PairingStateChangedEventArgs success = await WaitForPairingEventAsync(
            harness.Events, e => e.Status == PairingStatus.Success, "durable success outranks the watchdog");
        success.Payload.Should().Be(OrderingUser);

        // Settle so a late suppressed Timeout could still surface before the
        // terminal-count assertion — there must be exactly one.
        await Task.Delay(200, testToken);
        harness.Events.Count(e =>
            e.Status is PairingStatus.Success
                or PairingStatus.Timeout
                or PairingStatus.LoggedOut
                or PairingStatus.ClientOutdated)
            .Should().Be(1, "one terminal event, no discarded success");
        harness.Events.Should().NotContain(e => e.Status == PairingStatus.Timeout);

        harness.CopyPhases().Should().Equal("Verify", "Put", "Read", "Reply", "Success");
        recording.Puts.Should().Be(1, "exactly one credential write lands");
        harness.Sender.Replies.Should().HaveCount(1, "exactly one pair-success ack answers the finish");
        harness.Sender.Replies.Single().Attrs["id"].Should().Be("fin-1");

        CredentialRecord? row = await inner.GetAsync(testToken);
        row.Should().NotBeNull("the durable row survives the suppressed timeout");
        row!.PairedUser.Should().Be(OrderingUser);
        row.Device.Should().Be((int)OrderingDevice);

        await inner.DisposeAsync();
    }

    [Fact]
    public async Task Companion_finish_without_hello_is_dropped_before_verification()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        AccountId account = NewAccount("companion-bypass");

        ICredentialStore inner = await CreateEmptyStoreAsync(account, testToken);
        var recording = new RecordingStore(inner);
        var randomness = new RecordingRandomness();
        var harness = new OrderingHarness(
            account, recording, wrapVerifyPhases: true, randomBytes: randomness.Next);

        CredentialRecord seed = KnownUnpairedRow();
        await inner.PutAsync(seed, testToken);

        await RequestCodeAsync(harness, randomness, testToken);
        await WaitForPairingEventAsync(harness.Events, e => e.Status == PairingStatus.Code, "code issued");

        // A finish that SKIPS the handshake — correctly sealed under the live
        // bundle key, so the sequence gate alone must reject it.
        byte[] container = BuildSuccessContainer(seed, OrderingUser, OrderingDevice, OrderingRegistrationId);
        byte[] bundle = ProtectUnder(randomness.BundleKey(), container);
        harness.Pairing.OnStanzaReceived(null, BuildCompanionFinish("fin-bypass", bundle));
        await Task.Delay(300, testToken);

        harness.CopyPhases().Should().BeEmpty("no verification runs when the sequence is skipped");
        recording.Puts.Should().Be(0, "no credential write without the handshake");
        harness.Sender.Replies.Should().BeEmpty("no reply without verification");
        harness.Sender.HelloAcks.Should().BeEmpty("no acknowledgement without a hello");
        harness.Events.Should().NotContain(e => e.Status == PairingStatus.Success);

        // The attempt stays live: a hello afterwards still acks (the drop is
        // side-effect free, not fatal).
        harness.Pairing.OnStanzaReceived(null, BuildCompanionHello("hello-late"));
        await WaitForSentAsync(harness.Sender.HelloAcks, testToken);

        await inner.DisposeAsync();
    }

    [Fact]
    public async Task Companion_stall_after_hello_surfaces_typed_timeout_and_resets_attempt()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        AccountId account = NewAccount("companion-stall");

        ICredentialStore inner = await CreateEmptyStoreAsync(account, testToken);
        var recording = new RecordingStore(inner);
        var randomness = new RecordingRandomness();
        var clock = new FakeTimeProvider();
        var harness = new OrderingHarness(
            account, recording,
            wrapVerifyPhases: true,
            timeProvider: clock,
            randomBytes: randomness.Next);

        CredentialRecord seed = KnownUnpairedRow();
        await inner.PutAsync(seed, testToken);

        await RequestCodeAsync(harness, randomness, testToken);
        await WaitForPairingEventAsync(harness.Events, e => e.Status == PairingStatus.Code, "code issued");

        // The companion flow stalls after the hello: the finish never arrives
        // (Pitfall 4's phone-side mismatch mode, which upstream sees only as a
        // bare timeout) — the coordinator reports the TYPED outcome.
        harness.Pairing.OnStanzaReceived(null, BuildCompanionHello("hello-1"));
        await WaitForSentAsync(harness.Sender.HelloAcks, testToken);
        clock.Advance(PairingQrPayload.AttemptTimeout + TimeSpan.FromSeconds(1));

        PairingStateChangedEventArgs timeout = await WaitForPairingEventAsync(
            harness.Events, e => e.Status == PairingStatus.Timeout, "typed timeout");
        timeout.Payload.Should().BeNull("a timeout carries no payload");
        recording.Puts.Should().Be(0, "a stalled handshake never writes");

        // The attempt reset — no wedged coordinator: a fresh code can be
        // requested and issued on the same instance.
        string again = await RequestCodeAsync(harness, randomness, testToken);
        again.Should().NotBeNullOrWhiteSpace();
        await WaitForPairingEventAsync(
            harness.Events,
            e => e.Status == PairingStatus.Code
                && harness.Events.Count(x => x.Status == PairingStatus.Code) >= 2,
            "second code issued");

        await inner.DisposeAsync();
    }

    [Fact]
    public async Task Companion_finish_with_foreign_bundle_key_fails_closed()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        AccountId account = NewAccount("companion-tamper");

        ICredentialStore inner = await CreateEmptyStoreAsync(account, testToken);
        var recording = new RecordingStore(inner);
        var randomness = new RecordingRandomness();
        var harness = new OrderingHarness(
            account, recording, wrapVerifyPhases: true, randomBytes: randomness.Next);

        CredentialRecord seed = KnownUnpairedRow();
        await inner.PutAsync(seed, testToken);

        await RequestCodeAsync(harness, randomness, testToken);
        await WaitForPairingEventAsync(harness.Events, e => e.Status == PairingStatus.Code, "code issued");
        harness.Pairing.OnStanzaReceived(null, BuildCompanionHello("hello-1"));
        await WaitForSentAsync(harness.Sender.HelloAcks, testToken);

        // A bundle sealed under a key that is NOT the live code's (wrong epoch
        // or a tampered ciphertext): the GCM tag must not open.
        byte[] container = BuildSuccessContainer(seed, OrderingUser, OrderingDevice, OrderingRegistrationId);
        byte[] foreignKey = FixedBytes(0x77);
        harness.Pairing.OnStanzaReceived(null, BuildCompanionFinish("fin-bad", ProtectUnder(foreignKey, container)));
        await Task.Delay(300, testToken);

        harness.CopyPhases().Should().BeEmpty("a bundle that fails authentication never reaches verification");
        recording.Puts.Should().Be(0, "no credential write on a tag mismatch");
        harness.Sender.Replies.Should().BeEmpty("no reply on a tag mismatch");
        harness.Events.Should().NotContain(e => e.Status == PairingStatus.Success);

        // Fail-closed is not fatal: the attempt survives and a correctly
        // sealed finish still completes the verified path.
        harness.Pairing.OnStanzaReceived(
            null, BuildCompanionFinish("fin-good", ProtectUnder(randomness.BundleKey(), container)));
        PairingStateChangedEventArgs success = await WaitForPairingEventAsync(
            harness.Events, e => e.Status == PairingStatus.Success, "verified success after the rejection");
        success.Payload.Should().Be(OrderingUser);
        recording.Puts.Should().Be(1, "only the authenticated finish writes");

        await inner.DisposeAsync();
    }

    [Fact]
    public async Task Companion_finish_from_a_rotated_code_epoch_is_rejected()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        AccountId account = NewAccount("companion-epoch");

        ICredentialStore inner = await CreateEmptyStoreAsync(account, testToken);
        var recording = new RecordingStore(inner);
        var randomness = new RecordingRandomness();
        var clock = new FakeTimeProvider();
        var harness = new OrderingHarness(
            account, recording,
            wrapVerifyPhases: true,
            timeProvider: clock,
            randomBytes: randomness.Next);

        CredentialRecord seed = KnownUnpairedRow();
        await inner.PutAsync(seed, testToken);

        // Code epoch 1: issued, then the attempt ends (watchdog).
        await RequestCodeAsync(harness, randomness, testToken);
        await WaitForPairingEventAsync(harness.Events, e => e.Status == PairingStatus.Code, "first code");
        byte[] staleBundleKey = randomness.BundleKey();
        clock.Advance(PairingQrPayload.AttemptTimeout + TimeSpan.FromSeconds(1));
        await WaitForPairingEventAsync(
            harness.Events, e => e.Status == PairingStatus.Timeout, "first attempt timed out");

        // Code epoch 2: a fresh attempt derives a fresh bundle key from the
        // new code — the epoch is bound in key material, not in a counter.
        await RequestCodeAsync(harness, randomness, testToken);
        await WaitForPairingEventAsync(
            harness.Events,
            e => e.Status == PairingStatus.Code
                && harness.Events.Count(x => x.Status == PairingStatus.Code) >= 2,
            "second code issued");
        harness.Pairing.OnStanzaReceived(null, BuildCompanionHello("hello-2"));
        await WaitForSentAsync(harness.Sender.HelloAcks, testToken);

        byte[] container = BuildSuccessContainer(seed, OrderingUser, OrderingDevice, OrderingRegistrationId);
        harness.Pairing.OnStanzaReceived(
            null, BuildCompanionFinish("fin-stale", ProtectUnder(staleBundleKey, container)));
        await Task.Delay(300, testToken);

        harness.CopyPhases().Should().BeEmpty("the prior code's material never opens under the live code");
        recording.Puts.Should().Be(0, "a rotated-epoch finish never writes");
        harness.Events.Should().NotContain(e => e.Status == PairingStatus.Success);

        // The live epoch still finishes normally.
        harness.Pairing.OnStanzaReceived(
            null, BuildCompanionFinish("fin-live", ProtectUnder(randomness.BundleKey(), container)));
        PairingStateChangedEventArgs success = await WaitForPairingEventAsync(
            harness.Events, e => e.Status == PairingStatus.Success, "live-epoch success");
        success.Payload.Should().Be(OrderingUser);
        recording.Puts.Should().Be(1);

        await inner.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // Task 4 (03-05): the pairing-CODE entry contract — the five plan
    // behaviors: a deterministic happy path, the single-attempt rule across
    // both entry points, cancellation and server-failure resets, and typed
    // phone-number validation. The fake never supplies a code; every
    // assertion re-derives what the coordinator consumed.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Pairing_code_happy_path_display_is_deterministic_under_injected_randomness()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        AccountId account = NewAccount("code-happy");

        ICredentialStore inner = await CreateEmptyStoreAsync(account, testToken);
        var recording = new RecordingStore(inner);
        var randomness = new RecordingRandomness();
        var harness = new OrderingHarness(
            account, recording, wrapVerifyPhases: true, randomBytes: randomness.Next);

        CredentialRecord seed = KnownUnpairedRow();
        await inner.PutAsync(seed, testToken);

        string display = await RequestCodeAsync(harness, randomness, testToken);

        // The display is not random noise from the test's point of view: it
        // is exactly ToDisplay(GenerateCode(injected bytes)) — the
        // coordinator consumed the scripted code bytes and nothing else.
        display.Should().Be(
            randomness.ExpectedDisplay(),
            "the issued display must re-derive from the exact code bytes the coordinator drew");
        display.Should().MatchRegex("^[0-9A-Z]{4}-[0-9A-Z]{4}$", "the host receives the XXXX-XXXX display string");

        PairingStateChangedEventArgs code = await WaitForPairingEventAsync(
            harness.Events, e => e.Status == PairingStatus.Code, "code issued");
        code.Payload.Should().Be(display, "the Code event carries the same display the call returns");

        await inner.DisposeAsync();
    }

    [Fact]
    public async Task Pairing_code_single_attempt_rejects_second_entry_point()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        AccountId account = NewAccount("code-single");

        ICredentialStore inner = await CreateEmptyStoreAsync(account, testToken);
        var recording = new RecordingStore(inner);
        var randomness = new RecordingRandomness();
        var harness = new OrderingHarness(
            account, recording, wrapVerifyPhases: true, randomBytes: randomness.Next);

        CredentialRecord seed = KnownUnpairedRow();
        await inner.PutAsync(seed, testToken);

        await RequestCodeAsync(harness, randomness, testToken);
        await WaitForPairingEventAsync(harness.Events, e => e.Status == PairingStatus.Code, "first code issued");

        // The attempt is live until its terminal event: both entry points
        // must refuse a second attempt rather than silently sharing the
        // coordinator's single _attempt slot.
        Func<Task> secondCode = () => harness.Pairing.RequestPairingCodeAsync(CodePhone, testToken);
        await secondCode.Should().ThrowAsync<InvalidOperationException>(
            "the code entry must not start a second attempt while one is live");

        Func<Task> qrEntry = () => harness.Pairing.StartAsync(testToken);
        await qrEntry.Should().ThrowAsync<InvalidOperationException>(
            "the QR entry obeys the same single-attempt rule");

        // Neither rejection produced wire traffic — the first request's
        // frame was dequeued by the helper, so the queue proves silence.
        harness.Sender.CodeRequests.Should().BeEmpty("a rejected entry never reaches the wire");

        await inner.DisposeAsync();
    }

    [Fact]
    public async Task Pairing_code_cancellation_resets_attempt_before_any_iq()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        AccountId account = NewAccount("code-cancel");

        ICredentialStore inner = await CreateEmptyStoreAsync(account, testToken);
        var gate = new GateFirstGetStore(inner);
        var randomness = new RecordingRandomness();
        var harness = new OrderingHarness(
            account, gate, wrapVerifyPhases: true, randomBytes: randomness.Next);

        // Park the attempt inside the registration bootstrap: the armed gate
        // blocks the credential read, so cancellation lands BEFORE code
        // generation and before any iq can leave the fake.
        gate.Arm();
        using var cancelCts = new CancellationTokenSource();
        Task<string> request = harness.Pairing.RequestPairingCodeAsync(CodePhone, cancelCts.Token);
        await gate.Entered.WaitAsync(testToken);
        cancelCts.Cancel();

        Func<Task> awaiting = () => request;
        await awaiting.Should().ThrowAsync<OperationCanceledException>(
            "host cancellation surfaces unwrapped from the bootstrap");

        harness.Sender.CodeRequests.Should().BeEmpty("the cancel landed before the request reached the wire");
        harness.Events.Should().BeEmpty("a cancelled request issues no Code event");

        // The attempt was cleared, not wedged: the same coordinator — with
        // the gate now pass-through — issues a real code on retry.
        string display = await RequestCodeAsync(harness, randomness, testToken);
        display.Should().Be(randomness.ExpectedDisplay(), "the retry runs the full derivation again");
        await WaitForPairingEventAsync(
            harness.Events, e => e.Status == PairingStatus.Code, "code issued after the cancellation reset");

        await inner.DisposeAsync();
    }

    [Fact]
    public async Task Pairing_code_failure_reply_resets_attempt()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        AccountId account = NewAccount("code-failure");

        ICredentialStore inner = await CreateEmptyStoreAsync(account, testToken);
        var recording = new RecordingStore(inner);
        var randomness = new RecordingRandomness();
        var harness = new OrderingHarness(
            account, recording, wrapVerifyPhases: true, randomBytes: randomness.Next);

        CredentialRecord seed = KnownUnpairedRow();
        await inner.PutAsync(seed, testToken);

        randomness.BeginAttempt();
        Task<string> request = harness.Pairing.RequestPairingCodeAsync(CodePhone, testToken);
        BinaryNode sent = await WaitForSentAsync(harness.Sender.CodeRequests, testToken);

        // A correlated server rejection (never 405 — that is the bounded
        // version-refetch path, not a terminal rejection): the request fails
        // descriptively and the attempt is cleared with it.
        harness.Pairing.OnStanzaReceived(null, BuildCodeErrorReply(sent.Attrs["id"], "406"));
        Func<Task> awaiting = () => request;
        await awaiting.Should().ThrowAsync<SdkConnectionException>()
            .WithMessage("*rejected by the server (code 406)*");

        harness.Events.Should().BeEmpty("a rejected request never issues a Code event");
        harness.Sender.CodeRequests.Should().BeEmpty("the failed request's frame was consumed above");

        // Reset proof: the same coordinator issues a fresh code afterwards,
        // deriving the display from the second attempt's own code bytes.
        string display = await RequestCodeAsync(harness, randomness, testToken);
        display.Should().Be(randomness.ExpectedDisplay(), "the retry derives from the fresh attempt's bytes");
        await WaitForPairingEventAsync(
            harness.Events, e => e.Status == PairingStatus.Code, "code issued after the failure reset");

        await inner.DisposeAsync();
    }

    [Fact]
    public async Task Pairing_code_validation_rejects_bad_phone_numbers_with_typed_reasons()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        AccountId account = NewAccount("code-validation");

        ICredentialStore inner = await CreateEmptyStoreAsync(account, testToken);
        var recording = new RecordingStore(inner);
        var harness = new OrderingHarness(
            account, recording, wrapVerifyPhases: true, randomBytes: new RecordingRandomness().Next);

        (string Input, PairingCodeValidationReason Reason)[] cases =
        [
            ("+15551234567", PairingCodeValidationReason.NonDigits),
            ("1555abc5678", PairingCodeValidationReason.NonDigits),
            ("123456", PairingCodeValidationReason.TooShort),
            ("0123456789", PairingCodeValidationReason.LeadingZero),
        ];

        foreach ((string input, PairingCodeValidationReason reason) in cases)
        {
            Func<Task> attempt = () => harness.Pairing.RequestPairingCodeAsync(input, testToken);
            var thrown = await attempt.Should().ThrowAsync<PairingCodeRequestException>(
                $"'{input}' violates the {reason} rule before any wire work");
            thrown.Which.Reason.Should().Be(reason);
            thrown.Which.Message.Should().NotContain(input, "the offending input is never echoed back");
        }

        harness.Sender.CodeRequests.Should().BeEmpty("validation runs before generation or the wire");
        harness.Events.Should().BeEmpty("no Code event fires for rejected input");

        await inner.DisposeAsync();
    }
}
