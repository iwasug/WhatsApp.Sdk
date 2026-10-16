#pragma warning disable CS1591
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
/// <param name="QuotedMessageId">The wire message id of this received message (04-04, MSG-05): pass it as <c>quotedMessageId</c> to <see cref="Messenger.ReplyAsync"/> to reply while quoting it. Null when the wire carried no <c>id</c> (the message was never quotable).</param>
public sealed record MessageReceivedEventArgs(
    Domain.MessageKeyRecord Key,
    Domain.Jid Chat,
    Domain.Jid Sender,
    bool IsFromMe,
    DateTimeOffset Timestamp,
    string? Text,
    string? QuotedMessageId = null);

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

/// <summary>
/// Receipt type vocabulary (04-04, MSG-04 — 04-RESEARCH Pattern 4): the six kinds the
/// SDK parses from inbound <c>receipt</c> nodes. Types outside this vocabulary (e.g.
/// <c>read_self</c>) are silently ignored — informational only (T-4-14).
/// </summary>
public enum ReceiptKind
{
    /// <summary>The peer's server confirmed delivery (<c>type="delivery"</c>, also the historical no-type form).</summary>
    Delivery,

    /// <summary>The peer marked the messages read (<c>type="read"</c>).</summary>
    Read,

    /// <summary>The peer played a voice note (<c>type="played"</c>).</summary>
    Played,

    /// <summary>Sender receipt for from-me messages (<c>type="sender"</c>).</summary>
    Sender,

    /// <summary>The client was not "online" — the server forwards but WA apps don't render (<c>type="inactive"</c>).</summary>
    Inactive,

    /// <summary>The peer failed to decrypt our message and requests a resend (<c>type="retry"</c>).</summary>
    Retry,
}

/// <summary>
/// Event arguments for one parsed inbound <c>receipt</c> node (04-04, MSG-04 —
/// Pattern 4 shape): the kind, the referenced message ids (attribute id first, then
/// <c>list/item</c> children in wire order), the chat, the optional group participant,
/// and the receipt timestamp.
/// </summary>
/// <remarks>
/// <para>
/// Handler contract (identical to <see cref="MessageReceivedEventArgs"/>): return
/// quickly, never call <see cref="Connection.WaConnection.DisposeAsync"/> from the
/// handler, handler exceptions are logged and swallowed.
/// </para>
/// <para>
/// Receipts are informational typed events that gate no SDK behavior and no security
/// decision (T-4-14): the attrs are adversary-influenced and parsed bounds-checked, a
/// duplicate receipt raises the event again, and the host decides what to trust.
/// </para>
/// </remarks>
/// <param name="Kind">The receipt type (see <see cref="ReceiptKind"/>).</param>
/// <param name="MessageIds">The referenced message ids, verbatim from the wire.</param>
/// <param name="Chat">The chat the receipt belongs to (bare user form for 1:1).</param>
/// <param name="Participant">The group sender the receipt references; null outside groups.</param>
/// <param name="Timestamp">The receipt timestamp (UTC) from the wire <c>t</c> attribute when present, else receipt time.</param>
public sealed record ReceiptReceivedEventArgs(
    ReceiptKind Kind,
    IReadOnlyList<string> MessageIds,
    Domain.Jid Chat,
    Domain.Jid? Participant,
    DateTimeOffset Timestamp);

