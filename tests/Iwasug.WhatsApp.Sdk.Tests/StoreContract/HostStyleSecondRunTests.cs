using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Testing;
using Iwasug.WhatsApp.Sdk.Testing.FaultInjection;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.StoreContract;

/// <summary>
/// The formal proof of Phase 1 success criterion 3, executed as a distinct final run:
/// a fresh host-style consumer adopts the kit with ONE deliberately-different factory
/// (<see cref="ScratchStoreFactory"/>) and drives all five store capabilities through the
/// kit's public API in one flow — zero SDK changes, zero reference-implementation types.
/// </summary>
/// <remarks>
/// This is intentionally redundant with the per-class bindings (which already run every
/// fact via inheritance): the redundancy IS the point. It exercises the kit exactly as a
/// host consumption flow would — one factory, all five capabilities, one run — proving no
/// hidden reference-impl dependency anywhere in the kit's path (T-01-16). This file must
/// not reference the reference store implementation by name or by code; that property is
/// an acceptance criterion of plan 01-05. Facts carry a <c>Host_style_</c> prefix so the
/// run log visually separates this formal proof from per-class coverage.
/// </remarks>
public sealed class HostStyleSecondRunTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Host_style_session_Put_Get_round_trip_and_missing_returns_null()
    {
        var factory = new ScratchStoreFactory();
        ISignalSessionStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var record = new SignalSessionRecord("alice@s.whatsapp.net", new byte[] { 1, 2, 3, 4 });

        await store.PutAsync(record.Address, record, CancellationToken.None);

        // Independent subsequent read observes the value; an absent address is null, not a throw (D-06/D-19).
        SignalSessionRecord? fetched = await store.GetAsync(record.Address, CancellationToken.None);
        SignalSessionRecord? missing = await store.GetAsync("nobody@s.whatsapp.net", CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.Address.Should().Be(record.Address);
        fetched.SessionState.Should().Equal(record.SessionState);
        missing.Should().BeNull();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Host_style_identity_round_trip_and_two_account_isolation()
    {
        IIdentityStoreFactory factory = new ScratchStoreFactory();
        IIdentityStore storeA = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        IIdentityStore storeB = await factory.CreateAsync(AccountId.Create("acct-B"), CancellationToken.None);
        const string address = "shared@s.whatsapp.net";

        await storeA.PutAsync(address, new SignalIdentityRecord("key-A"), CancellationToken.None);
        await storeB.PutAsync(address, new SignalIdentityRecord("key-B"), CancellationToken.None);

        SignalIdentityRecord? fetchedA = await storeA.GetAsync(address, CancellationToken.None);
        SignalIdentityRecord? fetchedB = await storeB.GetAsync(address, CancellationToken.None);

        // Structural namespacing (D-02): same address, two accounts, zero cross-reads.
        fetchedA.Should().NotBeNull();
        fetchedB.Should().NotBeNull();
        fetchedA!.IdentityKeyPublic.Should().Be("key-A");
        fetchedB!.IdentityKeyPublic.Should().Be("key-B");
        await storeA.DisposeAsync();
        await storeB.DisposeAsync();
    }

    [Fact]
    public async Task Host_style_prekey_put_remove_then_get_returns_null()
    {
        // Non-session seams bind through the interface: ScratchStoreFactory's (and the
        // decorator's) non-session CreateAsync overloads are explicit implementations.
        IPreKeyStoreFactory factory = new ScratchStoreFactory();
        IPreKeyStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        await store.PutPreKeyAsync(42, new PreKeyRecordEntry(42), CancellationToken.None);
        (await store.GetPreKeyAsync(42, CancellationToken.None)).Should().NotBeNull();

        bool removed = await store.RemovePreKeyAsync(42, CancellationToken.None);
        PreKeyRecordEntry? fetched = await store.GetPreKeyAsync(42, CancellationToken.None);

        removed.Should().BeTrue();
        fetched.Should().BeNull(); // consuming a one-time pre-key leaves nothing behind (D-07)
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Host_style_cache_put_GetRecent_TrimOldest_PurgeExpired()
    {
        IRecentMessageCacheStoreFactory factory = new ScratchStoreFactory();
        IRecentMessageCacheStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        string chat = "alice@s.whatsapp.net";

        await store.PutAsync(new RecentMessageEntry("h1", chat, BaseTime), CancellationToken.None);
        await store.PutAsync(new RecentMessageEntry("h2", chat, BaseTime.AddMinutes(1)), CancellationToken.None);
        await store.PutAsync(new RecentMessageEntry("h3", chat, BaseTime.AddMinutes(2)), CancellationToken.None);

        IReadOnlyList<RecentMessageEntry> recent = await store.GetRecentAsync(chat, 2, CancellationToken.None);
        recent.Select(entry => entry.MessageKey).Should().Equal("h3", "h2"); // newest first, this chat only

        await store.TrimOldestAsync(1, CancellationToken.None);
        (await store.GetAsync("h1", CancellationToken.None)).Should().BeNull(); // oldest trimmed

        await store.PurgeExpiredAsync(BaseTime.AddMinutes(2), CancellationToken.None);
        (await store.GetAsync("h2", CancellationToken.None)).Should().BeNull(); // strictly before cutoff
        (await store.GetAsync("h3", CancellationToken.None)).Should().NotBeNull(); // at-cutoff survives
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Host_style_safety_put_overwrites_last_write_wins()
    {
        ISafetyStateStoreFactory factory = new ScratchStoreFactory();
        ISafetyStateStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        await store.PutAsync(new SafetyStateRecord(BaseTime, WarmupStage: 1), CancellationToken.None);
        await store.PutAsync(new SafetyStateRecord(BaseTime, WarmupStage: 2), CancellationToken.None);

        SafetyStateRecord? fetched = await store.GetAsync(CancellationToken.None);

        // Safety state is a single mutable row per account: last write wins.
        fetched.Should().NotBeNull();
        fetched!.CreatedAt.Should().Be(BaseTime);
        fetched.WarmupStage.Should().Be(2);
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Host_style_faulting_factory_FailReads_throws_and_None_returns_null()
    {
        var scratch = new ScratchStoreFactory();
        var faulting = new FaultingStoreFactory(signalSession: scratch) { Mode = FaultMode.FailReads };
        ISignalSessionStore store = await faulting.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        // A failing read THROWS — never surfaced as null (D-19/T-01-09), here through the
        // host-style flow's own FaultingStoreFactory wrap of the scratch implementation.
        Func<Task<SignalSessionRecord>> act = async () => await store.GetAsync("nobody@s.whatsapp.net", CancellationToken.None);
        await act.Should().ThrowAsync<SdkStoreException>();
        await store.DisposeAsync();

        // Distinct observable path: with the fault cleared, a genuinely absent key returns
        // null — failure and missing stay mechanically distinguishable in this run too.
        var passThrough = new FaultingStoreFactory(signalSession: scratch) { Mode = FaultMode.None };
        ISignalSessionStore store2 = await passThrough.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        (await store2.GetAsync("nobody@s.whatsapp.net", CancellationToken.None)).Should().BeNull();
        await store2.DisposeAsync();
    }

    [Fact]
    public void Host_style_completeness_check_passes_in_scratch_context()
    {
        // The kit's completeness backstop, executed in the host-style second-run context:
        // every abstract contract class has a concrete binding in the loaded assemblies,
        // and this run reaches it with ScratchStoreFactory as the implementation in play.
        new ScratchContextComplianceCheck().Every_contract_class_has_a_concrete_subclass();
    }

    /// <summary>
    /// Empty concrete subclass of <see cref="ComplianceTestBase"/> so the inherited
    /// completeness <c>[Fact]</c> is runnable from this host-style flow — exactly the
    /// gesture a host makes when adopting the kit.
    /// </summary>
    private sealed class ScratchContextComplianceCheck : ComplianceTestBase
    {
    }
}
