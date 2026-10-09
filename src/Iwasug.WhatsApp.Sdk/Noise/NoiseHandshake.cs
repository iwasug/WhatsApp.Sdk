using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;
using Iwasug.WhatsApp.Sdk.Errors;
using NSec.Cryptography;

namespace Iwasug.WhatsApp.Sdk.Noise;

/// <summary>
/// Supplies the opaque handshake payload blobs carried by <see cref="NoiseHandshake"/>.
/// The payloads' schema semantics belong to Phase 3 pairing (ClientHello registration,
/// ClientFinish login) — the handshake carries the bytes without parsing, logging, or
/// validating their content.
/// </summary>
/// <remarks>
/// Implementations must return a non-null array on every call; blobs larger than
/// <see cref="NoiseHandshake.MaxHandshakePayloadBytes"/> are rejected fail-closed.
/// </remarks>
public interface INoiseHandshakePayloads
{
    /// <summary>Opaque client payload carried in handshake message A.</summary>
    /// <returns>The blob bytes. Must not be null.</returns>
    byte[] GetClientHello();

    /// <summary>Opaque client payload carried in handshake message C.</summary>
    /// <returns>The blob bytes. Must not be null.</returns>
    byte[] GetClientFinish();
}

/// <summary>
/// Noise XX symmetric state (02-03, spec-faithful): the <c>h / ck / k / n</c> tuple
/// from Noise spec rev 33/34 section 5 over SHA-256 + AES-256-GCM, driving the
/// <c>-&gt;e / &lt;-e,ee,s,es / -&gt;s,se</c> token flow (section 7.5) for
/// <c>Noise_XX_25519_AESGCM_SHA256</c>.
/// </summary>
/// <remarks>
/// <para>
/// Provenance: composed from the public Noise spec (message flow, MixHash/MixKey/
/// EncryptAndHash/Split semantics, 32-byte null-padded protocol name) and the
/// MIT-licensed Baileys handshake mechanics read-only in 02-SPIKE-NOTES.md
/// (F1/F2/O2: AESGCM suite, prologue authentication, per-DH counter reset,
/// <c>HKDF(data, 64, salt, info='')</c> split into chaining-key-then-cipher-key).
/// Proven against the committed cacophony vectors in
/// <c>tests/.../TestData/noise-xx-aesgcm-vectors.json</c> — crypto correctness
/// is imported, never self-generated (T-2-05).
/// </para>
/// <para>
/// Nonce shape: 12 bytes of <c>0x00*4 || BE64(n)</c>. For handshake-phase
/// counters this is byte-identical to Baileys' <c>zeros[0..8) || BE32(n)</c>
/// layout (O2) for every realistic counter — the two descriptions agree, they
/// do not compete.
/// </para>
/// <para>
/// Key agreement uses NSec typed keys (F6 call shape); the Diffie-Hellman
/// composition here is protocol logic, never primitive math (STACK.md rule).
/// Single-owner disposable: <see cref="Dispose"/> zeroizes the chaining key,
/// cipher key, and handshake hash. Transport keys minted by <see cref="Split"/>
/// are owned copies — disposing this state does not touch them.
/// </para>
/// </remarks>
internal sealed class NoiseSymmetricState : IDisposable
{
    /// <summary>SHA-256 output length: chaining key, hash, and DH secret size.</summary>
    internal const int HashLength = 32;

    /// <summary>AES-256-GCM key length (Noise AESGCM uses 256-bit keys).</summary>
    internal const int KeyLength = 32;

    /// <summary>AES-GCM authentication tag length (full 128-bit tag).</summary>
    internal const int TagLength = 16;

    /// <summary>AEAD nonce length on the wire.</summary>
    internal const int NonceLength = 12;

    private byte[] _chainingKey;
    private byte[] _hash;
    private byte[]? _key;
    private ulong _nonce;
    private bool _hasKey;
    private bool _split;
    private int _disposed;

    /// <summary>
    /// Initializes the symmetric state: <c>h = ck = padded-protocol-name</c>,
    /// then authenticates the prologue bytes (Noise <c>MixHash(prologue)</c>).
    /// </summary>
    /// <param name="paddedProtocolName">
    /// The Noise protocol name null-padded to exactly 32 bytes
    /// (<c>Noise_XX_25519_AESGCM_SHA256</c> + 4 <c>0x00</c>).
    /// </param>
    /// <param name="prologue">
    /// Prologue bytes (the WA intro header in production, the vector prologue
    /// under test). Copied, never retained by reference.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The protocol name is not exactly 32 bytes after padding.
    /// </exception>
    internal NoiseSymmetricState(byte[] paddedProtocolName, byte[] prologue)
    {
        ArgumentNullException.ThrowIfNull(paddedProtocolName);
        ArgumentNullException.ThrowIfNull(prologue);
        if (paddedProtocolName.Length != NoiseProtocol.PaddedProtocolNameLength)
        {
            throw new ArgumentException("The padded protocol name must be exactly 32 bytes.", nameof(paddedProtocolName));
        }

        _hash = (byte[])paddedProtocolName.Clone();
        _chainingKey = (byte[])paddedProtocolName.Clone();
        _nonce = 0;
        MixHash(prologue);
    }

    /// <summary>
    /// Authenticates plaintext into the transcript: <c>h = SHA256(h || data)</c>.
    /// </summary>
    internal void MixHash(ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        byte[] input = new byte[_hash.Length + data.Length];
        _hash.CopyTo(input.AsSpan(0, _hash.Length));
        data.CopyTo(input.AsSpan(_hash.Length));
        byte[] next = SHA256.HashData(input);
        CryptographicOperations.ZeroMemory(input);
        CryptographicOperations.ZeroMemory(_hash);
        _hash = next;
    }

    /// <summary>
    /// Mixes key material into the chaining key and installs the fresh cipher
    /// key: <c>(ck, temp_k) = HKDF(ck, input, 2)</c>, then resets the nonce.
    /// </summary>
    /// <param name="inputKeyMaterial">The 32-byte DH shared secret. Never logged.</param>
    internal void MixKey(ReadOnlySpan<byte> inputKeyMaterial)
    {
        ThrowIfDisposed();
        (byte[] nextCk, byte[] nextKey) = HkdfTwoBlocks(_chainingKey, inputKeyMaterial);
        CryptographicOperations.ZeroMemory(_chainingKey);
        if (_key is not null)
        {
            CryptographicOperations.ZeroMemory(_key);
        }

        _chainingKey = nextCk;
        _key = nextKey;
        _nonce = 0;
        _hasKey = true;
    }

    /// <summary>
    /// Encrypts and authenticates: AES-256-GCM with the running hash as
    /// associated data, then mixes the ciphertext into the transcript.
    /// Consumes one nonce. Requires a prior <see cref="MixKey"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">No cipher key is installed yet.</exception>
    internal byte[] EncryptAndHash(ReadOnlySpan<byte> plaintext)
    {
        ThrowIfDisposed();
        if (!_hasKey || _key is null)
        {
            throw new InvalidOperationException("Handshake encryption requires a mixed key.");
        }

        byte[] ciphertext = AeadEncrypt(_key, _nonce, _hash, plaintext);
        _nonce++;
        MixHash(ciphertext);
        return ciphertext;
    }

