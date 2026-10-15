using System.Text.Json;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Media;
using Xunit;

public class MediaTracerTests
{
    [Fact]
    public void CryptoRoundTrip()
    {
        byte[] mediaKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        byte[] plaintext = [1, 2, 3, 4, 5];
        var (blob, fsha, fenc) = MediaCrypto.Encrypt(plaintext, mediaKey, MediaCrypto.ImageKeysInfo);
        var decoded = MediaCrypto.VerifyAndDecrypt(blob, mediaKey, MediaCrypto.ImageKeysInfo, fsha, fenc);
        Assert.Equal(plaintext, decoded);
    }
}