using System.Security.Cryptography;
using System.Text;
using Iwasug.WhatsApp.Sdk.Errors;

namespace Iwasug.WhatsApp.Sdk.Pairing;

/// <summary>
/// Link-code cryptographic chain (CONN-02, T-3-10): PBKDF2-SHA256 key
/// derivation, AES-256-CTR ephemeral wrap, HKDF adv-secret and bundle-key
/// derivation, and AES-GCM bundle protection — each stage a separate method so
/// each is KAT-testable against <c>link-code-kats.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Behavior provenance (03-RESEARCH.md Code Examples 2–3, which cite whatsmeow
/// <c>pair-code.go</c> behavioral read-only — no code copied): PBKDF2-SHA256
/// with 131072 iterations and 32-byte output over the ASCII code; the 80-byte
/// wrap layout <c>salt[0:32] ‖ iv[32:48] ‖ ct[48:80]</c>; the
/// <c>adv_secret</c> and <c>link_code_pairing_key_bundle_encryption_key</c>
/// HKDF info strings; the <c>nonce(12) ‖ ciphertext ‖ tag(16)</c> bundle wire
/// shape. The C# below is independently written.
/// </para>
/// <para>
/// Primitive discipline (STACK.md rule — composition, never invention): every
/// stage delegates to an audited primitive — <c>Rfc2898DeriveBytes.Pbkdf2</c>,
/// <c>Aes</c>, <c>HKDF</c>, <c>AesGcm</c> — plus NSec where curve math is
/// needed (callers derive publics; nothing here touches a curve). The one
/// genuine composition is AES-CTR: .NET 10 has no <c>CipherMode.Ctr</c> (the
/// <c>CipherMode</c> fields are CBC/ECB/OFB/CFB/CTS), so CTR runs over the
/// in-box <c>Aes</c> block cipher in ECB mode driving a 16-byte big-endian
/// counter — the standard construction, covered by the CTR known-answer test.
/// CTR (not CBC) because the wrap encrypts a fixed 32-byte ephemeral public
/// into a position-independent keystream segment, matching the upstream wrap
/// layout byte-for-byte.
/// </para>
/// <para>
/// Secret hygiene (T-3-10): no logging here (the coordinator is the sole log
/// site and logs rule names only); key copies and keystream buffers are
/// zeroized in <c>finally</c>; exception messages name rules, never values.
/// </para>
/// </remarks>
internal static class LinkCodeCrypto
{
    /// <summary>PBKDF2 iteration count — the single declared source (Pitfall 4: drifts silently).</summary>
    internal const int Pbkdf2Iterations = 131072;

    /// <summary>Derived-key and adv-secret size.</summary>
    internal const int KeySize = 32;

    /// <summary>Link-code salt size (the wrap's leading segment).</summary>
    internal const int SaltSize = 32;

    /// <summary>AES-CTR counter-block / IV size.</summary>
    internal const int CounterSize = 16;

    /// <summary>AES-GCM nonce size.</summary>
    internal const int NonceSize = 12;

    /// <summary>AES-GCM authentication tag size.</summary>
    internal const int TagSize = 16;

    /// <summary>Wrapped-ephemeral size: salt(32) ‖ iv(16) ‖ ct(32).</summary>
    internal const int WrappedEphemeralSize = SaltSize + CounterSize + KeySize;

    /// <summary>HKDF info for the adv secret (03-RESEARCH.md Code Example 3).</summary>
    internal static readonly byte[] AdvSecretInfo = Encoding.ASCII.GetBytes("adv_secret");

    /// <summary>HKDF info for the key-bundle encryption key (03-RESEARCH.md Code Example 3).</summary>
    internal static readonly byte[] BundleKeyInfo =
        Encoding.ASCII.GetBytes("link_code_pairing_key_bundle_encryption_key");

