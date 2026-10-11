using System.Text;
using AwesomeAssertions;
using Google.Protobuf;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Signal;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.InMemory;
using Iwasug.WhatsApp.Sdk.Stores.Runtime;
using NSubstitute;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Signal;

/// <summary>
/// Durable-session proofs (04-01 Task 2, Pitfalls 2/5): ratchet state
/// round-trips through <c>SessionStructure</c> bytes, sessions are keyed by
/// the full per-device AD JID, skipped keys are bounded and fail-closed, and
/// a persist failure never leaves state ahead of the durable bytes.
/// </summary>
/// <remarks>
/// Output discipline (V6/T-4-01, NoiseVectorTests precedent): key material is
/// compared as SHA-256 fingerprints or not at all; assertions never carry
/// raw secrets.
/// </remarks>
public sealed class SignalSessionTests
{
    private sealed record SyntheticPeer(
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

    private static SyntheticPeer CreateSyntheticPeer(uint signedPreKeyId, uint oneTimePreKeyId, uint registrationId)
    {
        (byte[] xPriv, byte[] xPub) = X3dh.GenerateX25519KeyPair();
        (byte[] edPriv, byte[] edPubDjb) = X3dh.GenerateIdentityKeyPair();
        (byte[] spkPriv, byte[] spkPub) = X3dh.GenerateX25519KeyPair();
        byte[] spkSig = X3dh.SignWithIdentity(edPriv, SignalCipher.ToDjbIdentity(spkPub));
        (byte[] otPriv, byte[] otPub) = X3dh.GenerateX25519KeyPair();
        return new SyntheticPeer(
            xPriv, SignalCipher.ToDjbIdentity(xPub),
            edPriv, edPubDjb,
            spkPriv, spkPub, spkSig, signedPreKeyId,
            otPriv, otPub, oneTimePreKeyId,
            registrationId);
    }

    private static (DoubleRatchet Alice, DoubleRatchet Bob, PeerBundle Bundle) CreatePair(
        uint aliceReg, uint bobReg)
    {
        SyntheticPeer aliceKeys = CreateSyntheticPeer(1, 101, aliceReg);
        SyntheticPeer bobKeys = CreateSyntheticPeer(22, 1101, bobReg);
        var bundle = new PeerBundle(
            bobKeys.XIdentityDjb,
            bobKeys.SignedPreKeyPublic,
            bobKeys.SignedPreKeySignature,
            bobKeys.SignedPreKeyId,
            bobKeys.OneTimePublic,
            bobKeys.OneTimePreKeyId,
            bobKeys.RegistrationId);

        (byte[] ephPriv, _) = X3dh.GenerateX25519KeyPair();
        AliceSession aliceSession = X3dh.InitializeAlice(aliceKeys.XIdentityPrivate, ephPriv, bundle, bobKeys.EdPublicDjb);
        BobSession bobSession = X3dh.InitializeBob(
            bobKeys.XIdentityPrivate, bobKeys.SignedPreKeyPrivate, bobKeys.OneTimePrivate, true,
            aliceKeys.XIdentityDjb, aliceSession.BasePublic);

        var alice = DoubleRatchet.InitializeAlice(
            aliceSession, ephPriv, aliceKeys.XIdentityDjb, bobKeys.XIdentityDjb, bundle, aliceReg);
        var bob = DoubleRatchet.InitializeBob(
            bobSession, bobKeys.XIdentityDjb, aliceKeys.XIdentityDjb, aliceSession.BasePublic, bobReg, aliceReg);
        return (alice, bob, bundle);
    }

    private static byte[] EncryptAs(DoubleRatchet ratchet, string text, out bool preKey)
    {
        SignalEncryptOutcome outcome = ratchet.Encrypt(Encoding.UTF8.GetBytes(text), CancellationToken.None);
        if (outcome is SignalEncryptOutcome.PreKeyMessageSent preKeySent)
        {
            preKey = true;
            return preKeySent.WireBytes;
        }

        preKey = false;
        return ((SignalEncryptOutcome.MessageSent)outcome).WireBytes;
    }

    private static string DecryptAs(DoubleRatchet ratchet, byte[] wire, bool isPreKey)
    {
        SignalDecryptOutcome outcome = ratchet.Decrypt(wire, isPreKey, CancellationToken.None);
        return Encoding.UTF8.GetString(
            outcome.Should().BeOfType<SignalDecryptOutcome.Decrypted>().Subject.Plaintext);
    }

    [Fact]
    public async Task serialize_roundtrip_restart_proof_through_the_store_binding()
    {
        (DoubleRatchet alice, DoubleRatchet bob, _) = CreatePair(1001, 1234);
        await using var factory = new InMemoryStoreFactory();
        ISignalSessionStore sessions =
            await factory.CreateAsync(AccountId.Create("serialize-roundtrip"), CancellationToken.None);
        var repository = new DoubleRatchet.SignalSessionRepository(sessions, new DurableWriteRetryPolicy());
        string address = DoubleRatchet.SignalSessionRepository.AddressFor(Jid.Parse("alice.phone.0@s.whatsapp.net"));

        // A fresh Alice still owes the prekey form after a restart: the
        // bundle coordinates survive the roundtrip with the flag.
        DoubleRatchet reloadedAlice = DoubleRatchet.Deserialize(alice.Serialize());
        byte[] firstWire = EncryptAs(reloadedAlice, "m0", out bool firstPreKey);
        firstPreKey.Should().BeTrue("a fresh Alice session still owes its first-contact prekey message");
        var firstEnvelope = SignalCipher.DecryptPreKeyEnvelope(firstWire)
            .Should().BeOfType<PreKeyEnvelopeOutcome.Parsed>().Subject.Content;
        firstEnvelope.SignedPreKeyId.Should().Be(22);
        firstEnvelope.PreKeyId.Should().Be(1101);

        // Bob establishes, persists, and "restarts": the next message
        // decrypts from freshly loaded bytes.
        DecryptAs(bob, firstWire, isPreKey: true).Should().Be("m0");
        await repository.SaveAsync(address, bob, CancellationToken.None);

        DoubleRatchet? restarted = await repository.LoadAsync(address, CancellationToken.None);
        restarted.Should().NotBeNull();
        byte[] secondWire = EncryptAs(reloadedAlice, "m1", out bool secondPreKey);
        secondPreKey.Should().BeFalse();
        DecryptAs(restarted!, secondWire, isPreKey: false).Should().Be("m1");

        // And a post-first-send Alice reload sends the regular form.
        DoubleRatchet reloadedSender = DoubleRatchet.Deserialize(reloadedAlice.Serialize());
        EncryptAs(reloadedSender, "m2", out bool thirdPreKey).Should().NotBeNull();
        thirdPreKey.Should().BeFalse("an established Alice session never re-sends prekey form");
    }

    [Fact]
    public void out_of_order_delivery_decrypts_each_message_exactly_once()
    {
        (DoubleRatchet alice, DoubleRatchet bob, _) = CreatePair(1001, 1234);

        byte[] m0 = EncryptAs(alice, "m0", out _);
        byte[] m1 = EncryptAs(alice, "m1", out _);
        byte[] m2 = EncryptAs(alice, "m2", out _);

        // Reverse arrival: the last message stages the gap, the prekey first
        // message replays through its staged key, then the middle fills in.
        DecryptAs(bob, m2, isPreKey: false).Should().Be("m2");
        bob.SkippedKeyCount.Should().Be(2);
        DecryptAs(bob, m0, isPreKey: true).Should().Be("m0");
        DecryptAs(bob, m1, isPreKey: false).Should().Be("m1");
        bob.SkippedKeyCount.Should().Be(0, "every staged key is consumed exactly once");

        // Re-delivery of a consumed counter is a typed failure, not a second plaintext.
        bob.Decrypt(m1, isPreKey: false, CancellationToken.None)
            .Should().BeOfType<SignalDecryptOutcome.Failed>()
            .Which.Reason.Should().Be(SignalDecryptFailure.OldCounter);
    }

    [Fact]
    public void skipped_key_bound_breach_is_typed_and_the_session_still_accepts_in_order()
    {
        (DoubleRatchet alice, DoubleRatchet bob, _) = CreatePair(1001, 1234);

        var wires = new List<byte[]> { EncryptAs(alice, "m0", out _) };
        for (int i = 1; i <= DoubleRatchet.MaxSkippedMessageKeys + 5; i++)
        {
            wires.Add(EncryptAs(alice, $"m{i}", out _));
        }

        // A gap wider than the bound fails closed with state untouched.
        byte[] last = wires[wires.Count - 1];
        bob.Decrypt(last, isPreKey: false, CancellationToken.None)
            .Should().BeOfType<SignalDecryptOutcome.Failed>()
            .Which.Reason.Should().Be(SignalDecryptFailure.SkippedKeyLimit);
        bob.SkippedKeyCount.Should().Be(0, "a cap breach stages nothing");

        // The next in-order messages still decrypt afterwards.
        DecryptAs(bob, wires[0], isPreKey: true).Should().Be("m0");
        DecryptAs(bob, wires[1], isPreKey: false).Should().Be("m1");
    }

    [Fact]
    public async Task deserializ_tampered_blob_fails_typed_never_partial()
    {
        (DoubleRatchet alice, DoubleRatchet bob, _) = CreatePair(1001, 1234);
        await using var factory = new InMemoryStoreFactory();
        ISignalSessionStore sessions =
            await factory.CreateAsync(AccountId.Create("deserializ-tamper"), CancellationToken.None);
        var repository = new DoubleRatchet.SignalSessionRepository(sessions, new DurableWriteRetryPolicy());
        string address = DoubleRatchet.SignalSessionRepository.AddressFor(Jid.Parse("alice.phone.0@s.whatsapp.net"));
        await repository.SaveAsync(address, bob, CancellationToken.None);

        SignalSessionRecord? stored = await sessions.GetAsync(address, CancellationToken.None);
        stored.Should().NotBeNull();
        byte[] valid = stored!.SessionState;

        byte[] flipped = (byte[])valid.Clone();
        flipped[3] = 0x7F;
        Action loadFlipped = () => DoubleRatchet.Deserialize(flipped);
        loadFlipped.Should().Throw<SdkFormatException>(
            "a corrupted length prefix is not a well-formed session");

        Action loadTruncated = () => DoubleRatchet.Deserialize(valid.AsSpan(0, 10).ToArray());
        loadTruncated.Should().Throw<SdkFormatException>();

        byte[] chopped = valid.AsSpan(0, valid.Length - 8).ToArray();
        Action loadChopped = () => DoubleRatchet.Deserialize(chopped);
        loadChopped.Should().Throw<SdkFormatException>("a torn tail is not a well-formed session");

        var wrongVersion = SessionStructure.Parser.ParseFrom(valid);
        wrongVersion.SessionVersion = 2;
        Action loadWrongVersion = () => DoubleRatchet.Deserialize(wrongVersion.ToByteArray());
        loadWrongVersion.Should().Throw<SdkFormatException>("only v1 sessions are accepted");

        // A flip inside key bytes stays well-formed (lengths hold) but the
        // session is unusable: flipping the trailing identity byte breaks the
        // base-key pin, so the next prekey delivery fails typed instead of
        // establishing. Structural validation is the documented mitigation;
        // use-time authentication is the backstop.
        byte[] keyFlipped = (byte[])valid.Clone();
        keyFlipped[keyFlipped.Length - 1] ^= 0xFF;
        DoubleRatchet forged = DoubleRatchet.Deserialize(keyFlipped);
        byte[] probe = EncryptAs(alice, "probe", out _);
        forged.Decrypt(probe, isPreKey: true, CancellationToken.None)
            .Should().BeOfType<SignalDecryptOutcome.Failed>()
            .Which.Reason.Should().Be(SignalDecryptFailure.UnexpectedPreKey);

        // The durable bytes are untouched by the failed loads.
        SignalSessionRecord? reloaded = await sessions.GetAsync(address, CancellationToken.None);
        reloaded!.SessionState.Should().Equal(valid);
    }

    [Fact]
    public async Task per_device_jid_pair_sessions_never_collide()
    {
        (DoubleRatchet alice1, DoubleRatchet bob1, _) = CreatePair(1001, 1234);
        (DoubleRatchet alice2, DoubleRatchet bob2, _) = CreatePair(2001, 2234);
        await using var factory = new InMemoryStoreFactory();
        ISignalSessionStore sessions =
            await factory.CreateAsync(AccountId.Create("per-device"), CancellationToken.None);
        var repository = new DoubleRatchet.SignalSessionRepository(sessions, new DurableWriteRetryPolicy());

        string device1 = DoubleRatchet.SignalSessionRepository.AddressFor(Jid.Parse("alice.phone.1@s.whatsapp.net"));
        string device2 = DoubleRatchet.SignalSessionRepository.AddressFor(Jid.Parse("alice.phone.2@s.whatsapp.net"));
        device1.Should().Be("alice.phone.1@s.whatsapp.net", "sessions key by the full per-device AD JID");
        device1.Should().NotBe(device2, "two devices of one user never share a session");

        await repository.SaveAsync(device1, bob1, CancellationToken.None);
        await repository.SaveAsync(device2, bob2, CancellationToken.None);

        DoubleRatchet? loaded1 = await repository.LoadAsync(device1, CancellationToken.None);
        loaded1.Should().NotBeNull();
        DoubleRatchet? loaded2 = await repository.LoadAsync(device2, CancellationToken.None);
        loaded2.Should().NotBeNull();

        // Each device session decrypts only its own peer's stream.
        byte[] forDevice1 = EncryptAs(alice1, "for device 1", out _);
        DecryptAs(loaded1!, forDevice1, isPreKey: true).Should().Be("for device 1");
        loaded2!.Decrypt(forDevice1, isPreKey: true, CancellationToken.None)
            .Should().BeOfType<SignalDecryptOutcome.Failed>(
                "a ciphertext for device 1 never opens under device 2's chain");
    }

    [Fact]
    public async Task persist_throw_keeps_the_in_memory_state_on_last_durable_bytes()
    {
        (DoubleRatchet alice, DoubleRatchet bob, _) = CreatePair(1001, 1234);
        string address = DoubleRatchet.SignalSessionRepository.AddressFor(Jid.Parse("alice.phone.0@s.whatsapp.net"));

        // Bob establishes and persists m0; those bytes are the durability horizon.
        DecryptAs(bob, EncryptAs(alice, "m0", out _), isPreKey: true).Should().Be("m0");
        byte[] durable = bob.Serialize();

        var store = Substitute.For<ISignalSessionStore>();
        store.GetAsync(address, Arg.Any<CancellationToken>())
            .Returns(new SignalSessionRecord(address, (byte[])durable.Clone()));
        store.PutAsync(
                Arg.Any<string>(), Arg.Any<SignalSessionRecord>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new SdkStoreException("Simulated durable fault.")));
        var repository = new DoubleRatchet.SignalSessionRepository(store, new DurableWriteRetryPolicy());

        // Advance in memory, then fail the persist: the failure is loud.
        bob.Encrypt(Encoding.UTF8.GetBytes("bob reply"), CancellationToken.None);
        Func<Task> save = () => repository.SaveAsync(address, bob, CancellationToken.None);
        await save.Should().ThrowAsync<SdkStoreException>();

        // A fresh load observes exactly the last durable bytes, still functional.
        DoubleRatchet? reloaded = await repository.LoadAsync(address, CancellationToken.None);
        reloaded.Should().NotBeNull();
        reloaded!.Serialize().Should().Equal(durable, "no unpersisted advance survives a persist failure");
        DecryptAs(reloaded!, EncryptAs(alice, "m1", out _), isPreKey: false).Should().Be("m1");
    }

    [Fact]
    public async Task per_address_gate_serializes_mutation_and_survives_cancellation()
    {
        const string address = "gate-proof-address";
        IDisposable first = await SignalSessionGate.AcquireAsync(address, CancellationToken.None);

        Task<IDisposable> second = SignalSessionGate.AcquireAsync(address, CancellationToken.None).AsTask();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        second.IsCompleted.Should().BeFalse("the second holder waits while the first holds the gate");
        first.Dispose();
        (await second).Dispose();

        IDisposable other = await SignalSessionGate.AcquireAsync("unrelated-address", CancellationToken.None);
        other.Dispose();

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Func<Task> cancelled = () => SignalSessionGate.AcquireAsync(address, cts.Token).AsTask();
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        IDisposable afterCancel = await SignalSessionGate.AcquireAsync(address, CancellationToken.None);
        afterCancel.Dispose();
    }
}
