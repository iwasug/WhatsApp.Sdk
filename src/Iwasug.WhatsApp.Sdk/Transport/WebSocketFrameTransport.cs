using System.Buffers;
using System.Net.WebSockets;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Noise;
using Microsoft.Extensions.Logging;

namespace Iwasug.WhatsApp.Sdk.Transport;

/// <summary>
/// Production <see cref="IFrameTransport"/> over the in-box <see cref="ClientWebSocket"/>
/// (02-05): one WS binary message per frame, byte-transparent. Connects lazily on first
/// use with spike-confirmed endpoint/Origin browser-mimicry headers, framework-managed
/// keepalive, and the configured connect timeout; reassembles fragmented WS messages and
/// surfaces the peer's close code for the reconnect policy.
/// </summary>
/// <remarks>
/// <para>
/// Byte-transparency is deliberate: framing above this seam (Noise handshake envelopes,
/// the query correlation envelope, and — from 02-04 — the 3-byte wire prefix parsed by
/// <see cref="FrameReader"/>) owns its bytes end-to-end, so the same
/// <see cref="Connection.WaConnection"/> code path runs unchanged over this transport
/// and the in-memory loopback fake. "Outbound framed through NoiseTransport" (02-05 plan)
/// holds because the connection encrypts every post-handshake frame with
/// <see cref="Noise.NoiseTransport"/> before it reaches <see cref="SendAsync"/> — this
/// class never sees plaintext semantics and never logs frame bytes.
/// </para>
/// <para>
/// Close-code mapping: the real server closes with its numeric disconnect codes
/// (428/440/515/401 — sub-1000 codes the RFC never envisioned but the wire carries),
/// which arrive verbatim in the close frame and are surfaced as-is. Standard
/// <c>1000 (normal)</c> recycles as 428 (server-closed → bounded retry, never silent);
/// any other standard code passes through for the policy table to treat as drift.
/// Outbound, codes ride the loopback convention <c>4000 + code</c> (a raw sub-1000 code
/// is not a sendable WS close status): the LoopbackTests fake server understands it,
/// and no production caller sends WA codes today.
/// </para>
/// <para>
/// TLS (T-2-11): default chain validation always applies — this class never sets
/// <c>ServerCertificateCustomValidationCallback</c> (or any bypass) on any code path.
/// The loopback proof uses plaintext <c>ws://</c>, which involves no TLS and therefore
/// bypasses nothing; every <c>wss://</c> connect validates normally.
/// </para>
/// <para>
/// Lifecycle mirrors <see cref="Stores.StoreRegistry"/>: constructed per connection (no
/// statics, no shared mutable state across accounts), connect is single-flight
/// (first-caller-wins lazy task — a faulted connect poisons this instance by design and
/// the connection mints a fresh transport per attempt), <c>CT</c> first line,
/// <c>OperationCanceledException</c> never wrapped or retried,
/// <c>.ConfigureAwait(false)</c> on every await, and disposal sets the flag first,
/// snapshots the owned socket, and disposes it.
/// </para>
/// </remarks>
public sealed class WebSocketFrameTransport : IFrameTransport
{
    /// <summary>
    /// Spike-confirmed connect target (02-SPIKE-NOTES.md F5:
    /// Baileys <c>waWebSocketUrl</c> at commit <c>0af2386</c>).
    /// </summary>
    internal const string DefaultEndpoint = "wss://web.whatsapp.com/ws/chat";

    /// <summary>
    /// Spike-confirmed browser-mimicry origin (02-SPIKE-NOTES.md F5:
    /// Baileys <c>DEFAULT_ORIGIN</c>).
    /// </summary>
    internal const string DefaultOrigin = "https://web.whatsapp.com";

    /// <summary>
    /// Browser-mimicry user agent sent alongside <see cref="DefaultOrigin"/> (header
    /// content, not a security downgrade — 02-RESEARCH V9). Tunable without API break.
    /// </summary>
    internal const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    /// <summary>Loopback close-code base: the fake server closes with <c>4000 + WA code</c>.</summary>
    internal const int LoopbackCloseBase = 4000;

