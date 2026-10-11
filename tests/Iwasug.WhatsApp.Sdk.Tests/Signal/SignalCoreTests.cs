using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Signal;
using NSec.Cryptography;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Signal;

/// <summary>
/// Wave-0 spike proofs (04-01 Task 1, MSG-01 crypto half, T-4-02): the pinned
/// byte layout in <c>tests/.../TestData/signal-kats.json</c> reproduces
/// byte-exact through <see cref="SignalCipher"/>, X3DH agreement converges
/// for a synthetic peer pair, and one full X3DH→ratchet→wire→decrypt
/// roundtrip proves the layout end-to-end. No Signal ciphertext leaves the
/// process in this plan.
/// </summary>
/// <remarks>
/// <para>
/// Fixture provenance: schemas and wire layout learned read-only from Baileys
/// MIT sources at commit 0af2386 (WAProto.proto, src/Signal/libsignal.ts,
/// src/Utils/signal.ts, src/Utils/crypto.ts) plus the public Signal X3DH
/// specification rev 1; no whatsmeow bytes involved. Vectors are
/// self-consistent pinning of that published layout — not live-captured
/// traffic; the 04-05 live-device UAT is the interop backstop (notably the
/// XEdDSA-vs-Ed25519 signed-prekey question documented on
/// <see cref="X3dh.InitializeAlice"/>).
/// </para>
/// <para>
/// Output discipline (V6/T-4-01, NoiseVectorTests precedent): assertions
/// compare hex strings of public wire data and SHA-256 fingerprints of key
/// material — raw secrets never enter an assertion message, so even a
/// failing run cannot print key material.
/// </para>
/// </remarks>
public sealed class SignalCoreTests
{
    private sealed record KatChainDerivation(
        [property: JsonPropertyName("chain_key")] string ChainKey,
        [property: JsonPropertyName("message_key")] string MessageKey,
        [property: JsonPropertyName("next_chain_key")] string NextChainKey);

    private sealed record KatExpansion(
        [property: JsonPropertyName("input")] string Input,
        [property: JsonPropertyName("aes_key")] string AesKey,
        [property: JsonPropertyName("hmac_key")] string HmacKey,
        [property: JsonPropertyName("iv")] string Iv);

    private sealed record KatMessageEncrypt(
        [property: JsonPropertyName("aes_key")] string AesKey,
        [property: JsonPropertyName("hmac_key")] string HmacKey,
        [property: JsonPropertyName("iv")] string Iv,
        [property: JsonPropertyName("plaintext_hex")] string PlaintextHex,
        [property: JsonPropertyName("sender_identity_djb")] string SenderIdentityDjb,
        [property: JsonPropertyName("receiver_identity_djb")] string ReceiverIdentityDjb,
        [property: JsonPropertyName("ratchet_key")] string RatchetKey,
        [property: JsonPropertyName("counter")] uint Counter,
        [property: JsonPropertyName("previous_counter")] uint PreviousCounter,
        [property: JsonPropertyName("wire")] string Wire);

    private sealed record KatPreKeyMessage(
        [property: JsonPropertyName("base_key")] string BaseKey,
        [property: JsonPropertyName("identity_djb")] string IdentityDjb,
        [property: JsonPropertyName("registration_id")] uint RegistrationId,
        [property: JsonPropertyName("signed_pre_key_id")] uint SignedPreKeyId,
        [property: JsonPropertyName("pre_key_id")] uint PreKeyId,
        [property: JsonPropertyName("wire")] string Wire);

    private sealed record KatSignature(
        [property: JsonPropertyName("signing_private")] string SigningPrivate,
        [property: JsonPropertyName("message_hex")] string MessageHex,
        [property: JsonPropertyName("signing_public_djb")] string SigningPublicDjb,
        [property: JsonPropertyName("signature")] string Signature);

    private sealed record KatProvenance(
        [property: JsonPropertyName("upstream")] string Upstream,
        [property: JsonPropertyName("whatsmeow_bytes")] bool WhatsmeowBytes,
        [property: JsonPropertyName("doc")] string Doc);

