namespace Iwasug.WhatsApp.Sdk.Connection;

/// <summary>
/// Tuning surface for one account's connection: socket timeouts, query timeout, iq
/// keepalive cadence, and reconnect backoff bounds. Safe defaults ship in code (Baileys
/// <c>src/Defaults/index.ts</c> socket config at commit <c>0af2386</c>, confirmed in
/// 02-SPIKE-NOTES.md F5).
/// </summary>
/// <remarks>
/// <para>
/// This is a plain options object: the connection owner receives it directly.
/// <c>IOptions&lt;T&gt;</c> binding arrives with Phase 7's DI surface (MUL-03) — the
/// registration *shape* is fixed here so hosts write it once (StoreRegistryOptions
/// precedent).
/// </para>
/// </remarks>
public sealed class ConnectionOptions
{
    /// <summary>
    /// WebSocket handshake timeout, in milliseconds (Baileys <c>connectTimeoutMs</c>).
    /// Must be positive.
    /// </summary>
    public int ConnectTimeoutMs { get; init; } = 20_000;

    /// <summary>
    /// Correlated iq round-trip timeout, in milliseconds (Baileys
    /// <c>defaultQueryTimeoutMs</c>). A reply arriving after this window is discarded
    /// with a debug log and never completes a reused id (Pitfall 5). Must be positive.
    /// </summary>
    public int QueryTimeoutMs { get; init; } = 60_000;

    /// <summary>
    /// Cadence of the <c>w:p</c> iq ping keepalive, in milliseconds (Baileys
    /// <c>keepAliveIntervalMs</c>). This iq ping and
    /// <c>ClientWebSocketOptions.KeepAliveInterval</c> WS pongs are complementary, not
    /// substitutes (O4). Must be positive.
    /// </summary>
    public int KeepAliveIntervalMs { get; init; } = 30_000;

    /// <summary>
    /// Reconnect backoff base, in milliseconds: the attempt-0 ceiling of the full-jitter
    /// window and the floor discipline reference. Must not be negative.
    /// </summary>
    public int RetryBaseDelayMs { get; init; } = 1_000;

    /// <summary>
    /// Reconnect backoff ceiling, in milliseconds: no single reconnect delay ever
    /// exceeds it, under any jitter sequence or attempt count. Must not be below
    /// <see cref="RetryBaseDelayMs"/>.
    /// </summary>
    public int RetryMaxDelayMs { get; init; } = 60_000;

    /// <summary>
    /// Injectable WA Web app-version override, dotted <c>primary.secondary.tertiary</c>
    /// (e.g. <c>2.3000.1043857760</c>); <c>null</c> resolves the runtime revision
    /// through <c>WaVersionResolver</c> instead. Tests and the loopback wire supply a
    /// revision here so no network access is needed; the handshake payload carries the
    /// override ?? resolver value onto the wire.
    /// </summary>
    public string? WaVersionOverride { get; init; }

    /// <summary>
    /// Fail-fast guard (DurableWriteRetryPolicy <c>ValidateOptions</c> precedent):
    /// resolves <c>null</c> to defaults and throws
    /// <see cref="ArgumentOutOfRangeException"/> on any out-of-range bound — before any
    /// socket I/O — instead of surfacing later as a raw <c>Math.Clamp</c> argument error
    /// or a fabricated reconnect schedule.
    /// </summary>
    /// <param name="options">Host-supplied options, or <c>null</c> for defaults.</param>
    /// <returns>The resolved options (the same instance when already valid).</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A timeout is not positive, <see cref="RetryBaseDelayMs"/> is negative, or
    /// <see cref="RetryMaxDelayMs"/> is below <see cref="RetryBaseDelayMs"/>.
    /// </exception>
    internal static ConnectionOptions ValidateOptions(ConnectionOptions? options)
    {
        ConnectionOptions resolved = options ?? new ConnectionOptions();
        if (resolved.ConnectTimeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ConnectTimeoutMs must be positive.");
        }

        if (resolved.QueryTimeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "QueryTimeoutMs must be positive.");
        }

        if (resolved.KeepAliveIntervalMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "KeepAliveIntervalMs must be positive.");
        }

        if (resolved.RetryBaseDelayMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RetryBaseDelayMs must not be negative.");
        }

        if (resolved.RetryMaxDelayMs < resolved.RetryBaseDelayMs)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RetryMaxDelayMs must not be below RetryBaseDelayMs.");
        }

        if (resolved.WaVersionOverride is not null && !WaVersion.TryParseDotted(resolved.WaVersionOverride, out _))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "WaVersionOverride must be a dotted app version (primary.secondary.tertiary).");
        }

        return resolved;
    }
}
