namespace Iwasug.WhatsApp.Sdk.Hosting;

using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Safety;

/// <summary>
/// Per-account configuration row for host-driven multi-account startup. Operator
/// intent is carried here; <see cref="AccountId"/> is validated (through
/// <see cref="Accounts.AccountId.Create"/>) at hosted-service start, never at bind time
/// of the host configuration.
/// </summary>
public sealed class AccountDescriptor
{
    /// <summary>Host-chosen account id string (validated via <see cref="Accounts.AccountId.Create"/> at start).</summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>Optional human-readable label. Never logged or persisted by the SDK.</summary>
    public string? Name { get; set; }

    /// <summary>Per-account connection overrides. Null falls back to <see cref="WhatsAppSdkOptions.DefaultConnection"/>.</summary>
    public ConnectionOptions? Connection { get; set; }

    /// <summary>Per-account safety overrides. Null falls back to <see cref="WhatsAppSdkOptions.DefaultSafety"/>.</summary>
    public SafetyOptions? Safety { get; set; }
}