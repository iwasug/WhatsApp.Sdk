#pragma warning disable RS0026
namespace Iwasug.WhatsApp.Sdk.Hosting;

using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Stores;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Container-neutral DI registration for the WhatsApp SDK.
/// </summary>
public static class WhatsAppSdkServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SDK against a delegate-based configuration.
    /// </summary>
    public static IServiceCollection AddWhatsAppSdk(
        this IServiceCollection services,
        Action<WhatsAppSdkOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<WhatsAppSdkOptions>()
            .Configure(o => configure?.Invoke(o))
            .Validate(o => { WhatsAppSdkOptions.Validate(o); return true; });

        RegisterCore(services);
        return services;
    }

    /// <summary>
    /// Registers the SDK by binding options from <paramref name="configuration"/>.
    /// </summary>
    public static IServiceCollection AddWhatsAppSdk(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "WhatsApp")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

#pragma warning disable IL2026, IL3050
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(configuration);
        services.AddOptions<WhatsAppSdkOptions>()
            .BindConfiguration(sectionName)
            .Validate(o => { WhatsAppSdkOptions.Validate(o); return true; })
            .ValidateOnStart();
#pragma warning restore IL2026, IL3050

        RegisterCore(services);
        return services;
    }

    private static void RegisterCore(IServiceCollection services)
    {
        services.AddSingleton<IWhatsAppSessionManager>(sp =>
        {
            IAccountRuntimeFactory factory = sp.GetRequiredService<IAccountRuntimeFactory>();
            ILogger<SessionManager>? logger = sp.GetService<ILogger<SessionManager>>();
            return new SessionManager(factory, logger);
        });

        services.AddSingleton<IAccountRuntimeFactory>(sp =>
        {
            IOptions<WhatsAppSdkOptions> opts = sp.GetRequiredService<IOptions<WhatsAppSdkOptions>>();
            WhatsAppSdkOptions value = opts.Value;
            return new DefaultAccountRuntimeFactory(
                registry: null,
                loggerFactory: sp.GetService<ILoggerFactory>(),
                timeProvider: sp.GetService<TimeProvider>() ?? TimeProvider.System,
                channelCapacity: value.OutboundQueueCapacity,
                eventChannelCapacity: value.EventChannelCapacity,
                defaultConnection: value.DefaultConnection,
                defaultSafety: value.DefaultSafety,
                storeOptions: value.StoreFactories);
        });

        services.AddHostedService<WhatsAppHostedService>();
    }
}
#pragma warning restore RS0026