    /// <summary>
    /// Decrypts and authenticates: verifies with the running hash as
    /// associated data, then mixes the ciphertext into the transcript.
    /// The nonce advances only on success. Requires a prior <see cref="MixKey"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">No cipher key is installed yet.</exception>
    /// <exception cref="SdkConnectionException">
    /// Authentication failed. The message names the rule only — never peer bytes.
    /// </exception>
    internal byte[] DecryptAndHash(ReadOnlySpan<byte> ciphertext)
    {
        ThrowIfDisposed();
        if (!_hasKey || _key is null)
        {
            throw new InvalidOperationException("Handshake decryption requires a mixed key.");
        }

        byte[] plaintext;
        try
        {
            plaintext = AeadDecrypt(_key, _nonce, _hash, ciphertext);
        }
        catch (CryptographicException ex)
        {
            throw new SdkConnectionException("Handshake payload failed authentication.", ex);
        }

        _nonce++;
        MixHash(ciphertext);
        return plaintext;
    }

    /// <summary>
    /// Previews the <see cref="Split"/> output after one more
    /// <see cref="MixKey"/> step — WITHOUT mutating this state. The initiator
    /// knows both Diffie-Hellman inputs of its <c>se</c> step as soon as
    /// message 2 is processed (own static + responder ephemeral), so the
    /// post-<c>se</c> chaining key — and hence the spec transport keys — are
    /// already determined while the running state must stay behind for
    /// message 3's token order. Pure function of the current chaining key.
    /// </summary>
    /// <param name="nextMixKeyMaterial">
    /// The 32-byte DH secret of the upcoming mix. Never logged.
    /// </param>
    /// <returns>Freshly allocated (k1, k2) copies owned by the caller.</returns>
    internal (byte[] K1, byte[] K2) PreviewSplit(ReadOnlySpan<byte> nextMixKeyMaterial)
    {
        ThrowIfDisposed();
        (byte[] nextCk, byte[] nextKey) = HkdfTwoBlocks(_chainingKey, nextMixKeyMaterial);
        CryptographicOperations.ZeroMemory(nextKey);
        try
        {
            return HkdfTwoBlocks(nextCk, Array.Empty<byte>());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nextCk);
        }
    }

    /// <summary>
    /// Splits the handshake into the two transport direction keys:
    /// <c>(k1, k2) = HKDF(ck, zerolen, 2)</c>. The initiator sends with
    /// <c>k1</c> and receives with <c>k2</c>; the responder is mirrored.
    /// One-shot: a second split throws. The transcript hash stays live in
    /// the state — read it via <see cref="CurrentHash"/> once the final
    /// message is processed.
    /// </summary>
    /// <returns>Freshly allocated (k1, k2) copies owned by the caller.</returns>
    internal (byte[] K1, byte[] K2) Split()
    {
        ThrowIfDisposed();
        ThrowIfSplit();
        (byte[] k1, byte[] k2) = HkdfTwoBlocks(_chainingKey, Array.Empty<byte>());
        _split = true;
        return (k1, k2);
    }

    /// <summary>
    /// The live transcript hash (both parties must agree once the handshake
    /// completes). Returns a fresh copy; wiped on <see cref="Dispose"/>.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The state is disposed.</exception>
    internal byte[] CurrentHash
    {
        get
        {
            ThrowIfDisposed();
            return (byte[])_hash.Clone();
        }
    }

    /// <summary>
    /// Noise HKDF with two outputs: <c>temp = HMAC(ck, ikm)</c>,
    /// <c>out1 = HMAC(temp, 0x01)</c>, <c>out2 = HMAC(temp, out1 || 0x02)</c>.
    /// Implemented over the BCL RFC-5869 HKDF with <c>salt = ck</c> and empty
    /// <c>info</c>, which reduces to exactly this construction.
    /// </summary>
    internal static (byte[] First, byte[] Second) HkdfTwoBlocks(
        ReadOnlySpan<byte> chainingKey,
        ReadOnlySpan<byte> inputKeyMaterial)
    {
        byte[] okm = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            inputKeyMaterial.ToArray(),
            2 * HashLength,
            chainingKey.ToArray(),
            Array.Empty<byte>());
        byte[] first = new byte[HashLength];
        byte[] second = new byte[HashLength];
        okm.AsSpan(0, HashLength).CopyTo(first);
        okm.AsSpan(HashLength, HashLength).CopyTo(second);
        CryptographicOperations.ZeroMemory(okm);
        return (first, second);
    }

    /// <summary>
    /// Encodes a handshake-phase nonce: 12 bytes of
    /// <c>0x00*4 || BE64(n)</c> (Noise AESGCM shape; byte-identical to the
    /// Baileys O2 layout for every realistic counter).
    /// </summary>
    internal static byte[] EncodeNonce(ulong n)
    {
        byte[] nonce = new byte[NonceLength];
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4, 8), n);
        return nonce;
    }

    /// <summary>
    /// Raw AES-256-GCM encrypt: returns <c>ciphertext || tag(16)</c>.
    /// Shared by the handshake cipher and the vector transport-sample checks
    /// (one primitive — the nonce discipline differs per caller, never the math).
    /// </summary>
    internal static byte[] AeadEncrypt(
        ReadOnlySpan<byte> key,
        ulong nonce,
        ReadOnlySpan<byte> associatedData,
        ReadOnlySpan<byte> plaintext)
    {
        byte[] nonceBytes = EncodeNonce(nonce);
        byte[] output = new byte[plaintext.Length + TagLength];
        using var cipher = new AesGcm(key, TagLength);
        cipher.Encrypt(
            nonceBytes,
            plaintext,
            output.AsSpan(0, plaintext.Length),
            output.AsSpan(plaintext.Length, TagLength),
            associatedData);
        CryptographicOperations.ZeroMemory(nonceBytes);
        return output;
    }

    /// <summary>
    /// Raw AES-256-GCM decrypt of <c>ciphertext || tag(16)</c>.
    /// </summary>
    /// <exception cref="CryptographicException">Authentication failed.</exception>
    internal static byte[] AeadDecrypt(
        ReadOnlySpan<byte> key,
        ulong nonce,
        ReadOnlySpan<byte> associatedData,
        ReadOnlySpan<byte> ciphertext)
    {
        if (ciphertext.Length < TagLength)
        {
            throw new CryptographicException("Ciphertext is shorter than the authentication tag.");
        }

        byte[] nonceBytes = EncodeNonce(nonce);
        int plaintextLength = ciphertext.Length - TagLength;
        byte[] plaintext = new byte[plaintextLength];
        try
        {
            using var cipher = new AesGcm(key, TagLength);
            cipher.Decrypt(
                nonceBytes,
                ciphertext.Slice(0, plaintextLength),
                ciphertext.Slice(plaintextLength, TagLength),
                plaintext,
                associatedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonceBytes);
        }

        return plaintext;
    }

    /// <summary>
    /// X25519 agreement with an NSec-owned private key and a raw 32-byte peer
    /// public key. Returns the fresh 32-byte shared secret (caller wipes).
    /// </summary>
    /// <exception cref="SdkConnectionException">
    /// The peer key is unusable or agreement failed. Messages name the rule
    /// only — never key material.
    /// </exception>
    internal static byte[] DhAgree(Key ownPrivate, ReadOnlySpan<byte> peerPublicKey)
    {
        ArgumentNullException.ThrowIfNull(ownPrivate);
        PublicKey peer;
        try
        {
            peer = PublicKey.Import(
                KeyAgreementAlgorithm.X25519,
                peerPublicKey,
                KeyBlobFormat.RawPublicKey);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SdkConnectionException("Handshake key agreement failed: unusable peer key.", ex);
        }

        var parameters = new SharedSecretCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        };
        try
        {
            using SharedSecret agreement = KeyAgreementAlgorithm.X25519.Agree(ownPrivate, peer, parameters)
                ?? throw new SdkConnectionException("Handshake key agreement failed.");
            return agreement.Export(SharedSecretBlobFormat.RawSharedSecret);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SdkException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SdkConnectionException("Handshake key agreement failed.", ex);
        }
    }

    /// <summary>
    /// Zeroizes the chaining key, cipher key, and handshake hash. Transports
    /// minted by <see cref="Split"/> hold their own copies and are unaffected.
    /// Disposing twice is a no-op.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_chainingKey);
        CryptographicOperations.ZeroMemory(_hash);
        if (_key is not null)
        {
            CryptographicOperations.ZeroMemory(_key);
            _key = null;
        }

        _nonce = 0;
    }

    /// <summary>True once <see cref="Dispose"/> wiped every secret buffer.</summary>
    internal bool IsWiped => Volatile.Read(ref _disposed) == 1;

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new ObjectDisposedException(nameof(NoiseSymmetricState));
        }
    }

    private void ThrowIfSplit()
    {
        if (_split)
        {
            throw new InvalidOperationException("The handshake state already split into transport keys.");
        }
    }
}

