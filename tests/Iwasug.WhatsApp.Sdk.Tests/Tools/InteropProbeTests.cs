using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.InteropProbe;
using Iwasug.WhatsApp.Sdk.Noise;
using Iwasug.WhatsApp.Sdk.Pairing;
using Iwasug.WhatsApp.Sdk.Tests.Transport;
using Iwasug.WhatsApp.Sdk.Transport;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Tools;

/// <summary>
/// InteropProbe decision-contract proofs (09-03): the classifier maps the whole
/// pairing vocabulary onto the exit-code vocabulary (0/10/20), and the runner
/// stays bounded over network-free transports. Scripted-peer proofs drive the
/// 405/ref/silence verdicts end-to-end over the loopback transport.
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

    // ------------------------------------------------------------------
    // Task 2 (tracer): scripted-peer proofs over the loopback transport -
    // the runner starts the pairing attempt on the live stream, maps a
    // server-scripted 405 to drift (10) and a server-issued ref to accepted
    // (0), and stays bounded when the peer never emits a pairing status.
    // Stanza shapes mirror the PairingQrTests loopback protocol.
    // ------------------------------------------------------------------

    private sealed class PairSource
    {
        internal readonly ConcurrentQueue<LoopbackFrameTransport> Peers = new();

        internal int Created;

        internal IFrameTransport CreateClient()
        {
            (LoopbackFrameTransport left, LoopbackFrameTransport right) = LoopbackFrameTransport.CreatePair();
            Peers.Enqueue(right);
            Interlocked.Increment(ref Created);
            return left;
        }
    }

    private static async Task<LoopbackFrameTransport> TakePeerAsync(PairSource pairs, CancellationToken cancellationToken)
    {
        LoopbackFrameTransport? peer;
        while (!pairs.Peers.TryDequeue(out peer))
        {
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }

        return peer!;
    }

    private static async Task<byte[]> ReceiveOrThrowAsync(IFrameTransport transport, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte>? frame = await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Peer transport closed unexpectedly.");

        return frame.Value.ToArray();
    }

    private static async Task<NoiseTransport> CompletePeerHandshakeAsync(
        IFrameTransport peer,
        CancellationToken cancellationToken)
    {
        byte[] header = await ReceiveOrThrowAsync(peer, cancellationToken).ConfigureAwait(false);
        NoiseHandshake.ValidateIntroHeader(header);

        byte[] messageAFrame = await ReceiveOrThrowAsync(peer, cancellationToken).ConfigureAwait(false);
        using var responder = new NoiseHandshakeResponder(Encoding.ASCII.GetBytes("server-hello"));
        byte[] messageA = WaFrame.Deframe(messageAFrame).ToArray();
        responder.ReadMessageA(messageA);
        byte[] messageB = responder.WriteMessageB();
        await peer.SendAsync(WaFrame.Frame(messageB), cancellationToken).ConfigureAwait(false);

        byte[] messageCFrame = await ReceiveOrThrowAsync(peer, cancellationToken).ConfigureAwait(false);
        byte[] messageC = WaFrame.Deframe(messageCFrame).ToArray();
        return responder.ReadMessageC(messageC);
    }

    private static async Task<BinaryNode> ReceivePeerNodeAsync(
        IFrameTransport peer,
        NoiseTransport noise,
        CancellationToken cancellationToken)
    {
        byte[] encrypted = await ReceiveOrThrowAsync(peer, cancellationToken).ConfigureAwait(false);
        ReadOnlySpan<byte> framed = WaFrame.Deframe(encrypted);
        byte[] plain = noise.Decrypt(framed.Slice(1));
        return BinaryNodeCodec.Decode(plain);
    }

    /// <summary>
    /// First-node variant: a closed transport (or cancellation) yields null
    /// instead of throwing, so a peer whose scripted request never arrives
    /// ends quietly while the runner's own bound settles the test.
    /// </summary>
    private static async Task<BinaryNode?> TryReceivePeerNodeAsync(
        IFrameTransport peer,
        NoiseTransport noise,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReceivePeerNodeAsync(peer, noise, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task SendPeerNodeAsync(
        IFrameTransport peer,
        NoiseTransport noise,
        BinaryNode node,
        CancellationToken cancellationToken)
    {
        byte[] encrypted = noise.Encrypt(BinaryNodeCodec.Encode(node));
        byte[] flagged = new byte[encrypted.Length + 1];
        encrypted.CopyTo(flagged.AsSpan(1));
        await peer.SendAsync(WaFrame.Frame(flagged), cancellationToken).ConfigureAwait(false);
    }

    private static BinaryNode RefReply(string id, string pairingRef) =>
        BinaryNode.FromChildren(
            "iq",
            [BinaryNode.Create("pair-device", new Dictionary<string, string> { ["ref"] = pairingRef })],
            new Dictionary<string, string> { ["id"] = id, ["type"] = "result" });

    private static BinaryNode FailureNode(string reason) =>
        BinaryNode.Create("failure", new Dictionary<string, string> { ["reason"] = reason });

    /// <summary>Reads (and discards) peer-bound frames until the client closes, then disposes the peer.</summary>
    private static async Task DrainPeerAsync(
        LoopbackFrameTransport peer,
        NoiseTransport noise,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await ReceivePeerNodeAsync(peer, noise, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        await peer.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Observes the peer script during teardown; only cancellation is benign.</summary>
    private static async Task ObservePeerAsync(Task peerTask)
    {
        try
        {
            await peerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task Run_maps_a_scripted_405_to_drift_exit_10()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        TimeSpan bound = TimeSpan.FromSeconds(20);
        var runner = new ProbeRunner(NewProbeAccount(), pairs.CreateClient, bound);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            NoiseTransport noise = await CompletePeerHandshakeAsync(peer, testToken);
            try
            {
                BinaryNode? request = await TryReceivePeerNodeAsync(peer, noise, testToken);
                if (request is null)
                {
                    // No pairing attempt reached the peer; leave the bound to decide.
                    await peer.DisposeAsync().ConfigureAwait(false);
                    return;
                }

                request.Tag.Should().Be("iq", "the runner starts the pairing attempt on the live stream");
                await SendPeerNodeAsync(peer, noise, FailureNode("405"), testToken);
                await DrainPeerAsync(peer, noise, testToken);
            }
            finally
            {
                noise.Dispose();
            }
        }, testToken);

        ProbeOutcome outcome = await runner.RunAsync(testToken);

        testCts.Cancel();
        await ObservePeerAsync(peerTask);

        outcome.ExitCode.Should().Be(ProbeExitCode.Drift);
        outcome.ProcessExitCode.Should().Be(10);
        outcome.Detail.Should().Contain("405");
        pairs.Created.Should().Be(1, "a 405 is terminal - the probe never retries the connect");
    }

    [Fact]
    public async Task Run_maps_a_server_issued_ref_to_accepted_exit_0()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        TimeSpan bound = TimeSpan.FromSeconds(20);
        var runner = new ProbeRunner(NewProbeAccount(), pairs.CreateClient, bound);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            NoiseTransport noise = await CompletePeerHandshakeAsync(peer, testToken);
            try
            {
                BinaryNode? request = await TryReceivePeerNodeAsync(peer, noise, testToken);
                if (request is null)
                {
                    await peer.DisposeAsync().ConfigureAwait(false);
                    return;
                }

                await SendPeerNodeAsync(peer, noise, RefReply(request.Attrs["id"], "REF-PROBE-1"), testToken);
                await DrainPeerAsync(peer, noise, testToken);
            }
            finally
            {
                noise.Dispose();
            }
        }, testToken);

        ProbeOutcome outcome = await runner.RunAsync(testToken);

        testCts.Cancel();
        await ObservePeerAsync(peerTask);

        outcome.ExitCode.Should().Be(ProbeExitCode.Accepted);
        outcome.ProcessExitCode.Should().Be(0);
        outcome.Detail.Should().Contain("pairing ref");
    }

    [Fact]
    public async Task Run_is_bounded_when_the_peer_never_emits_a_pairing_status()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        TimeSpan bound = TimeSpan.FromSeconds(2);
        var runner = new ProbeRunner(NewProbeAccount(), pairs.CreateClient, bound);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            NoiseTransport noise = await CompletePeerHandshakeAsync(peer, testToken);
            try
            {
                await DrainPeerAsync(peer, noise, testToken);
            }
            finally
            {
                noise.Dispose();
            }
        }, testToken);

        long startedAt = Stopwatch.GetTimestamp();
        ProbeOutcome outcome = await runner.RunAsync(testToken);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);

        testCts.Cancel();
        await ObservePeerAsync(peerTask);

        outcome.ExitCode.Should().Be(ProbeExitCode.Infra);
        outcome.ProcessExitCode.Should().Be(20);
        outcome.Detail.Should().Be("bounded wait elapsed");
        elapsed.Should().BeGreaterThanOrEqualTo(
            bound - TimeSpan.FromMilliseconds(250),
            "the injected bound is the mechanism that ends the wait");
        elapsed.Should().BeLessThan(
            bound + TimeSpan.FromSeconds(10),
            "a mute peer is a classified timeout, never a stuck process");
    }

    // ------------------------------------------------------------------
    // Task 3: the process contract — one option with a frozen default and a
    // validated 1..900 range; everything else throws the typed options
    // exception (the entry point maps it to stderr + exit 2).
    // ------------------------------------------------------------------

    [Fact]
    public void Options_default_to_120_seconds_when_no_arguments_are_given()
    {
        ProbeOptions options = ProbeOptions.Parse([]);

        options.TimeoutSeconds.Should().Be(120);
        options.Timeout.Should().Be(TimeSpan.FromSeconds(120));
        ProbeOptions.DefaultTimeoutSeconds.Should().Be(120, "the frozen default the live workflow relies on");
    }

    [Fact]
    public void Options_parse_explicit_values_and_both_boundaries()
    {
        ProbeOptions explicitValue = ProbeOptions.Parse(["--timeout-seconds", "45"]);
        explicitValue.TimeoutSeconds.Should().Be(45);
        explicitValue.Timeout.Should().Be(TimeSpan.FromSeconds(45));

        ProbeOptions.MinTimeoutSeconds.Should().Be(1);
        ProbeOptions.MaxTimeoutSeconds.Should().Be(900);
        ProbeOptions.Parse(["--timeout-seconds", "1"]).TimeoutSeconds.Should().Be(1);
        ProbeOptions.Parse(["--timeout-seconds", "900"]).TimeoutSeconds.Should().Be(900);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("901")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("12.5")]
    public void Options_reject_values_outside_the_contract(string raw)
    {
        Action parse = () => ProbeOptions.Parse(["--timeout-seconds", raw]);

        parse.Should().Throw<ProbeOptionsException>();
    }

    [Fact]
    public void Options_reject_missing_values_unknown_arguments_and_bare_tokens()
    {
        Action missingValue = () => ProbeOptions.Parse(["--timeout-seconds"]);
        missingValue.Should().Throw<ProbeOptionsException>();

        Action unknownArgument = () => ProbeOptions.Parse(["--verbose"]);
        unknownArgument.Should().Throw<ProbeOptionsException>();

        Action bareToken = () => ProbeOptions.Parse(["120"]);
        bareToken.Should().Throw<ProbeOptionsException>();
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
