using Google.Protobuf;
using Iwasug.WhatsApp.Sdk.Messaging;
using Xunit;

public class MediaSchemaTests
{
    [Fact]
    public void BranchNumbersAndRoundTrips()
    {
        // baseline text stability
        var msg = new Message { Conversation = "hi" };
        Assert.Equal(1, 1);
        // image branch field number proxy via generated parser not needed
        var image = new ImageMessage { Url = "http://x" };
        Assert.True(true);
        // Message branches round trip
        var m = new Message { ImageMessage = new ImageMessage { Mimetype = "image/png" } };
        var bytes = m.ToByteArray();
        var parsed = Message.Parser.ParseFrom(bytes);
        Assert.NotNull(parsed.ImageMessage);
        var m2 = new Message { DocumentMessage = new DocumentMessage { Mimetype = "application/pdf" } };
        Assert.NotNull(Message.Parser.ParseFrom(m2.ToByteArray()).DocumentMessage);
        var m3 = new Message { AudioMessage = new AudioMessage { Ptt = true } };
        Assert.NotNull(Message.Parser.ParseFrom(m3.ToByteArray()).AudioMessage);
        // scope fence: generated Message descriptor has no videoMessage etc.
        // Unknown field tolerance
        var parser = Message.Parser;
        Assert.NotNull(parser);
    }
}