using Google.Protobuf;
using Iwasug.WhatsApp.Sdk.Messaging;
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

    [Fact]
    public void ServerErrorReceiptProtoRoundTrip()
    {
        const string stanzaId = "3EB0MSGID";
        var msg = new ServerErrorReceipt { StanzaId = stanzaId };
        var parsed = ServerErrorReceipt.Parser.ParseFrom(msg.ToByteArray());

        Assert.Equal(1, ServerErrorReceipt.StanzaIdFieldNumber);
        Assert.Equal(stanzaId, parsed.StanzaId);
        Assert.True(ServerErrorReceipt.Parser.ParseFrom(msg.ToByteArray()).Equals(parsed));
    }

    [Fact]
    public void MediaRetryNotificationProtoRoundTrip()
    {
        const string stanzaId = "3EB0MSGID";
        const string directPath = "/v1/dp-fresh";
        const int result = 1;

        var msg = new MediaRetryNotification
        {
            StanzaId = stanzaId,
            DirectPath = directPath,
            Result = result,
        };
        var parsed = MediaRetryNotification.Parser.ParseFrom(msg.ToByteArray());

        Assert.Equal(1, MediaRetryNotification.StanzaIdFieldNumber);
        Assert.Equal(2, MediaRetryNotification.DirectPathFieldNumber);
        Assert.Equal(3, MediaRetryNotification.ResultFieldNumber);
        Assert.Equal(stanzaId, parsed.StanzaId);
        Assert.Equal(directPath, parsed.DirectPath);
        Assert.Equal(result, parsed.Result);
    }
}