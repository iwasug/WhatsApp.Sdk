using Iwasug.WhatsApp.Sdk.Errors;

namespace Iwasug.WhatsApp.Sdk.Pairing;

/// <summary>
/// Why a pairing-code request rejected its phone-number argument (CONN-02,
/// 03-RESEARCH.md Open Question 6): the digits-only E.164-without-<c>+</c>
/// contract, surfaced to hosts through <see cref="PairingCodeRequestException"/>.
/// </summary>
/// <remarks>
/// <para>
/// This enum is part of the public API — hosts catch <see cref="PairingCodeRequestException"/>
/// to determine the validation failure reason.
/// </para>
/// </remarks>
public enum PairingCodeValidationReason
{
    /// <summary>The input contains non-digit characters (including a leading '+').</summary>
    NonDigits,

    /// <summary>The input has six or fewer digits — too short to be a dialable E.164 number.</summary>
    TooShort,

    /// <summary>The input starts with '0' — E.164 numbers never have a leading zero.</summary>
    LeadingZero,
}

/// <summary>
/// The pairing-code request's phone number violated its input contract: the
/// request never reached generation or the wire. The message names the rule
/// only — the offending input is never embedded.
/// </summary>
public sealed class PairingCodeRequestException : SdkException
{
    /// <summary>Creates a pairing-code validation failure (SDK-thrown; hosts catch).</summary>
    /// <param name="reason">The failing rule.</param>
    /// <param name="message">The violated rule — the offending value itself is never embedded.</param>
    internal PairingCodeRequestException(PairingCodeValidationReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PairingCodeRequestException"/> class.
    /// </summary>
    /// <param name="message">Rule/operation description — no sensitive payload values.</param>
    /// <param name="inner">Optional inner exception (e.g. the underlying store error).</param>
    public PairingCodeRequestException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }

    /// <summary>The failing rule.</summary>
    public PairingCodeValidationReason Reason { get; }
}
