using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Iwasug.WhatsApp.Sdk.Errors;

namespace Iwasug.WhatsApp.Sdk.Signal;

/// <summary>
/// Typed decrypt outcome for the Signal wire layer (04-01): decryption
/// failures are VALUES returned to the caller (the retry path), never
/// exceptions thrown through the stanza pump (04-RESEARCH Security Domain).
/// </summary>
internal abstract record SignalDecryptOutcome
{
    /// <summary>Authentication and padding both verified; the plaintext is fresh output owned by the caller.</summary>
    internal sealed record Decrypted(byte[] Plaintext, byte[] RatchetKey, uint Counter, uint PreviousCounter) : SignalDecryptOutcome;

    /// <summary>The wire bytes failed validation; the reason names the rule only, never key or plaintext bytes.</summary>
    internal sealed record Failed(SignalDecryptFailure Reason) : SignalDecryptOutcome;
}

/// <summary>Rule-only decrypt failure reasons (T-4-01: no key material in messages).</summary>
internal enum SignalDecryptFailure
{
    /// <summary>The version byte's low nibble is not the pinned Signal version.</summary>
    BadVersion,

    /// <summary>The wire bytes are shorter than version + minimal body + MAC.</summary>
    Truncated,

    /// <summary>The appended MAC does not verify (constant-time compare).</summary>
    BadMac,

    /// <summary>AES-CBC output fails padding validation.</summary>
    BadPadding,

    /// <summary>The protobuf body does not parse or violates length rules.</summary>
    MalformedMessage,

    /// <summary>The counter was already consumed (replay of a delivered message).</summary>
    OldCounter,

    /// <summary>Staging the skipped keys for the gap would exceed the per-chain bound.</summary>
    SkippedKeyLimit,

    /// <summary>The prekey message replays an already-established base key.</summary>
    DuplicatePreKey,

    /// <summary>A prekey message arrived on a session that cannot absorb it.</summary>
    UnexpectedPreKey,
}

/// <summary>
/// Parsed prekey wire content (the <c>message</c> field is the COMPLETE inner
/// whisper wire — version || proto || MAC — still encrypted under the first
/// chain key; see <see cref="SignalCipher.EncryptPreKeyMessage"/>).
/// </summary>
internal sealed record PreKeyWireContent(
    uint? PreKeyId,
    byte[] BaseKey,
    byte[] IdentityKeyDjb,
    byte[] InnerMessage,
    uint RegistrationId,
    uint SignedPreKeyId);

/// <summary>Typed parse outcome for one prekey envelope (untrusted peer input).</summary>
internal abstract record PreKeyEnvelopeOutcome
{
    /// <summary>The envelope parsed and every length rule holds.</summary>
    internal sealed record Parsed(PreKeyWireContent Content) : PreKeyEnvelopeOutcome;

    /// <summary>The envelope failed validation; the reason names the rule only.</summary>
    internal sealed record Failed(SignalDecryptFailure Reason) : PreKeyEnvelopeOutcome;
}

/// <summary>
/// WhatsApp Signal message cipher (04-01, internal): the 80-byte message-key
/// expansion plus AES-256-CBC with the pinned wire layout — version byte,
/// serialized <c>SignalMessage</c>, appended truncated HMAC — proven against
/// <c>tests/.../TestData/signal-kats.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Byte layout (pinned by the Wave-0 spike fixtures, which win over prose):
/// the wire form is <c>version(0x33) || serialized-SignalMessage ||
/// MAC[..8]</c>, where the MAC is HMAC-SHA256 over
/// <c>version || body || sender-DJB-identity || receiver-DJB-identity</c>
/// truncated to 8 bytes. The version byte is <c>0x33</c> (libsignal
/// <c>(3 &lt;&lt; 4) | 3</c>), confirmed read-only in Baileys
/// <c>src/Signal/libsignal.ts</c> at commit 0af2386
/// (<c>(version &amp; 0xf) !== 3</c> rejects; <c>sigType === 3</c> marks
/// prekey messages). Identity keys on the wire use the 33-byte DJB form
/// (<c>0x05 || 32-byte key</c>, Baileys <c>generateSignalPubKey</c> in
/// <c>src/Utils/crypto.ts</c> at the same commit — the 33-byte length is
/// asserted live in <c>libsignal.ts</c>'s pkmsg identity extraction).
/// Plaintext is PKCS7-style padded to a multiple of 160 bytes before
/// AES-256-CBC (fixture-pinned; the 04-05 live-device UAT is the interop
/// backstop for the block size).
/// </para>
/// <para>
/// Discipline (Shared Patterns 4): BCL primitives only
/// (<see cref="Aes"/>, <see cref="HMACSHA256"/>, <see cref="HKDF"/>) —
/// never hand-rolled curve math or MAC logic; every temporary key buffer is
/// zeroized; length-check throws are short and rule-only (never name key
/// bytes); MAC comparison is constant-time. No logging on this path
/// (T-4-01 secret hygiene).
/// </para>
/// </remarks>
internal static class SignalCipher
{
    /// <summary>Pinned Signal message version (low nibble of the version byte).</summary>
    internal const int WireVersion = 3;

