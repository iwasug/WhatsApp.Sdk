using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Safety;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Safety;

/// <summary>
/// SAF-07 safe-by-default validation (05-05 Task 1): defaults accepted, inverted
/// jitter rejected, unsafe throughput rejected without opt-in, accepted with
/// opt-in plus explicit acknowledgment, missing acknowledgment rejected.
/// </summary>
public sealed class SafetyOptionsValidationTests
{
    private const string Ack = "Test host acknowledges the Ban-Safety risk ledger.";

    [Fact]
    public void defaults_accepted()
    {
        SafetyOptions resolved = SafetyOptions.Validate(null);
        resolved.Should().BeEquivalentTo(SafetyOptions.SafeDefaults);

        var act = () => SafetyOptions.Validate(SafetyOptions.SafeDefaults);
        act.Should().NotThrow();
    }

    [Fact]
    public void inverted_jitter_rejected()
    {
        var act = () => SafetyOptions.Validate(new SafetyOptions { JitterRatioBegin = 0.9, JitterRatioEnd = 0.4 });
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void unsafe_rejected_without_opt_in()
    {
        var minute = () => SafetyOptions.Validate(new SafetyOptions { MaxMessagesPerMinute = 61 });
        minute.Should().Throw<ArgumentOutOfRangeException>();

        var hour = () => SafetyOptions.Validate(new SafetyOptions { MaxMessagesPerHour = 601 });
        hour.Should().Throw<ArgumentOutOfRangeException>();

        var day = () => SafetyOptions.Validate(new SafetyOptions { MaxMessagesPerDay = 1001 });
        day.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void unsafe_accepted_with_ack()
    {
        SafetyOptions resolved = SafetyOptions.Validate(new SafetyOptions
        {
            AllowUnsafeThroughput = true,
            DeclaredUnsafeAcknowledgment = Ack,
            MaxMessagesPerMinute = 1000,
            MaxMessagesPerHour = 1000,
            MaxMessagesPerDay = 1000,
        });

        resolved.MaxMessagesPerMinute.Should().Be(1000);
        resolved.AllowUnsafeThroughput.Should().BeTrue();
    }

    [Fact]
    public void missing_ack_rejected()
    {
        var nullAck = () => SafetyOptions.Validate(new SafetyOptions
        {
            AllowUnsafeThroughput = true,
            DeclaredUnsafeAcknowledgment = null,
            MaxMessagesPerMinute = 1000,
        });
        nullAck.Should().Throw<ArgumentOutOfRangeException>();

        var emptyAck = () => SafetyOptions.Validate(new SafetyOptions
        {
            AllowUnsafeThroughput = true,
            DeclaredUnsafeAcknowledgment = string.Empty,
            MaxMessagesPerMinute = 1000,
        });
        emptyAck.Should().Throw<ArgumentOutOfRangeException>();

        var whitespaceAck = () => SafetyOptions.Validate(new SafetyOptions
        {
            AllowUnsafeThroughput = true,
            DeclaredUnsafeAcknowledgment = "   ",
            MaxMessagesPerMinute = 1000,
        });
        whitespaceAck.Should().Throw<ArgumentOutOfRangeException>();
    }
}
