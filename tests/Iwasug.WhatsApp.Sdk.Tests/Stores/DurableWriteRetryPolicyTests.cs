using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Stores.Runtime;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Stores;

/// <summary>
/// D-18 bounded durable-write retry, proven with deterministic time (FakeTimeProvider):
/// success-fast, retry-then-succeed, exhaustion-throws-with-inner (last exception preserved,
/// payload discipline), cancellation and SDK-typed exceptions never retried, and backoff
/// that stays inside the 50-250ms bounds while growing per attempt (A1, clamp-after-jitter).
/// </summary>
public sealed class DurableWriteRetryPolicyTests
{
    [Fact]
    public async Task Succeeds_immediately_when_operation_succeeds()
    {
        var policy = new DurableWriteRetryPolicy(new RetryOptions(), new FakeTimeProvider());
        int calls = 0;

        int result = await policy.ExecuteAsync(_ =>
        {
            calls++;
            return Task.FromResult(42);
        }, CancellationToken.None);

        result.Should().Be(42);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task Retries_twice_failing_then_succeeding_operation()
    {
        var fake = new FakeTimeProvider();
        var policy = new DurableWriteRetryPolicy(new RetryOptions(), fake);
        int calls = 0;

        Task<int> task = policy.ExecuteAsync(_ =>
        {
            calls++;
            return calls switch
            {
                1 => Task.FromException<int>(new InvalidOperationException("transient one")),
                2 => Task.FromException<int>(new InvalidOperationException("transient two")),
                _ => Task.FromResult(7),
            };
        }, CancellationToken.None);

        // Two worst-case delays (65ms + 130ms) always fit inside 65 + 130 = 195ms of fake time.
        fake.Advance(TimeSpan.FromMilliseconds(65));
        while (calls < 2)
        {
            await Task.Yield();
        }

        fake.Advance(TimeSpan.FromMilliseconds(130));
        while (calls < 3)
        {
            await Task.Yield();
        }

        int result = await task;

        result.Should().Be(7);
        calls.Should().Be(3);
    }

    [Fact]
    public async Task Exhausts_attempts_and_throws_SdkStoreException_preserving_last_inner()
    {
        var fake = new FakeTimeProvider();
        var policy = new DurableWriteRetryPolicy(new RetryOptions(), fake);
        int calls = 0;
        InvalidOperationException[] failures = new[]
        {
            new InvalidOperationException("record payload alpha"),
            new InvalidOperationException("record payload beta"),
            new InvalidOperationException("record payload gamma"),
        };

        Task<int> task = policy.ExecuteAsync(_ =>
        {
            InvalidOperationException failure = failures[Math.Min(calls, failures.Length - 1)];
            calls++;
            return Task.FromException<int>(failure);
        }, CancellationToken.None);

        fake.Advance(TimeSpan.FromMilliseconds(65));
        while (calls < 2)
        {
            await Task.Yield();
        }

        fake.Advance(TimeSpan.FromMilliseconds(130));
        while (calls < 3)
        {
            await Task.Yield();
        }

        SdkStoreException thrown = await Assert.ThrowsAsync<SdkStoreException>(() => task);

        calls.Should().Be(3);
        thrown.Message.Should().Contain("durable store write failed after 3 attempts");
        thrown.InnerException.Should().BeSameAs(failures[2]);

        // Payload discipline (T-01-12): the surfaced message names the operation — never the
        // inner exception's message content.
        thrown.Message.Should().NotContain("gamma");
    }

    [Fact]
    public void Constructor_throws_ArgumentOutOfRangeException_when_MaxAttempts_is_zero()
    {
        Func<DurableWriteRetryPolicy> act = () => new DurableWriteRetryPolicy(new RetryOptions { MaxAttempts = 0 });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_throws_ArgumentOutOfRangeException_when_MaxDelay_is_below_BaseDelay()
    {
        Func<DurableWriteRetryPolicy> act = () => new DurableWriteRetryPolicy(new RetryOptions { BaseDelayMs = 200, MaxDelayMs = 100 });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_throws_ArgumentOutOfRangeException_when_BaseDelay_is_negative()
    {
        Func<DurableWriteRetryPolicy> act = () => new DurableWriteRetryPolicy(new RetryOptions { BaseDelayMs = -1 });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Does_not_retry_OperationCanceledException()
    {
        var policy = new DurableWriteRetryPolicy(new RetryOptions(), new FakeTimeProvider());
        int calls = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            policy.ExecuteAsync(_ =>
            {
                calls++;
                return Task.FromException<int>(new OperationCanceledException());
            }, CancellationToken.None));

        calls.Should().Be(1);
    }

    [Fact]
    public async Task Does_not_retry_SdkException()
    {
        var policy = new DurableWriteRetryPolicy(new RetryOptions(), new FakeTimeProvider());
        int calls = 0;

        await Assert.ThrowsAsync<SdkStoreException>(() =>
            policy.ExecuteAsync(_ =>
            {
                calls++;
                return Task.FromException<int>(new SdkStoreException("already sdk-typed"));
            }, CancellationToken.None));

        calls.Should().Be(1);
    }

    [Fact]
    public async Task Backoff_delays_stay_within_bounds_and_grow_per_attempt()
    {
        var fake = new FakeTimeProvider();
        var policy = new DurableWriteRetryPolicy(new RetryOptions(), fake);
        int calls = 0;

        Task<int> task = policy.ExecuteAsync(_ =>
        {
            calls++;
            return calls switch
            {
                1 => Task.FromException<int>(new InvalidOperationException("one")),
                2 => Task.FromException<int>(new InvalidOperationException("two")),
                _ => Task.FromResult(9),
            };
        }, CancellationToken.None);

        // Attempt 1 failed; first retry delay is clamped into [50, 65] (raw 50 ±30%).
        fake.Advance(TimeSpan.FromMilliseconds(49));
        task.IsCompleted.Should().BeFalse("the first delay is at least BaseDelayMs (50ms) but only 49ms of fake time elapsed");

        // 65ms always covers delay1 → attempt 2 runs and schedules delay2 ∈ [70, 130].
        fake.Advance(TimeSpan.FromMilliseconds(16));
        while (calls < 2)
        {
            await Task.Yield();
        }

        // 49 + 16 + 4 = 69ms since the second failure: strictly below the [70, ...] floor,
        // so attempt 3 has not started — proves delay2 > delay1's maximum (65).
        fake.Advance(TimeSpan.FromMilliseconds(4));
        task.IsCompleted.Should().BeFalse("the second delay is at least 70ms but only 69ms elapsed since the second failure");

        // 69 + 126 = 195ms ≥ 65 + 130 always covers delay2 → attempt 3 succeeds.
        fake.Advance(TimeSpan.FromMilliseconds(126));
        while (calls < 3)
        {
            await Task.Yield();
        }

        int result = await task;

        result.Should().Be(9);
        calls.Should().Be(3);
    }
}
