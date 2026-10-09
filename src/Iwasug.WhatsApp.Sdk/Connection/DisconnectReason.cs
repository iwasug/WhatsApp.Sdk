namespace Iwasug.WhatsApp.Sdk.Connection;

/// <summary>
/// Why a connection dropped, as the numeric status code the server sent. Member names
/// follow Baileys' <c>DisconnectReason</c> enum (<c>src/Types/index.ts</c> at commit
/// <c>0af2386</c>, quoted in 02-SPIKE-NOTES.md O1); each member documents the Phase 2
/// reconnect behavior from <see cref="ReconnectPolicy"/>.
/// </summary>
/// <remarks>
/// <para>
/// Scope guard (Jid-style): this enum carries the code vocabulary only — the
/// code-to-action table lives in <see cref="ReconnectPolicy.Decide"/>, and the
/// host-visible lifecycle lives in <see cref="ConnectionState"/>. Numeric values ARE
/// the wire contract: they match the server-sent status codes, so they must never be
/// renumbered.
/// </para>
/// <para>
/// 408 overload note (O1): Baileys aliases <c>timedOut = 408</c> onto
/// <c>connectionLost = 408</c> — network loss and query timeout share one code. The
/// policy is identical either way, but diagnostics MUST log the sub-cause (socket error
/// vs outstanding query) alongside the code (02-RESEARCH Pitfall 2).
/// </para>
/// </remarks>
public enum DisconnectReason
{
    /// <summary>
    /// Session logged out — credentials are dead (Baileys <c>loggedOut</c>).
    /// <b>Terminal:</b> never retry; emit <see cref="ConnectionState.LoggedOut"/>
    /// exactly once. The host must re-pair (Phase 3). Auto-retrying loops forever
    /// against a dead session (T-2-01).
    /// </summary>
    LoggedOut = 401,

    /// <summary>
    /// Authentication expired — Baileys lists 419 in <c>UNAUTHORIZED_CODES</c> alongside
    /// 401/403 (02-SPIKE-NOTES.md O1), though it is not a named Baileys
    /// <c>DisconnectReason</c> member. <b>Terminal:</b> auth failures must never retry —
    /// a bounded retry here would loop against dead credentials (T-2-01 class).
    /// </summary>
    AuthExpired = 419,

    /// <summary>
    /// Forbidden (Baileys <c>forbidden</c>). <b>Terminal:</b> never retry; surface to
    /// the host like <see cref="LoggedOut"/>.
    /// </summary>
    Forbidden = 403,

    /// <summary>
    /// Stale client rejected by the server (Baileys <c>clientOutdated</c>, surfaced
    /// from a <c>failure</c> stanza with <c>reason=405</c> or a 405 stream error).
    /// <b>Terminal:</b> never retry — the server rejects this build, so reconnecting
    /// loops forever against a permanently-unsupported version (03-RESEARCH.md
    /// Pitfall 1). The connection clears the cached WA Web revision
    /// (<c>WaVersionResolver</c>) on this path so the next pairing attempt
    /// re-resolves instead of retrying the rejected value.
    /// </summary>
    ClientOutdated = 405,

    /// <summary>
    /// Network lost or query timed out (Baileys <c>connectionLost</c> / <c>timedOut</c>,
    /// same code — see the 408 overload note in remarks). <b>Retry</b> with full-jitter
    /// backoff; in-flight queries fail as connection-lost, never silently (Pitfall 5).
    /// </summary>
    ConnectionLost = 408,

    /// <summary>
    /// Protocol/client version mismatch (Baileys <c>multideviceMismatch</c>).
    /// <b>Terminal:</b> needs a deploy with a bumped version, not retries — retrying a
    /// version the server rejects is a reconnect loop with no exit.
    /// </summary>
    MultideviceMismatch = 411,

    /// <summary>
    /// Server closed the connection, including server-side enforcement kicks (Baileys
    /// <c>connectionClosed</c>). <b>Retry</b> with full-jitter backoff.
    /// </summary>
    ConnectionClosed = 428,

    /// <summary>
    /// Another companion replaced this session (Baileys <c>connectionReplaced</c>).
    /// <b>Surface-and-stop:</b> no auto-retry — immediate reconnect starts a kick-war
    /// between devices. Surface <c>account-replaced</c>; the host restarts explicitly.
    /// </summary>
    ConnectionReplaced = 440,

    /// <summary>
    /// Bad session (Baileys <c>badSession</c>). <b>Retry</b> with backoff (bounded);
    /// Phase 3 resume may later escalate repeated occurrences to re-pair.
    /// </summary>
    BadSession = 500,

    /// <summary>
    /// Service unavailable (Baileys <c>unavailableService</c>). <b>Retry</b> with
    /// backoff (longer base than <see cref="ConnectionLost"/> — server-side outage,
    /// not a local flap).
    /// </summary>
    UnavailableService = 503,

    /// <summary>
    /// Server demands a fresh session (Baileys <c>restartRequired</c>; also fires right
    /// after QR scan in the Baileys flow). <b>Immediate restart:</b> fresh socket +
    /// fresh Noise handshake (fresh ephemeral — never reuse <c>e</c>), backoff counter
    /// reset.
    /// </summary>
    RestartRequired = 515,
}
