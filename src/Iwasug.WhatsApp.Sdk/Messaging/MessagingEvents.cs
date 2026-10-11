using Iwasug.WhatsApp.Sdk.Domain;

namespace Iwasug.WhatsApp.Sdk.Messaging;

/// <summary>
/// Base type for all WhatsApp SDK events published through the event stream.
/// All event arg records (MessageReceivedEventArgs, ReceiptReceivedEventArgs,
/// MessageAckedEventArgs, DecryptionFailedEventArgs) inherit from this type.
/// </summary>
public record WhatsAppEvent;

/// <summary>
/// Types of WhatsApp events for the bounded event stream (MSG-07)
/// and EventHandler compatibility (CONN-05).
/// </summary>
public enum WhatsAppEventType
{
    /// <summary>A message was received and decrypted successfully.</summary>
    MessageReceived,

    /// <summary>A delivery receipt was received.</summary>
    ReceiptReceived,

    /// <summary>A message acknowledgment was received.</summary>
    MessageAcked,

    /// <summary>A message decryption failed.</summary>
    DecryptionFailed,
}

/// <summary>
/// Event arguments for a received message event.
/// </summary>
/// <param name="Key">The wire message key identifying the message.</param>
/// <param name="Chat">The chat JID the message belongs to.</param>
/// <param name="Sender">The sender's JID.</param>
/// <param name="IsFromMe">Whether the message was sent by the local account.</param>
/// <param name="Timestamp">The timestamp (UTC) when the message was sent.</param>
/// <param name="Text">Optional plaintext of the message; null for encrypted-only messages.</param>
public sealed record MessageReceivedEventArgs(
    MessageKeyRecord Key,
    Jid Chat,
    Jid Sender,
    bool IsFromMe,
    DateTimeOffset Timestamp,
    string? Text) : WhatsAppEvent;

// Event args for receipt, ack, and decryption failure follow the same pattern.
// They do NOT inherit from WhatsAppEvent to avoid the sealed-type constraint,
// since WhatsAppEvent is a base/ marker type for the event channel.

/// <summary>
/// Event arguments for a received receipt event.
/// </summary>
/// <param name="ReceiptType">The type of receipt (delivery/read/played/sender/inactive/retry).</param>
/// <param name="MessageIds">The message ids carried by the receipt.</param>
/// <param name="Chat">The chat JID the receipt belongs to.</param>
/// <param name="Participant">Optional participant JID for group receipts; null for 1:1.</param>
/// <param name="Timestamp">The timestamp when the receipt was sent.</param>
public sealed record ReceiptReceivedEventArgs(
    WhatsAppEventType ReceiptType,
    string[] MessageIds,
    Jid Chat,
    string? Participant,
    DateTimeOffset Timestamp) : WhatsAppEvent;

/// <summary>
/// Event arguments for a message acknowledgment event.
/// </summary>
/// <param name="MessageId">The message id that was acknowledged.</param>
/// <param name="Chat">The chat JID the acknowledgment belongs to.</param>
/// <param name="IsFromMe">Whether the acknowledgment was sent by the local account.</param>
/// <param name="Timestamp">The timestamp when the acknowledgment was sent.</param>
public sealed record MessageAckedEventArgs(
    string MessageId,
    Jid Chat,
    bool IsFromMe,
    DateTimeOffset Timestamp) : WhatsAppEvent;

/// <summary>
/// Event arguments for a decryption failure event.
/// </summary>
/// <param name="Key">The message key of the message that failed decryption.</param>
/// <param name="Chat">The chat JID the message belongs to.</param>
/// <param name="ErrorDetail">Optional error detail describing the failure.</param>
/// <param name="Timestamp">The timestamp when the failure was detected.</param>
public sealed record DecryptionFailedEventArgs(
    MessageKeyRecord Key,
    Jid Chat,
    string? ErrorDetail,
    DateTimeOffset Timestamp) : WhatsAppEvent;

/// <summary>
/// Outcome of a send operation, carried from the send path to the dispatcher.
/// </summary>
/// <param name="MessageId">The client-generated message id.</param>
/// <param name="Status">The status of the send operation.</param>
public sealed record SendOutcome(
    string MessageId,
    SendOutcomeStatus Status);

/// <summary>
/// Status of a send operation.
/// </summary>
public enum SendOutcomeStatus
{
    /// <summary>The message was sent successfully.</summary>
    Sent,

    /// <summary>The send operation timed out.</summary>
    TimedOut
}