/// <summary>
/// Raised when one inbound message failed to decrypt (04-04, MSG-06). Raised on
/// <b>every</b> failure — uncapped — while the wire-side retry receipt emission stops
/// after <see cref="ReceiptHandler"/>'s Pattern-5 count cap: past the cap the typed
/// event is the host's only signal (T-4-13 retry-loop bound).
/// </summary>
/// <remarks>
/// Handler contract (identical to <see cref="MessageReceivedEventArgs"/>): return
/// quickly, never call <see cref="Connection.WaConnection.DisposeAsync"/> from the
/// handler, handler exceptions are logged and swallowed. The event names rules only —
/// no plaintext, ciphertext, or key bytes ever surface (T-4-01 discipline).
/// </remarks>
/// <param name="Chat">The chat the failed message belongs to (bare user form for 1:1).</param>
/// <param name="Sender">The sender's full per-device JID.</param>
/// <param name="MessageId">The failed message's wire id.</param>
/// <param name="RetryCount">The 1-based failure count for this (sender, message id) pair.</param>
/// <param name="Timestamp">The original message timestamp (UTC) from the wire <c>t</c> attribute when present, else discovery time.</param>
public sealed record DecryptionFailedEventArgs(
    Domain.Jid Chat,
    Domain.Jid Sender,
    string MessageId,
    int RetryCount,
    DateTimeOffset Timestamp);

/// <summary>
/// Base record of the typed event stream (04-05, MSG-07): every fact the SDK raises
/// through <see cref="Messenger.ReadEventsAsync"/> derives from this record and carries
/// its occurrence timestamp. One vocabulary, two consumption shapes — the
/// <c>EventHandler</c> events shipped in plans 04-03/04-04 and this stream are raised
/// from the SAME internal emit sites, so both surfaces always observe the same facts.
/// </summary>
/// <remarks>
/// Stream contract: the stream carries message, receipt, ack, and decryption-failure
/// facts only. Connection state and logout are <c>EventHandler</c> events on
/// <see cref="Connection.WaConnection"/> (CONN-05 compatibility) and are deliberately
/// NOT part of this hierarchy. Consuming code must not block the drain indefinitely
/// (the channel is bounded — see <see cref="Messenger.EventChannelCapacity"/> — and a
/// slow consumer slows the receive pump rather than dropping events); exceptions from
/// host code never cross the SDK boundary (enumeration runs on the host's own task).
/// </remarks>
/// <param name="Timestamp">When the fact occurred (UTC, from the wire when present).</param>
public abstract record WhatsAppEvent(DateTimeOffset Timestamp);

/// <summary>
/// Stream twin of <see cref="MessageReceivedEventArgs"/> (04-05, MSG-07): one received-
/// and-decrypted message, raised through the stream after the durable session write and
/// the ack have completed (D-06 ordering — identical to the <c>EventHandler</c> twin).
/// </summary>
/// <param name="Key">The public message-key projection for the message.</param>
/// <param name="Chat">The chat JID the message belongs to (bare user form for 1:1).</param>
/// <param name="Sender">The sender's full per-device JID (group participant for skmsg).</param>
/// <param name="IsFromMe">Whether the message was sent by this account.</param>
/// <param name="Timestamp">The message timestamp (UTC) from the wire <c>t</c> attribute when present, else receipt time.</param>
/// <param name="Text">The decrypted conversation text; null when the message carries no conversation body.</param>
/// <param name="QuotedMessageId">The wire message id, passable to <see cref="Messenger.ReplyAsync"/> as <c>quotedMessageId</c>; null when never quotable.</param>
public sealed record MessageReceivedEvent(
    Domain.MessageKeyRecord Key,
    Domain.Jid Chat,
    Domain.Jid Sender,
    bool IsFromMe,
    DateTimeOffset Timestamp,
    string? Text,
    string? QuotedMessageId) : WhatsAppEvent(Timestamp);

/// <summary>
/// Stream twin of <see cref="ReceiptReceivedEventArgs"/> (04-05, MSG-07): one parsed
/// inbound <c>receipt</c> node. Informational only — receipts gate no SDK behavior and
/// no security decision (T-4-14); a duplicate receipt produces a duplicate event.
/// </summary>
/// <param name="Kind">The receipt type (see <see cref="ReceiptKind"/>).</param>
/// <param name="MessageIds">The referenced message ids, verbatim from the wire.</param>
/// <param name="Chat">The chat the receipt belongs to (bare user form for 1:1).</param>
/// <param name="Participant">The group sender the receipt references; null outside groups.</param>
/// <param name="Timestamp">The receipt timestamp (UTC) from the wire <c>t</c> attribute when present, else receipt time.</param>
public sealed record ReceiptReceivedEvent(
    ReceiptKind Kind,
    IReadOnlyList<string> MessageIds,
    Domain.Jid Chat,
    Domain.Jid? Participant,
    DateTimeOffset Timestamp) : WhatsAppEvent(Timestamp);

