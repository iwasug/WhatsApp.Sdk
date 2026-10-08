using System.Collections.Concurrent;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;

namespace Iwasug.WhatsApp.Sdk.Testing.Rules;

/// <summary>
/// Reusable store-contract rule helpers (D-11's full semantic contract): persistence
/// ordering (durability-await, D-06 store-side), two-account isolation (D-02), and
/// parallel cross-read enforcement (D-11 concurrency family, T-01-10). Shared by every
/// contract class in the kit and directly callable by host suites verifying their own
/// store implementations.
/// </summary>
/// <remarks>
/// All helpers are generic over the account-scoped store type: callers supply thin
/// delegates that bind the concrete interface methods (Put/Get shape) of their store
/// family, and the helper owns the rule mechanics — instance creation, ordering, and the
/// assertion. Stores are account-scoped views (D-02), so the helpers create instances
/// through the factory the caller binds and dispose only the instances they created.
/// </remarks>
public static class StoreContractRules
{
    /// <summary>
    /// Persistence-ordering proof for the D-06 store-side durability contract: a write
    /// completes only when an INDEPENDENT second store instance — created from the same
    /// factory for the same account AFTER the write returned — observes the value.
    /// </summary>
    /// <remarks>
    /// Stores are account-scoped views (D-02), so second-instance visibility is the
    /// strongest in-process persistence-ordering proof available store-side (Pitfall 7):
    /// by the time <c>Put</c> returned, the value must already be visible to a fresh view
    /// of the same account. <paramref name="createStore"/> is invoked twice — first for
    /// the writing instance, then for the independent reading instance.
    /// </remarks>
    /// <typeparam name="TStore">The account-scoped store interface under test.</typeparam>
    /// <param name="createStore">Creates a store instance for the account under test (called twice).</param>
    /// <param name="writeAsync">Performs the durable write on the first instance.</param>
    /// <param name="assertVisibleAsync">Reads and asserts the value is visible through the second, independent instance.</param>
    public static async Task AssertWriteVisibleToIndependentReadAsync<TStore>(
        Func<ValueTask<TStore>> createStore,
        Func<TStore, Task> writeAsync,
        Func<TStore, Task> assertVisibleAsync)
        where TStore : IAsyncDisposable
    {
        await using var writer = await createStore();
        await writeAsync(writer);

        // The write returned (D-06 awaited). Completion is only real if an independent
        // subsequent read — a SECOND store instance for the same account — observes it.
        await using var reader = await createStore();
        await assertVisibleAsync(reader);
    }

    /// <summary>
    /// Two accounts created from ONE factory, the same key written on both with
    /// slot-distinct values (slot 0 = account A, slot 1 = account B), then each account's
    /// reads must see only its own write (D-02 structural namespacing).
    /// </summary>
    /// <typeparam name="TStore">The account-scoped store interface under test.</typeparam>
    /// <param name="createStoreAsync">Creates the store instance for an account id.</param>
    /// <param name="accountA">First account (slot 0).</param>
    /// <param name="accountB">Second account (slot 1).</param>
    /// <param name="writeSlotValueAsync">Writes the slot-distinct value for slot 0 or 1 under the shared key.</param>
    /// <param name="assertSeesOnlySlotValueAsync">Reads the shared key and asserts ONLY that slot's value is visible.</param>
    public static async Task AssertTwoAccountsIsolatedAsync<TStore>(
        Func<AccountId, ValueTask<TStore>> createStoreAsync,
        AccountId accountA,
        AccountId accountB,
        Func<TStore, int, Task> writeSlotValueAsync,
        Func<TStore, int, Task> assertSeesOnlySlotValueAsync)
        where TStore : IAsyncDisposable
    {
        await using var storeA = await createStoreAsync(accountA);
        await using var storeB = await createStoreAsync(accountB);

        // Same key on both accounts, values distinguishable per "slot" (0 = A, 1 = B).
        await writeSlotValueAsync(storeA, 0);
        await writeSlotValueAsync(storeB, 1);

        await assertSeesOnlySlotValueAsync(storeA, 0);
        await assertSeesOnlySlotValueAsync(storeB, 1);
    }

