using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Stores;

namespace Iwasug.WhatsApp.Sdk.Testing.FaultInjection;

/// <summary>
/// Which operations a <see cref="FaultingStoreFactory"/> fails on demand — the fault
/// selector for failure ≠ missing (D-19) contract enforcement.
/// </summary>
public enum FaultMode
{
    /// <summary>Pure pass-through: every operation delegates to the wrapped store.</summary>
    None,

    /// <summary>Every read (Get/GetBundle/GetPreKey/GetRecent) throws <see cref="SdkStoreException"/>.</summary>
    FailReads,

    /// <summary>Every write (Put/Remove/Trim/Purge) throws <see cref="SdkStoreException"/>.</summary>
    FailWrites
}

/// <summary>
/// Kit-public decorator factory that fails wrapped stores on demand — the mechanical
/// seam behind the failure ≠ missing rule (D-19, Pitfall 2): hosts and the kit's own
/// contract classes wrap any factory with this decorator, set <see cref="Mode"/>, and
/// assert that a failing read THROWS <see cref="SdkStoreException"/> while a genuinely
/// missing key still returns <c>null</c>. Shipped as part of the kit so host suites get
/// failure ≠ missing enforcement without writing their own fault plumbing.
/// </summary>
/// <remarks>
/// <para>
/// Test infrastructure by design: this decorator never retries, never swallows, and never
/// maps a fault to a default value — it exists to prove that store consumers observe the
/// strict failure ≠ missing discipline. <see cref="Mode"/> is read at every operation, so
/// it can be toggled after stores have been created (a failing read on one call, a
/// pass-through on the next).
/// </para>
/// <para>
/// Disposal is deliberately NOT cascaded: <see cref="DisposeAsync"/> (and the wrapping
/// stores' <c>DisposeAsync</c>) dispose nothing of the wrapped factory or store — the
/// decorator is test infrastructure and the inner lifecycle belongs to the suite that
/// created it (D-23 ownership stays with the creator).
/// </para>
/// <para>
/// Binding pattern: all five factory interfaces share the <c>CreateAsync</c> signature but
/// differ by return type, so only the Signal-session implementation is implicit and the
/// other four are explicit interface implementations. Bind the decorator to the seam
/// under test through an interface-typed variable —
/// <c>IPreKeyStoreFactory factory = new FaultingStoreFactory(preKey: inner) { Mode = ... };</c>
/// — and call <c>factory.CreateAsync(...)</c> so the correct store type is produced.
/// </para>
/// </remarks>
public sealed class FaultingStoreFactory :
    ISignalSessionStoreFactory,
    IIdentityStoreFactory,
    IPreKeyStoreFactory,
    IRecentMessageCacheStoreFactory,
    ISafetyStateStoreFactory,
    IAsyncDisposable
{
    private readonly ISignalSessionStoreFactory? _signalSession;
    private readonly IIdentityStoreFactory? _identity;
    private readonly IPreKeyStoreFactory? _preKey;
    private readonly IRecentMessageCacheStoreFactory? _recentMessageCache;
    private readonly ISafetyStateStoreFactory? _safetyState;

    /// <summary>
    /// Wraps whichever factories are supplied; a seam without an inner factory cannot
    /// create stores and throws when its <c>CreateAsync</c> is called.
    /// </summary>
    /// <param name="signalSession">Inner factory for the Signal-session seam.</param>
    /// <param name="identity">Inner factory for the identity seam.</param>
    /// <param name="preKey">Inner factory for the pre-key seam.</param>
    /// <param name="recentMessageCache">Inner factory for the recent-message cache seam.</param>
    /// <param name="safetyState">Inner factory for the safety-state seam.</param>
    public FaultingStoreFactory(
        ISignalSessionStoreFactory? signalSession = null,
        IIdentityStoreFactory? identity = null,
        IPreKeyStoreFactory? preKey = null,
        IRecentMessageCacheStoreFactory? recentMessageCache = null,
        ISafetyStateStoreFactory? safetyState = null)
    {
        _signalSession = signalSession;
        _identity = identity;
        _preKey = preKey;
        _recentMessageCache = recentMessageCache;
        _safetyState = safetyState;
    }

    /// <summary>
    /// The active fault selector, consulted at every wrapped operation. Assign
    /// <see cref="FaultMode.FailReads"/> to make reads throw, <see cref="FaultMode.FailWrites"/>
    /// to make writes throw, or <see cref="FaultMode.None"/> to pass everything through.
    /// </summary>
    public FaultMode Mode { get; set; }

    /// <inheritdoc />
    public async ValueTask<ISignalSessionStore> CreateAsync(AccountId accountId, CancellationToken cancellationToken = default)
    {
        ISignalSessionStoreFactory inner = _signalSession ?? throw NotWired(nameof(ISignalSessionStoreFactory));
        return new FaultingSignalSessionStore(this, await inner.CreateAsync(accountId, cancellationToken));
    }

    /// <inheritdoc cref="CreateAsync(AccountId, System.Threading.CancellationToken)" />
    async ValueTask<IIdentityStore> IIdentityStoreFactory.CreateAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        IIdentityStoreFactory inner = _identity ?? throw NotWired(nameof(IIdentityStoreFactory));
        return new FaultingIdentityStore(this, await inner.CreateAsync(accountId, cancellationToken));
    }

    /// <inheritdoc cref="CreateAsync(AccountId, System.Threading.CancellationToken)" />
    async ValueTask<IPreKeyStore> IPreKeyStoreFactory.CreateAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        IPreKeyStoreFactory inner = _preKey ?? throw NotWired(nameof(IPreKeyStoreFactory));
        return new FaultingPreKeyStore(this, await inner.CreateAsync(accountId, cancellationToken));
    }

    /// <inheritdoc cref="CreateAsync(AccountId, System.Threading.CancellationToken)" />
    async ValueTask<IRecentMessageCacheStore> IRecentMessageCacheStoreFactory.CreateAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        IRecentMessageCacheStoreFactory inner = _recentMessageCache ?? throw NotWired(nameof(IRecentMessageCacheStoreFactory));
        return new FaultingRecentMessageCacheStore(this, await inner.CreateAsync(accountId, cancellationToken));
    }

    /// <inheritdoc cref="CreateAsync(AccountId, System.Threading.CancellationToken)" />
    async ValueTask<ISafetyStateStore> ISafetyStateStoreFactory.CreateAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        ISafetyStateStoreFactory inner = _safetyState ?? throw NotWired(nameof(ISafetyStateStoreFactory));
        return new FaultingSafetyStateStore(this, await inner.CreateAsync(accountId, cancellationToken));
    }

    /// <summary>
    /// Disposes nothing: the decorator is test infrastructure and the wrapped factory's
    /// lifecycle belongs to the suite that created it.
    /// </summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Throws the injected <see cref="SdkStoreException"/> when <paramref name="phase"/>
    /// matches the current <see cref="Mode"/>; otherwise returns and the caller delegates
    /// to the wrapped store.
    /// </summary>
    internal static void ThrowIfFaulted(FaultingStoreFactory factory, FaultMode phase, string operation)
    {
        if (factory.Mode == phase)
        {
            throw new SdkStoreException(
                $"Injected store failure (FaultingStoreFactory Mode={phase}): {operation} cannot complete — " +
                "the failure surfaces as this exception and must never be mapped to null or missing data (D-19/D-21).");
        }
    }

    private static InvalidOperationException NotWired(string factoryKind) =>
        new($"No inner {factoryKind} was supplied to FaultingStoreFactory — pass it via the matching constructor parameter for the seam being created.");
}

