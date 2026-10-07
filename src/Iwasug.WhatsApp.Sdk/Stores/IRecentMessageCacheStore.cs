using Iwasug.WhatsApp.Sdk.Accounts;

namespace Iwasug.WhatsApp.Sdk.Stores;

/// <summary>
/// Account-scoped store of recently received messages — one of the five granular
/// persistence seams (D-01), and the only LOSSY store of the five (D-05: reply/quote
/// degrades gracefully when the cache is empty).
/// </summary>
/// <remarks>
/// <para>
/// Structural per-account namespacing (D-02): the instance IS the namespace — no store
/// method takes an <c>accountId</c>. Obtain instances from
/// <see cref="IRecentMessageCacheStoreFactory"/>; instances are long-lived per account
/// (D-24) and owned by the SDK (D-23).
/// </para>
/// <para>
/// PRIMITIVES ONLY — retention policy is SDK-owned (D-08, Pitfall 5): this interface
/// exposes trim/purge primitives whose callers decide what and when to trim; the bounds
/// themselves (how many entries to keep, how long to keep them) live in SDK-owned
/// <c>IOptions&lt;T&gt;</c> policy and are NEVER parameters here. A method taking a
/// retention setting would push host-implementers into owning policy — that is the
/// violation this design forbids (T-01-07).
/// </para>
/// <para>
/// Durability (D-06) applies in its weak form: a write completes only when an
/// independent subsequent read observes the value, but the cache is lossy by definition
/// (D-05) and write failures log-and-continue rather than propagate (D-20). Failure ≠
/// missing (D-19) still holds for reads: missing keys return <c>null</c>, failures throw.
/// </para>
/// </remarks>
public interface IRecentMessageCacheStore : IAsyncDisposable
{
    /// <summary>
    /// Returns the cached message with <paramref name="messageKey"/>, or <c>null</c> when
    /// missing (missing ≠ failure, D-19).
    /// </summary>
    /// <param name="messageKey">Message key the entry is stored under.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cached entry, or <c>null</c> when missing.</returns>
    /// <exception cref="Errors.SdkStoreException">The read failed (D-19) — never returned as <c>null</c>.</exception>
    Task<RecentMessageEntry?> GetAsync(string messageKey, CancellationToken cancellationToken);

    /// <summary>
    /// Stores <paramref name="entry"/> under its <see cref="RecentMessageEntry.MessageKey"/>
    /// (last-write-wins). Completes only once an independent subsequent read observes the
    /// value (D-06 store-side semantics).
    /// </summary>
    /// <param name="entry">Typed domain record to cache (D-04).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Errors.SdkStoreException">The write failed.</exception>
    Task PutAsync(RecentMessageEntry entry, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the cached message with <paramref name="messageKey"/>.
    /// </summary>
    /// <param name="messageKey">Message key to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when an entry was removed; <c>false</c> when none existed (removing an absent key is idempotent).</returns>
    /// <exception cref="Errors.SdkStoreException">The remove failed (D-19: failure ≠ missing).</exception>
    Task<bool> RemoveAsync(string messageKey, CancellationToken cancellationToken);

    /// <summary>
    /// Returns up to <paramref name="limit"/> cached entries for
    /// <paramref name="chatJid"/>, newest first (minimal read surface for now — Phase 4
    /// quoting builds on it; RESEARCH Open Question 2).
    /// </summary>
    /// <param name="chatJid">Chat address (JID string) to read entries for.</param>
    /// <param name="limit">Maximum number of entries to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Newest-first entries for that chat only; empty when none exist.</returns>
    /// <exception cref="Errors.SdkStoreException">The read failed (D-19).</exception>
    Task<IReadOnlyList<RecentMessageEntry>> GetRecentAsync(string chatJid, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the <paramref name="count"/> oldest cached entries account-wide — a trim
    /// PRIMITIVE. The caller (SDK-owned policy, D-08) decides the count; applying this
    /// primitive twice removes only what actually remains (no negative over-trim).
    /// </summary>
    /// <param name="count">Number of oldest entries to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Errors.SdkStoreException">The trim failed.</exception>
    Task TrimOldestAsync(int count, CancellationToken cancellationToken);

    /// <summary>
    /// Removes every cached entry whose timestamp is strictly before
    /// <paramref name="cutoffUtc"/> — a purge PRIMITIVE. The caller (SDK-owned policy,
    /// D-08) decides the cutoff; entries at or after the cutoff are untouched.
    /// </summary>
    /// <param name="cutoffUtc">UTC timestamp; entries strictly older than this are removed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Errors.SdkStoreException">The purge failed.</exception>
    Task PurgeExpiredAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken);
}

/// <summary>
/// Async factory producing account-scoped <see cref="IRecentMessageCacheStore"/>
/// instances — the per-account namespacing mechanism of D-02/D-22.
/// </summary>
/// <remarks>
/// Async from day one (D-22, Pitfall 1); called once per account (D-24), so
/// <c>ValueTask&lt;T&gt;</c> is safe here while store methods use <c>Task</c> (TAP guidance).
/// </remarks>
public interface IRecentMessageCacheStoreFactory
{
    /// <summary>
    /// Creates the store instance for exactly one account. The returned instance is
    /// long-lived (D-24): created once at account add, reused until remove/shutdown.
    /// </summary>
    /// <param name="accountId">Opaque account key (D-25).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The account-scoped store instance.</returns>
    ValueTask<IRecentMessageCacheStore> CreateAsync(AccountId accountId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Typed domain record for a cached recent message (D-04: typed domain objects, not
/// byte blobs). No wire vocabulary here (Pitfall 6 / D-15) — the message body itself is
/// deferred to the phase that defines wire types.
/// </summary>
/// <param name="MessageKey">Unique key the entry is stored and removed by.</param>
/// <param name="ChatJid">Chat address (JID string) the message belongs to.</param>
/// <param name="TimestampUtc">When the message arrived (UTC) — the basis for purge-primitive cutoffs.</param>
public sealed record RecentMessageEntry(string MessageKey, string ChatJid, DateTimeOffset TimestampUtc);
