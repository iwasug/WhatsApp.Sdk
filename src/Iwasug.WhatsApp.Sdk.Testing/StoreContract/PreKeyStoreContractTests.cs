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
/// Store-contract suite for <see cref="IPreKeyStore"/> — the same-suite-two-
/// implementations mechanism (D-10/D-11).
/// </summary>
/// <remarks>
/// Consumers subclass and bind their factory via <see cref="CreateFactory"/>. Covers the
/// two sub-stores (bundles by address, one-time pre-keys by integer id), their
/// independence, the missing-returns-null path (D-19), two-account isolation, the
/// idempotency edges (last-write-wins double-put, double-remove second false), the
/// fault-injection family (<see cref="FaultingStoreFactory"/>: faulted read throws,
/// absent key returns null), durability-await (independent second-instance read, D-06),
/// and parallel cross-read enforcement via
/// <see cref="StoreContractRules"/> — parallel
/// enforcement is uniform across all five store families (serial-only coverage is not
/// acceptable per D-11).
/// </remarks>
public abstract class PreKeyStoreContractTests
{
    /// <summary>Supplies the factory under test; the subclass binds the implementation.</summary>
    protected abstract IPreKeyStoreFactory CreateFactory();

    [Fact]
    public async Task Put_then_Get_bundle_round_trips_typed_record()
    {
        IPreKeyStoreFactory factory = CreateFactory();
        IPreKeyStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var bundle = new PreKeyBundle(SignedPreKeyId: 7, OneTimePreKeyId: 42);

        await store.PutBundleAsync("alice@s.whatsapp.net", bundle, CancellationToken.None);

        PreKeyBundle? fetched = await store.GetBundleAsync("alice@s.whatsapp.net", CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.SignedPreKeyId.Should().Be(7);
        fetched.OneTimePreKeyId.Should().Be(42);
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Put_then_Get_prekey_round_trips_typed_record()
    {
        IPreKeyStoreFactory factory = CreateFactory();
        IPreKeyStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var key = new PreKeyRecordEntry(PreKeyId: 42);

        await store.PutPreKeyAsync(42, key, CancellationToken.None);

        PreKeyRecordEntry? fetched = await store.GetPreKeyAsync(42, CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.PreKeyId.Should().Be(42);
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Get_missing_bundle_returns_null_not_throw()
    {
        IPreKeyStoreFactory factory = CreateFactory();
        IPreKeyStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        PreKeyBundle? fetched = await store.GetBundleAsync("nobody@s.whatsapp.net", CancellationToken.None);

        fetched.Should().BeNull(); // missing ≠ failure (D-19)
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Get_missing_prekey_returns_null_not_throw()
    {
        IPreKeyStoreFactory factory = CreateFactory();
        IPreKeyStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        PreKeyRecordEntry? fetched = await store.GetPreKeyAsync(999, CancellationToken.None);

        fetched.Should().BeNull(); // missing ≠ failure (D-19)
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Two_accounts_isolated_same_bundle_address()
    {
        IPreKeyStoreFactory factory = CreateFactory();
        IPreKeyStore storeA = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        IPreKeyStore storeB = await factory.CreateAsync(AccountId.Create("acct-B"), CancellationToken.None);
        const string address = "shared@s.whatsapp.net";

        await storeA.PutBundleAsync(address, new PreKeyBundle(1, 10), CancellationToken.None);
        await storeB.PutBundleAsync(address, new PreKeyBundle(2, 20), CancellationToken.None);

        PreKeyBundle? fetchedA = await storeA.GetBundleAsync(address, CancellationToken.None);
        PreKeyBundle? fetchedB = await storeB.GetBundleAsync(address, CancellationToken.None);

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
        IPreKeyStoreFactory factory = CreateFactory();
        IPreKeyStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        await store.PutPreKeyAsync(42, new PreKeyRecordEntry(42), CancellationToken.None);

        bool removed = await store.RemovePreKeyAsync(42, CancellationToken.None);
        PreKeyRecordEntry? fetched = await store.GetPreKeyAsync(42, CancellationToken.None);

        removed.Should().BeTrue();
        fetched.Should().BeNull();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Bundle_and_prekey_substores_are_independent()
    {
        IPreKeyStoreFactory factory = CreateFactory();
        IPreKeyStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        const string address = "alice@s.whatsapp.net";
        await store.PutBundleAsync(address, new PreKeyBundle(7, 42), CancellationToken.None);
        await store.PutPreKeyAsync(42, new PreKeyRecordEntry(42), CancellationToken.None);

        // Consuming the one-time pre-key (D-07 single-key CRUD) must not touch the bundle…
        await store.RemovePreKeyAsync(42, CancellationToken.None);

        PreKeyBundle? bundleAfterConsume = await store.GetBundleAsync(address, CancellationToken.None);
        PreKeyRecordEntry? preKeyAfterConsume = await store.GetPreKeyAsync(42, CancellationToken.None);
        bundleAfterConsume.Should().NotBeNull();
        preKeyAfterConsume.Should().BeNull();

        // …and overwriting the bundle must not resurrect the consumed pre-key.
        await store.PutBundleAsync(address, new PreKeyBundle(8, 43), CancellationToken.None);
        PreKeyRecordEntry? preKeyAfterBundleWrite = await store.GetPreKeyAsync(42, CancellationToken.None);
        preKeyAfterBundleWrite.Should().BeNull();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Double_put_bundle_last_write_wins()
    {
        IPreKeyStoreFactory factory = CreateFactory();
        IPreKeyStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        const string address = "alice@s.whatsapp.net";

        await store.PutBundleAsync(address, new PreKeyBundle(1, 10), CancellationToken.None);
        await store.PutBundleAsync(address, new PreKeyBundle(2, 20), CancellationToken.None);

        PreKeyBundle? fetched = await store.GetBundleAsync(address, CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.SignedPreKeyId.Should().Be(2);
        fetched.OneTimePreKeyId.Should().Be(20);
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Double_remove_prekey_second_returns_false()
    {
        IPreKeyStoreFactory factory = CreateFactory();
        IPreKeyStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        await store.PutPreKeyAsync(42, new PreKeyRecordEntry(42), CancellationToken.None);

        bool first = await store.RemovePreKeyAsync(42, CancellationToken.None);
        bool second = await store.RemovePreKeyAsync(42, CancellationToken.None);

        first.Should().BeTrue();
        second.Should().BeFalse(); // idempotency edge
        await store.DisposeAsync();
    }

    [Fact]
    public async Task FailReads_Get_throws_SdkStoreException_never_returns_null()
    {
        IPreKeyStoreFactory inner = CreateFactory();
        IPreKeyStoreFactory faulting = new FaultingStoreFactory(preKey: inner) { Mode = FaultMode.FailReads };
        IPreKeyStore store = await faulting.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        // A failing read THROWS on BOTH sub-stores — never mapped to null (D-19, T-01-09).
        Func<Task<PreKeyBundle>> bundleAct = async () => await store.GetBundleAsync("nobody@s.whatsapp.net", CancellationToken.None);
        await bundleAct.Should().ThrowAsync<SdkStoreException>();
        Func<Task<PreKeyRecordEntry>> preKeyAct = async () => await store.GetPreKeyAsync(999, CancellationToken.None);
        await preKeyAct.Should().ThrowAsync<SdkStoreException>();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Faulting_factory_Mode_None_absent_keys_return_null()
    {
        IPreKeyStoreFactory inner = CreateFactory();
        IPreKeyStoreFactory faulting = new FaultingStoreFactory(preKey: inner) { Mode = FaultMode.None };
        IPreKeyStore store = await faulting.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        // Distinct observable path from the fault above: genuinely absent keys return
        // null — failure and missing stay mechanically distinguishable (D-19).
        (await store.GetBundleAsync("nobody@s.whatsapp.net", CancellationToken.None)).Should().BeNull();
        (await store.GetPreKeyAsync(999, CancellationToken.None)).Should().BeNull();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Write_observed_by_independent_second_store_instance()
    {
        IPreKeyStoreFactory factory = CreateFactory();
        var account = AccountId.Create("acct-A");

        await StoreContractRules.AssertWriteVisibleToIndependentReadAsync<IPreKeyStore>(
            createStore: () => factory.CreateAsync(account, CancellationToken.None),
            writeAsync: writer => writer.PutBundleAsync(
                "durable@s.whatsapp.net", new PreKeyBundle(9, 99), CancellationToken.None),
            assertVisibleAsync: async reader =>
            {
                PreKeyBundle? fetched = await reader.GetBundleAsync("durable@s.whatsapp.net", CancellationToken.None);
                fetched.Should().NotBeNull("an awaited write must be observable by an independent subsequent read (D-06 store-side)");
                fetched!.SignedPreKeyId.Should().Be(9);
                fetched.OneTimePreKeyId.Should().Be(99);
            });
    }

    [Fact]
    public async Task Parallel_interleaved_prekey_writes_and_reads_produce_no_cross_reads()
    {
        IPreKeyStoreFactory factory = CreateFactory();
        var accountA = AccountId.Create("acct-A");
        var accountB = AccountId.Create("acct-B");

        // Genuine parallelism (Task.WhenAll inside the helper, no collection disabling):
        // B's prekey sub-store must never observe any id A wrote (D-11, T-01-10).
        await StoreContractRules.AssertParallelAccessNoCrossReadsAsync<IPreKeyStore>(
            createStoreAsync: accountId => factory.CreateAsync(accountId, CancellationToken.None),
            accountA: accountA,
            accountB: accountB,
            writeOnAAsync: (writer, i) => writer.PutPreKeyAsync(i, new PreKeyRecordEntry(i), CancellationToken.None),
            readLeakOnBAsync: async (reader, i) =>
                await reader.GetPreKeyAsync(i, CancellationToken.None) is not null,
            iterations: 100);
    }
}
