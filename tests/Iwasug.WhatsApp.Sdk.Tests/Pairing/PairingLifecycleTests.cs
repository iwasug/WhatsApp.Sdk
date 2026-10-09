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

    private static byte[] Ed25519PublicOf(byte[] seed)
    {
        byte[] copy = (byte[])seed.Clone();
        try
        {
            using Key key = Key.Import(SignatureAlgorithm.Ed25519, copy, KeyBlobFormat.RawPrivateKey);
            return key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    private static byte[] Ed25519Sign(byte[] seed, byte[] data)
    {
        byte[] copy = (byte[])seed.Clone();
        try
        {
            using Key key = Key.Import(SignatureAlgorithm.Ed25519, copy, KeyBlobFormat.RawPrivateKey);
            return SignatureAlgorithm.Ed25519.Sign(key, data);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    private static byte[] Join(params byte[][] segments)
    {
        int total = 0;
        foreach (byte[] segment in segments)
        {
            total += segment.Length;
        }

        byte[] result = new byte[total];
        int offset = 0;
        foreach (byte[] segment in segments)
        {
            segment.CopyTo(result.AsSpan(offset));
            offset += segment.Length;
        }

        return result;
    }

    private static byte[] BuildSuccessContainer(CredentialRecord row, string user, uint device, uint registrationId)
    {
        byte[] accountSeed = FixedBytes(0x50);
        byte[] accountPublic = Ed25519PublicOf(accountSeed);
        var details = new AdvDetails
        {
            PairedUser = user,
            Device = device,
            RegistrationId = registrationId,
            AccountPublic = ByteString.CopyFrom(accountPublic),
        };
        byte[] detailsBytes = details.ToByteArray();
        byte[] hmac = HMACSHA256.HashData(row.AdvSecret, detailsBytes);
        byte[] accountSignature = Ed25519Sign(accountSeed, Join([6, 0], detailsBytes));
        byte[] deviceSignature = Ed25519Sign(row.SigningKeyPrivate, Join([6, 1], detailsBytes, accountPublic));
        var container = new PairSuccessContainer
        {
            Details = details,
            Hmac = ByteString.CopyFrom(hmac),
            AccountSignature = ByteString.CopyFrom(accountSignature),
            DeviceSignature = ByteString.CopyFrom(deviceSignature),
        };
        return container.ToByteArray();
    }

    private static BinaryNode BuildRefReply(string id, string pairingRef) =>
        BinaryNode.FromChildren(
            "iq",
            [BinaryNode.Create("pair-device", new Dictionary<string, string> { ["ref"] = pairingRef })],
            new Dictionary<string, string> { ["id"] = id, ["type"] = "result" });

    private static BinaryNode BuildSuccessNotification(string id, byte[] container) =>
        BinaryNode.FromChildren(
            "notification",
            [BinaryNode.FromBytes("pair-success", container)],
            new Dictionary<string, string> { ["id"] = id });

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
        internal readonly ConcurrentQueue<BinaryNode> Replies = new();
        internal readonly List<string> Phases = new();
        internal readonly object PhaseGate = new();
        internal TaskCompletionSource? BlockReplies;

        internal async ValueTask SendAsync(BinaryNode node, CancellationToken cancellationToken)
        {
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

        internal OrderingHarness(AccountId account, ICredentialStore store, bool wrapVerifyPhases)
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
                timeProvider: null,
                resolver: null,
                logger: null);
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
}
