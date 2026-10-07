using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Stores;
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
/// This Phase 1 slice covers the round-trip, failure-≠-missing (missing case), and
/// per-account isolation rules; the full D-11 rule families (durability-await, fault
/// injection, parallelism, trimming) arrive with plans 01-03/01-04.
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
}
