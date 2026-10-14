using Iwasug.WhatsApp.Sdk.Domain;

using Iwasug.WhatsApp.Sdk.Errors;

namespace Iwasug.WhatsApp.Sdk.Safety;

/// <summary>
/// Safety-rule identifiers for violations surfaced by <see cref="ISafetyGate"/>.
/// </summary>
public enum SafetyRule
{
    /// <summary>Per-account rate budget exhausted.</summary>
    RateLimit,

    /// <summary>Send attempted outside configured active hours.</summary>
    InactiveHours,

    /// <summary>First-contact (cold) outreach budget exhausted.</summary>
    ColdOutreach,

    /// <summary>The account circuit breaker is open.</summary>
    CircuitOpen,
}

/// <summary>
/// Thrown when a stage of <see cref="SafetyGate"/> rejects a send. Carries the
/// violated rule only — no JID, no threshold, no payload values (T-05-03).
/// </summary>
public sealed class SafetyRuleViolationException : SdkException
{
    /// <summary>The rule that was violated.</summary>
    public SafetyRule Rule { get; }

    /// <summary>Initializes a new instance carrying the violated <paramref name="rule"/> and a rule-only message.</summary>
    public SafetyRuleViolationException(SafetyRule rule, string message, Exception? inner = null)
        : base(message, inner)
    {
        Rule = rule;
    }

}
