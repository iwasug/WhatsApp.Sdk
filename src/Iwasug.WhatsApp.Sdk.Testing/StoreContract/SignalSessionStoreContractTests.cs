using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Testing.FaultInjection;
using Iwasug.WhatsApp.Sdk.Testing.Rules;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Testing.StoreContract;

/// <summary>
/// Store-contract suite for <see cref="ISignalSessionStore"/> — the same-suite-two-
/// implementations mechanism (D-10/D-11, EF Core specification-tests pattern).
/// </summary>
/// <remarks>
/// Consumers (the SDK's own reference binding, and hosts verifying their persistence
/// implementations) subclass this type and override <see cref="CreateFactory"/> to supply
/// the factory under test. xunit runs inherited <c>[Fact]</c> methods in every concrete
/// subclass — that inheritance dispatch is the entire mechanism; don't hand-roll a runner.
/// This class covers the full D-11 rule family for the session seam: round-trip,
/// failure-≠-missing on BOTH paths (a faulting read throws via
/// <see cref="FaultingStoreFactory"/>; a genuinely absent key returns null — D-19),
/// per-account isolation, durability-await (write observable by an independent
/// second-instance read, D-06), and parallel cross-read enforcement via
/// <see cref="StoreContractRules.AssertParallelAccessNoCrossReadsAsync"/> — parallel
/// enforcement is uniform across all five store families (serial-only coverage is not
/// acceptable per D-11).
/// </remarks>
public abstract class SignalSessionStoreContractTests
{
    /// <summary>Supplies the factory under test; the subclass binds the implementation.</summary>
    protected abstract ISignalSessionStoreFactory CreateFactory();

    [Fact]
    public async Task Put_then_Get_round_trips_typed_record()
    {
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var record = new SignalSessionRecord("alice@s.whatsapp.net", new byte[] { 1, 2, 3, 4 });

        await store.PutAsync(record.Address, record, CancellationToken.None);

        // Independent subsequent read observes the value (D-06 store-side semantics).
        var fetched = await store.GetAsync(record.Address, CancellationToken.None);

        fetched.Should().NotBeNull();
        var observed = fetched!;
        observed.Address.Should().Be(record.Address);
        observed.SessionState.Should().Equal(record.SessionState);
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Get_missing_address_returns_null_not_throw()
    {
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        var fetched = await store.GetAsync("nobody@s.whatsapp.net", CancellationToken.None);

        fetched.Should().BeNull();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Two_accounts_isolated_same_address()
    {
        var factory = CreateFactory();
        var storeA = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var storeB = await factory.CreateAsync(AccountId.Create("acct-B"), CancellationToken.None);
        const string address = "shared@s.whatsapp.net";
        var recordA = new SignalSessionRecord(address, new byte[] { 0xAA });
        var recordB = new SignalSessionRecord(address, new byte[] { 0xBB });

        await storeA.PutAsync(address, recordA, CancellationToken.None);
        await storeB.PutAsync(address, recordB, CancellationToken.None);

        var fetchedA = await storeA.GetAsync(address, CancellationToken.None);
        var fetchedB = await storeB.GetAsync(address, CancellationToken.None);

        // Structural namespacing (D-02): same address, two accounts, zero cross-writes.
        fetchedA.Should().NotBeNull();
        fetchedB.Should().NotBeNull();
        fetchedA!.SessionState.Should().Equal(recordA.SessionState);
        fetchedB!.SessionState.Should().Equal(recordB.SessionState);
        await storeA.DisposeAsync();
        await storeB.DisposeAsync();
    }

    [Fact]
    public async Task FailReads_Get_throws_SdkStoreException_never_returns_null()
    {
        var inner = CreateFactory();
        var faulting = new FaultingStoreFactory(signalSession: inner) { Mode = FaultMode.FailReads };
        var store = await faulting.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        // A failing read THROWS — it must never surface as null (D-19: a DB blip misread as
        // "empty session" wrongly triggers session re-establishment; T-01-09).
        var act = async () => await store.GetAsync("nobody@s.whatsapp.net", CancellationToken.None);
        await act.Should().ThrowAsync<SdkStoreException>();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Faulting_factory_Mode_None_absent_key_returns_null()
    {
        var inner = CreateFactory();
        var faulting = new FaultingStoreFactory(signalSession: inner) { Mode = FaultMode.None };
        var store = await faulting.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        var fetched = await store.GetAsync("nobody@s.whatsapp.net", CancellationToken.None);

        // Distinct observable path from the fault above: a genuinely absent key returns
        // null — failure and missing stay mechanically distinguishable (D-19).
        fetched.Should().BeNull();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Write_observed_by_independent_second_store_instance()
    {
        var factory = CreateFactory();
        var account = AccountId.Create("acct-A");

        await StoreContractRules.AssertWriteVisibleToIndependentReadAsync<ISignalSessionStore>(
            createStore: () => factory.CreateAsync(account, CancellationToken.None),
            writeAsync: async writer =>
            {
                var record = new SignalSessionRecord("durable@s.whatsapp.net", new byte[] { 0x01, 0x02 });
                await writer.PutAsync(record.Address, record, CancellationToken.None);
            },
            assertVisibleAsync: async reader =>
            {
                var fetched = await reader.GetAsync("durable@s.whatsapp.net", CancellationToken.None);
                fetched.Should().NotBeNull("an awaited write must be observable by an independent subsequent read (D-06 store-side)");
                fetched!.SessionState.Should().Equal(new byte[] { 0x01, 0x02 });
            });
    }

    [Fact]
    public async Task Parallel_interleaved_writes_and_reads_produce_no_cross_reads()
    {
        var factory = CreateFactory();
        var accountA = AccountId.Create("acct-A");
        var accountB = AccountId.Create("acct-B");

        // Genuine parallelism (Task.WhenAll inside the helper, no collection disabling):
        // B must never observe any key A wrote (D-11 concurrency family, T-01-10).
        await StoreContractRules.AssertParallelAccessNoCrossReadsAsync<ISignalSessionStore>(
            createStoreAsync: accountId => factory.CreateAsync(accountId, CancellationToken.None),
            accountA: accountA,
            accountB: accountB,
            writeOnAAsync: (writer, i) => writer.PutAsync(
                $"parallel-{i}@s.whatsapp.net",
                new SignalSessionRecord($"parallel-{i}@s.whatsapp.net", new[] { (byte)i }),
                CancellationToken.None),
            readLeakOnBAsync: async (reader, i) =>
                await reader.GetAsync($"parallel-{i}@s.whatsapp.net", CancellationToken.None) is not null,
            iterations: 100);
    }
}
