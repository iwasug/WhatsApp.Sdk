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
/// Store-contract suite for <see cref="IIdentityStore"/> — the same-suite-two-
/// implementations mechanism (D-10/D-11, EF Core specification-tests pattern).
/// </summary>
/// <remarks>
/// Consumers (the SDK's own reference binding, and hosts verifying their persistence
/// implementations) subclass this type and override <see cref="CreateFactory"/> to supply
/// the factory under test. xunit runs inherited <c>[Fact]</c> methods in every concrete
/// subclass — that inheritance dispatch is the entire mechanism. This class covers the
/// full D-11 rule family for the identity seam: round-trip, failure-≠-missing on BOTH
/// paths (a faulting read throws via <see cref="FaultingStoreFactory"/>; a genuinely
/// absent key returns null — D-19), two-account isolation, idempotency edges,
/// durability-await (independent second-instance read, D-06), and parallel cross-read
/// enforcement via <see cref="StoreContractRules"/>
/// — parallel enforcement is uniform across all five store families (serial-only
/// coverage is not acceptable per D-11).
/// </remarks>
public abstract class IdentityStoreContractTests
{
    /// <summary>Supplies the factory under test; the subclass binds the implementation.</summary>
    protected abstract IIdentityStoreFactory CreateFactory();

    [Fact]
    public async Task Put_then_Get_round_trips_typed_record()
    {
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var record = new SignalIdentityRecord("identity-key-public");

        await store.PutAsync("alice@s.whatsapp.net", record, CancellationToken.None);

        // Independent subsequent read observes the value (D-06 store-side semantics).
        var fetched = await store.GetAsync("alice@s.whatsapp.net", CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.IdentityKeyPublic.Should().Be(record.IdentityKeyPublic);
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Get_missing_address_returns_null_not_throw()
    {
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        var fetched = await store.GetAsync("nobody@s.whatsapp.net", CancellationToken.None);

        fetched.Should().BeNull(); // missing ≠ failure (D-19) — full fault injection lands in 01-03
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Two_accounts_isolated_same_address()
    {
        var factory = CreateFactory();
        var storeA = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var storeB = await factory.CreateAsync(AccountId.Create("acct-B"), CancellationToken.None);
        const string address = "shared@s.whatsapp.net";
        var recordA = new SignalIdentityRecord("key-A");
        var recordB = new SignalIdentityRecord("key-B");

        await storeA.PutAsync(address, recordA, CancellationToken.None);
        await storeB.PutAsync(address, recordB, CancellationToken.None);

        var fetchedA = await storeA.GetAsync(address, CancellationToken.None);
        var fetchedB = await storeB.GetAsync(address, CancellationToken.None);

        // Structural namespacing (D-02): same address, two accounts, zero cross-writes (T-01-05).
        fetchedA.Should().NotBeNull();
        fetchedB.Should().NotBeNull();
        fetchedA!.IdentityKeyPublic.Should().Be("key-A");
        fetchedB!.IdentityKeyPublic.Should().Be("key-B");
        await storeA.DisposeAsync();
        await storeB.DisposeAsync();
    }

    [Fact]
    public async Task Double_put_same_address_last_write_wins()
    {
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        const string address = "alice@s.whatsapp.net";

        await store.PutAsync(address, new SignalIdentityRecord("first"), CancellationToken.None);
        await store.PutAsync(address, new SignalIdentityRecord("second"), CancellationToken.None);

        var fetched = await store.GetAsync(address, CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.IdentityKeyPublic.Should().Be("second");
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Double_remove_same_address_second_returns_false()
    {
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        const string address = "alice@s.whatsapp.net";
        await store.PutAsync(address, new SignalIdentityRecord("key"), CancellationToken.None);

        var first = await store.RemoveAsync(address, CancellationToken.None);
        var second = await store.RemoveAsync(address, CancellationToken.None);

        first.Should().BeTrue();
        second.Should().BeFalse(); // idempotency edge: removing an absent key reports false
        await store.DisposeAsync();
    }

    [Fact]
    public async Task FailReads_Get_throws_SdkStoreException_never_returns_null()
    {
        var inner = CreateFactory();
        IIdentityStoreFactory faulting = new FaultingStoreFactory(identity: inner) { Mode = FaultMode.FailReads };
        var store = await faulting.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        // A failing read THROWS — it must never surface as null (D-19: a flaky identity
        // read misinterpreted as "unknown peer" would force needless key re-fetch; T-01-09).
        var act = async () => await store.GetAsync("nobody@s.whatsapp.net", CancellationToken.None);
        await act.Should().ThrowAsync<SdkStoreException>();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Faulting_factory_Mode_None_absent_key_returns_null()
    {
        var inner = CreateFactory();
        IIdentityStoreFactory faulting = new FaultingStoreFactory(identity: inner) { Mode = FaultMode.None };
        var store = await faulting.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        // Distinct observable path from the fault above: a genuinely absent key returns
        // null — failure and missing stay mechanically distinguishable (D-19).
        (await store.GetAsync("nobody@s.whatsapp.net", CancellationToken.None)).Should().BeNull();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Write_observed_by_independent_second_store_instance()
    {
        var factory = CreateFactory();
        var account = AccountId.Create("acct-A");

        await StoreContractRules.AssertWriteVisibleToIndependentReadAsync<IIdentityStore>(
            createStore: () => factory.CreateAsync(account, CancellationToken.None),
            writeAsync: writer => writer.PutAsync(
                "durable@s.whatsapp.net", new SignalIdentityRecord("durable-key"), CancellationToken.None),
            assertVisibleAsync: async reader =>
            {
                var fetched = await reader.GetAsync("durable@s.whatsapp.net", CancellationToken.None);
                fetched.Should().NotBeNull("an awaited write must be observable by an independent subsequent read (D-06 store-side)");
                fetched!.IdentityKeyPublic.Should().Be("durable-key");
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
        await StoreContractRules.AssertParallelAccessNoCrossReadsAsync<IIdentityStore>(
            createStoreAsync: accountId => factory.CreateAsync(accountId, CancellationToken.None),
            accountA: accountA,
            accountB: accountB,
            writeOnAAsync: (writer, i) => writer.PutAsync(
                $"parallel-{i}@s.whatsapp.net", new SignalIdentityRecord($"key-A-{i}"), CancellationToken.None),
            readLeakOnBAsync: async (reader, i) =>
                await reader.GetAsync($"parallel-{i}@s.whatsapp.net", CancellationToken.None) is not null,
            iterations: 100);
    }
}
