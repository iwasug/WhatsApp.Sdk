using System.Globalization;

namespace Iwasug.WhatsApp.Sdk.InteropProbe;

/// <summary>
/// The probe's argument contract (09-03 Task 3): exactly one option,
/// <c>--timeout-seconds &lt;n&gt;</c> — default 120, valid range 1..900.
/// Anything else is a contract failure that throws
/// <see cref="ProbeOptionsException"/>; the entry point turns it into one
/// stderr message and exit code 2, disjoint from the probe outcome
/// vocabulary (0/10/20).
/// </summary>
public sealed class ProbeOptions
{
    /// <summary>The bound used when no option is passed — the live workflow's default.</summary>
    public const int DefaultTimeoutSeconds = 120;

    /// <summary>Shortest accepted bound.</summary>
    public const int MinTimeoutSeconds = 1;

    /// <summary>Longest accepted bound: the CI job must never sit longer than this.</summary>
    public const int MaxTimeoutSeconds = 900;

    private const string TimeoutOption = "--timeout-seconds";

    private ProbeOptions(int timeoutSeconds)
    {
        TimeoutSeconds = timeoutSeconds;
    }

    /// <summary>The validated bound, in seconds.</summary>
    public int TimeoutSeconds { get; }

    /// <summary>The validated bound as a duration for the runner.</summary>
    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);

    /// <summary>
    /// Parses the process arguments. No arguments yields the defaults; every
    /// other token must be the documented option with an in-range value.
    /// </summary>
    /// <param name="args">The raw process arguments.</param>
    /// <returns>The validated options.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is null.</exception>
    /// <exception cref="ProbeOptionsException">
    /// An unknown argument, a missing value, or a value that is not an integer
    /// in 1..900.
    /// </exception>
    public static ProbeOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        int timeoutSeconds = DefaultTimeoutSeconds;
        for (int i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], TimeoutOption, StringComparison.Ordinal))
            {
                throw new ProbeOptionsException(
                    $"Unknown argument '{args[i]}'. The only supported option is {TimeoutOption} <seconds>.");
            }

            if (i + 1 >= args.Count)
            {
                throw new ProbeOptionsException($"Missing value for {TimeoutOption}.");
            }

            string raw = args[++i];
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
            {
                throw new ProbeOptionsException(
                    $"{TimeoutOption} must be a whole number of seconds, got '{raw}'.");
            }

            if (seconds < MinTimeoutSeconds || seconds > MaxTimeoutSeconds)
            {
                throw new ProbeOptionsException(
                    $"{TimeoutOption} must be between {MinTimeoutSeconds} and {MaxTimeoutSeconds}, got {seconds}.");
            }

            timeoutSeconds = seconds;
        }

        return new ProbeOptions(timeoutSeconds);
    }
}

/// <summary>
/// The probe's argument-contract failure: the entry point writes its message
/// to stderr and exits 2 — never one of the probe outcome codes.
/// </summary>
public sealed class ProbeOptionsException : Exception
{
    /// <summary>Creates the failure with the operator-facing message.</summary>
    /// <param name="message">The material-free parse-failure message.</param>
    public ProbeOptionsException(string message)
        : base(message)
    {
    }
}
