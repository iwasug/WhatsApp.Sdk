namespace Iwasug.WhatsApp.Sdk.Accounts;

/// <summary>Bounded outbound queue entry for one account — carries the account id plus optional rule name and payload.</summary>
internal sealed record OutboundEnvelope(AccountId AccountId, string? RuleName = null, object? Payload = null);