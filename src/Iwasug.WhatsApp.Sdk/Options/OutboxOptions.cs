namespace Iwasug.WhatsApp.Sdk.Options;

/// <summary>
/// SDK-owned policy for the durable per-account outbox pump (EXT-01, Pattern 2).
/// Retention, attempt ceilings, and backoff bounds live here, never in the store
/// interface (Phase-1 D-08 discipline).
/// </summary>
internal sealed record OutboxOptions
{
    /// <summary>Maximum rows loaded per pump pass.</summary>
    public int MaxBatchSize { get; init; } = 10;

    /// <summary>Maximum attempts before an entry is dead-lettered (>= 1).</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>Initial backoff delay applied after the first failure.</summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Upper bound on a computed backoff delay.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Poll interval used by the continuous pump loop.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Poll floor / minimum spacing between pump passes.</summary>
    public TimeSpan PollFloor { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Delay applied to a store-side retry after an active-hours violation.</summary>
    public TimeSpan InactiveHoursRetryDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Safe defaults.</summary>
    public static OutboxOptions SafeDefaults => new();

    /// <summary>Validates and returns the resolved options (throws on invalid bounds).</summary>
    public static OutboxOptions Validate(OutboxOptions? options)
    {
        var resolved = options ?? SafeDefaults;
        if (resolved.MaxBatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxBatchSize must be positive.");
        }

        if (resolved.MaxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxAttempts must be positive.");
        }

        if (resolved.InitialBackoff < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "InitialBackoff must not be negative.");
        }

        if (resolved.MaxBackoff < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxBackoff must not be negative.");
        }

        if (resolved.PollInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "PollInterval must not be negative.");
        }

        if (resolved.PollFloor < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "PollFloor must not be negative.");
        }

        return resolved;
    }
}
