using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Testing.FaultInjection;
using Iwasug.WhatsApp.Sdk.Testing.Rules;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Testing.StoreContract;

/// <summary>
/// Store-contract suite for <see cref="IRecipientContactStore"/> — the same-suite-two-
/// implementations mechanism (D-10/D-11) for the additive seventh capability (SAF-04).
/// </summary>
/// <remarks>
/// Consumers subclass and bind their factory via <see cref="CreateFactory"/>. The
/// recipient ledger is a keyed set per account (planner decision): contact facts are
/// keyed by a deterministic, privacy-neutral recipient key, so this class asserts
/// keyed-set semantics — missing-key-returns-false, idempotent mark, per-account
/// isolation — plus the D-19 fault-injection family (<see cref="FaultingStoreFactory"/>),
/// durability-await (independent second-instance read, D-06), and parallel cross-read
/// enforcement via the keyed shape of the shared <see cref="StoreContractRules"/>
/// helper — parallel enforcement is uniform across all seven store families
/// (serial-only coverage is not acceptable per D-11).
/// </remarks>
public abstract class RecipientContactStoreContractTests
{
    /// <summary>Supplies the factory under test; the subclass binds the implementation.</summary>
    protected abstract IRecipientContactStoreFactory CreateFactory();

    [Fact]
    public async Task Mark_then_Has_returns_true()
    {
        IRecipientContactStoreFactory factory = CreateFactory();
        IRecipientContactStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        await store.MarkContactedAsync("15551234567@s.whatsapp.net", CancellationToken.None);

        (await store.HasContactedAsync("15551234567@s.whatsapp.net", CancellationToken.None)).Should().BeTrue();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Missing_key_returns_false_not_throw()
    {
        IRecipientContactStoreFactory factory = CreateFactory();
        IRecipientContactStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        (await store.HasContactedAsync("15557654321@s.whatsapp.net", CancellationToken.None)).Should().BeFalse(); // missing ≠ failure (D-19)
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Mark_is_idempotent()
    {
        IRecipientContactStoreFactory factory = CreateFactory();
        IRecipientContactStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        await store.MarkContactedAsync("15551234567@s.whatsapp.net", CancellationToken.None);
        await store.MarkContactedAsync("15551234567@s.whatsapp.net", CancellationToken.None);

        // Idempotency edge: remarking an already-contacted recipient stays a no-op success.
        (await store.HasContactedAsync("15551234567@s.whatsapp.net", CancellationToken.None)).Should().BeTrue();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Two_accounts_isolated_same_key()
    {
        IRecipientContactStoreFactory factory = CreateFactory();
        IRecipientContactStore storeA = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        IRecipientContactStore storeB = await factory.CreateAsync(AccountId.Create("acct-B"), CancellationToken.None);

        await storeA.MarkContactedAsync("15551234567@s.whatsapp.net", CancellationToken.None);

        // Structural namespacing (D-02 / T-01-05): A's contact fact is invisible to B.
        (await storeA.HasContactedAsync("15551234567@s.whatsapp.net", CancellationToken.None)).Should().BeTrue();
        (await storeB.HasContactedAsync("15551234567@s.whatsapp.net", CancellationToken.None)).Should().BeFalse();

        await storeB.MarkContactedAsync("15557654321@s.whatsapp.net", CancellationToken.None);
        (await storeA.HasContactedAsync("15557654321@s.whatsapp.net", CancellationToken.None)).Should().BeFalse();
        (await storeB.HasContactedAsync("15557654321@s.whatsapp.net", CancellationToken.None)).Should().BeTrue();
        await storeA.DisposeAsync();
        await storeB.DisposeAsync();
    }

    [Fact]
    public async Task FailReads_HasContacted_throws_SdkStoreException_never_returns_false()
    {
        IRecipientContactStoreFactory inner = CreateFactory();
        IRecipientContactStoreFactory faulting = new FaultingStoreFactory(recipientContact: inner) { Mode = FaultMode.FailReads };
        IRecipientContactStore store = await faulting.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        // A failing read THROWS — it must never surface as false (D-19: a ledger
        // blip misread as "never contacted" would wrongly consume cold-outreach budget).
        Func<Task<bool>> act = async () => await store.HasContactedAsync("15551234567@s.whatsapp.net", CancellationToken.None);
        await act.Should().ThrowAsync<SdkStoreException>();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task FailWrites_Mark_throws_SdkStoreException()
    {
        IRecipientContactStoreFactory inner = CreateFactory();
        IRecipientContactStoreFactory faulting = new FaultingStoreFactory(recipientContact: inner) { Mode = FaultMode.FailWrites };
        IRecipientContactStore store = await faulting.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        // A failing mark THROWS — the gate's D-20 log-and-continue is SDK-side policy,
        // not part of the store primitive (the primitive itself still throws).
        Func<Task> act = async () => await store.MarkContactedAsync("15551234567@s.whatsapp.net", CancellationToken.None);
        await act.Should().ThrowAsync<SdkStoreException>();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Faulting_factory_Mode_None_missing_key_returns_false()
    {
        IRecipientContactStoreFactory inner = CreateFactory();
        IRecipientContactStoreFactory faulting = new FaultingStoreFactory(recipientContact: inner) { Mode = FaultMode.None };
        IRecipientContactStore store = await faulting.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        // Distinct observable path from the faults above: a genuinely uncontacted key
        // returns false — failure and missing stay mechanically distinguishable (D-19).
        (await store.HasContactedAsync("15551234567@s.whatsapp.net", CancellationToken.None)).Should().BeFalse();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Write_observed_by_independent_second_store_instance()
    {
        IRecipientContactStoreFactory factory = CreateFactory();
        var account = AccountId.Create("acct-A");

        await StoreContractRules.AssertWriteVisibleToIndependentReadAsync<IRecipientContactStore>(
            createStore: () => factory.CreateAsync(account, CancellationToken.None),
            writeAsync: writer => writer.MarkContactedAsync(
                "15551234567@s.whatsapp.net", CancellationToken.None),
            assertVisibleAsync: async reader =>
            {
                (await reader.HasContactedAsync("15551234567@s.whatsapp.net", CancellationToken.None))
                    .Should().BeTrue("an awaited mark must be observable by an independent subsequent read (D-06 store-side)");
            });
    }

    [Fact]
    public async Task Parallel_interleaved_marks_and_reads_produce_no_cross_reads()
    {
        IRecipientContactStoreFactory factory = CreateFactory();
        var accountA = AccountId.Create("acct-A");
        var accountB = AccountId.Create("acct-B");

        // Genuine parallelism (Task.WhenAll inside the helper, no collection disabling),
        // KEYED shape: A's keys are marked while B's instance reads the same keys — B
        // must never observe A's contact facts (D-11 concurrency family, T-01-10).
        await StoreContractRules.AssertParallelAccessNoCrossReadsAsync<IRecipientContactStore>(
            createStoreAsync: accountId => factory.CreateAsync(accountId, CancellationToken.None),
            accountA: accountA,
            accountB: accountB,
            writeOnAAsync: (writer, i) => writer.MarkContactedAsync(
                $"1555000{i:000}@s.whatsapp.net", CancellationToken.None),
            readLeakOnBAsync: async (reader, i) => await reader.HasContactedAsync(
                $"1555000{i:000}@s.whatsapp.net", CancellationToken.None),
            iterations: 100);
    }
}
