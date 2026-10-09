using Iwasug.WhatsApp.Sdk.Errors;

namespace Iwasug.WhatsApp.Sdk.Stores.Runtime;

/// <summary>
/// Tuning parameters for <see cref="DurableWriteRetryPolicy"/> (planner decision A1):
/// a bounded attempt count with a short, jittered exponential backoff, so a transient
/// durable-store outage is bridged briefly and then fails LOUDLY instead of hanging the
/// protocol loop or masking the outage behind success-shaped latency (T-01-14).
/// </summary>
public sealed class RetryOptions
{
    /// <summary>
    /// Total attempts including the first try (3 = first try + 2 retries). Bounded by
    /// design (D-18): the caller observes a typed failure after this many attempts.
    /// </summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>
    /// Delay before the first retry, in milliseconds; also the floor of the clamp window
    /// applied to every jittered delay.
    /// </summary>
    public int BaseDelayMs { get; init; } = 50;

    /// <summary>
    /// Ceiling of the clamp window, in milliseconds: no single retry delay ever exceeds it.
    /// </summary>
    public int MaxDelayMs { get; init; } = 250;
}

/// <summary>
/// D-18 bounded durable-write retry: wraps a durable-capability store write (identity,
/// Signal sessions, pre-keys, safety state) in a bounded attempt loop with a short, jittered
/// exponential backoff, so a transient durable-store outage is bridged without acknowledging
/// state to the protocol layer before it is persisted (D-06) and without unbounded silent
/// retries masking the outage (T-01-14).
/// </summary>
/// <remarks>
/// <para>
/// Scope: the policy applies to the FOUR durable stores only. The recent-message cache does
/// NOT route through it — D-20 assigns the cache log-and-continue semantics, which are the
/// cache-writing SDK machinery's own policy and land with that machinery in Phase 4/5.
/// </para>
/// <para>
/// First real caller: the Phase 2+ protocol loop. This phase ships the wrapper as production
/// SDK code with deterministic unit proofs (FakeTimeProvider) — it is not test scaffolding.
/// Exhaustion (every attempt failed) throws <see cref="SdkStoreException"/> with the
/// last operation exception preserved as the inner exception (D-21 propagate shape): hosts
/// observe a bounded, typed failure, never unbounded retries.
/// </para>
/// <para>
/// Backoff (planner decision A1): raw delay = BaseDelayMs * 2^attemptIndex, then ±30% jitter
/// via <see cref="Random.Shared"/>, THEN clamp to [BaseDelayMs, MaxDelayMs]. Clamp-AFTER-jitter
/// keeps every observed delay inside the configured bounds and keeps attempt N's maximum
/// possible delay below attempt N+1's minimum, so backoff grows monotonically under any
/// jitter sequence.
/// </para>
/// <para>
/// Retryability: <see cref="OperationCanceledException"/> is never retried (cancellation is
/// the host's intent, not a store outage); <see cref="SdkException"/> is never retried
/// (already SDK-typed — the store itself raised an SDK error). Everything else is treated as
/// a transient durable-store failure and retried up to <see cref="RetryOptions.MaxAttempts"/>.
/// </para>
/// <para>
/// Message discipline (T-01-01/T-01-12): the exhaustion message names the operation — never
/// record payloads, key values, or the inner exception's message content.
/// </para>
/// </remarks>
public sealed class DurableWriteRetryPolicy(RetryOptions? options = null, TimeProvider? timeProvider = null)
{
    private readonly RetryOptions _options = ValidateOptions(options);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Runs <paramref name="operation"/> under the bounded retry loop: up to
    /// <see cref="RetryOptions.MaxAttempts"/> attempts with jittered exponential backoff
    /// between attempts (none after the last). Returns the operation's result on first
    /// success; throws the operation's exception directly when it is not retryable;
    /// throws <see cref="Errors.SdkStoreException"/> (last exception as inner) on exhaustion.
    /// </summary>
    /// <typeparam name="T">The operation's result type.</typeparam>
    /// <param name="operation">The durable-store write to attempt; receives the cancellation token.</param>
    /// <param name="cancellationToken">Cancellation token observed by the operation and the inter-attempt delays.</param>
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        Exception? last = null;
        for (int attempt = 0; attempt < _options.MaxAttempts; attempt++)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRetryable(ex))
            {
                last = ex;
            }

            if (attempt < _options.MaxAttempts - 1)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(ComputeDelayMs(attempt)),
                    _timeProvider,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        throw new SdkStoreException(
            $"durable store write failed after {_options.MaxAttempts} attempts.",
            last);
    }

    private static bool IsRetryable(Exception ex)
        => ex is not OperationCanceledException && ex is not SdkException;

    // Fail-fast config validation (WR-01): an invalid RetryOptions throws
    // ArgumentOutOfRangeException here — before any store I/O — instead of
    // surfacing later as a raw ArgumentException from Math.Clamp or a
    // fabricated SdkStoreException from a zero-attempt loop.
    private static RetryOptions ValidateOptions(RetryOptions? options)
    {
        RetryOptions resolved = options ?? new RetryOptions();
        if (resolved.MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxAttempts must be at least 1.");
        }

        if (resolved.BaseDelayMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "BaseDelayMs must not be negative.");
        }

        if (resolved.MaxDelayMs < resolved.BaseDelayMs)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDelayMs must not be below BaseDelayMs.");
        }

        return resolved;
    }

    // Clamp-AFTER-jitter (A1): raw = BaseDelayMs * 2^attemptIndex, ±30% jitter, then clamp
    // to [BaseDelayMs, MaxDelayMs] — observed delays never leave the configured bounds and
    // attempt N's maximum possible delay stays below attempt N+1's minimum.
    private int ComputeDelayMs(int attemptIndex)
    {
        double raw = (double)_options.BaseDelayMs * Math.Pow(2, attemptIndex);
        double jitter = Random.Shared.NextDouble() * 0.6 - 0.3;
        return (int)Math.Clamp(raw * (1.0 + jitter), _options.BaseDelayMs, _options.MaxDelayMs);
    }
}
