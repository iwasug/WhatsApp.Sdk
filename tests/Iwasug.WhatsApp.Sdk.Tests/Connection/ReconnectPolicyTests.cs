using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Connection;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Connection;

/// <summary>
/// The CONN-04 code→action table, proven without sockets (Decide is a pure function):
/// 401/403/411/419 terminal with no retry arm at any attempt, 440 surface-and-stop, 515
/// immediate restart, 408/428/500/503 and unknown codes bounded jittered retry, and
/// <see cref="ConnectionOptions"/> defaults matching the spike-confirmed Baileys socket
/// config (02-SPIKE-NOTES.md F5).
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

        var retry = decision.Should().BeOfType<ReconnectDecision.RetryAfter>().Subject;
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

        var retry = decision.Should().BeOfType<ReconnectDecision.RetryAfter>().Subject;
        retry.Delay.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        retry.Delay.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(options.RetryMaxDelayMs));
    }

    [Fact]
    public void Decide_first_attempt_delay_never_exceeds_base()
    {
        var options = new ConnectionOptions { RetryBaseDelayMs = 1_000, RetryMaxDelayMs = 60_000 };

        ReconnectDecision decision = ReconnectPolicy.Decide(408, 0, options);

        var retry = decision.Should().BeOfType<ReconnectDecision.RetryAfter>().Subject;
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

            var retry = decision.Should().BeOfType<ReconnectDecision.RetryAfter>().Subject;
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
}
