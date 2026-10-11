using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using NSec.Cryptography;

namespace Iwasug.WhatsApp.Sdk.Signal;

/// <summary>
/// Typed group-decrypt outcome (04-05, MSG-02): decryption failures are VALUES,
/// never exceptions thrown through the stanza pump — identical discipline to
/// <see cref="SignalDecryptOutcome"/>. The reason names the rule only; no key,
/// plaintext, or ciphertext bytes ever surface (T-4-01 hygiene).
/// </summary>
internal abstract record SenderKeyDecryptOutcome
{
    /// <summary>Signature verified and padding validated; the plaintext is fresh output owned by the caller.</summary>
    internal sealed record Decrypted(byte[] Plaintext) : SenderKeyDecryptOutcome;

    /// <summary>The wire bytes failed validation; the reason names the rule only.</summary>
    internal sealed record Failed(SenderKeyDecryptFailure Reason) : SenderKeyDecryptOutcome;
}

/// <summary>Rule-only group-decrypt failure reasons (never key material).</summary>
internal enum SenderKeyDecryptFailure
{
    /// <summary>The version byte's low nibble is not the pinned Signal version.</summary>
    BadVersion,

    /// <summary>The wire bytes are shorter than version + minimal body + signature.</summary>
    Truncated,

    /// <summary>The protobuf body does not parse or the ciphertext is not block-aligned.</summary>
    MalformedMessage,

    /// <summary>No stored sender key matches the message's sender-key id (SKDM not yet received).</summary>
    UnknownKey,

    /// <summary>The appended Ed25519 signature does not verify under the stored signing key.</summary>
    BadSignature,

    /// <summary>The iteration gap past the stored chain exceeds the bounded skip limit.</summary>
    GapTooLarge,

    /// <summary>The iteration was already consumed and its message key is no longer cached.</summary>
    OldCounter,

    /// <summary>The AES-CBC output fails padding validation.</summary>
    BadPadding,
}

/// <summary>
/// One parsed sender-key distribution message (SKDM) — the axolotl inner form
/// carried inside <c>Message.senderKeyDistributionMessage</c> (04-05, MSG-02).
/// </summary>
/// <param name="SenderKeyId">The sender-key generation id the receiving chain is keyed by.</param>
/// <param name="Iteration">The sending chain's current iteration at distribution time.</param>
/// <param name="ChainKey">The 32-byte sender chain key seed. Never logged.</param>
/// <param name="SigningKeyPublic">The 32-byte RAW Ed25519 public signing key (never the DJB form). Never logged.</param>
internal sealed record SenderKeyDistribution(
    uint SenderKeyId,
    uint Iteration,
    byte[] ChainKey,
    byte[] SigningKeyPublic);

/// <summary>
/// Sender-key group E2EE state machine (04-05, MSG-02; 04-RESEARCH Pattern 6):
/// per-group sender chain (chain key + iteration), an Ed25519 signing keypair,
/// SKDM construction/parsing, and signed <c>skmsg</c> encrypt/decrypt. Wire
/// layouts are pinned from the MIT-licensed Baileys <c>src/Signal/Group/*</c>
/// sources (NOTICE rows), written independently:
/// <list type="bullet">
/// <item><description>Distribution: <c>version(0x33) || serialized SenderKeyDistributionMessage{id, iteration, chainKey, signingKey}</c>.</description></item>
/// <item><description>Group message: <c>version(0x33) || serialized SenderKeyMessage{id, iteration, ciphertext} || 64-byte Ed25519 signature</c> over the version-prefixed bytes — verified BEFORE any event (T-4-15).</description></item>
/// <item><description>Chain KDF: message-key seed = HMAC-SHA256(chainKey, 0x01), next chain key = HMAC-SHA256(chainKey, 0x02). Seed expansion: HKDF-SHA256(ikm=seed, salt=32 zero bytes, info="WhisperGroup", 64 bytes) → iv = okm[0..16], cipher key = okm[16..48]. AES-256-CBC over the 160-byte-block padded plaintext; NO MAC — the Ed25519 signature provides integrity.</description></item>
/// </list>
/// <para>
/// Bounded iteration handling (Baileys sender-key-state behavior): a future gap up
/// to <see cref="MaxFutureGap"/> derives forward and caches the skipped message-key
/// seeds (oldest evicted past <see cref="MaxMessageKeys"/>); a larger gap fails
/// typed; an already-consumed iteration without a cached seed fails
/// <see cref="SenderKeyDecryptFailure.OldCounter"/>.
/// </para>
/// <para>
/// Discipline (Shared Patterns 4/5): NSec typed keys only
/// (<see cref="SignatureAlgorithm.Ed25519"/> — no hand-rolled curve math), BCL
/// <see cref="HMACSHA256"/>/<see cref="HKDF"/>/<see cref="Aes"/>, zeroized
/// temporaries, and no log call on this class ever sees key, chain, or plaintext
/// bytes. Persistence is the SDK-owned
/// <see cref="SenderKeyRecordStructure"/> bytes round-trip (process-local
/// repository — see <see cref="SenderKeyRepository"/>).
/// </para>
/// </summary>
internal sealed class SenderKey
{
    /// <summary>Sender chain key length (32 bytes).</summary>
    internal const int ChainKeyLength = 32;