    /// <summary>Still open (mirrors the loopback fake's shared-close sentinel).</summary>
    private const int Open = -1;

    private readonly Uri _endpoint;
    private readonly ConnectionOptions _options;
    private readonly ILogger<WebSocketFrameTransport>? _logger;

    private readonly object _connectGate = new();
    private Task<ClientWebSocket>? _connectTask;
    private int _closeCode = Open;
    private int _disposed;

    /// <summary>
    /// Creates a transport for <paramref name="endpoint"/>. No I/O happens here — the
    /// socket connects lazily on first send/receive (single-flight).
    /// </summary>
    /// <param name="endpoint">WebSocket URL (production: <see cref="DefaultEndpoint"/>).</param>
    /// <param name="options">Timeouts and keepalive bounds; null resolves to defaults.</param>
    /// <param name="logger">Optional per-connection logger (Abstractions only).</param>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> is out of range.</exception>
    public WebSocketFrameTransport(
        Uri endpoint,
        ConnectionOptions? options = null,
        ILogger<WebSocketFrameTransport>? logger = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = ConnectionOptions.ValidateOptions(options);
        _logger = logger;
    }

    /// <inheritdoc />
    public int? CloseStatusCode
    {
        get
        {
            int code = Volatile.Read(ref _closeCode);
            return code == Open ? null : code;
        }
    }

    /// <inheritdoc />
    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (frame.IsEmpty)
        {
            throw new SdkTransportException("Frame must not be empty.");
        }

        if (CloseStatusCode is not null)
        {
            throw new SdkConnectionException("Transport is closed — no more sends are accepted.");
        }

