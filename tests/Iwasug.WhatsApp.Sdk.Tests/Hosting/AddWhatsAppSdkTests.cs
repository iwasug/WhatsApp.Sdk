using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Hosting;

public class AddWhatsAppSdkTests
{
    [Fact]
    public void Delegate_Overload_Registers_Manager_And_Options()
    {
        var services = new ServiceCollection();
        services.AddWhatsAppSdk(o =>
        {
            o.Accounts.Add(new AccountDescriptor { AccountId = "acct-a" });
        });

        using var provider = services.BuildServiceProvider();
        provider.GetService<IWhatsAppSessionManager>().Should().NotBeNull();
        provider.GetService<IAccountRuntimeFactory>().Should().NotBeNull();

        var opts = provider.GetRequiredService<IOptions<WhatsAppSdkOptions>>().Value;
        opts.Accounts.Should().HaveCount(1);
        opts.Accounts[0].AccountId.Should().Be("acct-a");
    }

    [Fact]
    public void Configuration_Overload_Binds_Accounts()
    {
        var dict = new Dictionary<string, string?>
        {
            ["WhatsApp:Accounts:0:AccountId"] = "cfg-1",
            ["WhatsApp:StartupJitterMs"] = "5",
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        var services = new ServiceCollection();
        services.AddWhatsAppSdk(config, "WhatsApp");

        using var provider = services.BuildServiceProvider();
        var opts = provider.GetRequiredService<IOptions<WhatsAppSdkOptions>>().Value;
        opts.Accounts.Should().HaveCount(1);
        opts.Accounts[0].AccountId.Should().Be("cfg-1");
        opts.StartupJitterMs.Should().Be(5);
    }

    [Fact]
    public async Task Per_Account_Safety_Overrides_Resolve_Independently()
    {
        var registry = new Iwasug.WhatsApp.Sdk.Stores.StoreRegistry(new Iwasug.WhatsApp.Sdk.Stores.StoreRegistryOptions());
        var tight = new Iwasug.WhatsApp.Sdk.Safety.SafetyOptions { MaxMessagesPerMinute = 10 };
        var loose = new Iwasug.WhatsApp.Sdk.Safety.SafetyOptions { MaxMessagesPerMinute = 60, AllowUnsafeThroughput = true, DeclaredUnsafeAcknowledgment = "ack" };
        var factory = new DefaultAccountRuntimeFactory(
            registry,
            defaultConnection: new Iwasug.WhatsApp.Sdk.Connection.ConnectionOptions(),
            defaultSafety: new Iwasug.WhatsApp.Sdk.Safety.SafetyOptions { MaxMessagesPerMinute = 20 });

        var d1 = new AccountDescriptor { AccountId = "a1", Safety = tight };
        var d2 = new AccountDescriptor { AccountId = "a2", Safety = loose };

        var r1 = await factory.CreateAsync(d1, TestContext.Current.CancellationToken);
        var r2 = await factory.CreateAsync(d2, TestContext.Current.CancellationToken);

        r1.SafetyOptions!.MaxMessagesPerMinute.Should().Be(10);
        r2.SafetyOptions!.MaxMessagesPerMinute.Should().Be(60);

        await r1.DisposeAsync();
        await r2.DisposeAsync();
    }
}