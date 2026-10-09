using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;

namespace Iwasug.WhatsApp.Sdk.Stores;

/// <summary>
/// Account-scoped store of the pairing credential row — one of the six granular
/// persistence seams (D-01).
/// </summary>
/// <remarks>
/// <para>
/// Single-row semantics (planner decision): credentials are one mutable row per account,
/// so this store has no key parameters — <c>PutAsync</c> overwrites the row
/// (last-write-wins) and <c>RemoveAsync</c> clears it. Structural per-account
/// namespacing (D-02): the instance IS the namespace. Obtain instances from
/// <see cref="ICredentialStoreFactory"/>; instances are long-lived per account (D-24)
/// and owned by the SDK (D-23).
/// </para>
/// <para>
/// Durability (D-06): the write completes only when an independent subsequent read
/// observes the value; the SDK awaits it before continuing. Failure ≠ missing (D-19):
/// a missing row returns <c>null</c>; failures throw. Durability class (D-05):
/// must-not-lose. Removing an absent row is idempotent and returns <c>false</c>.
/// </para>
/// </remarks>
public interface ICredentialStore : IAsyncDisposable
{
    /// <summary>
    /// Returns the account's credential row, or <c>null</c> when none has been stored
    /// (missing ≠ failure, D-19).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored row, or <c>null</c> when missing.</returns>
    /// <exception cref="Errors.SdkStoreException">The read failed (D-19) — never returned as <c>null</c>.</exception>
    Task<CredentialRecord?> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stores <paramref name="credentials"/> as the account's credential row, overwriting any
    /// existing row (last-write-wins). Completes only once an independent subsequent read
    /// observes the value (D-06 store-side durability contract).
    /// </summary>
    /// <param name="credentials">Typed domain record to persist (D-04).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Errors.SdkStoreException">The write failed (D-18/D-21).</exception>
    Task PutAsync(CredentialRecord credentials, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the account's credential row.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when a row was removed; <c>false</c> when none existed (removing an absent row is idempotent).</returns>
    /// <exception cref="Errors.SdkStoreException">The remove failed (D-19: failure ≠ missing).</exception>
    Task<bool> RemoveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Async factory producing account-scoped <see cref="ICredentialStore"/> instances —
/// the per-account namespacing mechanism of D-02/D-22.
/// </summary>
/// <remarks>
/// Async from day one (D-22, Pitfall 1); called once per account (D-24), so
/// <c>ValueTask&lt;T&gt;</c> is safe here while store methods use <c>Task</c> (TAP guidance).
/// </remarks>
public interface ICredentialStoreFactory
{
    /// <summary>
    /// Creates the store instance for exactly one account. The returned instance is
    /// long-lived (D-24): created once at account add, reused until remove/shutdown.
    /// </summary>
    /// <param name="accountId">Opaque account key (D-25).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The account-scoped store instance.</returns>
    ValueTask<ICredentialStore> CreateAsync(AccountId accountId, CancellationToken cancellationToken = default);
}
