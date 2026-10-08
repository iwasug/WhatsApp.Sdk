namespace Iwasug.WhatsApp.Sdk.Connection;

/// <summary>
/// Host-observable lifecycle of one account's connection (CONN-05): exactly these four
/// states, per account, with <see cref="DisconnectReason"/> and attempt count carried on
/// every transition event and log scope.
/// </summary>
/// <remarks>
/// <para>
/// Transition rule (locked — T-2-07): <c>Connecting → Open → Closing →
/// (Connecting | LoggedOut)</c>. <c>LoggedOut</c> is terminal for this connection
/// instance and is emitted exactly once (single-writer dispatch + Interlocked guard,
/// implemented in 02-02). Hosts must never observe <c>Open</c> after
/// <c>LoggedOut</c>, nor duplicate terminal events.
/// </para>
/// <para>
/// Scope guard (Jid-style): this enum carries the state vocabulary only — backoff
/// attempt depth stays in the <c>Closing</c> event payload and logs (02-RESEARCH Open
/// Question 4), never as extra public states.
/// </para>
/// </remarks>
public enum ConnectionState
{
    /// <summary>
    /// Opening the socket and running the Noise handshake (including every
    /// reconnect re-handshake with a fresh ephemeral).
    /// </summary>
    Connecting = 0,

    /// <summary>
    /// Handshake complete and the query router is round-tripping. The reconnect
    /// attempt counter resets on entry.
    /// </summary>
    Open = 1,

    /// <summary>
    /// Dropped and between attempts: the reconnect loop owns the connection and will
    /// re-enter <see cref="Connecting"/> per <see cref="ReconnectPolicy"/>, unless the
    /// drop was terminal (then this transitions once to <see cref="LoggedOut"/>).
    /// </summary>
    Closing = 2,

    /// <summary>
    /// Terminal: credentials dead or client outdated (401/403/411/419). Emitted exactly
    /// once; the instance never leaves this state. The host must re-pair or redeploy.
    /// </summary>
    LoggedOut = 3,
}
