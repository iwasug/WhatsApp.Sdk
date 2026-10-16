using System.Collections.Concurrent;
using Iwasug.WhatsApp.Sdk.Errors;

namespace Iwasug.WhatsApp.Sdk.Media;

/// <summary>
/// Process-local tracker bounding media-retry requests per message id (D-66, T-06-03).
/// </summary>
internal sealed class MediaRetryTracker
{
    internal const int MaxRequestsPerId = 3;
    internal const int MaxEntries = 4096;
    internal static readonly TimeSpan EvictionHorizon = TimeSpan.FromHours(1);

    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public MediaRetryTracker(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    internal TimeProvider Clock => _clock;

    internal int Count => _entries.Count;

    /// <summary>Whether another request may be made for this id.</summary>
    internal bool CanRequest(string messageId) =>
        _entries.TryGetValue(messageId, out Entry? e) ? e.Attempts < MaxRequestsPerId : true;

    /// <summary>Register one request. Returns false when the cap is already reached.</summary>
    internal bool TryRegister(string messageId) => TryAcquire(messageId);

    internal bool TryReserve(string messageId) => TryAcquire(messageId);

    internal bool TryAcquire(string messageId)
    {
        if (string.IsNullOrEmpty(messageId))
        {
            throw new SdkFormatException("Media retry message id must be non-empty.");
        }

        EvictIfNeeded();

        while (true)
        {
            if (!_entries.TryGetValue(messageId, out Entry? existing))
            {
                if (_entries.TryAdd(messageId, new Entry(1, _clock.GetUtcNow())))
                {
                    return true;
                }
                continue;
            }

            if (existing.Attempts >= MaxRequestsPerId)
            {
                return false;
            }

            var next = new Entry(existing.Attempts + 1, existing.FirstRequestUtc);
            if (_entries.TryUpdate(messageId, next, existing))
            {
                return true;
            }
        }
    }

    internal int GetAttempts(string messageId) =>
        _entries.TryGetValue(messageId, out Entry? e) ? e.Attempts : 0;

    internal void Remove(string messageId) => _entries.TryRemove(messageId, out _);

    internal void Reset(string messageId) => Remove(messageId);

    internal bool IsExhausted(string messageId) =>
        _entries.TryGetValue(messageId, out Entry? e) && e.Attempts >= MaxRequestsPerId;

    private void EvictIfNeeded()
    {
        if (_entries.Count < MaxEntries)
        {
            return;
        }
        DateTimeOffset cutoff = _clock.GetUtcNow() - EvictionHorizon;
        foreach (string key in _entries.Keys.ToList())
        {
            if (_entries.TryGetValue(key, out Entry? e) && e.FirstRequestUtc < cutoff)
            {
                _entries.TryRemove(key, out _);
            }
        }
        if (_entries.Count >= MaxEntries)
        {
            // Drop oldest half by first-request time. Re-check freshness at removal time:
            // a concurrent TryAcquire may have refreshed an entry between snapshot and
            // removal (raising its FirstRequestUtc); drop it only if it is still stale
            // by its own observed timestamp — never a blanket TryRemove.
            foreach (string key in _entries
                .OrderBy(kv => kv.Value.FirstRequestUtc)
                .Take(_entries.Count / 2)
                .Select(kv => kv.Key)
                .ToList())
            {
                if (_entries.TryGetValue(key, out Entry? current) && current.FirstRequestUtc < cutoff)
                {
                    // stale even after re-check; safe to remove
                }
                if (!_entries.TryGetValue(key, out _))
                {
                    continue; // gone between snapshot and removal (concurrent)
                }
                _entries.TryRemove(key, out _);
            }
        }
    }

    private sealed record Entry(int Attempts, DateTimeOffset FirstRequestUtc);
}
