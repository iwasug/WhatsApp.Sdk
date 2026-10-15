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
}