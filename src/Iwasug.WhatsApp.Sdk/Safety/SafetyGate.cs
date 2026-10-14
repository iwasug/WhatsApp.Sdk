using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Messaging;
using Iwasug.WhatsApp.Sdk.Stores;
using Microsoft.Extensions.Logging;

namespace Iwasug.WhatsApp.Sdk.Safety;

/// <summary>Internal mandatory safety gate.</summary>
internal sealed class SafetyGate : ISafetyGate
{
    private readonly TimeProvider _clock;
    private readonly SafetyOptions _options;
    private readonly ILogger<SafetyGate>? _logger;
    private readonly Func<CancellationToken, ValueTask<StoreSession>> _resolveStores;
    private readonly Func<WhatsAppEvent, CancellationToken, ValueTask> _publish;
    private readonly TokenBucketRateLimiterSet _rateLimits;
    private readonly WarmupLadder _warmup;

    /// <summary>Budget exhausted event.</summary>
    public event EventHandler<BudgetExhaustedEventArgs>? BudgetExhausted;

    /// <summary>Throttle engaged event.</summary>
    public event EventHandler<ThrottleEngagedEventArgs>? ThrottleEngaged;

    /// <summary>Account restricted event.</summary>
    public event EventHandler<AccountRestrictedEventArgs>? AccountRestricted;

    public SafetyGate(
        TimeProvider clock,
        SafetyOptions options,
        ILogger<SafetyGate>? logger,
        Func<CancellationToken, ValueTask<StoreSession>> resolveStores,
        Func<WhatsAppEvent, CancellationToken, ValueTask> publish)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = SafetyOptions.Validate(options);
        _logger = logger;
        _resolveStores = resolveStores ?? throw new ArgumentNullException(nameof(resolveStores));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _rateLimits = new TokenBucketRateLimiterSet(_clock, _options);
        _warmup = new WarmupLadder(_clock);
    }

    public async Task<SendOutcome> ExecuteSendAsync(
        SendContext context,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<SendOutcome>> innerSend)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(innerSend);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            CheckCircuit(context);
            CheckActiveHours(context);
            await AcquireRateLimits(context, cancellationToken).ConfigureAwait(false);
            await CheckWarmupAsync(context, cancellationToken).ConfigureAwait(false);
            await CheckColdOutreach(context, cancellationToken).ConfigureAwait(false);
            await ApplyJitter(context, cancellationToken).ConfigureAwait(false);
            ApplyPacing(context);

            SendOutcome outcome = await innerSend(cancellationToken).ConfigureAwait(false);
            RecordOutcome(context, outcome);
            return outcome;
        }
        catch (SafetyRuleViolationException ex)
        {
            await PublishViolationAsync(ex, context, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task PublishViolationAsync(SafetyRuleViolationException ex, SendContext context, CancellationToken cancellationToken)
    {
        WhatsAppEvent? evt = ex.Rule switch
        {
            SafetyRule.RateLimit => new BudgetExhaustedEvent(context.Chat.ToString(), "Window", DateTimeOffset.MaxValue, (int)ex.Rule, _clock.GetUtcNow()),
            SafetyRule.InactiveHours => new ThrottleEngagedEvent(context.Chat.ToString(), "Window", DateTimeOffset.MaxValue, (int)ex.Rule, _clock.GetUtcNow()),
            SafetyRule.ColdOutreach => new AccountRestrictedEvent(context.Chat.ToString(), "Outcome", DateTimeOffset.MaxValue, (int)ex.Rule, _clock.GetUtcNow()),
            SafetyRule.CircuitOpen => new AccountRestrictedEvent(context.Chat.ToString(), "Error", DateTimeOffset.MaxValue, (int)ex.Rule, _clock.GetUtcNow()),
            _ => null,
        };

        if (evt is not null)
        {
            try
            {
                await _publish(evt, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Best-effort publish; the violation itself still propagates.
            }
        }

        switch (ex.Rule)
        {
            case SafetyRule.RateLimit:
                BudgetExhausted?.Invoke(this, new BudgetExhaustedEventArgs(context.Chat.ToString(), "Window", DateTimeOffset.MaxValue, (int)ex.Rule));
                break;
            case SafetyRule.InactiveHours:
                ThrottleEngaged?.Invoke(this, new ThrottleEngagedEventArgs(context.Chat.ToString(), "Window", DateTimeOffset.MaxValue, (int)ex.Rule));
                break;
            case SafetyRule.ColdOutreach:
            case SafetyRule.CircuitOpen:
                AccountRestricted?.Invoke(this, new AccountRestrictedEventArgs(context.Chat.ToString(), "Error", DateTimeOffset.MaxValue, (int)ex.Rule));
                break;
        }
    }

    private void CheckCircuit(SendContext context) { }
    private void CheckActiveHours(SendContext context) { }
    private async Task AcquireRateLimits(SendContext context, CancellationToken cancellationToken)
    {
        StoreSession stores = await _resolveStores(cancellationToken).ConfigureAwait(false);
        TokenBucketRateLimiterSet.AcquisitionResult result = await _rateLimits.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Acquired)
        {
            throw new SafetyRuleViolationException(SafetyRule.RateLimit, "Safety rule violated.");
        }

        if (result.DelayedBeyondThreshold)
        {
            ThrottleEngaged?.Invoke(this, new ThrottleEngagedEventArgs(context.Chat.ToString(), "Window", DateTimeOffset.MaxValue, 0));
        }

        // Additional jitter on the reported delay.
        await Task.Delay(_rateLimits.ApplyJitter(), _clock, cancellationToken).ConfigureAwait(false);
    }

    private async Task CheckWarmupAsync(SendContext context, CancellationToken cancellationToken)
    {
        StoreSession stores = await _resolveStores(cancellationToken).ConfigureAwait(false);
        int stage = await _warmup.GetCurrentStageAsync(stores, cancellationToken).ConfigureAwait(false);
        bool admitted = await _warmup.AdmitAsync(stores, cancellationToken).ConfigureAwait(false);
        if (!admitted)
        {
            throw new SafetyRuleViolationException(SafetyRule.RateLimit, "Safety rule violated.");
        }
        _ = stage;
    }

    private Task CheckColdOutreach(SendContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ApplyJitter(SendContext context, CancellationToken cancellationToken)
    {
        TimeSpan delay = _rateLimits.ApplyJitter();
        return Task.Delay(delay, _clock, cancellationToken);
    }

    public Task ApplyJitterAsync(SendContext context, CancellationToken cancellationToken) => ApplyJitter(context, cancellationToken);

    private void ApplyPacing(SendContext context) { }
    private void RecordOutcome(SendContext context, SendOutcome outcome) { }
}
