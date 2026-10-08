using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Noise;
using Iwasug.WhatsApp.Sdk.Transport;
using Microsoft.Extensions.Logging;

namespace Iwasug.WhatsApp.Sdk.Connection;

/// <summary>
/// Host-observable transition of one account's connection (CONN-05).
/// </summary>
public sealed class ConnectionStateChangedEventArgs : EventArgs
{
    /// <summary>Creates a transition event.</summary>
    /// <param name="state">The state just entered.</param>
    /// <param name="statusCode">Drop code that caused <c>Closing</c>/<c>LoggedOut</c>; null for <c>Connecting</c>/<c>Open</c> and for host-initiated stops.</param>
    /// <param name="attempt">Reconnect attempt depth (0 on the first <c>Connecting</c>; reset on <c>Open</c> and on 515-restart).</param>
    public ConnectionStateChangedEventArgs(ConnectionState state, int? statusCode, int attempt)
    {
        State = state;
        StatusCode = statusCode;
        Attempt = attempt;
    }

    /// <summary>The state just entered.</summary>
    public ConnectionState State { get; }

    /// <summary>Drop code that caused the transition, when any.</summary>
    public int? StatusCode { get; }

    /// <summary>Reconnect attempt depth at the transition.</summary>
    public int Attempt { get; }
}

/// <summary>
/// One account's live connection (02-02 tracer skeleton): transport auth plus one
/// correlated query per connect, then autonomous classified reconnect — no host code on
/// any drop (CONN-04/CONN-05).
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle shape mirrors <see cref="Stores.StoreRegistry"/>: constructed per
/// <see cref="AccountId"/> (no statics, no shared mutable state across accounts —
/// mutable state is per-instance; the only statics are immutable shared data), CT first
/// line, default-<c>AccountId</c> guard, single-lifecycle gate against
/// <see cref="DisposeAsync"/>, disposal cascade with aggregate semantics,
/// <see cref="OperationCanceledException"/> never wrapped or retried, and the
/// StoreErrorSurface wrap-and-log shape applied to the transport/connection exception
/// subtypes. This class is the SOLE log site for the connection vertical (handshake and
/// router never log), so message discipline — operation/rule names and the account id
/// only, never key material, frame bytes, node payloads, or inner-exception content — is
/// reviewable here alone.
/// </para>
/// <para>
/// Connect flow per attempt: fresh transport from the factory → spike-confirmed WA
/// header (<see cref="NoiseProtocol"/>) → fresh <see cref="NoiseHandshake"/> (fresh
/// ephemeral, never reused) → one opaque liveness-probe query through
/// <see cref="QueryRouter"/> (proves framing, id-correlation, and the fresh AEAD keys
/// end-to-end; the probe's content is ignored) → <see cref="ConnectionState.Open"/>.
/// </para>
/// <para>
/// Drop path: the pump's EOF carries the peer's close code into
/// <see cref="ReconnectPolicy.Decide"/> — <c>RetryAfter</c> waits the full-jitter delay
/// on the injected <see cref="TimeProvider"/>, <c>RestartImmediate</c> (515) resets the
/// attempt counter with no delay, <c>Terminal</c> (401/403/411/419) emits
/// <see cref="ConnectionState.LoggedOut"/> exactly once (Interlocked guard) and stops,
/// <c>SurfaceAndStop</c> (440) stops in <c>Closing</c> without retrying (no kick-war).
/// Peer-protocol violations (dict-version mismatch and friends) halt the loop without
/// retry — a hostile peer is not a network flap. Every transition flows through the
/// single-writer monitor loop; handler exceptions are logged and swallowed so host bugs
/// cannot kill the loop, and <see cref="DisposeAsync"/> from inside a handler fails fast
/// instead of deadlocking.
/// </para>
/// <para>
/// Stillborn rule: the first open is reported only when the liveness probe succeeds
/// while the channel is still alive. A drop that lands between the probe reply and the
/// open emit (or a terminal code for the very first attempt) surfaces the terminal
/// outcome to the <see cref="ConnectAsync"/> caller with no <c>Open</c> event — a
/// connection that died before its open was reported never opens. Every later drop
/// flows through <see cref="ReconnectPolicy.Decide"/> with the events above.
/// </para>
/// <para>
/// Single lifecycle: <see cref="ConnectAsync"/> may be called once; cancellation before
/// the first open aborts the handshake (ephemeral zeroized via handshake disposal, the
/// attempt transport disposed so the peer observes EOF, no retry) and surfaces the
/// cancellation unwrapped. A connect attempt that exceeds
/// <see cref="ConnectionOptions.ConnectTimeoutMs"/> is treated as a 408 drop (Pitfall 2:
/// timeouts are connection-lost class) and recycled through the policy table.
/// </para>
/// </remarks>
public sealed class WaConnection : IAsyncDisposable
{
    private static readonly byte[] ProbeBody = "wa-probe"u8.ToArray();
    private static readonly HashSet<int> KnownCodes = [401, 403, 411, 419, 408, 428, 440, 500, 503, 515];

