using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
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
/// Noise XX handshake skeleton, initiator role (02-02): <c>WriteMessageA →
/// ReadMessageB → WriteMessageC</c> over the XX token flow with a real NSec X25519
/// ephemeral per instance and real BCL AES-GCM transport keys out of
/// <see cref="ReadMessageB"/>.
/// </summary>
/// <remarks>
/// <para>
/// One instance per handshake attempt — the ephemeral key is created in the constructor
/// and a second <c>WriteMessageA</c> is rejected, so an ephemeral is structurally never
/// reused across reconnects (Pitfall 4). The connection creates a fresh instance per
/// attempt and disposes it once message C is sent, zeroizing the ephemeral and all
/// intermediate key material (NSec keys live in libsodium secure memory and are
/// disposed; managed copies go through <see cref="CryptographicOperations.ZeroMemory"/>).
/// </para>
/// <para>
/// Skeleton wire shape (loopback-tracer only — 02-03 replaces these bytes with the
/// F2 <c>HandshakeMessage</c> protobuf, keeping these three methods as the surface):
/// message A and B are <c>WA-header(4) || ephemeral-pubkey(32) || length(u16 BE) ||
/// opaque-payload</c>; message C is <c>length(u16 BE) || opaque-payload</c>. Peer bytes
/// are untrusted: lengths are bounds-checked before any copy, the header (including the
/// <see cref="NoiseProtocol.DictVersion"/> byte) must match exactly, and any mismatch
/// aborts with a rule-naming <see cref="SdkConnectionException"/> — never with peer
/// bytes in the message (T-2-09; the loud log is the connection owner's job).
/// </para>
/// <para>
/// Skeleton KDF (02-03 replaces this with the spec MixKey/MixHash chain proven against
/// cacophony vectors): <c>transcript = SHA256(paddedName || header || initiatorEpub ||
/// responderEpub)</c>, then <c>HKDF-SHA256(ikm: sharedSecret, salt: transcript,
/// info: "wa-noise-skeleton-v1", 64 bytes)</c> split into initiator-write [0..32) and
/// initiator-read [32..64). The transcript binds the handshake to the WA prologue bytes
/// (F2/O2 spirit). <see cref="DeriveTransportKeys"/> is internal-static so the loopback
/// peer in tests derives the identical keys from the same inputs — no KDF duplication.
/// </para>
/// <para>
/// Ownership: <see cref="ReadMessageB"/> mints exactly one <see cref="NoiseTransport"/>
/// whose ownership transfers to the caller — disposing the handshake afterwards does not
/// touch the transport. State guards reject out-of-order calls with
/// <see cref="InvalidOperationException"/>.
/// </para>
/// </remarks>
public sealed class NoiseHandshake : IDisposable
{
    /// <summary>X25519 ephemeral public key size in bytes.</summary>
    internal const int EphemeralPublicKeySize = 32;

    /// <summary>
    /// Maximum accepted opaque handshake payload in bytes (32 KiB — handshake blobs are
    /// tiny; anything larger is garbage and fails closed). Fits the u16 length field with
    /// headroom.
    /// </summary>
    internal const int MaxHandshakePayloadBytes = 32 * 1024;

    private static readonly byte[] KeyDerivationInfo = "wa-noise-skeleton-v1"u8.ToArray();

    private readonly INoiseHandshakePayloads _payloads;
    private readonly Key _ephemeral;
    private readonly byte[] _ephemeralPublic;
    private int _step;
    private int _disposed;

