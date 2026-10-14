namespace Iwasug.WhatsApp.Sdk.Safety;

/// <summary>
/// Breaker position of <see cref="CircuitBreaker"/> (05-04, SAF-05): the
/// in-process send-decision state, not an HTTP status. Polly stays HTTP-only
/// per <c>STACK.md</c> — this machine takes its signal from
/// <see cref="Messaging.SendOutcome"/>.
/// </summary>
internal enum CircuitState
{
    /// <summary>Sends flow; failures accumulate toward the trip thresholds.</summary>
    Closed,

    /// <summary>All application sends refuse immediately until the backoff elapses.</summary>
    Open,

    /// <summary>Backoff elapsed: exactly one probe send is admitted to test recovery.</summary>
    HalfOpen,
}
