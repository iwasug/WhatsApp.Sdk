using Iwasug.WhatsApp.Sdk.Errors;

namespace Iwasug.WhatsApp.Sdk.Pairing;

/// <summary>
/// Phone-number validation outcome for the pairing-code request argument
/// (CONN-02, 03-RESEARCH.md Open Question 6): the digits-only E.164-without-<c>+</c>
/// contract, decided before any generation or wire work.
/// </summary>
internal enum PhoneNumberValidation
{
    /// <summary>The number satisfies the contract and may proceed to code generation.</summary>
    Valid,

    /// <summary>The input contains non-digit characters (including a leading '+').</summary>
    NonDigits,

    /// <summary>The input has six or fewer digits — too short to be a dialable E.164 number.</summary>
    TooShort,

    /// <summary>The input starts with '0' — E.164 numbers never have a leading zero.</summary>
    LeadingZero,
}

/// <summary>
/// Pairing-code generation and phone-number validation (CONN-02): five random
/// bytes map to eight characters over the Crockford alphabet, displayed as
/// <c>XXXX-XXXX</c> for the user to type on the phone.
/// </summary>
/// <remarks>
/// <para>
/// Behavior provenance (03-RESEARCH.md Code Example 2, which cites whatsmeow
/// <c>pair-code.go</c> behavioral read-only — no code copied): the alphabet
/// <c>123456789ABCDEFGHJKLMNPQRSTVWXYZ</c> (32 symbols, no I/L/O/U), five random
/// bytes, PBKDF2-SHA256 with 131072 iterations over the ASCII code. The C# below
/// is independently written; the mapping is base32-style and re-derivable from
/// the worked examples.
/// </para>
/// <para>
/// Mapping (worked examples — verify by hand): the five bytes are one 40-bit
/// big-endian integer; each output character takes the next 5-bit group from the
/// most significant side, indexing <see cref="Alphabet"/>. All-zero bytes give
/// index 0 eight times, i.e. raw code <c>11111111</c> (display
/// <c>1111-1111</c>); all-<c>FF</c> bytes give index 31 eight times, i.e.
/// <c>ZZZZZZZZ</c>. Randomness is injected (platform CSPRNG in production), so
/// the pure mapping stays deterministic under test.
/// </para>
/// <para>
/// Secret hygiene (T-3-10/T-3-11): this type never logs (the coordinator is the
/// sole log site and never logs the code); validation messages name the rule
/// only, never the input.
/// </para>
/// </remarks>
internal static class PairingCodeGenerator
{
    /// <summary>
    /// The Crockford base32 alphabet for pairing codes — the single declared
    /// source (03-RESEARCH.md Pitfall 4: this constant drifts silently).
    /// </summary>
    internal const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTVWXYZ";

    /// <summary>Random bytes consumed per code (40 bits of entropy).</summary>
    internal const int CodeByteLength = 5;

    /// <summary>Characters per raw code (8 × 5 bits = the 40 input bits).</summary>
    internal const int CodeCharLength = 8;

    /// <summary>Minimum accepted phone-number digit count (exclusive — six or fewer rejects).</summary>
    internal const int MinPhoneDigits = 6;

    /// <summary>
    /// Maps five injected random bytes to the eight-character raw code.
    /// </summary>
    /// <param name="randomBytes">Randomness source: returns exactly five bytes per call.</param>
    /// <returns>The eight-character raw code (no hyphen — see <see cref="ToDisplay"/>).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="randomBytes"/> is null.</exception>
    /// <exception cref="SdkFormatException">The source returned anything but five bytes.</exception>
    internal static string GenerateCode(Func<int, byte[]> randomBytes)
    {
        ArgumentNullException.ThrowIfNull(randomBytes);
        byte[] bytes = randomBytes(CodeByteLength);
        if (bytes is null || bytes.Length != CodeByteLength)
        {
            throw new SdkFormatException("Pairing randomness must supply exactly five bytes per code.");
        }

        ulong value = ((ulong)bytes[0] << 32)
            | ((ulong)bytes[1] << 24)
            | ((ulong)bytes[2] << 16)
            | ((ulong)bytes[3] << 8)
            | bytes[4];
        Span<char> chars = stackalloc char[CodeCharLength];
        for (int i = 0; i < CodeCharLength; i++)
        {
            int shift = (CodeCharLength - 1 - i) * 5;
            chars[i] = Alphabet[(int)((value >> shift) & 31)];
        }

        return new string(chars);
    }

    /// <summary>
    /// Formats a raw eight-character code for display: <c>XXXX-XXXX</c> with the
    /// hyphen at index 4 (03-RESEARCH.md Open Question 6).
    /// </summary>
    /// <param name="rawCode">The eight-character code from <see cref="GenerateCode"/>.</param>
    /// <returns>The display string the host shows the user.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rawCode"/> is null.</exception>
    /// <exception cref="SdkFormatException">The code is not eight alphabet characters.</exception>
    internal static string ToDisplay(string rawCode)
    {
        ArgumentNullException.ThrowIfNull(rawCode);
        if (rawCode.Length != CodeCharLength)
        {
            throw new SdkFormatException("Pairing codes must be eight characters before display.");
        }

        foreach (char c in rawCode)
        {
            if (Alphabet.IndexOf(c, StringComparison.Ordinal) < 0)
            {
                throw new SdkFormatException("Pairing codes must use the Crockford alphabet only.");
            }
        }

        return string.Concat(rawCode.AsSpan(0, 4), "-", rawCode.AsSpan(4, 4));
    }

    /// <summary>
    /// Validates the <c>RequestPairingCodeAsync</c> phone-number argument
    /// (03-RESEARCH.md Open Question 6 contract): digits-only E.164 without
    /// '+', longer than six digits, no leading zero. Runs before any generation
    /// or wire work — malformed input never reaches the derivation or the wire.
    /// </summary>
    /// <param name="phoneNumber">The host-supplied number (never logged, never echoed).</param>
    /// <returns>The validation outcome — <c>Valid</c> or the first failing rule.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="phoneNumber"/> is null.</exception>
    internal static PhoneNumberValidation ValidatePhoneNumber(string? phoneNumber)
    {
        ArgumentNullException.ThrowIfNull(phoneNumber);
        foreach (char c in phoneNumber)
        {
            if (!char.IsAsciiDigit(c))
            {
                return PhoneNumberValidation.NonDigits;
            }
        }

        if (phoneNumber.Length <= MinPhoneDigits)
        {
            return PhoneNumberValidation.TooShort;
        }

        if (phoneNumber[0] == '0')
        {
            return PhoneNumberValidation.LeadingZero;
        }

        return PhoneNumberValidation.Valid;
    }
}