/// <summary>
/// Noise XX handshake, initiator role (02-03, spec-faithful): <c>WriteMessageA →
/// ReadMessageB → WriteMessageC</c> over the XX token flow
/// (arrow-e / arrow-e-ee-s-es / arrow-s-se) with a fresh NSec X25519 ephemeral AND a
/// fresh static key per instance, and transport keys out of the spec
/// <c>Split()</c>.
/// </summary>
/// <remarks>
/// <para>
/// One instance per handshake attempt — both keys are created in the
/// constructor and message A is single-shot, so key material is structurally
/// never reused across reconnects (Pitfall 4). The connection creates a fresh
/// instance per attempt and disposes it once message C is sent, zeroizing the
/// keys and all intermediate chaining state (NSec keys live in libsodium
/// secure memory and are disposed; managed copies go through
/// <see cref="CryptographicOperations.ZeroMemory"/>). Phase 3 persists the
/// static identity; Phase 2 mints it per attempt (maximal forward secrecy now,
/// identity binding with pairing).
/// </para>
/// <para>
/// Wire shape: the connection sends the four-byte WA intro once, then message A,
/// B, and C are bare <c>HandshakeMessage</c> protobuf bodies carried in shared
/// three-byte length-prefixed frames. The protobuf is the carrier — the crypto
/// tokens inside are raw XX byte strings produced by the spec core, which the
/// cacophony vectors prove byte-for-byte. Peer bytes are untrusted: the intro is
/// validated once at the edge, envelopes must be present, and every length is
/// bounds-checked before any copy — any mismatch aborts with a rule-naming
/// <see cref="SdkConnectionException"/>, never with peer bytes in the message
/// (T-2-09; the loud log is the connection owner's job).
/// </para>
/// <para>
/// Ownership: <see cref="ReadMessageB"/> mints exactly one
/// <see cref="NoiseTransport"/> whose ownership transfers to the caller —
/// disposing the handshake afterwards does not touch the transport. State
/// guards reject out-of-order calls with
/// <see cref="InvalidOperationException"/>. Cancellation has no hook on this
/// synchronous API by design — aborting a handshake means disposing the
/// instance (the connection's cancel path does exactly this), which zeroizes
/// every in-flight secret; <see cref="IsKeyMaterialZeroized"/> proves it.
/// </para>
/// </remarks>
public sealed class NoiseHandshake : IDisposable
{
    /// <summary>X25519 public key size in bytes (ephemeral and static alike).</summary>
    internal const int PublicKeySize = 32;

    /// <summary>Encrypted static key size: 32-byte key plus the 16-byte AEAD tag.</summary>
    internal const int EncryptedStaticSize = PublicKeySize + NoiseSymmetricState.TagLength;

    /// <summary>
    /// Maximum accepted opaque handshake payload in bytes (32 KiB — handshake blobs are
    /// tiny; anything larger is garbage and fails closed).
    /// </summary>
    internal const int MaxHandshakePayloadBytes = 32 * 1024;

    private readonly INoiseHandshakePayloads _payloads;
    private readonly Key _ephemeral;
    private readonly Key _static;
    private readonly byte[] _ephemeralPublic;
    private readonly byte[] _staticPublic;
    private readonly NoiseSymmetricState _state;
    private byte[]? _responderEphemeral;
    private int _step;
    private int _disposed;

    /// <summary>
    /// Creates a handshake with a fresh NSec X25519 ephemeral key and a fresh
    /// static key, authenticated against the WA intro-header prologue.
    /// </summary>
    /// <param name="payloads">Opaque payload provider (Phase 3 supplies semantics).</param>
    /// <exception cref="ArgumentNullException"><paramref name="payloads"/> is null.</exception>
    public NoiseHandshake(INoiseHandshakePayloads payloads)
    {
        _payloads = payloads ?? throw new ArgumentNullException(nameof(payloads));
        KeyCreationParameters creationParameters = default;
        _ephemeral = Key.Create(KeyAgreementAlgorithm.X25519, creationParameters);
        try
        {
            _static = Key.Create(KeyAgreementAlgorithm.X25519, creationParameters);
        }
        catch
        {
            _ephemeral.Dispose();
            throw;
        }

        _ephemeralPublic = _ephemeral.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        _staticPublic = _static.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        _state = new NoiseSymmetricState(PaddedProtocolName(), NoiseProtocol.WaHeader.ToArray());
    }

    /// <summary>
    /// Creates a handshake with a fresh NSec X25519 ephemeral key and the PERSISTED
    /// static key (Phase 3 resume): the same static identity across restarts, so the
    /// server recognizes the session instead of demanding a re-pair (CONN-03).
    /// </summary>
    /// <param name="payloads">Opaque payload provider (Phase 3 supplies semantics).</param>
    /// <param name="staticPrivateKey">
    /// The persisted 32-byte X25519 static private key (a credential-row copy — imported
    /// into libsodium secure memory, then zeroized here so the caller keeps no live copy).
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="payloads"/> or <paramref name="staticPrivateKey"/> is null.</exception>
    /// <exception cref="SdkConnectionException">The static key is not a usable 32-byte key (message names the rule only).</exception>
    internal NoiseHandshake(INoiseHandshakePayloads payloads, byte[] staticPrivateKey)
    {
        _payloads = payloads ?? throw new ArgumentNullException(nameof(payloads));
        ArgumentNullException.ThrowIfNull(staticPrivateKey);
        if (staticPrivateKey.Length != PublicKeySize)
        {
            throw new SdkConnectionException("Handshake static keys must be 32 bytes.");
        }

        KeyCreationParameters creationParameters = default;
        _ephemeral = Key.Create(KeyAgreementAlgorithm.X25519, creationParameters);
        try
        {
            _static = Key.Import(KeyAgreementAlgorithm.X25519, staticPrivateKey, KeyBlobFormat.RawPrivateKey, creationParameters);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ephemeral.Dispose();
            throw new SdkConnectionException("Handshake static keys must be usable private keys.", ex);
        }

        CryptographicOperations.ZeroMemory(staticPrivateKey);
        _ephemeralPublic = _ephemeral.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        _staticPublic = _static.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        _state = new NoiseSymmetricState(PaddedProtocolName(), NoiseProtocol.WaHeader.ToArray());
    }

