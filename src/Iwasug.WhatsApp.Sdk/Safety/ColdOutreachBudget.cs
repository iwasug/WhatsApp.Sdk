using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Stores;

namespace Iwasug.WhatsApp.Sdk.Safety;

internal sealed class ColdOutreachBudget
{
    private readonly TimeProvider _clock;
    private readonly int _dailyCap;
    private readonly object _gate = new();
    private DateOnly _countDay;
    private int _dailyCount;

    public ColdOutreachBudget() : this(TimeProvider.System, 30) { }

    public ColdOutreachBudget(TimeProvider clock) : this(clock ?? TimeProvider.System, 30) { }

    public ColdOutreachBudget(int dailyCap) : this(TimeProvider.System, dailyCap) { }

    public ColdOutreachBudget(TimeProvider clock, int dailyCap)
    {
        _clock = clock ?? TimeProvider.System;
        _dailyCap = dailyCap < 1 ? 30 : dailyCap;
    }

    public ColdOutreachBudget(SafetyOptions options) : this(TimeProvider.System, options ?? SafetyOptions.SafeDefaults) { }

    public ColdOutreachBudget(TimeProvider clock, SafetyOptions options)
        : this(clock ?? TimeProvider.System, (options ?? SafetyOptions.SafeDefaults).MaxColdOutreachPerDay)
    {
    }

    public int DailyCap => _dailyCap;

    public Task<(bool Allowed, bool IsCold)> CheckAsync(SendContext context, IRecipientContactStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        cancellationToken.ThrowIfCancellationRequested();

        string key = RecipientKey(context.Chat);
        // Run store check asynchronously.
        return CheckCoreAsync(context, store, key, cancellationToken);
    }

    private async Task<(bool Allowed, bool IsCold)> CheckCoreAsync(SendContext context, IRecipientContactStore store, string key, CancellationToken cancellationToken)
    {
        bool contacted = await store.HasContactedAsync(key, cancellationToken).ConfigureAwait(false);
        if (contacted)
        {
            return (true, false);
        }

        if (context.IsReply)
        {
            // Reply exemption: does not consume the daily cap.
            return (true, true);
        }

        // Normal first contact: enforce the daily cap.
        lock (_gate)
        {
            DateOnly today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
            if (_countDay != today)
            {
                _countDay = today;
                _dailyCount = 0;
            }

            if (_dailyCount >= _dailyCap)
            {
                throw new SafetyRuleViolationException(SafetyRule.ColdOutreach, "Safety rule violated.");
            }

            _dailyCount++;
        }

        return (true, true);
    }

    public static string RecipientKey(Jid jid)
    {
        ArgumentNullException.ThrowIfNull(jid);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{jid.User}@{jid.Server}");
    }
}