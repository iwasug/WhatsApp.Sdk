using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Stores;

namespace Iwasug.WhatsApp.Sdk.Safety;

internal sealed class WarmupLadder
{
    public static readonly int[] DefaultStages = new[] { 20, 40, 80, 160, 320, 640, 1000 };

    private readonly TimeProvider _clock;
    private readonly int[] _stages;
    private readonly object _countLock = new();
    private int _sentToday;
    private DateOnly _countDay;

    public WarmupLadder(TimeProvider? clock = null, int[]? stages = null)
    {
        _clock = clock ?? TimeProvider.System;
        _stages = (stages is { Length: > 0 }) ? (int[])stages.Clone() : (int[])DefaultStages.Clone();
    }

    public WarmupLadder(TimeProvider clock) : this(clock, null) { }

    public int[] Stages => (int[])_stages.Clone();

    public async Task<int> GetCurrentStageAsync(StoreSession stores, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stores);
        cancellationToken.ThrowIfCancellationRequested();

        SafetyStateRecord? state = await stores.SafetyState.GetAsync(cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            CredentialRecord? credential = await stores.Credentials.GetAsync(cancellationToken).ConfigureAwait(false);
            if (credential?.PairedUser is not null)
            {
                await stores.SafetyState.PutAsync(
                    new SafetyStateRecord(_clock.GetUtcNow(), 0),
                    cancellationToken).ConfigureAwait(false);
            }

            return 0;
        }

        DateTimeOffset now = _clock.GetUtcNow();
        int elapsedDays = (now - state.CreatedAt).Days;
        if (elapsedDays < 0)
        {
            elapsedDays = 0;
        }

        int index = Math.Clamp(elapsedDays, 0, _stages.Length - 1);
        return index;
    }

    public Task<bool> AdmitAsync(int stage, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateOnly today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        lock (_countLock)
        {
            if (_countDay != today)
            {
                _countDay = today;
                _sentToday = 0;
            }

            return Task.FromResult(_sentToday < CurrentStageCeiling(stage));
        }
    }

    public Task RecordSendAsync(StoreSession stores, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stores);
        cancellationToken.ThrowIfCancellationRequested();
        DateOnly today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        lock (_countLock)
        {
            if (_countDay != today)
            {
                _countDay = today;
                _sentToday = 0;
            }

            _sentToday++;
        }

        return Task.CompletedTask;
    }

    public int CurrentStageCeiling(int stageIndex)
    {
        int idx = Math.Clamp(stageIndex, 0, _stages.Length - 1);
        return _stages[idx];
    }
}