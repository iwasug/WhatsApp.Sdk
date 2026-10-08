using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Stores;
using NSubstitute;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Stores;

/// <summary>
/// Registry behavior proofs (D-03/D-23/D-24/D-26): all-fallback resolution with per-account
/// isolation, host-first precedence, subset mixing, once-per-account caching, the
/// uniqueness helper for the future account-adding API, and the disposal cascade —
/// including host-factory-owned instances and aggregate dispose failures (T-01-13).
/// </summary>
public sealed class StoreRegistryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task All_fallback_resolution_roundtrips_writes_and_reads()
    {
        var registry = new StoreRegistry(new StoreRegistryOptions());
        var account = AccountId.Create("reg-fallback-A");

        var session = await registry.CreateSessionAsync(account, CancellationToken.None);
        var record = new SignalSessionRecord("peer@s.whatsapp.net", [1, 2, 3]);

        await session.SignalSessions.PutAsync("peer@s.whatsapp.net", record, CancellationToken.None);
        var retrieved = await session.SignalSessions.GetAsync("peer@s.whatsapp.net", CancellationToken.None);

        retrieved.Should().BeEquivalentTo(record);
    }

    [Fact]
    public async Task All_fallback_sessions_are_isolated_per_account()
    {
        var registry = new StoreRegistry(new StoreRegistryOptions());
        var accountA = AccountId.Create("reg-iso-A");
        var accountB = AccountId.Create("reg-iso-B");
        var forA = new SignalSessionRecord("peer@s.whatsapp.net", [1, 1, 1]);
        var forB = new SignalSessionRecord("peer@s.whatsapp.net", [2, 2, 2]);

        var sessionA = await registry.CreateSessionAsync(accountA, CancellationToken.None);
        var sessionB = await registry.CreateSessionAsync(accountB, CancellationToken.None);
        await sessionA.SignalSessions.PutAsync("peer@s.whatsapp.net", forA, CancellationToken.None);
        await sessionB.SignalSessions.PutAsync("peer@s.whatsapp.net", forB, CancellationToken.None);

        (await sessionA.SignalSessions.GetAsync("peer@s.whatsapp.net", CancellationToken.None)).Should().BeEquivalentTo(forA);
        (await sessionB.SignalSessions.GetAsync("peer@s.whatsapp.net", CancellationToken.None)).Should().BeEquivalentTo(forB);
    }

    [Fact]
    public async Task Host_factory_wins_when_registered_and_fallback_fills_the_rest()
    {
        var account = AccountId.Create("reg-host-A");
        var hostStore = Substitute.For<ISignalSessionStore>();
        var hostFactory = Substitute.For<ISignalSessionStoreFactory>();
        hostFactory.CreateAsync(Arg.Any<AccountId>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(hostStore));
        var registry = new StoreRegistry(new StoreRegistryOptions { SignalSessionFactory = hostFactory });

        var session = await registry.CreateSessionAsync(account, CancellationToken.None);

        session.SignalSessions.Should().BeSameAs(hostStore);
        _ = hostFactory.Received(1).CreateAsync(account, Arg.Any<CancellationToken>());

        // The unregistered capabilities still function through the in-memory fallback.
        var state = new SafetyStateRecord(Now, WarmupStage: 0);
        await session.SafetyState.PutAsync(state, CancellationToken.None);
        (await session.SafetyState.GetAsync(CancellationToken.None)).Should().Be(state);
    }

    [Fact]
    public async Task Subset_registration_mixes_host_identity_with_fallback_everything_else()
    {
        var account = AccountId.Create("reg-subset-A");
        var hostIdentity = Substitute.For<IIdentityStore>();
        var hostFactory = Substitute.For<IIdentityStoreFactory>();
        hostFactory.CreateAsync(Arg.Any<AccountId>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(hostIdentity));
        var registry = new StoreRegistry(new StoreRegistryOptions { IdentityFactory = hostFactory });

        var session = await registry.CreateSessionAsync(account, CancellationToken.None);

        session.Identity.Should().BeSameAs(hostIdentity);
        _ = hostFactory.Received(1).CreateAsync(account, Arg.Any<CancellationToken>());

        // Every other capability behaves like a working store (the fallback filled it).
        var sessionRecord = new SignalSessionRecord("peer@s.whatsapp.net", [9]);
        await session.SignalSessions.PutAsync("peer@s.whatsapp.net", sessionRecord, CancellationToken.None);
        (await session.SignalSessions.GetAsync("peer@s.whatsapp.net", CancellationToken.None)).Should().BeEquivalentTo(sessionRecord);
    }

    [Fact]
    public async Task Same_account_id_returns_the_cached_session_and_creates_once()
    {
        var account = AccountId.Create("reg-cache-A");
        var hostFactory = Substitute.For<ISignalSessionStoreFactory>();
        hostFactory.CreateAsync(Arg.Any<AccountId>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(Substitute.For<ISignalSessionStore>()));
        var registry = new StoreRegistry(new StoreRegistryOptions { SignalSessionFactory = hostFactory });

        var first = await registry.CreateSessionAsync(account, CancellationToken.None);
        var second = await registry.CreateSessionAsync(account, CancellationToken.None);

        second.Should().BeSameAs(first);
        _ = hostFactory.Received(1).CreateAsync(account, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureUnique_throws_for_a_known_account_and_passes_for_unknown()
    {
        var registry = new StoreRegistry(new StoreRegistryOptions());
        var known = AccountId.Create("reg-unique-A");
        var unknown = AccountId.Create("reg-unique-B");

        await registry.CreateSessionAsync(known, CancellationToken.None);

        var ensure = () => registry.EnsureUnique(known);
        ensure.Should().Throw<SdkDuplicateAccountException>();

        var ensureUnknown = () => registry.EnsureUnique(unknown);
        ensureUnknown.Should().NotThrow();
    }

    [Fact]
    public async Task Disposing_the_session_disposes_host_factory_instances_too()
    {
        var account = AccountId.Create("reg-cascade-A");
        var hostStore = Substitute.For<ISignalSessionStore>();
        hostStore.DisposeAsync().Returns(ValueTask.CompletedTask);
        var hostFactory = Substitute.For<ISignalSessionStoreFactory>();
        hostFactory.CreateAsync(Arg.Any<AccountId>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(hostStore));
        var registry = new StoreRegistry(new StoreRegistryOptions { SignalSessionFactory = hostFactory });

        var session = await registry.CreateSessionAsync(account, CancellationToken.None);
        await session.DisposeAsync();

        _ = hostStore.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Disposing_the_registry_disposes_its_cached_sessions()
    {
        var account = AccountId.Create("reg-shutdown-A");
        var hostStore = Substitute.For<ISignalSessionStore>();
        hostStore.DisposeAsync().Returns(ValueTask.CompletedTask);
        var hostFactory = Substitute.For<ISignalSessionStoreFactory>();
        hostFactory.CreateAsync(Arg.Any<AccountId>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(hostStore));
        var registry = new StoreRegistry(new StoreRegistryOptions { SignalSessionFactory = hostFactory });

        await registry.CreateSessionAsync(account, CancellationToken.None);
        await registry.DisposeAsync();

        _ = hostStore.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Dispose_failures_aggregate_without_aborting_the_cascade()
    {
        var account = AccountId.Create("reg-aggregate-A");
        var brokenStore = Substitute.For<ISignalSessionStore>();
        brokenStore.When(x => x.DisposeAsync()).Do(_ => throw new InvalidOperationException("dispose boom"));
        var brokenFactory = Substitute.For<ISignalSessionStoreFactory>();
        brokenFactory.CreateAsync(Arg.Any<AccountId>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(brokenStore));

        var identityStore = Substitute.For<IIdentityStore>();
        identityStore.DisposeAsync().Returns(ValueTask.CompletedTask);
        var identityFactory = Substitute.For<IIdentityStoreFactory>();
        identityFactory.CreateAsync(Arg.Any<AccountId>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(identityStore));

        var registry = new StoreRegistry(new StoreRegistryOptions
        {
            SignalSessionFactory = brokenFactory,
            IdentityFactory = identityFactory,
        });

        var session = await registry.CreateSessionAsync(account, CancellationToken.None);

        var dispose = async () => await session.DisposeAsync();
        var thrown = (await dispose.Should().ThrowAsync<AggregateException>()).Which;

        thrown.InnerExceptions.Should().Contain(ex => ex is InvalidOperationException);
        // The store after the failing one still got its dispose attempt (T-01-13).
        _ = identityStore.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task CreateSessionAsync_throws_ObjectDisposedException_after_registry_disposal()
    {
        var registry = new StoreRegistry(new StoreRegistryOptions());
        var account = AccountId.Create("reg-disposed-A");

        await registry.DisposeAsync();

        var act = async () => await registry.CreateSessionAsync(account, CancellationToken.None);
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Factory_failure_wraps_into_SdkStoreException_then_a_retry_succeeds()
    {
        var account = AccountId.Create("reg-retry-A");
        var boom = new InvalidOperationException("factory boom payload");
        var goodStore = Substitute.For<ISignalSessionStore>();
        var hostFactory = Substitute.For<ISignalSessionStoreFactory>();
        hostFactory
            .CreateAsync(Arg.Any<AccountId>(), Arg.Any<CancellationToken>())
            .Returns(
                ValueTask.FromException<ISignalSessionStore>(boom),
                ValueTask.FromResult(goodStore));
        var registry = new StoreRegistry(new StoreRegistryOptions { SignalSessionFactory = hostFactory });

        var first = async () => await registry.CreateSessionAsync(account, CancellationToken.None);
        var thrown = (await first.Should().ThrowAsync<SdkStoreException>()).Which;

        thrown.InnerException.Should().BeSameAs(boom);
        // Payload discipline (T-01-12): the surfaced message never carries the inner
        // exception's content.
        thrown.Message.Should().NotContain("factory boom payload");

        // Fault eviction (D-24): the failed creation did not poison the cache.
        var second = await registry.CreateSessionAsync(account, CancellationToken.None);
        second.SignalSessions.Should().BeSameAs(goodStore);
    }
}
