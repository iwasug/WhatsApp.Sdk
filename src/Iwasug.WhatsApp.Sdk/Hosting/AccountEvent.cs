using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Messaging;

namespace Iwasug.WhatsApp.Sdk.Hosting;

/// <summary>
/// A manager-level event fused from a single account's event stream, carrying the
/// originating <see cref="Accounts.AccountId"/> tag verbatim (never rewritten or re-routed
/// by the fan-in). Wraps the existing WhatsApp event vocabulary without redefining
/// any of its members.
/// </summary>
public sealed record AccountEvent(AccountId AccountId, WhatsAppEvent Event);
