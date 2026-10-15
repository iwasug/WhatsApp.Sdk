namespace Iwasug.WhatsApp.Sdk.Accounts;

/// <summary>Bounded outbound queue entry for one account (payload-free placeholder in Wave 1).</summary>
internal sealed record OutboundEnvelope(AccountId AccountId, string? RuleName = null, object? Payload = null);