    /// <summary>The version byte prefixing every wire message: (3 &lt;&lt; 4) | 3.</summary>
    internal const byte VersionByte = 0x33;

    /// <summary>Truncated HMAC length appended after the protobuf body.</summary>
    internal const int MacLength = 8;

    /// <summary>DJB identity-encoding type byte (Baileys KEY_BUNDLE_TYPE).</summary>
    internal const byte DjbTypeByte = 0x05;

    /// <summary>Raw X25519/Ed25519 public key length.</summary>
    internal const int RawKeyLength = 32;

    /// <summary>DJB-encoded identity length (type byte + raw key).</summary>
    internal const int DjbIdentityLength = 33;

    /// <summary>Ed25519 signature length.</summary>
    internal const int SignatureLength = 64;

    /// <summary>AES-256 key length (first third of the expanded message key).</summary>
    internal const int AesKeyLength = 32;

    /// <summary>HMAC-SHA256 key length (second third of the expanded message key).</summary>
    internal const int HmacKeyLength = 32;

    /// <summary>AES-CBC IV length (final sixth of the expanded message key).</summary>
    internal const int IvLength = 16;

    /// <summary>Expanded message-key length: 32 AES + 32 HMAC + 16 IV (WhatsApp Security Whitepaper).</summary>
    internal const int MessageKeyMaterialLength = 80;

    /// <summary>Chain-derived message-key length feeding the expansion.</summary>
    internal const int ChainMessageKeyLength = 32;

    /// <summary>Plaintext padding block size (fixture-pinned; see class remarks).</summary>
    internal const int PaddingBlockSize = 160;

    /// <summary>Fail-closed cap on accepted wire input (garbage larger than this is truncated input, not a message).</summary>
    internal const int MaxWireBytes = 64 * 1024;

    /// <summary>HKDF info label for the 80-byte message-key expansion.</summary>
    internal const string MessageKeyInfoLabel = "WhisperMessageKeys";

