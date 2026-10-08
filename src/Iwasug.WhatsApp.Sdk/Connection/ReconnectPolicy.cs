namespace Iwasug.WhatsApp.Sdk.Connection;

/// <summary>
/// Host-visible reconnect verdict for one drop: what the reconnect loop must do next.
/// Produced by <see cref="ReconnectPolicy.Decide"/> without a socket — every code has
/// an answer (CONN-04).
/// </summary>
/// <remarks>
/// Terminal and <see cref="SurfaceAndStop"/> both mean "no automatic retry", with
/// different host signals: <see cref="Terminal"/> = credentials or client dead
/// (re-pair or redeploy — the instance moves to <see cref="ConnectionState.LoggedOut"/>);
/// <see cref="SurfaceAndStop"/> = server-initiated takeover needing an explicit host
/// restart (the instance stays out of the reconnect loop until the host acts).
/// </remarks>
public abstract record ReconnectDecision
{
    private ReconnectDecision()
    {
    }

    /// <summary>
    /// Dead end: credentials dead or client outdated (401/403/411/419). The connection
    /// moves to <see cref="ConnectionState.LoggedOut"/> exactly once and the reconnect
    /// loop ends — no timer or delay path is reachable from this verdict (T-2-01).
    /// </summary>
    public sealed record Terminal : ReconnectDecision
    {
        /// <summary>The singleton terminal verdict (carries no data).</summary>
        public static Terminal Instance { get; } = new();

        private Terminal()
        {
        }
    }

    /// <summary>
    /// Wait <see cref="Delay"/> (full-jitter backoff, already clamped to the configured
    /// bounds), then reconnect with a fresh socket and fresh Noise handshake.
    /// </summary>
    /// <param name="Delay">
    /// How long the reconnect loop waits before the next attempt. Always within
    /// [zero, <see cref="ConnectionOptions.RetryMaxDelayMs"/>], under any jitter
    /// sequence or attempt count.
    /// </param>
    public sealed record RetryAfter(TimeSpan Delay) : ReconnectDecision;

    /// <summary>
    /// Restart immediately (515): fresh socket + fresh Noise handshake (fresh ephemeral —
    /// never reuse <c>e</c>) with no delay, and the backoff attempt counter resets.
    /// </summary>
    public sealed record RestartImmediate : ReconnectDecision
    {
        /// <summary>The singleton restart verdict (carries no data).</summary>
        public static RestartImmediate Instance { get; } = new();

        private RestartImmediate()
        {
        }
    }

    /// <summary>
    /// Stop without retrying and surface to the host (440 replaced): immediate
    /// reconnect would start a kick-war between devices. The host restarts explicitly.
    /// </summary>
    public sealed record SurfaceAndStop : ReconnectDecision
    {
        /// <summary>The singleton surface-and-stop verdict (carries no data).</summary>
        public static SurfaceAndStop Instance { get; } = new();

        private SurfaceAndStop()
        {
        }
    }
}

/// <summary>
/// Pure status-code → <see cref="ReconnectDecision"/> table (CONN-04 core table,
/// 02-RESEARCH Disconnect Taxonomy): 401/403/411/419 terminal, 408/428/500/503 jittered
/// retry, 515 immediate restart, 440 surface-and-stop, unknown bounded retry.
/// </summary>
/// <remarks>
/// <para>
/// Pure function: no I/O, no timers, no logging — exhaustively unit-testable without
/// sockets. The one impurity is the jitter draw (<see cref="Random.Shared"/>), which is
/// inherent to backoff and bounded by construction. Because this function cannot log,
/// callers MUST log unknown codes loudly (warning with the code) — an unknown code is a
/// Phase 9 drift signal, not silent handling.
/// </para>
/// <para>
/// Delay math reuses the Phase 1 clamp-after-jitter discipline (DurableWriteRetryPolicy,
/// D-18) with the FULL-jitter formula from the AWS canonical reference (02-RESEARCH
/// Pattern 4): <c>delay = uniform(0, min(cap, base * 2^attempt))</c>, clamped to
/// <c>[0, cap]</c> after the draw. Delay bounds live in <see cref="ConnectionOptions"/>
/// (<see cref="ConnectionOptions.RetryBaseDelayMs"/> /
/// <see cref="ConnectionOptions.RetryMaxDelayMs"/>).
/// </para>
/// <para>
/// Attempt counting is the caller's job: <c>attempt</c> is the zero-based
/// count of consecutive failed attempts. The caller resets it on
/// <see cref="ConnectionState.Open"/> and on <see cref="ReconnectDecision.RestartImmediate"/>.
/// </para>
/// </remarks>
public static class ReconnectPolicy
{
    /// <summary>
    /// Answers one drop: maps <paramref name="statusCode"/> to the reconnect verdict,
    /// computing the full-jitter delay for retryable codes from <paramref name="attempt"/>.
    /// </summary>
    /// <param name="statusCode">
    /// Server-sent disconnect status code (the numeric <see cref="DisconnectReason"/>
    /// value; any other integer is an unknown/drift code and gets a bounded retry).
    /// </param>
    /// <param name="attempt">
    /// Zero-based count of consecutive failed attempts. Must not be negative.
    /// </param>
    /// <param name="options">
    /// Backoff bounds; <c>null</c> resolves to <see cref="ConnectionOptions"/> defaults.
    /// Invalid bounds throw before any delay is computed.
    /// </param>
    /// <returns>
    /// <see cref="ReconnectDecision.Terminal"/> for 401/403/411/419 (never retry —
    /// 401/419 must not loop against dead credentials, T-2-01);
    /// <see cref="ReconnectDecision.RestartImmediate"/> for 515;
    /// <see cref="ReconnectDecision.SurfaceAndStop"/> for 440;
    /// <see cref="ReconnectDecision.RetryAfter"/> for 408/428/500/503 and every unknown
    /// code (bounded by <see cref="ConnectionOptions.RetryMaxDelayMs"/>; the caller logs
    /// unknown codes loudly).
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="attempt"/> is negative, or <paramref name="options"/> carries
    /// out-of-range bounds (see <see cref="ConnectionOptions"/>).
    /// </exception>
    public static ReconnectDecision Decide(int statusCode, int attempt, ConnectionOptions? options = null)
    {
        if (attempt < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt), "Attempt must not be negative.");
        }

        ConnectionOptions resolved = ConnectionOptions.ValidateOptions(options);

        return statusCode switch
        {
            401 or 403 or 411 or 419 => ReconnectDecision.Terminal.Instance,
            515 => ReconnectDecision.RestartImmediate.Instance,
            440 => ReconnectDecision.SurfaceAndStop.Instance,
            _ => new ReconnectDecision.RetryAfter(ComputeDelay(attempt, resolved)),
        };
    }

    /// <summary>
    /// Full-jitter backoff (AWS canonical, 02-RESEARCH Pattern 4) with clamp-after-jitter
    /// discipline (D-18): raw ceiling <c>min(cap, base * 2^attempt)</c> computed in
    /// <c>double</c> (no integer overflow at any attempt), uniform draw below it, then
    /// clamp to <c>[0, cap]</c> so the observed delay never leaves the configured bounds.
    /// </summary>
    private static TimeSpan ComputeDelay(int attempt, ConnectionOptions options)
    {
        double ceiling = Math.Min((double)options.RetryBaseDelayMs * Math.Pow(2, attempt), options.RetryMaxDelayMs);
        double drawn = Random.Shared.NextDouble() * ceiling;
        return TimeSpan.FromMilliseconds(Math.Clamp(drawn, 0, options.RetryMaxDelayMs));
    }
}