    /// <summary>
    /// Test/vector seam: fixed static + ephemeral private keys and an explicit
    /// prologue (the vector prologue under test, the WA header in production).
    /// Invisible to the public surface.
    /// </summary>
    internal NoiseHandshake(
        INoiseHandshakePayloads payloads,
        byte[] staticPrivateKey,
        byte[] ephemeralPrivateKey,
        byte[] prologue)
    {
        _payloads = payloads ?? throw new ArgumentNullException(nameof(payloads));
        ArgumentNullException.ThrowIfNull(staticPrivateKey);
        ArgumentNullException.ThrowIfNull(ephemeralPrivateKey);
        ArgumentNullException.ThrowIfNull(prologue);
        if (staticPrivateKey.Length != PublicKeySize || ephemeralPrivateKey.Length != PublicKeySize)
        {
            throw new ArgumentException("Fixed handshake keys must be 32 bytes each.");
        }

        KeyCreationParameters creationParameters = default;
        _static = Key.Import(KeyAgreementAlgorithm.X25519, staticPrivateKey, KeyBlobFormat.RawPrivateKey, creationParameters);
        try
        {
            _ephemeral = Key.Import(KeyAgreementAlgorithm.X25519, ephemeralPrivateKey, KeyBlobFormat.RawPrivateKey, creationParameters);
        }
        catch
        {
            _static.Dispose();
            throw;
        }

        CryptographicOperations.ZeroMemory(staticPrivateKey);
        CryptographicOperations.ZeroMemory(ephemeralPrivateKey);
        _ephemeralPublic = _ephemeral.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        _staticPublic = _static.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        _state = new NoiseSymmetricState(PaddedProtocolName(), prologue);
    }

    /// <summary>
    /// Writes bare <c>HandshakeMessage{client_hello}</c>; the connection sends the
    /// WA intro once and wraps this body in the shared three-byte frame.
    /// Exactly one call per instance — the ephemeral is never reused.
    /// </summary>
    /// <returns>The bare <c>HandshakeMessage{client_hello}</c> envelope bytes (unframed).</returns>
    /// <exception cref="ObjectDisposedException">The handshake is disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// Message A was already written, or the payload provider returned no data.
    /// </exception>
    /// <exception cref="SdkConnectionException">The hello blob exceeds the size limit.</exception>
    public byte[] WriteMessageA()
    {
        ThrowIfDisposed();
        if (_step != 0)
        {
            throw new InvalidOperationException("Handshake message A was already written.");
        }

        byte[] hello = _payloads.GetClientHello()
            ?? throw new InvalidOperationException("Handshake payload provider returned no data.");
        if (hello.Length > MaxHandshakePayloadBytes)
        {
            throw new SdkConnectionException("Handshake payload exceeds the size limit.");
        }

        byte[] raw = WriteRawMessage1(hello);
        byte[] ephemeral = new byte[PublicKeySize];
        byte[] payload = new byte[raw.Length - PublicKeySize];
        raw.AsSpan(0, PublicKeySize).CopyTo(ephemeral);
        raw.AsSpan(PublicKeySize).CopyTo(payload);
        CryptographicOperations.ZeroMemory(raw);

        var envelope = new HandshakeMessage
        {
            ClientHello = new ClientHello
            {
                Ephemeral = ByteString.CopyFrom(ephemeral),
                Payload = ByteString.CopyFrom(payload),
            },
        };
        CryptographicOperations.ZeroMemory(ephemeral);
        CryptographicOperations.ZeroMemory(payload);

        return envelope.ToByteArray();
    }

    /// <summary>
    /// Reads message B, validates it fail-closed, runs the responder-side DHs
    /// (<c>ee</c>, then decrypt static, then <c>es</c>), splits the transport
    /// keys, and mints the post-handshake <see cref="NoiseTransport"/>
    /// (ownership transfers to the caller). The peer's opaque blob is
    /// authenticated, length-validated, and discarded — its semantics belong
    /// to Phase 3.
    /// </summary>
    /// <param name="message">The message-B frame bytes (untrusted peer input).</param>
    /// <returns>The transport, with this side's write/read keys installed.</returns>
    /// <exception cref="ObjectDisposedException">The handshake is disposed.</exception>
    /// <exception cref="InvalidOperationException">Message A was not written first.</exception>
    /// <exception cref="SdkConnectionException">
    /// Framing is inconsistent, the header or dictionary version mismatches
    /// (fail-closed, T-2-09), a required envelope or key field is missing or
    /// missized, authentication failed, or key agreement failed. Messages name
    /// the rule only — never peer bytes or key material.
    /// </exception>
    public NoiseTransport ReadMessageB(ReadOnlyMemory<byte> message)
    {
        ThrowIfDisposed();
        if (_step != 1)
        {
            throw new InvalidOperationException("Handshake message B requires message A first.");
        }

        byte[] raw = ParseMessageB(message);
        (byte[] sendKey, byte[] receiveKey, byte[] payload) = CompleteRawMessage2(raw);
        CryptographicOperations.ZeroMemory(raw);
        CryptographicOperations.ZeroMemory(payload);
        try
        {
            return new NoiseTransport(sendKey, receiveKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sendKey);
            CryptographicOperations.ZeroMemory(receiveKey);
        }
    }

    /// <summary>
    /// Writes message C: <c>HandshakeMessage{client_finish}</c> carrying the
    /// encrypted static key and the encrypted opaque finish blob (XX:
    /// <c>->s, se</c> then payload — everything encrypted now).
    /// </summary>
    /// <returns>The message-C frame bytes.</returns>
    /// <exception cref="ObjectDisposedException">The handshake is disposed.</exception>
    /// <exception cref="InvalidOperationException">Message B was not processed first.</exception>
    /// <exception cref="SdkConnectionException">The finish blob exceeds the size limit.</exception>
    public byte[] WriteMessageC()
    {
        ThrowIfDisposed();
        if (_step != 2)
        {
            throw new InvalidOperationException("Handshake message C requires message B first.");
        }

        byte[] finish = _payloads.GetClientFinish()
            ?? throw new InvalidOperationException("Handshake payload provider returned no data.");
        if (finish.Length > MaxHandshakePayloadBytes)
        {
            throw new SdkConnectionException("Handshake payload exceeds the size limit.");
        }

        byte[] raw = WriteRawMessage3(finish);
        byte[] encStatic = new byte[EncryptedStaticSize];
        byte[] encPayload = new byte[raw.Length - EncryptedStaticSize];
        raw.AsSpan(0, EncryptedStaticSize).CopyTo(encStatic);
        raw.AsSpan(EncryptedStaticSize).CopyTo(encPayload);
        CryptographicOperations.ZeroMemory(raw);

        var envelope = new HandshakeMessage
        {
            ClientFinish = new ClientFinish
            {
                StaticKey = ByteString.CopyFrom(encStatic),
                Payload = ByteString.CopyFrom(encPayload),
            },
        };
        CryptographicOperations.ZeroMemory(encStatic);
        CryptographicOperations.ZeroMemory(encPayload);
        return envelope.ToByteArray();
    }

