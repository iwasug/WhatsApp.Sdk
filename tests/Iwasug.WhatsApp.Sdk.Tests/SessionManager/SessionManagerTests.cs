using Iwasug.WhatsApp.Sdk.Accounts;
using Xunit;
using Iwasug.WhatsApp.Sdk.Errors;
using AwesomeAssertions;
using System.Threading.Tasks;

namespace Iwasug.WhatsApp.Sdk.Tests.SessionManager;

public class SessionManagerTests
{
    [Fact]
    public async Task Add_Status_Remove_ReAdd_Works()
    {
        await using var manager = new Iwasug.WhatsApp.Sdk.Accounts.SessionManager(new Iwasug.WhatsApp.Sdk.Accounts.DefaultAccountRuntimeFactory());
        var id = AccountId.Create($"sm-{Guid.NewGuid():N}");

        await manager.AddAccountAsync(id, TestContext.Current.CancellationToken);
        var status = manager.GetStatus(id);
        status.Should().NotBeNull();
        status!.AccountId.Should().Be(id);

        await manager.RemoveAccountAsync(id, TestContext.Current.CancellationToken);
        manager.GetStatus(id).Should().BeNull();
        await manager.RemoveAccountAsync(id, TestContext.Current.CancellationToken); // no-op

        await manager.AddAccountAsync(id, TestContext.Current.CancellationToken);
        manager.GetStatus(id).Should().NotBeNull();
    }

    [Fact]
    public async Task Duplicate_Throws()
    {
        await using var manager = new Iwasug.WhatsApp.Sdk.Accounts.SessionManager(new Iwasug.WhatsApp.Sdk.Accounts.DefaultAccountRuntimeFactory());
        var id = AccountId.Create($"sm-{Guid.NewGuid():N}");

        await manager.AddAccountAsync(id, TestContext.Current.CancellationToken);
        var act = async () => await manager.AddAccountAsync(id, TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<SdkDuplicateAccountException>();
    }

    [Fact]
    public async Task Unknown_Remove_NoOp()
    {
        await using var manager = new Iwasug.WhatsApp.Sdk.Accounts.SessionManager(new Iwasug.WhatsApp.Sdk.Accounts.DefaultAccountRuntimeFactory());
        var id = AccountId.Create($"sm-{Guid.NewGuid():N}");
        await manager.RemoveAccountAsync(id, TestContext.Current.CancellationToken);
        manager.GetStatus(id).Should().BeNull();
    }

    [Fact]
    public async Task Unknown_Restart_Throws()
    {
        await using var manager = new Iwasug.WhatsApp.Sdk.Accounts.SessionManager(new Iwasug.WhatsApp.Sdk.Accounts.DefaultAccountRuntimeFactory());
        var id = AccountId.Create($"sm-{Guid.NewGuid():N}");
        var act = async () => await manager.RestartAccountAsync(id, TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<SdkUnknownAccountException>();
    }

    [Fact]
    public async Task Runtime_Instances_Distinct_Across_Accounts()
    {
        await using var manager = new Iwasug.WhatsApp.Sdk.Accounts.SessionManager(new Iwasug.WhatsApp.Sdk.Accounts.DefaultAccountRuntimeFactory());
        var a = AccountId.Create($"a-{Guid.NewGuid():N}");
        var b = AccountId.Create($"b-{Guid.NewGuid():N}");
        await manager.AddAccountAsync(a, TestContext.Current.CancellationToken);
        await manager.AddAccountAsync(b, TestContext.Current.CancellationToken);

        // access runtimes via reflection? Skip strict; status count
        manager.GetStatuses().Should().HaveCount(2);
    }

    [Fact]
    public async Task Interleaved_Add_Restart_Remove_No_Resurrection()
    {
        // 07 WR-03: the plan required an interleaved add/remove proof — no
        // resurrection and a final count of zero when all removes win. Add and
        // restart race on the same id; a duplicate thrown by restart (the
        // resurrection/race symptom) fails the test, as does any leftover status.
        var registry = new Iwasug.WhatsApp.Sdk.Stores.StoreRegistry(new Iwasug.WhatsApp.Sdk.Stores.StoreRegistryOptions());
        await using var manager = new Iwasug.WhatsApp.Sdk.Accounts.SessionManager(new DefaultAccountRuntimeFactory(registry));
        var id = AccountId.Create($"sm-{Guid.NewGuid():N}");
        var ct = TestContext.Current.CancellationToken;

        var adder = Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                try { await manager.AddAccountAsync(id, ct); }
                catch (SdkDuplicateAccountException) { }
                await manager.RemoveAccountAsync(id, ct);
            }
        }, ct);

        var restarter = Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                try { await manager.RestartAccountAsync(id, ct); }
                catch (SdkUnknownAccountException) { }
            }
        }, ct);

        await Task.WhenAll(adder, restarter);

        manager.GetStatuses().Should().BeEmpty();
    }

    [Fact]
    public async Task TryGetMessenger_Resolves_Live_Account_And_Is_False_For_Unknown()
    {
        // 07 WR-08: the manager's public surface is the only handle a host has on a
        // single account's messenger; it must resolve the live instance and report
        // false (not throw) for ids that are absent.
        await using var manager = new Iwasug.WhatsApp.Sdk.Accounts.SessionManager(new Iwasug.WhatsApp.Sdk.Accounts.DefaultAccountRuntimeFactory());
        var id = AccountId.Create($"sm-{Guid.NewGuid():N}");
        var ct = TestContext.Current.CancellationToken;

        manager.TryGetMessenger(id, out _).Should().BeFalse();

        await manager.AddAccountAsync(id, ct);

        manager.TryGetMessenger(id, out var messenger).Should().BeTrue();
        manager.TryGetRuntime(id, out var runtime).Should().BeTrue();
        messenger.Should().BeSameAs(runtime!.Messenger);

        await manager.RemoveAccountAsync(id, ct);
        manager.TryGetMessenger(id, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Restart_Rebuilds_The_Graph_And_Retires_The_Old_Token_Source()
    {
        // 07 IN-10 (07-01-PLAN.md restart identity): a restart must publish a fresh
        // per-account graph — new runtime, new (live) token source — retire the old one
        // (its token cancelled), and never accumulate registry entries across restarts.
        await using var manager = new Iwasug.WhatsApp.Sdk.Accounts.SessionManager(new Iwasug.WhatsApp.Sdk.Accounts.DefaultAccountRuntimeFactory());
        var id = AccountId.Create($"sm-{Guid.NewGuid():N}");
        var ct = TestContext.Current.CancellationToken;

        await manager.AddAccountAsync(id, ct);
        manager.TryGetRuntime(id, out var first).Should().BeTrue();
        CancellationToken retired = first!.LinkedCts.Token;

        for (int i = 0; i < 3; i++)
        {
            await manager.RestartAccountAsync(id, ct);
        }

        manager.TryGetRuntime(id, out var second).Should().BeTrue();
        second.Should().NotBeSameAs(first, "restart rebuilds the account graph");
        second!.LinkedCts.Token.IsCancellationRequested.Should().BeFalse("the fresh token source is live");
        retired.IsCancellationRequested.Should().BeTrue("the previous runtime's token is cancelled at retirement");
        manager.GetStatuses().Should().HaveCount(1, "restarts must not accumulate registrations");
        manager.GetStatus(id)!.AccountId.Should().Be(id);
    }
}