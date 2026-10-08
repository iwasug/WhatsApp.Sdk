using System.Collections.Concurrent;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Stores.InMemory;
using Microsoft.Extensions.Logging;

namespace Iwasug.WhatsApp.Sdk.Stores;

/// <summary>
/// SDK-side composition layer over the five store seams: resolves the account-scoped store
/// set for one account with host-first + in-memory-fallback semantics (D-03) and caches the
/// resolved session for the account's lifetime (D-24: long-lived per account — created once
/// at account add, reused until remove/shutdown).
/// </summary>
/// <remarks>
/// <para>
/// Precedence (D-03): when the host registered a factory for a capability via
/// <see cref="StoreRegistryOptions"/>, its <c>CreateAsync</c> is awaited (D-22) and the host
/// instance is used for that capability; capabilities left unregistered fall back to the
/// registry's shared <see cref="InMemoryStoreFactory"/> instance. Every capability of the
/// resolved <see cref="StoreSession"/> is therefore always non-null.
/// </para>
/// <para>
/// Ownership and disposal (D-23): the SDK owns every store instance it creates — fallback
/// instances AND instances obtained by calling a host factory (the SDK invoked the factory,
/// so the hand-over is SDK-owned) — and disposes all five when the account's session is
/// disposed or the registry shuts down. Disposal aggregates: every store gets a dispose
/// attempt even when one throws.
/// </para>
/// <para>
/// Caching (D-24): store instances are long-lived per account, not per-operation. A second
/// <see cref="CreateSessionAsync"/> call with the same <see cref="AccountId"/> returns the
/// cached session without creating new store instances. Under parallel per-account access
/// the cache is a concurrent structure and creation runs exactly once per account; a failed
/// creation is evicted so a later caller can retry instead of observing a poisoned cache.
/// </para>
/// <para>
/// Durable-write routing contract (Phase 2+): when the protocol loop performs
/// durable-capability writes (identity, signal sessions, pre-keys, safety state) through a
/// <see cref="StoreSession"/>, those writes MUST route through
/// <see cref="Runtime.DurableWriteRetryPolicy"/> (bounded retry with short backoff, D-18) so
/// no state is acknowledged to the protocol layer before it is persisted (D-06). The
/// recent-message cache is NOT routed through the retry policy (D-20: log-and-continue is
/// the cache-writing machinery's own policy, arriving with Phase 4/5). Phase 1 delivers no
/// protocol caller — this contract is declared on the consumer side here and the policy
/// ships as production SDK code with unit proofs.
/// </para>
/// <para>
/// Uniqueness (D-26): the cache already prevents re-creation for a known account;
/// duplicate-account rejection is enforced by the future account-adding API through
/// <see cref="EnsureUnique"/> — that validation is written and unit-tested now rather than
/// invented in Phase 7.
/// </para>
/// </remarks>
public sealed class StoreRegistry : IAsyncDisposable
{
    private readonly StoreRegistryOptions _options;
    private readonly ILogger<StoreRegistry>? _logger;
    private readonly InMemoryStoreFactory _fallback = new();

    // Interface-typed views over the fallback factory: the four non-session factory seams
    // are explicit interface implementations on InMemoryStoreFactory (all five CreateAsync
    // signatures are identical except for the return type), so they must be dispatched
    // through the interface to reach the right overload.
    private readonly ISignalSessionStoreFactory _fallbackSessions;
    private readonly IIdentityStoreFactory _fallbackIdentity;
    private readonly IPreKeyStoreFactory _fallbackPreKeys;
    private readonly IRecentMessageCacheStoreFactory _fallbackCache;
    private readonly ISafetyStateStoreFactory _fallbackSafety;

    // D-24 session cache. Lazy<Task<T>> guarantees creation runs exactly once per account
    // under parallel access (the Lazy memoizes its factory); a faulted creation is evicted
    // so a later caller can retry.
    private readonly ConcurrentDictionary<AccountId, Lazy<Task<StoreSession>>> _sessions = new();

    // 0 = live, 1 = disposing/disposed (WR-03): CreateSessionAsync honors it,
    // DisposeAsync sets it first so post-dispose use throws ObjectDisposedException
    // and no new session can be registered after the shutdown cascade begins.
    private int _disposed;

    // Serializes "check flag → GetOrAdd" in CreateSessionAsync against "set flag
    // → snapshot" in DisposeAsync so a preempted registration can never insert a
    // live session after the shutdown drain completes (WR-01). Contention is a
    // once-per-account call path, so a plain lock is sufficient.
    private readonly object _lifecycleGate = new();

