using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Media;
using Iwasug.WhatsApp.Sdk.Messaging;
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
    public void FourthRequestIsExhaustedTypedOutcome()
    {
        var tracker = new MediaRetryTracker();
        Assert.True(tracker.TryReserve("m4"));
        Assert.True(tracker.TryReserve("m4"));
        Assert.True(tracker.TryReserve("m4"));
        Assert.False(tracker.TryReserve("m4"));
        Assert.True(tracker.IsExhausted("m4"));
        Assert.Equal(3, tracker.GetAttempts("m4"));
    }

    [Fact]
    public void ServerErrorReceiptNodeRoutesMediaRetryBeforeRetryClassification()
    {
        var chat = Jid.Parse("alice@s.whatsapp.net");
        BinaryNode node = ReceiptHandler.BuildServerErrorReceiptNode("m-route", chat, isFromMe: false, participant: null);

        Assert.Equal("receipt", node.Tag);
        Assert.Equal("m-route", node.Attrs["id"]);
        Assert.Equal("server-error", node.Attrs["type"]);
        Assert.Equal(chat.ToString(), node.Attrs["to"]);
        Assert.Contains(node.Children ?? [], c => c.Tag == "rmr");
        Assert.Contains(node.Children ?? [], c => c.Tag == "encrypt");
    }

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

    [Fact]
    public void EvictionHorizonDefined() => Assert.Equal(TimeSpan.FromHours(1), MediaRetryTracker.EvictionHorizon);
}