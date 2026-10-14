namespace Iwasug.WhatsApp.Sdk.Safety;

/// <summary>Safe-by-default safety configuration.</summary>
public sealed record SafetyOptions
{
    /// <inheritdoc cref="SafetyOptions"/>
    public SafetyOptions() { }

    /// <summary>Maximum messages per minute.</summary>
    public int MaxMessagesPerMinute { get; init; } = 20;

    /// <summary>Maximum messages per hour.</summary>
    public int MaxMessagesPerHour { get; init; } = 250;

    /// <summary>Maximum messages per day.</summary>
    public int MaxMessagesPerDay { get; init; } = 1000;

    /// <summary>Maximum cold-outreach messages per day.</summary>
    public int MaxColdOutreachPerDay { get; init; } = 30;

    /// <summary>Jitter ratio begin.</summary>
    public double JitterRatioBegin { get; init; } = 0.4;

    /// <summary>Jitter ratio end.</summary>
    public double JitterRatioEnd { get; init; } = 0.9;

    /// <summary>Active hours start.</summary>
    public TimeSpan ActiveHoursStart { get; init; } = TimeSpan.Zero;

    /// <summary>Active hours end.</summary>
    public TimeSpan ActiveHoursEnd { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Whether to queue sends during inactive hours.</summary>
    public bool QueueDuringInactiveHours { get; init; } = false;

    /// <summary>Whether unsafe throughput is explicitly allowed.</summary>
    public bool AllowUnsafeThroughput { get; init; } = false;

    /// <summary>Base inter-send jitter.</summary>
    public TimeSpan BaseInterSendJitter { get; init; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>Throttle notice threshold. Delays at or beyond this surface a throttle notice.</summary>
    public TimeSpan ThrottleNoticeThreshold { get; init; } = TimeSpan.FromMilliseconds(500);
    /// <summary>Conservative defaults.</summary>
    public static SafetyOptions SafeDefaults => new();

    /// <summary>Resolves null to defaults and validates bounds before any I/O.</summary>
    internal static SafetyOptions Validate(SafetyOptions? options)
    {
        SafetyOptions resolved = options ?? SafeDefaults;

        if (resolved.JitterRatioBegin > resolved.JitterRatioEnd)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Jitter ratio bounds are inverted.");
        }

        if (!resolved.AllowUnsafeThroughput)
        {
            if (resolved.MaxMessagesPerMinute > 60)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "MaxMessagesPerMinute exceeds the safe threshold.");
            }

            if (resolved.MaxMessagesPerHour > 300)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "MaxMessagesPerHour exceeds the safe threshold.");
            }

            if (resolved.MaxMessagesPerDay > 1000)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "MaxMessagesPerDay exceeds the safe threshold.");
            }
        }

        return resolved;
    }
}