    /// <summary>
    /// Creates a registry over <paramref name="options"/>. The registry takes ownership of
    /// resolving, caching, and disposing store instances (D-23/D-24); it does not take
    /// ownership of the options object.
    /// </summary>
    /// <param name="options">Host registration surface — factories per capability, subset allowed (D-03).</param>
    /// <param name="logger">Optional logger; D-21 failure logging works when a logger is present and is skipped when absent.</param>
    public StoreRegistry(StoreRegistryOptions options, ILogger<StoreRegistry>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _fallbackSessions = _fallback;
        _fallbackIdentity = _fallback;
        _fallbackPreKeys = _fallback;
        _fallbackCache = _fallback;
        _fallbackSafety = _fallback;
    }

    /// <summary>
    /// Resolves the account-scoped store set for <paramref name="accountId"/> (D-02/D-03):
    /// host factories first, in-memory fallback for every unregistered capability. The
    /// session is cached and long-lived (D-24) — a second call with the same account id
    /// returns the cached session without creating new store instances.
    /// </summary>
    /// <param name="accountId">Opaque account key (D-25) — must come from <see cref="Accounts.AccountId.Create"/>.</param>
    /// <param name="cancellationToken">Cancellation token (applies to the creating call; the cached session is shared afterwards).</param>
    /// <returns>The account's resolved store session.</returns>
    /// <exception cref="SdkFormatException"><paramref name="accountId"/> is the default (uninitialized) value.</exception>
    /// <exception cref="SdkStoreException">A host or fallback factory failed to create a store (D-21: logged when a logger is present; the original exception is preserved as the inner exception).</exception>
    public ValueTask<StoreSession> CreateSessionAsync(AccountId accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDefaultAccountId(accountId);

        // Check-then-register is one critical section so DisposeAsync can never
        // observe a half-registered state: either the lazy lands before the
        // shutdown snapshot (and is drained), or the flag is already set and
        // this throws (WR-01). The gate alone cannot close the force-creation
        // window (a preemption between gate release and `lazy.Value`), so the
        // creation path re-checks the flag before resolving any store and the
        // completed-session paths below re-check it before handing a session
        // to the caller.
        Lazy<Task<StoreSession>> lazy;
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(StoreRegistry));