    /// <summary>
    /// Derives the 32-byte link-code wrapping key: PBKDF2-SHA256 over the ASCII
    /// code with 131072 iterations.
    /// </summary>
    /// <param name="codeAscii">The eight pairing-code characters as ASCII (never mutated).</param>
    /// <param name="salt">The 32-byte wrap salt (never mutated).</param>
    /// <returns>A fresh 32-byte key (caller-owned).</returns>
    /// <exception cref="ArgumentException">The code is empty or the salt is not 32 bytes.</exception>
    internal static byte[] DeriveKey(ReadOnlySpan<byte> codeAscii, ReadOnlySpan<byte> salt)
    {
        if (codeAscii.IsEmpty)
        {
            throw new ArgumentException("The link-code password must not be empty.", nameof(codeAscii));
        }

        if (salt.Length != SaltSize)
        {
            throw new ArgumentException("The link-code salt must be 32 bytes.", nameof(salt));
        }

        byte[] password = codeAscii.ToArray();
        byte[] saltCopy = salt.ToArray();
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                password, saltCopy, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            CryptographicOperations.ZeroMemory(saltCopy);
        }
    }

    /// <summary>
    /// AES-256-CTR encryption over the in-box <c>Aes</c> block cipher: each
    /// 16-byte counter block (the IV incremented as a 128-bit big-endian
    /// integer) is ECB-encrypted and XORed into the keystream.
    /// </summary>
    /// <param name="key">The 32-byte AES key (never mutated).</param>
    /// <param name="iv">The 16-byte initial counter block (never mutated).</param>
    /// <param name="plaintext">The bytes to encrypt (never mutated).</param>
    /// <returns>The ciphertext (same length as the input).</returns>
    /// <exception cref="ArgumentException">The key is not 32 bytes or the IV is not 16 bytes.</exception>
    internal static byte[] EncryptCtr(byte[] key, byte[] iv, byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(iv);
        ArgumentNullException.ThrowIfNull(plaintext);
        byte[] output = new byte[plaintext.Length];
        XorCtr(key, iv, plaintext, output);
        return output;
    }

    /// <summary>
    /// AES-256-CTR decryption — the same XOR operation as encryption (CTR is
    /// symmetric); a separate name so call sites read directionally.
    /// </summary>
    /// <param name="key">The 32-byte AES key (never mutated).</param>
    /// <param name="iv">The 16-byte initial counter block (never mutated).</param>
    /// <param name="ciphertext">The bytes to decrypt (never mutated).</param>
    /// <returns>The plaintext (same length as the input).</returns>
    /// <exception cref="ArgumentException">The key is not 32 bytes or the IV is not 16 bytes.</exception>
    internal static byte[] DecryptCtr(byte[] key, byte[] iv, byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(iv);
        ArgumentNullException.ThrowIfNull(ciphertext);
        byte[] output = new byte[ciphertext.Length];
        XorCtr(key, iv, ciphertext, output);
        return output;
    }

    /// <summary>
    /// Derives the 32-byte adv secret via in-box HKDF-SHA256.
    /// </summary>
    /// <param name="ikm">Input keying material (never mutated).</param>
    /// <param name="salt">The 32-byte HKDF salt (never mutated).</param>
    /// <param name="info">The domain-separation info (for example <see cref="AdvSecretInfo"/>).</param>
    /// <returns>A fresh 32-byte secret (caller-owned).</returns>
    /// <exception cref="ArgumentException">The IKM is empty, the salt is not 32 bytes, or the info is empty.</exception>
    internal static byte[] DeriveAdvSecret(byte[] ikm, byte[] salt, byte[] info)
    {
        ArgumentNullException.ThrowIfNull(ikm);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(info);
        if (ikm.Length == 0)
        {
            throw new ArgumentException("The HKDF input keying material must not be empty.", nameof(ikm));
        }

        if (salt.Length != SaltSize)
        {
            throw new ArgumentException("The HKDF salt must be 32 bytes.", nameof(salt));
        }

        if (info.Length == 0)
        {
            throw new ArgumentException("The HKDF info must not be empty.", nameof(info));
        }

        return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, KeySize, salt, info);
    }

    /// <summary>
    /// Derives the 32-byte key-bundle encryption key: HKDF-SHA256 over the
    /// link key with the bundle info string.
    /// </summary>
    /// <param name="linkKey">The 32-byte link-code wrapping key (never mutated).</param>
    /// <param name="salt">The 32-byte bundle salt (never mutated).</param>
    /// <returns>A fresh 32-byte bundle key (caller-owned).</returns>
    /// <exception cref="ArgumentException">The link key or salt is not 32 bytes.</exception>
    internal static byte[] DeriveBundleKey(byte[] linkKey, byte[] salt)
    {
        ArgumentNullException.ThrowIfNull(linkKey);
        ArgumentNullException.ThrowIfNull(salt);
        if (linkKey.Length != KeySize)
        {
            throw new ArgumentException("The link key must be 32 bytes.", nameof(linkKey));
        }

        if (salt.Length != SaltSize)
        {
            throw new ArgumentException("The bundle salt must be 32 bytes.", nameof(salt));
        }

        return HKDF.DeriveKey(HashAlgorithmName.SHA256, linkKey, KeySize, salt, BundleKeyInfo);
    }

    /// <summary>
    /// Protects one key bundle with AES-GCM: the wire bytes are
    /// <c>nonce(12) ‖ ciphertext ‖ tag(16)</c>.
    /// </summary>
    /// <param name="key">The 32-byte bundle key (never mutated).</param>
    /// <param name="nonce">The 12-byte fresh nonce (never mutated — a fresh nonce per bundle).</param>
    /// <param name="plaintext">The bundle bytes (never mutated).</param>
    /// <returns>The wire bytes (caller-owned).</returns>
    /// <exception cref="ArgumentException">The key, nonce, or plaintext violates its shape rule.</exception>
    internal static byte[] ProtectBundle(byte[] key, byte[] nonce, byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(nonce);
        ArgumentNullException.ThrowIfNull(plaintext);
        if (key.Length != KeySize)
        {
            throw new ArgumentException("The bundle key must be 32 bytes.", nameof(key));
        }

        if (nonce.Length != NonceSize)
        {
            throw new ArgumentException("The bundle nonce must be 12 bytes.", nameof(nonce));
        }

        if (plaintext.Length == 0)
        {
            throw new ArgumentException("The bundle plaintext must not be empty.", nameof(plaintext));
        }

        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagSize];
        using (var gcm = new AesGcm(key, TagSize))
        {
            gcm.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        byte[] bundle = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(bundle.AsSpan(0, NonceSize));
        ciphertext.CopyTo(bundle.AsSpan(NonceSize, ciphertext.Length));
        tag.CopyTo(bundle.AsSpan(NonceSize + ciphertext.Length, TagSize));
        CryptographicOperations.ZeroMemory(ciphertext);
        CryptographicOperations.ZeroMemory(tag);
        return bundle;
    }

    /// <summary>
    /// Opens one GCM-protected bundle: splits the wire bytes and verifies the
    /// tag before releasing the plaintext. A tampered byte fails here — the
    /// BCL throws and no plaintext is returned (the behavioral GCM gate).
    /// </summary>
    /// <param name="key">The 32-byte bundle key (never mutated).</param>
    /// <param name="bundle">The wire bytes: nonce(12) ‖ ciphertext ‖ tag(16).</param>
    /// <returns>The plaintext bundle (caller-owned).</returns>
    /// <exception cref="ArgumentException">The key or bundle violates its shape rule.</exception>
    /// <exception cref="CryptographicException">The tag does not verify (wrong key or tampered bytes).</exception>
    internal static byte[] UnprotectBundle(byte[] key, byte[] bundle)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(bundle);
        if (key.Length != KeySize)
        {
            throw new ArgumentException("The bundle key must be 32 bytes.", nameof(key));
        }

        if (bundle.Length < NonceSize + TagSize + 1)
        {
            throw new ArgumentException("The bundle is too short to carry nonce, ciphertext, and tag.", nameof(bundle));
        }

        byte[] nonce = bundle.AsSpan(0, NonceSize).ToArray();
        byte[] ciphertext = bundle.AsSpan(NonceSize, bundle.Length - NonceSize - TagSize).ToArray();
        byte[] tag = bundle.AsSpan(bundle.Length - TagSize, TagSize).ToArray();
        byte[] plaintext = new byte[ciphertext.Length];
        try
        {
            using (var gcm = new AesGcm(key, TagSize))
            {
                gcm.Decrypt(nonce, ciphertext, tag, plaintext);
            }

            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    /// <summary>
    /// Wraps one 32-byte ephemeral public into the 80-byte wire layout
    /// <c>salt[0:32] ‖ iv[32:48] ‖ ct[48:80]</c> (03-RESEARCH.md Code Example 2).
    /// </summary>
    /// <param name="linkKey">The 32-byte link-code wrapping key (never mutated).</param>
    /// <param name="salt">The 32-byte wrap salt (never mutated).</param>
    /// <param name="iv">The 16-byte CTR initial counter (never mutated).</param>
    /// <param name="ephemeralPublic">The 32-byte ephemeral public (never mutated).</param>
    /// <returns>The 80-byte wrapped ephemeral (caller-owned).</returns>
    /// <exception cref="ArgumentException">Any input violates its shape rule.</exception>
    internal static byte[] WrapEphemeral(byte[] linkKey, byte[] salt, byte[] iv, byte[] ephemeralPublic)
    {
        ArgumentNullException.ThrowIfNull(linkKey);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(iv);
        ArgumentNullException.ThrowIfNull(ephemeralPublic);
        if (linkKey.Length != KeySize)
        {
            throw new ArgumentException("The link key must be 32 bytes.", nameof(linkKey));
        }

        if (salt.Length != SaltSize)
        {
            throw new ArgumentException("The wrap salt must be 32 bytes.", nameof(salt));
        }

        if (iv.Length != CounterSize)
        {
            throw new ArgumentException("The wrap IV must be 16 bytes.", nameof(iv));
        }

        if (ephemeralPublic.Length != KeySize)
        {
            throw new ArgumentException("The ephemeral public must be 32 bytes.", nameof(ephemeralPublic));
        }

        byte[] ciphertext = EncryptCtr(linkKey, iv, ephemeralPublic);
        byte[] wrapped = new byte[WrappedEphemeralSize];
        salt.CopyTo(wrapped.AsSpan(0, SaltSize));
        iv.CopyTo(wrapped.AsSpan(SaltSize, CounterSize));
        ciphertext.CopyTo(wrapped.AsSpan(SaltSize + CounterSize, KeySize));
        CryptographicOperations.ZeroMemory(ciphertext);
        return wrapped;
    }

    /// <summary>
    /// Opens one 80-byte wrapped ephemeral: parses the layout and CTR-decrypts
    /// the trailing segment (the test-side inverse of <see cref="WrapEphemeral"/>).
    /// </summary>
    /// <param name="linkKey">The 32-byte link-code wrapping key (never mutated).</param>
    /// <param name="wrapped">The 80-byte wrapped ephemeral (never mutated).</param>
    /// <returns>The 32-byte ephemeral public (caller-owned).</returns>
    /// <exception cref="ArgumentException">An input violates its shape rule.</exception>
    internal static byte[] UnwrapEphemeral(byte[] linkKey, byte[] wrapped)
    {
        ArgumentNullException.ThrowIfNull(linkKey);
        ArgumentNullException.ThrowIfNull(wrapped);
        if (linkKey.Length != KeySize)
        {
            throw new ArgumentException("The link key must be 32 bytes.", nameof(linkKey));
        }

        if (wrapped.Length != WrappedEphemeralSize)
        {
            throw new ArgumentException("The wrapped ephemeral must be 80 bytes.", nameof(wrapped));
        }

        byte[] iv = wrapped.AsSpan(SaltSize, CounterSize).ToArray();
        byte[] ciphertext = wrapped.AsSpan(SaltSize + CounterSize, KeySize).ToArray();
        try
        {
            return DecryptCtr(linkKey, iv, ciphertext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(iv);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private static void XorCtr(byte[] key, byte[] iv, byte[] input, byte[] output)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException("The CTR key must be 32 bytes.", nameof(key));
        }

        if (iv.Length != CounterSize)
        {
            throw new ArgumentException("The CTR IV must be 16 bytes.", nameof(iv));
        }

        if (input.Length != output.Length)
        {
            throw new ArgumentException("The CTR input and output must have equal length.", nameof(input));
        }

        byte[] counter = (byte[])iv.Clone();
        byte[] keystream = new byte[CounterSize];
        try
        {
            using Aes aes = Aes.Create();
            aes.Key = (byte[])key.Clone();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            using ICryptoTransform encryptor = aes.CreateEncryptor();
            int offset = 0;
            while (offset < input.Length)
            {
                encryptor.TransformBlock(counter, 0, CounterSize, keystream, 0);
                int block = Math.Min(CounterSize, input.Length - offset);
                for (int i = 0; i < block; i++)
                {
                    output[offset + i] = (byte)(input[offset + i] ^ keystream[i]);
                }

                offset += block;
                IncrementBigEndian(counter);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(counter);
            CryptographicOperations.ZeroMemory(keystream);
        }
    }

    private static void IncrementBigEndian(byte[] counter)
    {
        for (int i = counter.Length - 1; i >= 0; i--)
        {
            counter[i]++;
            if (counter[i] != 0)
            {
                return;
            }
        }
    }
}
