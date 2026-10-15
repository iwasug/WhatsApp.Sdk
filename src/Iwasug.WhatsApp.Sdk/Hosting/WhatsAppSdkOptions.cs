namespace Iwasug.WhatsApp.Sdk.Hosting;

using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Safety;
using Iwasug.WhatsApp.Sdk.Stores;

/// <summary>
/// Root options object bound from host configuration (or configured via the
/// <c>AddWhatsAppSdk(Action&lt;WhatsAppSdkOptions&gt;)</c> overload). Carries the
/// account list, safe defaults, and host-level tuning.
/// </summary>
public sealed class WhatsAppSdkOptions
{
    /// <summary>Conservative defaults.</summary>
    public static WhatsAppSdkOptions SafeDefaults => new();

    /// <summary>Accounts to register at host start. Each row carries per-account overrides.</summary>
    public List<AccountDescriptor> Accounts { get; set; } = new();

    /// <summary>Default connection options applied when an account does not override them.</summary>
    public ConnectionOptions DefaultConnection { get; set; } = new ConnectionOptions();

    /// <summary>Default safety options applied when an account does not override them.</summary>
    public SafetyOptions DefaultSafety { get; set; } = SafetyOptions.SafeDefaults;

    /// <summary>Host-owned store factory seams, re-used for every account.</summary>
    public StoreRegistryOptions StoreFactories { get; set; } = new StoreRegistryOptions();

    /// <summary>Stagger between per-account starts, in milliseconds.</summary>
    public int StartupJitterMs { get; set; } = 100;

    /// <summary>Bounded drain window applied on host stop, in milliseconds.</summary>
    public int ShutdownDrainTimeoutMs { get; set; } = 5000;

    /// <summary>Whether accounts are auto-started with the host. Defaults to true.</summary>
    public bool AutoStartAccounts { get; set; } = true;

    /// <summary>Per-account outbound queue capacity.</summary>
    public int OutboundQueueCapacity { get; set; } = 128;

    /// <summary>Per-account event channel capacity.</summary>
    public int EventChannelCapacity { get; set; } = 128;

    /// <summary>
    /// Resolves null to defaults and validates bounds and sub-options before any I/O.
    /// </summary>
    public static WhatsAppSdkOptions Validate(WhatsAppSdkOptions? options)
    {
        WhatsAppSdkOptions resolved = options ?? new WhatsAppSdkOptions();

        if (resolved.Accounts is null)
        {
            resolved.Accounts = new List<AccountDescriptor>();
        }

        resolved.DefaultConnection = ConnectionOptions.ValidateOptions(resolved.DefaultConnection);
        resolved.DefaultSafety = SafetyOptions.Validate(resolved.DefaultSafety);

        if (resolved.StartupJitterMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "StartupJitterMs must not be negative.");
        }

        if (resolved.ShutdownDrainTimeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ShutdownDrainTimeoutMs must be positive.");
        }

        if (resolved.OutboundQueueCapacity <= 0)
        {
            resolved.OutboundQueueCapacity = 128;
        }

        if (resolved.EventChannelCapacity <= 0)
        {
            resolved.EventChannelCapacity = 128;
        }

        if (resolved.StoreFactories is null)
        {
            resolved.StoreFactories = new StoreRegistryOptions();
        }

        foreach (AccountDescriptor descriptor in resolved.Accounts!)
        {
            if (descriptor is null) throw new ArgumentNullException(nameof(options), "Account descriptors must not be null.");
            if (string.IsNullOrWhiteSpace(descriptor.AccountId))
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Account descriptor AccountId must not be empty.");
            }

            // Validate without surfacing the value (rule-only). AccountId.Create throws SdkFormatException on invalid.
            AccountId.Create(descriptor.AccountId);

            if (descriptor.Connection is not null)
            {
                ConnectionOptions.ValidateOptions(descriptor.Connection);
            }

            if (descriptor.Safety is not null)
            {
                SafetyOptions.Validate(descriptor.Safety);
            }
        }

        return resolved;
    }
}