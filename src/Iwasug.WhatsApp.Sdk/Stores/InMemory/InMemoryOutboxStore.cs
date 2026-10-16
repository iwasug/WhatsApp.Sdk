using System.Collections.Concurrent;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Stores;

namespace Iwasug.WhatsApp.Sdk.Stores.InMemory;

/// <summary>Shared backing row for <see cref="InMemoryOutboxStore"/> (D-06 durability: a second
/// view created from the same factory for the same account observes the same rows).</summary>
internal sealed class OutboxRow
{
    internal readonly object Gate = new();
    internal readonly Dictionary<string, OutboxEntry> Entries = new(StringComparer.Ordinal);
}

/// <summary>In-memory reference implementation of <see cref="IOutboxStore"/> (EXT-01).</summary>
/// <remarks>
/// <para>WR-03: due-row filtering consults an injected <see cref="TimeProvider"/>
/// (default <see cref="TimeProvider.System"/>) instead of wall-clock time, so a host or
/// test that supplies a non-system provider keeps the store's queue due-ness in step
/// with the <c>OutboxPump</c> clock that schedules retries.</para>
/// </remarks>
internal sealed class InMemoryOutboxStore : IOutboxStore
{
    private readonly OutboxRow _row;
    private readonly TimeProvider _clock;

    public InMemoryOutboxStore() : this(new OutboxRow(), null)
    {
    }

    public InMemoryOutboxStore(OutboxRow row, TimeProvider? clock = null)
    {
        _row = row ?? throw new ArgumentNullException(nameof(row));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<OutboxEntry> EnqueueAsync(OutboxEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        lock (_row.Gate)
        {
            _row.Entries[entry.Id] = entry;
            return _row.Entries[entry.Id];
        }
    }

    public async Task<IReadOnlyList<OutboxEntry>> GetDueAsync(int max, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (max <= 0)
        {
            return Array.Empty<OutboxEntry>();
        }

        List<OutboxEntry> due;
        lock (_row.Gate)
        {
            // WR-03: injected clock, never DateTimeOffset.UtcNow, so queued rows become
            // due in step with the pump's TimeProvider.
            DateTimeOffset now = _clock.GetUtcNow();
            due = _row.Entries.Values
                .Where(e =>
                    e.State is OutboxState.Pending or OutboxState.RetryScheduled
                    && (e.NextAttemptUtc is null || e.NextAttemptUtc <= now))
                .OrderBy(e => e.EnqueuedUtc)
                .Take(max)
                .ToList();
        }
        await Task.CompletedTask;
        return due;
    }

    public async Task MarkSentAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_row.Gate)
        {
            if (_row.Entries.TryGetValue(id, out OutboxEntry? existing))
            {
                _row.Entries[id] = existing with { State = OutboxState.Sent, NextAttemptUtc = null };
            }
        }
        await Task.CompletedTask;
    }

    public async Task ScheduleRetryAsync(string id, int attempt, DateTimeOffset nextAttemptUtc, string? ruleName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_row.Gate)
        {
            if (_row.Entries.TryGetValue(id, out OutboxEntry? existing))
            {
                _row.Entries[id] = existing with
                {
                    Attempt = attempt,
                    State = OutboxState.RetryScheduled,
                    NextAttemptUtc = nextAttemptUtc,
                    LastErrorRuleName = ruleName,
                };
            }
        }
        await Task.CompletedTask;
    }

    public async Task DeadLetterAsync(string id, string? reason, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_row.Gate)
        {
            if (_row.Entries.TryGetValue(id, out OutboxEntry? existing))
            {
                _row.Entries[id] = existing with
                {
                    State = OutboxState.DeadLettered,
                    LastErrorRuleName = reason,
                    NextAttemptUtc = null,
                };
            }
        }
        await Task.CompletedTask;
    }

    public async Task<bool> TryRemoveAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_row.Gate)
        {
            return _row.Entries.Remove(id);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}