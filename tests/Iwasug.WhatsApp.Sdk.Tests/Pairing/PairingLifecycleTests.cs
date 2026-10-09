using System.Collections.Concurrent;
using System.Security.Cryptography;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Pairing;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.InMemory;
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

        private readonly ICredentialStore _inner;

        internal RecordingStore(ICredentialStore inner)
        {
            _inner = inner;
        }

        public async Task<CredentialRecord?> GetAsync(CancellationToken cancellationToken)
        {
            Calls.Enqueue("Get");
            return await _inner.GetAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task PutAsync(CredentialRecord credentials, CancellationToken cancellationToken)
        {
            Calls.Enqueue("Put");
            Interlocked.Increment(ref Puts);
            await _inner.PutAsync(credentials, cancellationToken).ConfigureAwait(false);
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
}
