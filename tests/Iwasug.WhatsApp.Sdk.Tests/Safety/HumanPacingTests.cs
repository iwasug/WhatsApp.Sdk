#pragma warning disable xUnit1051
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Safety;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Safety;

/// <summary>
/// SAF-06 pacing proofs (05-04 Task 2): active-hours window evaluation (normal,
/// midnight-wrap, zoned), bounded CSPRNG pause draws, cancellation propagation,
/// and best-effort presence that never fails the send.
/// </summary>
public sealed class HumanPacingTests
{
    private sealed class RecordingPresence : IPacingPresence
    {
        public int Calls { get; private set; }

        public bool LastBestEffort { get; private set; }

        public Task SendComposingAsync(bool bestEffort, CancellationToken cancellationToken)
        {
            Calls++;
            LastBestEffort = bestEffort;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingPresence : IPacingPresence
    {
        public Task SendComposingAsync(bool bestEffort, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("Presence node unavailable."));
    }

    private static DateTimeOffset At(int hour, int day = 14, int minute = 0) =>
        new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

    private static SafetyOptions WindowOptions(TimeSpan start, TimeSpan end, TimeZoneInfo? zone = null) => new()
    {
        ActiveHoursStart = start,
        ActiveHoursEnd = end,
        ActiveHoursTimeZone = zone,
        PacingPresenceMinMs = 0,
        PacingPresenceMaxMs = 0,
    };

    [Fact]
    public void outside_window_throws_inactive_hours()
    {
        var clock = new FakeTimeProvider(At(12));
        var pacing = new HumanPacing(clock, WindowOptions(TimeSpan.FromHours(9), TimeSpan.FromHours(17)));

        clock.SetUtcNow(At(20));

        Action act = () => pacing.AssertInsideActiveHours(CancellationToken.None);
        act.Should().Throw<SafetyRuleViolationException>()
            .And.Rule.Should().Be(SafetyRule.InactiveHours);
    }

    [Fact]
    public void inside_window_passes()
    {
        var clock = new FakeTimeProvider(At(12));
        var pacing = new HumanPacing(clock, WindowOptions(TimeSpan.FromHours(9), TimeSpan.FromHours(17)));

        pacing.AssertInsideActiveHours(CancellationToken.None);
    }

    [Fact]
    public void midnight_wrap_window_handles_overnight()
    {
        // 22:00 -> 06:00 overnight window (clock always moves forward: FakeTimeProvider rejects backward sets).
        var clock = new FakeTimeProvider(At(21));
        var pacing = new HumanPacing(clock, WindowOptions(TimeSpan.FromHours(22), TimeSpan.FromHours(6)));

        Action evening = () => pacing.AssertInsideActiveHours(CancellationToken.None);
        evening.Should().Throw<SafetyRuleViolationException>("21:00 is before the overnight window opens");

        clock.SetUtcNow(At(23));
        pacing.AssertInsideActiveHours(CancellationToken.None);

        clock.SetUtcNow(At(5, day: 15));
        pacing.AssertInsideActiveHours(CancellationToken.None);

        clock.SetUtcNow(At(7, day: 15));
        Action morning = () => pacing.AssertInsideActiveHours(CancellationToken.None);
        morning.Should().Throw<SafetyRuleViolationException>()
            .And.Rule.Should().Be(SafetyRule.InactiveHours);

        clock.SetUtcNow(At(12, day: 15));
        Action noon = () => pacing.AssertInsideActiveHours(CancellationToken.None);
        noon.Should().Throw<SafetyRuleViolationException>();
    }

    [Fact]
    public void timezone_converts_before_window_check()
    {
        // Fixed UTC+5 zone: 05:00 UTC is 10:00 local (inside 09:00-17:00),
        // 13:00 UTC is 18:00 local (outside).
        var zone = TimeZoneInfo.CreateCustomTimeZone("TEST+05", TimeSpan.FromHours(5), "TEST+05", "TEST+05");
        var clock = new FakeTimeProvider(At(5));
        var pacing = new HumanPacing(clock, WindowOptions(TimeSpan.FromHours(9), TimeSpan.FromHours(17), zone));

        pacing.AssertInsideActiveHours(CancellationToken.None);

        clock.SetUtcNow(At(13));
        Action act = () => pacing.AssertInsideActiveHours(CancellationToken.None);
        act.Should().Throw<SafetyRuleViolationException>()
            .And.Rule.Should().Be(SafetyRule.InactiveHours);
    }

    [Fact]
    public void pause_bounds_within_range_over_1000_draws()
    {
        var clock = new FakeTimeProvider();
        var pacing = new HumanPacing(clock, options: null);

        for (int i = 0; i < 1000; i++)
        {
            int ms = pacing.NextPauseMs();
            ms.Should().BeInRange(2000, 12000, "every composing pause draws inside the documented 2,000-12,000 ms range");
        }
    }

    [Fact]
    public async Task cancel_pause_propagates_cancellation()
    {
        // Deterministic long pause on the real clock; cancel almost immediately.
        var options = new SafetyOptions { PacingPresenceMinMs = 60_000, PacingPresenceMaxMs = 60_000 };
        var pacing = new HumanPacing(TimeProvider.System, options);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(20));

        Func<Task> act = () => pacing.ApplyPresencePauseAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>("canceling the token cancels the pause and propagates");
    }

    [Fact]
    public async Task presence_throws_swallowed_and_pause_still_applies()
    {
        var clock = new FakeTimeProvider();
        var presence = new RecordingPresence();
        var options = WindowOptions(TimeSpan.Zero, TimeSpan.FromHours(24));
        var pacing = new HumanPacing(clock, options, presence);

        await pacing.ApplyPresencePauseAsync(CancellationToken.None);

        presence.Calls.Should().Be(1);
        presence.LastBestEffort.Should().BeTrue("the gate always signals best-effort");

        var failing = new HumanPacing(clock, options, new ThrowingPresence());
        Func<Task> act = () => failing.ApplyPresencePauseAsync(CancellationToken.None);
        await act.Should().NotThrowAsync("a throwing presence must not fail the pause/send");
    }

    [Fact]
    public void inverted_pacing_bounds_clamp_in_validate()
    {
        SafetyOptions resolved = SafetyOptions.Validate(new SafetyOptions
        {
            PacingPresenceMinMs = 12_000,
            PacingPresenceMaxMs = 2_000,
        });

        resolved.PacingPresenceMaxMs.Should().Be(12_000, "inverted pacing bounds clamp onto the minimum instead of throwing");
        resolved.PacingPresenceMinMs.Should().Be(12_000);
    }
}