    /// <summary>
    /// Expands one 32-byte chain message key into the 80-byte WhatsApp
    /// message key (AES key ‖ HMAC key ‖ IV) via HKDF-SHA256.
    /// </summary>
    /// <param name="chainMessageKey">The 32-byte chain output. Never logged.</param>
    /// <returns>Freshly allocated (aesKey, hmacKey, iv) copies owned by the caller.</returns>
    /// <exception cref="SdkFormatException">The input is not exactly 32 bytes.</exception>
    internal static (byte[] AesKey, byte[] HmacKey, byte[] Iv) ExpandMessageKey(ReadOnlySpan<byte> chainMessageKey)
    {
        if (chainMessageKey.Length != ChainMessageKeyLength)
        {
            throw new SdkFormatException("Signal message-key expansion requires a 32-byte chain key.");
        }

        byte[] okm = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            chainMessageKey.ToArray(),
            MessageKeyMaterialLength,
            new byte[ChainMessageKeyLength],
            Encoding.ASCII.GetBytes(MessageKeyInfoLabel));
        try
        {
            byte[] aes = new byte[AesKeyLength];
            byte[] hmac = new byte[HmacKeyLength];
            byte[] iv = new byte[IvLength];
            okm.AsSpan(0, AesKeyLength).CopyTo(aes);
            okm.AsSpan(AesKeyLength, HmacKeyLength).CopyTo(hmac);
            okm.AsSpan(AesKeyLength + HmacKeyLength, IvLength).CopyTo(iv);
            return (aes, hmac, iv);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(okm);
        }
    }

    /// <summary>
    /// Encodes a raw 32-byte public key into the 33-byte DJB wire form
    /// (type byte <c>0x05</c> prefix — Baileys <c>generateSignalPubKey</c>).
    /// </summary>
    /// <exception cref="SdkFormatException">The input is not exactly 32 bytes.</exception>
    internal static byte[] ToDjbIdentity(ReadOnlySpan<byte> rawPublicKey)
    {
        if (rawPublicKey.Length != RawKeyLength)
        {
            throw new SdkFormatException("Signal identity keys must be 32 bytes before DJB encoding.");
        }

        byte[] encoded = new byte[DjbIdentityLength];
        encoded[0] = DjbTypeByte;
        rawPublicKey.CopyTo(encoded.AsSpan(1));
        return encoded;
    }

    /// <summary>
    /// Decodes the 33-byte DJB wire form back to the raw 32-byte key,
    /// validating the type prefix.
    /// </summary>
    /// <exception cref="SdkFormatException">The input is not a 33-byte DJB identity.</exception>
    internal static byte[] FromDjbIdentity(ReadOnlySpan<byte> djbIdentity)
    {
        if (djbIdentity.Length != DjbIdentityLength || djbIdentity[0] != DjbTypeByte)
        {
            throw new SdkFormatException("Signal identity keys must be 33-byte DJB-encoded values.");
        }

        return djbIdentity.Slice(1).ToArray();
    }

    /// <summary>
    /// Encrypts one Double Ratchet message: pads, AES-256-CBC encrypts, wraps
    /// in <c>SignalMessage</c>, and frames the pinned wire form.
    /// </summary>
    /// <param name="aesKey">32-byte AES key. Never logged.</param>
    /// <param name="hmacKey">32-byte MAC key. Never logged.</param>
    /// <param name="iv">16-byte IV. Never logged.</param>
    /// <param name="plaintext">Message bytes; must not be empty (fail-closed).</param>
    /// <param name="senderIdentityDjb">Sender identity in 33-byte DJB form.</param>
    /// <param name="receiverIdentityDjb">Receiver identity in 33-byte DJB form.</param>
    /// <param name="ratchetPublicKey">Current sending ratchet public key (32 bytes).</param>
    /// <param name="counter">Sending chain index for this message.</param>
    /// <param name="previousCounter">Length of the previous sending chain.</param>
    /// <returns>The complete whisper wire bytes (version || proto || MAC).</returns>
    /// <exception cref="SdkFormatException">Any length rule is violated, or the plaintext is empty.</exception>
    internal static byte[] EncryptWhisperMessage(
        ReadOnlySpan<byte> aesKey,
        ReadOnlySpan<byte> hmacKey,
        ReadOnlySpan<byte> iv,
        byte[] plaintext,
        ReadOnlySpan<byte> senderIdentityDjb,
        ReadOnlySpan<byte> receiverIdentityDjb,
        ReadOnlySpan<byte> ratchetPublicKey,
        uint counter,
        uint previousCounter)
    {
        ThrowUnlessKey(aesKey, AesKeyLength);
        ThrowUnlessKey(hmacKey, HmacKeyLength);
        ThrowUnlessKey(iv, IvLength);
        ThrowUnlessDjb(senderIdentityDjb);
        ThrowUnlessDjb(receiverIdentityDjb);
        if (ratchetPublicKey.Length != RawKeyLength)
        {
            throw new SdkFormatException("Signal ratchet keys must be 32 bytes.");
        }

        if (plaintext is null || plaintext.Length == 0)
        {
            throw new SdkFormatException("Signal plaintext must not be empty.");
        }

        byte[] padded = Pad(plaintext);
        byte[] ciphertext;
        try
        {
            ciphertext = AesCbcEncrypt(aesKey, iv, padded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(padded);
        }

        var message = new SignalMessage
        {
            RatchetKey = ByteString.CopyFrom(ratchetPublicKey.ToArray()),
            Counter = counter,
            PreviousCounter = previousCounter,
            Ciphertext = ByteString.CopyFrom(ciphertext),
        };
        byte[] body = message.ToByteArray();
        return FrameWhisperWire(body, hmacKey, senderIdentityDjb, receiverIdentityDjb);
    }

    /// <summary>
    /// Decrypts one Double Ratchet wire message: version, length, MAC, proto,
    /// AES-CBC, and padding are each validated fail-closed as a typed outcome.
    /// </summary>
    /// <param name="wire">Version || proto || MAC bytes (untrusted peer input).</param>
    /// <param name="aesKey">32-byte AES key. Never logged.</param>
    /// <param name="hmacKey">32-byte MAC key. Never logged.</param>
    /// <param name="iv">16-byte IV from the same expanded message key. Never logged.</param>
    /// <param name="senderIdentityDjb">Expected sender identity (33-byte DJB form).</param>
    /// <param name="receiverIdentityDjb">Expected receiver identity (33-byte DJB form).</param>
    internal static SignalDecryptOutcome DecryptWhisperMessage(
        ReadOnlySpan<byte> wire,
        ReadOnlySpan<byte> aesKey,
        ReadOnlySpan<byte> hmacKey,
        ReadOnlySpan<byte> iv,
        ReadOnlySpan<byte> senderIdentityDjb,
        ReadOnlySpan<byte> receiverIdentityDjb)
    {
        SignalDecryptFailure? split = TrySplitEnvelope(wire, out byte version, out byte[]? body, out byte[]? receivedMac);
        if (split.HasValue || body is null || receivedMac is null)
        {
            return new SignalDecryptOutcome.Failed(split ?? SignalDecryptFailure.MalformedMessage);
        }

        SignalMessage? header = TryParseWhisperBody(body);
        if (header is null)
        {
            return new SignalDecryptOutcome.Failed(SignalDecryptFailure.MalformedMessage);
        }

        return DecryptParsedMessage(version, body, receivedMac, header, aesKey, hmacKey, iv, senderIdentityDjb, receiverIdentityDjb);
    }

    /// <summary>
    /// Splits version || proto || MAC without authenticating (header bytes
    /// select the chain key; the MAC is verified after selection).
    /// Returns null on success, or the rule-only failure when the envelope
    /// shape itself is invalid.
    /// </summary>
    internal static SignalDecryptFailure? TrySplitEnvelope(
        ReadOnlySpan<byte> wire, out byte version, out byte[]? body, out byte[]? receivedMac)
    {
        version = 0;
        body = null;
        receivedMac = null;
        if (wire.Length > MaxWireBytes || wire.Length < 1 + MacLength + 1)
        {
            return SignalDecryptFailure.Truncated;
        }

        if ((wire[0] & 0x0F) != WireVersion)
        {
            return SignalDecryptFailure.BadVersion;
        }

        version = wire[0];
        body = wire.Slice(1, wire.Length - 1 - MacLength).ToArray();
        receivedMac = wire.Slice(wire.Length - MacLength, MacLength).ToArray();
        return null;
    }

    /// <summary>
    /// Parses a <c>SignalMessage</c> body and validates its length rules
    /// (32-byte ratchet key, non-empty block-aligned ciphertext); null when
    /// the body is not a well-formed message (never throws).
    /// </summary>
    internal static SignalMessage? TryParseWhisperBody(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        SignalMessage message;
        try
        {
            message = SignalMessage.Parser.ParseFrom(body);
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }

        byte[] ratchetKey = message.RatchetKey.ToByteArray();
        byte[] ciphertext = message.Ciphertext.ToByteArray();
        if (ratchetKey.Length != RawKeyLength || ciphertext.Length == 0 || ciphertext.Length % 16 != 0)
        {
            return null;
        }

        return message;
    }

    /// <summary>
    /// Verifies the MAC over <c>version || body || sender || receiver</c>
    /// (constant-time) and decrypts <paramref name="header"/> (already parsed
    /// from <paramref name="body"/>) under the given expanded message key.
    /// </summary>
    internal static SignalDecryptOutcome DecryptParsedMessage(
        byte version,
        byte[] body,
        byte[] receivedMac,
        SignalMessage header,
        ReadOnlySpan<byte> aesKey,
        ReadOnlySpan<byte> hmacKey,
        ReadOnlySpan<byte> iv,
        ReadOnlySpan<byte> senderIdentityDjb,
        ReadOnlySpan<byte> receiverIdentityDjb)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(receivedMac);
        ArgumentNullException.ThrowIfNull(header);
        if (aesKey.Length != AesKeyLength || hmacKey.Length != HmacKeyLength || iv.Length != IvLength)
        {
            return new SignalDecryptOutcome.Failed(SignalDecryptFailure.MalformedMessage);
        }

        if (senderIdentityDjb.Length != DjbIdentityLength || receiverIdentityDjb.Length != DjbIdentityLength)
        {
            return new SignalDecryptOutcome.Failed(SignalDecryptFailure.MalformedMessage);
        }

        byte[] computedMac = ComputeWhisperMac(hmacKey, version, body, senderIdentityDjb, receiverIdentityDjb);
        try
        {
            if (receivedMac.Length != MacLength
                || !CryptographicOperations.FixedTimeEquals(computedMac, receivedMac))
            {
                return new SignalDecryptOutcome.Failed(SignalDecryptFailure.BadMac);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(computedMac);
        }

        byte[] ciphertext = header.Ciphertext.ToByteArray();
        byte[] padded;
        try
        {
            padded = AesCbcDecrypt(aesKey, iv, ciphertext);
        }
        catch (CryptographicException)
        {
            return new SignalDecryptOutcome.Failed(SignalDecryptFailure.BadPadding);
        }

        try
        {
            if (!TryUnpad(padded, out byte[]? plaintext) || plaintext is null)
            {
                return new SignalDecryptOutcome.Failed(SignalDecryptFailure.BadPadding);
            }

            return new SignalDecryptOutcome.Decrypted(
                plaintext,
                header.RatchetKey.ToByteArray(),
                header.Counter,
                header.PreviousCounter);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(padded);
        }
    }

    /// <summary>
    /// Frames a first-contact prekey message: version byte plus the
    /// serialized <c>PreKeySignalMessage</c> (no outer MAC — the embedded
    /// inner wire carries its own).
    /// </summary>
    /// <param name="innerMessage">
    /// The COMPLETE inner whisper wire (version || proto || MAC) as produced
    /// by <see cref="EncryptWhisperMessage"/> — pinned by the Wave-0 spike
    /// fixtures: the first message stays MAC-authenticated end to end, and
    /// the responder replays it through the regular decrypt path. The 04-05
    /// live-device UAT is the interop backstop for this embedding choice.
    /// </param>
    /// <param name="baseKey">Our ephemeral base public key (32 bytes).</param>
    /// <param name="identityKeyDjb">Our identity key in 33-byte DJB form.</param>
    /// <param name="registrationId">Our 14-bit registration id.</param>
    /// <param name="signedPreKeyId">The peer bundle's signed-prekey id.</param>
    /// <param name="preKeyId">The consumed one-time prekey id, when the bundle carried one.</param>
    /// <exception cref="SdkFormatException">Any length rule is violated.</exception>
    internal static byte[] EncryptPreKeyMessage(
        byte[] innerMessage,
        ReadOnlySpan<byte> baseKey,
        ReadOnlySpan<byte> identityKeyDjb,
        uint registrationId,
        uint signedPreKeyId,
        uint? preKeyId)
    {
        ArgumentNullException.ThrowIfNull(innerMessage);
        if (innerMessage.Length == 0)
        {
            throw new SdkFormatException("Signal prekey messages must carry a non-empty inner message.");
        }

        if (baseKey.Length != RawKeyLength)
        {
            throw new SdkFormatException("Signal base keys must be 32 bytes.");
        }

        ThrowUnlessDjb(identityKeyDjb);

        var preKey = new PreKeySignalMessage
        {
            BaseKey = ByteString.CopyFrom(baseKey.ToArray()),
            IdentityKey = ByteString.CopyFrom(identityKeyDjb.ToArray()),
            Message = ByteString.CopyFrom(innerMessage),
            RegistrationId = registrationId,
            SignedPreKeyId = signedPreKeyId,
        };
        if (preKeyId.HasValue)
        {
            preKey.PreKeyId = preKeyId.Value;
        }

        byte[] body = preKey.ToByteArray();
        byte[] wire = new byte[1 + body.Length];
        wire[0] = VersionByte;
        body.CopyTo(wire.AsSpan(1));
        return wire;
    }

    /// <summary>
    /// Parses one prekey wire message (version + proto), validating every
    /// length rule fail-closed as a typed outcome.
    /// </summary>
    /// <param name="wire">Version || serialized PreKeySignalMessage (untrusted peer input).</param>
    internal static PreKeyEnvelopeOutcome DecryptPreKeyEnvelope(ReadOnlySpan<byte> wire)
    {
        if (wire.Length > MaxWireBytes || wire.Length < 1 + 1)
        {
            return new PreKeyEnvelopeOutcome.Failed(SignalDecryptFailure.Truncated);
        }

        if ((wire[0] & 0x0F) != WireVersion)
        {
            return new PreKeyEnvelopeOutcome.Failed(SignalDecryptFailure.BadVersion);
        }

        PreKeySignalMessage preKey;
        try
        {
            preKey = PreKeySignalMessage.Parser.ParseFrom(wire.Slice(1).ToArray());
        }
        catch (InvalidProtocolBufferException)
        {
            return new PreKeyEnvelopeOutcome.Failed(SignalDecryptFailure.MalformedMessage);
        }

        byte[] baseKey = preKey.BaseKey.ToByteArray();
        byte[] identityKey = preKey.IdentityKey.ToByteArray();
        byte[] inner = preKey.Message.ToByteArray();
        if (baseKey.Length != RawKeyLength
            || identityKey.Length != DjbIdentityLength
            || identityKey[0] != DjbTypeByte
            || inner.Length == 0)
        {
            return new PreKeyEnvelopeOutcome.Failed(SignalDecryptFailure.MalformedMessage);
        }

        uint? preKeyId = preKey.PreKeyId == 0 ? null : preKey.PreKeyId;
        return new PreKeyEnvelopeOutcome.Parsed(new PreKeyWireContent(
            preKeyId,
            baseKey,
            identityKey,
            inner,
            preKey.RegistrationId,
            preKey.SignedPreKeyId));
    }

    /// <summary>
    /// PKCS7-style padding to a multiple of <see cref="PaddingBlockSize"/>:
    /// always adds padding (1..block-size bytes, each byte the pad length).
    /// </summary>
    internal static byte[] Pad(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        int padLength = PaddingBlockSize - (plaintext.Length % PaddingBlockSize);
        byte[] padded = new byte[plaintext.Length + padLength];
        plaintext.CopyTo(padded.AsSpan(0, plaintext.Length));
        padded.AsSpan(plaintext.Length).Fill((byte)padLength);
        return padded;
    }

    /// <summary>Validates and strips <see cref="Pad"/> padding; false on any violation (never throws).</summary>
    internal static bool TryUnpad(ReadOnlySpan<byte> padded, out byte[]? plaintext)
    {
        plaintext = null;
        if (padded.Length == 0 || padded.Length % PaddingBlockSize != 0)
        {
            return false;
        }

        int padLength = padded[padded.Length - 1];
        if (padLength == 0 || padLength > PaddingBlockSize || padLength > padded.Length)
        {
            return false;
        }

        for (int i = padded.Length - padLength; i < padded.Length; i++)
        {
            if (padded[i] != padLength)
            {
                return false;
            }
        }

        plaintext = padded.Slice(0, padded.Length - padLength).ToArray();
        return true;
    }

    private static byte[] FrameWhisperWire(
        byte[] body,
        ReadOnlySpan<byte> hmacKey,
        ReadOnlySpan<byte> senderIdentityDjb,
        ReadOnlySpan<byte> receiverIdentityDjb)
    {
        byte[] mac = ComputeWhisperMac(hmacKey, VersionByte, body, senderIdentityDjb, receiverIdentityDjb);
        try
        {
            byte[] wire = new byte[1 + body.Length + MacLength];
            wire[0] = VersionByte;
            body.CopyTo(wire.AsSpan(1));
            mac.CopyTo(wire.AsSpan(1 + body.Length));
            return wire;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(mac);
        }
    }

    private static byte[] ComputeWhisperMac(
        ReadOnlySpan<byte> hmacKey,
        byte versionByte,
        ReadOnlySpan<byte> body,
        ReadOnlySpan<byte> senderIdentityDjb,
        ReadOnlySpan<byte> receiverIdentityDjb)
    {
        byte[] input = new byte[1 + body.Length + senderIdentityDjb.Length + receiverIdentityDjb.Length];
        input[0] = versionByte;
        body.CopyTo(input.AsSpan(1));
        senderIdentityDjb.CopyTo(input.AsSpan(1 + body.Length));
        receiverIdentityDjb.CopyTo(input.AsSpan(1 + body.Length + senderIdentityDjb.Length));
        try
        {
            byte[] full = HMACSHA256.HashData(hmacKey.ToArray(), input);
            try
            {
                return full.AsSpan(0, MacLength).ToArray();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(full);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private static byte[] AesCbcEncrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        aes.IV = iv.ToArray();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using ICryptoTransform encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plaintext.ToArray(), 0, plaintext.Length);
    }

    private static byte[] AesCbcDecrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, byte[] ciphertext)
    {
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        aes.IV = iv.ToArray();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using ICryptoTransform decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }

    private static void ThrowUnlessKey(ReadOnlySpan<byte> key, int expected)
    {
        if (key.Length != expected)
        {
            throw new SdkFormatException("Signal key material has an unexpected length.");
        }
    }

    private static void ThrowUnlessDjb(ReadOnlySpan<byte> djb)
    {
        if (djb.Length != DjbIdentityLength || djb[0] != DjbTypeByte)
        {
            throw new SdkFormatException("Signal identity keys must be 33-byte DJB-encoded values.");
        }
    }
}
