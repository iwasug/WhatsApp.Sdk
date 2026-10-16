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

    internal SafetyOptions Options => _options;
    private readonly ILogger<SafetyGate>? _logger;
    private readonly Func<CancellationToken, ValueTask<StoreSession>> _resolveStores;
    private readonly Func<WhatsAppEvent, CancellationToken, ValueTask> _publish;
    private readonly TokenBucketRateLimiterSet _rateLimits;
    private readonly WarmupLadder _warmup;
    private readonly ColdOutreachBudget _coldOutreach;
    private readonly CircuitBreaker _breaker;
    private readonly HumanPacing _pacing;

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
        Func<WhatsAppEvent, CancellationToken, ValueTask> publish,
        IPacingPresence? presence = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = SafetyOptions.Validate(options);
        _logger = logger;
        _resolveStores = resolveStores ?? throw new ArgumentNullException(nameof(resolveStores));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _rateLimits = new TokenBucketRateLimiterSet(_clock, _options);
        _warmup = new WarmupLadder(_clock);
        _coldOutreach = new ColdOutreachBudget(_clock, _options);
        _breaker = new CircuitBreaker(_clock);
    
        _pacing = new HumanPacing(_clock, _options, presence);
    }

    internal Safety.CircuitState CircuitState => _breaker.State;

    public async Task<SendOutcome> ExecuteSendAsync(
        SendContext context,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<SendOutcome>> innerSend)
    {
        ArgumentNullException.ThrowIfNull(innerSend);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            CheckCircuit(context, cancellationToken);
            CheckActiveHours(context, cancellationToken);
            await AcquireRateLimits(context, cancellationToken).ConfigureAwait(false);
            await CheckWarmupAsync(context, cancellationToken).ConfigureAwait(false);
            await CheckColdOutreach(context, cancellationToken).ConfigureAwait(false);
            await ApplyJitter(context, cancellationToken).ConfigureAwait(false);
            await ApplyPresencePauseAsync(cancellationToken).ConfigureAwait(false);

            SendOutcome outcome = await innerSend(cancellationToken).ConfigureAwait(false);
            await RecordOutcomeAsync(context, outcome, cancellationToken).ConfigureAwait(false);
            return outcome;
        }
        catch (SafetyRuleViolationException ex)
        {
            await PublishViolationAsync(ex, context, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private void CheckCircuit(SendContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _breaker.ThrowIfOpen(cancellationToken);
    }

    private async Task PublishViolationAsync(SafetyRuleViolationException ex, SendContext context, CancellationToken cancellationToken)
    {
        // CircuitOpen refusals emit nothing: the Open transition already emitted
        // AccountRestricted once via PublishBreakerOpenAsync, and every refusal
        // while already open is steady-state, not a transition (SAF-05: no
        // duplicate AccountRestricted on repeated sends while already open).
        // The typed refusal itself still propagates to the caller below.
        if (ex.Rule == SafetyRule.CircuitOpen)
        {
            return;
        }

        WhatsAppEvent? evt = ex.Rule switch
        {
            SafetyRule.RateLimit => new BudgetExhaustedEvent(context.Chat.ToString(), "Window", DateTimeOffset.MaxValue, (int)ex.Rule, _clock.GetUtcNow()),
            SafetyRule.InactiveHours => new ThrottleEngagedEvent(context.Chat.ToString(), "Window", DateTimeOffset.MaxValue, (int)ex.Rule, _clock.GetUtcNow()),
            SafetyRule.ColdOutreach => new AccountRestrictedEvent(context.Chat.ToString(), "Outcome", DateTimeOffset.MaxValue, (int)ex.Rule, _clock.GetUtcNow()),
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
                AccountRestricted?.Invoke(this, new AccountRestrictedEventArgs(context.Chat.ToString(), "Error", DateTimeOffset.MaxValue, (int)ex.Rule));
                break;
        }

    }

    /// <summary>
    /// Publishes one breaker opening: the <c>AccountRestricted</c> stream twin,
    /// then the handler twin. Best-effort on the success path — the inner send
    /// already settled, so a publish failure (or a caller cancellation racing the
    /// publish) never converts the settled outcome into an exception.
    /// </summary>
    private async Task PublishBreakerOpenAsync(SendContext context, CancellationToken cancellationToken)
    {
        var args = new AccountRestrictedEventArgs(
            context.Chat.ToString(), "Error", _breaker.NextProbeAtUtc, (int)SafetyRule.CircuitOpen);
        WhatsAppEvent evt = new AccountRestrictedEvent(
            args.AccountKey, args.Trigger, args.NextAllowedUtc, args.SafetyStage, _clock.GetUtcNow());

        try
        {
            await _publish(evt, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The send already settled; the outcome still returns below.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Safety event publish failed; continuing (D-20).");
        }

        AccountRestricted?.Invoke(this, args);
    }

    private void CheckActiveHours(SendContext context, CancellationToken cancellationToken)
    {
        _pacing.AssertInsideActiveHours(cancellationToken);
    }
    private async Task AcquireRateLimits(SendContext context, CancellationToken cancellationToken)
    {
        // Process-local buckets: no store round-trip (durability lives in the
        // warm-up row and the recipient ledger, not in rate windows).
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
        bool admitted = await _warmup.AdmitAsync(stage, cancellationToken).ConfigureAwait(false);
        if (!admitted)
        {
            throw new SafetyRuleViolationException(SafetyRule.RateLimit, "Safety rule violated.");
        }
    }

    private async Task CheckColdOutreach(SendContext context, CancellationToken cancellationToken)
    {
        StoreSession stores = await _resolveStores(cancellationToken).ConfigureAwait(false);
        await _coldOutreach.CheckAsync(context, stores.Recipients, cancellationToken).ConfigureAwait(false);
    }
    public Task ApplyJitter(SendContext context, CancellationToken cancellationToken)
    {
        TimeSpan delay = _rateLimits.ApplyJitter();
        return Task.Delay(delay, _clock, cancellationToken);
    }

    public Task ApplyJitterAsync(SendContext context, CancellationToken cancellationToken) => ApplyJitter(context, cancellationToken);

    private Task ApplyPresencePauseAsync(CancellationToken cancellationToken) =>
        _pacing.ApplyPresencePauseAsync(cancellationToken);

    private async Task RecordOutcomeAsync(SendContext context, SendOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        // Circuit record (05-04, SAF-05): every settled outcome feeds the breaker,
        // including ServerError and TimedOut. A fresh Open transition publishes
        // AccountRestricted exactly once (breaker-owned idempotency: failures
        // recorded while already Open return false and emit nothing).
        bool opened = _breaker.RecordOutcome(outcome, cancellationToken);
        if (opened)
        {
            await PublishBreakerOpenAsync(context, cancellationToken).ConfigureAwait(false);
        }

        // Warm-up accounting is best-effort and never fails the returned outcome (D-20).
        try
        {
            StoreSession warmupStores = await _resolveStores(cancellationToken).ConfigureAwait(false);
            await _warmup.RecordSendAsync(warmupStores, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Warm-up record failed; continuing (D-20).");
        }

        // Mark only after a send that actually left the process (Sent or TimedOut), and
        // only for normal (non-reply) sends (must_have: marked only for normal sends).
        if (outcome.Status != SendOutcomeStatus.Sent && outcome.Status != SendOutcomeStatus.TimedOut)
        {
            return;
        }

        if (context.IsReply)
        {
            return;
        }

        try
        {
            StoreSession stores = await _resolveStores(cancellationToken).ConfigureAwait(false);
            await stores.Recipients.MarkContactedAsync(ColdOutreachBudget.RecipientKey(context.Chat), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Recipient-contact mark failed; continuing (D-20).");
        }
    }
}
