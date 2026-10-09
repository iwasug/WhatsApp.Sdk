namespace Iwasug.WhatsApp.Sdk.Pairing;

/// <summary>
/// Every state one QR pairing attempt can report to the host (CONN-01).
/// </summary>
/// <remarks>
/// <para>
/// Scope guard (ConnectionState-style): this enum carries the pairing-attempt
/// vocabulary only — connection lifecycle stays in
/// <see cref="Connection.ConnectionState"/> and backoff depth stays in the
/// connection event payload, never as extra pairing states.
/// </para>
/// <para>
/// Design boundary (CONN-01 requirement): the QR reference the <c>Code</c> payload
/// carries is an opaque server-supplied string the SDK never interprets — scan
/// tracking stays server-side, reference decode is not implemented.
/// </para>
/// </remarks>
public enum PairingStatus
{
    /// <summary>
    /// A fresh QR payload is ready. The host should render
    /// <see cref="PairingStateChangedEventArgs.Payload"/> (the exact wire string)
    /// for the user to scan. Superseded by the next <c>Code</c> on rotation — the
    /// host should replace, never stack, rendered codes.
    /// </summary>
    Code = 0,

    /// <summary>
    /// The phone completed pairing. The host should persist its session and stop
    /// driving pairing — no further events follow this attempt.
    /// </summary>
    Success = 1,

    /// <summary>
    /// No scan completed inside the attempt window
    /// (<see cref="PairingQrPayload.AttemptTimeout"/>). The host should re-pair
    /// (start a fresh attempt for a fresh code) — the expired codes are dead.
    /// </summary>
    Timeout = 2,

    /// <summary>
    /// The server rejected the session during pairing (logged-out class). The host
    /// should re-pair from scratch — retrying the same attempt cannot succeed.
    /// </summary>
    LoggedOut = 3,

    /// <summary>
    /// The server rejected this client build as stale (405 class). The host should
    /// surface upgrade guidance — re-pairing the same build loops against a
    /// permanently-unsupported version.
    /// </summary>
    ClientOutdated = 4,
}

/// <summary>
/// One pairing state transition, in the <c>EventArgs</c> style already used by
/// connection events.
/// </summary>
/// <remarks>
/// <para>
/// Secret hygiene (T-3-06): the <c>Code</c> payload is itself a single-use secret
/// (it carries the noise and identity publics plus the adv secret) — the host must
/// render it, never log or persist it beyond the live attempt.
/// </para>
/// </remarks>
public sealed class PairingStateChangedEventArgs : EventArgs
{
    /// <summary>Creates a pairing transition event.</summary>
    /// <param name="status">The pairing state just entered.</param>
    /// <param name="payload">
    /// The QR wire string on <see cref="PairingStatus.Code"/>, the pair-success
    /// passthrough on <see cref="PairingStatus.Success"/>, <c>null</c> on every
    /// other status.
    /// </param>
    public PairingStateChangedEventArgs(PairingStatus status, string? payload)
    {
        Status = status;
        Payload = payload;
    }

    /// <summary>The pairing state just entered.</summary>
    public PairingStatus Status { get; }

    /// <summary>
    /// Status payload (QR wire string or success passthrough); <c>null</c> on
    /// terminal states that carry no payload.
    /// </summary>
    public string? Payload { get; }
}