    /// <summary>
    /// Raw XX message 1 (initiator): authenticate the ephemeral and the
    /// payload (no key exists yet — plaintext on the wire).
    /// </summary>
    /// <returns><c>ephemeral(32) || payload</c>, freshly allocated.</returns>
    internal byte[] WriteRawMessage1(byte[] payload)
    {
        ThrowIfDisposed();
        if (_step != 0)
        {
            throw new InvalidOperationException("Handshake message 1 requires a fresh handshake.");
        }

        _state.MixHash(_ephemeralPublic);
        _state.MixHash(payload);
        _step = 1;

        byte[] raw = new byte[PublicKeySize + payload.Length];
        _ephemeralPublic.CopyTo(raw.AsSpan(0, PublicKeySize));
        payload.CopyTo(raw.AsSpan(PublicKeySize));
        return raw;
    }

    /// <summary>
    /// Raw XX message 2 processing (initiator): authenticate the responder
    /// ephemeral, mix <c>ee</c>, decrypt the responder static, mix <c>es</c>,
    /// decrypt the payload, then split. Returns this side's transport keys
    /// (send = k1, receive = k2) plus the recovered payload.
    /// </summary>
    /// <returns>
    /// Freshly allocated (send, receive, payload) copies owned by the caller.
    /// </returns>
    internal (byte[] SendKey, byte[] ReceiveKey, byte[] Payload) CompleteRawMessage2(byte[] raw)
    {
        ThrowIfDisposed();
        if (_step != 1)
        {
            throw new InvalidOperationException("Handshake message 2 requires message 1 first.");
        }

        if (raw.Length < PublicKeySize + EncryptedStaticSize + NoiseSymmetricState.TagLength)
        {
            throw new SdkConnectionException("Handshake message B failed validation: inconsistent framing.");
        }

        byte[] responderEphemeral = raw.AsSpan(0, PublicKeySize).ToArray();
        byte[] encStatic = raw.AsSpan(PublicKeySize, EncryptedStaticSize).ToArray();
        byte[] encPayload = raw.AsSpan(PublicKeySize + EncryptedStaticSize).ToArray();
        CryptographicOperations.ZeroMemory(raw);

        _state.MixHash(responderEphemeral);
        byte[] ee = NoiseSymmetricState.DhAgree(_ephemeral, responderEphemeral);
        _state.MixKey(ee);
        CryptographicOperations.ZeroMemory(ee);

        byte[] responderStatic;
        try
        {
            responderStatic = _state.DecryptAndHash(encStatic);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encStatic);
        }

        if (responderStatic.Length != PublicKeySize)
        {
            CryptographicOperations.ZeroMemory(responderStatic);
            CryptographicOperations.ZeroMemory(responderEphemeral);
            throw new SdkConnectionException("Handshake message B failed validation: unusable peer key.");
        }

        byte[] es = NoiseSymmetricState.DhAgree(_ephemeral, responderStatic);
        _state.MixKey(es);
        CryptographicOperations.ZeroMemory(es);
        CryptographicOperations.ZeroMemory(responderStatic);

        byte[] payload;
        try
        {
            payload = _state.DecryptAndHash(encPayload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encPayload);
        }

        _responderEphemeral = responderEphemeral;

        // The se inputs (own static + responder ephemeral) are both known, so
        // the post-se chain — the spec Split point both parties must share —
        // is determined now. Preview it without moving the running state: the
        // real se mix happens in message 3 per the token order.
        byte[] sePreview = NoiseSymmetricState.DhAgree(_static, responderEphemeral);
        (byte[] k1, byte[] k2) = _state.PreviewSplit(sePreview);
        CryptographicOperations.ZeroMemory(sePreview);

