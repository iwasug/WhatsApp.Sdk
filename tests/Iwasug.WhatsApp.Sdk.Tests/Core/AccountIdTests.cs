using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Errors;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Core;

/// <summary>
/// Unit tests for the A4 AccountId validation rule: case-sensitive ordinal identity,
/// non-empty, no whitespace-only, no control characters, 128-character cap.
/// </summary>
public sealed class AccountIdTests
{
    [Fact]
    public void Create_rejects_null()
    {
        Func<AccountId> create = () => AccountId.Create(null!);

        create.Should().Throw<SdkFormatException>();
    }

    [Fact]
    public void Create_rejects_empty_string()
    {
        Func<AccountId> create = () => AccountId.Create(string.Empty);

        create.Should().Throw<SdkFormatException>();
    }

    [Fact]
    public void Create_rejects_whitespace_only_string()
    {
        Func<AccountId> create = () => AccountId.Create("   \t  ");

        create.Should().Throw<SdkFormatException>();
    }

    [Fact]
    public void Create_rejects_control_characters()
    {
        Func<AccountId> create = () => AccountId.Create("acct\tname\n");

        create.Should().Throw<SdkFormatException>();
    }

    [Fact]
    public void Create_rejects_values_longer_than_128_characters()
    {
        Func<AccountId> create = () => AccountId.Create(new string('x', 129));

        create.Should().Throw<SdkFormatException>();
    }

    [Fact]
    public void Create_accepts_values_of_exactly_128_characters()
    {
        var id = AccountId.Create(new string('x', 128));

        id.Value.Should().HaveLength(128);
    }

    [Fact]
    public void Distinct_ordinal_casing_produces_distinct_account_ids()
    {
        var lower = AccountId.Create("a");
        var upper = AccountId.Create("A");

        lower.Should().NotBe(upper);
        lower.Equals(upper).Should().BeFalse();
        lower.CompareTo(upper).Should().NotBe(0);
    }

    [Fact]
    public void ToString_round_trips_the_value()
    {
        var id = AccountId.Create("crm-42");

        id.ToString().Should().Be("crm-42");
        id.Value.Should().Be("crm-42");
    }

    [Fact]
    public void CompareTo_is_ordinal_not_culture_aware()
    {
        // Ordinal: 'B' (0x42) < 'a' (0x61). A culture-aware comparison would sort "a" first.
        AccountId.Create("B").CompareTo(AccountId.Create("a")).Should().BeNegative();
        AccountId.Create("a").CompareTo(AccountId.Create("b")).Should().BeNegative();
        AccountId.Create("same").CompareTo(AccountId.Create("same")).Should().Be(0);
    }

    [Fact]
    public void Equal_values_compare_equal()
    {
        AccountId.Create("tenant-7").Should().Be(AccountId.Create("tenant-7"));
        (AccountId.Create("tenant-7") == AccountId.Create("tenant-7")).Should().BeTrue();
    }

    [Fact]
    public void Exception_messages_do_not_embed_the_offending_value()
    {
        // Message discipline (T-01-01): the rule is named, the payload value is not.
        Func<AccountId> act = () => AccountId.Create("secret-value\r\n");

        act.Should().Throw<SdkFormatException>()
            .Where(ex => !ex.Message.Contains("secret-value", StringComparison.Ordinal));
    }
}
