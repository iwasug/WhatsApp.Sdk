using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Connection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Connection;

/// <summary>
/// The CONN-04 code→action table, proven without sockets (Decide is a pure function):
/// 401/403/411/419 terminal with no retry arm at any attempt, 440 surface-and-stop, 515
/// immediate restart, 408/428/500/503 and unknown codes bounded jittered retry, and
/// <see cref="ConnectionOptions"/> defaults matching the spike-confirmed Baileys socket
/// config (02-SPIKE-NOTES.md F5). Backoff proofs ride <see cref="ReconnectBackoff"/>
/// (DurableWriteRetryPolicyTests style: FakeTimeProvider construction,
/// advance-and-assert-incomplete delay proofs, NotContain payload-discipline asserts).
/// </summary>
public sealed class ReconnectPolicyTests
{
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(411)]
    [InlineData(419)]
    public void Decide_terminal_codes_return_Terminal_at_any_attempt(int statusCode)
    {
        foreach (int attempt in new[] { 0, 1, 10 })
        {
            ReconnectDecision decision = ReconnectPolicy.Decide(statusCode, attempt);

            decision.Should().BeOfType<ReconnectDecision.Terminal>(
                $"code {statusCode} is auth-or-client dead at attempt {attempt} — no retry path may exist");
        }
    }

    [Fact]
    public void Decide_401_logged_out_never_retries_even_after_many_attempts()
    {
        // T-2-01: 401 maps to Terminal with no retry arm — reconnecting against dead
        // credentials loops forever, so the table must answer Terminal at EVERY attempt.
        ReconnectDecision decision = ReconnectPolicy.Decide(401, 100);

        decision.Should().BeSameAs(ReconnectDecision.Terminal.Instance);
    }

    [Fact]
    public void Decide_419_auth_expired_is_terminal_like_401()
    {
        // 02-SPIKE-NOTES.md O1 amendment: 419 sits in Baileys UNAUTHORIZED_CODES with
        // 401/403 — auth-terminal, never a bounded retry (T-2-01 class).
        ReconnectDecision decision = ReconnectPolicy.Decide(419, 3);

        decision.Should().BeSameAs(ReconnectDecision.Terminal.Instance);
    }

    [Fact]
    public void Decide_440_replaced_surfaces_and_stops()
    {
        ReconnectDecision decision = ReconnectPolicy.Decide(440, 0);

        decision.Should().BeSameAs(ReconnectDecision.SurfaceAndStop.Instance);
    }

    [Fact]
    public void Decide_515_restart_required_restarts_immediately()
    {
        ReconnectDecision decision = ReconnectPolicy.Decide(515, 5);

        decision.Should().BeSameAs(ReconnectDecision.RestartImmediate.Instance);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(428)]
    [InlineData(500)]
    [InlineData(503)]
    public void Decide_retryable_codes_return_RetryAfter_within_bounds(int statusCode)
    {
        var options = new ConnectionOptions();

        ReconnectDecision decision = ReconnectPolicy.Decide(statusCode, 2, options);

        ReconnectDecision.RetryAfter retry = decision.Should().BeOfType<ReconnectDecision.RetryAfter>().Subject;
        retry.Delay.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        retry.Delay.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(options.RetryMaxDelayMs));
    }

    [Theory]
    [InlineData(405)]
    [InlineData(418)]
    [InlineData(999)]
    public void Decide_unknown_codes_retry_bounded_for_drift_safety(int statusCode)
    {
        // Unknown/drift codes fail safe: bounded retry (no tight loop), and the caller
        // logs loudly — the delay here proves the bound; the log obligation is documented
        // on Decide for the 02-02 loop to honor.
        var options = new ConnectionOptions();

        ReconnectDecision decision = ReconnectPolicy.Decide(statusCode, 7, options);

        ReconnectDecision.RetryAfter retry = decision.Should().BeOfType<ReconnectDecision.RetryAfter>().Subject;
        retry.Delay.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        retry.Delay.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(options.RetryMaxDelayMs));
    }

    [Fact]
    public void Decide_first_attempt_delay_never_exceeds_base()
    {
        var options = new ConnectionOptions { RetryBaseDelayMs = 1_000, RetryMaxDelayMs = 60_000 };

        ReconnectDecision decision = ReconnectPolicy.Decide(408, 0, options);

        ReconnectDecision.RetryAfter retry = decision.Should().BeOfType<ReconnectDecision.RetryAfter>().Subject;
        retry.Delay.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(options.RetryBaseDelayMs));
    }

    [Fact]
    public void Decide_delays_stay_capped_across_attempt_sweep()
    {
        // Clamp-after-jitter discipline (D-18): no attempt index — however large — may
        // produce a delay above the cap (also proves no integer overflow in base * 2^n).
        var options = new ConnectionOptions { RetryBaseDelayMs = 1_000, RetryMaxDelayMs = 8_000 };

        for (int attempt = 0; attempt <= 30; attempt++)
        {
            ReconnectDecision decision = ReconnectPolicy.Decide(503, attempt, options);

            ReconnectDecision.RetryAfter retry = decision.Should().BeOfType<ReconnectDecision.RetryAfter>().Subject;
            retry.Delay.Should().BeLessThanOrEqualTo(
                TimeSpan.FromMilliseconds(options.RetryMaxDelayMs),
                $"attempt {attempt} must stay capped");
        }
    }

    [Fact]
    public void Decide_negative_attempt_throws_ArgumentOutOfRangeException()
    {
        Action act = () => ReconnectPolicy.Decide(408, -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Decide_invalid_options_throw_before_any_delay_is_computed()
    {
        Action act = () => ReconnectPolicy.Decide(
            408, 0, new ConnectionOptions { RetryBaseDelayMs = 200, RetryMaxDelayMs = 100 });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ConnectionOptions_defaults_match_spike_confirmed_socket_config()
    {
        // 02-SPIKE-NOTES.md F5 (Baileys src/Defaults/index.ts at 0af2386):
        // connectTimeoutMs 20000, defaultQueryTimeoutMs 60000, keepAliveIntervalMs 30000.
        // Retry bounds are the 02-RESEARCH starting point (base 1s, cap 60s).
        var options = new ConnectionOptions();

        options.ConnectTimeoutMs.Should().Be(20_000);
        options.QueryTimeoutMs.Should().Be(60_000);
        options.KeepAliveIntervalMs.Should().Be(30_000);
        options.RetryBaseDelayMs.Should().Be(1_000);
        options.RetryMaxDelayMs.Should().Be(60_000);
    }

    [Fact]
    public void Decide_440_replaced_never_retries_at_any_depth()
    {
        // Seed 2-seed-04: 440 avoids the device kick-war — SurfaceAndStop at every depth.
        foreach (int attempt in new[] { 0, 1, 50 })
        {
            ReconnectPolicy.Decide(440, attempt).Should().BeSameAs(
                ReconnectDecision.SurfaceAndStop.Instance,
                $"440 must surface-and-stop at attempt {attempt}, never schedule a retry");
        }
    }

    [Fact]
    public void Decide_411_outdated_never_retries_at_any_depth()
    {
        // Seed 2-seed-04: 411 needs a deploy with a bumped version, not retries.
        foreach (int attempt in new[] { 0, 1, 50 })
        {
            ReconnectPolicy.Decide(411, attempt).Should().BeSameAs(
                ReconnectDecision.Terminal.Instance,
                $"411 must stay terminal at attempt {attempt}");
        }
    }

    [Theory]
    [InlineData(408)]
    [InlineData(428)]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(405)]
    public void Decide_huge_attempt_counts_stay_bounded_without_overflow(int statusCode)
    {
        // Unbounded 408/428 attempts (02-RESEARCH A6) and bounded drift codes alike:
        // double arithmetic never overflows, every draw stays within [0, cap].
        var options = new ConnectionOptions();

        ReconnectDecision decision = ReconnectPolicy.Decide(statusCode, 1_000_000, options);

        ReconnectDecision.RetryAfter retry = decision.Should().BeOfType<ReconnectDecision.RetryAfter>().Subject;
        retry.Delay.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        retry.Delay.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(options.RetryMaxDelayMs));
    }

    [Fact]
    public void Decide_guard_messages_name_rules_only()
    {
        Action negative = () => ReconnectPolicy.Decide(408, -1);

        ArgumentOutOfRangeException thrown = negative.Should().Throw<ArgumentOutOfRangeException>().Which;
        thrown.Message.Should().Contain("must not be negative");
        thrown.Message.Should().NotContain("-1", "guard messages name the rule — they never embed the offending value");
    }

    [Fact]
    public async Task Backoff_first_retry_waits_its_full_jitter_delay_on_fake_time()
    {
        var fake = new FakeTimeProvider();
        var backoff = new ReconnectBackoff(
            new ConnectionOptions { RetryBaseDelayMs = 1_000, RetryMaxDelayMs = 60_000 },
            fake,
            nextUnitDraw: () => 1.0);

        // Attempt 0: ceiling = min(60000, 1000 * 2^0) = 1000, max draw → exactly 1000ms.
        Task<TimeSpan> wait = backoff.WaitAsync(CancellationToken.None);

        fake.Advance(TimeSpan.FromMilliseconds(999));
        wait.IsCompleted.Should().BeFalse("only 999ms of the 1000ms full-jitter delay elapsed");

        fake.Advance(TimeSpan.FromMilliseconds(1));
        (await wait).Should().Be(TimeSpan.FromMilliseconds(1_000));
        backoff.Attempt.Should().Be(1, "the wait consumed exactly one attempt depth");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1_234_567)]
    public void Backoff_delays_stay_within_bounds_under_adversarial_jitter_seeds(int seed)
    {
        // Clamp-after-jitter (D-18) under hostile draws: no seed and no depth — however
        // large — may produce a delay outside [0, cap], and depth 0 never exceeds base.
        var options = new ConnectionOptions { RetryBaseDelayMs = 1_000, RetryMaxDelayMs = 8_000 };
        var jitter = new Random(seed);

        for (int attempt = 0; attempt <= 30; attempt++)
        {
            TimeSpan delay = ReconnectPolicy.ComputeDelay(attempt, options, jitter.NextDouble());

            delay.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
            delay.Should().BeLessThanOrEqualTo(
                TimeSpan.FromMilliseconds(options.RetryMaxDelayMs),
                $"seed {seed} attempt {attempt} must stay capped");
            if (attempt == 0)
            {
                delay.Should().BeLessThanOrEqualTo(
                    TimeSpan.FromMilliseconds(options.RetryBaseDelayMs),
                    $"seed {seed}: the first window is the base ceiling");
            }
        }
    }

    [Fact]
    public void Backoff_jitter_decorrelates_delays_at_a_fixed_attempt()
    {
        // Full jitter must spread delays across the window — a constant delay would be
        // the thundering-herd loser the AWS canonical reference warns against.
        var options = new ConnectionOptions { RetryBaseDelayMs = 1_000, RetryMaxDelayMs = 60_000 };
        var jitter = new Random(7);

        TimeSpan[] draws = Enumerable.Range(0, 50)
            .Select(_ => ReconnectPolicy.ComputeDelay(5, options, jitter.NextDouble()))
            .ToArray();

        draws.Min().Should().NotBe(draws.Max(), "50 draws at one attempt must not collapse to a constant");
        draws.Should().OnlyContain(d => d >= TimeSpan.Zero && d <= TimeSpan.FromMilliseconds(32_000));
    }

    [Fact]
    public void Backoff_attempt_counter_resets_for_open_and_515_restart()
    {
        // 02-RESEARCH Pattern 4: Open breaks the failure chain and 515 restarts with a
        // reset counter — the window restarts at the base ceiling either way.
        var backoff = new ReconnectBackoff(
            new ConnectionOptions { RetryBaseDelayMs = 1_000, RetryMaxDelayMs = 60_000 },
            new FakeTimeProvider(),
            nextUnitDraw: () => 1.0);

        backoff.NextDelay();
        backoff.NextDelay();
        backoff.NextDelay();
        backoff.Attempt.Should().Be(3);

        backoff.Reset();
        backoff.Attempt.Should().Be(0);
        backoff.NextDelay().Should().Be(
            TimeSpan.FromMilliseconds(1_000),
            "after reset the window restarts at the base ceiling");

        // The verdict side is attempt-agnostic for restart: 515 restarts even at depth.
        ReconnectPolicy.Decide(515, 29).Should().BeSameAs(ReconnectDecision.RestartImmediate.Instance);
    }

    [Fact]
    public void Backoff_constructor_rejects_out_of_range_bounds_before_any_delay()
    {
        Func<ReconnectBackoff> create = () => new ReconnectBackoff(
            new ConnectionOptions { RetryBaseDelayMs = 200, RetryMaxDelayMs = 100 },
            new FakeTimeProvider());

        create.Should().Throw<ArgumentOutOfRangeException>();
    }
}
