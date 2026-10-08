using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Stores;
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
/// subclass — that inheritance dispatch is the entire mechanism. Full fault-injection
/// variants of the failure-≠-missing rule arrive with plan 01-03; this class asserts the
/// missing-returns-null path here.
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
}
