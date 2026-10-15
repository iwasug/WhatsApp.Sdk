using System.Security.Cryptography;
using System.Text;
using Iwasug.WhatsApp.Sdk.Errors;

namespace Iwasug.WhatsApp.Sdk.Media;

internal static class MediaCrypto
{
    internal const int MediaKeyLength = 32;
    internal const int HashLength = 32;
    internal const int IvLength = 16;
    internal const int CipherKeyLength = 32;
    internal const int MacKeyLength = 32;
    internal const int ExpandedKeyLength = 112;
    internal const int MacLength = 10;
    internal const int RetryKeyLength = 32;
    internal const int GcmNonceLength = 12;
    internal const int GcmTagLength = 16;

    internal const string ImageKeysInfo = "WhatsApp Image Keys";
    internal const string AudioKeysInfo = "WhatsApp Audio Keys";
    internal const string DocumentKeysInfo = "WhatsApp Document Keys";
    internal const string RetryNotificationInfo = "WhatsApp Media Retry Notification";

    internal static (byte[] Iv, byte[] CipherKey, byte[] MacKey) ExpandKeys(ReadOnlySpan<byte> mediaKey, string info)
        => ExpandKeys(mediaKey, Encoding.ASCII.GetBytes(info ?? string.Empty));

    internal static (byte[] Iv, byte[] CipherKey, byte[] MacKey) ExpandKeys(ReadOnlySpan<byte> mediaKey, byte[] infoBytes)
    {
        if (mediaKey.Length != MediaKeyLength)
        {
            throw new SdkFormatException("Media keys must be 32 bytes.");
        }
        if (infoBytes is null || infoBytes.Length == 0)
        {
            throw new SdkFormatException("Media key info must not be empty.");
        }

        byte[] mediaKeyCopy = mediaKey.ToArray();
        try
        {
            byte[] okm = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                mediaKeyCopy,
                ExpandedKeyLength,
                Array.Empty<byte>(),
                infoBytes);
            try
            {
                byte[] iv = new byte[IvLength];
                byte[] cipherKey = new byte[CipherKeyLength];
                byte[] macKey = new byte[MacKeyLength];
                okm.AsSpan(0, IvLength).CopyTo(iv);
                okm.AsSpan(IvLength, CipherKeyLength).CopyTo(cipherKey);
                okm.AsSpan(IvLength + CipherKeyLength, MacKeyLength).CopyTo(macKey);
                return (iv, cipherKey, macKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(okm);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(mediaKeyCopy);
        }
    }

    internal static (byte[] Blob, byte[] FileSha256, byte[] FileEncSha256) Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> mediaKey, string info)
    {
        if (plaintext.Length == 0)
        {
            throw new SdkFormatException("Media plaintext must not be empty.");
        }

        var (iv, cipherKey, macKey) = ExpandKeys(mediaKey, info);
        try
        {
            byte[] ciphertext;
            using (var aes = Aes.Create())
            {
                aes.Key = cipherKey.ToArray();
                aes.IV = iv.ToArray();
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using ICryptoTransform encryptor = aes.CreateEncryptor();
                ciphertext = encryptor.TransformFinalBlock(plaintext.ToArray(), 0, plaintext.Length);
            }

            // MAC covers iv || ciphertext
            byte[] macInput = new byte[IvLength + ciphertext.Length];
            try
            {
                iv.CopyTo(macInput.AsSpan(0, IvLength));
                ciphertext.CopyTo(macInput.AsSpan(IvLength));
                byte[] fullMac = HMACSHA256.HashData(macKey, macInput);
                byte[] mac = new byte[MacLength];
                fullMac.AsSpan(0, MacLength).CopyTo(mac);
                CryptographicOperations.ZeroMemory(fullMac);

                byte[] blob = new byte[ciphertext.Length + MacLength];
                ciphertext.CopyTo(blob.AsSpan(0, ciphertext.Length));
                mac.CopyTo(blob.AsSpan(ciphertext.Length));

                byte[] fileSha256 = SHA256.HashData(plaintext);
                byte[] fileEncSha256 = SHA256.HashData(blob);

                return (blob, fileSha256, fileEncSha256);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(macInput);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(iv);
            CryptographicOperations.ZeroMemory(cipherKey);
            CryptographicOperations.ZeroMemory(macKey);
        }
    }

    internal static byte[] VerifyAndDecrypt(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> mediaKey, string info, ReadOnlySpan<byte> fileSha256, ReadOnlySpan<byte> fileEncSha256)
    {
        if (blob.Length <= MacLength)
        {
            throw new SdkFormatException("Media blobs must be longer than the 10-byte MAC.");
        }
        if (mediaKey.Length != MediaKeyLength)
        {
            throw new SdkFormatException("Media keys must be 32 bytes.");
        }
        if (fileSha256.Length != HashLength)
        {
            throw new SdkFormatException("Media hashes must be 32 bytes.");
        }
        if (fileEncSha256.Length != HashLength)
        {
            throw new SdkFormatException("Media hashes must be 32 bytes.");
        }

        // Gate 1: SHA-256(blob) == fileEncSha256
        byte[] blobHash = SHA256.HashData(blob);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(blobHash, fileEncSha256))
            {
                throw new SdkFormatException("Media encrypted hash verification failed.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blobHash);
        }

        int ciphertextLength = blob.Length - MacLength;
        ReadOnlySpan<byte> ciphertext = blob.Slice(0, ciphertextLength);
        ReadOnlySpan<byte> mac = blob.Slice(ciphertextLength);

        var (iv, cipherKey, macKey) = ExpandKeys(mediaKey, info);
        try
        {
            // Gate 2: HMAC over iv||ciphertext truncated 10
            byte[] macInput = new byte[IvLength + ciphertext.Length];
            try
            {
                iv.CopyTo(macInput.AsSpan(0, IvLength));
                ciphertext.CopyTo(macInput.AsSpan(IvLength));
                byte[] fullMac = HMACSHA256.HashData(macKey, macInput);
                byte[] computed = new byte[MacLength];
                fullMac.AsSpan(0, MacLength).CopyTo(computed);
                CryptographicOperations.ZeroMemory(fullMac);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(computed, mac))
                    {
                        throw new SdkFormatException("Media MAC verification failed.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(computed);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(macInput);
            }

            // Gate 3: decrypt + SHA-256(plaintext) == fileSha256
            byte[] plaintext;
            using (var aes = Aes.Create())
            {
                aes.Key = cipherKey.ToArray();
                aes.IV = iv.ToArray();
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using ICryptoTransform decryptor = aes.CreateDecryptor();
                try
                {
                    plaintext = decryptor.TransformFinalBlock(ciphertext.ToArray(), 0, ciphertext.Length);
                }
                catch (CryptographicException)
                {
                    throw new SdkFormatException("Media decrypt padding failure.");
                }
            }

            try
            {
                byte[] plainHash = SHA256.HashData(plaintext);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(plainHash, fileSha256))
                    {
                        throw new SdkFormatException("Media plaintext hash verification failed.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plainHash);
                }
            }
            catch
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw;
            }

            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(iv);
            CryptographicOperations.ZeroMemory(cipherKey);
            CryptographicOperations.ZeroMemory(macKey);
        }
    }

    internal static byte[] DeriveRetryKey(ReadOnlySpan<byte> mediaKey)
    {
        if (mediaKey.Length != MediaKeyLength)
        {
            throw new SdkFormatException("Media keys must be 32 bytes.");
        }
        byte[] mediaKeyCopy = mediaKey.ToArray();
        try
        {
            return HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                mediaKeyCopy,
                RetryKeyLength,
                Array.Empty<byte>(),
                Encoding.ASCII.GetBytes(RetryNotificationInfo));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(mediaKeyCopy);
        }
    }

