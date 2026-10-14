#pragma warning disable xUnit1051
using Iwasug.WhatsApp.Sdk.Safety;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Safety;

public sealed class TokenBucketRateLimitTests
{
    [Fact]
    public async Task minute_cap_rejects_over_budget()
    {
        var clock = new FakeTimeProvider();
        var options = new SafetyOptions { MaxMessagesPerMinute = 20 };
        var set = new TokenBucketRateLimiterSet(clock, options);
        for (int i = 0; i < 20; i++)
        {
            var r = await set.TryAcquireAsync();
            Assert.True(r.Acquired);
        }
        var over = await set.TryAcquireAsync();
        Assert.False(over.Acquired);
        Assert.Equal(RateWindow.Minute, over.Window);
    }

    [Fact]
    public async Task hour_cap_rejects_over_budget()
    {
        var clock = new FakeTimeProvider();
        var options = new SafetyOptions { MaxMessagesPerMinute = 1000, MaxMessagesPerHour = 250, AllowUnsafeThroughput = true, DeclaredUnsafeAcknowledgment = "Rate-limit test." };
        var set = new TokenBucketRateLimiterSet(clock, options);
        for (int i = 0; i < 250; i++)
        {
            var r = await set.TryAcquireAsync();
            Assert.True(r.Acquired);
        }
        var over = await set.TryAcquireAsync();
        Assert.False(over.Acquired);
        Assert.Equal(RateWindow.Hour, over.Window);
    }

    [Fact]
    public async Task day_cap_rejects_over_budget()
    {
        var clock = new FakeTimeProvider();
        var options = new SafetyOptions { MaxMessagesPerMinute = 2000, MaxMessagesPerHour = 2000, MaxMessagesPerDay = 1000, AllowUnsafeThroughput = true, DeclaredUnsafeAcknowledgment = "Rate-limit test." };
        var set = new TokenBucketRateLimiterSet(clock, options);
        for (int i = 0; i < 1000; i++)
        {
            var r = await set.TryAcquireAsync();
            Assert.True(r.Acquired);
        }
        var over = await set.TryAcquireAsync();
        Assert.False(over.Acquired);
        Assert.Equal(RateWindow.Day, over.Window);
    }

    [Fact]
    public async Task cancellation_propagates()
    {
        var clock = new FakeTimeProvider();
        var set = new TokenBucketRateLimiterSet(clock, new SafetyOptions());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await set.TryAcquireAsync(cts.Token));
    }
}