    /// <summary>
    /// Creates a handshake with a fresh NSec X25519 ephemeral key.
    /// </summary>
    /// <param name="payloads">Opaque payload provider (Phase 3 supplies semantics).</param>
    /// <exception cref="ArgumentNullException"><paramref name="payloads"/> is null.</exception>
    public NoiseHandshake(INoiseHandshakePayloads payloads)
    {
        _payloads = payloads ?? throw new ArgumentNullException(nameof(payloads));
        KeyCreationParameters creationParameters = default;
        _ephemeral = Key.Create(KeyAgreementAlgorithm.X25519, creationParameters);
        _ephemeralPublic = _ephemeral.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    /// <summary>
    /// Writes message A: <c>WA-header || ephemeral-pubkey || length || client-hello</c>.
    /// Exactly one call per instance — the ephemeral is never reused.
    /// </summary>
    /// <returns>The message-A frame bytes.</returns>
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

        _step = 1;
        var frame = new byte[4 + EphemeralPublicKeySize + 2 + hello.Length];
        NoiseProtocol.WaHeader.CopyTo(frame.AsSpan(0, 4));
        _ephemeralPublic.CopyTo(frame.AsSpan(4, EphemeralPublicKeySize));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4 + EphemeralPublicKeySize, 2), (ushort)hello.Length);
        hello.CopyTo(frame.AsSpan(4 + EphemeralPublicKeySize + 2));
        return frame;
    }

    /// <summary>
    /// Reads message B, validates it fail-closed, runs X25519 + the skeleton KDF, and
    /// mints the post-handshake <see cref="NoiseTransport"/> (ownership transfers to the
    /// caller). The peer's opaque blob is length-validated and discarded — its semantics
    /// belong to Phase 3.
    /// </summary>
    /// <param name="message">The message-B frame bytes (untrusted peer input).</param>
    /// <returns>The transport, with this side's write/read keys installed.</returns>
    /// <exception cref="ObjectDisposedException">The handshake is disposed.</exception>
    /// <exception cref="InvalidOperationException">Message A was not written first.</exception>
    /// <exception cref="SdkConnectionException">
    /// Framing is inconsistent, the header or dictionary version mismatches (fail-closed,
    /// T-2-09), the peer key is unusable, or key agreement failed. Messages name the rule
    /// only — never peer bytes.
    /// </exception>
    public NoiseTransport ReadMessageB(ReadOnlyMemory<byte> message)
    {
        ThrowIfDisposed();
        if (_step != 1)
        {
            throw new InvalidOperationException("Handshake message B requires message A first.");
        }

        ReadOnlySpan<byte> span = message.Span;
        const int prefixLength = 4 + EphemeralPublicKeySize + 2;
        if (span.Length < prefixLength)
        {
            throw new SdkConnectionException("Handshake message B failed validation: inconsistent framing.");
        }

        if (!span.Slice(0, 4).SequenceEqual(NoiseProtocol.WaHeader))
        {
            if (span.Length >= 4
                && span[0] == 0x57 && span[1] == 0x41 && span[2] == 0x06)
            {
                throw new SdkConnectionException("Handshake message B failed validation: dictionary version mismatch.");
            }

            throw new SdkConnectionException("Handshake message B failed validation: unexpected protocol header.");
        }

        int payloadLength = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(4 + EphemeralPublicKeySize, 2));
        if (payloadLength > MaxHandshakePayloadBytes || span.Length != prefixLength + payloadLength)
        {
            throw new SdkConnectionException("Handshake message B failed validation: inconsistent framing.");
        }

        PublicKey peerPublic;
        try
        {
            peerPublic = PublicKey.Import(
                KeyAgreementAlgorithm.X25519,
                span.Slice(4, EphemeralPublicKeySize),
                KeyBlobFormat.RawPublicKey);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SdkConnectionException("Handshake message B failed validation: unusable peer key.", ex);
        }

        byte[] peerEphemeral = span.Slice(4, EphemeralPublicKeySize).ToArray();
        byte[] sharedSecret;
        var agreeParameters = new SharedSecretCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        };
        try
        {
            using SharedSecret agreement = KeyAgreementAlgorithm.X25519.Agree(_ephemeral, peerPublic, agreeParameters)
                ?? throw new SdkConnectionException("Handshake key agreement failed.");
            sharedSecret = agreement.Export(SharedSecretBlobFormat.RawSharedSecret);
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

        (byte[] writeKey, byte[] readKey) = DeriveTransportKeys(sharedSecret, _ephemeralPublic, peerEphemeral);
        CryptographicOperations.ZeroMemory(sharedSecret);

        _step = 2;
        try
        {
            return new NoiseTransport(writeKey, readKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(writeKey);
            CryptographicOperations.ZeroMemory(readKey);
        }
    }

    /// <summary>
    /// Writes message C: <c>length || client-finish</c> (plaintext — the first AEAD traffic
    /// is the post-handshake query, which proves the fresh keys end-to-end).
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

        _step = 3;
        var frame = new byte[2 + finish.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), (ushort)finish.Length);
        finish.CopyTo(frame.AsSpan(2));
        return frame;
    }

    /// <summary>
    /// Skeleton KDF: binds the WA prologue transcript, then HKDF-SHA256 into the two
    /// direction keys (initiator-write first, initiator-read second).
    /// </summary>
    /// <param name="sharedSecret">The 32-byte X25519 shared secret. Never logged.</param>
    /// <param name="initiatorEphemeral">Initiator ephemeral public key (32 bytes).</param>
    /// <param name="responderEphemeral">Responder ephemeral public key (32 bytes).</param>
    /// <returns>Initiator write key and read key (32 bytes each).</returns>
    internal static (byte[] InitiatorWriteKey, byte[] InitiatorReadKey) DeriveTransportKeys(
        ReadOnlySpan<byte> sharedSecret,
        ReadOnlySpan<byte> initiatorEphemeral,
        ReadOnlySpan<byte> responderEphemeral)
    {
        var transcriptInput = new byte[
            NoiseProtocol.PaddedProtocolNameLength + 4 + EphemeralPublicKeySize + EphemeralPublicKeySize];
        Encoding.ASCII.GetBytes(
            NoiseProtocol.ProtocolName,
            transcriptInput.AsSpan(0, NoiseProtocol.ProtocolName.Length));
        NoiseProtocol.WaHeader.CopyTo(transcriptInput.AsSpan(NoiseProtocol.PaddedProtocolNameLength, 4));
        initiatorEphemeral.CopyTo(transcriptInput.AsSpan(NoiseProtocol.PaddedProtocolNameLength + 4, EphemeralPublicKeySize));
        responderEphemeral.CopyTo(transcriptInput.AsSpan(NoiseProtocol.PaddedProtocolNameLength + 4 + EphemeralPublicKeySize, EphemeralPublicKeySize));
        byte[] transcript = SHA256.HashData(transcriptInput);
        CryptographicOperations.ZeroMemory(transcriptInput);

        byte[] okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret.ToArray(), 2 * NoiseTransport.KeySize, transcript, KeyDerivationInfo);
        CryptographicOperations.ZeroMemory(transcript);
        var writeKey = new byte[NoiseTransport.KeySize];
        var readKey = new byte[NoiseTransport.KeySize];
        okm.AsSpan(0, NoiseTransport.KeySize).CopyTo(writeKey);
        okm.AsSpan(NoiseTransport.KeySize, NoiseTransport.KeySize).CopyTo(readKey);
        CryptographicOperations.ZeroMemory(okm);
        return (writeKey, readKey);
    }

    /// <summary>
    /// Disposes the ephemeral key (libsodium secure memory) and zeroizes the public-key
    /// copy. Transports minted by <see cref="ReadMessageB"/> are separately owned and
    /// are not affected. Disposing twice is a no-op.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _ephemeral.Dispose();
        CryptographicOperations.ZeroMemory(_ephemeralPublic);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new ObjectDisposedException(nameof(NoiseHandshake));
        }
    }
}
