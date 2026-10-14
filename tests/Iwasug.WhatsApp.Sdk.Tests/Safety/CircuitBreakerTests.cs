#pragma warning disable xUnit1051
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Messaging;
using Iwasug.WhatsApp.Sdk.Safety;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Safety;

/// <summary>
/// SAF-05 breaker unit proofs (05-04 Task 1): threshold trips, exponential
/// backoff doubling with cap, half-open single-probe discipline, and exactly-once
/// open-transition events.
/// </summary>
public sealed class CircuitBreakerTests
{
    private static SendOutcome Sent(string id = "sent-1") =>
        new(id, SendOutcomeStatus.Sent);

    private static SendOutcome ServerError(string id = "err-1") =>
        new(id, SendOutcomeStatus.ServerError, Error: 429);

    private static SendOutcome TimedOut(string id = "timeout-1") =>
        new(id, SendOutcomeStatus.TimedOut);

    private static CircuitBreaker Create(FakeTimeProvider clock, Action? onOpened = null) =>
        new(clock, onOpened: onOpened);

    [Fact]
    public void open_on_threshold_after_five_consecutive_server_errors()
    {
        var clock = new FakeTimeProvider();
        int events = 0;
        CircuitBreaker breaker = Create(clock, onOpened: () => events++);

        for (int i = 0; i < 4; i++)
        {
            breaker.RecordOutcome(ServerError($"e-{i}"), CancellationToken.None).Should().BeFalse();
            breaker.State.Should().Be(CircuitState.Closed, "four failures are below the threshold of five");
        }

        breaker.RecordOutcome(ServerError("e-4"), CancellationToken.None).Should().BeTrue("the fifth failure is a fresh Open transition");
        breaker.State.Should().Be(CircuitState.Open);
        events.Should().Be(1);

        Action act = () => breaker.ThrowIfOpen(CancellationToken.None);
        act.Should().Throw<SafetyRuleViolationException>()
            .And.Rule.Should().Be(SafetyRule.CircuitOpen);
    }

    [Fact]
    public void open_on_threshold_after_three_consecutive_timeouts()
    {
        var clock = new FakeTimeProvider();
        int events = 0;
        CircuitBreaker breaker = Create(clock, onOpened: () => events++);

        breaker.RecordOutcome(TimedOut("t-1"), CancellationToken.None).Should().BeFalse();
        breaker.RecordOutcome(TimedOut("t-2"), CancellationToken.None).Should().BeFalse();
        breaker.State.Should().Be(CircuitState.Closed);

        breaker.RecordOutcome(TimedOut("t-3"), CancellationToken.None).Should().BeTrue("three consecutive timeouts trip on their own");
        breaker.State.Should().Be(CircuitState.Open);
        events.Should().Be(1);
    }

    [Fact]
    public void success_resets_consecutive_failure_count()
    {
        var clock = new FakeTimeProvider();
        CircuitBreaker breaker = Create(clock);

        for (int i = 0; i < 4; i++)
        {
            breaker.RecordOutcome(ServerError($"e-{i}"), CancellationToken.None);
        }

        breaker.RecordOutcome(Sent(), CancellationToken.None).Should().BeFalse("recovery fires no event");

        for (int i = 0; i < 4; i++)
        {
            breaker.RecordOutcome(ServerError($"f-{i}"), CancellationToken.None).Should().BeFalse();
        }

        breaker.State.Should().Be(CircuitState.Closed, "the success reset the streak; only four failures accumulated since");
    }

    [Fact]
    public void backoff_doubles_with_each_failed_probe_up_to_cap()
    {
        var clock = new FakeTimeProvider();
        CircuitBreaker breaker = Create(clock);

        for (int i = 0; i < 5; i++)
        {
            breaker.RecordOutcome(ServerError($"e-{i}"), CancellationToken.None);
        }

        breaker.CurrentBackoff.Should().Be(TimeSpan.FromSeconds(30), "the first opening waits the 30s base");
        DateTimeOffset firstProbeAt = breaker.NextProbeAtUtc;

        // First failed probe: 30s -> 60s.
        clock.Advance(TimeSpan.FromSeconds(30));
        breaker.ThrowIfOpen(CancellationToken.None);
        breaker.State.Should().Be(CircuitState.HalfOpen);
        breaker.RecordOutcome(TimedOut("probe-1"), CancellationToken.None).Should().BeTrue("a failed probe is a fresh Open transition");
        breaker.CurrentBackoff.Should().Be(TimeSpan.FromSeconds(60));
        breaker.NextProbeAtUtc.Should().BeAfter(firstProbeAt);

        // Second failed probe: 60s -> 120s.
        clock.Advance(TimeSpan.FromSeconds(60));
        breaker.ThrowIfOpen(CancellationToken.None);
        breaker.RecordOutcome(ServerError("probe-2"), CancellationToken.None).Should().BeTrue();
        breaker.CurrentBackoff.Should().Be(TimeSpan.FromSeconds(120));

        // Drive to the cap: doubling never exceeds 10 minutes.
        for (int i = 0; i < 6; i++)
        {
            clock.Advance(breaker.CurrentBackoff);
            breaker.ThrowIfOpen(CancellationToken.None);
            breaker.RecordOutcome(ServerError($"probe-cap-{i}"), CancellationToken.None);
        }

        breaker.CurrentBackoff.Should().Be(TimeSpan.FromMinutes(10));
        breaker.State.Should().Be(CircuitState.Open);
    }

