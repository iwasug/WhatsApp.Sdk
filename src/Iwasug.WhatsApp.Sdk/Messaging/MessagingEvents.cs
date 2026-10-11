namespace Iwasug.WhatsApp.Sdk.Messaging;

/// <summary>
/// Event arguments for one received-and-decrypted message (04-03, MSG-01).
/// Raised exactly once per successfully decrypted inbound message, after the
/// durable session write and the ack have completed (D-06 ordering: persist →
/// ack → event → cache).
/// </summary>
/// <remarks>
/// Handler contract (identical to <see cref="Connection.ConnectionStateChangedEventArgs"/>):
/// return quickly (copy to your own queue — Channels precedent), never call
/// <see cref="Connection.WaConnection.DisposeAsync"/> from the handler, handler exceptions are
/// logged and swallowed.
/// </remarks>
/// <param name="Key">The public message-key projection for the message.</param>
/// <param name="Chat">The chat JID the message belongs to (bare user form for 1:1).</param>
/// <param name="Sender">The sender's full per-device JID.</param>
/// <param name="IsFromMe">Whether the message was sent by this account.</param>
/// <param name="Timestamp">The message timestamp (UTC) from the wire <c>t</c> attribute when present, else receipt time.</param>
/// <param name="Text">The decrypted conversation text; null when the message carries no conversation body.</param>
public sealed record MessageReceivedEventArgs(
    Domain.MessageKeyRecord Key,
    Domain.Jid Chat,
    Domain.Jid Sender,
    bool IsFromMe,
    DateTimeOffset Timestamp,
    string? Text);

/// <summary>
/// Terminal outcome of one send operation (04-03, MSG-03). Exactly one outcome
/// per send: the send path never re-sends, never maps one send onto two
/// outcomes, and never returns success speculatively.
/// </summary>
/// <remarks>
/// <para>
/// Idempotency contract: one logical send = one network send. On timeout the
/// SDK returns the <see cref="SendOutcomeStatus.TimedOut"/> outcome carrying the id and drops —
/// it never re-sends a message on its own. The host may re-call
/// <see cref="Messenger.SendMessageAsync"/>, which is a NEW send with a NEW id.
/// The Phase-3 <c>retryFrame</c> disconnect path re-sends only a frame that was
/// already in flight when the socket dropped (protocol-mandated, not
/// message-level retry). A peer retry receipt triggers resend only in plan
/// 04-04 and only with the same id.
/// </para>
/// <para>
/// Cancellation is not an outcome: a cancelled send surfaces
/// <see cref="OperationCanceledException"/> unwrapped (repo cancel-passes-through
/// discipline), releases the send gate, and never touches the wire.
/// </para>
/// </remarks>
/// <param name="MessageId">The client-generated message id (3EB0 + 18 CSPRNG bytes, 40-char uppercase hex).</param>
/// <param name="Status">The terminal status of the send.</param>
/// <param name="Error">The server reply <c>error</c> attribute when present; nonzero means failure.</param>
/// <param name="T">The server reply <c>t</c> attribute when present (sender-stamped unix seconds, string).</param>
/// <param name="ServerId">The server reply <c>server_id</c> attribute when present.</param>
public sealed record SendOutcome(
    string MessageId,
    SendOutcomeStatus Status,
    int? Error = null,
    string? T = null,
    string? ServerId = null);

/// <summary>
/// Terminal status of a send operation (04-03, MSG-03).
/// </summary>
public enum SendOutcomeStatus
{
    /// <summary>The server acked the message id.</summary>
    Sent,

    /// <summary>The server replied with a nonzero <c>error</c> attribute for the message id.</summary>
    ServerError,

    /// <summary>No correlated reply arrived inside the send window; the message was dropped (never re-sent).</summary>
    TimedOut,
}

/// <summary>
/// Typed warning raised when an ack's <c>phash</c> no longer matches the resolved device
/// set the message fanned out to (04-03 Pitfall 3): the peer's device set moved. The SDK
/// has already invalidated its device-list cache for <see cref="Chat"/>; the next send
/// re-queries USync. The affected send still settles its normal <see cref="SendOutcome"/>.
/// </summary>
/// <param name="Chat">The peer chat whose device set moved.</param>
/// <param name="MessageId">The message id whose ack carried the mismatched phash.</param>
/// <param name="ExpectedPhash">The phash computed over the fanout device set.</param>
/// <param name="ReceivedPhash">The phash the server returned on the ack.</param>
public sealed record DeviceSetChangedEventArgs(
    Domain.Jid Chat,
    string MessageId,
    string ExpectedPhash,
    string ReceivedPhash);