        ClientWebSocket socket = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "WebSocket frame send failed for endpoint {Endpoint}.", _endpoint);
            throw new SdkTransportException("Frame send failed.", ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (CloseStatusCode is not null)
        {
            return null;
        }

        ClientWebSocket socket = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        byte[] rented = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using var message = new MemoryStream();
            while (true)
            {
                ValueWebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(rented.AsMemory(0, 8192), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ObjectDisposedException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is WebSocketException || ex is IOException)
                {
                    _logger?.LogWarning(ex, "WebSocket frame receive failed for endpoint {Endpoint}.", _endpoint);
                    throw new SdkTransportException("Frame receive failed.", ex);
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    int code = socket.CloseStatus.HasValue
                        ? MapInboundCloseCode((int)socket.CloseStatus.Value)
                        : (int)DisconnectReason.ConnectionLost;
                    RecordClose(code);
                    _logger?.LogInformation(
                        "WebSocket peer closed for endpoint {Endpoint}: status code {StatusCode}.",
                        _endpoint,
                        code);
                    await EchoCloseAsync(socket).ConfigureAwait(false);
                    return null;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    throw new SdkTransportException("Unexpected text message on the binary frame transport.");
                }

                message.Write(rented, 0, result.Count);
                if (message.Length > NoiseProtocol.MaxFrameBytes)
                {
                    throw new SdkTransportException("Frame exceeds the maximum frame size.");
                }

                if (result.EndOfMessage)
                {
                    break;
                }
            }

            return message.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <inheritdoc />
    public async ValueTask CloseAsync(int statusCode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int first = RecordClose(statusCode);

        Task<ClientWebSocket>? pending;
        lock (_connectGate)
        {
            pending = _connectTask;
        }

        if (pending is null)
        {
            return;
        }

        ClientWebSocket socket;
        try
        {
            socket = await pending.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "WebSocket close observed no live socket for endpoint {Endpoint}.", _endpoint);
            return;
        }

        try
        {
            await socket.CloseOutputAsync(MapOutboundCloseCode(first), "closing", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SdkTransportException("Transport close failed.", ex);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Task<ClientWebSocket>? pending;
        lock (_connectGate)
        {
            Interlocked.Exchange(ref _disposed, 1);
            pending = _connectTask;
            _connectTask = null;
        }

        if (pending is null)
        {
            return ValueTask.CompletedTask;
        }

        return new ValueTask(DisposeConnectedAsync(pending));
    }

    /// <summary>
    /// Maps an inbound WS close code to the reconnect vocabulary: loopback
    /// <c>4000 + code</c> strips to the WA code, standard normal recycles as
    /// server-closed (428, bounded retry), everything else passes through for the
    /// policy table (unknown codes get bounded retry + a loud log downstream).
    /// </summary>
    internal static int MapInboundCloseCode(int webSocketCode) =>
        webSocketCode switch
        {
            >= LoopbackCloseBase and <= 4999 => webSocketCode - LoopbackCloseBase,
            1000 => (int)DisconnectReason.ConnectionClosed,
            _ => webSocketCode,
        };

    /// <summary>
    /// Maps a status code to a sendable WS close status: standard codes pass through,
    /// WA/loopback codes ride the <c>4000 + code</c> convention the loopback peer
    /// understands (a raw sub-1000 code is not a sendable close status).
    /// </summary>
    internal static WebSocketCloseStatus MapOutboundCloseCode(int statusCode) =>
        statusCode is >= 1000 and <= 4999
            ? (WebSocketCloseStatus)statusCode
            : (WebSocketCloseStatus)(LoopbackCloseBase + statusCode);

    private async Task<ClientWebSocket> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        Task<ClientWebSocket>? pending;
        lock (_connectGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(WebSocketFrameTransport));
            }

            pending = _connectTask ??= ConnectCoreAsync(cancellationToken);
        }

        return await pending.ConfigureAwait(false);
    }

    private async Task<ClientWebSocket> ConnectCoreAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.KeepAliveInterval = TimeSpan.FromMilliseconds(_options.KeepAliveIntervalMs);
            socket.Options.SetRequestHeader("Origin", DefaultOrigin);
            socket.Options.SetRequestHeader("User-Agent", DefaultUserAgent);

            _logger?.LogInformation("WebSocket connecting to {Endpoint}.", _endpoint);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(_options.ConnectTimeoutMs));
            await socket.ConnectAsync(_endpoint, timeout.Token).ConfigureAwait(false);

            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(WebSocketFrameTransport));
            }

            _logger?.LogInformation("WebSocket connected to {Endpoint}.", _endpoint);
            return socket;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw;
        }
        catch (OperationCanceledException ex)
        {
            socket.Dispose();
            _logger?.LogWarning(ex, "WebSocket connect timed out for endpoint {Endpoint}.", _endpoint);
            throw new SdkTransportException("WebSocket connect timed out.", ex);
        }
        catch (ObjectDisposedException)
        {
            socket.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            socket.Dispose();
            _logger?.LogWarning(ex, "WebSocket connect failed for endpoint {Endpoint}.", _endpoint);
            throw new SdkTransportException("WebSocket connect failed.", ex);
        }
    }

    private async Task DisposeConnectedAsync(Task<ClientWebSocket> pending)
    {
        try
        {
            ClientWebSocket socket = await pending.ConfigureAwait(false);
            socket.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "WebSocket disposal observed no live socket for endpoint {Endpoint}.", _endpoint);
        }
    }

    private async Task EchoCloseAsync(ClientWebSocket socket)
    {
        using var echoTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closing", echoTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "WebSocket close echo failed for endpoint {Endpoint}.", _endpoint);
        }
    }

    private int RecordClose(int code)
    {
        if (Interlocked.CompareExchange(ref _closeCode, code, Open) == Open)
        {
            return code;
        }

        return Volatile.Read(ref _closeCode);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new ObjectDisposedException(nameof(WebSocketFrameTransport));
        }
    }
}