    private sealed record KatFile(
        [property: JsonPropertyName("provenance")] KatProvenance Provenance,
        [property: JsonPropertyName("kdf_ck")] KatChainDerivation KdfCk,
        [property: JsonPropertyName("message_key_expansion")] KatExpansion Expansion,
        [property: JsonPropertyName("message_encrypt")] KatMessageEncrypt MessageEncrypt,
        [property: JsonPropertyName("prekey_message")] KatPreKeyMessage PreKeyMessage,
        [property: JsonPropertyName("sender_key_signature")] KatSignature Signature);

    private static KatFile LoadVectors()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "signal-kats.json");
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<KatFile>(json)
            ?? throw new InvalidOperationException("Signal KAT fixture failed to deserialize.");
    }

    private static string Fingerprint(byte[] keyMaterial) =>
        Convert.ToHexString(SHA256.HashData(keyMaterial));

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    [Fact]
    public void kat_fixture_carries_provenance_and_all_layout_vectors()
    {
        KatFile vectors = LoadVectors();

        vectors.Provenance.Upstream.Should().Contain("Baileys");
        vectors.Provenance.WhatsmeowBytes.Should().BeFalse("fixtures must never embed whatsmeow bytes");
        vectors.Provenance.Doc.Should().Contain("no whatsmeow");

        Convert.FromHexString(vectors.KdfCk.ChainKey).Should().HaveCount(32);
        Convert.FromHexString(vectors.KdfCk.MessageKey).Should().HaveCount(32);
        Convert.FromHexString(vectors.Expansion.Input).Should().HaveCount(32);
        Convert.FromHexString(vectors.MessageEncrypt.PlaintextHex).Should().NotBeEmpty();
        Convert.FromHexString(vectors.PreKeyMessage.BaseKey).Should().HaveCount(33);
        vectors.PreKeyMessage.BaseKey.Should().StartWith("05");
        Convert.FromHexString(vectors.PreKeyMessage.IdentityDjb).Should().HaveCount(33);
        Convert.FromHexString(vectors.Signature.SigningPublicDjb).Should().HaveCount(33);
        Convert.FromHexString(vectors.Signature.Signature).Should().HaveCount(64);

        // The pinned wire form opens with the 0x33 version byte (Baileys libsignal.ts: low nibble 3).
        byte[] wire = Convert.FromHexString(vectors.MessageEncrypt.Wire);
        wire[0].Should().Be(0x33);
        byte[] preKeyWire = Convert.FromHexString(vectors.PreKeyMessage.Wire);
        preKeyWire[0].Should().Be(0x33);
    }

    [Fact]
    public void kat_chain_derivation_matches_kdf_ck_vector()
    {
        KatFile vectors = LoadVectors();

        (byte[] messageKey, byte[] nextChain) = DoubleRatchet.KdfCk(Convert.FromHexString(vectors.KdfCk.ChainKey));

        Hex(messageKey).Should().Be(vectors.KdfCk.MessageKey);
        Hex(nextChain).Should().Be(vectors.KdfCk.NextChainKey);
    }

    [Fact]
    public void kat_message_key_expansion_matches_80_byte_vector()
    {
        KatFile vectors = LoadVectors();

        (byte[] aes, byte[] hmac, byte[] iv) = SignalCipher.ExpandMessageKey(Convert.FromHexString(vectors.Expansion.Input));

        Hex(aes).Should().Be(vectors.Expansion.AesKey);
        Hex(hmac).Should().Be(vectors.Expansion.HmacKey);
        Hex(iv).Should().Be(vectors.Expansion.Iv);
        (aes.Length + hmac.Length + iv.Length).Should().Be(80);
    }

    [Fact]
    public void kat_message_encrypt_layout_matches_pinned_wire_bytes()
    {
        KatFile vectors = LoadVectors();
        KatMessageEncrypt vector = vectors.MessageEncrypt;

        byte[] wire = SignalCipher.EncryptWhisperMessage(
            Convert.FromHexString(vector.AesKey),
            Convert.FromHexString(vector.HmacKey),
            Convert.FromHexString(vector.Iv),
            Convert.FromHexString(vector.PlaintextHex),
            Convert.FromHexString(vector.SenderIdentityDjb),
            Convert.FromHexString(vector.ReceiverIdentityDjb),
            Convert.FromHexString(vector.RatchetKey),
            vector.Counter,
            vector.PreviousCounter);

        Hex(wire).Should().Be(vector.Wire);

        // Structural pins beyond the golden bytes: version byte, block-aligned
        // 160-byte ciphertext body, 8-byte appended MAC.
        wire[0].Should().Be(0x33);
        SignalMessage? header = SignalCipher.TryParseWhisperBody(wire.AsSpan(1, wire.Length - 1 - 8).ToArray());
        header.Should().NotBeNull();
        header!.Counter.Should().Be(vector.Counter);
        header.PreviousCounter.Should().Be(vector.PreviousCounter);
        Hex(header.RatchetKey.ToByteArray()).Should().Be(vector.RatchetKey.ToLowerInvariant());
        header.Ciphertext.Length.Should().Be(160);
    }

    [Fact]
    public void kat_message_decrypt_roundtrip_recovers_fixture_plaintext()
    {
        KatFile vectors = LoadVectors();
        KatMessageEncrypt vector = vectors.MessageEncrypt;

        SignalDecryptOutcome outcome = SignalCipher.DecryptWhisperMessage(
            Convert.FromHexString(vector.Wire),
            Convert.FromHexString(vector.AesKey),
            Convert.FromHexString(vector.HmacKey),
            Convert.FromHexString(vector.Iv),
            Convert.FromHexString(vector.SenderIdentityDjb),
            Convert.FromHexString(vector.ReceiverIdentityDjb));

        var decrypted = outcome.Should().BeOfType<SignalDecryptOutcome.Decrypted>().Subject;
        Encoding.UTF8.GetString(decrypted.Plaintext).Should().Be("Hello, Signal!");
        decrypted.Counter.Should().Be(vector.Counter);
        decrypted.PreviousCounter.Should().Be(vector.PreviousCounter);
    }

    [Fact]
    public void kat_prekey_message_vector_frames_the_inner_wire()
    {
        KatFile vectors = LoadVectors();
        KatPreKeyMessage vector = vectors.PreKeyMessage;

        byte[] innerWire = Convert.FromHexString(vectors.MessageEncrypt.Wire);
        byte[] wire = SignalCipher.EncryptPreKeyMessage(
            innerWire,
            Convert.FromHexString(vector.BaseKey),
            Convert.FromHexString(vector.IdentityDjb),
            vector.RegistrationId,
            vector.SignedPreKeyId,
            vector.PreKeyId);

        Hex(wire).Should().Be(vector.Wire);

        PreKeyEnvelopeOutcome parsed = SignalCipher.DecryptPreKeyEnvelope(Convert.FromHexString(vector.Wire));
        var content = parsed.Should().BeOfType<PreKeyEnvelopeOutcome.Parsed>().Subject.Content;
        Hex(content.BaseKey).Should().Be(vector.BaseKey.ToLowerInvariant());
        Hex(content.IdentityKeyDjb).Should().Be(vector.IdentityDjb.ToLowerInvariant());
        content.RegistrationId.Should().Be(vector.RegistrationId);
        content.SignedPreKeyId.Should().Be(vector.SignedPreKeyId);
        content.PreKeyId.Should().Be(vector.PreKeyId);
        Hex(content.InnerMessage).Should().Be(vectors.MessageEncrypt.Wire.ToLowerInvariant());
    }

    [Fact]
    public void kat_sender_key_signature_verifies_and_fails_on_a_flipped_byte()
    {
        KatFile vectors = LoadVectors();
        KatSignature vector = vectors.Signature;

        // Deterministic Ed25519: re-signing the fixture message reproduces the pinned signature.
        byte[] reproduced = X3dh.SignWithIdentity(
            Convert.FromHexString(vector.SigningPrivate),
            Convert.FromHexString(vector.MessageHex));
        Hex(reproduced).Should().Be(vector.Signature.ToLowerInvariant());

        // The pinned public matches the private (fingerprint compare — never raw key bytes in asserts).
        byte[] derivedDjb = X3dh.DeriveEd25519PublicDjb(Convert.FromHexString(vector.SigningPrivate));
        Fingerprint(derivedDjb).Should().Be(Fingerprint(Convert.FromHexString(vector.SigningPublicDjb)));

        // NSec verification passes on the pinned values, fails on one flipped signature byte.
        PublicKey verifyKey = PublicKey.Import(
            SignatureAlgorithm.Ed25519,
            SignalCipher.FromDjbIdentity(Convert.FromHexString(vector.SigningPublicDjb)),
            KeyBlobFormat.RawPublicKey);
        SignatureAlgorithm.Ed25519.Verify(
            verifyKey,
            Convert.FromHexString(vector.MessageHex),
            Convert.FromHexString(vector.Signature)).Should().BeTrue();

        byte[] tampered = Convert.FromHexString(vector.Signature);
        tampered[tampered.Length - 1] ^= 0xFF;
        SignatureAlgorithm.Ed25519.Verify(
            verifyKey,
            Convert.FromHexString(vector.MessageHex),
            tampered).Should().BeFalse();
    }

    [Fact]
    public void kat_empty_and_missized_inputs_fail_closed()
    {
        KatFile vectors = LoadVectors();
        KatMessageEncrypt vector = vectors.MessageEncrypt;
        byte[] aes = Convert.FromHexString(vector.AesKey);
        byte[] hmac = Convert.FromHexString(vector.HmacKey);
        byte[] iv = Convert.FromHexString(vector.Iv);
        byte[] sender = Convert.FromHexString(vector.SenderIdentityDjb);
        byte[] receiver = Convert.FromHexString(vector.ReceiverIdentityDjb);
        byte[] ratchet = Convert.FromHexString(vector.RatchetKey);

        Action emptyPlaintext = () => SignalCipher.EncryptWhisperMessage(aes, hmac, iv, [], sender, receiver, ratchet, 0, 0);
        emptyPlaintext.Should().Throw<SdkFormatException>();

        Action shortKey = () => SignalCipher.ExpandMessageKey(new byte[16]);
        shortKey.Should().Throw<SdkFormatException>();

        Action badDjb = () => SignalCipher.ToDjbIdentity(new byte[33]);
        badDjb.Should().Throw<SdkFormatException>("the DJB type prefix is validated");

        SignalDecryptOutcome truncated = SignalCipher.DecryptWhisperMessage(
            [0x33, 0x00], aes, hmac, iv, sender, receiver);
        truncated.Should().BeOfType<SignalDecryptOutcome.Failed>()
            .Which.Reason.Should().Be(SignalDecryptFailure.Truncated);

        PreKeyEnvelopeOutcome bundleMissingFields = SignalCipher.DecryptPreKeyEnvelope(
            Convert.FromHexString(vectors.PreKeyMessage.Wire).AsSpan(0, 4).ToArray());
        bundleMissingFields.Should().BeOfType<PreKeyEnvelopeOutcome.Failed>();
    }

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

    private static PeerBundle BundleOf(SyntheticPeer peer, bool withOneTime) => new(
        peer.XIdentityDjb,
        peer.SignedPreKeyPublic,
        peer.SignedPreKeySignature,
        peer.SignedPreKeyId,
        withOneTime ? peer.OneTimePublic : null,
        withOneTime ? peer.OneTimePreKeyId : null,
        peer.RegistrationId);

    [Fact]
    public void x3dh_alice_and_bob_converge_with_and_without_a_one_time_key()
    {
        foreach (bool withOneTime in new[] { true, false })
        {
            SyntheticPeer alice = CreateSyntheticPeer(1, 101, 1001);
            SyntheticPeer bob = CreateSyntheticPeer(22, 1101, 1234);
            PeerBundle bundle = BundleOf(bob, withOneTime);

            (byte[] ephPriv, _) = X3dh.GenerateX25519KeyPair();
            AliceSession aliceSession = X3dh.InitializeAlice(alice.XIdentityPrivate, ephPriv, bundle, bob.EdPublicDjb);
            BobSession bobSession = X3dh.InitializeBob(
                bob.XIdentityPrivate, bob.SignedPreKeyPrivate,
                withOneTime ? bob.OneTimePrivate : null, withOneTime,
                alice.XIdentityDjb, aliceSession.BasePublic);

            Fingerprint(aliceSession.RootKey).Should().Be(Fingerprint(bobSession.RootKey));
        }
    }

    [Fact]
    public void x3dh_rejects_a_bundle_with_an_invalid_signed_prekey_signature()
    {
        SyntheticPeer alice = CreateSyntheticPeer(1, 101, 1001);
        SyntheticPeer bob = CreateSyntheticPeer(22, 1101, 1234);
        PeerBundle bundle = BundleOf(bob, true);

        byte[] badSig = (byte[])bundle.SignedPreKeySignature.Clone();
        badSig[0] ^= 0xFF;
        PeerBundle tampered = bundle with { SignedPreKeySignature = badSig };

        (byte[] ephPriv, _) = X3dh.GenerateX25519KeyPair();
        Action init = () => X3dh.InitializeAlice(alice.XIdentityPrivate, ephPriv, tampered, bob.EdPublicDjb);
        init.Should().Throw<SdkFormatException>("a forged signed prekey must fail closed, never a partial session");
    }

    [Fact]
    public void ratchet_roundtrip_full_x3dh_to_prekey_wire_to_decrypt()
    {
        SyntheticPeer aliceKeys = CreateSyntheticPeer(1, 101, 1001);
        SyntheticPeer bobKeys = CreateSyntheticPeer(22, 1101, 1234);
        PeerBundle bundle = BundleOf(bobKeys, true);

        (byte[] ephPriv, _) = X3dh.GenerateX25519KeyPair();
        AliceSession aliceSession = X3dh.InitializeAlice(aliceKeys.XIdentityPrivate, ephPriv, bundle, bobKeys.EdPublicDjb);
        (byte[] sendPriv, byte[] sendPub) = X3dh.GenerateX25519KeyPair();
        BobSession bobSession = X3dh.InitializeBob(
            bobKeys.XIdentityPrivate, bobKeys.SignedPreKeyPrivate, bobKeys.OneTimePrivate, true,
            aliceKeys.XIdentityDjb, aliceSession.BasePublic);

        var alice = DoubleRatchet.InitializeAlice(aliceSession, sendPriv, sendPub, bundle, aliceKeys.XIdentityDjb, bobKeys.XIdentityDjb, 1001);
        var bob = DoubleRatchet.InitializeBob(
            bobSession, bobKeys.SignedPreKeyPrivate, bobKeys.XIdentityDjb, aliceKeys.XIdentityDjb, aliceSession.BasePublic, 1234, 1001);

        byte[] plaintext = Encoding.UTF8.GetBytes("Wave-0 spike: the whole path in one proof.");
        SignalEncryptOutcome first = alice.Encrypt(plaintext, CancellationToken.None);
        byte[] preKeyWire = first.Should().BeOfType<SignalEncryptOutcome.PreKeyMessageSent>().Subject.WireBytes;

        // The prekey wire carries the bundle coordinates it was built from.
        var envelope = SignalCipher.DecryptPreKeyEnvelope(preKeyWire)
            .Should().BeOfType<PreKeyEnvelopeOutcome.Parsed>().Subject.Content;
        envelope.SignedPreKeyId.Should().Be(22);
        envelope.PreKeyId.Should().Be(1101);
        envelope.RegistrationId.Should().Be(1001, "the first prekey message carries Alice's registration id");
        Hex(envelope.BaseKey).Should().Be(Hex(aliceSession.BasePublic).ToLowerInvariant());

        SignalDecryptOutcome outcome = bob.Decrypt(preKeyWire, isPreKey: true, CancellationToken.None);
        var decrypted = outcome.Should().BeOfType<SignalDecryptOutcome.Decrypted>().Subject;
        decrypted.Plaintext.Should().Equal(plaintext, "the peer side decrypts byte-exact");

        // The reply exercises the DH-ratchet step: Bob's fresh ratchet key
        // drives Alice onto a new receiving chain.
        SignalEncryptOutcome reply = bob.Encrypt(Encoding.UTF8.GetBytes("ack from Bob"), CancellationToken.None);
        byte[] replyWire = reply.Should().BeOfType<SignalEncryptOutcome.MessageSent>().Subject.WireBytes;
        SignalDecryptOutcome replyOutcome = alice.Decrypt(replyWire, isPreKey: false, CancellationToken.None);
        replyOutcome.Should().BeOfType<SignalDecryptOutcome.Decrypted>()
            .Which.Plaintext.Should().Equal(Encoding.UTF8.GetBytes("ack from Bob"));

        // And Alice speaks again on the post-ratchet chains.
        SignalEncryptOutcome second = alice.Encrypt(Encoding.UTF8.GetBytes("second window"), CancellationToken.None);
        byte[] secondWire = second.Should().BeOfType<SignalEncryptOutcome.MessageSent>().Subject.WireBytes;
        bob.Decrypt(secondWire, isPreKey: false, CancellationToken.None)
            .Should().BeOfType<SignalDecryptOutcome.Decrypted>()
            .Which.Plaintext.Should().Equal(Encoding.UTF8.GetBytes("second window"));
    }

    [Fact]
    public void ratchet_roundtrip_replay_is_a_typed_failure_never_a_second_session()
    {
        SyntheticPeer aliceKeys = CreateSyntheticPeer(1, 101, 1001);
        SyntheticPeer bobKeys = CreateSyntheticPeer(22, 1101, 1234);
        PeerBundle bundle = BundleOf(bobKeys, true);

        (byte[] ephPriv, _) = X3dh.GenerateX25519KeyPair();
        AliceSession aliceSession = X3dh.InitializeAlice(aliceKeys.XIdentityPrivate, ephPriv, bundle, bobKeys.EdPublicDjb);
        (byte[] sendPriv, byte[] sendPub) = X3dh.GenerateX25519KeyPair();
        BobSession bobSession = X3dh.InitializeBob(
            bobKeys.XIdentityPrivate, bobKeys.SignedPreKeyPrivate, bobKeys.OneTimePrivate, true,
            aliceKeys.XIdentityDjb, aliceSession.BasePublic);

        var alice = DoubleRatchet.InitializeAlice(aliceSession, sendPriv, sendPub, bundle, aliceKeys.XIdentityDjb, bobKeys.XIdentityDjb, 1001);
        var bob = DoubleRatchet.InitializeBob(
            bobSession, bobKeys.SignedPreKeyPrivate, bobKeys.XIdentityDjb, aliceKeys.XIdentityDjb, aliceSession.BasePublic, 1234, 1001);

        byte[] wire = alice.Encrypt(Encoding.UTF8.GetBytes("once only"), CancellationToken.None)
            .Should().BeOfType<SignalEncryptOutcome.PreKeyMessageSent>().Subject.WireBytes;

        bob.Decrypt(wire, isPreKey: true, CancellationToken.None)
            .Should().BeOfType<SignalDecryptOutcome.Decrypted>();

        // Replaying the same PreKeySignalMessage never creates a second
        // session: the established base key routes the inner message to the
        // regular path, where its consumed counter reports a typed failure.
        bob.Decrypt(wire, isPreKey: true, CancellationToken.None)
            .Should().BeOfType<SignalDecryptOutcome.Failed>()
            .Which.Reason.Should().Be(SignalDecryptFailure.OldCounter);
    }

    [Fact]
    public void tampered_mac_and_version_fail_as_typed_outcomes()
    {
        SyntheticPeer aliceKeys = CreateSyntheticPeer(1, 101, 1001);
        SyntheticPeer bobKeys = CreateSyntheticPeer(22, 1101, 1234);
        PeerBundle bundle = BundleOf(bobKeys, false);

        (byte[] ephPriv, _) = X3dh.GenerateX25519KeyPair();
        AliceSession aliceSession = X3dh.InitializeAlice(aliceKeys.XIdentityPrivate, ephPriv, bundle, bobKeys.EdPublicDjb);
        (byte[] sendPriv, byte[] sendPub) = X3dh.GenerateX25519KeyPair();
        BobSession bobSession = X3dh.InitializeBob(
            bobKeys.XIdentityPrivate, bobKeys.SignedPreKeyPrivate, null, false,
            aliceKeys.XIdentityDjb, aliceSession.BasePublic);

        var alice = DoubleRatchet.InitializeAlice(aliceSession, sendPriv, sendPub, bundle, aliceKeys.XIdentityDjb, bobKeys.XIdentityDjb, 1001);

        // Alice's first message is prekey-form; send a second (regular-form) message for wire tampering.
        alice.Encrypt(Encoding.UTF8.GetBytes("first"), CancellationToken.None);
        byte[] wire = alice.Encrypt(Encoding.UTF8.GetBytes("second"), CancellationToken.None)
            .Should().BeOfType<SignalEncryptOutcome.MessageSent>().Subject.WireBytes;

        var bob = DoubleRatchet.InitializeBob(
            bobSession, bobKeys.SignedPreKeyPrivate, bobKeys.XIdentityDjb, aliceKeys.XIdentityDjb, aliceSession.BasePublic, 1234, 1001);

        byte[] badMac = (byte[])wire.Clone();
        badMac[badMac.Length - 1] ^= 0xFF;
        bob.Decrypt(badMac, isPreKey: false, CancellationToken.None)
            .Should().BeOfType<SignalDecryptOutcome.Failed>()
            .Which.Reason.Should().Be(SignalDecryptFailure.BadMac);

        byte[] badVersion = (byte[])wire.Clone();
        badVersion[0] = 0x32;
        bob.Decrypt(badVersion, isPreKey: false, CancellationToken.None)
            .Should().BeOfType<SignalDecryptOutcome.Failed>()
            .Which.Reason.Should().Be(SignalDecryptFailure.BadVersion);

        // The untampered message still decrypts afterwards: tampering never corrupts the session.
        bob.Decrypt(wire, isPreKey: false, CancellationToken.None)
            .Should().BeOfType<SignalDecryptOutcome.Decrypted>()
            .Which.Plaintext.Should().Equal(Encoding.UTF8.GetBytes("second"));
    }

    [Fact]
    public void tampered_session_inputs_never_create_partial_state()
    {
        SyntheticPeer aliceKeys = CreateSyntheticPeer(1, 101, 1001);

        // Wrong-length keys fail closed at every entry point (never defaults, never partial sessions).
        Action shortIdentity = () => X3dh.DeriveX25519Public(new byte[16]);
        shortIdentity.Should().Throw<SdkFormatException>();

        Action shortAgree = () => DoubleRatchet.KdfCk(new byte[16]);
        shortAgree.Should().Throw<SdkFormatException>("KDF inputs are length-checked");

        // A cancelled decrypt never advances state: the first delivery still decrypts afterwards.
        SyntheticPeer bobKeys = CreateSyntheticPeer(22, 1101, 1234);
        PeerBundle bundle = BundleOf(bobKeys, false);
        (byte[] ephPriv, _) = X3dh.GenerateX25519KeyPair();
        AliceSession aliceSession = X3dh.InitializeAlice(aliceKeys.XIdentityPrivate, ephPriv, bundle, bobKeys.EdPublicDjb);
        (byte[] sendPriv, byte[] sendPub) = X3dh.GenerateX25519KeyPair();
        BobSession bobSession = X3dh.InitializeBob(
            bobKeys.XIdentityPrivate, bobKeys.SignedPreKeyPrivate, null, false,
            aliceKeys.XIdentityDjb, aliceSession.BasePublic);
        var alice = DoubleRatchet.InitializeAlice(aliceSession, sendPriv, sendPub, bundle, aliceKeys.XIdentityDjb, bobKeys.XIdentityDjb, 1001);
        var bob = DoubleRatchet.InitializeBob(
            bobSession, bobKeys.SignedPreKeyPrivate, bobKeys.XIdentityDjb, aliceKeys.XIdentityDjb, aliceSession.BasePublic, 1234, 1001);

        byte[] wire = alice.Encrypt(Encoding.UTF8.GetBytes("hello"), CancellationToken.None)
            .Should().BeOfType<SignalEncryptOutcome.PreKeyMessageSent>().Subject.WireBytes;

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Action cancelled = () => bob.Decrypt(wire, isPreKey: true, cts.Token);
        cancelled.Should().Throw<OperationCanceledException>();
        bob.ReceivedAny.Should().BeFalse("a cancelled decrypt never advances state");

        bob.Decrypt(wire, isPreKey: true, CancellationToken.None)
            .Should().BeOfType<SignalDecryptOutcome.Decrypted>();
    }
}