        _step = 2;
        return (k1, k2, payload);
    }

    /// <summary>
    /// Raw XX message 3 (initiator): encrypt the static, mix <c>se</c>,
    /// encrypt the payload.
    /// </summary>
    /// <returns><c>enc_static(48) || enc_payload</c>, freshly allocated.</returns>
    internal byte[] WriteRawMessage3(byte[] payload)
    {
        ThrowIfDisposed();
        if (_step != 2 || _responderEphemeral is null)
        {
            throw new InvalidOperationException("Handshake message C requires message B first.");
        }

        byte[] encStatic = _state.EncryptAndHash(_staticPublic);
        byte[] se = NoiseSymmetricState.DhAgree(_static, _responderEphemeral);
        _state.MixKey(se);
        CryptographicOperations.ZeroMemory(se);
        byte[] encPayload = _state.EncryptAndHash(payload);
        _step = 3;

        byte[] raw = new byte[encStatic.Length + encPayload.Length];
        encStatic.CopyTo(raw.AsSpan(0, encStatic.Length));
        encPayload.CopyTo(raw.AsSpan(encStatic.Length));
        CryptographicOperations.ZeroMemory(encStatic);
        CryptographicOperations.ZeroMemory(encPayload);
        return raw;
    }

    /// <summary>
    /// Runs the real spec <see cref="NoiseSymmetricState.Split"/> after message
    /// 3 completed the token order. Test/vector use only: proves the early
    /// preview keys from <see cref="CompleteRawMessage2"/> converge with the
    /// token-ordered split (both derive from the identical post-<c>se</c>
    /// chaining key). One-shot.
    /// </summary>
    /// <returns>Freshly allocated (send = k1, receive = k2) copies.</returns>
    /// <exception cref="InvalidOperationException">Message C was not written yet.</exception>
    internal (byte[] SendKey, byte[] ReceiveKey) SplitTransportKeys()
    {
        ThrowIfDisposed();
        if (_step != 3)
        {
            throw new InvalidOperationException("Transport keys split only after message C.");
        }

        return _state.Split();
    }

    /// <summary>
    /// The final transcript hash (both parties must agree). Available after
    /// message C completes the exchange; wiped on <see cref="Dispose"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Message C was not processed yet.</exception>
    /// <exception cref="ObjectDisposedException">The handshake is disposed.</exception>
    internal byte[] HandshakeHash
    {
        get
        {
            ThrowIfDisposed();
            if (_step != 3)
            {
                throw new InvalidOperationException("The handshake hash is available only after message C.");
            }

            return _state.CurrentHash;
        }
    }

    /// <summary>
    /// Proves abort hygiene: true only once <see cref="Dispose"/> wiped every
    /// managed secret buffer (ephemeral/static public copies, responder copy,
    /// transcript hash, and the symmetric state). NSec keys are disposed into
    /// libsodium secure memory by <see cref="Dispose"/>.
    /// </summary>
    internal bool IsKeyMaterialZeroized
    {
        get
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                return false;
            }

            return IsZero(_ephemeralPublic)
                && IsZero(_staticPublic)
                && (_responderEphemeral is null || IsZero(_responderEphemeral))
                && _state.IsWiped;
        }
    }

    /// <summary>
    /// The 32-byte null-padded protocol name: the 28-char ASCII suite name plus
    /// four <c>0x00</c> bytes (F1 construction).
    /// </summary>
    internal static byte[] PaddedProtocolName()
    {
        byte[] padded = new byte[NoiseProtocol.PaddedProtocolNameLength];
        "Noise_XX_25519_AESGCM_SHA256"u8.CopyTo(padded.AsSpan(0, 28));
        return padded;
    }

    internal static void ValidateIntroHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length != 4)
        {
            throw new SdkConnectionException("Unexpected protocol header.");
        }

        if (header[0] != 0x57 || header[1] != 0x41 || header[2] != 0x06)
        {
            throw new SdkConnectionException("Unexpected protocol header.");
        }

        if (header[3] != NoiseProtocol.DictVersion)
        {
            throw new SdkConnectionException("dictionary version mismatch.");
        }
    }

    private static byte[] ParseMessageB(ReadOnlyMemory<byte> message)
    {
        ReadOnlySpan<byte> span = message.Span;
        if (span.Length >= 4
            && span[0] == 0x57 && span[1] == 0x41 && span[2] == 0x06
            && span[3] != NoiseProtocol.DictVersion)
        {
            throw new SdkConnectionException("Handshake message B failed validation: dictionary version mismatch.");
        }

        if (span.Length < 1)
        {
            throw new SdkConnectionException("Handshake message B failed validation: inconsistent framing.");
        }

        HandshakeMessage envelope;
        try
        {
            envelope = HandshakeMessage.Parser.ParseFrom(span.ToArray());
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new SdkConnectionException("Handshake message B failed validation: inconsistent framing.", ex);
        }

        ServerHello? hello = envelope.ServerHello;
        if (hello is null
            || hello.Ephemeral.Length != PublicKeySize
            || hello.StaticKey.Length != EncryptedStaticSize
            || hello.Payload.Length == 0
            || hello.Payload.Length > MaxHandshakePayloadBytes + NoiseSymmetricState.TagLength)
        {
            throw new SdkConnectionException("Handshake message B failed validation: inconsistent framing.");
        }

        byte[] raw = new byte[PublicKeySize + EncryptedStaticSize + hello.Payload.Length];
        hello.Ephemeral.ToByteArray().CopyTo(raw.AsSpan(0, PublicKeySize));
        hello.StaticKey.ToByteArray().CopyTo(raw.AsSpan(PublicKeySize, EncryptedStaticSize));
        hello.Payload.ToByteArray().CopyTo(raw.AsSpan(PublicKeySize + EncryptedStaticSize));
        return raw;
    }

    private static bool IsZero(byte[] buffer)
    {
        foreach (byte b in buffer)
        {
            if (b != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Disposes the ephemeral and static keys (libsodium secure memory) and
    /// zeroizes every managed secret copy: public-key copies, the responder
    /// ephemeral, and the full symmetric state (including the live transcript
    /// hash). Transports minted by <see cref="ReadMessageB"/> are separately
    /// owned and are not affected. Disposing twice is a no-op.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _state.Dispose();
        _ephemeral.Dispose();
        _static.Dispose();
        CryptographicOperations.ZeroMemory(_ephemeralPublic);
        CryptographicOperations.ZeroMemory(_staticPublic);
        if (_responderEphemeral is not null)
        {
            CryptographicOperations.ZeroMemory(_responderEphemeral);
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new ObjectDisposedException(nameof(NoiseHandshake));
        }
    }
}

/// <summary>
/// Noise XX handshake, responder role (internal, 02-03): the loopback peer and
/// the vector tests' second party. Mirrors <see cref="NoiseHandshake"/> with
/// the responder token processing (<c>e, ee / s, es</c> on message 2,
/// <c>se</c> on message 3) and mirrored split keys (send = k2, receive = k1).
/// </summary>
/// <remarks>
/// One instance per handshake attempt; the server-hello blob is copied in and
/// never retained by reference. Disposal zeroes everything, exactly like the
/// initiator. Invisible to the public surface — no PublicAPI entries.
/// </remarks>
internal sealed class NoiseHandshakeResponder : IDisposable
{
    private readonly Key _ephemeral;
    private readonly Key _static;
    private readonly byte[] _ephemeralPublic;
    private readonly byte[] _staticPublic;
    private readonly NoiseSymmetricState _state;
    private readonly byte[] _serverHello;
    private byte[]? _initiatorEphemeral;
    private byte[]? _initiatorStaticPublic;
    private int _step;
    private int _disposed;

    /// <summary>Creates a responder with fresh keys and a copied hello blob.</summary>
    /// <param name="serverHello">Opaque server-hello blob (copied).</param>
    internal NoiseHandshakeResponder(byte[] serverHello)
    {
        ArgumentNullException.ThrowIfNull(serverHello);
        if (serverHello.Length > NoiseHandshake.MaxHandshakePayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(serverHello), "Server hello exceeds the size limit.");
        }

        KeyCreationParameters creationParameters = default;
        _ephemeral = Key.Create(KeyAgreementAlgorithm.X25519, creationParameters);
        try
        {
            _static = Key.Create(KeyAgreementAlgorithm.X25519, creationParameters);
        }
        catch
        {
            _ephemeral.Dispose();
            throw;
        }

        _ephemeralPublic = _ephemeral.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        _staticPublic = _static.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        _serverHello = (byte[])serverHello.Clone();
        _state = new NoiseSymmetricState(NoiseHandshake.PaddedProtocolName(), NoiseProtocol.WaHeader.ToArray());
    }

    /// <summary>
    /// Test/vector seam: fixed static + ephemeral private keys and an explicit
    /// prologue. Invisible to the public surface.
    /// </summary>
    internal NoiseHandshakeResponder(
        byte[] serverHello,
        byte[] staticPrivateKey,
        byte[] ephemeralPrivateKey,
        byte[] prologue)
    {
        ArgumentNullException.ThrowIfNull(serverHello);
        ArgumentNullException.ThrowIfNull(staticPrivateKey);
        ArgumentNullException.ThrowIfNull(ephemeralPrivateKey);
        ArgumentNullException.ThrowIfNull(prologue);
        if (serverHello.Length > NoiseHandshake.MaxHandshakePayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(serverHello), "Server hello exceeds the size limit.");
        }

        if (staticPrivateKey.Length != NoiseHandshake.PublicKeySize
            || ephemeralPrivateKey.Length != NoiseHandshake.PublicKeySize)
        {
            throw new ArgumentException("Fixed handshake keys must be 32 bytes each.");
        }

        KeyCreationParameters creationParameters = default;
        _static = Key.Import(KeyAgreementAlgorithm.X25519, staticPrivateKey, KeyBlobFormat.RawPrivateKey, creationParameters);
        try
        {
            _ephemeral = Key.Import(KeyAgreementAlgorithm.X25519, ephemeralPrivateKey, KeyBlobFormat.RawPrivateKey, creationParameters);
        }
        catch
        {
            _static.Dispose();
            throw;
        }

        CryptographicOperations.ZeroMemory(staticPrivateKey);
        CryptographicOperations.ZeroMemory(ephemeralPrivateKey);
        _ephemeralPublic = _ephemeral.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        _staticPublic = _static.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        _serverHello = (byte[])serverHello.Clone();
        _state = new NoiseSymmetricState(NoiseHandshake.PaddedProtocolName(), prologue);
    }

    /// <summary>
    /// Reads bare <c>HandshakeMessage{client_hello}</c>, validates it fail-closed,
    /// and processes the initiator ephemeral. The intro shape and dictionary version
    /// are validated by the connection edge (<see cref="NoiseHandshake.ValidateIntroHeader"/>), not here.
    /// </summary>
    /// <exception cref="SdkConnectionException">
    /// Framing is inconsistent or a required field is missing or missized. Messages
    /// name the rule only.
    /// </exception>
    internal void ReadMessageA(ReadOnlyMemory<byte> message)
    {
        ThrowIfDisposed();
        if (_step != 0)
        {
            throw new InvalidOperationException("Handshake message A requires a fresh handshake.");
        }

        byte[] raw = ParseMessageA(message);
        byte[] hello = ReadRawMessage1(raw);
        CryptographicOperations.ZeroMemory(raw);
        CryptographicOperations.ZeroMemory(hello);
    }

    /// <summary>
    /// Writes bare <c>HandshakeMessage{server_hello}</c>; the connection wraps this
    /// body in the shared three-byte frame after the intro has been sent once. The
    /// envelope carries the fresh ephemeral in the clear, the encrypted static, and
    /// the encrypted hello.
    /// </summary>
    internal byte[] WriteMessageB()
    {
        ThrowIfDisposed();
        if (_step != 1)
        {
            throw new InvalidOperationException("Handshake message B requires message A first.");
        }

        byte[] raw = WriteRawMessage2(_serverHello);
        byte[] ephemeral = new byte[NoiseHandshake.PublicKeySize];
        byte[] encStatic = new byte[NoiseHandshake.EncryptedStaticSize];
        byte[] encPayload = new byte[raw.Length - NoiseHandshake.PublicKeySize - NoiseHandshake.EncryptedStaticSize];
        raw.AsSpan(0, NoiseHandshake.PublicKeySize).CopyTo(ephemeral);
        raw.AsSpan(NoiseHandshake.PublicKeySize, NoiseHandshake.EncryptedStaticSize).CopyTo(encStatic);
        raw.AsSpan(NoiseHandshake.PublicKeySize + NoiseHandshake.EncryptedStaticSize).CopyTo(encPayload);
        CryptographicOperations.ZeroMemory(raw);

        var envelope = new HandshakeMessage
        {
            ServerHello = new ServerHello
            {
                Ephemeral = ByteString.CopyFrom(ephemeral),
                StaticKey = ByteString.CopyFrom(encStatic),
                Payload = ByteString.CopyFrom(encPayload),
            },
        };
        CryptographicOperations.ZeroMemory(ephemeral);
        CryptographicOperations.ZeroMemory(encStatic);
        CryptographicOperations.ZeroMemory(encPayload);

        return envelope.ToByteArray();
    }

    /// <summary>
    /// Reads message C (<c>HandshakeMessage{client_finish}</c>), decrypts the
    /// initiator static, mixes <c>se</c>, decrypts the finish blob
    /// (discarded), splits, and mints this side's <see cref="NoiseTransport"/>
    /// (send = k2, receive = k1 — mirrored from the initiator).
    /// </summary>
    /// <returns>The transport, with this side's write/read keys installed.</returns>
    internal NoiseTransport ReadMessageC(ReadOnlyMemory<byte> message)
    {
        ThrowIfDisposed();
        if (_step != 2)
        {
            throw new InvalidOperationException("Handshake message C requires message B first.");
        }

        byte[] raw = ParseMessageC(message);
        (byte[] sendKey, byte[] receiveKey, byte[] payload) = CompleteRawMessage3(raw);
        CryptographicOperations.ZeroMemory(raw);
        CryptographicOperations.ZeroMemory(payload);
        try
        {
            return new NoiseTransport(sendKey, receiveKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sendKey);
            CryptographicOperations.ZeroMemory(receiveKey);
        }
    }

    /// <summary>
    /// Raw XX message 1 processing (responder): authenticate the initiator
    /// ephemeral and hello (plaintext — no key exists yet).
    /// </summary>
    /// <returns>The recovered hello payload (fresh copy owned by the caller).</returns>
    internal byte[] ReadRawMessage1(byte[] raw)
    {
        ThrowIfDisposed();
        if (_step != 0)
        {
            throw new InvalidOperationException("Handshake message 1 requires a fresh handshake.");
        }

        if (raw.Length < NoiseHandshake.PublicKeySize
            || raw.Length - NoiseHandshake.PublicKeySize > NoiseHandshake.MaxHandshakePayloadBytes)
        {
            throw new SdkConnectionException("Handshake message A failed validation: inconsistent framing.");
        }

        _initiatorEphemeral = raw.AsSpan(0, NoiseHandshake.PublicKeySize).ToArray();
        byte[] hello = raw.AsSpan(NoiseHandshake.PublicKeySize).ToArray();
        _state.MixHash(_initiatorEphemeral);
        _state.MixHash(hello);
        _step = 1;
        return hello;
    }

    /// <summary>
    /// Raw XX message 2 (responder): authenticate the fresh ephemeral, mix
    /// <c>ee</c>, encrypt the static, mix <c>es</c>, encrypt the payload.
    /// </summary>
    /// <returns>
    /// <c>ephemeral(32) || enc_static(48) || enc_payload</c>, freshly allocated.
    /// </returns>
    internal byte[] WriteRawMessage2(byte[] serverHello)
    {
        ThrowIfDisposed();
        if (_step != 1 || _initiatorEphemeral is null)
        {
            throw new InvalidOperationException("Handshake message 2 requires message 1 first.");
        }

        if (serverHello.Length > NoiseHandshake.MaxHandshakePayloadBytes)
        {
            throw new SdkConnectionException("Handshake payload exceeds the size limit.");
        }

        _state.MixHash(_ephemeralPublic);
        byte[] ee = NoiseSymmetricState.DhAgree(_ephemeral, _initiatorEphemeral);
        _state.MixKey(ee);
        CryptographicOperations.ZeroMemory(ee);

        byte[] encStatic = _state.EncryptAndHash(_staticPublic);
        byte[] es = NoiseSymmetricState.DhAgree(_static, _initiatorEphemeral);
        _state.MixKey(es);
        CryptographicOperations.ZeroMemory(es);

        byte[] encPayload = _state.EncryptAndHash(serverHello);
        _step = 2;

        byte[] raw = new byte[NoiseHandshake.PublicKeySize + encStatic.Length + encPayload.Length];
        _ephemeralPublic.CopyTo(raw.AsSpan(0, NoiseHandshake.PublicKeySize));
        encStatic.CopyTo(raw.AsSpan(NoiseHandshake.PublicKeySize));
        encPayload.CopyTo(raw.AsSpan(NoiseHandshake.PublicKeySize + encStatic.Length));
        CryptographicOperations.ZeroMemory(encStatic);
        CryptographicOperations.ZeroMemory(encPayload);
        return raw;
    }

    /// <summary>
    /// Raw XX message 3 processing (responder): decrypt the initiator static,
    /// mix <c>se</c>, decrypt the finish blob, then split.
    /// Returns this side's transport keys (send = k2, receive = k1) plus the
    /// recovered payload.
    /// </summary>
    /// <returns>
    /// Freshly allocated (send, receive, payload) copies owned by the caller.
    /// </returns>
    internal (byte[] SendKey, byte[] ReceiveKey, byte[] Payload) CompleteRawMessage3(byte[] raw)
    {
        ThrowIfDisposed();
        if (_step != 2 || _initiatorEphemeral is null)
        {
            throw new InvalidOperationException("Handshake message 3 requires message 2 first.");
        }

        if (raw.Length < NoiseHandshake.EncryptedStaticSize + NoiseSymmetricState.TagLength)
        {
            throw new SdkConnectionException("Handshake message C failed validation: inconsistent framing.");
        }

        byte[] encStatic = raw.AsSpan(0, NoiseHandshake.EncryptedStaticSize).ToArray();
        byte[] encPayload = raw.AsSpan(NoiseHandshake.EncryptedStaticSize).ToArray();
        CryptographicOperations.ZeroMemory(raw);

        byte[] initiatorStatic;
        try
        {
            initiatorStatic = _state.DecryptAndHash(encStatic);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encStatic);
        }

        if (initiatorStatic.Length != NoiseHandshake.PublicKeySize)
        {
            CryptographicOperations.ZeroMemory(initiatorStatic);
            throw new SdkConnectionException("Handshake message C failed validation: unusable peer key.");
        }

        byte[] se = NoiseSymmetricState.DhAgree(_ephemeral, initiatorStatic);
        _state.MixKey(se);
        CryptographicOperations.ZeroMemory(se);

        // Tracer observability (03-01): the resume proof asserts server-side that the
        // initiator encrypted its PERSISTED static key into message C. The decrypted
        // public is stashed (private copy) for the loopback peer to read — production
        // never consults it, and disposal zeroizes it with the rest.
        _initiatorStaticPublic = (byte[])initiatorStatic.Clone();
        CryptographicOperations.ZeroMemory(initiatorStatic);

        byte[] payload;
        try
        {
            payload = _state.DecryptAndHash(encPayload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encPayload);
        }

        (byte[] k1, byte[] k2) = _state.Split();
        _step = 3;
        return (k2, k1, payload);
    }

    /// <summary>
    /// The initiator's decrypted static public key from message C (tracer
    /// observability for the resume proof: the loopback peer asserts it equals the
    /// seeded credential row's static identity). Available after message C is
    /// processed; wiped on <see cref="Dispose"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Message C was not processed yet.</exception>
    /// <exception cref="ObjectDisposedException">The responder is disposed.</exception>
    internal byte[]? InitiatorStaticPublic
    {
        get
        {
            ThrowIfDisposed();
            if (_step != 3 || _initiatorStaticPublic is null)
            {
                throw new InvalidOperationException("The initiator static key is available only after message C.");
            }

            return (byte[])_initiatorStaticPublic.Clone();
        }
    }

    /// <summary>
    /// The final transcript hash (both parties must agree). Available after
    /// <see cref="ReadMessageC"/> completes the exchange; wiped on
    /// <see cref="Dispose"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Message C was not processed yet.</exception>
    /// <exception cref="ObjectDisposedException">The handshake is disposed.</exception>
    internal byte[] HandshakeHash
    {
        get
        {
            ThrowIfDisposed();
            if (_step != 3)
            {
                throw new InvalidOperationException("The handshake hash is available only after message C.");
            }

            return _state.CurrentHash;
        }
    }

    /// <summary>
    /// Proves abort hygiene: true only once <see cref="Dispose"/> wiped every
    /// managed secret buffer. See <see cref="NoiseHandshake.IsKeyMaterialZeroized"/>.
    /// </summary>
    internal bool IsKeyMaterialZeroized
    {
        get
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                return false;
            }

            return IsZero(_ephemeralPublic)
                && IsZero(_staticPublic)
                && IsZero(_serverHello)
                && (_initiatorEphemeral is null || IsZero(_initiatorEphemeral))
                && (_initiatorStaticPublic is null || IsZero(_initiatorStaticPublic))
                && _state.IsWiped;
        }
    }

    private static byte[] ParseMessageA(ReadOnlyMemory<byte> message)
    {
        ReadOnlySpan<byte> span = message.Span;
        if (span.Length < 1)
        {
            throw new SdkConnectionException("Handshake message A failed validation: inconsistent framing.");
        }

        HandshakeMessage envelope;
        try
        {
            envelope = HandshakeMessage.Parser.ParseFrom(span.ToArray());
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new SdkConnectionException("Handshake message A failed validation: inconsistent framing.", ex);
        }

        ClientHello? hello = envelope.ClientHello;
        if (hello is null
            || hello.Ephemeral.Length != NoiseHandshake.PublicKeySize
            || hello.Payload.Length > NoiseHandshake.MaxHandshakePayloadBytes)
        {
            throw new SdkConnectionException("Handshake message A failed validation: inconsistent framing.");
        }

        byte[] raw = new byte[NoiseHandshake.PublicKeySize + hello.Payload.Length];
        hello.Ephemeral.ToByteArray().CopyTo(raw.AsSpan(0, NoiseHandshake.PublicKeySize));
        hello.Payload.ToByteArray().CopyTo(raw.AsSpan(NoiseHandshake.PublicKeySize));
        return raw;
    }

    private static byte[] ParseMessageC(ReadOnlyMemory<byte> message)
    {
        HandshakeMessage envelope;
        try
        {
            envelope = HandshakeMessage.Parser.ParseFrom(message.ToArray());
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new SdkConnectionException("Handshake message C failed validation: inconsistent framing.", ex);
        }

        ClientFinish? finish = envelope.ClientFinish;
        if (finish is null
            || finish.StaticKey.Length != NoiseHandshake.EncryptedStaticSize
            || finish.Payload.Length == 0
            || finish.Payload.Length > NoiseHandshake.MaxHandshakePayloadBytes + NoiseSymmetricState.TagLength)
        {
            throw new SdkConnectionException("Handshake message C failed validation: inconsistent framing.");
        }

        byte[] raw = new byte[NoiseHandshake.EncryptedStaticSize + finish.Payload.Length];
        finish.StaticKey.ToByteArray().CopyTo(raw.AsSpan(0, NoiseHandshake.EncryptedStaticSize));
        finish.Payload.ToByteArray().CopyTo(raw.AsSpan(NoiseHandshake.EncryptedStaticSize));
        return raw;
    }

    private static bool IsZero(byte[] buffer)
    {
        foreach (byte b in buffer)
        {
            if (b != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Disposes the ephemeral and static keys and zeroizes every managed
    /// secret copy. Disposing twice is a no-op.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _state.Dispose();
        _ephemeral.Dispose();
        _static.Dispose();
        CryptographicOperations.ZeroMemory(_ephemeralPublic);
        CryptographicOperations.ZeroMemory(_staticPublic);
        CryptographicOperations.ZeroMemory(_serverHello);
        if (_initiatorEphemeral is not null)
        {
            CryptographicOperations.ZeroMemory(_initiatorEphemeral);
        }

        if (_initiatorStaticPublic is not null)
        {
            CryptographicOperations.ZeroMemory(_initiatorStaticPublic);
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new ObjectDisposedException(nameof(NoiseHandshakeResponder));
        }
    }
}
