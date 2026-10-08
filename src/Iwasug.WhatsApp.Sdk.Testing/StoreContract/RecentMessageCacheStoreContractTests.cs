using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Testing.FaultInjection;
using Iwasug.WhatsApp.Sdk.Testing.Rules;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Testing.StoreContract;

/// <summary>
/// Store-contract suite for <see cref="IRecentMessageCacheStore"/> — the same-suite-two-
/// implementations mechanism (D-10/D-11).
/// </summary>
/// <remarks>
/// Consumers subclass and bind their factory via <see cref="CreateFactory"/>. The trim
/// facts exercise the PRIMITIVES only (<see cref="IRecentMessageCacheStore.TrimOldestAsync"/>/
/// <see cref="IRecentMessageCacheStore.PurgeExpiredAsync"/>): the runtime contract is that
/// the primitives execute and observably change state, while the retention POLICY itself
/// lives in SDK-owned IOptions and never appears in interface signatures (D-08, Pitfall 5,
/// T-01-07 — the compile-time half of that guarantee is pinned by plan 01-02 Task 1's
/// acceptance criteria). Plan 01-03 adds the fault-injection family
/// (<see cref="FaultingStoreFactory"/>: faulted read throws, absent key returns null,
/// faulted write throws — the STORE PRIMITIVE still throws on failure; the SDK's
/// log-and-continue wrapper is Phase 4/5 policy and not part of the store contract,
/// D-20), durability-await (independent second-instance read, D-06), and parallel
/// cross-read enforcement via
/// <see cref="StoreContractRules.AssertParallelAccessNoCrossReadsAsync"/> — parallel
/// enforcement is uniform across all five store families (serial-only coverage is not
/// acceptable per D-11).
/// </remarks>
public abstract class RecentMessageCacheStoreContractTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Supplies the factory under test; the subclass binds the implementation.</summary>
    protected abstract IRecentMessageCacheStoreFactory CreateFactory();

    [Fact]
    public async Task Put_then_Get_round_trips_typed_record()
    {
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var entry = new RecentMessageEntry("msg-1", "alice@s.whatsapp.net", BaseTime);

        await store.PutAsync(entry, CancellationToken.None);

        var fetched = await store.GetAsync("msg-1", CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.MessageKey.Should().Be("msg-1");
        fetched.ChatJid.Should().Be("alice@s.whatsapp.net");
        fetched.TimestampUtc.Should().Be(BaseTime);
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Get_missing_message_key_returns_null_not_throw()
    {
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        var fetched = await store.GetAsync("absent-key", CancellationToken.None);

        fetched.Should().BeNull(); // missing ≠ failure (D-19)
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Two_accounts_isolated_same_message_key()
    {
        var factory = CreateFactory();
        var storeA = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var storeB = await factory.CreateAsync(AccountId.Create("acct-B"), CancellationToken.None);
        const string key = "shared-key";

        await storeA.PutAsync(new RecentMessageEntry(key, "alice@s.whatsapp.net", BaseTime), CancellationToken.None);
        await storeB.PutAsync(new RecentMessageEntry(key, "bob@s.whatsapp.net", BaseTime.AddMinutes(1)), CancellationToken.None);

        var fetchedA = await storeA.GetAsync(key, CancellationToken.None);
        var fetchedB = await storeB.GetAsync(key, CancellationToken.None);

        // Structural namespacing (D-02 / T-01-05): same key, zero cross-writes.
        fetchedA.Should().NotBeNull();
        fetchedB.Should().NotBeNull();
        fetchedA!.ChatJid.Should().Be("alice@s.whatsapp.net");
        fetchedB!.ChatJid.Should().Be("bob@s.whatsapp.net");
        await storeA.DisposeAsync();
        await storeB.DisposeAsync();
    }

    [Fact]
    public async Task GetRecent_returns_newest_first_limited_to_requested_chat()
    {
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var chatA = "alice@s.whatsapp.net";
        var chatB = "bob@s.whatsapp.net";

        await store.PutAsync(new RecentMessageEntry("a1", chatA, BaseTime), CancellationToken.None);
        await store.PutAsync(new RecentMessageEntry("a2", chatA, BaseTime.AddMinutes(1)), CancellationToken.None);
        await store.PutAsync(new RecentMessageEntry("a3", chatA, BaseTime.AddMinutes(2)), CancellationToken.None);
        await store.PutAsync(new RecentMessageEntry("b1", chatB, BaseTime.AddMinutes(3)), CancellationToken.None);

        var recent = await store.GetRecentAsync(chatA, 2, CancellationToken.None);

        recent.Should().HaveCount(2);
        recent.Select(entry => entry.MessageKey).Should().Equal("a3", "a2"); // newest first, chat A only
        recent.Should().OnlyContain(entry => entry.ChatJid == chatA);
        await store.DisposeAsync();
    }

    [Fact]
    public async Task TrimOldest_removes_oldest_entries_observable_via_GetRecent()
    {
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var chat = "alice@s.whatsapp.net";

        await store.PutAsync(new RecentMessageEntry("m1", chat, BaseTime), CancellationToken.None);
        await store.PutAsync(new RecentMessageEntry("m2", chat, BaseTime.AddMinutes(1)), CancellationToken.None);
        await store.PutAsync(new RecentMessageEntry("m3", chat, BaseTime.AddMinutes(2)), CancellationToken.None);

        var before = await store.GetRecentAsync(chat, 10, CancellationToken.None);
        await store.TrimOldestAsync(1, CancellationToken.None);
        var after = await store.GetRecentAsync(chat, 10, CancellationToken.None);

        before.Should().HaveCount(3);
        after.Select(entry => entry.MessageKey).Should().Equal("m3", "m2"); // oldest (m1) gone
        await store.DisposeAsync();
    }

    [Fact]
    public async Task PurgeExpired_removes_only_entries_strictly_before_cutoff()
    {
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var chat = "alice@s.whatsapp.net";
        var older = BaseTime;
        var atCutoff = BaseTime.AddMinutes(1);
        var newer = BaseTime.AddMinutes(2);

        await store.PutAsync(new RecentMessageEntry("old", chat, older), CancellationToken.None);
        await store.PutAsync(new RecentMessageEntry("at", chat, atCutoff), CancellationToken.None);
        await store.PutAsync(new RecentMessageEntry("new", chat, newer), CancellationToken.None);

        await store.PurgeExpiredAsync(atCutoff, CancellationToken.None);

        (await store.GetAsync("old", CancellationToken.None)).Should().BeNull();
        (await store.GetAsync("at", CancellationToken.None)).Should().NotBeNull(); // at-cutoff survives
        (await store.GetAsync("new", CancellationToken.None)).Should().NotBeNull();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Double_put_same_key_last_write_wins()
    {
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        const string key = "msg-1";

        await store.PutAsync(new RecentMessageEntry(key, "first@s.whatsapp.net", BaseTime), CancellationToken.None);
        await store.PutAsync(new RecentMessageEntry(key, "second@s.whatsapp.net", BaseTime.AddMinutes(1)), CancellationToken.None);

        var fetched = await store.GetAsync(key, CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.ChatJid.Should().Be("second@s.whatsapp.net");
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Double_remove_same_key_second_returns_false()
    {
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        await store.PutAsync(new RecentMessageEntry("msg-1", "alice@s.whatsapp.net", BaseTime), CancellationToken.None);

        var first = await store.RemoveAsync("msg-1", CancellationToken.None);
        var second = await store.RemoveAsync("msg-1", CancellationToken.None);

        first.Should().BeTrue();
        second.Should().BeFalse(); // idempotency edge
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Double_trim_oldest_second_removes_only_what_remains()
    {
        var factory = CreateFactory();
        var store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var chat = "alice@s.whatsapp.net";

        await store.PutAsync(new RecentMessageEntry("m1", chat, BaseTime), CancellationToken.None);
        await store.PutAsync(new RecentMessageEntry("m2", chat, BaseTime.AddMinutes(1)), CancellationToken.None);

        await store.TrimOldestAsync(1, CancellationToken.None);
        await store.TrimOldestAsync(1, CancellationToken.None);
        var emptyCheck = await store.GetRecentAsync(chat, 10, CancellationToken.None);

        // Second trim removed only what remained (no negative over-trim)…
        emptyCheck.Should().BeEmpty();

        // …and a third trim over an empty cache is a no-op, not an underflow.
        await store.TrimOldestAsync(1, CancellationToken.None);
        (await store.GetRecentAsync(chat, 10, CancellationToken.None)).Should().BeEmpty();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task FailReads_Get_throws_SdkStoreException_never_returns_null()
    {
        var inner = CreateFactory();
        IRecentMessageCacheStoreFactory faulting = new FaultingStoreFactory(recentMessageCache: inner) { Mode = FaultMode.FailReads };
        var store = await faulting.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        // A failing read THROWS — never mapped to null (D-19; T-01-09). Even for a
        // lossy cache, the fault is observably distinct from an absent entry.
        var act = async () => await store.GetAsync("nobody@s.whatsapp.net", CancellationToken.None);
        await act.Should().ThrowAsync<SdkStoreException>();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Faulting_factory_Mode_None_absent_key_returns_null()
    {
        var inner = CreateFactory();
        IRecentMessageCacheStoreFactory faulting = new FaultingStoreFactory(recentMessageCache: inner) { Mode = FaultMode.None };
        var store = await faulting.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        // Distinct observable path from the fault above: a genuinely absent key returns
        // null — failure and missing stay mechanically distinguishable (D-19).
        (await store.GetAsync("absent-key", CancellationToken.None)).Should().BeNull();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task FailWrites_Put_throws_Mode_None_Put_succeeds()
    {
        var inner = CreateFactory();
        var decorator = new FaultingStoreFactory(recentMessageCache: inner) { Mode = FaultMode.FailWrites };
        IRecentMessageCacheStoreFactory faulting = decorator;
        var store = await faulting.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        // The STORE PRIMITIVE throws on an injected write failure (D-20 nuance): the
        // cache's log-and-continue swallow policy is SDK-side (Phases 4/5) and not part
        // of the store contract the kit enforces.
        var act = async () => await store.PutAsync(
            new RecentMessageEntry("faulted-key", "alice@s.whatsapp.net", BaseTime), CancellationToken.None);
        await act.Should().ThrowAsync<SdkStoreException>();

        // Same primitive with the fault cleared succeeds — injection is per-call, and the
        // failed attempt left nothing behind.
        decorator.Mode = FaultMode.None;
        await store.PutAsync(new RecentMessageEntry("faulted-key", "alice@s.whatsapp.net", BaseTime), CancellationToken.None);
        (await store.GetAsync("faulted-key", CancellationToken.None)).Should().NotBeNull();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Write_observed_by_independent_second_store_instance()
    {
        var factory = CreateFactory();
        var account = AccountId.Create("acct-A");

        await StoreContractRules.AssertWriteVisibleToIndependentReadAsync<IRecentMessageCacheStore>(
            createStore: () => factory.CreateAsync(account, CancellationToken.None),
            writeAsync: writer => writer.PutAsync(
                new RecentMessageEntry("durable-key", "alice@s.whatsapp.net", BaseTime), CancellationToken.None),
            assertVisibleAsync: async reader =>
            {
                var fetched = await reader.GetAsync("durable-key", CancellationToken.None);
                fetched.Should().NotBeNull("an awaited Put must be observable by an independent subsequent read (D-06 store-side)");
                fetched!.MessageKey.Should().Be("durable-key");
            });
    }

    [Fact]
    public async Task Parallel_interleaved_writes_and_reads_produce_no_cross_reads()
    {
        var factory = CreateFactory();
        var accountA = AccountId.Create("acct-A");
        var accountB = AccountId.Create("acct-B");

        // Genuine parallelism (Task.WhenAll inside the helper, no collection disabling):
        // B must never observe any message key A wrote (D-11 concurrency family, T-01-10).
        await StoreContractRules.AssertParallelAccessNoCrossReadsAsync<IRecentMessageCacheStore>(
            createStoreAsync: accountId => factory.CreateAsync(accountId, CancellationToken.None),
            accountA: accountA,
            accountB: accountB,
            writeOnAAsync: (writer, i) => writer.PutAsync(
                new RecentMessageEntry($"parallel-{i}", "parallel@s.whatsapp.net", BaseTime.AddMilliseconds(i)),
                CancellationToken.None),
            readLeakOnBAsync: async (reader, i) =>
                await reader.GetAsync($"parallel-{i}", CancellationToken.None) is not null,
            iterations: 100);
    }
}
