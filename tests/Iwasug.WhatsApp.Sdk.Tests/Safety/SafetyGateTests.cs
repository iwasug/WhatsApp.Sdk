using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Safety;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Safety;

public sealed class SafetyGateTests
{
    [Fact]
    public void defaults_are_conservative()
    {
        SafetyOptions s = SafetyOptions.SafeDefaults;
        s.MaxMessagesPerMinute.Should().Be(20);
        s.MaxMessagesPerHour.Should().Be(250);
        s.MaxMessagesPerDay.Should().Be(1000);
        s.MaxColdOutreachPerDay.Should().Be(30);
        s.JitterRatioBegin.Should().Be(0.4);
        s.JitterRatioEnd.Should().Be(0.9);
        s.AllowUnsafeThroughput.Should().BeFalse();
    }

    [Fact]
    public void validate_null_resolves_defaults()
    {
        SafetyOptions resolved = SafetyOptions.Validate(null);
        resolved.Should().BeEquivalentTo(SafetyOptions.SafeDefaults);
    }

    [Fact]
    public void inverted_jitter_throws()
    {
        var act = () => SafetyOptions.Validate(new SafetyOptions { JitterRatioBegin = 0.9, JitterRatioEnd = 0.4 });
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void unsafe_threshold_throws_unless_allowed()
    {
        var act = () => SafetyOptions.Validate(new SafetyOptions { MaxMessagesPerMinute = 200, AllowUnsafeThroughput = false });
        act.Should().Throw<ArgumentOutOfRangeException>();

        var allowed = SafetyOptions.Validate(new SafetyOptions { MaxMessagesPerMinute = 200, AllowUnsafeThroughput = true, DeclaredUnsafeAcknowledgment = "Test acknowledges the risk ledger." });
        allowed.MaxMessagesPerMinute.Should().Be(200);
    }
}
