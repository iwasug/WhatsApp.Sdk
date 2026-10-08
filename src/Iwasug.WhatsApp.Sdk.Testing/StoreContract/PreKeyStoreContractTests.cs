using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Stores;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Testing.StoreContract;

/// <summary>
/// Store-contract suite for <see cref="IPreKeyStore"/> — the same-suite-two-
/// implementations mechanism (D-10/D-11).
/// </summary>
/// <remarks>
/// Consumers subclass and bind their factory via <see cref="CreateFactory"/>. Covers the
/// two sub-stores (bundles by address, one-time pre-keys by integer id), their
/// independence, the missing-returns-null path (D-19), two-account isolation, and the
/// idempotency edges (last-write-wins double-put, double-remove second false).
/// </remarks>
public abstract class PreKeyStoreContractTests
{
    /// <summary>Supplies the factory under test; the subclass binds the implementation.</summary>
    protected abstract IPreKeyStoreFactory CreateFactory();

    [Fact]
    public async Task Put_then_Get_bundle_round_trips_typed_record()
    {
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var bundle = new PreKeyBundle(SignedPreKeyId: 7, OneTimePreKeyId: 42);

        await store.PutBundleAsync("alice@s.whatsapp.net", bundle, CancellationToken.None);

        var fetched = await store.GetBundleAsync("alice@s.whatsapp.net", CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.SignedPreKeyId.Should().Be(7);
        fetched.OneTimePreKeyId.Should().Be(42);
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Put_then_Get_prekey_round_trips_typed_record()
    {
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var key = new PreKeyRecordEntry(PreKeyId: 42);

        await store.PutPreKeyAsync(42, key, CancellationToken.None);

        var fetched = await store.GetPreKeyAsync(42, CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.PreKeyId.Should().Be(42);
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Get_missing_bundle_returns_null_not_throw()
    {
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        var fetched = await store.GetBundleAsync("nobody@s.whatsapp.net", CancellationToken.None);

        fetched.Should().BeNull(); // missing ≠ failure (D-19)
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Get_missing_prekey_returns_null_not_throw()
    {
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        var fetched = await store.GetPreKeyAsync(999, CancellationToken.None);

        fetched.Should().BeNull(); // missing ≠ failure (D-19)
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Two_accounts_isolated_same_bundle_address()
    {
        var factory = CreateFactory();
        var storeA = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var storeB = await factory.CreateAsync(AccountId.Create("acct-B"), CancellationToken.None);
        const string address = "shared@s.whatsapp.net";

        await storeA.PutBundleAsync(address, new PreKeyBundle(1, 10), CancellationToken.None);
        await storeB.PutBundleAsync(address, new PreKeyBundle(2, 20), CancellationToken.None);

        var fetchedA = await storeA.GetBundleAsync(address, CancellationToken.None);
        var fetchedB = await storeB.GetBundleAsync(address, CancellationToken.None);

        // Structural namespacing (D-02 / T-01-05): same address, zero cross-writes.
        fetchedA.Should().NotBeNull();
        fetchedB.Should().NotBeNull();
        fetchedA!.SignedPreKeyId.Should().Be(1);
        fetchedB!.SignedPreKeyId.Should().Be(2);
        await storeA.DisposeAsync();
        await storeB.DisposeAsync();
    }

    [Fact]
    public async Task Remove_prekey_then_get_returns_null()
    {
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        await store.PutPreKeyAsync(42, new PreKeyRecordEntry(42), CancellationToken.None);

        var removed = await store.RemovePreKeyAsync(42, CancellationToken.None);
        var fetched = await store.GetPreKeyAsync(42, CancellationToken.None);

        removed.Should().BeTrue();
        fetched.Should().BeNull();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Bundle_and_prekey_substores_are_independent()
    {
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        const string address = "alice@s.whatsapp.net";
        await store.PutBundleAsync(address, new PreKeyBundle(7, 42), CancellationToken.None);
        await store.PutPreKeyAsync(42, new PreKeyRecordEntry(42), CancellationToken.None);

        // Consuming the one-time pre-key (D-07 single-key CRUD) must not touch the bundle…
        await store.RemovePreKeyAsync(42, CancellationToken.None);

        var bundleAfterConsume = await store.GetBundleAsync(address, CancellationToken.None);
        var preKeyAfterConsume = await store.GetPreKeyAsync(42, CancellationToken.None);
        bundleAfterConsume.Should().NotBeNull();
        preKeyAfterConsume.Should().BeNull();

        // …and overwriting the bundle must not resurrect the consumed pre-key.
        await store.PutBundleAsync(address, new PreKeyBundle(8, 43), CancellationToken.None);
        var preKeyAfterBundleWrite = await store.GetPreKeyAsync(42, CancellationToken.None);
        preKeyAfterBundleWrite.Should().BeNull();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Double_put_bundle_last_write_wins()
    {
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        const string address = "alice@s.whatsapp.net";

        await store.PutBundleAsync(address, new PreKeyBundle(1, 10), CancellationToken.None);
        await store.PutBundleAsync(address, new PreKeyBundle(2, 20), CancellationToken.None);

        var fetched = await store.GetBundleAsync(address, CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.SignedPreKeyId.Should().Be(2);
        fetched.OneTimePreKeyId.Should().Be(20);
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Double_remove_prekey_second_returns_false()
    {
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        await store.PutPreKeyAsync(42, new PreKeyRecordEntry(42), CancellationToken.None);

        var first = await store.RemovePreKeyAsync(42, CancellationToken.None);
        var second = await store.RemovePreKeyAsync(42, CancellationToken.None);

        first.Should().BeTrue();
        second.Should().BeFalse(); // idempotency edge
        await store.DisposeAsync();
    }
}
