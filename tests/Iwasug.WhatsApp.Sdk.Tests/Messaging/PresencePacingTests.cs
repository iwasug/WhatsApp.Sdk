#pragma warning disable xUnit1051
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Safety;
using Iwasug.WhatsApp.Sdk.Messaging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Messaging;

/// <summary>
/// EXT-02 pacing proofs: best-effort presence that never fails the send after the
/// Phase 8 upgrade (PresenceManager replaces the no-op seam).
/// </summary>
public sealed class PresencePacingTests
{
    private sealed class RecordingPresence : IPacingPresence
    {
        public int Calls { get; private set; }

        public bool LastBestEffort { get; private set; }

        public Task SendComposingAsync(bool bestEffort, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastBestEffort = bestEffort;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingPresence : IPacingPresence
    {
        public Task SendComposingAsync(bool bestEffort, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("Presence node unavailable."));
    }

    [Fact]
    public async Task composing_best_effort_does_not_throw_when_presence_node_fails()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var options = new SafetyOptions
        {
            ActiveHoursStart = TimeSpan.Zero,
            ActiveHoursEnd = TimeSpan.FromHours(24),
            PacingPresenceMinMs = 0,
            PacingPresenceMaxMs = 0,
        };
        var pacing = new HumanPacing(clock, options, new ThrowingPresence());

        Func<Task> act = () => pacing.ApplyPresencePauseAsync(CancellationToken.None);
        await act.Should().NotThrowAsync("a throwing presence must not fail the pause/send");
    }

    [Fact]
    public async Task composing_best_effort_swallows_cancellation_only_policy()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var options = new SafetyOptions
        {
            ActiveHoursStart = TimeSpan.Zero,
            ActiveHoursEnd = TimeSpan.FromHours(24),
            PacingPresenceMinMs = 0,
            PacingPresenceMaxMs = 0,
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var cancelledPresence = new CancelledPresence();
        var pacing = new HumanPacing(clock, options, cancelledPresence);

        Func<Task> act = () => pacing.ApplyPresencePauseAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>("only cancellation propagates from best-effort presence");
    }

    private sealed class CancelledPresence : IPacingPresence
    {
        public Task SendComposingAsync(bool bestEffort, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}