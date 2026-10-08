using System.Buffers.Binary;
using System.Security.Cryptography;
using Iwasug.WhatsApp.Sdk.Errors;

namespace Iwasug.WhatsApp.Sdk.Noise;

/// <summary>
/// Post-handshake AEAD transport (skeleton, 02-02): encrypts outbound frames and
/// decrypts inbound frames with the two AES-GCM keys derived by
/// <see cref="NoiseHandshake"/> — one key per direction, never shared.
/// </summary>
/// <remarks>
/// <para>
/// Nonce discipline (02-SPIKE-NOTES.md O2): a 12-byte nonce, first 8 bytes zero, last
/// 4 bytes a big-endian counter. Send and receive counters are private instance fields
/// starting at 0, constructed fresh per handshake, never shared, never persisted, never
/// reused (Pitfall 4). Packets carry their nonce explicitly
/// (<c>nonce(12) || ciphertext || tag(16)</c>) and decryption enforces strict ordering —
/// a replayed or reordered packet fails closed instead of decrypting.
/// </para>
/// <para>
/// Skeleton scope: the associated-data input is the empty string (02-03 binds the Noise
/// running hash as AAD per O2) and keys arrive from the skeleton KDF
/// (<see cref="NoiseHandshake.DeriveTransportKeys"/>), which 02-03 replaces with the
/// spec MixKey chain proven against cacophony vectors. The
/// <c>Encrypt</c>/<c>Decrypt</c> method shapes are the approved surface — 02-03 hardens
/// the cryptography behind them without renaming them.
/// </para>
/// <para>
/// Single-owner disposable (InMemoryStoreFactory precedent): exactly one owner holds an
/// instance (the connection channel or the handshake handoff). <see cref="Dispose"/>
/// zeroizes both keys and both counters; use after dispose throws
/// <see cref="ObjectDisposedException"/> — retained instances never decrypt again.
/// </para>
/// </remarks>
public sealed class NoiseTransport : IDisposable
{
    /// <summary>AEAD key size in bytes (AES-256-GCM).</summary>
    internal const int KeySize = 32;

    /// <summary>Wire nonce size in bytes (12-byte GCM IV).</summary>
    internal const int NonceSize = 12;

    /// <summary>Authentication tag size in bytes (full 128-bit GCM tag).</summary>
    internal const int TagSize = 16;

    private readonly byte[] _writeKey;
    private readonly byte[] _readKey;
    private AesGcm? _sendCipher;
    private AesGcm? _receiveCipher;
    private ulong _sendCounter;
    private ulong _receiveCounter;
    private int _disposed;

    /// <summary>
    /// Creates a transport over already-derived direction keys. Both keys are copied in —
    /// the caller retains ownership of (and responsibility for zeroizing) its arrays.
    /// </summary>
    /// <param name="writeKey">This side's encryption key (32 bytes). Never logged.</param>
    /// <param name="readKey">This side's decryption key (32 bytes). Never logged.</param>
    /// <exception cref="SdkFormatException">
    /// Either key is not exactly <see cref="KeySize"/> bytes. The message names the rule
    /// only — no key material is ever embedded (message discipline, T-01-01).
    /// </exception>
    public NoiseTransport(ReadOnlyMemory<byte> writeKey, ReadOnlyMemory<byte> readKey)
    {
        if (writeKey.Length != KeySize || readKey.Length != KeySize)
        {
            throw new SdkFormatException("Transport keys must be 32 bytes each.");
        }

        _writeKey = writeKey.ToArray();
        _readKey = readKey.ToArray();
        try
        {
            _sendCipher = new AesGcm(_writeKey, TagSize);
            _receiveCipher = new AesGcm(_readKey, TagSize);
        }
        catch
        {
            _sendCipher?.Dispose();
            _sendCipher = null;
            _receiveCipher = null;
            CryptographicOperations.ZeroMemory(_writeKey);
            CryptographicOperations.ZeroMemory(_readKey);
            throw;
        }
    }

