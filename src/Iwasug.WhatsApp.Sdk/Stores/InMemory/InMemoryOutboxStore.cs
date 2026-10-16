using System.Collections.Concurrent;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Stores;

namespace Iwasug.WhatsApp.Sdk.Stores.InMemory;

/// <summary>In-memory reference implementation of <see cref="IOutboxStore"/> (EXT-01).</summary>
internal sealed class InMemoryOutboxStore : IOutboxStore
{
    private readonly ConcurrentDictionary<string, OutboxEntry> _entries = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public InMemoryOutboxStore()
    {
    }

    public async Task<OutboxEntry> EnqueueAsync(OutboxEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _entries[entry.Id] = entry;
            // Store-side durability (D-06): an independent read observes the row
            // before this task completes; in-memory that is immediate.
            return _entries[entry.Id];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<OutboxEntry>> GetDueAsync(int max, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (max <= 0)
        {
            return Array.Empty<OutboxEntry>();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            List<OutboxEntry> due = _entries.Values
                .Where(e =>
                    e.State is OutboxState.Pending or OutboxState.RetryScheduled
                    && (e.NextAttemptUtc is null || e.NextAttemptUtc <= now))
                .OrderBy(e => e.EnqueuedUtc)
                .Take(max)
                .ToList();
            return due;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkSentAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_entries.TryGetValue(id, out OutboxEntry? existing))
            {
                _entries[id] = existing with { State = OutboxState.Sent, NextAttemptUtc = null };
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ScheduleRetryAsync(string id, int attempt, DateTimeOffset nextAttemptUtc, string? ruleName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_entries.TryGetValue(id, out OutboxEntry? existing))
            {
                _entries[id] = existing with
                {
                    Attempt = attempt,
                    State = OutboxState.RetryScheduled,
                    NextAttemptUtc = nextAttemptUtc,
                    LastErrorRuleName = ruleName,
                };
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeadLetterAsync(string id, string? reason, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_entries.TryGetValue(id, out OutboxEntry? existing))
            {
                _entries[id] = existing with
                {
                    State = OutboxState.DeadLettered,
                    LastErrorRuleName = reason,
                    NextAttemptUtc = null,
                };
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryRemoveAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _entries.TryRemove(id, out _);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}