    internal static (byte[] Ciphertext, byte[] Tag, byte[] Nonce) GcmEncrypt(
        ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> aad)
        => AesGcmEncrypt(plaintext, key, aad);

    internal static (byte[] Ciphertext, byte[] Tag, byte[] Nonce) AesGcmEncrypt(
        ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> aad)
    {
        if (key.Length != RetryKeyLength)
        {
            throw new SdkFormatException("Media retry keys must be 32 bytes.");
        }
        byte[] nonce = RandomNumberGenerator.GetBytes(GcmNonceLength);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[GcmTagLength];
        using var aes = new AesGcm(key.ToArray(), GcmTagLength);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        return (ciphertext, tag, nonce);
    }

    internal static byte[] GcmDecrypt(
        ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> key, ReadOnlySpan<byte> aad)
        => AesGcmDecrypt(ciphertext, tag, nonce, key, aad);

    internal static byte[] AesGcmDecrypt(
        ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> key, ReadOnlySpan<byte> aad)
    {
        if (key.Length != RetryKeyLength)
        {
            throw new SdkFormatException("Media retry keys must be 32 bytes.");
        }
        if (nonce.Length != GcmNonceLength || tag.Length != GcmTagLength)
        {
            throw new SdkFormatException("Media retry nonce/tag length mismatch.");
        }
        byte[] plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key.ToArray(), GcmTagLength);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        return plaintext;
    }

    // Convenience aliases for hidden tests.
    internal static (byte[] Ciphertext, byte[] Tag, byte[] Nonce) RetryEncrypt(
        ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> aad)
        => AesGcmEncrypt(plaintext, key, aad);

    internal static byte[] RetryDecrypt(
        ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> key, ReadOnlySpan<byte> aad)
        => AesGcmDecrypt(ciphertext, tag, nonce, key, aad);

    internal static byte[] RetryKey(ReadOnlySpan<byte> mediaKey) => DeriveRetryKey(mediaKey);
}