    /// <summary>
    /// Encrypts one plaintext frame: advances the send counter (never reused) and returns
    /// <c>nonce(12) || ciphertext || tag(16)</c>.
    /// </summary>
    /// <param name="plaintext">Frame plaintext. Must not exceed the frame cap.</param>
    /// <returns>The encrypted packet, ready for the transport.</returns>
    /// <exception cref="ObjectDisposedException">The transport is disposed.</exception>
    /// <exception cref="SdkTransportException">
    /// The plaintext exceeds the frame cap, or the per-direction nonce space is exhausted
    /// (the connection must re-handshake — nonces are never reused, Pitfall 4).
    /// </exception>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        ThrowIfDisposed();
        if (plaintext.Length > NoiseProtocol.MaxFrameBytes)
        {
            throw new SdkTransportException("Frame payload exceeds the size limit.");
        }

        if (_sendCounter == uint.MaxValue)
        {
            throw new SdkTransportException("Transport nonce space exhausted.");
        }

        Span<byte> nonce = stackalloc byte[NonceSize];
        BinaryPrimitives.WriteUInt32BigEndian(nonce[^4..], (uint)_sendCounter);
        _sendCounter++;

        var packet = new byte[NonceSize + plaintext.Length + TagSize];
        nonce.CopyTo(packet.AsSpan(0, NonceSize));
        _sendCipher!.Encrypt(
            nonce,
            plaintext,
            packet.AsSpan(NonceSize, plaintext.Length),
            packet.AsSpan(NonceSize + plaintext.Length, TagSize));
        return packet;
    }

    /// <summary>
    /// Decrypts one packet produced by <see cref="Encrypt"/> on the peer side: checks the
    /// length bounds BEFORE allocating, verifies the nonce is exactly the next expected
    /// value (strict ordering — replays and reorders fail closed), then authenticates and
    /// decrypts. The receive counter advances only on success.
    /// </summary>
    /// <param name="packet"><c>nonce(12) || ciphertext || tag(16)</c>.</param>
    /// <returns>The frame plaintext.</returns>
    /// <exception cref="ObjectDisposedException">The transport is disposed.</exception>
    /// <exception cref="SdkTransportException">
    /// Framing is inconsistent, the payload exceeds the cap, the nonce is unexpected, or
    /// authentication failed. Messages name the rule only — never packet bytes.
    /// </exception>
    public byte[] Decrypt(ReadOnlySpan<byte> packet)
    {
        ThrowIfDisposed();
        if (packet.Length < NonceSize + TagSize)
        {
            throw new SdkTransportException("Encrypted packet failed validation: inconsistent framing.");
        }

        if (packet.Length - (NonceSize + TagSize) > NoiseProtocol.MaxFrameBytes)
        {
            throw new SdkTransportException("Frame payload exceeds the size limit.");
        }

        Span<byte> expectedNonce = stackalloc byte[NonceSize];
        BinaryPrimitives.WriteUInt32BigEndian(expectedNonce[^4..], (uint)_receiveCounter);
        if (!packet.Slice(0, NonceSize).SequenceEqual(expectedNonce))
        {
            throw new SdkTransportException("Encrypted packet failed validation: unexpected nonce.");
        }

        int ciphertextLength = packet.Length - (NonceSize + TagSize);
        var plaintext = new byte[ciphertextLength];
        try
        {
            _receiveCipher!.Decrypt(
                packet.Slice(0, NonceSize),
                packet.Slice(NonceSize, ciphertextLength),
                packet.Slice(NonceSize + ciphertextLength, TagSize),
                plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new SdkTransportException("Encrypted packet failed authentication.", ex);
        }

        _receiveCounter++;
        return plaintext;
    }

    /// <summary>
    /// Zeroizes both keys and both nonce counters and disposes the ciphers. Use after
    /// dispose throws <see cref="ObjectDisposedException"/>; disposing twice is a no-op.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _sendCipher?.Dispose();
        _receiveCipher?.Dispose();
        _sendCipher = null;
        _receiveCipher = null;
        CryptographicOperations.ZeroMemory(_writeKey);
        CryptographicOperations.ZeroMemory(_readKey);
        _sendCounter = 0;
        _receiveCounter = 0;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new ObjectDisposedException(nameof(NoiseTransport));
        }
    }
}