            // The value factory may run more than once under a race; ConcurrentDictionary keeps
            // exactly one winner, so exactly one Lazy (and therefore one creation) per account
            // survives. The Lazy's own factory references the winning lazy through the captured
            // local so the session can evict exactly its own entry.
            lazy = null!;
            lazy = _sessions.GetOrAdd(
                accountId,
                id => new Lazy<Task<StoreSession>>(() => CreateSessionCoreAsync(id, cancellationToken, lazy)));
        }

        Task<StoreSession> creation;
        try
        {
            creation = lazy.Value;
        }
        catch
        {
            Evict(accountId, lazy);
            throw;
        }

        if (creation.IsCompletedSuccessfully)
        {
            // The drain may have disposed this session between our registration
            // and the completed check; never hand out a post-dispose session.
            if (Volatile.Read(ref _disposed) != 0)
            {
                Evict(accountId, lazy);
                return new ValueTask<StoreSession>(DisposeAndThrowAsync(creation.Result));
            }

            return new ValueTask<StoreSession>(creation.Result);
        }

        return AwaitCreationAsync(accountId, lazy, creation);
    }

    /// <summary>
    /// D-26 uniqueness validation for the future account-adding API: rejects an account id
    /// that already has a cached session. <see cref="CreateSessionAsync"/> does NOT throw
    /// for a known id (it returns the cache per D-24) — the registration path is the caller
    /// that must enforce uniqueness, and it enforces it here.
    /// </summary>
    /// <remarks>
    /// Check-then-act by design (IN-09): this is a VALIDATION helper, not an atomic
    /// reservation — two racing registrants can both pass and the loser must be rejected
    /// by the registration itself. When the Phase 7 account-adding API lands, it MUST
    /// perform the actual add atomically (a single <c>GetOrAdd</c>-style insertion whose
    /// loser observes the winner and throws <see cref="SdkDuplicateAccountException"/>)
    /// and treat this method as a fast pre-check only, never as the enforcement.
    /// </remarks>
    /// <param name="accountId">The account id the caller intends to register.</param>
    /// <exception cref="SdkDuplicateAccountException">The account id already exists (D-26).</exception>
    internal void EnsureUnique(AccountId accountId)
    {
        ThrowIfDefaultAccountId(accountId);

        if (_sessions.ContainsKey(accountId))
        {
            // Message discipline (T-01-01): the rule is named, the host account value is not.
            throw new SdkDuplicateAccountException(
                "An account with this id is already registered (D-26): account ids must be unique.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Set the flag and snapshot the session cache under the lifecycle gate:
        // any CreateSessionAsync that observes the flag throws
        // ObjectDisposedException instead of resolving a fresh session against
        // a disposed registry (WR-03), and no new entry can be added after the
        // snapshot — so a single drain pass is sufficient (WR-01).
        KeyValuePair<AccountId, Lazy<Task<StoreSession>>>[] snapshot;
        lock (_lifecycleGate)
        {
            Interlocked.Exchange(ref _disposed, 1);
            snapshot = _sessions.ToArray();
        }

        List<Exception>? failures = null;

        // Snapshot-and-evict: remove each entry before disposing its session so a racing
        // CreateSessionAsync does not resolve to a session that is being torn down.
        foreach (var kvp in snapshot)
        {
            Evict(kvp.Key, kvp.Value);
            if (!kvp.Value.IsValueCreated)
            {
                // Never forced before shutdown: a late force faults inside
                // CreateSessionCoreAsync's disposed-check instead of creating.
                continue;
            }

            StoreSession? session;
            try
            {
                session = await kvp.Value.Value.ConfigureAwait(false);
            }
            catch
            {
                // Creation failed; nothing was created to dispose (rollback already ran).
                continue;
            }

            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        // The fallback factory participates in the D-23 disposal cascade (it implements
        // IAsyncDisposable to clear its state); it holds no unmanaged resources, but the
        // registry disposes it for symmetry with the host-owned session teardown.
        try
        {
            await _fallback.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(ex);
        }

        if (failures is not null)
        {
            throw new AggregateException("One or more store sessions failed to dispose.", failures);
        }
    }

    private async Task<StoreSession> CreateSessionCoreAsync(AccountId accountId, CancellationToken cancellationToken, Lazy<Task<StoreSession>> self)
    {
        // Load-bearing WR-01 guard: a lazy registered before the shutdown snapshot
        // but forced after it (or one evicted-not-created by the drain) must fault
        // here instead of minting a live session against a torn-down fallback.
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(StoreRegistry));
        }

        // Rollback list: stores created before a later capability fails are SDK-owned
        // (D-23) and must not leak — they are disposed before the failure propagates.
        var created = new List<IAsyncDisposable>(5);
        try
        {
            // Host factories are hoisted into locals: a null-conditional method group
            // (expr?.M) cannot convert to a delegate, and the local snapshot lets the
            // lambda's nullability flow analysis work. The snapshot also pins the host's
            // registration for the whole resolution instead of re-reading the property.
            var signalFactory = _options.SignalSessionFactory;
            var signal = await ResolveStoreAsync(
                signalFactory is null
                    ? null
                    : (id, token) => signalFactory.CreateAsync(id, token),
                _fallbackSessions.CreateAsync,
                "signal-session",
                accountId,
                cancellationToken).ConfigureAwait(false);
            created.Add(signal.Store);

            var identityFactory = _options.IdentityFactory;
            var identity = await ResolveStoreAsync(
                identityFactory is null
                    ? null
                    : (id, token) => identityFactory.CreateAsync(id, token),
                _fallbackIdentity.CreateAsync,
                "identity",
                accountId,
                cancellationToken).ConfigureAwait(false);
            created.Add(identity.Store);

            var preKeyFactory = _options.PreKeyFactory;
            var preKeys = await ResolveStoreAsync(
                preKeyFactory is null
                    ? null
                    : (id, token) => preKeyFactory.CreateAsync(id, token),
                _fallbackPreKeys.CreateAsync,
                "prekey",
                accountId,
                cancellationToken).ConfigureAwait(false);
            created.Add(preKeys.Store);

            var cacheFactory = _options.RecentMessageCacheFactory;
            var cache = await ResolveStoreAsync(
                cacheFactory is null
                    ? null
                    : (id, token) => cacheFactory.CreateAsync(id, token),
                _fallbackCache.CreateAsync,
                "recent-message-cache",
                accountId,
                cancellationToken).ConfigureAwait(false);
            created.Add(cache.Store);

            var safetyFactory = _options.SafetyStateFactory;
            var safety = await ResolveStoreAsync(
                safetyFactory is null
                    ? null
                    : (id, token) => safetyFactory.CreateAsync(id, token),
                _fallbackSafety.CreateAsync,
                "safety-state",
                accountId,
                cancellationToken).ConfigureAwait(false);
            created.Add(safety.Store);

            var session = new StoreSession(
                signal.Store,
                identity.Store,
                preKeys.Store,
                cache.Store,
                safety.Store,
                [signal.FromHost, identity.FromHost, preKeys.FromHost, cache.FromHost, safety.FromHost]);
            session.AttachEvictionCallback(() => Evict(accountId, self));
            return session;
        }
        catch
        {
            // Best-effort rollback (D-23): the original creation failure is what the
            // caller must see; dispose failures during rollback are secondary.
            foreach (var store in created)
            {
                try
                {
                    await store.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Rollback disposal failure must not mask the creation failure.
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Resolves one capability: the host factory when registered (D-03 host-first), the
    /// in-memory fallback otherwise. Reports where the instance came from so the session
    /// can record provenance (D-23 diagnostics).
    /// </summary>
    private async ValueTask<(TStore Store, bool FromHost)> ResolveStoreAsync<TStore>(
        Func<AccountId, CancellationToken, ValueTask<TStore>>? hostCreate,
        Func<AccountId, CancellationToken, ValueTask<TStore>> fallbackCreate,
        string capability,
        AccountId accountId,
        CancellationToken cancellationToken)
    {
        if (hostCreate is not null)
        {
            return (await InvokeLoggedAsync(hostCreate, accountId, cancellationToken, capability).ConfigureAwait(false), FromHost: true);
        }

        return (await InvokeLoggedAsync(fallbackCreate, accountId, cancellationToken, capability).ConfigureAwait(false), FromHost: false);
    }

    private async ValueTask<TStore> InvokeLoggedAsync<TStore>(
        Func<AccountId, CancellationToken, ValueTask<TStore>> create,
        AccountId accountId,
        CancellationToken cancellationToken,
        string capability)
    {
        try
        {
            return await create(accountId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // D-21 at the host→SDK trust boundary, via the shared error surface: the
            // failure surfaces as an SdkStoreException to the awaiting caller AND one
            // ILogger warning naming the operation and account id — never record
            // payloads or the inner exception's message (T-01-12).
            throw StoreErrorSurface.LogAndWrap(
                ex,
                _logger,
                $"store factory for capability '{capability}'",
                accountId);
        }
    }

    private void Evict(AccountId accountId, Lazy<Task<StoreSession>> lazy)
    {
        // Exact-pair removal: only the entry this session owns is evicted, so a
        // concurrently re-created entry for the same account is never dropped.
        _sessions.TryRemove(new KeyValuePair<AccountId, Lazy<Task<StoreSession>>>(accountId, lazy));
    }

    private async ValueTask<StoreSession> AwaitCreationAsync(AccountId accountId, Lazy<Task<StoreSession>> lazy, Task<StoreSession> creation)
    {
        try
        {
            var session = await creation.ConfigureAwait(false);
            // The drain may have disposed this session between registration and
            // completion; never hand out a post-dispose session (WR-01).
            if (Volatile.Read(ref _disposed) != 0)
            {
                Evict(accountId, lazy);
                await session.DisposeAsync().ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(StoreRegistry));
            }

            return session;
        }
        catch
        {
            // A faulted creation must not poison the cache (D-24): evict so a later
            // caller can retry with fresh store instances.
            Evict(accountId, lazy);
            throw;
        }
    }

    private static async Task<StoreSession> DisposeAndThrowAsync(StoreSession session)
    {
        await session.DisposeAsync().ConfigureAwait(false);
        throw new ObjectDisposedException(nameof(StoreRegistry));
    }

    private static void ThrowIfDefaultAccountId(AccountId accountId)
    {
        if (accountId.Value is null)
        {
            throw new SdkFormatException(
                "AccountId must be created via AccountId.Create — the default (uninitialized) AccountId is not valid.");
        }
    }
}

/// <summary>
/// The resolved view of one account's five stores (D-02/D-03): host-registered instances
/// where the host registered a factory, in-memory fallback instances where it did not —
/// every property is non-null. This is a resolution RESULT, not a registration surface
/// (D-01 forbids a public aggregate): hosts register factories via
/// <see cref="StoreRegistryOptions"/>, they never construct or register
/// <see cref="StoreSession"/> instances.
/// </summary>
/// <remarks>
/// <para>
/// The five instances are long-lived per account (D-24) and SDK-owned (D-23: the SDK
/// created them, either from the fallback or by invoking the host factory — both paths are
/// SDK-obtained). <see cref="DisposeAsync"/> disposes all five sequentially and aggregates
/// dispose failures so every store gets a dispose attempt (T-01-13). Disposing a session
/// evicts it from its owning registry, so the registry never re-serves a disposed session.
/// </para>
/// <para>
/// Provenance: each instance remembers whether the SDK created it from the fallback or
/// received it from the host factory (tracked privately for diagnostics); disposal treats
/// both paths identically as SDK-owned (D-23).
/// </para>
/// <para>
/// Durable-write routing: durable-capability writes through this session (identity,
/// signal sessions, pre-keys, safety state) MUST route through
/// <see cref="Runtime.DurableWriteRetryPolicy"/> when the Phase 2+ protocol loop invokes
/// them (D-18); the recent-message cache does not (D-20). See
/// <see cref="StoreRegistry"/> for the full contract.
/// </para>
/// </remarks>
public sealed class StoreSession : IAsyncDisposable
{
    /// <summary>Where a resolved store instance came from (diagnostic provenance, D-23).</summary>
    private enum StoreOrigin
    {
        /// <summary>Created by the registry's in-memory fallback factory.</summary>
        Fallback,

        /// <summary>Created by a host factory the SDK invoked.</summary>
        HostFactory,
    }

    private readonly List<(IAsyncDisposable Store, StoreOrigin Origin)> _ownedStores;
    private Action? _evictionCallback;
    private int _disposed;

    /// <summary>
    /// Internal constructor — only <see cref="StoreRegistry"/> resolves sessions.
    /// </summary>
    /// <param name="signalSessions">Account-scoped Signal session store.</param>
    /// <param name="identity">Account-scoped peer identity store.</param>
    /// <param name="preKeys">Account-scoped pre-key store.</param>
    /// <param name="recentMessages">Account-scoped recent-message cache.</param>
    /// <param name="safetyState">Account-scoped ban-safety state row.</param>
    /// <param name="fromHost">Index-aligned with the fixed store order (signal, identity, prekey, cache, safety): true when the instance came from a host factory.</param>
    internal StoreSession(
        ISignalSessionStore signalSessions,
        IIdentityStore identity,
        IPreKeyStore preKeys,
        IRecentMessageCacheStore recentMessages,
        ISafetyStateStore safetyState,
        bool[] fromHost)
    {
        SignalSessions = signalSessions;
        Identity = identity;
        PreKeys = preKeys;
        RecentMessages = recentMessages;
        SafetyState = safetyState;
        _ownedStores =
        [
            (signalSessions, fromHost[0] ? StoreOrigin.HostFactory : StoreOrigin.Fallback),
            (identity, fromHost[1] ? StoreOrigin.HostFactory : StoreOrigin.Fallback),
            (preKeys, fromHost[2] ? StoreOrigin.HostFactory : StoreOrigin.Fallback),
            (recentMessages, fromHost[3] ? StoreOrigin.HostFactory : StoreOrigin.Fallback),
            (safetyState, fromHost[4] ? StoreOrigin.HostFactory : StoreOrigin.Fallback),
        ];
    }

    /// <summary>Account-scoped Signal protocol session store (must-not-lose, D-05).</summary>
    public ISignalSessionStore SignalSessions { get; }

    /// <summary>Account-scoped peer identity store (must-not-lose, D-05).</summary>
    public IIdentityStore Identity { get; }

    /// <summary>Account-scoped pre-key store (must-not-lose, D-05).</summary>
    public IPreKeyStore PreKeys { get; }

    /// <summary>Account-scoped recent-message cache (lossy by definition, D-05/D-20).</summary>
    public IRecentMessageCacheStore RecentMessages { get; }

    /// <summary>Account-scoped ban-safety state row (must-not-lose, D-05).</summary>
    public ISafetyStateStore SafetyState { get; }

    internal void AttachEvictionCallback(Action callback) => _evictionCallback = callback;

    /// <summary>
    /// Disposes every store instance the SDK created for this account (D-23: fallback
    /// instances and host-factory instances alike). Each store gets a dispose attempt even
    /// when another throws; dispose failures are aggregated into one
    /// <see cref="AggregateException"/> (T-01-13). Disposing twice is a no-op.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        List<Exception>? failures = null;
        foreach (var (store, _) in _ownedStores)
        {
            try
            {
                await store.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        // Evict before surfacing failures: the session is dead either way.
        var eviction = Interlocked.Exchange(ref _evictionCallback, null);
        eviction?.Invoke();

        if (failures is not null)
        {
            throw new AggregateException("One or more store instances failed to dispose.", failures);
        }
    }
}
