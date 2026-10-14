using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;

namespace Iwasug.WhatsApp.Sdk.Stores;

/// <summary>
/// Account-scoped store of peer Signal identities — one of the seven granular persistence
/// seams (D-01: separate interfaces per concern, no aggregate facade).
/// </summary>
/// <remarks>
/// <para>
/// Structural per-account namespacing (D-02): the instance IS the namespace — no store
/// method takes an <c>accountId</c>. Obtain instances from
/// <see cref="IIdentityStoreFactory"/>; instances are long-lived per account (D-24) and
/// owned by the SDK, which disposes them on account removal and shutdown (D-23).
/// </para>
/// <para>
/// Single-key CRUD only (D-07): no transaction API — the SDK sequences multi-step state
/// changes itself, which keeps any backend implementable.
/// </para>
/// <para>
/// Durability (D-06): write methods complete only when an independent subsequent read
/// observes the value; the SDK awaits every durable write before continuing protocol work
/// (no fire-and-forget). Failure ≠ missing (D-19): reads return <c>null</c> for a missing
/// key and THROW on a read failure — the two are never conflated. Durability class
/// (D-05): must-not-lose.
/// </para>
/// </remarks>
public interface IIdentityStore : IAsyncDisposable
{
    /// <summary>
    /// Returns the identity stored for <paramref name="address"/>, or <c>null</c> when
    /// none exists for it (missing ≠ failure, D-19).
    /// </summary>
    /// <param name="address">Peer address (JID string) the identity is keyed by.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored identity, or <c>null</c> when missing.</returns>
    /// <exception cref="Errors.SdkStoreException">The read failed (D-19) — never returned as <c>null</c>.</exception>
    Task<SignalIdentityRecord?> GetAsync(string address, CancellationToken cancellationToken);

    /// <summary>
    /// Stores <paramref name="identity"/> under <paramref name="address"/> (overwriting
    /// any existing value — last-write-wins). Completes only once an independent
    /// subsequent read observes the value (D-06 store-side durability contract).
    /// </summary>
    /// <param name="address">Peer address the identity is keyed by.</param>
    /// <param name="identity">Typed domain record to persist (D-04).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Errors.SdkStoreException">The write failed (D-18/D-21).</exception>
    Task PutAsync(string address, SignalIdentityRecord identity, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the identity for <paramref name="address"/>.
    /// </summary>
    /// <param name="address">Peer address to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when a value was removed; <c>false</c> when none existed (removing an absent key is idempotent).</returns>
    /// <exception cref="Errors.SdkStoreException">The remove failed (D-19: failure ≠ missing).</exception>
    Task<bool> RemoveAsync(string address, CancellationToken cancellationToken);
}

/// <summary>
/// Async factory producing account-scoped <see cref="IIdentityStore"/> instances — the
/// per-account namespacing mechanism of D-02/D-22.
/// </summary>
/// <remarks>
/// Async from day one (D-22, Pitfall 1) even though the reference implementation is
/// synchronous internally: host implementations may need to open connections or pools.
/// Called once per account (D-24), so <c>ValueTask&lt;T&gt;</c> is safe here — but store
/// methods use <c>Task</c> (TAP guidance: host-implemented interfaces must not impose
/// single-consumption constraints).
/// </remarks>
public interface IIdentityStoreFactory
{
    /// <summary>
    /// Creates the store instance for exactly one account. The returned instance is
    /// long-lived (D-24): created once at account add, reused until remove/shutdown.
    /// </summary>
    /// <param name="accountId">Opaque account key (D-25).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The account-scoped store instance.</returns>
    ValueTask<IIdentityStore> CreateAsync(AccountId accountId, CancellationToken cancellationToken = default);
}
