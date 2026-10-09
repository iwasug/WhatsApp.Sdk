using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Noise;
using Iwasug.WhatsApp.Sdk.Tests.Transport;
using Iwasug.WhatsApp.Sdk.Transport;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Connection;

/// <summary>
/// Tracer proofs (02-02): one production-quality vertical slice with zero sockets — WA
/// header → Noise skeleton handshake (real NSec X25519, fresh per attempt) → correlated
/// iq round-trip → open event, then an injected drop → autonomous reconnect with a fresh
/// handshake → open again, and 401 → logged_out exactly once with no retry.
/// </summary>
/// <remarks>
/// <para>
/// The scripted loopback peer stands in for the server (threat-model boundary: its bytes
/// are parsed by the same fail-closed rules as a real peer's). It speaks the skeleton
/// wire shape using the SDK's own <see cref="NoiseTransport"/> (mirrored keys) and the
/// router's internal envelope helpers — the single source of truth, so 02-03/02-04 wire
/// changes update both sides together. The peer's fixed test key is loopback-only
/// fixture material, never used outside these tests.
/// </para>
/// <para>
/// Spike fallbacks honored: F4 (no pre-auth iq exists) — the round-trip is an opaque
/// post-handshake-shaped query proving framing + id-correlation + timeout machinery.
/// </para>
/// </remarks>
public sealed class LoopbackRoundTripTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NoRetryWindow = TimeSpan.FromMilliseconds(300);

    private sealed class StaticPayloads : INoiseHandshakePayloads
    {
        private readonly string _hello;
        private readonly string _finish;

        internal StaticPayloads(string hello, string finish)
        {
            _hello = hello;
            _finish = finish;
        }

        public byte[] GetClientHello() => Encoding.ASCII.GetBytes(_hello);

        public byte[] GetClientFinish() => Encoding.ASCII.GetBytes(_finish);
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        internal readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Error)> Entries = new();

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception), exception));

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
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

    private sealed class PeerHandshakeResult
    {
        internal required byte[] ClientMessageA { get; init; }

        internal required byte[] ClientMessageC { get; init; }

        internal required NoiseTransport Transport { get; init; }
    }

    private static AccountId NewAccount(string hint) =>
        AccountId.Create($"loopback-{hint}-" + Guid.NewGuid().ToString("N"));

    private static ConnectionOptions TracerOptions() => new()
    {
        ConnectTimeoutMs = 5_000,
        QueryTimeoutMs = 5_000,
        RetryBaseDelayMs = 0,
        RetryMaxDelayMs = 0,
    };

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
        ReadOnlyMemory<byte>? frame = await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Peer transport closed unexpectedly.");

        return frame.Value.ToArray();
    }

    private static async Task<PeerHandshakeResult> CompletePeerHandshakeAsync(
        IFrameTransport peer,
        byte[] serverHello,
        CancellationToken cancellationToken)
    {
        byte[] header = await ReceiveOrThrowAsync(peer, cancellationToken).ConfigureAwait(false);
        NoiseHandshake.ValidateIntroHeader(header);

        // The peer is the responder: one production implementation speaks both
        // sides (no KDF or envelope duplication — the script drives the
        // internal responder role over the same spec core as the client).
        byte[] messageAFrame = await ReceiveOrThrowAsync(peer, cancellationToken).ConfigureAwait(false);
        using var responder = new NoiseHandshakeResponder(serverHello);
        byte[] messageA = WaFrame.Deframe(messageAFrame).ToArray();
        responder.ReadMessageA(messageA);
        byte[] messageB = responder.WriteMessageB();
        await peer.SendAsync(WaFrame.Frame(messageB), cancellationToken).ConfigureAwait(false);

        byte[] messageCFrame = await ReceiveOrThrowAsync(peer, cancellationToken).ConfigureAwait(false);
        byte[] messageC = WaFrame.Deframe(messageCFrame).ToArray();
        NoiseTransport transport = responder.ReadMessageC(messageC);
        return new PeerHandshakeResult
        {
            ClientMessageA = messageA,
            ClientMessageC = messageC,
            Transport = transport,
        };
    }

    private static async Task<(string Id, byte[] Body)> ReceivePeerQueryAsync(
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

        return (id, body);
    }

    private static async Task SendPeerReplyAsync(
        IFrameTransport peer,
        NoiseTransport noise,
        string id,
        byte[] body,
        CancellationToken cancellationToken)
    {
        byte[] response = QueryRouter.BuildFrame(id, body);
        byte[] encrypted = noise.Encrypt(response);
        byte[] flagged = new byte[encrypted.Length + 1];
        encrypted.CopyTo(flagged.AsSpan(1));
        await peer.SendAsync(WaFrame.Frame(flagged), cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunPeerEchoLoopAsync(
        IFrameTransport peer,
        NoiseTransport noise,
        Func<byte[], byte[]>? reply,
        int maxQueries,
        CancellationToken cancellationToken)
    {
        reply ??= body => body;
        int served = 0;
        while (served < maxQueries)
        {
            ReadOnlyMemory<byte>? frame = await peer.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                return;
            }

            ReadOnlySpan<byte> framed = WaFrame.Deframe(frame.Value.Span);
            byte[] plain = noise.Decrypt(framed.Slice(1));
            if (!QueryRouter.TryParseFrame(plain, out string? id, out byte[]? body) || id is null || body is null)
            {
                throw new InvalidOperationException("Peer received an unparsable query frame.");
            }

            byte[] response = QueryRouter.BuildFrame(id, reply(body));
            byte[] encrypted = noise.Encrypt(response);
            byte[] flagged = new byte[encrypted.Length + 1];
            encrypted.CopyTo(flagged.AsSpan(1));
            await peer.SendAsync(WaFrame.Frame(flagged), cancellationToken).ConfigureAwait(false);
            served++;
        }
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

    [Fact]
    public async Task HappyPath_header_handshake_query_open_over_loopback()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        await using var connection = new WaConnection(
            NewAccount("happy"),
            pairs.CreateClient,
            new StaticPayloads("client-hello", "client-finish"),
            TracerOptions());
        connection.StateChanged += (_, e) => states.Enqueue(e);

        Task<PeerHandshakeResult> peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            PeerHandshakeResult handshake = await CompletePeerHandshakeAsync(
                peer,
                Encoding.ASCII.GetBytes("server-hello"),
                testToken);
            await RunPeerEchoLoopAsync(peer, handshake.Transport, body => body, int.MaxValue, testToken);
            await peer.DisposeAsync();
            return handshake;
        }, testToken);

        await connection.ConnectAsync(testToken);
        await WaitForQueuedAsync(states, e => e.State == ConnectionState.Open, "open");

        byte[] reply = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("ping"), testToken);
        reply.Should().Equal(Encoding.ASCII.GetBytes("ping"));
        connection.State.Should().Be(ConnectionState.Open);

        ConnectionState[] kinds = states.Select(e => e.State).ToArray();
        kinds.Should().Equal(ConnectionState.Connecting, ConnectionState.Open);
        states.First(e => e.State == ConnectionState.Connecting).Attempt.Should().Be(0);

        await connection.DisposeAsync();
        PeerHandshakeResult peerHandshake = await peerTask;
        peerHandshake.ClientMessageA.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Injected428Drop_reconnects_with_fresh_handshake_and_opens_again()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        var messageAFrames = new ConcurrentQueue<byte[]>();
        await using var connection = new WaConnection(
            NewAccount("reconnect"),
            pairs.CreateClient,
            new StaticPayloads("client-hello", "client-finish"),
            TracerOptions());
        connection.StateChanged += (_, e) => states.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport first = await TakePeerAsync(pairs, testToken);
            PeerHandshakeResult firstHandshake = await CompletePeerHandshakeAsync(
                first,
                Encoding.ASCII.GetBytes("server-hello"),
                testToken);
            messageAFrames.Enqueue(firstHandshake.ClientMessageA);
            await RunPeerEchoLoopAsync(first, firstHandshake.Transport, body => body, 2, testToken);
            await first.CloseAsync(428, testToken);

            LoopbackFrameTransport second = await TakePeerAsync(pairs, testToken);
            PeerHandshakeResult secondHandshake = await CompletePeerHandshakeAsync(
                second,
                Encoding.ASCII.GetBytes("server-hello"),
                testToken);
            messageAFrames.Enqueue(secondHandshake.ClientMessageA);
            await RunPeerEchoLoopAsync(second, secondHandshake.Transport, body => body, int.MaxValue, testToken);
            await second.DisposeAsync();
        }, testToken);

        await connection.ConnectAsync(testToken);
        byte[] firstReply = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("before-drop"), testToken);
        firstReply.Should().Equal(Encoding.ASCII.GetBytes("before-drop"));

        await WaitForQueuedAsync(states, e => e.State == ConnectionState.Open && states.Count(s => s.State == ConnectionState.Open) >= 2, "second open");

        byte[] secondReply = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("after-drop"), testToken);
        secondReply.Should().Equal(Encoding.ASCII.GetBytes("after-drop"));

        byte[][] frames = messageAFrames.ToArray();
        frames.Should().HaveCount(2);
        frames[0].SequenceEqual(frames[1]).Should().BeFalse("each handshake must mint a fresh ephemeral — never reuse e");

        ConnectionStateChangedEventArgs[] connectings = states.Where(e => e.State == ConnectionState.Connecting).ToArray();
        connectings.Should().HaveCount(2);
        connectings[0].Attempt.Should().Be(0);
        connectings[1].Attempt.Should().Be(1);
        states.Count(e => e.State == ConnectionState.Open).Should().Be(2);
        ConnectionStateChangedEventArgs closing = states.Single(e => e.State == ConnectionState.Closing);
        closing.StatusCode.Should().Be(428);
        pairs.Created.Should().Be(2);

        await connection.DisposeAsync();
        await peerTask;
    }

    [Fact]
    public async Task Drop401_after_open_emits_logged_out_exactly_once_and_never_retries()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        await using var connection = new WaConnection(
            NewAccount("terminal"),
            pairs.CreateClient,
            new StaticPayloads("client-hello", "client-finish"),
            TracerOptions());
        connection.StateChanged += (_, e) => states.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            PeerHandshakeResult handshake = await CompletePeerHandshakeAsync(
                peer,
                Encoding.ASCII.GetBytes("server-hello"),
                testToken);
            await RunPeerEchoLoopAsync(peer, handshake.Transport, body => body, 2, testToken);
            await peer.CloseAsync(401, testToken);
        }, testToken);

        await connection.ConnectAsync(testToken);
        byte[] reply = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("last-words"), testToken);
        reply.Should().Equal(Encoding.ASCII.GetBytes("last-words"));

        await WaitForQueuedAsync(states, e => e.State == ConnectionState.LoggedOut, "logged_out");

        await Task.Delay(NoRetryWindow, testToken);
        states.Count(e => e.State == ConnectionState.LoggedOut).Should().Be(1);
        states.Any(e => e.State == ConnectionState.Connecting && e.Attempt > 0).Should().BeFalse();
        pairs.Created.Should().Be(1);
        connection.State.Should().Be(ConnectionState.LoggedOut);

        Func<Task> lateQuery = () => connection.SendQueryAsync(Encoding.ASCII.GetBytes("too-late"), testToken).AsTask();
        (await lateQuery.Should().ThrowAsync<SdkConnectionException>())
            .Which.Message.Should().Contain("not open");

        await connection.DisposeAsync();
        await peerTask;
    }

    [Fact]
    public async Task Drop515_during_connecting_restarts_immediately_with_counter_reset()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        await using var connection = new WaConnection(
            NewAccount("restart"),
            pairs.CreateClient,
            new StaticPayloads("client-hello", "client-finish"),
            TracerOptions());
        connection.StateChanged += (_, e) => states.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport doomed = await TakePeerAsync(pairs, testToken);
            await doomed.CloseAsync(515, testToken);

            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            PeerHandshakeResult handshake = await CompletePeerHandshakeAsync(
                peer,
                Encoding.ASCII.GetBytes("server-hello"),
                testToken);
            await RunPeerEchoLoopAsync(peer, handshake.Transport, body => body, int.MaxValue, testToken);
            await peer.DisposeAsync();
        }, testToken);

        await connection.ConnectAsync(testToken);
        connection.State.Should().Be(ConnectionState.Open);

        ConnectionStateChangedEventArgs[] connectings = states.Where(e => e.State == ConnectionState.Connecting).ToArray();
        connectings.Should().HaveCount(2);
        connectings[0].Attempt.Should().Be(0);
        connectings[1].Attempt.Should().Be(0, "515 restarts immediately with the backoff counter reset");
        states.Count(e => e.State == ConnectionState.Open).Should().Be(1);
        pairs.Created.Should().Be(2);

        await connection.DisposeAsync();
        await peerTask;
    }

    [Fact]
    public async Task Drop401_during_connecting_surfaces_logged_out_without_open()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        await using var connection = new WaConnection(
            NewAccount("early-terminal"),
            pairs.CreateClient,
            new StaticPayloads("client-hello", "client-finish"),
            TracerOptions());
        connection.StateChanged += (_, e) => states.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport doomed = await TakePeerAsync(pairs, testToken);
            await doomed.CloseAsync(401, testToken);
        }, testToken);

        Func<Task> act = () => connection.ConnectAsync(testToken);
        (await act.Should().ThrowAsync<SdkConnectionException>())
            .Which.Message.Should().Contain("logged out");

        states.Any(e => e.State == ConnectionState.Open).Should().BeFalse();
        states.Count(e => e.State == ConnectionState.LoggedOut).Should().Be(1);
        pairs.Created.Should().Be(1);
        connection.State.Should().Be(ConnectionState.LoggedOut);

        await connection.DisposeAsync();
        await peerTask;
    }

    [Fact]
    public async Task Cancel_mid_handshake_aborts_with_unwrapped_cancellation_and_no_retry()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        var gotMessageA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new WaConnection(
            NewAccount("cancel"),
            pairs.CreateClient,
            new StaticPayloads("client-hello", "client-finish"),
            TracerOptions());
        connection.StateChanged += (_, e) => states.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            await ReceiveOrThrowAsync(peer, testToken);
            await ReceiveOrThrowAsync(peer, testToken);
            gotMessageA.TrySetResult();
            ReadOnlyMemory<byte>? next = await peer.ReceiveAsync(testToken);
            next.Should().BeNull("abort disposes the attempt transport — no partial transport is retained");
        }, testToken);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        Task connectTask = connection.ConnectAsync(connectCts.Token);
        await gotMessageA.Task.WaitAsync(testToken);
        connectCts.Cancel();

        Func<Task> act = () => connectTask;
        (await act.Should().ThrowAsync<OperationCanceledException>())
            .Which.Should().NotBeAssignableTo<SdkException>("cancellation passes through unwrapped, never wrapped or retried");

        await WaitForQueuedAsync(states, e => e.State == ConnectionState.Closing, "abort closing");
        pairs.Created.Should().Be(1);
        connection.State.Should().Be(ConnectionState.Closing);
        states.Any(e => e.State == ConnectionState.Open).Should().BeFalse();

        await connection.DisposeAsync();
        await peerTask;
    }

    [Fact]
    public async Task Drop440_surfaces_without_retry_and_without_logged_out()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        await using var connection = new WaConnection(
            NewAccount("replaced"),
            pairs.CreateClient,
            new StaticPayloads("client-hello", "client-finish"),
            TracerOptions());
        connection.StateChanged += (_, e) => states.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            PeerHandshakeResult handshake = await CompletePeerHandshakeAsync(
                peer,
                Encoding.ASCII.GetBytes("server-hello"),
                testToken);
            await RunPeerEchoLoopAsync(peer, handshake.Transport, body => body, 2, testToken);
            await peer.CloseAsync(440, testToken);
        }, testToken);

        await connection.ConnectAsync(testToken);
        byte[] reply = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("last"), testToken);
        reply.Should().Equal(Encoding.ASCII.GetBytes("last"));

        await WaitForQueuedAsync(
            states,
            e => e.State == ConnectionState.Closing && e.StatusCode == 440,
            "closing with 440");

        await Task.Delay(NoRetryWindow, testToken);
        states.Any(e => e.State == ConnectionState.LoggedOut).Should().BeFalse("440 surfaces account-replaced and stops — it is not logged-out");
        pairs.Created.Should().Be(1, "immediate reconnect would start a kick-war between devices");
        connection.State.Should().Be(ConnectionState.Closing);

        Func<Task> lateQuery = () => connection.SendQueryAsync(Encoding.ASCII.GetBytes("too-late"), testToken).AsTask();
        (await lateQuery.Should().ThrowAsync<SdkConnectionException>())
            .Which.Message.Should().Contain("not open");

        await connection.DisposeAsync();
        await peerTask;
    }

    [Fact]
    public async Task Handshake_rejects_dictionary_version_mismatch_fail_closed()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        var logger = new CaptureLogger<WaConnection>();
        await using var connection = new WaConnection(
            NewAccount("dict-mismatch"),
            pairs.CreateClient,
            new StaticPayloads("client-hello", "client-finish"),
            TracerOptions(),
            timeProvider: null,
            logger: logger);
        connection.StateChanged += (_, e) => states.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            await ReceiveOrThrowAsync(peer, testToken);
            await ReceiveOrThrowAsync(peer, testToken);

            KeyCreationParameters keyParameters = default;
            using var peerKey = Key.Create(KeyAgreementAlgorithm.X25519, keyParameters);
            byte[] peerEphemeral = peerKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
            byte[] hello = Encoding.ASCII.GetBytes("server-hello");
            byte[] messageB = new byte[4 + 32 + 2 + hello.Length];
            messageB[0] = 0x57;
            messageB[1] = 0x41;
            messageB[2] = 0x06;
            messageB[3] = 0x02;
            peerEphemeral.CopyTo(messageB.AsSpan(4));
            BinaryPrimitives.WriteUInt16BigEndian(messageB.AsSpan(36, 2), (ushort)hello.Length);
            hello.CopyTo(messageB.AsSpan(38));
            await peer.SendAsync(WaFrame.Frame(messageB), testToken);

            ReadOnlyMemory<byte>? next = await peer.ReceiveAsync(testToken);
            next.Should().BeNull("the client aborts the handshake — no message C follows a rejected B");
        }, testToken);

        Func<Task> act = () => connection.ConnectAsync(testToken);
        (await act.Should().ThrowAsync<SdkConnectionException>())
            .Which.Message.Should().Contain("dictionary version mismatch");

        states.Any(e => e.State == ConnectionState.Open).Should().BeFalse();
        pairs.Created.Should().Be(1, "a protocol violation halts the loop — a hostile peer is not a network flap");
        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Error && e.Error is SdkConnectionException && e.Error.Message.Contains("dictionary version mismatch"));

        await connection.DisposeAsync();
        await peerTask;
    }

    [Fact]
    public async Task SendQueryAsync_times_out_when_peer_silent()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        var logger = new CaptureLogger<WaConnection>();
        var options = new ConnectionOptions
        {
            ConnectTimeoutMs = 5_000,
            QueryTimeoutMs = 200,
            RetryBaseDelayMs = 0,
            RetryMaxDelayMs = 0,
        };
        await using var connection = new WaConnection(
            NewAccount("quiet-peer"),
            pairs.CreateClient,
            new StaticPayloads("client-hello", "client-finish"),
            options,
            timeProvider: null,
            logger: logger);
        connection.StateChanged += (_, e) => states.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            PeerHandshakeResult handshake = await CompletePeerHandshakeAsync(
                peer,
                Encoding.ASCII.GetBytes("server-hello"),
                testToken);
            (string probeId, byte[] probeBody) = await ReceivePeerQueryAsync(peer, handshake.Transport, testToken);
            await SendPeerReplyAsync(peer, handshake.Transport, probeId, probeBody, testToken);

            (string id, byte[] body) = await ReceivePeerQueryAsync(peer, handshake.Transport, testToken);
            await Task.Delay(800, testToken);
            await SendPeerReplyAsync(peer, handshake.Transport, id, body, testToken);

            ReadOnlyMemory<byte>? next = await peer.ReceiveAsync(testToken);
            next.Should().BeNull();
        }, testToken);

        await connection.ConnectAsync(testToken);

        Func<Task> act = () => connection.SendQueryAsync(Encoding.ASCII.GetBytes("hello?"), testToken).AsTask();
        (await act.Should().ThrowAsync<SdkConnectionException>())
            .Which.Message.Should().Contain("timed out");
        connection.State.Should().Be(ConnectionState.Open);

        await WaitForQueuedAsync(
            states,
            e => e.State == ConnectionState.Open,
            "open before late-reply discard");
        await Task.Delay(900, testToken);
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Debug && e.Message.Contains("discarded"));

        await connection.DisposeAsync();
        await peerTask;
    }

    [Fact]
    public void Handshake_dispose_blocks_use_and_second_dispose_is_safe()
    {
        var handshake = new NoiseHandshake(new StaticPayloads("hello", "finish"));
        byte[] messageA = handshake.WriteMessageA();
        messageA.Should().NotBeEmpty();

        Assert.Throws<InvalidOperationException>(() => handshake.WriteMessageA());
        Assert.Throws<InvalidOperationException>(() => handshake.WriteMessageC());
        Assert.Throws<SdkConnectionException>(() => handshake.ReadMessageB(ReadOnlyMemory<byte>.Empty));

        handshake.Dispose();
        Assert.Throws<ObjectDisposedException>(() => handshake.WriteMessageA());
        Assert.Throws<ObjectDisposedException>(() => handshake.WriteMessageC());
        Assert.Throws<ObjectDisposedException>(() => handshake.ReadMessageB(new byte[64]));
        handshake.Dispose();
    }

    [Fact]
    public void Transport_dispose_blocks_use_and_second_dispose_is_safe()
    {
        byte[] initiatorWrite = new byte[32];
        byte[] initiatorRead = new byte[32];
        RandomNumberGenerator.Fill(initiatorWrite);
        RandomNumberGenerator.Fill(initiatorRead);
        using var initiator = new NoiseTransport(initiatorWrite, initiatorRead);
        using var responder = new NoiseTransport(initiatorRead, initiatorWrite);

        byte[] packet = initiator.Encrypt("hello"u8.ToArray());
        responder.Decrypt(packet).Should().Equal("hello"u8.ToArray());

        byte[] tampered = (byte[])packet.Clone();
        tampered[20] ^= 0xFF;
        Assert.Throws<SdkTransportException>(() => responder.Decrypt(tampered));
        Assert.Throws<SdkTransportException>(() => responder.Decrypt(new byte[10]));

        initiator.Dispose();
        Assert.Throws<ObjectDisposedException>(() => initiator.Encrypt("x"u8.ToArray()));
        Assert.Throws<ObjectDisposedException>(() => initiator.Decrypt(packet));
        initiator.Dispose();

        Assert.Throws<SdkFormatException>(() => new NoiseTransport(new byte[16], new byte[32]));
    }
}
