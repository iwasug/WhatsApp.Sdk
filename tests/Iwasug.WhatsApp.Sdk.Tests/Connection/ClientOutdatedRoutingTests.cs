using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Noise;
using Iwasug.WhatsApp.Sdk.Pairing;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.InMemory;
using Iwasug.WhatsApp.Sdk.Tests.Transport;
using Iwasug.WhatsApp.Sdk.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Connection;

/// <summary>
/// ClientOutdated routing through the connection (03-02): a 405 drop maps to
/// <see cref="DisconnectReason.ClientOutdated"/>, the reconnect policy stops the loop
/// with zero further attempts, and the pairing-terminal seam fires exactly once —
/// while a 503 drop keeps retrying without raising the seam. All drop injection
/// reuses the loopback composition (one shared local helper per piece, mirroring
/// <c>LoopbackRoundTripTests</c>), and every reconnect verdict is asserted on the
/// injected <see cref="FakeTimeProvider"/> clock — no test sleeps on wall time.
/// </summary>
public sealed class ClientOutdatedRoutingTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private sealed class StaticPayloads : INoiseHandshakePayloads
    {
        public byte[] GetClientHello() => Encoding.ASCII.GetBytes("client-hello");

        public byte[] GetClientFinish() => Encoding.ASCII.GetBytes("client-finish");
    }

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

    private sealed class Observed
    {
        internal readonly ConcurrentQueue<ConnectionStateChangedEventArgs> States = new();
        internal readonly ConcurrentQueue<PairingTerminalEventArgs> Seam = new();
        internal readonly PairSource Pairs = new();
    }

    private static AccountId NewAccount(string hint) =>
        AccountId.Create($"outdated-{hint}-" + Guid.NewGuid().ToString("N"));

    private static ConnectionOptions TestOptions() => new()
    {
        ConnectTimeoutMs = 5_000,
        QueryTimeoutMs = 5_000,
        RetryBaseDelayMs = 1_000,
        RetryMaxDelayMs = 60_000,
    };

    private static async Task<INoiseHandshakePayloads> CreateSeededPayloadsAsync(AccountId account, CancellationToken cancellationToken)
    {
        ICredentialStoreFactory factory = new InMemoryStoreFactory();
        ICredentialStore store = await factory.CreateAsync(account, cancellationToken).ConfigureAwait(false);
        var row = new CredentialRecord(
            DateTimeOffset.UtcNow,
            "android",
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32),
            "15551234567",
            1,
            42);
        await store.PutAsync(row, cancellationToken).ConfigureAwait(false);
        return new HandshakePayloadProvider(
            account,
            store,
            new RegistrationDefaults(
                new byte[32], 7, new byte[32], new byte[64], 42, "android", 2, 3000, 1043857760));
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

    private static async Task ServeOneQueryAsync(
        IFrameTransport peer,
        NoiseTransport noise,
        CancellationToken cancellationToken)
    {
        byte[] encrypted = await ReceiveOrThrowAsync(peer, cancellationToken).ConfigureAwait(false);
        ReadOnlySpan<byte> framed = WaFrame.Deframe(encrypted);
        byte[] plain = noise.Decrypt(framed.Slice(1));
        if (!QueryRouter.TryParseFrame(plain, out string? id, out byte[]? body) || id is null || body is null)
        {
            throw new InvalidOperationException("Peer received an unparsable query frame.");
        }

        byte[] response = QueryRouter.BuildFrame(id, body);
        byte[] reply = noise.Encrypt(response);
        byte[] flagged = new byte[reply.Length + 1];
        reply.CopyTo(flagged.AsSpan(1));
        await peer.SendAsync(WaFrame.Frame(flagged), cancellationToken).ConfigureAwait(false);
    }

    private static async Task WaitForQueuedAsync(
        ConcurrentQueue<ConnectionStateChangedEventArgs> states,
        Func<ConnectionStateChangedEventArgs, bool> predicate,
        string what)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);
        while (!timeout.IsCancellationRequested)
        {
            foreach (ConnectionStateChangedEventArgs state in states)
            {
                if (predicate(state))
                {
                    return;
                }
            }

            await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for connection event: {what}.");
    }

    private static async Task SettleAfterClockAdvanceAsync(CancellationToken cancellationToken)
    {
        // The verdict never depends on wall time: under a correct terminal
        // implementation nothing is scheduled, so nothing can fire — this settle only
        // lets an erroneously-scheduled retry observe the advanced clock and expose
        // itself in the event counts below.
        await Task.Delay(50, cancellationToken).ConfigureAwait(false);
    }

    [Fact]
    public async Task Drop405_after_open_is_terminal_with_zero_reconnect_attempts()
    {
        var fakeTime = new FakeTimeProvider();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken testToken = testCts.Token;
        var observed = new Observed();
        AccountId account = NewAccount("stop");
        await using var connection = new WaConnection(
            account,
            observed.Pairs.CreateClient,
            await CreateSeededPayloadsAsync(account, testToken),
            TestOptions(),
            fakeTime);
        connection.StateChanged += (_, e) => observed.States.Enqueue(e);
        connection.PairingTerminal += (_, e) => observed.Seam.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(observed.Pairs, testToken);
            NoiseTransport noise = await CompletePeerHandshakeAsync(peer, testToken);
            await SendPeerNodeAsync(peer, noise, BinaryNode.Create("success"), testToken);
            await ServeOneQueryAsync(peer, noise, testToken);
            await peer.CloseAsync(405, testToken);
        }, testToken);

        await connection.ConnectAsync(testToken);
        byte[] reply = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("last-words"), testToken);
        reply.Should().Equal(Encoding.ASCII.GetBytes("last-words"));

        await WaitForQueuedAsync(
            observed.States,
            e => e.State == ConnectionState.LoggedOut && e.StatusCode == 405,
            "logged_out with 405");

        fakeTime.Advance(TimeSpan.FromHours(1));
        await SettleAfterClockAdvanceAsync(testToken);

        observed.Pairs.Created.Should().Be(1, "a 405 is terminal — zero further reconnect attempts may be issued");
        observed.States.Any(e => e.State == ConnectionState.Connecting && e.Attempt > 0).Should().BeFalse(
            "no reconnect attempt may follow a terminal 405");
        observed.States.Count(e => e.State == ConnectionState.LoggedOut).Should().Be(1);
        connection.State.Should().Be(ConnectionState.LoggedOut);

        await connection.DisposeAsync();
        await peerTask;
    }

    [Fact]
    public async Task Drop405_fires_the_pairing_terminal_seam_exactly_once_with_ClientOutdated()
    {
        var fakeTime = new FakeTimeProvider();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken testToken = testCts.Token;
        var observed = new Observed();
        AccountId account = NewAccount("seam");
        await using var connection = new WaConnection(
            account,
            observed.Pairs.CreateClient,
            await CreateSeededPayloadsAsync(account, testToken),
            TestOptions(),
            fakeTime);
        connection.StateChanged += (_, e) => observed.States.Enqueue(e);
        connection.PairingTerminal += (_, e) => observed.Seam.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(observed.Pairs, testToken);
            NoiseTransport noise = await CompletePeerHandshakeAsync(peer, testToken);
            await SendPeerNodeAsync(peer, noise, BinaryNode.Create("success"), testToken);
            await ServeOneQueryAsync(peer, noise, testToken);
            await peer.CloseAsync(405, testToken);
        }, testToken);

        await connection.ConnectAsync(testToken);
        byte[] seamReply = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("before-drop"), testToken);
        seamReply.Should().Equal(Encoding.ASCII.GetBytes("before-drop"));
        await WaitForQueuedAsync(
            observed.States,
            e => e.State == ConnectionState.LoggedOut,
            "logged_out");

        fakeTime.Advance(TimeSpan.FromHours(1));
        await SettleAfterClockAdvanceAsync(testToken);

        observed.Seam.Should().ContainSingle(
            "the 405 drop must surface exactly one pairing-terminal signal");
        await SettleAfterClockAdvanceAsync(testToken);

        observed.Seam.Should().ContainSingle(
            "the 405 drop must surface exactly one pairing-terminal signal");
        observed.Seam.Single().Reason.Should().Be(
            DisconnectReason.ClientOutdated,
            "the seam carries the truthful terminal outcome for the pairing surface");

        await connection.DisposeAsync();
        await peerTask;
    }

    [Fact]
    public async Task Drop405_during_connecting_is_pairing_agnostic_terminal_without_open()
    {
        var fakeTime = new FakeTimeProvider();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken testToken = testCts.Token;
        var observed = new Observed();
        await using var connection = new WaConnection(
            NewAccount("early"),
            observed.Pairs.CreateClient,
            new StaticPayloads(),
            TestOptions(),
            fakeTime);
        connection.StateChanged += (_, e) => observed.States.Enqueue(e);
        connection.PairingTerminal += (_, e) => observed.Seam.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport doomed = await TakePeerAsync(observed.Pairs, testToken);
            await doomed.CloseAsync(405, testToken);
        }, testToken);

        Func<Task> act = () => connection.ConnectAsync(testToken);
        (await act.Should().ThrowAsync<SdkConnectionException>())
            .Which.Message.Should().Contain("logged out");

        observed.States.Any(e => e.State == ConnectionState.Open).Should().BeFalse();
        observed.States.Count(e => e.State == ConnectionState.LoggedOut).Should().Be(1);
        observed.States.Single(e => e.State == ConnectionState.LoggedOut).StatusCode.Should().Be(405);
        observed.Seam.Should().ContainSingle(
            "the seam behaves identically with no pairing attempt live — the connection layer only reports reasons");
        observed.Seam.Single().Reason.Should().Be(DisconnectReason.ClientOutdated);

        fakeTime.Advance(TimeSpan.FromHours(1));
        await SettleAfterClockAdvanceAsync(testToken);
        observed.Pairs.Created.Should().Be(1);
        connection.State.Should().Be(ConnectionState.LoggedOut);

        await connection.DisposeAsync();
        await peerTask;
    }

    [Fact]
    public async Task Drop503_still_retries_without_raising_the_seam()
    {
        var fakeTime = new FakeTimeProvider();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken testToken = testCts.Token;
        var observed = new Observed();
        AccountId account = NewAccount("transient");
        await using var connection = new WaConnection(
            account,
            observed.Pairs.CreateClient,
            await CreateSeededPayloadsAsync(account, testToken),
            TestOptions(),
            fakeTime);
        connection.StateChanged += (_, e) => observed.States.Enqueue(e);
        connection.PairingTerminal += (_, e) => observed.Seam.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport first = await TakePeerAsync(observed.Pairs, testToken);
            NoiseTransport firstNoise = await CompletePeerHandshakeAsync(first, testToken);
            await SendPeerNodeAsync(first, firstNoise, BinaryNode.Create("success"), testToken);
            await ServeOneQueryAsync(first, firstNoise, testToken);
            await first.CloseAsync(503, testToken);

            LoopbackFrameTransport second = await TakePeerAsync(observed.Pairs, testToken);
            NoiseTransport secondNoise = await CompletePeerHandshakeAsync(second, testToken);
            await SendPeerNodeAsync(second, secondNoise, BinaryNode.Create("success"), testToken);
            await ServeOneQueryAsync(second, secondNoise, testToken);
            await second.DisposeAsync();
        }, testToken);

        await connection.ConnectAsync(testToken);
        byte[] firstReply = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("before-drop"), testToken);
        firstReply.Should().Equal(Encoding.ASCII.GetBytes("before-drop"));

        await WaitForQueuedAsync(
            observed.States,
            e => e.State == ConnectionState.Closing && e.StatusCode == 503,
            "closing with 503");

        // The retry waits a full-jitter draw of at most the 1s base on the injected
        // clock — advancing past it fires the single scheduled reconnect, and only that one.
        fakeTime.Advance(TimeSpan.FromSeconds(2));
        await WaitForQueuedAsync(
            observed.States,
            e => e.State == ConnectionState.Open && observed.States.Count(s => s.State == ConnectionState.Open) >= 2,
            "second open");

        byte[] secondReply = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("after-drop"), testToken);
        secondReply.Should().Equal(Encoding.ASCII.GetBytes("after-drop"));

        observed.Pairs.Created.Should().Be(2, "a transient 503 keeps retrying");
        observed.Seam.Should().BeEmpty("transient codes never masquerade as pairing terminals");
        observed.States.Any(e => e.State == ConnectionState.LoggedOut).Should().BeFalse();

        await connection.DisposeAsync();
        await peerTask;
    }

    [Fact]
    public async Task FailureNode405_after_open_is_terminal_with_seam_signal()
    {
        var fakeTime = new FakeTimeProvider();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken testToken = testCts.Token;
        var observed = new Observed();
        AccountId account = NewAccount("node");
        await using var connection = new WaConnection(
            account,
            observed.Pairs.CreateClient,
            await CreateSeededPayloadsAsync(account, testToken),
            TestOptions(),
            fakeTime);
        connection.StateChanged += (_, e) => observed.States.Enqueue(e);
        connection.PairingTerminal += (_, e) => observed.Seam.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(observed.Pairs, testToken);
            NoiseTransport noise = await CompletePeerHandshakeAsync(peer, testToken);
            await SendPeerNodeAsync(peer, noise, BinaryNode.Create("success"), testToken);
            await ServeOneQueryAsync(peer, noise, testToken);
            await SendPeerNodeAsync(
                peer,
                noise,
                BinaryNode.Create("failure", new Dictionary<string, string> { ["reason"] = "405" }),
                testToken);
        }, testToken);

        await connection.ConnectAsync(testToken);
        byte[] nodeReply = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("before-drop"), testToken);
        nodeReply.Should().Equal(Encoding.ASCII.GetBytes("before-drop"));
        await WaitForQueuedAsync(
            observed.States,
            e => e.State == ConnectionState.LoggedOut && e.StatusCode == 405,
            "logged_out with 405 from the failure dispatch");

        fakeTime.Advance(TimeSpan.FromHours(1));
        await SettleAfterClockAdvanceAsync(testToken);

        observed.Pairs.Created.Should().Be(1, "the failure dispatch feeds the same terminal table");
        observed.Seam.Should().ContainSingle();
        observed.Seam.Single().Reason.Should().Be(DisconnectReason.ClientOutdated);

        await connection.DisposeAsync();
        await peerTask;
    }
}
