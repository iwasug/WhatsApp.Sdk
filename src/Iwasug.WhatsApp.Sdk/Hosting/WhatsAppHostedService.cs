namespace Iwasug.WhatsApp.Sdk.Hosting;

using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Connection;
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
                    AccountId accountId = AccountId.Create(descriptor.AccountId);
                    await _manager.AddAccountAsync(descriptor, cancellationToken).ConfigureAwait(false);

                    if (options.AutoConnect && _manager.TryGetConnection(accountId, out WaConnection? connection))
                    {
                        StartConnection(accountId, connection);
                    }
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

    /// <summary>
    /// Starts one account's connection lifecycle without blocking host start (07 CR-01):
    /// <c>ConnectAsync</c> completes at first open, so the wait stays off the host's
    /// startup path; a fault is observed and logged here instead of escaping into the
    /// host's start loop (where per-account failures are deliberately swallowed).
    /// </summary>
    private void StartConnection(AccountId accountId, WaConnection connection)
    {
        _ = connection.ConnectAsync(CancellationToken.None).ContinueWith(
            t =>
            {
                Exception error = t.Exception?.GetBaseException()
                    ?? new InvalidOperationException("Connection start failed without an exception.");
                if (error is ObjectDisposedException or OperationCanceledException)
                {
                    _logger.LogDebug("Account connect cancelled {AccountId}.", accountId.Value);
                    return;
                }

                string ruleName = error is SdkException ? error.GetType().Name : "ConnectFailed";
                _logger.LogWarning(t.Exception, "Account connect failed {AccountId} {RuleName}.", accountId.Value, ruleName);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // 07 WR-07: one bound only. The old linked drain CTS was dead code — its token
        // was never observed — and the timeout was applied twice (CancelAfter plus a
        // Task.Delay); validated options guarantee a positive timeout here.
        TimeSpan drainTimeout = TimeSpan.FromMilliseconds(_options.Value.ShutdownDrainTimeoutMs);
        Task disposeTask = _manager.DisposeAsync().AsTask();

        try
        {
            await disposeTask.WaitAsync(drainTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Account drain timed out {RuleName}.", "DrainTimeout");
            ObserveAbandonedDrain(disposeTask);
        }
        catch (OperationCanceledException)
        {
            // Host stop aborted the wait; the drain keeps unwinding — observe it so a
            // late fault is logged instead of going unobserved.
            ObserveAbandonedDrain(disposeTask);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Account drain faulted {RuleName}.", ex.GetType().Name);
        }
    }

    /// <summary>
    /// 07 WR-07: after a timeout or host cancellation, <c>StopAsync</c> returns while the
    /// manager is still draining — observe its eventual completion so a late fault is
    /// logged rather than abandoned unobserved.
    /// </summary>
    private void ObserveAbandonedDrain(Task disposeTask)
    {
        _ = disposeTask.ContinueWith(
            t => _logger.LogWarning(t.Exception, "Abandoned account drain faulted {RuleName}.", "DrainFault"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}