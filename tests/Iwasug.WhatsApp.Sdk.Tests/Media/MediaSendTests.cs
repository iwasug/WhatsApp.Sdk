using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Media;
using Iwasug.WhatsApp.Sdk.Messaging;
using Xunit;

public class MediaSendTests
{
    [Theory]
    [InlineData("SendImageAsync")]
    [InlineData("SendDocumentAsync")]
    [InlineData("SendVoiceNoteAsync")]
    [InlineData("SendGroupImageAsync")]
    [InlineData("SendGroupDocumentAsync")]
    [InlineData("SendGroupVoiceNoteAsync")]
    public void PublicMediaSendMethodsExist(string methodName)
    {
        typeof(Messenger).GetMethod(methodName).Should().NotBeNull();
    }

    [Fact]
    public void DocumentBuildCarriesFileName()
    {
        var upload = new MediaUploadResult("https://cdn/x", "/v1/dp", new byte[32], new byte[32], new byte[32], 4, 1);
        var plaintext = MessageBuilder.BuildDocumentMessage(upload, "application/pdf", "title", "file.pdf", "cap");
        var message = MessageBuilder.ParseMessage(plaintext);
        message.DocumentMessage.Should().NotBeNull();
        message.DocumentMessage!.FileName.Should().Be("file.pdf");
    }

    [Fact]
    public void VoiceNoteBuildCarriesPttSecondsAndAudioMediaType()
    {
        var upload = new MediaUploadResult("https://cdn/x", "/v1/dp", new byte[32], new byte[32], new byte[32], 4, 1);
        var plaintext = MessageBuilder.BuildVoiceNoteMessage(upload, "audio/ogg; codecs=opus", 7, null);
        var message = MessageBuilder.ParseMessage(plaintext);
        message.AudioMessage.Should().NotBeNull();
        message.AudioMessage!.Ptt.Should().BeTrue();
        message.AudioMessage.Seconds.Should().Be(7);
    }

    [Fact]
    public void MediaSendWireAttrsConstantsAreStable()
    {
        MessageBuilder.MediaTypeAudio.Should().Be("audio");
        MessageBuilder.EncTypeSenderKey.Should().Be("skmsg");
    }
}