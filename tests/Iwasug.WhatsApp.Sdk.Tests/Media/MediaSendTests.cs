using AwesomeAssertions;
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
}
