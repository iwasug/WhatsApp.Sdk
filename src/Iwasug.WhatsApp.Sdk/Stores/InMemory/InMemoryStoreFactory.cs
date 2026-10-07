using System.Collections.Concurrent;
using Iwasug.WhatsApp.Sdk.Accounts;

namespace Iwasug.WhatsApp.Sdk.Stores.InMemory;

/// <summary>
/// In-memory reference implementation of <see cref="ISignalSessionStoreFactory"/> (STR-02).
/// </summary>
/// <remarks>
/// <para>
/// All state lives under this factory instance in a dictionary keyed by
/// (capability, account) — no global mutable statics (Pitfall 8 / T-01-02). Two accounts
/// never observe each other's data: per-account isolation is structural, and the
/// contract suite proves it (<c>Two_accounts_isolated_same_address</c>).
/// </para>
/// <para>
/// HONEST POSITIONING (D-09): this is a prototype-capable reference store. It does NOT
/// survive process restart and is not a production persistence guarantee — register your
/// own store implementation behind the same interfaces for production data.
/// </para>
/// <para>
/// Failure ≠ missing (D-19): a missing key returns <c>null</c>; this implementation has no
/// I/O and therefore no failure mode of its own — injected failures are exercised through
/// the test kit's fault-injection decorator (arrives with plan 01-03).
/// </para>
/// </remarks>
public sealed class InMemoryStoreFactory : ISignalSessionStoreFactory
{
    private const string SignalSessionCapability = "signal-session";

    private readonly ConcurrentDictionary<(string Capability, AccountId Account), ConcurrentDictionary<string, SignalSessionRecord>> _state = new();

    /// <inheritdoc />
    public ValueTask<ISignalSessionStore> CreateAsync(AccountId accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // GetOrAdd: every view created for the same (capability, account) observes the
        // same underlying records, so a later CreateAsync still sees earlier writes.
        var records = _state.GetOrAdd(
            (SignalSessionCapability, accountId),
            static _ => new ConcurrentDictionary<string, SignalSessionRecord>());

        return ValueTask.FromResult<ISignalSessionStore>(new InMemorySignalSessionStore(records));
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
            return Task.FromResult(records.TryGetValue(address, out var record) ? record : null);
        }

        public Task PutAsync(string address, SignalSessionRecord session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The value is observable to any subsequent read before this task completes
            // (D-06 store-side semantics; in-memory writes are synchronous).
            records[address] = session;
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
}
