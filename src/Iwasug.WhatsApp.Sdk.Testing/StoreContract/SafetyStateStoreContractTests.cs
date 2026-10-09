using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Testing.FaultInjection;
using Iwasug.WhatsApp.Sdk.Testing.Rules;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Testing.StoreContract;

/// <summary>
/// Store-contract suite for <see cref="ISafetyStateStore"/> — the same-suite-two-
/// implementations mechanism (D-10/D-11).
/// </summary>
/// <remarks>
/// Consumers subclass and bind their factory via <see cref="CreateFactory"/>. Safety
/// state is a single mutable row per account (planner decision), so this class asserts
/// single-row semantics — last-write-wins Put, idempotent Remove, missing-row-returns-
/// null — plus per-account isolation, instead of the keyed-entry facts the other stores
/// carry. Plan 01-03 adds the fault-injection family (<see cref="FaultingStoreFactory"/>),
/// durability-await (independent second-instance read, D-06), and parallel cross-read
/// enforcement via the keyless shape of the shared
/// <see cref="StoreContractRules"/> helper (A's row is
/// written while B's instance reads — B must never observe A's row) — parallel
/// enforcement is uniform across all five store families (serial-only coverage is not
/// acceptable per D-11).
/// </remarks>
public abstract class SafetyStateStoreContractTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Supplies the factory under test; the subclass binds the implementation.</summary>
    protected abstract ISafetyStateStoreFactory CreateFactory();

    [Fact]
    public async Task Put_then_Get_round_trips_typed_record()
    {
        ISafetyStateStoreFactory factory = CreateFactory();
        ISafetyStateStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var record = new SafetyStateRecord(CreatedAt, WarmupStage: 3);

        await store.PutAsync(record, CancellationToken.None);

        SafetyStateRecord? fetched = await store.GetAsync(CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.CreatedAt.Should().Be(CreatedAt);
        fetched.WarmupStage.Should().Be(3);
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Get_missing_row_returns_null_not_throw()
    {
        ISafetyStateStoreFactory factory = CreateFactory();
        ISafetyStateStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        SafetyStateRecord? fetched = await store.GetAsync(CancellationToken.None);

        fetched.Should().BeNull(); // missing ≠ failure (D-19)
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Two_accounts_see_independent_single_rows()
    {
        ISafetyStateStoreFactory factory = CreateFactory();
        ISafetyStateStore storeA = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        ISafetyStateStore storeB = await factory.CreateAsync(AccountId.Create("acct-B"), CancellationToken.None);

        await storeA.PutAsync(new SafetyStateRecord(CreatedAt, WarmupStage: 4), CancellationToken.None);

        // Structural namespacing (D-02 / T-01-05): A's row is invisible to B.
        SafetyStateRecord? rowA = await storeA.GetAsync(CancellationToken.None);
        SafetyStateRecord? rowB = await storeB.GetAsync(CancellationToken.None);
        rowA.Should().NotBeNull();
        rowB.Should().BeNull();

        await storeB.PutAsync(new SafetyStateRecord(CreatedAt.AddMinutes(1), WarmupStage: 1), CancellationToken.None);
        rowA = await storeA.GetAsync(CancellationToken.None);
        rowB = await storeB.GetAsync(CancellationToken.None);
        rowA!.WarmupStage.Should().Be(4);
        rowB!.WarmupStage.Should().Be(1);
        await storeA.DisposeAsync();
        await storeB.DisposeAsync();
    }

    [Fact]
    public async Task Double_put_overwrites_single_row_last_write_wins()
    {
        ISafetyStateStoreFactory factory = CreateFactory();
        ISafetyStateStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        await store.PutAsync(new SafetyStateRecord(CreatedAt, WarmupStage: 1), CancellationToken.None);
        await store.PutAsync(new SafetyStateRecord(CreatedAt, WarmupStage: 2), CancellationToken.None);

        SafetyStateRecord? fetched = await store.GetAsync(CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.WarmupStage.Should().Be(2);
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Double_remove_second_returns_false()
    {
        ISafetyStateStoreFactory factory = CreateFactory();
        ISafetyStateStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        await store.PutAsync(new SafetyStateRecord(CreatedAt, WarmupStage: 1), CancellationToken.None);

        bool first = await store.RemoveAsync(CancellationToken.None);
        bool second = await store.RemoveAsync(CancellationToken.None);

        first.Should().BeTrue();
        second.Should().BeFalse(); // idempotency edge: removing an absent row reports false
        await store.DisposeAsync();
    }

    [Fact]
    public async Task FailReads_Get_throws_SdkStoreException_never_returns_null()
    {
        ISafetyStateStoreFactory inner = CreateFactory();
        ISafetyStateStoreFactory faulting = new FaultingStoreFactory(safetyState: inner) { Mode = FaultMode.FailReads };
        ISafetyStateStore store = await faulting.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        // A failing read THROWS — it must never surface as null (D-19: a safety-state
        // blip misread as "no row" would restart warm-up and trip ban-safety pacing).
        Func<Task<SafetyStateRecord>> act = async () => await store.GetAsync(CancellationToken.None);
        await act.Should().ThrowAsync<SdkStoreException>();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Faulting_factory_Mode_None_absent_row_returns_null()
    {
        ISafetyStateStoreFactory inner = CreateFactory();
        ISafetyStateStoreFactory faulting = new FaultingStoreFactory(safetyState: inner) { Mode = FaultMode.None };
        ISafetyStateStore store = await faulting.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        // Distinct observable path from the fault above: a genuinely absent row returns
        // null — failure and missing stay mechanically distinguishable (D-19).
        (await store.GetAsync(CancellationToken.None)).Should().BeNull();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Write_observed_by_independent_second_store_instance()
    {
        ISafetyStateStoreFactory factory = CreateFactory();
        var account = AccountId.Create("acct-A");

        await StoreContractRules.AssertWriteVisibleToIndependentReadAsync<ISafetyStateStore>(
            createStore: () => factory.CreateAsync(account, CancellationToken.None),
            writeAsync: writer => writer.PutAsync(
                new SafetyStateRecord(CreatedAt, WarmupStage: 3), CancellationToken.None),
            assertVisibleAsync: async reader =>
            {
                SafetyStateRecord? fetched = await reader.GetAsync(CancellationToken.None);
                fetched.Should().NotBeNull("an awaited write must be observable by an independent subsequent read (D-06 store-side)");
                fetched!.WarmupStage.Should().Be(3);
            });
    }

    [Fact]
    public async Task Parallel_interleaved_writes_and_reads_produce_no_cross_reads()
    {
        ISafetyStateStoreFactory factory = CreateFactory();
        var accountA = AccountId.Create("acct-A");
        var accountB = AccountId.Create("acct-B");

        // Genuine parallelism (Task.WhenAll inside the helper, no collection disabling),
        // KEYLESS shape: A's single row is written while B's instance reads — B must
        // never observe A's row (D-11 concurrency family, T-01-10).
        await StoreContractRules.AssertParallelAccessNoCrossReadsAsync<ISafetyStateStore>(
            createStoreAsync: accountId => factory.CreateAsync(accountId, CancellationToken.None),
            accountA: accountA,
            accountB: accountB,
            writeOnAAsync: (writer, i) => writer.PutAsync(
                new SafetyStateRecord(CreatedAt.AddMilliseconds(i), WarmupStage: i), CancellationToken.None),
            readLeakOnBAsync: async reader => await reader.GetAsync(CancellationToken.None) is not null,
            iterations: 100);
    }
}
