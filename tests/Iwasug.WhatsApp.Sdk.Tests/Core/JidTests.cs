using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Core;

/// <summary>
/// Unit tests for D-16 (Jid as an immutable parsed value type) and the resolved
/// empty-input edge: Parse throws SdkFormatException on null/empty/no-@ input and
/// TryParse returns false — never a silently-constructed Jid (T-01-06).
/// </summary>
public sealed class JidTests
{
    [Fact]
    public void Parse_splits_user_and_server_for_a_simple_jid()
    {
        var jid = Jid.Parse("123@s.whatsapp.net");

        jid.User.Should().Be("123");
        jid.Server.Should().Be("s.whatsapp.net");
        jid.Agent.Should().BeNull();
        jid.Device.Should().BeNull();
    }

    [Fact]
    public void Parse_extracts_agent_and_device_components()
    {
        var jid = Jid.Parse("123.4.5@s.whatsapp.net");

        jid.User.Should().Be("123");
        jid.Agent.Should().Be("4");
        jid.Device.Should().Be(5);
        jid.Server.Should().Be("s.whatsapp.net");
    }

    [Fact]
    public void Parse_extracts_agent_without_device()
    {
        var jid = Jid.Parse("123.4@s.whatsapp.net");

        jid.User.Should().Be("123");
        jid.Agent.Should().Be("4");
        jid.Device.Should().BeNull();
    }

    [Fact]
    public void TryParse_returns_false_on_null()
    {
        bool parsed = Jid.TryParse(null!, out Jid? jid);

        parsed.Should().BeFalse();
        jid.Should().BeNull();
    }

    [Fact]
    public void TryParse_returns_false_on_empty_string()
    {
        bool parsed = Jid.TryParse(string.Empty, out Jid? jid);

        parsed.Should().BeFalse();
        jid.Should().BeNull();
    }

    [Fact]
    public void TryParse_returns_false_without_at_separator()
    {
        bool parsed = Jid.TryParse("123.s.whatsapp.net", out Jid? jid);

        parsed.Should().BeFalse();
        jid.Should().BeNull();
    }

    [Fact]
    public void Parse_throws_on_null()
    {
        Func<Jid> parse = () => Jid.Parse(null!);

        parse.Should().Throw<SdkFormatException>();
    }

    [Fact]
    public void Parse_throws_on_empty_string()
    {
        Func<Jid> parse = () => Jid.Parse(string.Empty);

        parse.Should().Throw<SdkFormatException>();
    }

    [Fact]
    public void Parse_throws_without_at_separator()
    {
        Func<Jid> parse = () => Jid.Parse("123.s.whatsapp.net");

        parse.Should().Throw<SdkFormatException>();
    }

    [Fact]
    public void Parse_throws_on_empty_user_part()
    {
        Func<Jid> parse = () => Jid.Parse("@s.whatsapp.net");

        parse.Should().Throw<SdkFormatException>();
    }

    [Fact]
    public void Parse_throws_on_empty_server_part()
    {
        Func<Jid> parse = () => Jid.Parse("123@");

        parse.Should().Throw<SdkFormatException>();
    }

    [Fact]
    public void ToString_round_trips_canonical_inputs()
    {
        foreach (string? canonical in new[]
        {
            "123@s.whatsapp.net",
            "123.4@s.whatsapp.net",
            "123.4.5@s.whatsapp.net",
            "lid-value.12.1@lid",
        })
        {
            Jid.Parse(canonical).ToString().Should().Be(canonical);
        }
    }

    [Fact]
    public void Canonical_string_comparison_is_case_sensitive()
    {
        var lower = Jid.Parse("abc@s.whatsapp.net");
        var upper = Jid.Parse("ABC@s.whatsapp.net");

        lower.Should().NotBe(upper);
        lower.Equals(upper).Should().BeFalse();
        (lower == upper).Should().BeFalse();
        lower.ToString().Should().NotBe(upper.ToString());
    }

    [Fact]
    public void Exception_messages_do_not_embed_the_offending_value()
    {
        // Message discipline (T-01-01/T-01-06): the rule is named, the payload is not.
        Func<Jid> act = () => Jid.Parse("secret-value");

        act.Should().Throw<SdkFormatException>()
            .Where(ex => !ex.Message.Contains("secret-value", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_rejects_zero_padded_device_as_non_canonical()
    {
        Func<Jid> parse = () => Jid.Parse("123.4.05@s.whatsapp.net");

        parse.Should().Throw<SdkFormatException>();
    }
}
