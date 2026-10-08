using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Stores;
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
/// carry.
/// </remarks>
public abstract class SafetyStateStoreContractTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Supplies the factory under test; the subclass binds the implementation.</summary>
    protected abstract ISafetyStateStoreFactory CreateFactory();

    [Fact]
    public async Task Put_then_Get_round_trips_typed_record()
    {
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var record = new SafetyStateRecord(CreatedAt, WarmupStage: 3);

        await store.PutAsync(record, CancellationToken.None);

        var fetched = await store.GetAsync(CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.CreatedAt.Should().Be(CreatedAt);
        fetched.WarmupStage.Should().Be(3);
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Get_missing_row_returns_null_not_throw()
    {
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        var fetched = await store.GetAsync(CancellationToken.None);

        fetched.Should().BeNull(); // missing ≠ failure (D-19)
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Two_accounts_see_independent_single_rows()
    {
        var factory = CreateFactory();
        var storeA = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var storeB = await factory.CreateAsync(AccountId.Create("acct-B"), CancellationToken.None);

        await storeA.PutAsync(new SafetyStateRecord(CreatedAt, WarmupStage: 4), CancellationToken.None);

        // Structural namespacing (D-02 / T-01-05): A's row is invisible to B.
        var rowA = await storeA.GetAsync(CancellationToken.None);
        var rowB = await storeB.GetAsync(CancellationToken.None);
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
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        await store.PutAsync(new SafetyStateRecord(CreatedAt, WarmupStage: 1), CancellationToken.None);
        await store.PutAsync(new SafetyStateRecord(CreatedAt, WarmupStage: 2), CancellationToken.None);

        var fetched = await store.GetAsync(CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.WarmupStage.Should().Be(2);
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Double_remove_second_returns_false()
    {
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        await store.PutAsync(new SafetyStateRecord(CreatedAt, WarmupStage: 1), CancellationToken.None);

        var first = await store.RemoveAsync(CancellationToken.None);
        var second = await store.RemoveAsync(CancellationToken.None);

        first.Should().BeTrue();
        second.Should().BeFalse(); // idempotency edge: removing an absent row reports false
        await store.DisposeAsync();
    }
}