    /// <summary>RAW Ed25519 signing public key length (the SKDM wire form — never DJB-prefixed).</summary>
    internal const int SigningKeyLength = 32;

    /// <summary>Bound on cached skipped message-key seeds per state (T-4-16 DoS bound; oldest evicted).</summary>
    internal const int MaxMessageKeys = 2000;

    /// <summary>Bound on a single forward iteration gap (a gap beyond this fails typed).</summary>
    internal const int MaxFutureGap = 2000;

    /// <summary>Bound on stored sender-key states per record (libsignal keeps the most recent 5).</summary>
    internal const int MaxStates = 5;

    /// <summary>Fail-closed cap on accepted wire input (garbage larger than this is not a message).</summary>
    internal const int MaxWireBytes = 64 * 1024;

    /// <summary>HKDF info label for the group message-key expansion ("WhisperGroup", libsignal deriveSecrets).</summary>
    internal const string MessageKeyInfoLabel = "WhisperGroup";

    /// <summary>HKDF output length for the group message-key expansion (iv ‖ cipher key ‖ unused tail).</summary>
    internal const int MessageKeyOutputLength = 64;

    /// <summary>Chain-KDF marker for deriving one message-key seed.</summary>
    internal const byte MessageKeySeedMarker = 0x01;

    /// <summary>Chain-KDF marker for deriving the next chain key.</summary>
    internal const byte MessageKeyAdvanceMarker = 0x02;

    private static readonly byte[] EmptySalt = new byte[ChainKeyLength];

    /// <summary>One sender-key state (generation): chain, iteration, signing keypair, cached skipped seeds.</summary>
    private sealed class State
    {
        internal uint SenderKeyId { get; init; }

        internal byte[] ChainKey { get; set; } = [];

        internal uint Iteration { get; set; }

        /// <summary>RAW Ed25519 private key; null on states created from an inbound SKDM (receive-only).</summary>
        internal byte[]? SigningKeyPrivate { get; set; }

        internal byte[] SigningKeyPublic { get; set; } = [];

        /// <summary>Cached skipped message-key seeds keyed by iteration (bounded, oldest evicted).</summary>
        internal SortedDictionary<uint, byte[]> MessageKeySeeds { get; } = new();
    }

    private readonly List<State> _states = [];

    private SenderKey()
    {
    }

    /// <summary>The most recent state's sender-key id (the generation this record actively sends under).</summary>
    /// <exception cref="SdkFormatException">The record holds no sender-key state.</exception>
    internal uint ActiveKeyId => ActiveState().SenderKeyId;

    /// <summary>
    /// Creates a fresh outbound sender key: random 32-bit id, random 32-byte chain
    /// seed, iteration 0, and a fresh Ed25519 signing keypair (NSec
    /// <see cref="SignatureAlgorithm.Ed25519"/> — plaintext export allowed because
    /// the private is persisted in the SDK-owned record serialization).
    /// </summary>
    internal static SenderKey Create()
    {
        var parameters = new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        };
        byte[] signingPrivate;
        byte[] signingPublic;
        using (Key key = Key.Create(SignatureAlgorithm.Ed25519, parameters))
        {
            signingPrivate = key.Export(KeyBlobFormat.RawPrivateKey);
            signingPublic = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        }