/// <summary>
/// Server ack fact for one sent message (04-05, MSG-07): the terminal
/// <see cref="SendOutcome"/> the send path settles, surfaced through the stream too.
/// Raised exactly once per completed send (a late or duplicate ack produces no
/// event); the awaited <see cref="SendOutcome"/> return value and this event share
/// the same internal emit point.
/// </summary>
/// <param name="MessageId">The client-generated message id the ack correlates.</param>
/// <param name="Chat">The chat the message was sent to.</param>
/// <param name="Status">The terminal status of the send (<see cref="SendOutcomeStatus.Sent"/> or <see cref="SendOutcomeStatus.ServerError"/>).</param>
/// <param name="Error">The server reply <c>error</c> attribute when present; nonzero means failure.</param>
/// <param name="T">The server reply <c>t</c> attribute when present (sender-stamped unix seconds, string).</param>
/// <param name="ServerId">The server reply <c>server_id</c> attribute when present.</param>
/// <param name="Timestamp">When the ack was correlated (UTC).</param>
public sealed record MessageAckedEvent(
    string MessageId,
    Domain.Jid Chat,
    SendOutcomeStatus Status,
    int? Error,
    string? T,
    string? ServerId,
    DateTimeOffset Timestamp) : WhatsAppEvent(Timestamp);

/// <summary>
/// Stream twin of <see cref="DecryptionFailedEventArgs"/> (04-05, MSG-07): one inbound
/// message failed to decrypt. Raised on every failure — uncapped — while the wire-side
/// retry receipt stops after <see cref="ReceiptHandler"/>'s Pattern-5 count cap.
/// Names rules only — no plaintext, ciphertext, or key bytes ever surface (T-4-01).
/// </summary>
/// <param name="Chat">The chat the failed message belongs to (bare user form for 1:1).</param>
/// <param name="Sender">The sender's full per-device JID.</param>
/// <param name="MessageId">The failed message's wire id.</param>
/// <param name="RetryCount">The 1-based failure count for this (sender, message id) pair.</param>
/// <param name="Timestamp">The original message timestamp (UTC) from the wire <c>t</c> attribute when present, else discovery time.</param>
public sealed record DecryptionFailedEvent(
    Domain.Jid Chat,
    Domain.Jid Sender,
    string MessageId,
    int RetryCount,
    DateTimeOffset Timestamp) : WhatsAppEvent(Timestamp);

/// <summary>Event args for an inbound peer presence update (EXT-02).</summary>
public sealed record PresenceUpdatedEventArgs(
    Domain.Jid Chat,
    Domain.Jid Sender,
    PresenceState State,
    DateTimeOffset Timestamp);

/// <summary>Event args raised after an outbound presence state is applied (EXT-02).</summary>
public sealed record PresenceAppliedEventArgs(
    PresenceState State,
    Domain.Jid? Chat,
    DateTimeOffset Timestamp);

/// <summary>Event arguments for one inbound reaction fact (EXT-03).</summary>
public sealed record ReactionReceivedEventArgs(
    Domain.Jid Chat,
    string TargetMessageId,
    Domain.Jid Sender,
    string? Emoji,
    DateTimeOffset Timestamp);

/// <summary>Event arguments for one inbound edited message fact (EXT-04).</summary>
public sealed record MessageEditedEventArgs(
    Domain.Jid Chat,
    string MessageId,
    string? NewText,
    DateTimeOffset Timestamp);

/// <summary>Event arguments for one inbound delete-for-everyone fact (EXT-04).</summary>
public sealed record MessageDeletedEventArgs(
    Domain.Jid Chat,
    IReadOnlyList<string> MessageIds,
    Domain.Jid Remover,
    DateTimeOffset Timestamp);
