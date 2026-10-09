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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Connection;

/// <summary>
/// Close-gate proofs (02-05) over the in-memory loopback fake: per-code lifecycle
/// (401/411 terminal, 408/428 retry, 515 restart-with-reset, 440 stop), single-flight
/// reconnect under parallel drops, exactly-once terminal events with no post-terminal
/// traffic, FakeTimeProvider backoff holds, the attempt-timeout sub-cause log, and the
/// minimal store-seam transcript proof. Real-socket coverage lives in
/// <see cref="LoopbackTests"/> only.
/// </summary>
public sealed class ConnectionStateTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SettleWindow = TimeSpan.FromMilliseconds(300);

    // Phase-3 open gate (03-01): every connection below opens on <success> plus an
    // observable credential row, so each peer script seeds a row and sends <success>
    // after the handshake. Registration defaults are loopback-only fixture material.
    private static readonly RegistrationDefaults SeededRegistrationDefaults = new(
        new byte[32], 7, new byte[32], new byte[64], 42, "android", 2, 3000, 1043857760);

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
        return new HandshakePayloadProvider(account, store, SeededRegistrationDefaults);
    }

    private static async Task SendPeerSuccessAsync(
        IFrameTransport peer,
        NoiseTransport noise,
        CancellationToken cancellationToken)
    {
        byte[] nodeBytes = BinaryNodeCodec.Encode(BinaryNode.Create("success"));
        byte[] encrypted = noise.Encrypt(nodeBytes);
        byte[] flagged = new byte[encrypted.Length + 1];
        encrypted.CopyTo(flagged.AsSpan(1));
        await peer.SendAsync(WaFrame.Frame(flagged), cancellationToken).ConfigureAwait(false);
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
        internal required NoiseTransport Transport { get; init; }
    }

    private static AccountId NewAccount(string hint) =>
        AccountId.Create($"state-{hint}-" + Guid.NewGuid().ToString("N"));

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
            await Task.Delay(5, cancellationToken);
        }

        return peer!;
    }

    private static async Task<byte[]> ReceiveOrThrowAsync(IFrameTransport transport, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte>? frame = await transport.ReceiveAsync(cancellationToken) ?? throw new InvalidOperationException("Peer transport closed unexpectedly.");

        return frame.Value.ToArray();
    }

    private static async Task<PeerHandshakeResult> CompletePeerHandshakeAsync(
        IFrameTransport peer,
        byte[] serverHello,
        CancellationToken cancellationToken)
    {
        byte[] header = await ReceiveOrThrowAsync(peer, cancellationToken);
        NoiseHandshake.ValidateIntroHeader(header);

        byte[] messageAFrame = await ReceiveOrThrowAsync(peer, cancellationToken);
        using var responder = new NoiseHandshakeResponder(serverHello);
        byte[] messageA = WaFrame.Deframe(messageAFrame).ToArray();
        responder.ReadMessageA(messageA);
        byte[] messageB = responder.WriteMessageB();
        await peer.SendAsync(WaFrame.Frame(messageB), cancellationToken);

        byte[] messageCFrame = await ReceiveOrThrowAsync(peer, cancellationToken);
        byte[] messageC = WaFrame.Deframe(messageCFrame).ToArray();
        NoiseTransport transport = responder.ReadMessageC(messageC);
        return new PeerHandshakeResult { Transport = transport };
    }

    private static async Task<(string Id, byte[] Body)> ReceivePeerQueryAsync(
        IFrameTransport peer,
        NoiseTransport noise,
        CancellationToken cancellationToken)
    {
        byte[] encrypted = await ReceiveOrThrowAsync(peer, cancellationToken);
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
        await peer.SendAsync(WaFrame.Frame(flagged), cancellationToken);
    }

    private static async Task RunPeerEchoLoopAsync(
        IFrameTransport peer,
        NoiseTransport noise,
        int maxQueries,
        CancellationToken cancellationToken)
    {
        int served = 0;
        while (served < maxQueries)
        {
            ReadOnlyMemory<byte>? frame = await peer.ReceiveAsync(cancellationToken);
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

            byte[] response = QueryRouter.BuildFrame(id, body);
            byte[] encrypted = noise.Encrypt(response);
            byte[] flagged = new byte[encrypted.Length + 1];
            encrypted.CopyTo(flagged.AsSpan(1));
            await peer.SendAsync(WaFrame.Frame(flagged), cancellationToken);
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

            await Task.Delay(10, timeout.Token);
        }

        throw new TimeoutException($"Timed out waiting for connection event: {what}.");
    }

    [Fact]
    public async Task Reconnecting_sequence_emits_connecting_open_closing_connecting_open()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        var logger = new CaptureLogger<WaConnection>();
        ConnectionOptions options = TracerOptions();
        AccountId sequenceAccount = NewAccount("sequence");
        await using var connection = new WaConnection(
            sequenceAccount,
            pairs.CreateClient,
            await CreateSeededPayloadsAsync(sequenceAccount, testToken),
            options,
            timeProvider: null,
            logger: logger);
        connection.StateChanged += (_, e) => states.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport first = await TakePeerAsync(pairs, testToken);
            PeerHandshakeResult firstHandshake = await CompletePeerHandshakeAsync(
                first, Encoding.ASCII.GetBytes("server-hello"), testToken);
            using (firstHandshake.Transport)
            {
                await SendPeerSuccessAsync(first, firstHandshake.Transport, testToken);
                await RunPeerEchoLoopAsync(first, firstHandshake.Transport, 1, testToken);
            }

            await first.CloseAsync(428, testToken);

            LoopbackFrameTransport second = await TakePeerAsync(pairs, testToken);
            PeerHandshakeResult secondHandshake = await CompletePeerHandshakeAsync(
                second, Encoding.ASCII.GetBytes("server-hello"), testToken);
            using (secondHandshake.Transport)
            {
                await SendPeerSuccessAsync(second, secondHandshake.Transport, testToken);
                await RunPeerEchoLoopAsync(second, secondHandshake.Transport, int.MaxValue, testToken);
            }

            await second.DisposeAsync();
        }, testToken);

        await connection.ConnectAsync(testToken);
        byte[] firstReply = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("before-drop"), testToken);
        firstReply.Should().Equal(Encoding.ASCII.GetBytes("before-drop"));

        await WaitForQueuedAsync(states, e => e.State == ConnectionState.Open && states.Count(s => s.State == ConnectionState.Open) >= 2, "second open");

        byte[] secondReply = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("after-drop"), testToken);
        secondReply.Should().Equal(Encoding.ASCII.GetBytes("after-drop"));

        // CONN-05 full cycle across one drop, with the Closing payload carrying the
        // attempt depth and last reason (02-RESEARCH Open Question 4).
        states.Select(e => e.State).Should().Equal(
            ConnectionState.Connecting, ConnectionState.Open, ConnectionState.Closing,
            ConnectionState.Connecting, ConnectionState.Open);
        ConnectionStateChangedEventArgs closing = states.Single(e => e.State == ConnectionState.Closing);
        closing.StatusCode.Should().Be(428);
        closing.Attempt.Should().Be(0, "open resets the failure chain, so the post-open drop carries depth 0");
        ConnectionStateChangedEventArgs[] connectings = states.Where(e => e.State == ConnectionState.Connecting).ToArray();
        connectings[0].Attempt.Should().Be(0);
        connectings[1].Attempt.Should().Be(1);

        // Per-account logs carry the same payload: state, code, and attempt depth.
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Closing") && e.Message.Contains("428"));
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Information && e.Message.Contains("Reconnecting") && e.Message.Contains("428"));

        await connection.DisposeAsync();
        await peerTask;
    }

    [Fact]
    public async Task Drop401_after_open_emits_logged_out_exactly_once_with_no_post_terminal_events()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        AccountId terminalAccount = NewAccount("terminal-401");
        await using var connection = new WaConnection(
            terminalAccount,
            pairs.CreateClient,
            await CreateSeededPayloadsAsync(terminalAccount, testToken),
            TracerOptions());
        connection.StateChanged += (_, e) => states.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            PeerHandshakeResult handshake = await CompletePeerHandshakeAsync(
                peer, Encoding.ASCII.GetBytes("server-hello"), testToken);
            using (handshake.Transport)
            {
                await SendPeerSuccessAsync(peer, handshake.Transport, testToken);
                await RunPeerEchoLoopAsync(peer, handshake.Transport, 1, testToken);
            }

            await peer.CloseAsync(401, testToken);
        }, testToken);

        await connection.ConnectAsync(testToken);
        byte[] reply = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("last-words"), testToken);
        reply.Should().Equal(Encoding.ASCII.GetBytes("last-words"));

        await WaitForQueuedAsync(states, e => e.State == ConnectionState.LoggedOut, "logged_out");
        await Task.Delay(SettleWindow, testToken);

        states.Count(e => e.State == ConnectionState.LoggedOut).Should().Be(1);
        states.Single(e => e.State == ConnectionState.LoggedOut).StatusCode.Should().Be(401);
        states.Last().State.Should().Be(ConnectionState.LoggedOut, "no post-terminal events may follow the terminal");
        states.Any(e => e.State == ConnectionState.Connecting && e.Attempt > 0).Should().BeFalse();
        pairs.Created.Should().Be(1, "terminal never schedules a retry");
        connection.State.Should().Be(ConnectionState.LoggedOut);

        await connection.DisposeAsync();
        await peerTask;
    }

    [Fact]
    public async Task Drop440_stops_in_closing_without_retry_or_logged_out()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        AccountId replacedAccount = NewAccount("replaced");
        await using var connection = new WaConnection(
            replacedAccount,
            pairs.CreateClient,
            await CreateSeededPayloadsAsync(replacedAccount, testToken),
            TracerOptions());
        connection.StateChanged += (_, e) => states.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            PeerHandshakeResult handshake = await CompletePeerHandshakeAsync(
                peer, Encoding.ASCII.GetBytes("server-hello"), testToken);
            using (handshake.Transport)
            {
                await SendPeerSuccessAsync(peer, handshake.Transport, testToken);
                await RunPeerEchoLoopAsync(peer, handshake.Transport, 1, testToken);
            }

            await peer.CloseAsync(440, testToken);
        }, testToken);

        await connection.ConnectAsync(testToken);
        byte[] reply = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("last"), testToken);
        reply.Should().Equal(Encoding.ASCII.GetBytes("last"));

        await WaitForQueuedAsync(
            states, e => e.State == ConnectionState.Closing && e.StatusCode == 440, "closing with 440");
        await Task.Delay(SettleWindow, testToken);

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
    public async Task Drop411_is_terminal_like_401()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        AccountId outdatedAccount = NewAccount("outdated");
        await using var connection = new WaConnection(
            outdatedAccount,
            pairs.CreateClient,
            await CreateSeededPayloadsAsync(outdatedAccount, testToken),
            TracerOptions());
        connection.StateChanged += (_, e) => states.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            PeerHandshakeResult handshake = await CompletePeerHandshakeAsync(
                peer, Encoding.ASCII.GetBytes("server-hello"), testToken);
            using (handshake.Transport)
            {
                await SendPeerSuccessAsync(peer, handshake.Transport, testToken);
                await RunPeerEchoLoopAsync(peer, handshake.Transport, 1, testToken);
            }

            await peer.CloseAsync(411, testToken);
        }, testToken);

        await connection.ConnectAsync(testToken);
        byte[] reply = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("last"), testToken);
        reply.Should().Equal(Encoding.ASCII.GetBytes("last"));

        await WaitForQueuedAsync(states, e => e.State == ConnectionState.LoggedOut, "logged_out");
        await Task.Delay(SettleWindow, testToken);

        states.Count(e => e.State == ConnectionState.LoggedOut).Should().Be(1);
        states.Single(e => e.State == ConnectionState.LoggedOut).StatusCode.Should().Be(411);
        pairs.Created.Should().Be(1, "411 needs a deploy with a bumped version, not retries");
        connection.State.Should().Be(ConnectionState.LoggedOut);

        await connection.DisposeAsync();
        await peerTask;
    }

    [Fact]
    public async Task Drops_428_then_515_restart_with_counter_reset_across_cycles()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        AccountId retryAccount = NewAccount("retry-then-restart");
        await using var connection = new WaConnection(
            retryAccount,
            pairs.CreateClient,
            await CreateSeededPayloadsAsync(retryAccount, testToken),
            TracerOptions());
        connection.StateChanged += (_, e) => states.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport first = await TakePeerAsync(pairs, testToken);
            PeerHandshakeResult firstHandshake = await CompletePeerHandshakeAsync(
                first, Encoding.ASCII.GetBytes("server-hello"), testToken);
            using (firstHandshake.Transport)
            {
                await SendPeerSuccessAsync(first, firstHandshake.Transport, testToken);
                await RunPeerEchoLoopAsync(first, firstHandshake.Transport, 1, testToken);
            }

            await first.CloseAsync(428, testToken);

            LoopbackFrameTransport second = await TakePeerAsync(pairs, testToken);
            PeerHandshakeResult secondHandshake = await CompletePeerHandshakeAsync(
                second, Encoding.ASCII.GetBytes("server-hello"), testToken);
            using (secondHandshake.Transport)
            {
                await SendPeerSuccessAsync(second, secondHandshake.Transport, testToken);
                await RunPeerEchoLoopAsync(second, secondHandshake.Transport, 1, testToken);
            }

            await second.CloseAsync(515, testToken);

            LoopbackFrameTransport third = await TakePeerAsync(pairs, testToken);
            PeerHandshakeResult thirdHandshake = await CompletePeerHandshakeAsync(
                third, Encoding.ASCII.GetBytes("server-hello"), testToken);
            using (thirdHandshake.Transport)
            {
                await SendPeerSuccessAsync(third, thirdHandshake.Transport, testToken);
                await RunPeerEchoLoopAsync(third, thirdHandshake.Transport, int.MaxValue, testToken);
            }

            await third.DisposeAsync();
        }, testToken);

        await connection.ConnectAsync(testToken);
        byte[] firstReply = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("one"), testToken);
        firstReply.Should().Equal(Encoding.ASCII.GetBytes("one"));

        // The second attempt must open before its peer closes: the peer only closes
        // after serving the explicit query, so the open is never raced by the drop.
        await WaitForQueuedAsync(
            states, e => e.State == ConnectionState.Open && states.Count(s => s.State == ConnectionState.Open) >= 2, "second open");
        byte[] secondReply = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("two"), testToken);
        secondReply.Should().Equal(Encoding.ASCII.GetBytes("two"));

        await WaitForQueuedAsync(
            states, e => e.State == ConnectionState.Open && states.Count(s => s.State == ConnectionState.Open) >= 3, "third open");

        byte[] thirdReply = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("three"), testToken);
        thirdReply.Should().Equal(Encoding.ASCII.GetBytes("three"));

        // 428 consumes depth 1; open resets; 515 restarts without consuming depth.
        ConnectionStateChangedEventArgs[] connectings = states.Where(e => e.State == ConnectionState.Connecting).ToArray();
        connectings.Select(e => e.Attempt).Should().Equal(0, 1, 0);
        ConnectionStateChangedEventArgs[] closings = states.Where(e => e.State == ConnectionState.Closing).ToArray();
        closings.Select(e => (e.StatusCode, e.Attempt)).Should().Equal((428, 0), (515, 0));
        states.Count(e => e.State == ConnectionState.Open).Should().Be(3);
        pairs.Created.Should().Be(3);

        await connection.DisposeAsync();
        await peerTask;
    }

    [Fact]
    public async Task Parallel_queries_during_drop_collapse_to_single_reconnect_attempt()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        int peerReceived = 0;
        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AccountId parallelAccount = NewAccount("parallel-drop");
        await using var connection = new WaConnection(
            parallelAccount,
            pairs.CreateClient,
            await CreateSeededPayloadsAsync(parallelAccount, testToken),
            TracerOptions());
        connection.StateChanged += (_, e) => states.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport first = await TakePeerAsync(pairs, testToken);
            PeerHandshakeResult firstHandshake = await CompletePeerHandshakeAsync(
                first, Encoding.ASCII.GetBytes("server-hello"), testToken);

            // Send <success> so the connection opens, then hold 8 queries without
            // replying: all 8 must fail as connection-lost on the injected drop.
            await SendPeerSuccessAsync(first, firstHandshake.Transport, testToken);
            for (int i = 0; i < 8; i++)
            {
                _ = await ReceivePeerQueryAsync(first, firstHandshake.Transport, testToken);
                if (Interlocked.Increment(ref peerReceived) == 8)
                {
                    allReceived.TrySetResult();
                }
            }

            firstHandshake.Transport.Dispose();
            await first.CloseAsync(428, testToken);

            LoopbackFrameTransport second = await TakePeerAsync(pairs, testToken);
            PeerHandshakeResult secondHandshake = await CompletePeerHandshakeAsync(
                second, Encoding.ASCII.GetBytes("server-hello"), testToken);
            using (secondHandshake.Transport)
            {
                await SendPeerSuccessAsync(second, secondHandshake.Transport, testToken);
                await RunPeerEchoLoopAsync(second, secondHandshake.Transport, int.MaxValue, testToken);
            }

            await second.DisposeAsync();
        }, testToken);

        await connection.ConnectAsync(testToken);

        Task<byte[]>[] queries = Enumerable.Range(0, 8)
            .Select(i => connection.SendQueryAsync(Encoding.ASCII.GetBytes($"parallel-{i}"), testToken).AsTask())
            .ToArray();
        await allReceived.Task.WaitAsync(testToken);

        await WaitForQueuedAsync(
            states, e => e.State == ConnectionState.Open && states.Count(s => s.State == ConnectionState.Open) >= 2, "second open");

        // Every in-flight query completes as connection-lost (typed), never silently.
        foreach (Task<byte[]> pending in queries)
        {
            Func<Task> observe = () => pending;
            (await observe.Should().ThrowAsync<SdkConnectionException>()).WithMessage("*closing*");
        }

        pairs.Created.Should().Be(2, "concurrent triggers collapse to one reconnect attempt");
        byte[] afterReply = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("after"), testToken);
        afterReply.Should().Equal(Encoding.ASCII.GetBytes("after"));

        await connection.DisposeAsync();
        await peerTask;
    }

    [Fact]
    public async Task Backoff_window_holds_reconnect_until_fake_time_advances()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        var fake = new FakeTimeProvider();
        var options = new ConnectionOptions
        {
            ConnectTimeoutMs = 5_000,
            QueryTimeoutMs = 5_000,
            RetryBaseDelayMs = 2_000,
            RetryMaxDelayMs = 8_000,
        };
        AccountId backoffAccount = NewAccount("backoff-hold");
        await using var connection = new WaConnection(
            backoffAccount,
            pairs.CreateClient,
            await CreateSeededPayloadsAsync(backoffAccount, testToken),
            options,
            fake);
        connection.StateChanged += (_, e) => states.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport first = await TakePeerAsync(pairs, testToken);
            PeerHandshakeResult firstHandshake = await CompletePeerHandshakeAsync(
                first, Encoding.ASCII.GetBytes("server-hello"), testToken);
            using (firstHandshake.Transport)
            {
                await SendPeerSuccessAsync(first, firstHandshake.Transport, testToken);
                await RunPeerEchoLoopAsync(first, firstHandshake.Transport, 1, testToken);
            }

            await first.CloseAsync(428, testToken);

            LoopbackFrameTransport second = await TakePeerAsync(pairs, testToken);
            PeerHandshakeResult secondHandshake = await CompletePeerHandshakeAsync(
                second, Encoding.ASCII.GetBytes("server-hello"), testToken);
            using (secondHandshake.Transport)
            {
                await SendPeerSuccessAsync(second, secondHandshake.Transport, testToken);
                await RunPeerEchoLoopAsync(second, secondHandshake.Transport, int.MaxValue, testToken);
            }

            await second.DisposeAsync();
        }, testToken);

        await connection.ConnectAsync(testToken);
        byte[] firstReply = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("before"), testToken);
        firstReply.Should().Equal(Encoding.ASCII.GetBytes("before"));

        await WaitForQueuedAsync(states, e => e.State == ConnectionState.Closing && e.StatusCode == 428, "closing with 428");

        // Fake time is frozen: no wall-clock waiting can start the next attempt.
        await Task.Delay(1_000, testToken);
        pairs.Created.Should().Be(1, "the backoff window holds while fake time is frozen");

        // Advancing past the cap covers every full-jitter draw: the attempt starts.
        fake.Advance(TimeSpan.FromMilliseconds(8_000));
        await WaitForQueuedAsync(
            states, e => e.State == ConnectionState.Open && states.Count(s => s.State == ConnectionState.Open) >= 2, "second open");

        byte[] secondReply = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("after"), testToken);
        secondReply.Should().Equal(Encoding.ASCII.GetBytes("after"));

        await connection.DisposeAsync();
        await peerTask;
    }

    [Fact]
    public async Task Dispose_during_backoff_starts_no_new_attempt()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        var fake = new FakeTimeProvider();
        var options = new ConnectionOptions
        {
            ConnectTimeoutMs = 5_000,
            QueryTimeoutMs = 5_000,
            RetryBaseDelayMs = 2_000,
            RetryMaxDelayMs = 8_000,
        };
        AccountId disposeAccount = NewAccount("dispose-backoff");
        await using var connection = new WaConnection(
            disposeAccount,
            pairs.CreateClient,
            await CreateSeededPayloadsAsync(disposeAccount, testToken),
            options,
            fake);
        connection.StateChanged += (_, e) => states.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            PeerHandshakeResult handshake = await CompletePeerHandshakeAsync(
                peer, Encoding.ASCII.GetBytes("server-hello"), testToken);
            using (handshake.Transport)
            {
                await SendPeerSuccessAsync(peer, handshake.Transport, testToken);
                await RunPeerEchoLoopAsync(peer, handshake.Transport, 1, testToken);
            }

            await peer.CloseAsync(428, testToken);
        }, testToken);

        await connection.ConnectAsync(testToken);
        byte[] reply = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("before"), testToken);
        reply.Should().Equal(Encoding.ASCII.GetBytes("before"));

        await WaitForQueuedAsync(states, e => e.State == ConnectionState.Closing && e.StatusCode == 428, "closing with 428");

        // Cancelling during backoff aborts the reconnect: no socket is held mid-backoff
        // (515-during-backoff is structurally precluded), so nothing new can start.
        await connection.DisposeAsync();
        fake.Advance(TimeSpan.FromMilliseconds(60_000));
        await Task.Delay(SettleWindow, testToken);

        pairs.Created.Should().Be(1);
        states.Any(e => e.State == ConnectionState.Connecting && e.Attempt > 0).Should().BeFalse();

        await peerTask;
    }

    [Fact]
    public async Task Concurrent_401_drop_and_dispose_emit_logged_out_at_most_once()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        AccountId raceAccount = NewAccount("drop-close-race");
        await using var connection = new WaConnection(
            raceAccount,
            pairs.CreateClient,
            await CreateSeededPayloadsAsync(raceAccount, testToken),
            TracerOptions());
        connection.StateChanged += (_, e) => states.Enqueue(e);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            PeerHandshakeResult handshake = await CompletePeerHandshakeAsync(
                peer, Encoding.ASCII.GetBytes("server-hello"), testToken);
            using (handshake.Transport)
            {
                await SendPeerSuccessAsync(peer, handshake.Transport, testToken);
                await RunPeerEchoLoopAsync(peer, handshake.Transport, 1, testToken);
            }

            await peer.CloseAsync(401, testToken);
        }, testToken);

        await connection.ConnectAsync(testToken);
        byte[] reply = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("last"), testToken);
        reply.Should().Equal(Encoding.ASCII.GetBytes("last"));

        // Race the terminal drop against host teardown: exactly-once holds in every
        // interleaving (at most one LoggedOut, and nothing follows it).
        await Task.WhenAll(
            Task.Run(async () =>
            {
                await Task.Delay(10, testToken);
                await connection.DisposeAsync();
            }, testToken),
            peerTask);

        await Task.Delay(SettleWindow, testToken);

        states.Count(e => e.State == ConnectionState.LoggedOut).Should().BeLessThanOrEqualTo(1);
        ConnectionStateChangedEventArgs[] loggedOut = states.Where(e => e.State == ConnectionState.LoggedOut).ToArray();
        if (loggedOut.Length == 1)
        {
            states.Last().State.Should().Be(ConnectionState.LoggedOut, "no post-terminal events may follow the terminal");
        }
    }

    [Fact]
    public async Task Connect_timeout_recycles_as_408_with_distinct_sub_cause_log()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        var logger = new CaptureLogger<WaConnection>();
        var options = new ConnectionOptions
        {
            ConnectTimeoutMs = 200,
            QueryTimeoutMs = 5_000,
            RetryBaseDelayMs = 0,
            RetryMaxDelayMs = 0,
        };
        AccountId timeoutAccount = NewAccount("attempt-timeout");
        await using var connection = new WaConnection(
            timeoutAccount,
            pairs.CreateClient,
            await CreateSeededPayloadsAsync(timeoutAccount, testToken),
            options,
            timeProvider: null,
            logger: logger);
        connection.StateChanged += (_, e) => states.Enqueue(e);

        // Two peer tasks: the first claims pair1 and holds it silently (forcing the
        // connect-attempt timeout); the gate ensures the second can only take pair2,
        // so the order is deterministic and no pair is ever stolen.
        var pair1Taken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var holderCts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        CancellationToken holderToken = holderCts.Token;
        var holderTask = Task.Run(async () =>
        {
            try
            {
                LoopbackFrameTransport silent = await TakePeerAsync(pairs, holderToken);
                pair1Taken.TrySetResult();
                await Task.Delay(Timeout.Infinite, holderToken);
                await silent.DisposeAsync();
            }
            catch (OperationCanceledException)
            {
            }
        }, testToken);

        // The monitor (started by ConnectAsync) mints pair1, which the holder claims;
        // only then can the second task take pair2 — no pair is ever stolen.
        Task connectTask = connection.ConnectAsync(testToken);
        await pair1Taken.Task.WaitAsync(testToken);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            PeerHandshakeResult handshake = await CompletePeerHandshakeAsync(
                peer, Encoding.ASCII.GetBytes("server-hello"), testToken);
            using (handshake.Transport)
            {
                await SendPeerSuccessAsync(peer, handshake.Transport, testToken);
                await RunPeerEchoLoopAsync(peer, handshake.Transport, int.MaxValue, testToken);
            }

            await peer.DisposeAsync();
        }, testToken);

        await connectTask;
        connection.State.Should().Be(ConnectionState.Open);

        ConnectionStateChangedEventArgs[] connectings = states.Where(e => e.State == ConnectionState.Connecting).ToArray();
        connectings.Select(e => e.Attempt).Should().Equal(0, 1);
        ConnectionStateChangedEventArgs closing = states.Single(e => e.State == ConnectionState.Closing);
        closing.StatusCode.Should().Be(408);

        // Same policy as every 408 (bounded retry), but distinctly diagnosed: the log
        // names the attempt-timeout sub-cause, never frame bytes or key material.
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Information && e.Message.Contains("attempt-timeout"));
        logger.Entries.Should().OnlyContain(e => !e.Message.Contains("wa-probe"));

        await connection.DisposeAsync();
        await holderCts.CancelAsync();
        await holderTask;
        try
        {
            await peerTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task ReconnectGate_collapses_concurrent_requests_to_one_attempt()
    {
        var gate = new ReconnectAttemptGate();
        int runs = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Task<int>[] callers = Enumerable.Range(0, 32).Select(_ => Task.Run(() => gate.RunAsync(async () =>
        {
            Interlocked.Increment(ref runs);
            await release.Task;
            return 7;
        }, CancellationToken.None))).ToArray();

        while (runs == 0)
        {
            await Task.Delay(5, timeout.Token);
        }

        await Task.Delay(200, timeout.Token);
        release.TrySetResult();

        int[] results = await Task.WhenAll(callers);
        results.Should().OnlyContain(r => r == 7);
        runs.Should().Be(1, "all 32 concurrent triggers must attach to the one in-flight attempt");
    }

    [Fact]
    public async Task ReconnectGate_fault_evicts_so_the_next_trigger_retries_fresh()
    {
        var gate = new ReconnectAttemptGate();
        int runs = 0;
        Func<Task<int>> failing = () =>
        {
            Interlocked.Increment(ref runs);
            return Task.FromException<int>(new InvalidOperationException("reconnect fault"));
        };

        Func<Task> first = () => gate.RunAsync(failing, CancellationToken.None);
        (await first.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*reconnect fault*");

        Func<Task> second = () => gate.RunAsync(failing, CancellationToken.None);
        (await second.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*reconnect fault*");

        runs.Should().Be(2, "a faulted attempt evicts — the next trigger retries instead of observing poison");
    }

    [Fact]
    public async Task ReconnectGate_attached_caller_observes_own_cancellation()
    {
        var gate = new ReconnectAttemptGate();
        int runs = 0;
        var hung = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> holder = gate.RunAsync(() =>
        {
            Interlocked.Increment(ref runs);
            return hung.Task;
        }, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        Func<Task> canceled = () => gate.RunAsync(() => hung.Task, cts.Token);
        await canceled.Should().ThrowAsync<OperationCanceledException>();

        hung.TrySetResult(7);
        (await holder).Should().Be(7, "one caller's cancellation never disturbs the shared attempt");
        runs.Should().Be(1);
    }

    [Fact]
    public async Task Handshake_transcript_round_trips_through_the_store_seam_as_minimal_proof()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        AccountId account = NewAccount("store-proof");
        await using var connection = new WaConnection(
            account,
            pairs.CreateClient,
            await CreateSeededPayloadsAsync(account, testToken),
            TracerOptions());

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            PeerHandshakeResult handshake = await CompletePeerHandshakeAsync(
                peer, Encoding.ASCII.GetBytes("server-hello"), testToken);
            using (handshake.Transport)
            {
                await SendPeerSuccessAsync(peer, handshake.Transport, testToken);
                await RunPeerEchoLoopAsync(peer, handshake.Transport, int.MaxValue, testToken);
            }

            await peer.DisposeAsync();
        }, testToken);

        await connection.ConnectAsync(testToken);

        // Minimal seam proof (full resume is Phase 3): the public-channel transcript
        // hash crosses the Phase 1 store seam and reads back byte-identical. Nothing
        // is read back INTO the handshake — resume-without-repair stays Phase 3.
        byte[]? hash = connection.LastHandshakeHash;
        hash.Should().NotBeNull();
        hash.Should().HaveCount(32);

        await using var registry = new StoreRegistry(new StoreRegistryOptions());
        StoreSession session = await registry.CreateSessionAsync(account, CancellationToken.None);
        var record = new SignalSessionRecord("handshake-transcript", hash!);
        await session.SignalSessions.PutAsync(record.Address, record, CancellationToken.None);

        SignalSessionRecord? fetched = await session.SignalSessions.GetAsync(record.Address, CancellationToken.None);
        fetched.Should().NotBeNull();
        fetched!.SessionState.Should().Equal(hash!);

        await connection.DisposeAsync();
        await peerTask;
    }
}