        var state = new State
        {
            SenderKeyId = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4)),
            ChainKey = RandomNumberGenerator.GetBytes(ChainKeyLength),
            Iteration = 0,
            SigningKeyPrivate = signingPrivate,
            SigningKeyPublic = signingPublic,
        };
        var senderKey = new SenderKey();
        senderKey._states.Add(state);
        return senderKey;
    }

    /// <summary>An empty record (no states) — inbound SKDMs fill it via <see cref="StoreDistribution"/>.</summary>
    internal static SenderKey Empty() => new();

    /// <summary>
    /// Builds the SKDM wire for the active state:
    /// <c>version(0x33) || serialized SenderKeyDistributionMessage</c>. Built from
    /// the state BEFORE the send's encryption advances it, so the receiver starts
    /// at the exact iteration the accompanying <c>skmsg</c> derives from.
    /// </summary>
    /// <exception cref="SdkFormatException">The record holds no state.</exception>
    internal byte[] BuildDistribution()
    {
        State state = ActiveState();
        var message = new SenderKeyDistributionMessage
        {
            Id = state.SenderKeyId,
            Iteration = state.Iteration,
            ChainKey = ByteString.CopyFrom(state.ChainKey),
            SigningKey = ByteString.CopyFrom(state.SigningKeyPublic),
        };
        byte[] body = message.ToByteArray();
        byte[] wire = new byte[1 + body.Length];
        wire[0] = SignalCipher.VersionByte;
        body.CopyTo(wire.AsSpan(1));
        return wire;
    }

    /// <summary>
    /// Bounds-checked parse of one SKDM wire (untrusted input): version byte,
    /// protobuf body, and exact key lengths; false on any violation (never throws,
    /// never names key bytes).
    /// </summary>
    internal static bool TryParseDistribution(ReadOnlySpan<byte> wire, out SenderKeyDistribution? distribution)
    {
        distribution = null;
        if (wire.Length <= 1 || wire.Length > MaxWireBytes || (wire[0] & 0x0F) != SignalCipher.WireVersion)
        {
            return false;
        }

        SenderKeyDistributionMessage message;
        try
        {
            message = SenderKeyDistributionMessage.Parser.ParseFrom(wire.Slice(1).ToArray());
        }
        catch (InvalidProtocolBufferException)
        {
            return false;
        }

        byte[] chainKey = message.ChainKey.ToByteArray();
        byte[] signingKey = message.SigningKey.ToByteArray();
        if (chainKey.Length != ChainKeyLength || signingKey.Length != SigningKeyLength)
        {
            return false;
        }

        distribution = new SenderKeyDistribution(message.Id, message.Iteration, chainKey, signingKey);
        return true;
    }

    /// <summary>
    /// Stores (or refreshes) the state described by an inbound SKDM: the chain
    /// resets to the distributed seed at the distributed iteration (Baileys
    /// sender-key-state behavior), and the state carries no signing private — a
    /// receive-only state can never be mistaken for an outbound one.
    /// </summary>
    internal void StoreDistribution(SenderKeyDistribution distribution)
    {
        ArgumentNullException.ThrowIfNull(distribution);
        State? existing = null;
        foreach (State state in _states)
        {
            if (state.SenderKeyId == distribution.SenderKeyId)
            {
                existing = state;
                break;
            }
        }

        if (existing is null)
        {
            _states.Add(new State
            {
                SenderKeyId = distribution.SenderKeyId,
                ChainKey = (byte[])distribution.ChainKey.Clone(),
                Iteration = distribution.Iteration,
                SigningKeyPrivate = null,
                SigningKeyPublic = (byte[])distribution.SigningKeyPublic.Clone(),
            });
            while (_states.Count > MaxStates)
            {
                Zero(_states[0].ChainKey);
                foreach (byte[] seed in _states[0].MessageKeySeeds.Values)
                {
                    Zero(seed);
                }

                _states.RemoveAt(0);
            }

            return;
        }

        Zero(existing.ChainKey);
        foreach (byte[] seed in existing.MessageKeySeeds.Values)
        {
            Zero(seed);
        }

        existing.ChainKey = (byte[])distribution.ChainKey.Clone();
        existing.Iteration = distribution.Iteration;
        existing.SigningKeyPublic = (byte[])distribution.SigningKeyPublic.Clone();
        existing.MessageKeySeeds.Clear();
    }

    /// <summary>
    /// Encrypts one group plaintext: derives the next message key off the active
    /// sender chain (advancing it), pads + AES-256-CBC encrypts, frames
    /// <c>SenderKeyMessage{id, iteration, ciphertext}</c> behind the version byte,
    /// and appends the Ed25519 signature over the version-prefixed bytes. Callers
    /// MUST persist the advanced state (D-06) before the wire node leaves.
    /// </summary>
    /// <param name="plaintext">Message bytes (a serialized <c>Message</c> proto); must not be empty.</param>
    /// <returns>The complete skmsg wire bytes (version || proto || signature).</returns>
    /// <exception cref="SdkFormatException">The plaintext is empty, the record has no state, or the active state has no signing private.</exception>
    internal byte[] Encrypt(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (plaintext.Length == 0)
        {
            throw new SdkFormatException("Group plaintext must not be empty.");
        }

        State state = ActiveState();
        if (state.SigningKeyPrivate is null)
        {
            throw new SdkFormatException("The sender key has no signing private key: the group message was not encrypted.");
        }

        uint iteration = state.Iteration;
        (byte[] seed, byte[] nextChainKey) = DeriveChainKey(state.ChainKey);
        Zero(state.ChainKey);
        state.ChainKey = nextChainKey;
        state.Iteration = iteration + 1;

        byte[] padded = SignalCipher.Pad(plaintext);
        byte[] ciphertext;
        try
        {
            (byte[] iv, byte[] cipherKey) = ExpandMessageKey(seed);
            try
            {
                ciphertext = AesCbcEncrypt(cipherKey, iv, padded);
            }
            finally
            {
                Zero(cipherKey);
                Zero(iv);
            }
        }
        finally
        {
            Zero(padded);
            Zero(seed);
        }

        var body = new SenderKeyMessage
        {
            Id = state.SenderKeyId,
            Iteration = iteration,
            Ciphertext = ByteString.CopyFrom(ciphertext),
        }.ToByteArray();
        byte[] signed = new byte[1 + body.Length];
        signed[0] = SignalCipher.VersionByte;
        body.CopyTo(signed.AsSpan(1));
        byte[] signature = Sign(state.SigningKeyPrivate, signed);
        try
        {
            byte[] wire = new byte[signed.Length + SignalCipher.SignatureLength];
            signed.CopyTo(wire);
            signature.CopyTo(wire.AsSpan(signed.Length));
            return wire;
        }
        finally
        {
            Zero(signature);
        }
    }

    /// <summary>
    /// Decrypts one <c>skmsg</c> wire (untrusted peer input): version, signature
    /// (verified FIRST — T-4-15), protobuf body, bounded chain derivation, and
    /// padding are each validated fail-closed as a typed outcome. The chain
    /// mutation is durable state: callers persist this record (D-06) after a
    /// <see cref="SenderKeyDecryptOutcome.Decrypted"/> result and BEFORE acking.
    /// </summary>
    internal SenderKeyDecryptOutcome Decrypt(ReadOnlySpan<byte> wire)
    {
        if (wire.Length > MaxWireBytes || wire.Length < 1 + SignalCipher.SignatureLength + 1)
        {
            return new SenderKeyDecryptOutcome.Failed(SenderKeyDecryptFailure.Truncated);
        }

        if ((wire[0] & 0x0F) != SignalCipher.WireVersion)
        {
            return new SenderKeyDecryptOutcome.Failed(SenderKeyDecryptFailure.BadVersion);
        }

        ReadOnlySpan<byte> signed = wire.Slice(0, wire.Length - SignalCipher.SignatureLength);
        ReadOnlySpan<byte> signature = wire.Slice(wire.Length - SignalCipher.SignatureLength, SignalCipher.SignatureLength);

        SenderKeyMessage message;
        try
        {
            message = SenderKeyMessage.Parser.ParseFrom(signed.Slice(1).ToArray());
        }
        catch (InvalidProtocolBufferException)
        {
            return new SenderKeyDecryptOutcome.Failed(SenderKeyDecryptFailure.MalformedMessage);
        }

        byte[] ciphertext = message.Ciphertext.ToByteArray();
        if (ciphertext.Length == 0 || ciphertext.Length % 16 != 0)
        {
            return new SenderKeyDecryptOutcome.Failed(SenderKeyDecryptFailure.MalformedMessage);
        }

        State? state = null;
        foreach (State candidate in _states)
        {
            if (candidate.SenderKeyId == message.Id)
            {
                state = candidate;
                break;
            }
        }

        if (state is null)
        {
            return new SenderKeyDecryptOutcome.Failed(SenderKeyDecryptFailure.UnknownKey);
        }

        if (!Verify(state.SigningKeyPublic, signed, signature))
        {
            return new SenderKeyDecryptOutcome.Failed(SenderKeyDecryptFailure.BadSignature);
        }

        (byte[]? seed, SenderKeyDecryptFailure? failure) = TakeMessageSeed(state, message.Iteration);
        if (seed is null)
        {
            return new SenderKeyDecryptOutcome.Failed(failure ?? SenderKeyDecryptFailure.OldCounter);
        }

        byte[] padded;
        try
        {
            (byte[] iv, byte[] cipherKey) = ExpandMessageKey(seed);
            try
            {
                padded = AesCbcDecrypt(cipherKey, iv, ciphertext);
            }
            finally
            {
                Zero(cipherKey);
                Zero(iv);
            }
        }
        finally
        {
            Zero(seed);
        }

        try
        {
            if (!SignalCipher.TryUnpad(padded, out byte[]? plaintext) || plaintext is null)
            {
                return new SenderKeyDecryptOutcome.Failed(SenderKeyDecryptFailure.BadPadding);
            }

            return new SenderKeyDecryptOutcome.Decrypted(plaintext);
        }
        finally
        {
            Zero(padded);
        }
    }

    /// <summary>
    /// Serializes the record through <c>SenderKeyRecordStructure</c> — the SDK-owned
    /// persisted format hosts store opaquely (session-serialization precedent,
    /// plan 04-01). Signing privates ride the record (never logged; the host owns
    /// the bytes' storage).
    /// </summary>
    internal byte[] Serialize()
    {
        var record = new SenderKeyRecordStructure();
        foreach (State state in _states)
        {
            var entry = new SenderKeyRecordStructure.Types.SenderKeyStateStructure
            {
                SenderKeyId = state.SenderKeyId,
                SenderChainKey = new SenderKeyRecordStructure.Types.SenderKeyStateStructure.Types.SenderChainKey
                {
                    Iteration = state.Iteration,
                    Seed = ByteString.CopyFrom(state.ChainKey),
                },
                SenderSigningKey = new SenderKeyRecordStructure.Types.SenderKeyStateStructure.Types.SenderSigningKey
                {
                    Public = ByteString.CopyFrom(state.SigningKeyPublic),
                },
            };
            if (state.SigningKeyPrivate is not null)
            {
                entry.SenderSigningKey.Private = ByteString.CopyFrom(state.SigningKeyPrivate);
            }

            foreach ((uint iteration, byte[] seed) in state.MessageKeySeeds)
            {
                entry.SenderMessageKeys.Add(new SenderKeyRecordStructure.Types.SenderKeyStateStructure.Types.SenderMessageKey
                {
                    Iteration = iteration,
                    Seed = ByteString.CopyFrom(seed),
                });
            }

            record.SenderKeyStates.Add(entry);
        }

        return record.ToByteArray();
    }

    /// <summary>
    /// Deserializes one record from <see cref="Serialize"/> bytes. Malformed
    /// states are skipped bounds-checked (a corrupt state never poisons the
    /// whole record); a wholly unparsable payload throws a rule-only
    /// <see cref="SdkFormatException"/>.
    /// </summary>
    internal static SenderKey Deserialize(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        SenderKeyRecordStructure record;
        try
        {
            record = SenderKeyRecordStructure.Parser.ParseFrom(bytes);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new SdkFormatException("Sender-key record bytes failed validation.", ex);
        }

        var senderKey = new SenderKey();
        foreach (SenderKeyRecordStructure.Types.SenderKeyStateStructure entry in record.SenderKeyStates)
        {
            byte[] chainKey = entry.SenderChainKey.Seed.ToByteArray();
            byte[] signingPublic = entry.SenderSigningKey.Public.ToByteArray();
            if (chainKey.Length != ChainKeyLength || signingPublic.Length != SigningKeyLength)
            {
                continue; // bounds-checked skip: one malformed state never poisons the record
            }

            var state = new State
            {
                SenderKeyId = entry.SenderKeyId,
                ChainKey = chainKey,
                Iteration = entry.SenderChainKey.Iteration,
                SigningKeyPublic = signingPublic,
            };
            byte[] signingPrivate = entry.SenderSigningKey.Private.ToByteArray();
            if (signingPrivate.Length == ChainKeyLength)
            {
                state.SigningKeyPrivate = signingPrivate;
            }

            foreach (SenderKeyRecordStructure.Types.SenderKeyStateStructure.Types.SenderMessageKey messageKey in entry.SenderMessageKeys)
            {
                byte[] seed = messageKey.Seed.ToByteArray();
                if (seed.Length != ChainKeyLength)
                {
                    continue;
                }

                state.MessageKeySeeds[messageKey.Iteration] = seed;
            }

            senderKey._states.Add(state);
            while (senderKey._states.Count > MaxStates)
            {
                senderKey._states.RemoveAt(0);
            }
        }

        return senderKey;
    }

    private State ActiveState() =>
        _states.Count == 0
            ? throw new SdkFormatException("The sender-key record holds no state.")
            : _states[^1];

    /// <summary>
    /// Resolves the message-key seed for <paramref name="iteration"/> against the
    /// state's chain, mutating the chain forward within the bounded gap and
    /// caching skipped seeds; an already-consumed iteration reads its cached seed
    /// exactly once. Returns the failure reason when no seed can be produced.
    /// </summary>
    private static (byte[]? Seed, SenderKeyDecryptFailure? Failure) TakeMessageSeed(State state, uint iteration)
    {
        if (iteration == state.Iteration)
        {
            (byte[] seed, byte[] nextChainKey) = DeriveChainKey(state.ChainKey);
            Zero(state.ChainKey);
            state.ChainKey = nextChainKey;
            state.Iteration = iteration + 1;
            return (seed, null);
        }

        if (iteration > state.Iteration)
        {
            if (iteration - state.Iteration > MaxFutureGap)
            {
                return (null, SenderKeyDecryptFailure.GapTooLarge);
            }

            byte[]? seed = null;
            while (state.Iteration <= iteration)
            {
                (byte[] current, byte[] nextChainKey) = DeriveChainKey(state.ChainKey);
                if (state.Iteration < iteration)
                {
                    StoreMessageKeySeed(state, state.Iteration, current);
                }
                else
                {
                    seed = current;
                }

                Zero(state.ChainKey);
                state.ChainKey = nextChainKey;
                state.Iteration++;
            }

            return (seed, null);
        }

        return state.MessageKeySeeds.Remove(iteration, out byte[]? cached)
            ? (cached, null)
            : (null, SenderKeyDecryptFailure.OldCounter);
    }

    private static void StoreMessageKeySeed(State state, uint iteration, byte[] seed)
    {
        state.MessageKeySeeds[iteration] = seed;
        while (state.MessageKeySeeds.Count > MaxMessageKeys)
        {
            uint oldest = 0;
            bool found = false;
            foreach (uint key in state.MessageKeySeeds.Keys)
            {
                oldest = key;
                found = true;
                break;
            }

            if (!found || !state.MessageKeySeeds.Remove(oldest, out byte[]? evicted))
            {
                break;
            }

            Zero(evicted);
        }
    }

    private static (byte[] Seed, byte[] NextChainKey) DeriveChainKey(ReadOnlySpan<byte> chainKey)
    {
        if (chainKey.Length != ChainKeyLength)
        {
            throw new SdkFormatException("Sender-key chain keys must be 32 bytes.");
        }

        byte[] key = chainKey.ToArray();
        try
        {
            byte[] seed = HMACSHA256.HashData(key, new byte[] { MessageKeySeedMarker });
            byte[] next = HMACSHA256.HashData(key, new byte[] { MessageKeyAdvanceMarker });
            return (seed, next);
        }
        finally
        {
            Zero(key);
        }
    }

    private static (byte[] Iv, byte[] CipherKey) ExpandMessageKey(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != ChainKeyLength)
        {
            throw new SdkFormatException("Sender-key message seeds must be 32 bytes.");
        }

        byte[] okm = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            seed.ToArray(),
            MessageKeyOutputLength,
            EmptySalt,
            Encoding.ASCII.GetBytes(MessageKeyInfoLabel));
        try
        {
            byte[] iv = new byte[SignalCipher.IvLength];
            byte[] cipherKey = new byte[SignalCipher.AesKeyLength];
            okm.AsSpan(0, SignalCipher.IvLength).CopyTo(iv);
            okm.AsSpan(SignalCipher.IvLength, SignalCipher.AesKeyLength).CopyTo(cipherKey);
            return (iv, cipherKey);
        }
        finally
        {
            Zero(okm);
        }
    }

    private static byte[] Sign(byte[] signingPrivate, ReadOnlySpan<byte> data)
    {
        byte[] copy = signingPrivate.ToArray();
        try
        {
            using Key key = Key.Import(SignatureAlgorithm.Ed25519, copy, KeyBlobFormat.RawPrivateKey);
            return SignatureAlgorithm.Ed25519.Sign(key, data.ToArray());
        }
        finally
        {
            Zero(copy);
        }
    }

    private static bool Verify(ReadOnlySpan<byte> signingPublic, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (signingPublic.Length != SigningKeyLength || signature.Length != SignalCipher.SignatureLength)
        {
            return false;
        }

        PublicKey key;
        try
        {
            key = PublicKey.Import(SignatureAlgorithm.Ed25519, signingPublic.ToArray(), KeyBlobFormat.RawPublicKey);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }

        // PublicKey does not implement IDisposable: NSec manages the native
        // allocation; verification happens on explicit byte copies only.
        return SignatureAlgorithm.Ed25519.Verify(key, data.ToArray(), signature.ToArray());
    }

    private static byte[] AesCbcEncrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> plaintext)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key.ToArray();
        aes.IV = iv.ToArray();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using ICryptoTransform encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plaintext.ToArray(), 0, plaintext.Length);
    }

    private static byte[] AesCbcDecrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, byte[] ciphertext)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key.ToArray();
        aes.IV = iv.ToArray();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using ICryptoTransform decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }

    private static void Zero(byte[]? buffer)
    {
        if (buffer is not null)
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}

