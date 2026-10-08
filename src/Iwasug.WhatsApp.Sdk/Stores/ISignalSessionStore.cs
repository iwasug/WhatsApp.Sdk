using Iwasug.WhatsApp.Sdk.Accounts;

namespace Iwasug.WhatsApp.Sdk.Stores;

/// <summary>
/// Account-scoped store of Signal protocol sessions — one of the five granular persistence
/// seams (D-01: separate interfaces per concern, no aggregate facade).
/// </summary>
/// <remarks>
/// <para>
/// Structural per-account namespacing (D-02): the instance IS the namespace — no store
/// method takes an <c>accountId</c>. Obtain instances from
/// <see cref="ISignalSessionStoreFactory"/>; instances are long-lived per account (D-24)
/// and owned by the SDK, which disposes them on account removal and shutdown (D-23).
/// </para>
/// <para>
/// Single-key CRUD only (D-07): no transaction API — the SDK sequences multi-step state
/// changes itself, which keeps any backend implementable.
/// </para>
/// <para>
/// Durability (D-06): write methods complete only when an independent subsequent read
/// observes the value; the SDK awaits every durable write before continuing protocol work
/// (no fire-and-forget). Failure ≠ missing (D-19): reads return <c>null</c> for a missing
/// key and THROW on a read failure — the two are never conflated.
/// </para>
/// </remarks>
public interface ISignalSessionStore : IAsyncDisposable
{
    /// <summary>
    /// Returns the session stored for <paramref name="address"/>, or <c>null</c> when no
    /// session exists for it (missing ≠ failure, D-19).
    /// </summary>
    /// <param name="address">Peer address (JID string) the session is keyed by.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored session, or <c>null</c> when missing.</returns>
    /// <exception cref="Errors.SdkStoreException">The read failed (D-19) — never returned as <c>null</c>.</exception>
    Task<SignalSessionRecord?> GetAsync(string address, CancellationToken cancellationToken);

    /// <summary>
    /// Stores <paramref name="session"/> under <paramref name="address"/> (overwriting any
    /// existing value). Completes only once an independent subsequent read observes the
    /// value (D-06 store-side durability contract).
    /// </summary>
    /// <param name="address">Peer address the session is keyed by.</param>
    /// <param name="session">Typed domain record to persist (D-04).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Errors.SdkStoreException">The write failed (D-18/D-21).</exception>
    Task PutAsync(string address, SignalSessionRecord session, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the session for <paramref name="address"/>.
    /// </summary>
    /// <param name="address">Peer address to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when a value was removed; <c>false</c> when none existed.</returns>
    /// <exception cref="Errors.SdkStoreException">The remove failed (D-19: failure ≠ missing).</exception>
    Task<bool> RemoveAsync(string address, CancellationToken cancellationToken);
}

/// <summary>
/// Async factory producing account-scoped <see cref="ISignalSessionStore"/> instances —
/// the per-account namespacing mechanism of D-02/D-22.
/// </summary>
/// <remarks>
/// Async from day one (D-22, Pitfall 1) even though the reference implementation is
/// synchronous internally: host implementations may need to open connections or pools.
/// Called once per account (D-24), so <c>ValueTask&lt;T&gt;</c> is safe here — but store
/// methods use <c>Task</c> (TAP guidance: host-implemented interfaces must not impose
/// single-consumption constraints).
/// </remarks>
public interface ISignalSessionStoreFactory
{
    /// <summary>
    /// Creates the store instance for exactly one account. The returned instance is
    /// long-lived (D-24): created once at account add, reused until remove/shutdown.
    /// </summary>
    /// <param name="accountId">Opaque account key (D-25).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The account-scoped store instance.</returns>
    ValueTask<ISignalSessionStore> CreateAsync(AccountId accountId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Typed domain record for a stored Signal session (D-04: typed domain objects, not
/// byte-blob APIs at the interface level).
/// </summary>
/// <param name="Address">Peer address the session belongs to.</param>
/// <param name="SessionState">
/// SDK-owned serialized ratchet state. Its encoding is an internal implementation detail
/// and is NOT a public contract — hosts persist these bytes opaquely and must not parse
/// them; a format change between SDK versions requires only that the bytes round-trip.
/// </param>
/// <remarks>
/// Equality is the record default: <see cref="string"/> compares by value but
/// <paramref name="SessionState"/> compares by REFERENCE (arrays do not implement
/// value equality). Two records holding byte-identical buffers are NOT
/// <c>==</c>-equal unless they share the array instance — compare buffer contents
/// (for example with <c>SequenceEqual</c>) when value equality is what you mean.
/// </remarks>
public sealed record SignalSessionRecord(string Address, byte[] SessionState);
