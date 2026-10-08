using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Stores;

namespace Iwasug.WhatsApp.Sdk.Tests.StoreContract;

/// <summary>
/// Deliberately-different second implementation of all five factory seams: plain
/// <c>Dictionary&lt;,&gt;</c> collections guarded by a SINGLE lock per factory instance —
/// deliberately NOT the reference store's per-account concurrent map design. Proves
/// success criterion 3 mechanically: the SAME abstract contract classes run green against
/// this implementation and the in-memory reference store with ZERO SDK code changes
/// between them (D-11 / D-10), i.e. the kit does not depend on reference-impl internals.
/// </summary>
/// <remarks>
/// The single-global-lock design is the deliberate contrast to
/// <see cref="Iwasug.WhatsApp.Sdk.Stores.InMemory.InMemoryStoreFactory"/>'s per-account
/// concurrent maps (T-01-05's structural isolation still must hold — the contract facts
/// assert it). Test-project type: never shipped in either NuGet package.
/// </remarks>
public sealed class ScratchStoreFactory
    : ISignalSessionStoreFactory,
      IIdentityStoreFactory,
      IPreKeyStoreFactory,
      IRecentMessageCacheStoreFactory,
      ISafetyStateStoreFactory
{
    // One gate for ALL state on this factory instance — the deliberate anti-pattern
    // (vs. per-account concurrent maps) that proves the contract kit is impl-agnostic.
    private readonly object _gate = new();

    private readonly Dictionary<(AccountId Account, string Key), SignalSessionRecord> _sessions = new();
    private readonly Dictionary<(AccountId Account, string Key), SignalIdentityRecord> _identities = new();
    private readonly Dictionary<(AccountId Account, string Key), PreKeyBundle> _bundles = new();
    private readonly Dictionary<(AccountId Account, int Key), PreKeyRecordEntry> _preKeys = new();
    private readonly Dictionary<(AccountId Account, string Key), RecentMessageEntry> _cacheEntries = new();

    /// <summary>Insertion-ordered cache keys per account (oldest first); guarded by <see cref="_gate"/>.</summary>
    private readonly Dictionary<AccountId, List<string>> _cacheIndex = new();

    private readonly Dictionary<AccountId, SafetyStateRecord> _safetyRows = new();

    /// <inheritdoc />
    public ValueTask<ISignalSessionStore> CreateAsync(AccountId accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ISignalSessionStore>(new SignalSessionView(this, accountId));
    }

    // Explicit implementations for the remaining four seams: C# forbids members that
    // differ only by return type (CS0111) — callers reach them through the interface,
    // exactly how the contract-kit bindings bind them.

    /// <inheritdoc />
    ValueTask<IIdentityStore> IIdentityStoreFactory.CreateAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IIdentityStore>(new IdentityView(this, accountId));
    }

    /// <inheritdoc />
    ValueTask<IPreKeyStore> IPreKeyStoreFactory.CreateAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IPreKeyStore>(new PreKeyView(this, accountId));
    }

    /// <inheritdoc />
    ValueTask<IRecentMessageCacheStore> IRecentMessageCacheStoreFactory.CreateAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IRecentMessageCacheStore>(new CacheView(this, accountId));
    }

    /// <inheritdoc />
    ValueTask<ISafetyStateStore> ISafetyStateStoreFactory.CreateAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ISafetyStateStore>(new SafetyView(this, accountId));
    }

    private sealed class SignalSessionView(ScratchStoreFactory factory, AccountId account) : ISignalSessionStore
    {
        public Task<SignalSessionRecord?> GetAsync(string address, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (factory._gate)
            {
                return Task.FromResult(factory._sessions.TryGetValue((account, address), out var record)
                    ? record with { SessionState = (byte[])record.SessionState.Clone() }
                    : null);
            }
        }

        public Task PutAsync(string address, SignalSessionRecord session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (factory._gate)
            {
                factory._sessions[(account, address)] = session with { SessionState = (byte[])session.SessionState.Clone() };
            }

            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(string address, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (factory._gate)
            {
                return Task.FromResult(factory._sessions.Remove((account, address)));
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class IdentityView(ScratchStoreFactory factory, AccountId account) : IIdentityStore
    {
        public Task<SignalIdentityRecord?> GetAsync(string address, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (factory._gate)
            {
                return Task.FromResult(factory._identities.TryGetValue((account, address), out var record) ? record : null);
            }
        }

        public Task PutAsync(string address, SignalIdentityRecord identity, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (factory._gate)
            {
                factory._identities[(account, address)] = identity;
            }

            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(string address, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (factory._gate)
            {
                return Task.FromResult(factory._identities.Remove((account, address)));
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PreKeyView(ScratchStoreFactory factory, AccountId account) : IPreKeyStore
    {
        public Task<PreKeyBundle?> GetBundleAsync(string address, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (factory._gate)
            {
                return Task.FromResult(factory._bundles.TryGetValue((account, address), out var bundle) ? bundle : null);
            }
        }

        public Task PutBundleAsync(string address, PreKeyBundle bundle, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (factory._gate)
            {
                factory._bundles[(account, address)] = bundle;
            }

            return Task.CompletedTask;
        }

        public Task<PreKeyRecordEntry?> GetPreKeyAsync(int preKeyId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (factory._gate)
            {
                return Task.FromResult(factory._preKeys.TryGetValue((account, preKeyId), out var key) ? key : null);
            }
        }

        public Task PutPreKeyAsync(int preKeyId, PreKeyRecordEntry key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (factory._gate)
            {
                factory._preKeys[(account, preKeyId)] = key;
            }

            return Task.CompletedTask;
        }

        public Task<bool> RemovePreKeyAsync(int preKeyId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (factory._gate)
            {
                return Task.FromResult(factory._preKeys.Remove((account, preKeyId)));
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CacheView(ScratchStoreFactory factory, AccountId account) : IRecentMessageCacheStore
    {
        public Task<RecentMessageEntry?> GetAsync(string messageKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (factory._gate)
            {
                return Task.FromResult(factory._cacheEntries.TryGetValue((account, messageKey), out var entry) ? entry : null);
            }
        }

        public Task PutAsync(RecentMessageEntry entry, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (factory._gate)
            {
                if (!factory._cacheEntries.ContainsKey((account, entry.MessageKey)))
                {
                    IndexOf(account).Add(entry.MessageKey);
                }

                factory._cacheEntries[(account, entry.MessageKey)] = entry;
            }

            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(string messageKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (factory._gate)
            {
                if (!factory._cacheEntries.Remove((account, messageKey)))
                {
                    return Task.FromResult(false);
                }

                IndexOf(account).Remove(messageKey);
                return Task.FromResult(true);
            }
        }

        public Task<IReadOnlyList<RecentMessageEntry>> GetRecentAsync(string chatJid, int limit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (limit <= 0)
            {
                return Task.FromResult<IReadOnlyList<RecentMessageEntry>>(Array.Empty<RecentMessageEntry>());
            }

            lock (factory._gate)
            {
                var index = IndexOf(account);
                var result = new List<RecentMessageEntry>(Math.Min(limit, index.Count));
                for (var i = index.Count - 1; i >= 0 && result.Count < limit; i--)
                {
                    if (factory._cacheEntries.TryGetValue((account, index[i]), out var entry)
                        && string.Equals(entry.ChatJid, chatJid, StringComparison.Ordinal))
                    {
                        result.Add(entry);
                    }
                }

                return Task.FromResult<IReadOnlyList<RecentMessageEntry>>(result);
            }
        }

        public Task TrimOldestAsync(int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count <= 0)
            {
                return Task.CompletedTask;
            }

            lock (factory._gate)
            {
                var index = IndexOf(account);
                var remove = Math.Min(count, index.Count);
                for (var i = 0; i < remove; i++)
                {
                    factory._cacheEntries.Remove((account, index[i]));
                }

                index.RemoveRange(0, remove);
            }

            return Task.CompletedTask;
        }

        public Task PurgeExpiredAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (factory._gate)
            {
                var index = IndexOf(account);
                var expired = index
                    .Where(key => factory._cacheEntries.TryGetValue((account, key), out var entry)
                        && entry.TimestampUtc < cutoffUtc)
                    .ToArray();

                foreach (var key in expired)
                {
                    factory._cacheEntries.Remove((account, key));
                }

                index.RemoveAll(key => !factory._cacheEntries.ContainsKey((account, key)));
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private List<string> IndexOf(AccountId accountId)
        {
            if (!factory._cacheIndex.TryGetValue(accountId, out var index))
            {
                index = new List<string>();
                factory._cacheIndex[accountId] = index;
            }

            return index;
        }
    }

    private sealed class SafetyView(ScratchStoreFactory factory, AccountId account) : ISafetyStateStore
    {
        public Task<SafetyStateRecord?> GetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (factory._gate)
            {
                return Task.FromResult(factory._safetyRows.TryGetValue(account, out var row) ? row : null);
            }
        }

        public Task PutAsync(SafetyStateRecord state, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (factory._gate)
            {
                factory._safetyRows[account] = state;
            }

            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (factory._gate)
            {
                return Task.FromResult(factory._safetyRows.Remove(account));
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
