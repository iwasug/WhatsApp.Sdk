using System.Diagnostics;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.InteropProbe;
using Iwasug.WhatsApp.Sdk.Pairing;
using Iwasug.WhatsApp.Sdk.Transport;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Tools;

/// <summary>
/// InteropProbe decision-contract proofs (09-03): the classifier maps the whole
/// pairing vocabulary onto the exit-code vocabulary (0/10/20), and the runner
/// stays bounded over network-free transports. Scripted-peer proofs for the
/// 405/silence/Code verdicts arrive with the runner's pairing start path.
/// </summary>
public sealed class InteropProbeTests
{
    private static AccountId NewProbeAccount() =>
        AccountId.Create("probe-tests-" + Guid.NewGuid().ToString("N")[..8]);

    [Theory]
    [InlineData(PairingStatus.Code, ProbeExitCode.Accepted, 0)]
    [InlineData(PairingStatus.ClientOutdated, ProbeExitCode.Drift, 10)]
    [InlineData(PairingStatus.Success, ProbeExitCode.Infra, 20)]
    [InlineData(PairingStatus.Timeout, ProbeExitCode.Infra, 20)]
    [InlineData(PairingStatus.LoggedOut, ProbeExitCode.Infra, 20)]
    public void Classifier_maps_every_pairing_status_to_the_exit_vocabulary(
        PairingStatus status,
        ProbeExitCode expectedExitCode,
        int expectedProcessExitCode)
    {
        ProbeOutcome outcome = ProbeClassifier.FromStatus(status);

        outcome.ExitCode.Should().Be(expectedExitCode);
        outcome.ProcessExitCode.Should().Be(expectedProcessExitCode);
        outcome.Detail.Should().NotBeNullOrWhiteSpace("every classified outcome carries a log detail");
    }

    [Fact]
    public void Classifier_maps_exceptions_to_infra_with_a_single_line_detail()
    {
        var exception = new SdkConnectionException(
            "Connection terminated: the account is logged out.\r\nsecond line");

        ProbeOutcome outcome = ProbeClassifier.FromException(exception);

        outcome.ExitCode.Should().Be(ProbeExitCode.Infra);
        outcome.ProcessExitCode.Should().Be(20);
        outcome.Detail.Should().Contain(nameof(SdkConnectionException));
        outcome.Detail.Should().Contain("logged out");
        outcome.Detail.Should().NotContain("\r").And.NotContain("\n");
    }

    [Fact]
    public void Classifier_truncates_long_exception_messages_without_material_leaks()
    {
        string longMessage = new('x', 500);

        ProbeOutcome outcome = ProbeClassifier.FromException(new SdkTransportException(longMessage));

        outcome.ExitCode.Should().Be(ProbeExitCode.Infra);
        outcome.Detail.Should().EndWith(new string('x', 200), "the detail is capped to one short log line");
    }

    [Fact]
    public async Task Run_classifies_a_fail_fast_transport_as_infra_within_the_injected_bound()
    {
        TimeSpan bound = TimeSpan.FromSeconds(2);
        var runner = new ProbeRunner(NewProbeAccount(), () => new RefusingTransport(), bound);

        long startedAt = Stopwatch.GetTimestamp();
        ProbeOutcome outcome = await runner.RunAsync(TestContext.Current.CancellationToken);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);

        outcome.ExitCode.Should().Be(ProbeExitCode.Infra);
        outcome.ProcessExitCode.Should().Be(20);
        elapsed.Should().BeGreaterThanOrEqualTo(
            bound - TimeSpan.FromMilliseconds(250),
            "the injected bound is the mechanism that ends the wait");
        elapsed.Should().BeLessThan(
            bound + TimeSpan.FromSeconds(5),
            "the runner must return promptly once its bound elapses - a refusing transport never hangs the process");
    }

    /// <summary>Fails every transport operation so the connect path faults immediately.</summary>
    private sealed class RefusingTransport : IFrameTransport
    {
        public int? CloseStatusCode => null;

        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new SdkTransportException("probe test transport: send refused"));

        public ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ReadOnlyMemory<byte>?>(new SdkTransportException("probe test transport: receive refused"));

        public ValueTask CloseAsync(int statusCode, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
