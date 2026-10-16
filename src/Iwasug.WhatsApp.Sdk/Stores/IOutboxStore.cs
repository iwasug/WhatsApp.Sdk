#pragma warning disable CS1591
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;

namespace Iwasug.WhatsApp.Sdk.Stores;

/// <summary>Persistence state of one <see cref="OutboxEntry"/> row (EXT-01).</summary>
public enum OutboxState
{
    /// <summary>Pending.</summary>
    Pending = 0,
    /// <summary>In flight.</summary>
    InFlight = 1,
    /// <summary>Sent.</summary>
    Sent = 2,
    /// <summary>Retry scheduled.</summary>
    RetryScheduled = 3,
    /// <summary>Dead lettered.</summary>
    DeadLettered = 4,
}

/// <summary>Media descriptor persisted in an outbox payload; bytes are never stored (T-08-03).</summary>
public sealed record OutboxMediaDescriptor(
    string? Url = null,
    string? DirectPath = null,
    string? MediaKeyBase64 = null,
    string? Mimetype = null,
    long? FileLength = null);

/// <summary>
/// Discriminated payload for an enqueued outbox message (EXT-01). Carries data and a
/// media upload descriptor only — never ciphertext, keys, or media bytes.
/// </summary>
public enum OutboxPayloadKind
{
    /// <summary>None.</summary>
    None = 0,
    /// <summary>Text.</summary>
    Text = 1,
    /// <summary>Group text.</summary>
    GroupText = 2,
    /// <summary>Reply.</summary>
    Reply = 3,
    /// <summary>Reaction.</summary>
    Reaction = 4,
    /// <summary>Edit.</summary>
    Edit = 5,
    /// <summary>Delete.</summary>
    Delete = 6,
    /// <summary>Media.</summary>
    Media = 7,
}

public sealed record OutboxPayload(
    OutboxPayloadKind Kind = OutboxPayloadKind.Text,
    string? Text = null,
    string? TargetMessageId = null,
    string? Emoji = null,
    OutboxMediaDescriptor? Media = null,
    string? Caption = null,
    string? Reason = null);

/// <summary>
/// Account-scoped durable outbox row (EXT-01). The instance of <see cref="IOutboxStore"/>
/// IS the account namespace; no method takes an account id.
/// </summary>
public sealed record OutboxEntry(
    string Id,
    Domain.Jid Destination,
    OutboxPayload Payload,
    DateTimeOffset EnqueuedUtc,
    OutboxState State = OutboxState.Pending,
    int Attempt = 0,
    DateTimeOffset? NextAttemptUtc = null,
    string? LastErrorRuleName = null);

/// <summary>Granular primitive seam for durable outbox rows (EXT-01).</summary>
public interface IOutboxStore : IAsyncDisposable
{
    Task<OutboxEntry> EnqueueAsync(OutboxEntry entry, CancellationToken cancellationToken);

    Task<IReadOnlyList<OutboxEntry>> GetDueAsync(int max, CancellationToken cancellationToken);

    Task MarkSentAsync(string id, CancellationToken cancellationToken);

    Task ScheduleRetryAsync(string id, int attempt, DateTimeOffset nextAttemptUtc, string? ruleName, CancellationToken cancellationToken);

    Task DeadLetterAsync(string id, string? reason, CancellationToken cancellationToken);

    Task<bool> TryRemoveAsync(string id, CancellationToken cancellationToken);
}

/// <summary>Async factory producing account-scoped <see cref="IOutboxStore"/> instances.</summary>
public interface IOutboxStoreFactory
{
    ValueTask<IOutboxStore> CreateAsync(AccountId accountId, CancellationToken cancellationToken = default);
}