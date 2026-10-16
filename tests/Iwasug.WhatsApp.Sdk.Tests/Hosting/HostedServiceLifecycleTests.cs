using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Hosting;

public class HostedServiceLifecycleTests
{
    [Fact]
    public async Task Start_Returns_Before_Accounts_Register_And_Stop_Drains()
    {
        var registry = new Iwasug.WhatsApp.Sdk.Stores.StoreRegistry(new Iwasug.WhatsApp.Sdk.Stores.StoreRegistryOptions());
        var factory = new DefaultAccountRuntimeFactory(registry);
        var manager = new Iwasug.WhatsApp.Sdk.Accounts.SessionManager(factory);
        var opts = new WhatsAppSdkOptions
        {
            Accounts = { new AccountDescriptor { AccountId = $"ls-{Guid.NewGuid():N}" } },
            StartupJitterMs = 0,
            ShutdownDrainTimeoutMs = 1000,
            AutoStartAccounts = true,
            AutoConnect = false, // unit test: never open real sockets
        };

        var service = new WhatsAppHostedService(Microsoft.Extensions.Options.Options.Create(opts), manager);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await service.StartAsync(cts.Token);

        manager.GetStatuses().Should().NotBeEmpty();

        await service.StopAsync(cts.Token);
        manager.GetStatuses().Should().BeEmpty();
    }

    [Fact]
    public async Task Bad_Account_Id_Does_Not_Kill_Host()
    {
        var registry = new Iwasug.WhatsApp.Sdk.Stores.StoreRegistry(new Iwasug.WhatsApp.Sdk.Stores.StoreRegistryOptions());
        var factory = new DefaultAccountRuntimeFactory(registry);
        var manager = new Iwasug.WhatsApp.Sdk.Accounts.SessionManager(factory);
        var opts = new WhatsAppSdkOptions
        {
            Accounts = { new AccountDescriptor { AccountId = "bad\tid" } },
            StartupJitterMs = 0,
            ShutdownDrainTimeoutMs = 500,
            AutoStartAccounts = true,
            AutoConnect = false, // unit test: never open real sockets
        };

        var service = new WhatsAppHostedService(Microsoft.Extensions.Options.Options.Create(opts), manager);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var act = async () => await service.StartAsync(cts.Token);
        await act.Should().NotThrowAsync();
        manager.GetStatuses().Should().BeEmpty();
        await service.StopAsync(cts.Token);
    }
}