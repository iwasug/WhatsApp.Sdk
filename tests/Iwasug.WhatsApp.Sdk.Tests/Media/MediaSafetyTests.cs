using Iwasug.WhatsApp.Sdk.Media;
using Xunit;

public class MediaSafetyTests
{
    [Fact]
    public void MimetypeExecutableRejected()
    {
        Assert.Throws<Iwasug.WhatsApp.Sdk.Errors.SdkFormatException>(() =>
            MediaMetadata.ValidateMimetype(MediaKind.Document, "application/x-msdownload"));
    }

    [Fact]
    public void TrackerMaxRequests() => Assert.Equal(3, MediaRetryTracker.MaxRequestsPerId);
}