/// <summary>
/// Account-scoped pass-through/faulting decorator around an
/// <see cref="ISignalSessionStore"/>: reads throw under <see cref="FaultMode.FailReads"/>,
/// writes throw under <see cref="FaultMode.FailWrites"/>, everything else delegates to the
/// wrapped store. Disposal is not cascaded (decorator lifecycle rules, see
/// <see cref="FaultingStoreFactory"/> remarks).
/// </summary>
public sealed class FaultingSignalSessionStore : ISignalSessionStore
{
    private readonly FaultingStoreFactory _factory;
    private readonly ISignalSessionStore _inner;

    /// <summary>Wraps <paramref name="inner"/> with the fault control of <paramref name="factory"/>.</summary>
    public FaultingSignalSessionStore(FaultingStoreFactory factory, ISignalSessionStore inner)
    {
        _factory = factory;
        _inner = inner;
    }

    /// <inheritdoc />
    public Task<SignalSessionRecord?> GetAsync(string address, CancellationToken cancellationToken)
    {
        FaultingStoreFactory.ThrowIfFaulted(_factory, FaultMode.FailReads, nameof(GetAsync));
        return _inner.GetAsync(address, cancellationToken);
    }

    /// <inheritdoc />
    public Task PutAsync(string address, SignalSessionRecord session, CancellationToken cancellationToken)
    {
        FaultingStoreFactory.ThrowIfFaulted(_factory, FaultMode.FailWrites, nameof(PutAsync));
        return _inner.PutAsync(address, session, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(string address, CancellationToken cancellationToken)
    {
        FaultingStoreFactory.ThrowIfFaulted(_factory, FaultMode.FailWrites, nameof(RemoveAsync));
        return _inner.RemoveAsync(address, cancellationToken);
    }

    /// <summary>Disposes nothing of the wrapped store (decorator lifecycle rules).</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Account-scoped pass-through/faulting decorator around an <see cref="IIdentityStore"/>
/// (fault semantics as for <see cref="FaultingSignalSessionStore"/>).
/// </summary>
public sealed class FaultingIdentityStore : IIdentityStore
{
    private readonly FaultingStoreFactory _factory;
    private readonly IIdentityStore _inner;

    /// <summary>Wraps <paramref name="inner"/> with the fault control of <paramref name="factory"/>.</summary>
    public FaultingIdentityStore(FaultingStoreFactory factory, IIdentityStore inner)
    {
        _factory = factory;
        _inner = inner;
    }

    /// <inheritdoc />
    public Task<SignalIdentityRecord?> GetAsync(string address, CancellationToken cancellationToken)
    {
        FaultingStoreFactory.ThrowIfFaulted(_factory, FaultMode.FailReads, nameof(GetAsync));
        return _inner.GetAsync(address, cancellationToken);
    }

    /// <inheritdoc />
    public Task PutAsync(string address, SignalIdentityRecord identity, CancellationToken cancellationToken)
    {
        FaultingStoreFactory.ThrowIfFaulted(_factory, FaultMode.FailWrites, nameof(PutAsync));
        return _inner.PutAsync(address, identity, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(string address, CancellationToken cancellationToken)
    {
        FaultingStoreFactory.ThrowIfFaulted(_factory, FaultMode.FailWrites, nameof(RemoveAsync));
        return _inner.RemoveAsync(address, cancellationToken);
    }

    /// <summary>Disposes nothing of the wrapped store (decorator lifecycle rules).</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Account-scoped pass-through/faulting decorator around an <see cref="IPreKeyStore"/>
/// (fault semantics as for <see cref="FaultingSignalSessionStore"/>, covering both
/// sub-stores: bundles by address and one-time pre-keys by id).
/// </summary>
public sealed class FaultingPreKeyStore : IPreKeyStore
{
    private readonly FaultingStoreFactory _factory;
    private readonly IPreKeyStore _inner;

    /// <summary>Wraps <paramref name="inner"/> with the fault control of <paramref name="factory"/>.</summary>
    public FaultingPreKeyStore(FaultingStoreFactory factory, IPreKeyStore inner)
    {
        _factory = factory;
        _inner = inner;
    }

    /// <inheritdoc />
    public Task<PreKeyBundle?> GetBundleAsync(string address, CancellationToken cancellationToken)
    {
        FaultingStoreFactory.ThrowIfFaulted(_factory, FaultMode.FailReads, nameof(GetBundleAsync));
        return _inner.GetBundleAsync(address, cancellationToken);
    }

    /// <inheritdoc />
    public Task PutBundleAsync(string address, PreKeyBundle bundle, CancellationToken cancellationToken)
    {
        FaultingStoreFactory.ThrowIfFaulted(_factory, FaultMode.FailWrites, nameof(PutBundleAsync));
        return _inner.PutBundleAsync(address, bundle, cancellationToken);
    }

    /// <inheritdoc />
    public Task<PreKeyRecordEntry?> GetPreKeyAsync(int preKeyId, CancellationToken cancellationToken)
    {
        FaultingStoreFactory.ThrowIfFaulted(_factory, FaultMode.FailReads, nameof(GetPreKeyAsync));
        return _inner.GetPreKeyAsync(preKeyId, cancellationToken);
    }

    /// <inheritdoc />
    public Task PutPreKeyAsync(int preKeyId, PreKeyRecordEntry key, CancellationToken cancellationToken)
    {
        FaultingStoreFactory.ThrowIfFaulted(_factory, FaultMode.FailWrites, nameof(PutPreKeyAsync));
        return _inner.PutPreKeyAsync(preKeyId, key, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> RemovePreKeyAsync(int preKeyId, CancellationToken cancellationToken)
    {
        FaultingStoreFactory.ThrowIfFaulted(_factory, FaultMode.FailWrites, nameof(RemovePreKeyAsync));
        return _inner.RemovePreKeyAsync(preKeyId, cancellationToken);
    }

    /// <summary>Disposes nothing of the wrapped store (decorator lifecycle rules).</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Account-scoped pass-through/faulting decorator around an
/// <see cref="IRecentMessageCacheStore"/> (fault semantics as for
/// <see cref="FaultingSignalSessionStore"/>, covering reads, writes, and the trim/purge
/// primitives). Note D-20: the SDK's log-and-continue behavior for cache write failures is
/// SDK-side policy, not part of the store primitive — the primitive itself still throws.
/// </summary>
public sealed class FaultingRecentMessageCacheStore : IRecentMessageCacheStore
{
    private readonly FaultingStoreFactory _factory;
    private readonly IRecentMessageCacheStore _inner;

    /// <summary>Wraps <paramref name="inner"/> with the fault control of <paramref name="factory"/>.</summary>
    public FaultingRecentMessageCacheStore(FaultingStoreFactory factory, IRecentMessageCacheStore inner)
    {
        _factory = factory;
        _inner = inner;
    }

    /// <inheritdoc />
    public Task<RecentMessageEntry?> GetAsync(string messageKey, CancellationToken cancellationToken)
    {
        FaultingStoreFactory.ThrowIfFaulted(_factory, FaultMode.FailReads, nameof(GetAsync));
        return _inner.GetAsync(messageKey, cancellationToken);
    }

    /// <inheritdoc />
    public Task PutAsync(RecentMessageEntry entry, CancellationToken cancellationToken)
    {
        FaultingStoreFactory.ThrowIfFaulted(_factory, FaultMode.FailWrites, nameof(PutAsync));
        return _inner.PutAsync(entry, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(string messageKey, CancellationToken cancellationToken)
    {
        FaultingStoreFactory.ThrowIfFaulted(_factory, FaultMode.FailWrites, nameof(RemoveAsync));
        return _inner.RemoveAsync(messageKey, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RecentMessageEntry>> GetRecentAsync(string chatJid, int limit, CancellationToken cancellationToken)
    {
        FaultingStoreFactory.ThrowIfFaulted(_factory, FaultMode.FailReads, nameof(GetRecentAsync));
        return _inner.GetRecentAsync(chatJid, limit, cancellationToken);
    }

    /// <inheritdoc />
    public Task TrimOldestAsync(int count, CancellationToken cancellationToken)
    {
        FaultingStoreFactory.ThrowIfFaulted(_factory, FaultMode.FailWrites, nameof(TrimOldestAsync));
        return _inner.TrimOldestAsync(count, cancellationToken);
    }

    /// <inheritdoc />
    public Task PurgeExpiredAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        FaultingStoreFactory.ThrowIfFaulted(_factory, FaultMode.FailWrites, nameof(PurgeExpiredAsync));
        return _inner.PurgeExpiredAsync(cutoffUtc, cancellationToken);
    }

    /// <summary>Disposes nothing of the wrapped store (decorator lifecycle rules).</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Account-scoped pass-through/faulting decorator around an <see cref="ISafetyStateStore"/>
/// (fault semantics as for <see cref="FaultingSignalSessionStore"/>, single-row shape).
/// </summary>
public sealed class FaultingSafetyStateStore : ISafetyStateStore
{
    private readonly FaultingStoreFactory _factory;
    private readonly ISafetyStateStore _inner;

    /// <summary>Wraps <paramref name="inner"/> with the fault control of <paramref name="factory"/>.</summary>
    public FaultingSafetyStateStore(FaultingStoreFactory factory, ISafetyStateStore inner)
    {
        _factory = factory;
        _inner = inner;
    }

    /// <inheritdoc />
    public Task<SafetyStateRecord?> GetAsync(CancellationToken cancellationToken)
    {
        FaultingStoreFactory.ThrowIfFaulted(_factory, FaultMode.FailReads, nameof(GetAsync));
        return _inner.GetAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task PutAsync(SafetyStateRecord state, CancellationToken cancellationToken)
    {
        FaultingStoreFactory.ThrowIfFaulted(_factory, FaultMode.FailWrites, nameof(PutAsync));
        return _inner.PutAsync(state, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(CancellationToken cancellationToken)
    {
        FaultingStoreFactory.ThrowIfFaulted(_factory, FaultMode.FailWrites, nameof(RemoveAsync));
        return _inner.RemoveAsync(cancellationToken);
    }

    /// <summary>Disposes nothing of the wrapped store (decorator lifecycle rules).</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
