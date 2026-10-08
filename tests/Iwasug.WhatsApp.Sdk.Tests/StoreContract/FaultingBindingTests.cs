using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.InMemory;
using Iwasug.WhatsApp.Sdk.Testing.FaultInjection;
using Iwasug.WhatsApp.Sdk.Testing.Rules;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.StoreContract;

/// <summary>
/// Faulting binding family: proves <see cref="FaultingStoreFactory"/> itself is
/// seam-correct so hosts can rely on it for their own failure-≠-missing enforcement —
/// Mode=None passes the session CRUD round-trip through untouched, FailReads/FailWrites
/// surface <see cref="SdkStoreException"/>, and two accounts behind the decorator stay
/// isolated per the shared rule helper. Third binding family alongside InMemory
/// (reference) and Scratch (host-style); the full contract suites already run against
/// the decorator through those families' subclasses.
/// </summary>
public sealed class FaultingBindingTests
{
    private static FaultingStoreFactory CreateWrappedFactory(FaultMode mode = FaultMode.None) =>
        new(signalSession: new InMemoryStoreFactory()) { Mode = mode };

    [Fact]
    public async Task Faulting_factory_Mode_None_passes_session_crud_round_trip()
    {
        var faulting = CreateWrappedFactory();
        var store = await faulting.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var record = new SignalSessionRecord("binding@s.whatsapp.net", new byte[] { 0x01, 0x02, 0x03 });

        await store.PutAsync(record.Address, record, CancellationToken.None);
        var fetched = await store.GetAsync(record.Address, CancellationToken.None);
        var removed = await store.RemoveAsync(record.Address, CancellationToken.None);
        var afterRemove = await store.GetAsync(record.Address, CancellationToken.None);

        // Mode=None is a pure pass-through: Put/Get/Remove behave exactly as the inner
        // factory's store would.
        fetched.Should().NotBeNull();
        fetched!.SessionState.Should().Equal(record.SessionState);
        removed.Should().BeTrue();
        afterRemove.Should().BeNull();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Faulting_factory_FailReads_Get_throws_SdkStoreException()
    {
        var faulting = CreateWrappedFactory(FaultMode.FailReads);
        var store = await faulting.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        var act = async () => await store.GetAsync("binding@s.whatsapp.net", CancellationToken.None);

        // The decorator converts a failing read into SdkStoreException — never null.
        await act.Should().ThrowAsync<SdkStoreException>();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Faulting_factory_FailWrites_Put_throws_SdkStoreException()
    {
        var faulting = CreateWrappedFactory(FaultMode.FailWrites);
        var store = await faulting.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        var act = () => store.PutAsync(
            "binding@s.whatsapp.net",
            new SignalSessionRecord("binding@s.whatsapp.net", new byte[] { 0x0A }),
            CancellationToken.None);

        await act.Should().ThrowAsync<SdkStoreException>();

        // Clearing the fault restores pass-through on the same decorator instance, and
        // the failed attempt left nothing behind.
        faulting.Mode = FaultMode.None;
        await store.PutAsync(
            "binding@s.whatsapp.net",
            new SignalSessionRecord("binding@s.whatsapp.net", new byte[] { 0x0A }),
            CancellationToken.None);
        (await store.GetAsync("binding@s.whatsapp.net", CancellationToken.None)).Should().NotBeNull();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Faulting_factory_two_accounts_isolated_via_rule_helper()
    {
        var faulting = CreateWrappedFactory();
        var accountA = AccountId.Create("acct-A");
        var accountB = AccountId.Create("acct-B");

        // The shared rule helper (the same one the contract classes use) confirms the
        // decorator preserves per-account namespacing (D-02).
        await StoreContractRules.AssertTwoAccountsIsolatedAsync<ISignalSessionStore>(
            createStoreAsync: accountId => faulting.CreateAsync(accountId, CancellationToken.None),
            accountA: accountA,
            accountB: accountB,
            writeSlotValueAsync: (store, slot) => store.PutAsync(
                "shared@s.whatsapp.net",
                new SignalSessionRecord("shared@s.whatsapp.net", new[] { (byte)(0xA0 + slot) }),
                CancellationToken.None),
            assertSeesOnlySlotValueAsync: async (store, slot) =>
            {
                var fetched = await store.GetAsync("shared@s.whatsapp.net", CancellationToken.None);
                fetched.Should().NotBeNull();
                fetched!.SessionState.Should().Equal(new byte[] { (byte)(0xA0 + slot) });
            });
    }
}
