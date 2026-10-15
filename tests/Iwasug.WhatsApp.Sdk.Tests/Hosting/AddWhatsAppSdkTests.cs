using System.Collections.Generic;
using System.Threading.Tasks;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Hosting;
using Iwasug.WhatsApp.Sdk.Stores;
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

    [Fact]
    public void Configuration_Overload_Does_Not_Clobber_Host_Configuration()
    {
        // 07 WR-06: a host IConfiguration registration must survive AddWhatsAppSdk, and
        // the SDK must bind the configuration instance the caller passed — not whatever
        // IConfiguration happens to be resolvable from the container.
        var hostConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Host:Key"] = "host-value" })
            .Build();
        var sdkConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["WhatsApp:StartupJitterMs"] = "7" })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(hostConfig);
        services.AddWhatsAppSdk(sdkConfig, "WhatsApp");

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IConfiguration>().Should().BeSameAs(hostConfig, "the host registration must win untouched");
        provider.GetRequiredService<IOptions<WhatsAppSdkOptions>>().Value.StartupJitterMs
            .Should().Be(7, "options bind the configuration instance the caller passed");
    }

    [Fact]
    public async Task Container_Registered_StoreRegistry_Is_Used_And_Not_Disposed()
    {
        // 07 WR-06: a container-registered StoreRegistry is the host persistence seam
        // (D-03) — the factory must use it as-is, and the runtime must not dispose it.
        var hostRegistry = new StoreRegistry(new StoreRegistryOptions());
        AccountId accountId = AccountId.Create("wr06-host");

        var services = new ServiceCollection();
        services.AddSingleton(hostRegistry);
        services.AddWhatsAppSdk(o => o.Accounts.Add(new AccountDescriptor { AccountId = accountId.Value }));

        using var provider = services.BuildServiceProvider();
        IAccountRuntimeFactory factory = provider.GetRequiredService<IAccountRuntimeFactory>();

        AccountRuntime runtime = await factory.CreateAsync(accountId, TestContext.Current.CancellationToken);
        runtime.Connection.StoreRegistry.Should().BeSameAs(hostRegistry);
        runtime.OwnedStoreRegistry.Should().BeNull("a host-supplied registry is host-owned");
        await runtime.DisposeAsync();

        // The host registry survives the runtime — it was never the runtime's to dispose.
        StoreSession probe = await hostRegistry.CreateSessionAsync(
            AccountId.Create("wr06-probe"), TestContext.Current.CancellationToken);
        probe.Should().NotBeNull();
    }

    [Fact]
    public void Delegate_Overload_Validates_Options_On_Start()
    {
        // 07 IN-09: the delegate overload validates on start like the configuration
        // overload — invalid options fail when the startup validator runs, instead of
        // lazily at the first IOptions<WhatsAppSdkOptions>.Value resolution. The
        // validation delegate's own exception surfaces unwrapped (ValidateOptions<T>
        // no longer wraps it), so assert on the operator-facing message, not the type.
        var services = new ServiceCollection();
        services.AddWhatsAppSdk(o => o.ShutdownDrainTimeoutMs = 0);

        using var provider = services.BuildServiceProvider();
        IStartupValidator validator = provider.GetRequiredService<IStartupValidator>();
        Action act = () => validator.Validate();
        act.Should().Throw<Exception>().WithMessage("*ShutdownDrainTimeoutMs*");
    }

    [Fact]
    public async Task Per_Account_Registry_Is_Owned_And_Disposed_With_The_Runtime()
    {
        // 07 WR-06: with no host registry registered, the factory creates a per-account
        // registry and the runtime disposes it — StoreRegistry.DisposeAsync must run
        // (it drains the session and disposes the fallback factory).
        var services = new ServiceCollection();
        services.AddWhatsAppSdk();
        using var provider = services.BuildServiceProvider();
        IAccountRuntimeFactory factory = provider.GetRequiredService<IAccountRuntimeFactory>();

        AccountId accountId = AccountId.Create($"wr06-{Guid.NewGuid():N}");
        AccountRuntime runtime = await factory.CreateAsync(accountId, TestContext.Current.CancellationToken);
        runtime.OwnedStoreRegistry.Should().NotBeNull();
        runtime.Connection.StoreRegistry.Should().BeSameAs(runtime.OwnedStoreRegistry);
        StoreRegistry owned = runtime.OwnedStoreRegistry!;

        await runtime.DisposeAsync();

        var act = async () => await owned.CreateSessionAsync(
            AccountId.Create("wr06-after"), TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }
}