using System.Buffers;
using System.Text;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Noise;
using Iwasug.WhatsApp.Sdk.Pairing;
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
/// ephemeral, never reused; the static identity is the persisted credential-row key
/// when the payload provider carries one, fresh only on the registration path) →
/// fresh <see cref="QueryRouter"/> bound to the attempt's channel → the pump decodes
/// every inbound frame (<c>FrameReader → NoiseTransport.Decrypt →
/// BinaryNodeCodec.Decode</c>) and dispatches on the node tag: <c>success</c> opens
/// through the credential-row gate below, <c>failure</c> maps its <c>reason</c> onto
/// the drop-code table, <c>iq</c>/<c>notification</c> raise the internal
/// stanza-received event, <c>stream:error</c> halts fail-closed. Correlated query
/// replies match on the node <c>id</c> attribute.
/// </para>
/// <para>
/// Drop path (02-05 close gate): the uninstall latches the attempt's router via
/// <see cref="QueryRouter.NotifyConnectionClosing"/> — in-flight queries fail as
/// connection-lost and late sends on that router fail fast — then the pump's EOF code
/// flows into <see cref="ReconnectPolicy.Decide"/> with the backoff's current depth:
/// <c>RetryAfter</c> waits the backoff's full-jitter draw on the injected
/// <see cref="TimeProvider"/>, <c>RestartImmediate</c> (515) resets the counter with no
/// delay, <c>Terminal</c> (401/403/411/419) emits
/// <see cref="ConnectionState.LoggedOut"/> exactly once (Interlocked guard) and stops,
/// <c>SurfaceAndStop</c> (440) stops in <c>Closing</c> without retrying (no kick-war).
/// Every attempt runs through the single-flight <see cref="ReconnectAttemptGate"/> so
/// concurrent triggers collapse onto one socket+handshake. 408 carries its sub-cause in
/// distinct logs (attempt-timeout vs socket-error vs probe class — same policy, distinct
/// diagnostics per Pitfall 2).
/// Peer-protocol violations (dict-version mismatch and friends) halt the loop without
/// retry — a hostile peer is not a network flap. Every transition flows through the
/// single-writer monitor loop; handler exceptions are logged and swallowed so host bugs
/// cannot kill the loop, and <see cref="DisposeAsync"/> from inside a handler fails fast
/// instead of deadlocking.
/// </para>
/// <para>
/// Stillborn rule: the first open is reported only when a <c>success</c> stanza arrives
/// AND the account's credential row is observable by an independent read
/// (persist-before-continue, D-06). A drop that lands between the success dispatch and
/// the open emit (or a terminal code for the very first attempt) surfaces the terminal
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
    private static readonly HashSet<int> KnownCodes = [401, 403, 411, 419, 408, 428, 440, 500, 503, 515];

    private readonly AccountId _accountId;
    private readonly Func<IFrameTransport> _transportFactory;
    private readonly INoiseHandshakePayloads _payloads;
    private readonly ConnectionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WaConnection>? _logger;
    private readonly ReconnectBackoff _backoff;
    private readonly ReconnectAttemptGate _reconnectGate = new();
    private readonly CancellationTokenSource _monitorCts = new();

    private volatile Channel? _channel;
    private byte[]? _lastHandshakeHash;
    private volatile TaskCompletionSource? _firstOpen;
    private readonly object _startupGate = new();
    private CancellationTokenSource? _hostLinkedCts;
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
    /// Raised (on the pump loop) for inbound <c>iq</c> and <c>notification</c> stanzas
    /// that no query waiter consumed. Internal by design: the pairing coordinator
    /// (plan 03-03) plugs in here; hosts never observe it.
    /// </summary>
    /// <remarks>
    /// Same handler contract as <see cref="StateChanged"/>: return quickly, never call
    /// <see cref="DisposeAsync"/>, exceptions are logged and swallowed. The node is
    /// passed by reference and must not be mutated.
    /// </remarks>
    internal event EventHandler<BinaryNode>? StanzaReceived;

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
        _backoff = new ReconnectBackoff(_options, _timeProvider);
    }

    /// <summary>Current host-observable state (written by the monitor loop only).</summary>
    public ConnectionState State => _state;

    /// <summary>
    /// Starts the connection lifecycle and completes at the first
    /// <see cref="ConnectionState.Open"/>: header → Noise handshake → success-gated
    /// open. A background monitor then owns reconnects until
    /// <see cref="DisposeAsync"/>. May be called once.
    /// </summary>
    /// <param name="cancellationToken">
    /// Governs the wait for the first open only (StoreRegistry precedent): cancelling
    /// before open aborts the handshake and surfaces unwrapped; cancelling after open is
    /// harmless (the monitor observes it only until the first open resolves).
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

        // The monitor observes host-cancel and dispose-cancel through one linked source
        // (synchronous, context-free cancellation with no callbacks to lose). The linked
        // source is created and published under the startup gate (StoreRegistry
        // _lifecycleGate precedent) so DisposeAsync can never miss it, and it lives
        // until DisposeAsync — the host token matters only until the first open.
        CancellationToken monitorToken;
        lock (_startupGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(WaConnection));
            }

            var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _monitorCts.Token);
            _hostLinkedCts = linked;
            monitorToken = linked.Token;
            _monitorTask = Task.Run(() => MonitorLoopAsync(monitorToken), CancellationToken.None);
        }

        await firstOpen.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The handshake transcript hash of the latest successful handshake (both parties
    /// agree it once the exchange completes). Public-channel value, never secret — the
    /// minimal Phase 1 store-seam proof round-trips these bytes; the Phase 3 resume
    /// proof additionally gates <see cref="ConnectionState.Open"/> on the durable
    /// credential row, not on this hash.
    /// </summary>
    internal byte[]? LastHandshakeHash
    {
        get
        {
            byte[]? hash = Volatile.Read(ref _lastHandshakeHash);
            return hash is null ? null : (byte[])hash.Clone();
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
        Channel? channel = _channel ?? throw new SdkConnectionException("The connection is not open: the query was not sent.");

        try
        {
            return await channel.Router.SendQueryAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex) when (channel != _channel)
        {
            // The attempt turned over mid-send: the query never reached the new channel,
            // so it fails fast as not-open (same class as the pre-send check above).
            // (A plain volatile read — itself atomic — is the correct comparison here.)
            throw new SdkConnectionException("The connection is not open: the query was not sent.", ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Task? monitor;
        CancellationTokenSource? linked;
        lock (_startupGate)
        {
            monitor = _monitorTask;
            linked = _hostLinkedCts;
            _hostLinkedCts = null;
        }

        if (monitor is not null && Task.CurrentId == monitor.Id)
        {
            throw new InvalidOperationException("DisposeAsync must not be called from a connection event handler.");
        }

        List<Exception>? failures = null;
        _monitorCts.Cancel();
        if (monitor is not null)
        {
            try
            {
                await monitor.ConfigureAwait(false);
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

        linked?.Dispose();
        _monitorCts.Dispose();
        if (failures is not null)
        {
            throw new AggregateException("One or more connection resources failed to dispose.", failures);
        }
    }

    private async Task MonitorLoopAsync(CancellationToken monitorToken)
    {
        while (!monitorToken.IsCancellationRequested)
        {
            Emit(ConnectionState.Connecting, statusCode: null, _backoff.Attempt);
            DropOutcome outcome;
            try
            {
                (int? Code, bool Opened) attempt;
                try
                {
                    attempt = await _reconnectGate.RunAsync(() => ConnectAndWatchAsync(monitorToken), monitorToken).ConfigureAwait(false);
                }
                finally
                {
                    // Teardown runs on EVERY attempt exit — normal, protocol-halt,
                    // cancellation, or fault — so no path leaves the channel installed
                    // with a never-latched router (in-flight queries would wait out the
                    // full query timeout instead of failing fast; 02-REVIEW CR-02).
                    // The success path inside ConnectAndWatchAsync may have already
                    // uninstalled; this net is idempotent (null-exchange) and its own
                    // failure must not mask the attempt's outcome, so it logs instead
                    // of throwing.
                    try
                    {
                        await UninstallChannelAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Channel teardown failed for account {AccountId}.", _accountId.Value);
                    }
                }

                if (attempt.Opened)
                {
                    _backoff.Reset();
                }

                outcome = MapDropToOutcome(attempt.Code);
            }
            catch (ProtocolHaltException ex)
            {
                StopForProtocolViolation(ex.InnerSdkError);
                return;
            }
            catch (OperationCanceledException)
            {
                if (_firstOpen is not null)
                {
                    Emit(ConnectionState.Closing, statusCode: null, _backoff.Attempt);
                }

                return;
            }
            catch (Exception ex)
            {
                if (monitorToken.IsCancellationRequested)
                {
                    return;
                }

                _logger?.LogError(ex, "Connection loop fault for account {AccountId}.", _accountId.Value);
                outcome = DropOutcome.Retry(408);
            }

            switch (outcome.Kind)
            {
                case DropOutcomeKind.Terminal:
                    if (Interlocked.CompareExchange(ref _loggedOut, 1, 0) == 0)
                    {
                        Emit(ConnectionState.LoggedOut, outcome.Code, _backoff.Attempt);
                    }

                    CompleteFirstOpen(new SdkConnectionException("Connection terminated: the account is logged out."));
                    return;
                case DropOutcomeKind.Stopped:
                    Emit(ConnectionState.Closing, outcome.Code, _backoff.Attempt);
                    CompleteFirstOpen(new SdkConnectionException("Connection stopped: the session state needs host action."));
                    return;
                case DropOutcomeKind.Restart:
                    // 515 restarts with the counter reset and no delay: it must not
                    // consume attempt depth (the backoff draw below always advances).
                    _backoff.Reset();
                    Emit(ConnectionState.Closing, outcome.Code, _backoff.Attempt);
                    break;
                default:
                    Emit(ConnectionState.Closing, outcome.Code, _backoff.Attempt);
                    try
                    {
                        TimeSpan waited = await _backoff.WaitAsync(monitorToken).ConfigureAwait(false);
                        _logger?.LogInformation(
                            "Reconnecting for account {AccountId}: status code {StatusCode}, backoff {Delay}, attempt {Attempt}.",
                            _accountId.Value,
                            outcome.Code,
                            waited,
                            _backoff.Attempt);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    break;
            }
        }
    }

    private DropOutcome MapDropToOutcome(int? code)
    {
        int answered = code ?? 408;
        if (code is not null && !KnownCodes.Contains(code.Value))
        {
            _logger?.LogWarning(
                "Unknown disconnect code {StatusCode} for account {AccountId}: bounded retry; possible protocol drift.",
                code.Value,
                _accountId.Value);
        }

        ReconnectDecision decision = ReconnectPolicy.Decide(answered, _backoff.Attempt, _options);
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
                return DropOutcome.Restart(answered);
            default:
                // The authoritative delay comes from the backoff's own draw in the loop
                // above; RetryAfter.Delay serves direct Decide consumers.
                return DropOutcome.Retry(answered);
        }
    }

    private void StopForProtocolViolation(SdkConnectionException error)
    {
        _logger?.LogError(
            error,
            "Handshake rejected for account {AccountId}: stopping without retry.",
            _accountId.Value);
        Emit(ConnectionState.Closing, statusCode: null, _backoff.Attempt);
        CompleteFirstOpen(error);
    }

    private async Task<(int? Code, bool Opened)> ConnectAndWatchAsync(CancellationToken monitorToken)
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

            // Phase 3 resume (03-01 tracer): when the payload provider carries a
            // credential snapshot seam, load it BEFORE the handshake so message C uses
            // the persisted static identity (whatsmeow handshake.go behavior: the
            // static comes from the store NoiseKey) instead of a fresh one. A throwing
            // read recycles as 408 — failure is never conflated with missing (D-19);
            // a missing row keeps the fresh-identity registration path.
            byte[]? resumeStatic = null;
            if (_payloads is ICredentialHandshakePayloads credentialPayloads)
            {
                try
                {
                    resumeStatic = await credentialPayloads.LoadCredentialsAsync(attemptToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (SdkException ex)
                {
                    _logger?.LogWarning(
                        ex,
                        "Credential read failed for account {AccountId}: recycling as 408 (read-failure class, never conflated with missing).",
                        _accountId.Value);
                    return (transport.CloseStatusCode ?? 408, false);
                }
            }

            handshake = resumeStatic is null
                ? new NoiseHandshake(_payloads)
                : new NoiseHandshake(_payloads, resumeStatic);
            try
            {
                await transport.SendAsync(NoiseProtocol.WaHeader.ToArray(), attemptToken).ConfigureAwait(false);
                await transport.SendAsync(WaFrame.Frame(handshake.WriteMessageA()), attemptToken).ConfigureAwait(false);
            }
            catch (SdkConnectionException) when (transport.CloseStatusCode is not null)
            {
                return (transport.CloseStatusCode, false);
            }

            // Server shape is intro || length-prefixed body: the intro must be
            // validated and stripped BEFORE deframing — the intro's own bytes are
            // not a length prefix, so deframe-first would misread them as one.
            // Headerless B (loopback shape) skips straight to deframing.
            // Fragmentation is structural (Pitfall 3): the intro and/or the frame
            // may arrive split across receives, so accumulate until one complete
            // frame is buffered — a partial frame retries the read, only a
            // definitively malformed frame halts (Rule 2). The attempt timeout
            // bounds the assembly.
            var pending = new ArrayBufferWriter<byte>();
            bool introChecked = false;
            byte[]? messageBBody = null;
            while (messageBBody is null)
            {
                ReadOnlyMemory<byte>? chunk;
                try
                {
                    chunk = await transport.ReceiveAsync(attemptToken).ConfigureAwait(false);
                }
                catch (SdkTransportException)
                {
                    _logger?.LogInformation(
                        "Handshake read failed for account {AccountId}: recycling as 408 (socket-error class, distinct from attempt-timeout 408).",
                        _accountId.Value);
                    return (transport.CloseStatusCode ?? 408, false);
                }

                if (chunk is null)
                {
                    int nullCode = transport.CloseStatusCode ?? 408;
                    if (transport.CloseStatusCode is null)
                    {
                        _logger?.LogInformation(
                            "Handshake stream ended without a close code for account {AccountId}: recycling as 408 (socket-error class).",
                            _accountId.Value);
                    }

                    return (nullCode, false);
                }

                chunk.Value.Span.CopyTo(pending.GetSpan(chunk.Value.Length));
                pending.Advance(chunk.Value.Length);

                ReadOnlySpan<byte> buffered = pending.WrittenMemory.Span;
                if (!introChecked && buffered.Length >= WaFrame.PrefixWidth)
                {
                    if (buffered[0] == NoiseProtocol.WaHeader[0]
                        && buffered[1] == NoiseProtocol.WaHeader[1]
                        && buffered[2] == NoiseProtocol.WaHeader[2])
                    {
                        if (buffered.Length < NoiseProtocol.WaHeader.Length)
                        {
                            continue; // The intro's last byte is still in flight.
                        }

                        try
                        {
                            NoiseHandshake.ValidateIntroHeader(buffered[..NoiseProtocol.WaHeader.Length]);
                        }
                        catch (SdkConnectionException ex)
                        {
                            throw new ProtocolHaltException(ex);
                        }

                        int introLength = NoiseProtocol.WaHeader.Length;
                        pending = new ArrayBufferWriter<byte>();
                        buffered[introLength..].CopyTo(pending.GetSpan(buffered.Length - introLength));
                        pending.Advance(buffered.Length - introLength);
                    }

                    introChecked = true;
                }

                buffered = pending.WrittenMemory.Span;
                if (buffered.Length < WaFrame.PrefixWidth)
                {
                    continue; // Not even a full length prefix yet.
                }

                int declaredLength = (buffered[0] << 16) | (buffered[1] << 8) | buffered[2];
                if (declaredLength > NoiseProtocol.MaxFrameBytes)
                {
                    throw new ProtocolHaltException(
                        new SdkConnectionException(
                            "Handshake message B failed validation.",
                            new SdkTransportException("Frame length exceeds the maximum frame size.")));
                }

                if (buffered.Length - WaFrame.PrefixWidth < declaredLength)
                {
                    continue; // The frame body is still arriving — retry the read.
                }

                try
                {
                    messageBBody = WaFrame.Deframe(buffered).ToArray();
                }
                catch (SdkTransportException ex)
                {
                    // Trailing bytes after a complete frame: definitively malformed.
                    throw new ProtocolHaltException(
                        new SdkConnectionException("Handshake message B failed validation.", ex));
                }
            }

            try
            {
                noise = handshake.ReadMessageB(messageBBody);
            }
            catch (SdkConnectionException ex)
            {
                throw new ProtocolHaltException(ex);
            }

            byte[] messageC = handshake.WriteMessageC();
            byte[] transcriptHash = handshake.HandshakeHash;
            try
            {
                await transport.SendAsync(WaFrame.Frame(messageC), attemptToken).ConfigureAwait(false);
            }
            catch (SdkConnectionException) when (transport.CloseStatusCode is not null)
            {
                return (transport.CloseStatusCode, false);
            }

            handshake.Dispose();
            handshake = null;

            // The router is per-attempt: the drop path latches it via
            // NotifyConnectionClosing (fail-fast + connection-lost failures) while the
            // next attempt mints a fresh one — a latched router never leaks into a new
            // attempt. The sender closure captures the channel variable (assigned just
            // below, before any send can flow) so each router always drives its own
            // channel even across handoffs.
            Channel? current = null;
            ValueTask RouteViaCurrent(string id, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
                SendRoutedAsync(
                    current ?? throw new InvalidOperationException("Query router used before its channel was published."),
                    id,
                    payload,
                    ct);
            var router = new QueryRouter(RouteViaCurrent, _options, _timeProvider);
            current = new Channel(transport, noise, router);
            transport = null;
            noise = null;
            _channel = current;

            // The open gate (03-01 tracer): no probe is sent — the pump resolves
            // this gate only for a <success> stanza whose credential row is
            // observable by an independent read. The attempt timeout still bounds
            // the wait (an attempt that exceeds ConnectTimeoutMs recycles as 408,
            // the same class as the handshake attempt-timeout).
            var openGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<int?> watch = WatchAsync(current, monitorToken, openGate);
            Task openTimeout = Task.Delay(TimeSpan.FromMilliseconds(_options.ConnectTimeoutMs), _timeProvider, monitorToken);

            Task winner = await Task.WhenAny(openGate.Task, watch, openTimeout).ConfigureAwait(false);
            if (ReferenceEquals(winner, watch))
            {
                int? dropCode = await watch.ConfigureAwait(false);
                await UninstallChannelAsync().ConfigureAwait(false);
                return (dropCode, false);
            }

            if (!ReferenceEquals(winner, openGate.Task) || !openGate.Task.IsCompletedSuccessfully)
            {
                monitorToken.ThrowIfCancellationRequested();
                _logger?.LogInformation(
                    "Connect attempt exceeded the configured timeout waiting for success for account {AccountId}: recycling as 408 (success-wait class, distinct from socket-error 408).",
                    _accountId.Value);
                await UninstallChannelAsync().ConfigureAwait(false);
                return (408, false);
            }

            Emit(ConnectionState.Open, statusCode: null, _backoff.Attempt);
            CompleteFirstOpenOk();
            Volatile.Write(ref _lastHandshakeHash, transcriptHash);
            int? finalCode = await watch.ConfigureAwait(false);
            return (finalCode, true);
        }
        catch (OperationCanceledException)
        {
            if (monitorToken.IsCancellationRequested)
            {
                throw;
            }

            _logger?.LogInformation(
                "Connect attempt exceeded the configured timeout for account {AccountId}: recycling as 408 (attempt-timeout class, distinct from socket-error 408).",
                _accountId.Value);
            return (408, false);
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

    private async Task<int?> WatchAsync(Channel channel, CancellationToken monitorToken, TaskCompletionSource openGate)
    {
        var buffered = new ArrayBufferWriter<byte>();
        while (!monitorToken.IsCancellationRequested)
        {
            ReadOnlyMemory<byte>? chunk;
            try
            {
                chunk = await channel.Transport.ReceiveAsync(monitorToken).ConfigureAwait(false);
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

            if (chunk is null)
            {
                return channel.Transport.CloseStatusCode ?? 408;
            }

            chunk.Value.Span.CopyTo(buffered.GetSpan(chunk.Value.Length));
            buffered.Advance(chunk.Value.Length);

            // Production pump: every buffered byte is drained through the shared
            // FrameReader (flag strip + compression) before NoiseTransport.Decrypt,
            // then decoded as a binary node and dispatched on its tag — with
            // id-attribute correlation checked first so query replies complete
            // their waiter before any stanza handling. Zero hand-rolled framing
            // remains on the inbound path.
            ReadOnlySequence<byte> buffer = new(buffered.WrittenMemory);
            try
            {
                while (FrameReader.TryReadFrame(ref buffer, out byte[]? payload))
                {
                    byte[] plain;
                    try
                    {
                        plain = channel.Noise.Decrypt(payload!);
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

                    BinaryNode? node;
                    try
                    {
                        node = BinaryNodeCodec.Decode(plain);
                    }
                    catch (SdkFormatException ex)
                    {
                        _logger?.LogWarning(
                            ex,
                            "Frame node decode failed for account {AccountId}: frame discarded.",
                            _accountId.Value);
                        continue;
                    }

                    if (node.Attrs.TryGetValue(QueryRouter.IdAttribute, out string? id)
                        && !string.IsNullOrEmpty(id))
                    {
                        byte[] body = node.Data
                            ?? (node.Text is not null ? Encoding.UTF8.GetBytes(node.Text) : BinaryNodeCodec.Encode(node));
                        if (channel.Router.TryDeliver(id, body))
                        {
                            continue;
                        }

                        _logger?.LogDebug(
                            "Late or unknown query reply {QueryId} discarded for account {AccountId}.",
                            id,
                            _accountId.Value);
                        // An uncorrelated iq/notification still reaches the stanza
                        // event below; other tags fall through to their dispatch.
                    }

                    switch (node.Tag)
                    {
                        case "success":
                            await HandleSuccessAsync(openGate, monitorToken).ConfigureAwait(false);
                            break;
                        case "failure":
                            return MapFailureCode(node);
                        case "iq":
                        case "notification":
                            RaiseStanzaReceived(node);
                            break;
                        case "stream:error":
                            throw new ProtocolHaltException(
                                new SdkConnectionException("Stream error received: halting without retry."));
                        default:
                            _logger?.LogDebug(
                                "Unknown stanza tag {Tag} discarded for account {AccountId}.",
                                node.Tag,
                                _accountId.Value);
                            break;
                    }
                }
            }
            catch (SdkTransportException ex)
            {
                // A post-handshake framing violation (empty payload, invalid zlib
                // envelope, over-cap length) is a hostile-peer protocol violation,
                // not a network flap: fail closed via ProtocolHaltException (Rule 2,
                // matching the message-B path) instead of faulting the watch task
                // into an unbounded 408-retry loop. The monitor's attempt-finally
                // still uninstalls the channel (02-REVIEW CR-02).
                _logger?.LogWarning(
                    ex,
                    "Frame framing validation failed for account {AccountId}.",
                    _accountId.Value);
                throw new ProtocolHaltException(
                    new SdkConnectionException("The connection framing failed validation.", ex));
            }

            buffered.Clear();
            if (!buffer.IsEmpty)
            {
                byte[] remainder = buffer.ToArray();
                remainder.CopyTo(buffered.GetSpan(remainder.Length));
                buffered.Advance(remainder.Length);
            }
        }

        throw new OperationCanceledException(monitorToken);
    }

    /// <summary>
    /// The Open gate (03-01 tracer): <c>Open</c> fires only for a received
    /// <c>success</c> stanza whose credential row is observable by an INDEPENDENT
    /// read — a second read after dispatch, proving the row is durable (D-06), not
    /// the same read that fed message C. Failure ≠ missing (D-19): a throwing read
    /// withholds Open and keeps waiting (the attempt still ends on drop or timeout);
    /// a missing row withholds Open the same way. Either way nothing is emitted —
    /// the prohibition is "no success-plus-row, no Open", on every input.
    /// </summary>
    private async Task HandleSuccessAsync(TaskCompletionSource openGate, CancellationToken monitorToken)
    {
        CredentialRecord? row = null;
        if (_payloads is ICredentialHandshakePayloads credentialPayloads)
        {
            try
            {
                row = await credentialPayloads.ReadCredentialsAsync(monitorToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SdkException ex)
            {
                _logger?.LogWarning(
                    ex,
                    "Credential read failed for account {AccountId}: success observed but open withheld (read-failure class, never conflated with missing).",
                    _accountId.Value);
                return;
            }
        }

        if (row is null)
        {
            _logger?.LogWarning(
                "Success stanza arrived with no observable credential row for account {AccountId}: open withheld.",
                _accountId.Value);
            return;
        }

        openGate.TrySetResult();
    }

    /// <summary>
    /// Maps a <c>failure</c> stanza's <c>reason</c> attribute onto the drop-code table:
    /// the numeric code stays observable even when no <see cref="DisconnectReason"/>
    /// member exists yet (plan 03-02 adds <c>ClientOutdated = 405</c>) — the policy
    /// classifies it from here. A missing or unparsable reason recycles as 408.
    /// </summary>
    private int MapFailureCode(BinaryNode node)
    {
        if (node.Attrs.TryGetValue("reason", out string? reason)
            && int.TryParse(reason, out int code))
        {
            _logger?.LogWarning(
                "Server failure stanza received for account {AccountId}: status code {StatusCode}.",
                _accountId.Value,
                code);
            return code;
        }

        _logger?.LogWarning(
            "Server failure stanza without a reason code for account {AccountId}: recycling as 408.",
            _accountId.Value);
        return 408;
    }

    private void RaiseStanzaReceived(BinaryNode node)
    {
        EventHandler<BinaryNode>? handlers = StanzaReceived;
        if (handlers is null)
        {
            return;
        }

        try
        {
            handlers.Invoke(this, node);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "Stanza handler failed for account {AccountId}.",
                _accountId.Value);
        }
    }

    private async ValueTask SendRoutedAsync(Channel channel, string id, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
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
            byte[] flagged = new byte[encrypted.Length + 1];
            encrypted.CopyTo(flagged.AsSpan(1));
            await channel.Transport.SendAsync(WaFrame.Frame(flagged), cancellationToken).ConfigureAwait(false);
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

        // Drop trigger for the 02-04 closing contract: pending queries fail fast as
        // connection-lost (typed SdkConnectionException) and later sends on this
        // attempt's router fail fast too — while the next attempt mints a fresh router.
        channel.Router.NotifyConnectionClosing();
        channel.Noise.Dispose();
        await channel.Transport.DisposeAsync().ConfigureAwait(false);
        await channel.Router.DisposeAsync().ConfigureAwait(false);
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
        SdkTransportException wrapped = ex as SdkTransportException ?? new SdkTransportException($"{operation} failed.", ex);
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
        internal Channel(IFrameTransport transport, NoiseTransport noise, QueryRouter router)
        {
            Transport = transport;
            Noise = noise;
            Router = router;
        }

        internal IFrameTransport Transport { get; }

        internal NoiseTransport Noise { get; }

        internal QueryRouter Router { get; }
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
        Restart,
        Terminal,
        Stopped,
    }

    private sealed class DropOutcome
    {
        private DropOutcome(DropOutcomeKind kind, int? code)
        {
            Kind = kind;
            Code = code;
        }

        internal DropOutcomeKind Kind { get; }

        internal int? Code { get; }

        internal static DropOutcome Retry(int? code) =>
            new(DropOutcomeKind.Retry, code);

        internal static DropOutcome Restart(int? code) =>
            new(DropOutcomeKind.Restart, code);

        internal static DropOutcome Stop(DropOutcomeKind kind, int? code) =>
            new(kind, code);
    }
}

/// <summary>
/// Single-flight gate for reconnect attempts (02-05): concurrent triggers collapse onto
/// the one in-flight attempt instead of multiplying sockets and handshakes. D-24
/// Lazy-Task memoization — the first caller starts the attempt, concurrent callers
/// attach to its task; a completed attempt (success or fault) evicts, so the next
/// trigger starts fresh instead of observing a poisoned result.
/// </summary>
/// <remarks>
/// The monitor loop is the only trigger today (single caller), so the collapse is
/// structural insurance for future trigger paths — and it is directly unit-proven
/// (parallel-drop facts in ConnectionStateTests). A caller that cancels while attached
/// observes its own cancellation without disturbing the attempt other callers await.
/// </remarks>
internal sealed class ReconnectAttemptGate
{
    private readonly object _gate = new();
    private Task? _inFlight;

    /// <summary>
    /// Runs <paramref name="attempt"/> unless one is already in flight, in which case
    /// the caller awaits the shared attempt. Each caller still observes its own
    /// <paramref name="callerToken"/> cancellation.
    /// </summary>
    /// <typeparam name="T">The attempt's result type.</typeparam>
    /// <param name="attempt">The reconnect attempt (started at most once per flight).</param>
    /// <param name="callerToken">The caller's cancellation (unwrapped, attempt-safe).</param>
    /// <returns>The attempt's result.</returns>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    internal async Task<T> RunAsync<T>(Func<Task<T>> attempt, CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        Task<T> shared;
        lock (_gate)
        {
            shared = _inFlight is Task<T> running && !running.IsCompleted ? running : (Task<T>)(_inFlight = RunInnerAsync(attempt));
        }

        if (callerToken.CanBeCanceled)
        {
            using var attached = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            var waiter = Task.Delay(Timeout.Infinite, attached.Token);
            Task winner = await Task.WhenAny(shared, waiter).ConfigureAwait(false);
            if (ReferenceEquals(winner, waiter))
            {
                callerToken.ThrowIfCancellationRequested();
            }

            attached.Cancel();
        }

        return await shared.ConfigureAwait(false);
    }

    private static async Task<T> RunInnerAsync<T>(Func<Task<T>> attempt) =>
        await attempt().ConfigureAwait(false);
}
