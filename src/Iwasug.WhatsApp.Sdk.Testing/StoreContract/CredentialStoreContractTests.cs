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
/// Store-contract suite for <see cref="ICredentialStore"/> — the same-suite-two-
/// implementations mechanism (D-10/D-11).
/// </summary>
/// <remarks>
/// Consumers subclass and bind their factory via <see cref="CreateFactory"/>. Credentials
/// are a single mutable row per account (planner decision), so this class asserts
/// single-row semantics — last-write-wins Put, idempotent Remove, missing-row-returns-
/// null — plus per-account isolation, instead of the keyed-entry facts the other stores
/// carry. Fault-injection (<see cref="FaultingStoreFactory"/>), durability-await
/// (independent second-instance read, D-06), and parallel cross-read enforcement via
/// the keyless shape of the shared <see cref="StoreContractRules"/> helper (A's row is
/// written while B's instance reads — B must never observe A's row) — parallel
/// enforcement is uniform across all six store families (serial-only coverage is not
/// acceptable per D-11).
/// </remarks>
public abstract class CredentialStoreContractTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static byte[] FixedBytes(int start)
    {
        byte[] bytes = new byte[32];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(start + i);
        }

        return bytes;
    }

    private static CredentialRecord TestRow(string? user = "15551234567", int device = 1) =>
        new(
            CreatedAt,
            "android",
            FixedBytes(0x01),
            FixedBytes(0x21),
            FixedBytes(0x41),
            FixedBytes(0x61),
            user,
            device,
            42);

    /// <summary>Supplies the factory under test; the subclass binds the implementation.</summary>
    protected abstract ICredentialStoreFactory CreateFactory();

    [Fact]
    public async Task Put_then_Get_round_trips_typed_record()
    {
        ICredentialStoreFactory factory = CreateFactory();
        ICredentialStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        CredentialRecord record = TestRow();

        await store.PutAsync(record, CancellationToken.None);

        CredentialRecord? fetched = await store.GetAsync(CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.CreatedAt.Should().Be(CreatedAt);
        fetched.Platform.Should().Be("android");
        fetched.NoiseKeyPrivate.Should().Equal(FixedBytes(0x01));
        fetched.IdentityKeyPrivate.Should().Equal(FixedBytes(0x21));
        fetched.SigningKeyPrivate.Should().Equal(FixedBytes(0x41));
        fetched.AdvSecret.Should().Equal(FixedBytes(0x61));
        fetched.PairedUser.Should().Be("15551234567");
        fetched.Device.Should().Be(1);
        fetched.RegistrationId.Should().Be(42);
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Get_missing_row_returns_null_not_throw()
    {
        ICredentialStoreFactory factory = CreateFactory();
        ICredentialStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        CredentialRecord? fetched = await store.GetAsync(CancellationToken.None);

        fetched.Should().BeNull(); // missing ≠ failure (D-19)
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Two_accounts_see_independent_single_rows()
    {
        ICredentialStoreFactory factory = CreateFactory();
        ICredentialStore storeA = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        ICredentialStore storeB = await factory.CreateAsync(AccountId.Create("acct-B"), CancellationToken.None);

        await storeA.PutAsync(TestRow("1111111111", 1), CancellationToken.None);

        // Structural namespacing (D-02 / T-01-05): A's row is invisible to B.
        CredentialRecord? rowA = await storeA.GetAsync(CancellationToken.None);
        CredentialRecord? rowB = await storeB.GetAsync(CancellationToken.None);
        rowA.Should().NotBeNull();
        rowB.Should().BeNull();

        await storeB.PutAsync(TestRow("2222222222", 2), CancellationToken.None);
        rowA = await storeA.GetAsync(CancellationToken.None);
        rowB = await storeB.GetAsync(CancellationToken.None);
        rowA!.PairedUser.Should().Be("1111111111");
        rowB!.PairedUser.Should().Be("2222222222");
        rowB.Device.Should().Be(2);
        await storeA.DisposeAsync();
        await storeB.DisposeAsync();
    }

    [Fact]
    public async Task Double_put_overwrites_single_row_last_write_wins()
    {
        ICredentialStoreFactory factory = CreateFactory();
        ICredentialStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        await store.PutAsync(TestRow("1111111111", 1), CancellationToken.None);
        await store.PutAsync(TestRow("2222222222", 2), CancellationToken.None);

        CredentialRecord? fetched = await store.GetAsync(CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.PairedUser.Should().Be("2222222222");
        fetched.Device.Should().Be(2);
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Double_remove_second_returns_false()
    {
        ICredentialStoreFactory factory = CreateFactory();
        ICredentialStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        await store.PutAsync(TestRow(), CancellationToken.None);

        bool first = await store.RemoveAsync(CancellationToken.None);
        bool second = await store.RemoveAsync(CancellationToken.None);

        first.Should().BeTrue();
        second.Should().BeFalse(); // idempotency edge: removing an absent row reports false
        await store.DisposeAsync();
    }

    [Fact]
    public async Task FailReads_Get_throws_SdkStoreException_never_returns_null()
    {
        ICredentialStoreFactory inner = CreateFactory();
        ICredentialStoreFactory faulting = new FaultingStoreFactory(credential: inner) { Mode = FaultMode.FailReads };
        ICredentialStore store = await faulting.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        // A failing read THROWS — it must never surface as null (D-19: a credential
        // blip misread as "no row" would re-enter registration and risk the pairing).
        Func<Task<CredentialRecord>> act = async () => await store.GetAsync(CancellationToken.None);
        await act.Should().ThrowAsync<SdkStoreException>();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Faulting_factory_Mode_None_absent_row_returns_null()
    {
        ICredentialStoreFactory inner = CreateFactory();
        ICredentialStoreFactory faulting = new FaultingStoreFactory(credential: inner) { Mode = FaultMode.None };
        ICredentialStore store = await faulting.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        // Distinct observable path from the fault above: a genuinely absent row returns
        // null — failure and missing stay mechanically distinguishable (D-19).
        (await store.GetAsync(CancellationToken.None)).Should().BeNull();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Write_observed_by_independent_second_store_instance()
    {
        ICredentialStoreFactory factory = CreateFactory();
        var account = AccountId.Create("acct-A");

        await StoreContractRules.AssertWriteVisibleToIndependentReadAsync<ICredentialStore>(
            createStore: () => factory.CreateAsync(account, CancellationToken.None),
            writeAsync: writer => writer.PutAsync(TestRow(), CancellationToken.None),
            assertVisibleAsync: async reader =>
            {
                CredentialRecord? fetched = await reader.GetAsync(CancellationToken.None);
                fetched.Should().NotBeNull("an awaited write must be observable by an independent subsequent read (D-06 store-side)");
                fetched!.PairedUser.Should().Be("15551234567");
            });
    }

    [Fact]
    public async Task Parallel_interleaved_writes_and_reads_produce_no_cross_reads()
    {
        ICredentialStoreFactory factory = CreateFactory();
        var accountA = AccountId.Create("acct-A");
        var accountB = AccountId.Create("acct-B");

        // Genuine parallelism (Task.WhenAll inside the helper, no collection disabling),
        // KEYLESS shape: A's single row is written while B's instance reads — B must
        // never observe A's row (D-11 concurrency family, T-01-10).
        await StoreContractRules.AssertParallelAccessNoCrossReadsAsync<ICredentialStore>(
            createStoreAsync: accountId => factory.CreateAsync(accountId, CancellationToken.None),
            accountA: accountA,
            accountB: accountB,
            writeOnAAsync: (writer, i) => writer.PutAsync(
                TestRow("15551234567", i), CancellationToken.None),
            readLeakOnBAsync: async reader => await reader.GetAsync(CancellationToken.None) is not null,
            iterations: 100);
    }
}
