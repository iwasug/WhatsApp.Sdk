namespace Iwasug.WhatsApp.Sdk.Stores;

/// <summary>
/// Host registration surface for the five store factory seams (D-01/D-03). Every property is
/// nullable: <c>null</c> means the host did not register that capability, and the SDK's
/// in-memory reference store fills the gap (D-03 — all five stores are optional; a host may
/// register only a subset).
/// </summary>
/// <remarks>
/// <para>
/// This is a plain options object: <see cref="StoreRegistry"/> receives it directly.
/// <c>IOptions&lt;T&gt;</c> binding arrives with Phase 7's DI surface (MUL-03) — the
/// registration *shape* is fixed here so hosts write it once.
/// </para>
/// <para>
/// No aggregate facade over the five stores exists (D-01): registration is per-capability,
/// five separate factories. Each factory is async (D-22) and produces account-scoped store
/// instances (D-02); the resolved set for one account is <see cref="StoreSession"/>.
/// </para>
/// </remarks>
public sealed class StoreRegistryOptions
{
    /// <summary>
    /// Factory for <see cref="ISignalSessionStore"/> instances. <c>null</c> = the
    /// in-memory reference store fills this capability (D-03).
    /// </summary>
    public ISignalSessionStoreFactory? SignalSessionFactory { get; set; }

    /// <summary>
    /// Factory for <see cref="IIdentityStore"/> instances. <c>null</c> = the in-memory
    /// reference store fills this capability (D-03).
    /// </summary>
    public IIdentityStoreFactory? IdentityFactory { get; set; }

    /// <summary>
    /// Factory for <see cref="IPreKeyStore"/> instances. <c>null</c> = the in-memory
    /// reference store fills this capability (D-03).
    /// </summary>
    public IPreKeyStoreFactory? PreKeyFactory { get; set; }

    /// <summary>
    /// Factory for <see cref="IRecentMessageCacheStore"/> instances. <c>null</c> = the
    /// in-memory reference store fills this capability (D-03).
    /// </summary>
    public IRecentMessageCacheStoreFactory? RecentMessageCacheFactory { get; set; }

    /// <summary>
    /// Factory for <see cref="ISafetyStateStore"/> instances. <c>null</c> = the in-memory
    /// reference store fills this capability (D-03).
    /// </summary>
    public ISafetyStateStoreFactory? SafetyStateFactory { get; set; }
}
