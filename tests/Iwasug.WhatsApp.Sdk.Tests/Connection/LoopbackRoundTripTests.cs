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
        internal readonly ConcurrentQueue<(LogLevel Level, string Message)> Entries = new();

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Enqueue((logLevel, formatter(state, exception)));

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
        ReadOnlyMemory<byte>? frame = await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (frame is null)
        {
            throw new InvalidOperationException("Peer transport closed unexpectedly.");
        }

        return frame.Value.ToArray();
    }

    private static async Task<PeerHandshakeResult> CompletePeerHandshakeAsync(
        IFrameTransport peer,
        byte[] serverHello,
        CancellationToken cancellationToken)
    {
        byte[] header = await ReceiveOrThrowAsync(peer, cancellationToken).ConfigureAwait(false);
        header.Should().Equal([0x57, 0x41, 0x06, 0x03]);

        byte[] messageA = await ReceiveOrThrowAsync(peer, cancellationToken).ConfigureAwait(false);
        if (messageA.Length < 38)
        {
            throw new InvalidOperationException("Peer observed malformed message A.");
        }

        messageA.AsSpan(0, 4).ToArray().Should().Equal([0x57, 0x41, 0x06, 0x03]);
        byte[] clientEphemeral = messageA.AsSpan(4, 32).ToArray();
        int helloLength = BinaryPrimitives.ReadUInt16BigEndian(messageA.AsSpan(36, 2));
        if (messageA.Length != 38 + helloLength)
        {
            throw new InvalidOperationException("Peer observed malformed message A.");
        }

        KeyCreationParameters keyParameters = default;
        using Key peerKey = Key.Create(KeyAgreementAlgorithm.X25519, keyParameters);
        byte[] peerEphemeral = peerKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        PublicKey clientPublic = PublicKey.Import(KeyAgreementAlgorithm.X25519, clientEphemeral, KeyBlobFormat.RawPublicKey);
        var agreeParameters = new SharedSecretCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        };
        byte[] sharedSecret;
        using (SharedSecret agreement = KeyAgreementAlgorithm.X25519.Agree(peerKey, clientPublic, agreeParameters)
            ?? throw new InvalidOperationException("Peer key agreement failed."))
        {
            sharedSecret = agreement.Export(SharedSecretBlobFormat.RawSharedSecret);
        }

        (byte[] initiatorWrite, byte[] initiatorRead) = NoiseHandshake.DeriveTransportKeys(
            sharedSecret,
            clientEphemeral,
            peerEphemeral);
        CryptographicOperations.ZeroMemory(sharedSecret);
        var transport = new NoiseTransport(initiatorRead, initiatorWrite);
        CryptographicOperations.ZeroMemory(initiatorWrite);
        CryptographicOperations.ZeroMemory(initiatorRead);

        var messageB = new byte[4 + 32 + 2 + serverHello.Length];
        messageB[0] = 0x57;
        messageB[1] = 0x41;
        messageB[2] = 0x06;
        messageB[3] = 0x03;
        peerEphemeral.CopyTo(messageB.AsSpan(4));
        BinaryPrimitives.WriteUInt16BigEndian(messageB.AsSpan(36, 2), (ushort)serverHello.Length);
        serverHello.CopyTo(messageB.AsSpan(38));
        await peer.SendAsync(messageB, cancellationToken).ConfigureAwait(false);

        byte[] messageC = await ReceiveOrThrowAsync(peer, cancellationToken).ConfigureAwait(false);
        return new PeerHandshakeResult
        {
            ClientMessageA = messageA,
            ClientMessageC = messageC,
            Transport = transport,
        };
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

            byte[] plain = noise.Decrypt(frame.Value.Span);
            if (!QueryRouter.TryParseFrame(plain, out string? id, out byte[]? body) || id is null || body is null)
            {
                throw new InvalidOperationException("Peer received an unparsable query frame.");
            }

            byte[] response = QueryRouter.BuildFrame(id, reply(body));
            await peer.SendAsync(noise.Encrypt(response), cancellationToken).ConfigureAwait(false);
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
        peerHandshake.ClientMessageA.AsSpan(0, 4).ToArray().Should().Equal([0x57, 0x41, 0x06, 0x03]);
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

        Task peerTask = Task.Run(async () =>
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

        Task peerTask = Task.Run(async () =>
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
}
