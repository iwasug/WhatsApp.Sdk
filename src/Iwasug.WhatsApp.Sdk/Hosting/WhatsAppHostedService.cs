namespace Iwasug.WhatsApp.Sdk.Hosting;

using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Errors;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Hosted service that starts every configured account with the host and drains
/// them on stop.
/// </summary>
internal sealed class WhatsAppHostedService : BackgroundService
{
    private readonly IOptions<WhatsAppSdkOptions> _options;
    private readonly IWhatsAppSessionManager _manager;
    private readonly ILogger<WhatsAppHostedService> _logger;

    public WhatsAppHostedService(
        IOptions<WhatsAppSdkOptions> options,
        IWhatsAppSessionManager manager,
        ILogger<WhatsAppHostedService>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<WhatsAppHostedService>.Instance;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        WhatsAppSdkOptions options = _options.Value;

        if (options.AutoStartAccounts)
        {
            int index = 0;
            foreach (AccountDescriptor descriptor in options.Accounts)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (index > 0 && options.StartupJitterMs > 0)
                {
                    await Task.Delay(options.StartupJitterMs, cancellationToken).ConfigureAwait(false);
                }

                index++;

                try
                {
                    AccountId.Create(descriptor.AccountId);
                    await _manager.AddAccountAsync(descriptor, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    string ruleName = ex is Iwasug.WhatsApp.Sdk.Errors.SdkException ? ex.GetType().Name : "StartupFailed";
                    _logger.LogWarning(ex, "Account start failed {AccountId} {RuleName}.", descriptor.AccountId, ruleName);
                }
            }
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on host stop
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        using CancellationTokenSource drainCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            drainCts.CancelAfter(_options.Value.ShutdownDrainTimeoutMs);
        }
        catch (ArgumentOutOfRangeException)
        {
            drainCts.CancelAfter(TimeSpan.FromMilliseconds(_options.Value.ShutdownDrainTimeoutMs));
        }

        Task disposeTask = _manager.DisposeAsync().AsTask();
        Task completed = await Task.WhenAny(disposeTask, Task.Delay(_options.Value.ShutdownDrainTimeoutMs, cancellationToken)).ConfigureAwait(false);

        if (completed != disposeTask)
        {
            _logger.LogWarning("Account drain timed out {RuleName}.", "DrainTimeout");
        }
        else
        {
            try
            {
                await disposeTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Account drain faulted {RuleName}.", ex.GetType().Name);
            }
        }
    }
}