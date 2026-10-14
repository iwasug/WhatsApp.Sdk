using Iwasug.WhatsApp.Sdk.Safety;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Safety;

public sealed class SafetyJitterTests
{
    [Fact]
    public void jitter_bounds_over_1000_draws()
    {
        var clock = new FakeTimeProvider();
        var options = new SafetyOptions { BaseInterSendJitter = TimeSpan.FromMilliseconds(100), JitterRatioBegin = 0.4, JitterRatioEnd = 0.9 };
        var set = new TokenBucketRateLimiterSet(clock, options);
        double min = double.MaxValue;
        double max = double.MinValue;
        for (int i = 0; i < 1000; i++)
        {
            TimeSpan d = set.ApplyJitter();
            double ms = d.TotalMilliseconds;
            min = Math.Min(min, ms);
            max = Math.Max(max, ms);
            Assert.InRange(ms, 40.0 - 0.001, 90.0 + 0.001);
        }
    }
}