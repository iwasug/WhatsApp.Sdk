using System.Security.Cryptography;
using System.Threading.RateLimiting;

namespace Iwasug.WhatsApp.Sdk.Safety;

/// <summary>Per-account minute/hour/day token buckets.</summary>
internal sealed class TokenBucketRateLimiterSet
{
    public readonly struct AcquisitionResult
    {
        public AcquisitionResult(bool acquired, RateWindow window, bool delayedBeyondThreshold)
        {
            Acquired = acquired;
            Window = window;
            DelayedBeyondThreshold = delayedBeyondThreshold;
        }

        public bool Acquired { get; }
        public RateWindow Window { get; }
        public bool DelayedBeyondThreshold { get; }
    }

    private readonly TimeProvider _clock;
    private readonly SafetyOptions _options;
    private readonly TokenBucketRateLimiter _perMinute;
    private readonly TokenBucketRateLimiter _perHour;
    private readonly TokenBucketRateLimiter _perDay;
    private readonly TimeSpan _threshold;
    private readonly Func<double> _jitterDraw;

    public TokenBucketRateLimiterSet(TimeProvider clock, SafetyOptions options, Func<double>? jitterDraw = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = SafetyOptions.Validate(options);
        _jitterDraw = jitterDraw ?? (() => Random.Shared.NextDouble());
        _threshold = _options.ThrottleNoticeThreshold;
        _perMinute = Create(_options.MaxMessagesPerMinute, TimeSpan.FromMinutes(1));
        _perHour = Create(_options.MaxMessagesPerHour, TimeSpan.FromHours(1));
        _perDay = Create(_options.MaxMessagesPerDay, TimeSpan.FromDays(1));
    }

    public TokenBucketRateLimiterSet(SafetyOptions options, TimeProvider? clock = null, Func<double>? jitterDraw = null)
        : this(clock ?? TimeProvider.System, options, jitterDraw)
    {
    }

    public TimeSpan BaseInterSendJitter => _options.BaseInterSendJitter;
    public double JitterRatioBegin => _options.JitterRatioBegin;
    public double JitterRatioEnd => _options.JitterRatioEnd;

    private static TokenBucketRateLimiter Create(int tokens, TimeSpan period)
    {
        return new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = tokens,
            TokensPerPeriod = tokens,
            ReplenishmentPeriod = period,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }

    public async Task<AcquisitionResult> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RateLimitLease? minuteLease = null;
        RateLimitLease? hourLease = null;
        RateLimitLease? dayLease = null;

        DateTimeOffset started = _clock.GetUtcNow();
        try
        {
            minuteLease = await _perMinute.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
            if (!minuteLease.IsAcquired)
            {
                return new AcquisitionResult(false, RateWindow.Minute, false);
            }

            hourLease = await _perHour.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
            if (!hourLease.IsAcquired)
            {
                await SafeDisposeAsync(minuteLease).ConfigureAwait(false);
                minuteLease = null;
                return new AcquisitionResult(false, RateWindow.Hour, false);
            }

            dayLease = await _perDay.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
            if (!dayLease.IsAcquired)
            {
                await SafeDisposeAsync(hourLease).ConfigureAwait(false);
                hourLease = null;
                await SafeDisposeAsync(minuteLease).ConfigureAwait(false);
                minuteLease = null;
                return new AcquisitionResult(false, RateWindow.Day, false);
            }

            // Track leases so they are not prematurely disposed; keep a reference until caller disposes?
            // Hold leases in a static ignored place? For tests, token counts already consumed.
            // Dispose leases immediately: TokenBucket lease IsAcquired true, disposing does not return token.
            await SafeDisposeAsync(dayLease).ConfigureAwait(false);
            await SafeDisposeAsync(hourLease).ConfigureAwait(false);
            await SafeDisposeAsync(minuteLease).ConfigureAwait(false);

            bool delayed = (_clock.GetUtcNow() - started) >= _threshold;
            return new AcquisitionResult(true, RateWindow.None, delayed);
        }
        catch (OperationCanceledException)
        {
            await DisposeAllAsync(minuteLease, hourLease, dayLease).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await DisposeAllAsync(minuteLease, hourLease, dayLease).ConfigureAwait(false);
            throw;
        }
    }

    public Task<AcquisitionResult> AcquireAsync(CancellationToken cancellationToken = default) => TryAcquireAsync(cancellationToken);

    public TimeSpan ApplyJitter(RandomNumberGenerator? rng = null)
    {
        double ratio = _options.JitterRatioBegin + (_options.JitterRatioEnd - _options.JitterRatioBegin) * _jitterDraw();
        return TimeSpan.FromTicks((long)(_options.BaseInterSendJitter.Ticks * ratio));
    }

    private static Task SafeDisposeAsync(RateLimitLease? lease)
    {
        lease?.Dispose();
        return Task.CompletedTask;
    }

    private static async Task DisposeAllAsync(RateLimitLease? a, RateLimitLease? b, RateLimitLease? c)
    {
        await SafeDisposeAsync(c).ConfigureAwait(false);
        await SafeDisposeAsync(b).ConfigureAwait(false);
        await SafeDisposeAsync(a).ConfigureAwait(false);
    }
}

internal enum RateWindow
{
    None,
    Minute,
    Hour,
    Day,
}
