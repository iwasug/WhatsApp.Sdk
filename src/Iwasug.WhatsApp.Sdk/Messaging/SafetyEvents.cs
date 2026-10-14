namespace Iwasug.WhatsApp.Sdk.Messaging;

/// <summary>
/// Safety event vocabulary. Handler contract: return quickly; never call
/// <see cref="Connection.WaConnection.DisposeAsync"/> from the handler; exceptions
/// are logged and swallowed.
/// </summary>
public sealed record BudgetExhaustedEventArgs(
    string AccountKey,
    string Trigger,
    DateTimeOffset NextAllowedUtc,
    int SafetyStage);

/// <inheritdoc cref="BudgetExhaustedEventArgs"/>
public sealed record ThrottleEngagedEventArgs(
    string AccountKey,
    string Trigger,
    DateTimeOffset NextAllowedUtc,
    int SafetyStage);

/// <inheritdoc cref="BudgetExhaustedEventArgs"/>
public sealed record AccountRestrictedEventArgs(
    string AccountKey,
    string Trigger,
    DateTimeOffset NextAllowedUtc,
    int SafetyStage);

/// <summary>Stream twin of <see cref="BudgetExhaustedEventArgs"/>.</summary>
public sealed record BudgetExhaustedEvent(
    string AccountKey,
    string Trigger,
    DateTimeOffset NextAllowedUtc,
    int SafetyStage,
    DateTimeOffset Timestamp) : WhatsAppEvent(Timestamp);

/// <summary>Stream twin of <see cref="ThrottleEngagedEventArgs"/>.</summary>
public sealed record ThrottleEngagedEvent(
    string AccountKey,
    string Trigger,
    DateTimeOffset NextAllowedUtc,
    int SafetyStage,
    DateTimeOffset Timestamp) : WhatsAppEvent(Timestamp);

/// <summary>Stream twin of <see cref="AccountRestrictedEventArgs"/>.</summary>
public sealed record AccountRestrictedEvent(
    string AccountKey,
    string Trigger,
    DateTimeOffset NextAllowedUtc,
    int SafetyStage,
    DateTimeOffset Timestamp) : WhatsAppEvent(Timestamp);