    private readonly AccountId _accountId;
    private readonly Func<IFrameTransport> _transportFactory;
    private readonly INoiseHandshakePayloads _payloads;
    private readonly ConnectionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WaConnection>? _logger;
    private readonly QueryRouter _router;
    private readonly CancellationTokenSource _monitorCts = new();

    private volatile Channel? _channel;
    private volatile TaskCompletionSource? _firstOpen;
    private Task? _monitorTask;
    private ConnectionState _state = ConnectionState.Connecting;
    private int _started;
    private int _disposed;
    private int _loggedOut;

    /// <summary>
    /// Raised on every state transition, from the connection's single-writer loop.
    /// Handlers must return quickly (copy to your own queue — Channels precedent) and
    /// must not call <see cref="DisposeAsync"/> (it fails fast instead of deadlocking).
    /// Handler exceptions are logged and swallowed.
    /// </summary>
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Creates one account's connection. No I/O happens here — call
    /// <see cref="ConnectAsync"/> to start the lifecycle.
    /// </summary>
    /// <param name="accountId">Opaque account key — must come from <see cref="AccountId.Create"/>.</param>
    /// <param name="transportFactory">Mints a fresh transport per connect attempt (fresh socket-equivalent on every reconnect).</param>
    /// <param name="payloads">Opaque handshake payloads (Phase 3 supplies semantics).</param>
    /// <param name="options">Timeouts and backoff bounds; null resolves to spike-confirmed defaults.</param>
    /// <param name="timeProvider">Clock for backoff/timeout delays; defaults to system time.</param>
    /// <param name="logger">Optional per-account logger (Abstractions only).</param>
    /// <exception cref="SdkFormatException"><paramref name="accountId"/> is the default (uninitialized) value.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="transportFactory"/> or <paramref name="payloads"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> is out of range.</exception>
    public WaConnection(
        AccountId accountId,
        Func<IFrameTransport> transportFactory,
        INoiseHandshakePayloads payloads,
        ConnectionOptions? options = null,
        TimeProvider? timeProvider = null,
        ILogger<WaConnection>? logger = null)
    {
        if (accountId.Value is null)
        {
            throw new SdkFormatException(
                "AccountId must be created via AccountId.Create — the default (uninitialized) AccountId is not valid.");
        }

        _accountId = accountId;
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _payloads = payloads ?? throw new ArgumentNullException(nameof(payloads));
        _options = ConnectionOptions.ValidateOptions(options);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _router = new QueryRouter(SendRoutedAsync, _options, _timeProvider);
    }

    /// <summary>Current host-observable state (written by the monitor loop only).</summary>
    public ConnectionState State => _state;

