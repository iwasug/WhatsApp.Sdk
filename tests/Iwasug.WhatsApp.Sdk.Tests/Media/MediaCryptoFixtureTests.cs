using System.Text.Json;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Media;
using Xunit;

public class MediaCryptoFixtureTests
{
    [Fact]
    public void ExpansionAndEncryptMatchFixture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "media-crypto-fixtures.json");
        var json = JsonDocument.Parse(File.ReadAllText(path));
        byte[] mediaKey = Convert.FromHexString(json.RootElement.GetProperty("mediaKey").GetString()!);
        byte[] plaintext = Convert.FromHexString(json.RootElement.GetProperty("plaintextHex").GetString()!);

        foreach (var kind in new[] { "image", "audio", "document" })
        {
            string info = kind switch { "image" => MediaCrypto.ImageKeysInfo, "audio" => MediaCrypto.AudioKeysInfo, "document" => MediaCrypto.DocumentKeysInfo, _ => throw new() };
            var (iv, ck, mk) = MediaCrypto.ExpandKeys(mediaKey, info);
            var el = json.RootElement.GetProperty("keys").GetProperty(kind);
            Assert.Equal(el.GetProperty("iv").GetString(), Convert.ToHexString(iv));
            Assert.Equal(el.GetProperty("cipherKey").GetString(), Convert.ToHexString(ck));
            Assert.Equal(el.GetProperty("macKey").GetString(), Convert.ToHexString(mk));
        }

        var (blob, fsha, fenc) = MediaCrypto.Encrypt(plaintext, mediaKey, MediaCrypto.ImageKeysInfo);
        Assert.Equal(json.RootElement.GetProperty("encrypt").GetProperty("blobHex").GetString(), Convert.ToHexString(blob));
        Assert.Equal(json.RootElement.GetProperty("encrypt").GetProperty("fileSha256").GetString(), Convert.ToHexString(fsha));
        Assert.Equal(json.RootElement.GetProperty("encrypt").GetProperty("fileEncSha256").GetString(), Convert.ToHexString(fenc));

        // tamper fail-closed
        byte[] macFlip = Convert.FromHexString(json.RootElement.GetProperty("tamper").GetProperty("macFlipBlobHex").GetString()!);
        Assert.Throws<SdkFormatException>(() => MediaCrypto.VerifyAndDecrypt(macFlip, mediaKey, MediaCrypto.ImageKeysInfo, fsha, fenc));

        byte[] ctFlip = Convert.FromHexString(json.RootElement.GetProperty("tamper").GetProperty("cipherFlipBlobHex").GetString()!);
        Assert.Throws<SdkFormatException>(() => MediaCrypto.VerifyAndDecrypt(ctFlip, mediaKey, MediaCrypto.ImageKeysInfo, fsha, fenc));

        // swapped info
        Assert.Throws<SdkFormatException>(() => MediaCrypto.VerifyAndDecrypt(blob, mediaKey, json.RootElement.GetProperty("tamper").GetProperty("wrongInfo").GetString()!, fsha, fenc));

        // short blob
        Assert.Throws<SdkFormatException>(() => MediaCrypto.VerifyAndDecrypt(new byte[10], mediaKey, MediaCrypto.ImageKeysInfo, fsha, fenc));

        // success
        var plain = MediaCrypto.VerifyAndDecrypt(blob, mediaKey, MediaCrypto.ImageKeysInfo, fsha, fenc);
        Assert.Equal(plaintext, plain);
    }
}
