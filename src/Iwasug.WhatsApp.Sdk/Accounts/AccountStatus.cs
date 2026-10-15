namespace Iwasug.WhatsApp.Sdk.Accounts;

/// <summary>Breaker position exposed on the account status snapshot.</summary>
public enum SafetyCircuitState
{
    /// <summary>Closed.</summary>
    Closed,
    /// <summary>Open.</summary>
    Open,
    /// <summary>Half-open.</summary>
    HalfOpen,
}

/// <summary>Immutable per-account status snapshot (rule-only: no payloads/JIDs/store content).</summary>
public sealed record AccountStatus(
    AccountId AccountId,
    Connection.ConnectionState ConnectionState,
    SafetyCircuitState CircuitState,
    int QueuedOutbound,
    int RecentFailures,
    DateTimeOffset? LastConnectedAt,
    DateTimeOffset? LastActivityAt,
    string? LastErrorRuleName);