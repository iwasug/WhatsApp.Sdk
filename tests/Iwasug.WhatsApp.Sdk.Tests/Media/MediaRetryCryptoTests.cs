using System.Security.Cryptography;
using System.Text.Json;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Media;
using Xunit;

public class MediaRetryCryptoTests
{
    [Fact]
    public void RetryKeyAndGcmLoopback()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "media-retry-fixtures.json");
        var json = JsonDocument.Parse(File.ReadAllText(path));
        byte[] mediaKey = Convert.FromHexString(json.RootElement.GetProperty("mediaKey").GetString()!);
        byte[] retryKey = MediaCrypto.DeriveRetryKey(mediaKey);
        Assert.Equal(json.RootElement.GetProperty("retryKey").GetString(), Convert.ToHexString(retryKey));

        var gcm = json.RootElement.GetProperty("gcm");
        byte[] ct = Convert.FromHexString(gcm.GetProperty("ciphertextHex").GetString()!);
        byte[] tag = Convert.FromHexString(gcm.GetProperty("tagHex").GetString()!);
        byte[] nonce = Convert.FromHexString(gcm.GetProperty("nonceHex").GetString()!);
        var plain = MediaCrypto.AesGcmDecrypt(ct, tag, nonce, retryKey, "3EB0MSGID"u8);
        Assert.Equal(gcm.GetProperty("plaintextHex").GetString(), Convert.ToHexString(plain));
        Assert.Throws<AuthenticationTagMismatchException>(() => MediaCrypto.AesGcmDecrypt(ct, tag, nonce, retryKey, "X"u8));
    }
}