/// <summary>
/// SDK-owned process-local sender-key store (04-05, MSG-02): records are held as
/// opaque <see cref="SenderKeyRecordStructure"/> bytes keyed by a canonical
/// address, exactly like plan 04-01's session serialization — hosts never see
/// them. A lost record (process restart) degrades gracefully: the next group
/// send re-distributes a fresh SKDM, and a member missing a key re-receives one
/// (documented design decision; an <c>ISenderKeyStore</c> host seam is deferred
/// to v2 alongside GRPM-01).
/// </summary>
/// <remarks>
/// Writes are synchronous and atomic in-memory; D-06 ordering (persist BEFORE
/// the protocol acknowledgment or the wire node leaves) is therefore structural —
/// the write completes before the caller's next line runs.
/// </remarks>
internal sealed class SenderKeyRepository
{
    private readonly ConcurrentDictionary<string, byte[]> _records = new(StringComparer.Ordinal);

    /// <summary>Address for THIS account's outbound sender key in one group.</summary>
    internal static string OutboundAddress(Jid group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"sender-key:out:{group}");
    }

    /// <summary>Address for the sender key one REMOTE member device distributes to this account in one group (T-4-15: keyed by (group, sender identity)).</summary>
    internal static string InboundAddress(Jid group, Jid sender)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(sender);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"sender-key:in:{group}|{sender}");
    }

    /// <summary>Loads the record at <paramref name="address"/>, or null when none exists.</summary>
    internal SenderKey? TryLoad(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!_records.TryGetValue(address, out byte[]? bytes))
        {
            return null;
        }

        return SenderKey.Deserialize(bytes);
    }

    /// <summary>Persists the record's serialization at <paramref name="address"/> (D-06: awaited by callers before any wire work).</summary>
    internal void Save(string address, SenderKey key)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(key);
        _records[address] = key.Serialize();
    }

    /// <summary>Stores one parsed SKDM at <paramref name="address"/>, creating the record when absent.</summary>
    internal void PutDistribution(string address, SenderKeyDistribution distribution)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(distribution);
        SenderKey key = TryLoad(address) ?? SenderKey.Empty();
        key.StoreDistribution(distribution);
        Save(address, key);
    }
}
