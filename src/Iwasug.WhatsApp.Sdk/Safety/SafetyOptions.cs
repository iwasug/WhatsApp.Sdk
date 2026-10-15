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

    /// <summary>
    /// Time zone interpreting the active-hours window; <c>null</c> means UTC.
    /// The gate converts the UTC clock into this zone before comparing against
    /// <see cref="ActiveHoursStart"/>/<see cref="ActiveHoursEnd"/>.
    /// </summary>
    public TimeZoneInfo? ActiveHoursTimeZone { get; init; }

    /// <summary>Whether to queue sends during inactive hours.</summary>
    public bool QueueDuringInactiveHours { get; init; } = false;

    /// <summary>Whether unsafe throughput is explicitly allowed.</summary>
    public bool AllowUnsafeThroughput { get; init; } = false;

    /// <summary>
    /// Explicit host acknowledgment required when <see cref="AllowUnsafeThroughput"/>
    /// is <c>true</c>. The host declares it has read the README Ban-Safety risk
    /// ledger and accepts the elevated restriction risk. Must be non-empty when
    /// opting into unsafe throughput; ignored otherwise.
    /// </summary>
    public string? DeclaredUnsafeAcknowledgment { get; init; }

    /// <summary>Base inter-send jitter.</summary>
    public TimeSpan BaseInterSendJitter { get; init; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>Throttle notice threshold. Delays at or beyond this surface a throttle notice.</summary>
    public TimeSpan ThrottleNoticeThreshold { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Minimum human-like presence pause in milliseconds (typing/recording before send).</summary>
    public int PacingPresenceMinMs { get; init; } = 2000;

    /// <summary>Maximum human-like presence pause in milliseconds (typing/recording before send).</summary>
    public int PacingPresenceMaxMs { get; init; } = 12000;

    /// <summary>Maximum bytes for an image upload.</summary>
    public long MaxImageBytes { get; init; } = 16777216;

    /// <summary>Maximum bytes for a document upload.</summary>
    public long MaxDocumentBytes { get; init; } = 67108864;

    /// <summary>Maximum bytes for an audio/voice-note upload.</summary>
    public long MaxAudioBytes { get; init; } = 16777216;

    /// <summary>Maximum bytes for a thumbnail payload.</summary>
    public long MaxThumbnailBytes { get; init; } = 65536;

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

        if (resolved.MaxMessagesPerMinute <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxMessagesPerMinute must be positive.");
        }

        if (resolved.MaxMessagesPerHour <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxMessagesPerHour must be positive.");
        }

        if (resolved.MaxMessagesPerDay <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxMessagesPerDay must be positive.");
        }

        if (resolved.MaxColdOutreachPerDay < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxColdOutreachPerDay must not be negative.");
        }

        if (resolved.BaseInterSendJitter < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "BaseInterSendJitter must not be negative.");
        }

        if (resolved.ThrottleNoticeThreshold < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ThrottleNoticeThreshold must not be negative.");
        }

        if (resolved.PacingPresenceMinMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Pacing presence minimum must not be negative.");
        }

        if (resolved.PacingPresenceMaxMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Pacing presence maximum must not be negative.");
        }

        if (resolved.PacingPresenceMinMs > resolved.PacingPresenceMaxMs)
        {
            // Clamp (do not throw): collapse the inverted range onto the minimum so
            // pacing stays bounded without rejecting the host configuration.
            resolved = resolved with { PacingPresenceMaxMs = resolved.PacingPresenceMinMs };
        }

        if (resolved.MaxImageBytes <= 0 || resolved.MaxDocumentBytes <= 0 || resolved.MaxAudioBytes <= 0 || resolved.MaxThumbnailBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Media byte caps must be positive.");
        }

        if (!resolved.AllowUnsafeThroughput)
        {
            if (resolved.MaxMessagesPerMinute > 60)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "MaxMessagesPerMinute exceeds the safe threshold of 60 per minute. Set AllowUnsafeThroughput with an explicit acknowledgment to exceed safe throughput.");
            }

            if (resolved.MaxMessagesPerHour > 600)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "MaxMessagesPerHour exceeds the safe threshold of 600 per hour. Set AllowUnsafeThroughput with an explicit acknowledgment to exceed safe throughput.");
            }

            if (resolved.MaxMessagesPerDay > 1000)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "MaxMessagesPerDay exceeds the safe threshold of 1000 per day. Set AllowUnsafeThroughput with an explicit acknowledgment to exceed safe throughput.");
            }

            if (resolved.MaxImageBytes > 16777216)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "MaxImageBytes exceeds the safe threshold. Set AllowUnsafeThroughput with an explicit acknowledgment to exceed safe throughput.");
            }

            if (resolved.MaxDocumentBytes > 67108864)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "MaxDocumentBytes exceeds the safe threshold. Set AllowUnsafeThroughput with an explicit acknowledgment to exceed safe throughput.");
            }

            if (resolved.MaxAudioBytes > 16777216)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "MaxAudioBytes exceeds the safe threshold. Set AllowUnsafeThroughput with an explicit acknowledgment to exceed safe throughput.");
            }

            if (resolved.MaxThumbnailBytes > 65536)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "MaxThumbnailBytes exceeds the safe threshold. Set AllowUnsafeThroughput with an explicit acknowledgment to exceed safe throughput.");
            }
        }
        else if (string.IsNullOrWhiteSpace(resolved.DeclaredUnsafeAcknowledgment))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "DeclaredUnsafeAcknowledgment is required when AllowUnsafeThroughput is true. Declare acknowledgment of the Ban-Safety risk ledger.");
        }

        return resolved;
    }
}