    /// <summary>
    /// Starts the connection lifecycle and completes at the first
    /// <see cref="ConnectionState.Open"/>: header → Noise handshake → liveness-probe
    /// query → open. A background monitor then owns reconnects until
    /// <see cref="DisposeAsync"/>. May be called once.
    /// </summary>
    /// <param name="cancellationToken">
    /// Governs the wait for the first open only (StoreRegistry precedent): cancelling
    /// before open aborts the handshake and surfaces unwrapped; cancelling after open is
    /// harmless (the registration is released on return).
    /// </param>
    /// <returns>A task completing at the first open.</returns>
    /// <exception cref="OperationCanceledException">Cancelled before opening (never wrapped, never retried).</exception>
    /// <exception cref="SdkConnectionException">
    /// Terminal before opening (logged-out/stopped/replaced) or the handshake rejected
    /// by validation. Messages name the outcome only.
    /// </exception>
    /// <exception cref="InvalidOperationException"><see cref="ConnectAsync"/> was already called.</exception>
    /// <exception cref="ObjectDisposedException">The connection is disposed.</exception>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            throw new InvalidOperationException("ConnectAsync has already been called on this connection.");
        }

        var firstOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _firstOpen = firstOpen;
        using (cancellationToken.Register(static state => ((CancellationTokenSource)state!).Cancel(), _monitorCts))
        {
            _monitorTask = Task.Run(MonitorLoopAsync, CancellationToken.None);
            if (Volatile.Read(ref _disposed) != 0)
            {
                firstOpen.TrySetException(new ObjectDisposedException(nameof(WaConnection)));
            }

            await firstOpen.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends one opaque query over the open connection and awaits its correlated reply.
    /// Fails fast (never queues, never silently drops) when the connection is not open.
    /// </summary>
    /// <param name="payload">Opaque query body.</param>
    /// <param name="cancellationToken">Caller cancellation (unwrapped).</param>
    /// <returns>The reply body.</returns>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    /// <exception cref="SdkConnectionException">
    /// Not open, timed out, dropped mid-query, or disposed. Messages name the outcome only.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The connection is disposed.</exception>
    public async ValueTask<byte[]> SendQueryAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return await _router.SendQueryAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_monitorTask is not null && Task.CurrentId == _monitorTask.Id)
        {
            throw new InvalidOperationException("DisposeAsync must not be called from a connection event handler.");
        }

        List<Exception>? failures = null;
        _monitorCts.Cancel();
        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        _firstOpen?.TrySetException(new ObjectDisposedException(nameof(WaConnection)));
        try
        {
            await UninstallChannelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(ex);
        }

        try
        {
            await _router.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(ex);
        }

        _monitorCts.Dispose();
        if (failures is not null)
        {
            throw new AggregateException("One or more connection resources failed to dispose.", failures);
        }
    }

    private async Task MonitorLoopAsync()
    {
        int attempt = 0;
        while (!_monitorCts.IsCancellationRequested)
        {
            Emit(ConnectionState.Connecting, statusCode: null, attempt);
            DropOutcome outcome;
            try
            {
                int? code = await ConnectAndWatchAsync(_monitorCts.Token).ConfigureAwait(false);
                await UninstallChannelAsync().ConfigureAwait(false);
                outcome = MapDropToOutcome(code, attempt);
            }
            catch (ProtocolHaltException ex)
            {
                StopForProtocolViolation(ex.InnerSdkError);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (_monitorCts.IsCancellationRequested)
                {
                    return;
                }

                _logger?.LogError(ex, "Connection loop fault for account {AccountId}.", _accountId.Value);
                outcome = DropOutcome.Retry(TimeSpan.Zero, attempt + 1, 408);
            }

            switch (outcome.Kind)
            {
                case DropOutcomeKind.Terminal:
                    if (Interlocked.CompareExchange(ref _loggedOut, 1, 0) == 0)
                    {
                        Emit(ConnectionState.LoggedOut, outcome.Code, attempt);
                    }

                    CompleteFirstOpen(new SdkConnectionException("Connection terminated: the account is logged out."));
                    return;
                case DropOutcomeKind.Stopped:
                    Emit(ConnectionState.Closing, outcome.Code, attempt);
                    CompleteFirstOpen(new SdkConnectionException("Connection stopped: the session state needs host action."));
                    return;
                default:
                    Emit(ConnectionState.Closing, outcome.Code, attempt);
                    attempt = outcome.NextAttempt;
                    if (outcome.Delay > TimeSpan.Zero)
                    {
                        try
                        {
                            await Task.Delay(outcome.Delay, _timeProvider, _monitorCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }

                    break;
            }
        }
    }

    private DropOutcome MapDropToOutcome(int? code, int attempt)
    {
        int answered = code ?? 408;
        if (code is not null && !KnownCodes.Contains(code.Value))
        {
            _logger?.LogWarning(
                "Unknown disconnect code {StatusCode} for account {AccountId}: bounded retry; possible protocol drift.",
                code.Value,
                _accountId.Value);
        }

        ReconnectDecision decision = ReconnectPolicy.Decide(answered, attempt, _options);
        switch (decision)
        {
            case ReconnectDecision.Terminal:
                _logger?.LogWarning(
                    "Terminal disconnect {StatusCode} for account {AccountId}: no retry.",
                    answered,
                    _accountId.Value);
                return DropOutcome.Stop(DropOutcomeKind.Terminal, answered);
            case ReconnectDecision.SurfaceAndStop:
                _logger?.LogWarning(
                    "Session replaced (status code {StatusCode}) for account {AccountId}: stopping without retry.",
                    answered,
                    _accountId.Value);
                return DropOutcome.Stop(DropOutcomeKind.Stopped, answered);
            case ReconnectDecision.RestartImmediate:
                _logger?.LogInformation(
                    "Immediate restart for account {AccountId}: status code {StatusCode}, attempt counter reset.",
                    _accountId.Value,
                    answered);
                return DropOutcome.Retry(TimeSpan.Zero, 0, answered);
            default:
                var retry = (ReconnectDecision.RetryAfter)decision;
                _logger?.LogInformation(
                    "Reconnect scheduled for account {AccountId}: status code {StatusCode}, delay {Delay}.",
                    _accountId.Value,
                    answered,
                    retry.Delay);
                return DropOutcome.Retry(retry.Delay, attempt + 1, answered);
        }
    }

    private void StopForProtocolViolation(SdkConnectionException error)
    {
        _logger?.LogError(
            error,
            "Handshake rejected for account {AccountId}: stopping without retry.",
            _accountId.Value);
        Emit(ConnectionState.Closing, statusCode: null, 0);
        CompleteFirstOpen(error);
    }

    private async Task<int?> ConnectAndWatchAsync(CancellationToken monitorToken)
    {
        IFrameTransport? transport = null;
        NoiseHandshake? handshake = null;
        NoiseTransport? noise = null;
        try
        {
            transport = CreateTransportSafely();
            using var attemptTimeout = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(_options.ConnectTimeoutMs),
                _timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(monitorToken, attemptTimeout.Token);
            CancellationToken attemptToken = linked.Token;

            handshake = new NoiseHandshake(_payloads);
            try
            {
                await transport.SendAsync(NoiseProtocol.WaHeader.ToArray(), attemptToken).ConfigureAwait(false);
                await transport.SendAsync(handshake.WriteMessageA(), attemptToken).ConfigureAwait(false);
            }
            catch (SdkConnectionException) when (transport.CloseStatusCode is not null)
            {
                return transport.CloseStatusCode;
            }

            ReadOnlyMemory<byte>? messageB;
            try
            {
                messageB = await transport.ReceiveAsync(attemptToken).ConfigureAwait(false);
            }
            catch (SdkTransportException)
            {
                return transport.CloseStatusCode ?? 408;
            }

            if (messageB is null)
            {
                return transport.CloseStatusCode ?? 408;
            }

            try
            {
                noise = handshake.ReadMessageB(messageB.Value);
            }
            catch (SdkConnectionException ex)
            {
                throw new ProtocolHaltException(ex);
            }

            try
            {
                await transport.SendAsync(handshake.WriteMessageC(), attemptToken).ConfigureAwait(false);
            }
            catch (SdkConnectionException) when (transport.CloseStatusCode is not null)
            {
                return transport.CloseStatusCode;
            }

            handshake.Dispose();
            handshake = null;

            var channel = new Channel(transport, noise);
            transport = null;
            noise = null;
            _channel = channel;

            Task<int?> watch = WatchAsync(channel, monitorToken);
            Task<byte[]> probe;
            try
            {
                probe = _router.SendQueryAsync(ProbeBody, attemptToken).AsTask();
            }
            catch (SdkException)
            {
                return await AbortChannelAsync(channel).ConfigureAwait(false);
            }

            Task winner = await Task.WhenAny(probe, watch).ConfigureAwait(false);
            if (ReferenceEquals(winner, watch))
            {
                int? dropCode = await watch.ConfigureAwait(false);
                await UninstallChannelAsync().ConfigureAwait(false);
                try
                {
                    await probe.ConfigureAwait(false);
                }
                catch (SdkException)
                {
                }

                return dropCode;
            }

            try
            {
                await probe.ConfigureAwait(false);
            }
            catch (SdkException)
            {
                return await AbortChannelAsync(channel).ConfigureAwait(false);
            }

            Emit(ConnectionState.Open, statusCode: null, 0);
            CompleteFirstOpenOk();
            return await watch.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (monitorToken.IsCancellationRequested)
            {
                throw;
            }

            return 408;
        }
        finally
        {
            if (transport is not null)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }

            handshake?.Dispose();
            noise?.Dispose();
        }
    }

    private async Task<int> AbortChannelAsync(Channel channel)
    {
        int? code = channel.Transport.CloseStatusCode;
        await UninstallChannelAsync().ConfigureAwait(false);
        return code ?? 408;
    }

    private async Task<int?> WatchAsync(Channel channel, CancellationToken monitorToken)
    {
        while (!monitorToken.IsCancellationRequested)
        {
            ReadOnlyMemory<byte>? frame;
            try
            {
                frame = await channel.Transport.ReceiveAsync(monitorToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SdkException ex)
            {
                _logger?.LogWarning(
                    ex,
                    "Frame receive failed for account {AccountId}.",
                    _accountId.Value);
                return channel.Transport.CloseStatusCode ?? 408;
            }

            if (frame is null)
            {
                return channel.Transport.CloseStatusCode ?? 408;
            }

            byte[] plain;
            try
            {
                plain = channel.Noise.Decrypt(frame.Value.Span);
            }
            catch (ObjectDisposedException)
            {
                return 408;
            }
            catch (SdkTransportException ex)
            {
                _logger?.LogWarning(
                    ex,
                    "Frame authentication failed for account {AccountId}.",
                    _accountId.Value);
                return channel.Transport.CloseStatusCode ?? 408;
            }

            if (!QueryRouter.TryParseFrame(plain, out string? id, out byte[]? body) || id is null || body is null)
            {
                _logger?.LogWarning(
                    "Frame correlation failed for account {AccountId}: frame discarded.",
                    _accountId.Value);
                continue;
            }

            if (!_router.TryDeliver(id, body))
            {
                _logger?.LogDebug(
                    "Late or unknown query reply {QueryId} discarded for account {AccountId}.",
                    id,
                    _accountId.Value);
            }
        }

        throw new OperationCanceledException(monitorToken);
    }

    private async ValueTask SendRoutedAsync(string id, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        Channel? channel = _channel;
        if (channel is null)
        {
            throw new SdkConnectionException("The connection is not open: the query was not sent.");
        }

        byte[] frame = QueryRouter.BuildFrame(id, payload);
        byte[] encrypted;
        try
        {
            encrypted = channel.Noise.Encrypt(frame);
        }
        catch (ObjectDisposedException ex)
        {
            throw new SdkConnectionException("The connection is not open: the query was not sent.", ex);
        }

        try
        {
            await channel.Transport.SendAsync(encrypted, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SdkConnectionException)
        {
            throw;
        }
        catch (ObjectDisposedException ex)
        {
            throw new SdkConnectionException("The connection is not open: the query was not sent.", ex);
        }
        catch (Exception ex)
        {
            throw new SdkTransportException("Query frame send failed.", ex);
        }
    }

    private async Task UninstallChannelAsync()
    {
        Channel? channel = Interlocked.Exchange(ref _channel, null);
        if (channel is null)
        {
            return;
        }

        _router.FailAllPending(new SdkConnectionException("Query did not complete: the connection dropped."));
        channel.Noise.Dispose();
        await channel.Transport.DisposeAsync().ConfigureAwait(false);
    }

    private IFrameTransport CreateTransportSafely()
    {
        try
        {
            return _transportFactory()
                ?? throw new InvalidOperationException("Transport factory returned no transport.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SdkException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw WrapTransport(ex, "transport factory");
        }
    }

    private SdkTransportException WrapTransport(Exception ex, string operation)
    {
        var wrapped = ex as SdkTransportException ?? new SdkTransportException($"{operation} failed.", ex);
        _logger?.LogWarning(
            ex,
            "{Operation} failed for account {AccountId}.",
            operation,
            _accountId.Value);
        return wrapped;
    }

    private void CompleteFirstOpenOk()
    {
        TaskCompletionSource? firstOpen = Interlocked.Exchange(ref _firstOpen, null);
        firstOpen?.TrySetResult();
    }

    private void CompleteFirstOpen(Exception error)
    {
        TaskCompletionSource? firstOpen = Interlocked.Exchange(ref _firstOpen, null);
        firstOpen?.TrySetException(error);
    }

    private void Emit(ConnectionState state, int? statusCode, int attempt)
    {
        _state = state;
        if (state == ConnectionState.Closing || state == ConnectionState.LoggedOut)
        {
            _logger?.LogWarning(
                "Connection {State} for account {AccountId}: status code {StatusCode}, attempt {Attempt}.",
                state,
                _accountId.Value,
                statusCode,
                attempt);
        }
        else
        {
            _logger?.LogInformation(
                "Connection {State} for account {AccountId}: attempt {Attempt}.",
                state,
                _accountId.Value,
                attempt);
        }

        EventHandler<ConnectionStateChangedEventArgs>? handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        try
        {
            handlers.Invoke(this, new ConnectionStateChangedEventArgs(state, statusCode, attempt));
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "Connection event handler failed for account {AccountId}.",
                _accountId.Value);
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new ObjectDisposedException(nameof(WaConnection));
        }
    }

    private sealed class Channel
    {
        internal Channel(IFrameTransport transport, NoiseTransport noise)
        {
            Transport = transport;
            Noise = noise;
        }

        internal IFrameTransport Transport { get; }

        internal NoiseTransport Noise { get; }
    }

    private sealed class ProtocolHaltException : Exception
    {
        internal ProtocolHaltException(SdkConnectionException inner)
            : base("Handshake rejected by validation.", inner)
        {
            InnerSdkError = inner;
        }

        internal SdkConnectionException InnerSdkError { get; }
    }

    private enum DropOutcomeKind
    {
        Retry,
        Terminal,
        Stopped,
    }

    private sealed class DropOutcome
    {
        private DropOutcome(DropOutcomeKind kind, TimeSpan delay, int nextAttempt, int? code)
        {
            Kind = kind;
            Delay = delay;
            NextAttempt = nextAttempt;
            Code = code;
        }

        internal DropOutcomeKind Kind { get; }

        internal TimeSpan Delay { get; }

        internal int NextAttempt { get; }

        internal int? Code { get; }

        internal static DropOutcome Retry(TimeSpan delay, int nextAttempt, int? code) =>
            new(DropOutcomeKind.Retry, delay, nextAttempt, code);

        internal static DropOutcome Stop(DropOutcomeKind kind, int? code) =>
            new(kind, TimeSpan.Zero, 0, code);
    }
}
