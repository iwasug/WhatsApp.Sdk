using AwesomeAssertions;
using Google.Protobuf;
using Iwasug.WhatsApp.Sdk.Messaging;
using Xunit;

public class MediaSchemaTests
{
    [Fact]
    public void BranchNumbersAndScopeFence()
    {
        var descriptor = Message.Descriptor;

        descriptor.FindFieldByName("conversation")!.FieldNumber.Should().Be(1);
        descriptor.FindFieldByName("senderKeyDistributionMessage")!.FieldNumber.Should().Be(2);
        descriptor.FindFieldByName("imageMessage")!.FieldNumber.Should().Be(3);
        descriptor.FindFieldByName("extendedTextMessage")!.FieldNumber.Should().Be(6);
        descriptor.FindFieldByName("documentMessage")!.FieldNumber.Should().Be(7);
        descriptor.FindFieldByName("audioMessage")!.FieldNumber.Should().Be(8);
        descriptor.FindFieldByName("messageContextInfo")!.FieldNumber.Should().Be(35);

        descriptor.FindFieldByName("videoMessage").Should().BeNull();
        descriptor.FindFieldByName("stickerMessage").Should().BeNull();
        descriptor.FindFieldByName("linkPreview").Should().BeNull();
        descriptor.FindFieldByName("viewOnce").Should().BeNull();

        // Unknown field tolerance / generated parser.
        Assert.NotNull(Message.Parser);
    }

    [Fact]
    public void MediaBranchesRoundTrip()
    {
        var m = new Message { ImageMessage = new ImageMessage { Mimetype = "image/png" } };
        Assert.NotNull(Message.Parser.ParseFrom(m.ToByteArray()).ImageMessage);
        var m2 = new Message { DocumentMessage = new DocumentMessage { Mimetype = "application/pdf" } };
        Assert.NotNull(Message.Parser.ParseFrom(m2.ToByteArray()).DocumentMessage);
        var m3 = new Message { AudioMessage = new AudioMessage { Ptt = true } };
        Assert.NotNull(Message.Parser.ParseFrom(m3.ToByteArray()).AudioMessage);
    }
}
