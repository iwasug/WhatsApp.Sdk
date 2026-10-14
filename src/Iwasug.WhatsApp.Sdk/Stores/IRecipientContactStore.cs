using Iwasug.WhatsApp.Sdk.Accounts;

namespace Iwasug.WhatsApp.Sdk.Stores;

/// <summary>
/// Account-scoped recipient-contact ledger — whether a recipient has ever been contacted
/// by this account — one of the granular persistence seams (additive, seven capabilities,
/// SAFE-by-default; see <see cref="StoreRegistryOptions.RecipientContactFactory"/>).
/// </summary>
/// <remarks>
/// <para>
/// Last-write-wins / small-row semantics: keyed by a deterministic, privacy-neutral
/// recipient key (<c>"{jid.User}@{jid.Server}"</c> — no display name, no message body).
/// </para>
/// <para>
/// Durability (D-06): <see cref="MarkContactedAsync"/> completes only once the value is
/// observable; missing is not a failure (D-19).
/// </para>
/// </remarks>
public interface IRecipientContactStore : IAsyncDisposable
{
    /// <summary>
    /// Returns whether the recipient identified by <paramref name="recipientKey"/> has been
    /// contacted at least once. <c>false</c> when the recipient is unknown (missing ≠
    /// failure).
    /// </summary>
    Task<bool> HasContactedAsync(string recipientKey, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the recipient identified by <paramref name="recipientKey"/> as contacted.
    /// Idempotent: marking an already-contacted recipient is a no-op success.
    /// </summary>
    Task MarkContactedAsync(string recipientKey, CancellationToken cancellationToken);
}

/// <summary>
/// Async factory producing account-scoped <see cref="IRecipientContactStore"/> instances
/// (D-02/D-22).
/// </summary>
public interface IRecipientContactStoreFactory
{
    /// <summary>Creates the store instance for exactly one account.</summary>
    ValueTask<IRecipientContactStore> CreateAsync(AccountId accountId, CancellationToken cancellationToken = default);
}