#pragma warning disable CS1591
namespace Iwasug.WhatsApp.Sdk.Messaging;

/// <summary>Per-recipient outcome of one <see cref="Messenger.SendBroadcastAsync"/> fan-out.</summary>
public sealed record BroadcastRecipientOutcome(
    Domain.Jid Recipient,
    SendOutcome Result);

/// <summary>Loop-level options for <see cref="Messenger.SendBroadcastAsync"/>.</summary>
public sealed record BroadcastOptions(
    TimeSpan? InterRecipientDelay = null);