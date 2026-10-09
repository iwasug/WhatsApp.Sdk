using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Noise;
using Iwasug.WhatsApp.Sdk.Transport;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Connection;

/// <summary>
/// The SINGLE real-socket test in the phase (02-RESEARCH Pattern 1): the production
/// <see cref="ClientWebSocket"/> path — Origin/UA headers, keepalive-configured connect,
/// Noise handshake, iq round-trip, and server-injected 428 → autonomous reconnect — proven
/// once over loopback against an <see cref="HttpListener"/> fake server speaking the
/// loopback protocol. Every other test rides the in-memory loopback fake.
/// </summary>
/// <remarks>
/// <para>
/// TLS (T-2-11): the loopback proof uses plaintext <c>ws://</c>, which involves no TLS
/// and therefore bypasses nothing. No code path — production or test — overrides
/// certificate validation (asserted by grep over <c>src/</c> in review, not by a
/// runtime flag this test could silently flip).
/// </para>
/// <para>
/// The fake server closes with <c>4000 + WA code</c> (the loopback close convention in
/// <see cref="WebSocketFrameTransport"/>): a raw sub-1000 code is not a sendable WS
/// close status on either stack, so the offset carries the code across the real close
/// handshake and the transport strips it back.
/// </para>
/// </remarks>
public sealed class LoopbackTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private sealed class StaticPayloads : INoiseHandshakePayloads
    {
        public byte[] GetClientHello() => Encoding.ASCII.GetBytes("client-hello");

        public byte[] GetClientFinish() => Encoding.ASCII.GetBytes("client-finish");
    }

    private sealed class LoopbackServer : IDisposable
    {
        private readonly HttpListener _listener;
        private bool _disposed;

        internal readonly ConcurrentQueue<(string? Origin, string? UserAgent)> ObservedHeaders = new();

        private LoopbackServer(HttpListener listener, Uri endpoint)
        {
            _listener = listener;
            Endpoint = endpoint;
        }

        internal Uri Endpoint { get; }

        internal static LoopbackServer Start()
        {
            int port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/wa/");
            listener.Start();
            return new LoopbackServer(listener, new Uri($"ws://127.0.0.1:{port}/wa/"));
        }

        internal async Task<WebSocket> AcceptAsync(CancellationToken cancellationToken)
        {
            HttpListenerContext context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
            ObservedHeaders.Enqueue((
                context.Request.Headers["Origin"],
                context.Request.Headers["User-Agent"]));
            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                throw new InvalidOperationException("Loopback server expected a WebSocket request.");
            }

            HttpListenerWebSocketContext webSocketContext = await context.AcceptWebSocketAsync(null);
            return webSocketContext.WebSocket;
        }

        /// <summary>
        /// Serves one loopback session: Noise handshake (production responder role),
        /// then <paramref name="echoQueriesBeforeClose"/> iq echoes, then the injected
        /// close carrying <paramref name="closeCode"/>.
        /// </summary>
        internal async Task ServeSessionAsync(
            WebSocket socket,
            byte[] serverHello,
            int echoQueriesBeforeClose,
            int closeCode,
            CancellationToken cancellationToken)
        {
            using WebSocket _ = socket;
            byte[] header = await ReceiveMessageAsync(socket, cancellationToken);
            NoiseHandshake.ValidateIntroHeader(header);

            byte[] messageAFrame = await ReceiveMessageAsync(socket, cancellationToken);
            using var responder = new NoiseHandshakeResponder(serverHello);
            responder.ReadMessageA(WaFrame.Deframe(messageAFrame).ToArray());
            await SendMessageAsync(socket, WaFrame.Frame(responder.WriteMessageB()), cancellationToken);

            byte[] messageCFrame = await ReceiveMessageAsync(socket, cancellationToken);
            using NoiseTransport transport = responder.ReadMessageC(WaFrame.Deframe(messageCFrame).ToArray());

            for (int i = 0; i < echoQueriesBeforeClose; i++)
            {
                byte[] encrypted = await ReceiveMessageAsync(socket, cancellationToken);
                ReadOnlySpan<byte> framed = WaFrame.Deframe(encrypted);
                byte[] plain = transport.Decrypt(framed.Slice(1));
                if (!QueryRouter.TryParseFrame(plain, out string? id, out byte[]? body) || id is null || body is null)
                {
                    throw new InvalidOperationException("Loopback server received an unparsable query frame.");
                }

                byte[] encryptedReply = transport.Encrypt(QueryRouter.BuildFrame(id, body));
                byte[] flaggedReply = new byte[encryptedReply.Length + 1];
                encryptedReply.CopyTo(flaggedReply.AsSpan(1));
                await SendMessageAsync(socket, WaFrame.Frame(flaggedReply), cancellationToken);
            }

            await socket.CloseAsync((WebSocketCloseStatus)(WebSocketFrameTransport.LoopbackCloseBase + closeCode), "injected", cancellationToken);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _listener.Stop();
            _listener.Close();
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private static async Task<byte[]> ReceiveMessageAsync(WebSocket socket, CancellationToken cancellationToken)
        {
            using var message = new MemoryStream();
            byte[] buffer = new byte[8192];
            while (true)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new InvalidOperationException("Loopback server saw close while a message was expected.");
                }

                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    break;
                }
            }

            return message.ToArray();
        }

        private static async Task SendMessageAsync(WebSocket socket, byte[] payload, CancellationToken cancellationToken)
        {
            await socket.SendAsync(payload, WebSocketMessageType.Binary, true, cancellationToken);
        }
    }

    private static AccountId NewAccount() =>
        AccountId.Create("loopback-real-" + Guid.NewGuid().ToString("N"));

    private static ConnectionOptions RealSocketOptions() => new()
    {
        ConnectTimeoutMs = 5_000,
        QueryTimeoutMs = 5_000,
        RetryBaseDelayMs = 0,
        RetryMaxDelayMs = 0,
    };

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
    public async Task RealSocket_connect_headers_handshake_roundtrip_then_428_reconnects()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        using var server = LoopbackServer.Start();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        ConnectionOptions options = RealSocketOptions();
        await using var connection = new WaConnection(
            NewAccount(),
            () => new WebSocketFrameTransport(server.Endpoint, options),
            new StaticPayloads(),
            options);
        connection.StateChanged += (_, e) => states.Enqueue(e);

        var serverTask = Task.Run(async () =>
        {
            WebSocket first = await server.AcceptAsync(testToken);
            await server.ServeSessionAsync(
                first,
                Encoding.ASCII.GetBytes("server-hello"),
                echoQueriesBeforeClose: 2,
                closeCode: 428,
                testToken);

            WebSocket second = await server.AcceptAsync(testToken);
            try
            {
                await server.ServeSessionAsync(
                    second,
                    Encoding.ASCII.GetBytes("server-hello"),
                    echoQueriesBeforeClose: int.MaxValue,
                    closeCode: 428,
                    testToken);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or InvalidOperationException)
            {
                // Client teardown aborts the socket mid-session — expected test end.
            }
        }, testToken);

        await connection.ConnectAsync(testToken);
        byte[] firstReply = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("before-drop"), testToken);
        firstReply.Should().Equal(Encoding.ASCII.GetBytes("before-drop"));

        await WaitForQueuedAsync(
            states,
            e => e.State == ConnectionState.Open && states.Count(s => s.State == ConnectionState.Open) >= 2,
            "second open after the injected 428");

        byte[] secondReply = await connection.SendQueryAsync(Encoding.ASCII.GetBytes("after-drop"), testToken);
        secondReply.Should().Equal(Encoding.ASCII.GetBytes("after-drop"));

        // Close-code mapping proven end-to-end: the server's injected 428 crossed the
        // real close handshake and drove the policy as 428 (retry, not terminal).
        ConnectionStateChangedEventArgs closing = states.Single(e => e.State == ConnectionState.Closing);
        closing.StatusCode.Should().Be(428);
        ConnectionStateChangedEventArgs[] connectings = states.Where(e => e.State == ConnectionState.Connecting).ToArray();
        connectings.Should().HaveCount(2);
        connectings[0].Attempt.Should().Be(0);
        connectings[1].Attempt.Should().Be(1);
        states.Count(e => e.State == ConnectionState.Open).Should().Be(2);

        // Browser-mimicry headers observed server-side on BOTH sockets (initial + reconnect).
        (string? Origin, string? UserAgent)[] observed = server.ObservedHeaders.ToArray();
        observed.Should().HaveCount(2);
        foreach ((string? origin, string? userAgent) in observed)
        {
            origin.Should().Be(WebSocketFrameTransport.DefaultOrigin);
            userAgent.Should().Be(WebSocketFrameTransport.DefaultUserAgent);
        }

        await connection.DisposeAsync();
        await serverTask.WaitAsync(testToken);
    }
}