    /// <summary>
    /// Real-parallelism cross-read enforcement (D-11 concurrency family, Pitfall 4, T-01-10):
    /// writes on account A and reads on account B interleave concurrently for
    /// <paramref name="iterations"/> iterations via <see cref="Task.WhenAll"/> (no test
    /// collection disables parallelization), and any B read that observes A's value is a
    /// cross-account leak that fails the assertion listing the leaking iterations.
    /// </summary>
    /// <remarks>
    /// KEYED shape: <paramref name="writeOnAAsync"/> writes A's value (typically keyed by
    /// the iteration index); <paramref name="readLeakOnBAsync"/> reads the same key on B
    /// and returns <c>true</c> when the read observed A's value. B is never written by the
    /// caller, so any observation of A's data on B is a namespace leak.
    /// </remarks>
    /// <typeparam name="TStore">The account-scoped store interface under test.</typeparam>
    /// <param name="createStoreAsync">Creates the store instance for an account id.</param>
    /// <param name="accountA">The account all writes target.</param>
    /// <param name="accountB">The account all reads target.</param>
    /// <param name="writeOnAAsync">Writes A's value for iteration <c>i</c> (keyed stores key it by <c>i</c>).</param>
    /// <param name="readLeakOnBAsync">Returns <c>true</c> when B's read for iteration <c>i</c> observed A's value.</param>
    /// <param name="iterations">Interleaving depth; callers pass 100 for the standard depth.</param>
    public static async Task AssertParallelAccessNoCrossReadsAsync<TStore>(
        Func<AccountId, ValueTask<TStore>> createStoreAsync,
        AccountId accountA,
        AccountId accountB,
        Func<TStore, int, Task> writeOnAAsync,
        Func<TStore, int, Task<bool>> readLeakOnBAsync,
        int iterations)
        where TStore : IAsyncDisposable
    {
        await using var storeA = await createStoreAsync(accountA);
        await using var storeB = await createStoreAsync(accountB);

        var leaks = new ConcurrentBag<int>();

        // Task.Run forces genuine concurrency: without it, synchronous-completing
        // in-memory delegates would run the writer loop to completion before the reader
        // started, silently degrading the fact to serial coverage (Pitfall 4).
        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < iterations; i++)
            {
                await writeOnAAsync(storeA, i);
            }
        });

        var reader = Task.Run(async () =>
        {
            for (var i = 0; i < iterations; i++)
            {
                if (await readLeakOnBAsync(storeB, i))
                {
                    leaks.Add(i);
                }
            }
        });

        await Task.WhenAll(writer, reader);

        leaks.Should().BeEmpty(
            "parallel interleaved access on two accounts of the same store type must produce zero cross-reads (D-11/T-01-10); leaked iterations: {0}",
            string.Join(", ", leaks.OrderBy(i => i)));
    }

    /// <summary>
    /// Real-parallelism cross-read enforcement — KEYLESS single-row shape for stores
    /// without key parameters (safety state): A's row is written on one account while B's
    /// instance reads concurrently; B must never observe A's row.
    /// </summary>
    /// <remarks>
    /// <paramref name="writeOnAAsync"/> still receives the iteration index so A's payload
    /// can vary per iteration; only the READ side is keyless. Semantics otherwise identical
    /// to the keyed overload.
    /// </remarks>
    /// <typeparam name="TStore">The account-scoped store interface under test.</typeparam>
    /// <param name="createStoreAsync">Creates the store instance for an account id.</param>
    /// <param name="accountA">The account all writes target.</param>
    /// <param name="accountB">The account all reads target.</param>
    /// <param name="writeOnAAsync">Writes A's row for iteration <c>i</c>.</param>
    /// <param name="readLeakOnBAsync">Returns <c>true</c> when B's read observed A's row.</param>
    /// <param name="iterations">Interleaving depth; callers pass 100 for the standard depth.</param>
    public static async Task AssertParallelAccessNoCrossReadsAsync<TStore>(
        Func<AccountId, ValueTask<TStore>> createStoreAsync,
        AccountId accountA,
        AccountId accountB,
        Func<TStore, int, Task> writeOnAAsync,
        Func<TStore, Task<bool>> readLeakOnBAsync,
        int iterations)
        where TStore : IAsyncDisposable
    {
        await using var storeA = await createStoreAsync(accountA);
        await using var storeB = await createStoreAsync(accountB);

        var leaks = new ConcurrentBag<int>();

        // Task.Run forces genuine concurrency (see the keyed overload's remark).
        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < iterations; i++)
            {
                await writeOnAAsync(storeA, i);
            }
        });

        var reader = Task.Run(async () =>
        {
            for (var i = 0; i < iterations; i++)
            {
                if (await readLeakOnBAsync(storeB))
                {
                    leaks.Add(i);
                }
            }
        });

        await Task.WhenAll(writer, reader);

        leaks.Should().BeEmpty(
            "parallel interleaved access on two accounts of the same store type must produce zero cross-reads (D-11/T-01-10); leaked iterations: {0}",
            string.Join(", ", leaks.OrderBy(i => i)));
    }
}
