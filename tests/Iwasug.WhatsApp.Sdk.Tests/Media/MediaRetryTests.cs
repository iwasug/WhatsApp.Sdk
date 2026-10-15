using Iwasug.WhatsApp.Sdk.Media;
using Xunit;

public class MediaRetryTests
{
    [Fact]
    public void CapEnforced()
    {
        var t = new MediaRetryTracker();
        Assert.True(t.TryRegister("m1"));
        Assert.True(t.TryRegister("m1"));
        Assert.True(t.TryRegister("m1"));
        Assert.False(t.TryRegister("m1"));
    }

    [Fact]
    public void EvictionHorizonDefined() => Assert.Equal(TimeSpan.FromHours(1), MediaRetryTracker.EvictionHorizon);
}