using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;

namespace Iwasug.WhatsApp.Sdk.Stores;

/// <summary>
/// Account-scoped store of Signal pre-keys — one of the five granular persistence seams
/// (D-01), split into two sub-stores per account: bundles keyed by peer address, and
/// single one-time pre-keys keyed by their integer id.
/// </summary>
/// <remarks>
/// <para>
/// Structural per-account namespacing (D-02): the instance IS the namespace — no store
/// method takes an <c>accountId</c>. Obtain instances from <see cref="IPreKeyStoreFactory"/>;
/// instances are long-lived per account (D-24) and owned by the SDK (D-23).
/// </para>
/// <para>
/// Single-key CRUD only (D-07): consume-and-save sequencing (consume a one-time pre-key
/// during session establishment, then save the updated session) is the SDK's job — the
/// store offers only single-key operations, never a transaction API, so any backend can
/// implement it.
/// </para>
/// <para>
/// Durability (D-06): write methods complete only when an independent subsequent read
/// observes the value. Failure ≠ missing (D-19): missing keys return <c>null</c>/false;
/// failures throw. Durability class (D-05): must-not-lose. Removing an absent key is
/// idempotent and returns <c>false</c>.
/// </para>
/// </remarks>
public interface IPreKeyStore : IAsyncDisposable
{
    /// <summary>
    /// Returns the pre-key bundle stored for <paramref name="address"/>, or <c>null</c>
    /// when none exists (missing ≠ failure, D-19).
    /// </summary>
    /// <param name="address">Peer address (JID string) the bundle is keyed by.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored bundle, or <c>null</c> when missing.</returns>
    /// <exception cref="Errors.SdkStoreException">The read failed (D-19) — never returned as <c>null</c>.</exception>
    Task<PreKeyBundle?> GetBundleAsync(string address, CancellationToken cancellationToken);

    /// <summary>
    /// Stores <paramref name="bundle"/> under <paramref name="address"/> (last-write-wins).
    /// Completes only once an independent subsequent read observes the value (D-06).
    /// </summary>
    /// <param name="address">Peer address the bundle is keyed by.</param>
    /// <param name="bundle">Typed domain record to persist (D-04).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Errors.SdkStoreException">The write failed (D-18/D-21).</exception>
    Task PutBundleAsync(string address, PreKeyBundle bundle, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the one-time pre-key with <paramref name="preKeyId"/>, or <c>null</c> when
    /// it is missing or has already been consumed (missing ≠ failure, D-19).
    /// </summary>
    /// <param name="preKeyId">Identifier of the one-time pre-key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored pre-key, or <c>null</c> when missing.</returns>
    /// <exception cref="Errors.SdkStoreException">The read failed (D-19) — never returned as <c>null</c>.</exception>
    Task<PreKeyRecordEntry?> GetPreKeyAsync(int preKeyId, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the one-time pre-key <paramref name="key"/> under
    /// <paramref name="preKeyId"/> (last-write-wins). Completes only once an independent
    /// subsequent read observes the value (D-06).
    /// </summary>
    /// <param name="preKeyId">Identifier of the one-time pre-key.</param>
    /// <param name="key">Typed domain record to persist (D-04).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Errors.SdkStoreException">The write failed (D-18/D-21).</exception>
    Task PutPreKeyAsync(int preKeyId, PreKeyRecordEntry key, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the one-time pre-key with <paramref name="preKeyId"/> (single-key consume).
    /// </summary>
    /// <param name="preKeyId">Identifier of the one-time pre-key to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when a key was removed; <c>false</c> when none existed (removing an absent key is idempotent).</returns>
    /// <exception cref="Errors.SdkStoreException">The remove failed (D-19: failure ≠ missing).</exception>
    Task<bool> RemovePreKeyAsync(int preKeyId, CancellationToken cancellationToken);
}

/// <summary>
/// Async factory producing account-scoped <see cref="IPreKeyStore"/> instances — the
/// per-account namespacing mechanism of D-02/D-22.
/// </summary>
/// <remarks>
/// Async from day one (D-22, Pitfall 1); called once per account (D-24), so
/// <c>ValueTask&lt;T&gt;</c> is safe here while store methods use <c>Task</c> (TAP guidance).
/// </remarks>
public interface IPreKeyStoreFactory
{
    /// <summary>
    /// Creates the store instance for exactly one account. The returned instance is
    /// long-lived (D-24): created once at account add, reused until remove/shutdown.
    /// </summary>
    /// <param name="accountId">Opaque account key (D-25).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The account-scoped store instance.</returns>
    ValueTask<IPreKeyStore> CreateAsync(AccountId accountId, CancellationToken cancellationToken = default);
}
