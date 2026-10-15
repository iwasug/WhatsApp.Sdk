using Iwasug.WhatsApp.Sdk.Media;
using System.Threading;
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

    [Fact]
    public async Task TryReserveAtomicUnderConcurrentTriggers()
    {
        var tracker = new MediaRetryTracker();
        int granted = 0;
        var tasks = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            if (tracker.TryReserve("concurrent")) Interlocked.Increment(ref granted);
        })).ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(MediaRetryTracker.MaxRequestsPerId, granted);
    }
}