    [Fact]
    public void half_open_probe_success_closes_breaker_without_event()
    {
        var clock = new FakeTimeProvider();
        int events = 0;
        CircuitBreaker breaker = Create(clock, onOpened: () => events++);

        for (int i = 0; i < 5; i++)
        {
            breaker.RecordOutcome(ServerError($"e-{i}"), CancellationToken.None);
        }

        events.Should().Be(1);
        clock.Advance(TimeSpan.FromSeconds(30));

        breaker.ThrowIfOpen(CancellationToken.None);
        breaker.State.Should().Be(CircuitState.HalfOpen);

        breaker.RecordOutcome(Sent("probe-ok"), CancellationToken.None).Should().BeFalse("recovery fires no event");
        breaker.State.Should().Be(CircuitState.Closed);
        events.Should().Be(1, "only the Open transition emits");

        // Closed again: sends flow with no guard throw.
        breaker.ThrowIfOpen(CancellationToken.None);
    }

    [Fact]
    public void half_open_probe_failure_reopens_with_longer_backoff()
    {
        var clock = new FakeTimeProvider();
        int events = 0;
        CircuitBreaker breaker = Create(clock, onOpened: () => events++);

        for (int i = 0; i < 5; i++)
        {
            breaker.RecordOutcome(ServerError($"e-{i}"), CancellationToken.None);
        }

        TimeSpan firstBackoff = breaker.CurrentBackoff;
        clock.Advance(firstBackoff);
        breaker.ThrowIfOpen(CancellationToken.None);

        breaker.RecordOutcome(ServerError("probe-fail"), CancellationToken.None).Should().BeTrue();
        breaker.State.Should().Be(CircuitState.Open);
        breaker.CurrentBackoff.Should().BeGreaterThan(firstBackoff, "a failed probe re-opens with the next (doubled) backoff");
        events.Should().Be(2, "each Open transition emits exactly once");
    }

    [Fact]
    public void half_open_admits_exactly_one_probe()
    {
        var clock = new FakeTimeProvider();
        CircuitBreaker breaker = Create(clock);

        for (int i = 0; i < 5; i++)
        {
            breaker.RecordOutcome(ServerError($"e-{i}"), CancellationToken.None);
        }

        clock.Advance(TimeSpan.FromSeconds(30));
        breaker.ThrowIfOpen(CancellationToken.None);

        Action secondProbe = () => breaker.ThrowIfOpen(CancellationToken.None);
        secondProbe.Should().Throw<SafetyRuleViolationException>()
            .And.Rule.Should().Be(SafetyRule.CircuitOpen, "the single half-open probe is already in flight");
    }

    [Fact]
    public void no_duplicate_event_while_open()
    {
        var clock = new FakeTimeProvider();
        int events = 0;
        CircuitBreaker breaker = Create(clock, onOpened: () => events++);

        for (int i = 0; i < 5; i++)
        {
            breaker.RecordOutcome(ServerError($"e-{i}"), CancellationToken.None);
        }

        events.Should().Be(1);

        // Repeated failed sends while already open: no fresh transition, no event.
        breaker.RecordOutcome(ServerError("late-1"), CancellationToken.None).Should().BeFalse();
        breaker.RecordOutcome(TimedOut("late-2"), CancellationToken.None).Should().BeFalse();
        breaker.RecordOutcome(ServerError("late-3"), CancellationToken.None).Should().BeFalse();

        events.Should().Be(1, "failures while already Open must not re-emit AccountRestricted");
        breaker.State.Should().Be(CircuitState.Open);
    }

    [Fact]
    public void throw_if_open_checks_cancellation_first()
    {
        var clock = new FakeTimeProvider();
        CircuitBreaker breaker = Create(clock);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Action act = () => breaker.ThrowIfOpen(cts.Token);
        act.Should().Throw<OperationCanceledException>();
    }
}
