using Iwasug.WhatsApp.Sdk.Errors;

namespace Iwasug.WhatsApp.Sdk.Accounts;

/// <summary>
/// The per-account namespace key (D-25): an opaque, validated value type wrapping a
/// host-chosen string (CRM ID, tenant ID, phone — the host's choice). The SDK treats the
/// value as opaque; the mapping to WhatsApp identity lives in the identity store, not in
/// this key.
/// </summary>
/// <remarks>
/// <para>
/// Uniqueness (D-26) is enforced by the SDK account registry, not by this type — two
/// equal <see cref="AccountId"/> values denote the same account by definition. The host
/// provides the id when adding an account; the SDK validates and rejects duplicates.
/// </para>
/// <para>
/// Construction is validation-only: the only way to produce a value is <see cref="Create"/>,
/// so invalid ids cannot enter the system. (<c>default(AccountId)</c> exists as with any
/// struct, but is detectable by its null <see cref="Value"/> and is never produced by the SDK.)
/// </para>
/// <para>
/// Equality and comparison are case-sensitive ordinal — hosts' backends differ in
/// collation, so the SDK must not fold case ("a" and "A" are distinct accounts).
/// </para>
/// </remarks>
public readonly record struct AccountId : IComparable<AccountId>
{
    /// <summary>Maximum length of a host-chosen account id, in characters.</summary>
    public const int MaxLength = 128;

    /// <summary>The host-chosen, opaque account id value.</summary>
    public string Value { get; }

    private AccountId(string value) => Value = value;

    /// <summary>
    /// Validates <paramref name="value"/> and creates an <see cref="AccountId"/>.
    /// </summary>
    /// <param name="value">Host-chosen account id. Must be non-null, non-empty, not
    /// whitespace-only, free of control characters, and at most <see cref="MaxLength"/>
    /// characters long.</param>
    /// <returns>A validated account id.</returns>
    /// <exception cref="SdkFormatException">
    /// <paramref name="value"/> violates one of the rules above. The message names the
    /// violated rule only — it never embeds the value itself (message discipline, T-01-01).
    /// </exception>
    public static AccountId Create(string value)
    {
        if (value is null)
        {
            throw new SdkFormatException("AccountId value must not be null.");
        }

        if (value.Length == 0)
        {
            throw new SdkFormatException("AccountId value must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new SdkFormatException("AccountId value must not be whitespace-only.");
        }

        if (value.Length > MaxLength)
        {
            throw new SdkFormatException(
                $"AccountId value must not exceed the maximum length of {MaxLength} characters.");
        }

        if (ContainsControlCharacter(value))
        {
            throw new SdkFormatException("AccountId value must not contain control characters.");
        }

        return new AccountId(value);
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Case-sensitive ordinal comparison (never culture-aware — see type remarks).</summary>
    public int CompareTo(AccountId other) => string.CompareOrdinal(Value, other.Value);

    private static bool ContainsControlCharacter(string value)
    {
        foreach (char c in value)
        {
            if (char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }
}
