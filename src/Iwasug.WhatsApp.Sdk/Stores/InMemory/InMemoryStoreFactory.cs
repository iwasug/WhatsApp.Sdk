using System.Collections.Concurrent;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;

namespace Iwasug.WhatsApp.Sdk.Stores.InMemory;

/// <summary>
/// In-memory reference implementation filling ALL six factory seams (D-03: one factory
/// fills any gap a host leaves unregistered; STR-02 reference implementation).
/// </summary>
/// <remarks>
/// <para>
/// All state lives under this factory instance in one dictionary keyed by
/// (capability, account) — no global mutable statics (Pitfall 8 / T-01-02). Two accounts
/// never observe each other's data: per-account isolation is structural, and the
/// contract suite proves it (e.g. <c>Two_accounts_isolated_same_address</c>).
/// </para>
/// <para>
/// HONEST POSITIONING (D-09): this is a prototype-capable reference store. It does NOT
/// survive process restart and is not a production persistence guarantee — register your
/// own store implementation behind the same interfaces for production data.
/// </para>
/// <para>
/// Failure ≠ missing (D-19): a missing key returns <c>null</c>; this implementation has no
/// I/O and therefore no failure mode of its own — injected failures are exercised through
/// the test kit's fault-injection decorator (plan 01-03).
/// </para>
/// <para>
/// The factory is <see cref="IAsyncDisposable"/> (added in plan 01-04 for the D-23
/// composition cascade): SDK composition layers that own a fallback factory dispose it
/// after disposing their cached sessions.
/// </para>
/// </remarks>
public sealed class InMemoryStoreFactory
    : ISignalSessionStoreFactory,
      IIdentityStoreFactory,
      IPreKeyStoreFactory,
      IRecentMessageCacheStoreFactory,
      ISafetyStateStoreFactory,
      ICredentialStoreFactory,
      IAsyncDisposable
{
    private const string SignalSessionCapability = "signal-session";
    private const string IdentityCapability = "identity";
    private const string PreKeyCapability = "prekey";
    private const string RecentMessageCacheCapability = "recent-message-cache";
    private const string SafetyStateCapability = "safety-state";
    private const string CredentialCapability = "credential";

    private readonly ConcurrentDictionary<(string Capability, AccountId Account), object> _state = new();

    // 0 = live, 1 = disposed: belt-and-braces (WR-01) so a CreateAsync that
    // races factory disposal can never mint a fresh session against cleared
    // state. StoreRegistry additionally guards its side of the seam.
    private int _disposed;

    /// <inheritdoc />
    public ValueTask<ISignalSessionStore> CreateAsync(AccountId accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        // GetOrAdd: every view created for the same (capability, account) observes the
        // same underlying records, so a later CreateAsync still sees earlier writes.
        var records = (ConcurrentDictionary<string, SignalSessionRecord>)GetOrAdd(accountId, SignalSessionCapability, () => new ConcurrentDictionary<string, SignalSessionRecord>());

        return ValueTask.FromResult<ISignalSessionStore>(new InMemorySignalSessionStore(records));
    }

    // The five factories beyond ISignalSessionStoreFactory are explicit interface
    // implementations: C# forbids members that differ only by return type (CS0111), and
    // all six factory signatures share (AccountId, CancellationToken). Callers reach
    // them through the interface, which is exactly how the contract kit binds them.

    /// <inheritdoc />
    ValueTask<IIdentityStore> IIdentityStoreFactory.CreateAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        var records = (ConcurrentDictionary<string, SignalIdentityRecord>)GetOrAdd(accountId, IdentityCapability, () => new ConcurrentDictionary<string, SignalIdentityRecord>());

        return ValueTask.FromResult<IIdentityStore>(new InMemoryIdentityStore(records));
    }

    /// <inheritdoc />
    ValueTask<IPreKeyStore> IPreKeyStoreFactory.CreateAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        object state = GetOrAdd(accountId, PreKeyCapability, () => new PreKeyState());

        return ValueTask.FromResult<IPreKeyStore>(new InMemoryPreKeyStore((PreKeyState)state));
    }

    /// <inheritdoc />
    ValueTask<IRecentMessageCacheStore> IRecentMessageCacheStoreFactory.CreateAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        object state = GetOrAdd(accountId, RecentMessageCacheCapability, () => new RecentMessageCacheState());

        return ValueTask.FromResult<IRecentMessageCacheStore>(new InMemoryRecentMessageCacheStore((RecentMessageCacheState)state));
    }

    /// <inheritdoc />
    ValueTask<ISafetyStateStore> ISafetyStateStoreFactory.CreateAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        object state = GetOrAdd(accountId, SafetyStateCapability, () => new SafetyStateRow());

        return ValueTask.FromResult<ISafetyStateStore>(new InMemorySafetyStateStore((SafetyStateRow)state));
    }

    /// <inheritdoc />
    ValueTask<ICredentialStore> ICredentialStoreFactory.CreateAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        object state = GetOrAdd(accountId, CredentialCapability, () => new CredentialRow());

        return ValueTask.FromResult<ICredentialStore>(new InMemoryCredentialStore((CredentialRow)state));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Factory-level disposal is part of the D-23 cascade wired by plan 01-04: the owning
    /// <see cref="StoreRegistry"/> (or any SDK composition layer) disposes the fallback
    /// factory after disposing its cached sessions. Disposal clears all account state and
    /// invalidates the factory — a disposed factory throws
    /// <see cref="ObjectDisposedException"/> from any later <c>CreateAsync</c> (WR-01),
    /// rather than serving fresh, empty state. Disposal also disposes every per-account
    /// recent-message-cache gate known at teardown (IN-02: factory-owned resources are
    /// released symmetrically) — views created before disposal must not be used
    /// afterwards; the owning <see cref="StoreRegistry"/> disposes its cached sessions
    /// before the factory, so no live view observes the teardown in the SDK-owned flow.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);

        // IN-02: release the per-account SemaphoreSlim gates before dropping the state
        // that owns them. A gate created concurrently with this teardown is simply not
        // in the snapshot — it holds no unmanaged resources until its WaitHandle is
        // touched, so the GC reclaims it harmlessly.
        foreach (RecentMessageCacheState cache in _state.Values.OfType<RecentMessageCacheState>())
        {
            cache.Gate.Dispose();
        }

        _state.Clear();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(InMemoryStoreFactory));
        }
    }

    private object GetOrAdd(AccountId accountId, string capability, Func<object> factory)
    {
        // The dictionary's own factory runs once per (capability, account); a cast of a
        // concurrently-added value is safe because every reader of this key uses the
        // same capability constant and therefore the same factory.
        return _state.GetOrAdd((capability, accountId), static (_, f) => f(), factory);
    }

    /// <summary>
    /// Light account-scoped view over one account's records (D-02: the instance is the
    /// namespace; it carries no accountId).
    /// </summary>
    private sealed class InMemorySignalSessionStore(ConcurrentDictionary<string, SignalSessionRecord> records)
        : ISignalSessionStore
    {
        public Task<SignalSessionRecord?> GetAsync(string address, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Defensive copy on the read boundary too: callers must never alias
            // (and later corrupt) the store's ratchet-state buffer.
            return Task.FromResult(records.TryGetValue(address, out SignalSessionRecord? record)
                ? record with { SessionState = (byte[])record.SessionState.Clone() }
                : null);
        }

        public Task PutAsync(string address, SignalSessionRecord session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The value is observable to any subsequent read before this task completes
            // (D-06 store-side semantics; in-memory writes are synchronous). Clone the
            // mutable ratchet-state buffer so a caller mutating its own array after the
            // write cannot silently rewrite the persisted record (WR-02).
            records[address] = session with { SessionState = (byte[])session.SessionState.Clone() };
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(string address, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(records.TryRemove(address, out _));
        }

        public ValueTask DisposeAsync()
        {
            // Views own nothing global — disposal releases only this view. Factory-level
            // disposal clearing all account state is wired in plan 01-04 (D-23 cascade).
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Account-scoped view over identity records (mirrors the session store).</summary>
    private sealed class InMemoryIdentityStore(ConcurrentDictionary<string, SignalIdentityRecord> records)
        : IIdentityStore
    {
        public Task<SignalIdentityRecord?> GetAsync(string address, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(records.TryGetValue(address, out SignalIdentityRecord? record) ? record : null);
        }

        public Task PutAsync(string address, SignalIdentityRecord identity, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Observable to any subsequent read before the task completes (D-06 store-side).
            records[address] = identity;
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(string address, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(records.TryRemove(address, out _));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Per-account pre-key state: bundles keyed by address and one-time pre-keys keyed by
    /// integer id are two independent sub-stores (bundle CRUD never touches pre-key CRUD).
    /// </summary>
    private sealed class PreKeyState
    {
        internal ConcurrentDictionary<string, PreKeyBundle> Bundles { get; } = new();

        internal ConcurrentDictionary<int, PreKeyRecordEntry> PreKeys { get; } = new();
    }

    /// <summary>Account-scoped view over the two pre-key sub-stores.</summary>
    private sealed class InMemoryPreKeyStore(PreKeyState state) : IPreKeyStore
    {
        public Task<PreKeyBundle?> GetBundleAsync(string address, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(state.Bundles.TryGetValue(address, out PreKeyBundle? bundle) ? bundle : null);
        }

        public Task PutBundleAsync(string address, PreKeyBundle bundle, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.Bundles[address] = bundle;
            return Task.CompletedTask;
        }

        public Task<PreKeyRecordEntry?> GetPreKeyAsync(int preKeyId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(state.PreKeys.TryGetValue(preKeyId, out PreKeyRecordEntry? key) ? key : null);
        }

        public Task PutPreKeyAsync(int preKeyId, PreKeyRecordEntry key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.PreKeys[preKeyId] = key;
            return Task.CompletedTask;
        }

        public Task<bool> RemovePreKeyAsync(int preKeyId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(state.PreKeys.TryRemove(preKeyId, out _));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Per-account cache state: entries keyed by message key PLUS an insertion-ordered
    /// index (oldest first) so <c>GetRecentAsync</c>, <c>TrimOldestAsync</c>, and
    /// <c>PurgeExpiredAsync</c> operate on observable order. A per-account
    /// <see cref="SemaphoreSlim"/> guards the ordered index — deterministic ordering where
    /// ordering matters, per the Pattern 4 lock-per-account rule.
    /// </summary>
    private sealed class RecentMessageCacheState
    {
        internal ConcurrentDictionary<string, RecentMessageEntry> Entries { get; } = new();

        /// <summary>Message keys in insertion order (oldest first). Guarded by <see cref="Gate"/>.</summary>
        internal List<string> Index { get; } = new();

        internal SemaphoreSlim Gate { get; } = new(1, 1);
    }

    /// <summary>Account-scoped view over the recent-message cache.</summary>
    private sealed class InMemoryRecentMessageCacheStore(RecentMessageCacheState state)
        : IRecentMessageCacheStore
    {
        public async Task<RecentMessageEntry?> GetAsync(string messageKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return state.Entries.TryGetValue(messageKey, out RecentMessageEntry? entry) ? entry : null;
            }
            finally
            {
                state.Gate.Release();
            }
        }

        public async Task PutAsync(RecentMessageEntry entry, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Last-write-wins on the same key; a first insert joins the index tail.
                if (!state.Entries.ContainsKey(entry.MessageKey))
                {
                    state.Index.Add(entry.MessageKey);
                }

                state.Entries[entry.MessageKey] = entry;
            }
            finally
            {
                state.Gate.Release();
            }
        }

        public async Task<bool> RemoveAsync(string messageKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!state.Entries.TryRemove(messageKey, out _))
                {
                    return false;
                }

                state.Index.Remove(messageKey);
                return true;
            }
            finally
            {
                state.Gate.Release();
            }
        }

        public async Task<IReadOnlyList<RecentMessageEntry>> GetRecentAsync(string chatJid, int limit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (limit <= 0)
            {
                return Array.Empty<RecentMessageEntry>();
            }

            await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Newest first: walk the insertion-ordered index backwards, same chat only.
                var result = new List<RecentMessageEntry>(Math.Min(limit, state.Index.Count));
                for (int i = state.Index.Count - 1; i >= 0 && result.Count < limit; i--)
                {
                    if (state.Entries.TryGetValue(state.Index[i], out RecentMessageEntry? entry)
                        && string.Equals(entry.ChatJid, chatJid, StringComparison.Ordinal))
                    {
                        result.Add(entry);
                    }
                }

                return result;
            }
            finally
            {
                state.Gate.Release();
            }
        }

        public async Task TrimOldestAsync(int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count <= 0)
            {
                return;
            }

            await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Removes only what remains — a second TrimOldest(n) after an empty
                // account removes nothing (no negative over-trim).
                int remove = Math.Min(count, state.Index.Count);
                for (int i = 0; i < remove; i++)
                {
                    state.Entries.TryRemove(state.Index[i], out _);
                }

                state.Index.RemoveRange(0, remove);
            }
            finally
            {
                state.Gate.Release();
            }
        }

        public async Task PurgeExpiredAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Strictly-before cutoff only; entries at or after the cutoff are untouched.
                string[] expired = state.Index
                    .Where(key => state.Entries.TryGetValue(key, out RecentMessageEntry? entry)
                        && entry.TimestampUtc < cutoffUtc)
                    .ToArray();

                foreach (string? key in expired)
                {
                    state.Entries.TryRemove(key, out _);
                }

                state.Index.RemoveAll(key => !state.Entries.ContainsKey(key));
            }
            finally
            {
                state.Gate.Release();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Per-account single safety-state row (last-write-wins).</summary>
    private sealed class SafetyStateRow
    {
        internal object Gate { get; } = new();

        internal SafetyStateRecord? Value;
    }

    /// <summary>Account-scoped view over the single safety-state row.</summary>
    private sealed class InMemorySafetyStateStore(SafetyStateRow row) : ISafetyStateStore
    {
        public Task<SafetyStateRecord?> GetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (row.Gate)
            {
                return Task.FromResult(row.Value);
            }
        }

        public Task PutAsync(SafetyStateRecord state, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Observable to any subsequent read before the task completes (D-06 store-side).
            lock (row.Gate)
            {
                row.Value = state;
            }

            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (row.Gate)
            {
                if (row.Value is null)
                {
                    return Task.FromResult(false);
                }

                row.Value = null;
                return Task.FromResult(true);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Per-account single credential row (last-write-wins).</summary>
    private sealed class CredentialRow
    {
        internal object Gate { get; } = new();

        internal CredentialRecord? Value;
    }

    /// <summary>Account-scoped view over the single credential row.</summary>
    private sealed class InMemoryCredentialStore(CredentialRow row) : ICredentialStore
    {
        public Task<CredentialRecord?> GetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (row.Gate)
            {
                return Task.FromResult(CloneRecord(row.Value));
            }
        }

        public Task PutAsync(CredentialRecord credentials, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(credentials);
            // Observable to any subsequent read before the task completes (D-06 store-side).
            lock (row.Gate)
            {
                row.Value = CloneRecord(credentials);
            }

            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (row.Gate)
            {
                if (row.Value is null)
                {
                    return Task.FromResult(false);
                }

                row.Value = null;
                return Task.FromResult(true);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        // Credential rows carry mutable key buffers: clone on both boundaries so a
        // caller mutating its own array after a write (or after a read) cannot
        // silently rewrite or observe the persisted row (WR-02 precedent).
        private static CredentialRecord? CloneRecord(CredentialRecord? record) =>
            record is null
                ? null
                : record with
                {
                    NoiseKeyPrivate = (byte[])record.NoiseKeyPrivate.Clone(),
                    IdentityKeyPrivate = (byte[])record.IdentityKeyPrivate.Clone(),
                    SigningKeyPrivate = (byte[])record.SigningKeyPrivate.Clone(),
                    AdvSecret = (byte[])record.AdvSecret.Clone(),
                };
    }
}
