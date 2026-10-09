using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Stores;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;

namespace Iwasug.WhatsApp.Sdk.Pairing;

/// <summary>
/// One account's QR pairing attempt driver (03-03 tracer, CONN-01): the host calls
/// <see cref="StartAsync"/> once per attempt and observes every outcome — code,
/// rotation codes, success, timeout, logged-out, client-outdated — on
/// <see cref="StateChanged"/>. The coordinator plugs into the seams plan 03-01
/// opened (internal stanza-received event for <c>iq</c>/<c>notification</c>) and
/// plan 03-02 opened (pairing-terminal seam plus <c>WaVersionResolver</c> 405
/// invalidation).
/// </summary>
/// <remarks>
/// <para>
/// Scripted stanza shapes (repo-local, A1): the live pair-device/pair-success
/// wire shapes are not yet observed against a real server, so this plan scripts
/// them — request <c>&lt;iq id type="get"&gt;&lt;pair-device/&gt;</c>, ref reply
/// <c>&lt;iq id type="result"&gt;&lt;pair-device ref="…"/&gt;</c>,
/// pairing-level error <c>&lt;iq id type="error"&gt;&lt;error code="405"/&gt;</c>,
/// success <c>&lt;notification&gt;&lt;pair-success user/&gt;</c> (an <c>iq</c>
/// carrying <c>pair-success</c> is accepted too). The ref rides an attribute,
/// not text content: content-slot raw strings decode as <c>Data</c> bytes
/// (BinaryNodeCodec header) while attr values roundtrip losslessly as strings.
/// The loopback tests are the executable definition; the end-of-phase human
/// proof confirms or corrects them.
/// </para>
/// <para>
/// Attempt rules: one live attempt at a time (a second start fails with a
/// descriptive error naming the existing attempt — never a silent replace);
/// every rotation code comes from a fresh server-issued ref, requested only
/// after the live code's lifetime elapses (the rotation intervals are
/// <see cref="PairingQrPayload"/>'s shared constants — the gate is ban-safety,
/// not politeness: back-to-back ref requests would hammer the server); at most
/// <see cref="MaxCodesPerAttempt"/> codes, then the attempt ends in
/// <see cref="PairingStatus.Timeout"/>; terminal states are one-shot (no event
/// after a terminal); a 503-class drop never terminates the attempt (Phase-2
/// reconnect semantics — the pending request is re-issued when the next channel
/// is ready); a pairing-level 405 invalidates the cached WA version and retries
/// at most <see cref="MaxVersionRefetches"/> times before
/// <see cref="PairingStatus.ClientOutdated"/> (ban-safety: no refetch hammering,
/// 03-RESEARCH.md Pitfall 1).
/// </para>
/// <para>
/// Success boundary (plan 03-04 owns the rest): the pair-success payload is
/// passed through UNVERIFIED — no HMAC/signature check, no credential write. On
/// <see cref="PairingStatus.Success"/> this coordinator does not persist; plan
/// 03-04 owns the verify-then-persist ordering through the
/// <see cref="CompletePairSuccessAsync"/> seam below (which currently throws
/// <see cref="NotSupportedException"/>).
/// </para>
/// <para>
/// Secret hygiene (T-3-06/T-3-07): the reference is opaque pass-through (never
/// parsed, never logged); QR key material is derived per code from the persisted
/// row and zeroized immediately after the payload string is built; this type logs
/// operation/rule names and the account id only — never refs, payloads, or key
/// material. Platform/NSec CSPRNG only; no hand-rolled curve math (public keys
/// derive via NSec X25519 import/export).
/// </para>
/// </remarks>
internal sealed class PairingCoordinator
{
    /// <summary>
    /// Maximum pairing-level 405 retries per attempt: each retry invalidates and
    /// re-resolves the cached WA version, then re-issues the pairing request. A
    /// small named bound — never an open-ended loop (Pitfall 1).
    /// </summary>
    internal const int MaxVersionRefetches = 3;

    /// <summary>Maximum QR codes per attempt (60s first + 5 × 20s = the 160s window).</summary>
    internal const int MaxCodesPerAttempt = 6;

    private const string PairDeviceTag = "pair-device";
    private const string RefAttribute = "ref";
    private const string ErrorTag = "error";
    private const string PairSuccessTag = "pair-success";
    private const string CodeAttribute = "code";
    private const string UserAttribute = "user";
    private const string StaleClientCode = "405";

    private readonly AccountId _accountId;
    private readonly HandshakePayloadProvider _payloads;
    private readonly ICredentialStore _credentials;
    private readonly Func<BinaryNode, CancellationToken, ValueTask> _sender;
    private readonly Func<bool> _hasChannel;
    private readonly TimeProvider _timeProvider;
    private readonly Func<WaVersionResolver?> _resolver;
    private readonly ILogger<PairingCoordinator>? _logger;
    private readonly object _gate = new();
    private Attempt? _attempt;
    private long _requestSequence;
    private int _channelPulses;
    private TaskCompletionSource _channelSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Raised on every pairing transition, from the pairing attempt loop and the
    /// stanza/terminal handlers.
    /// </summary>
    /// <remarks>
    /// Same handler contract as <see cref="WaConnection.StateChanged"/>: return
    /// quickly, never call dispose, exceptions are logged and swallowed.
    /// </remarks>
    public event EventHandler<PairingStateChangedEventArgs>? StateChanged;

    /// <summary>Creates the coordinator over one account's pairing seams.</summary>
    /// <param name="accountId">Opaque account key — must come from <see cref="AccountId.Create"/>.</param>
    /// <param name="payloads">Credential-aware handshake payloads (registration-bootstrap seam).</param>
    /// <param name="credentials">The account's credential store (QR key material source).</param>
    /// <param name="sender">Sends one pairing node on the current channel (the connection owns the channel).</param>
    /// <param name="hasChannel">Whether a channel is currently installed (no send is attempted without one).</param>
    /// <param name="timeProvider">Clock for rotation/timeout delays; defaults to system time.</param>
    /// <param name="resolver">Live lookup of the connection's version resolver (405 invalidation path).</param>
    /// <param name="logger">Optional per-account logger (Abstractions only).</param>
    /// <exception cref="SdkFormatException"><paramref name="accountId"/> is the default (uninitialized) value.</exception>
    /// <exception cref="ArgumentNullException">Any required seam is null.</exception>
    internal PairingCoordinator(
        AccountId accountId,
        HandshakePayloadProvider payloads,
        ICredentialStore credentials,
        Func<BinaryNode, CancellationToken, ValueTask> sender,
        Func<bool> hasChannel,
        TimeProvider? timeProvider = null,
        Func<WaVersionResolver?>? resolver = null,
        ILogger<PairingCoordinator>? logger = null)
    {
        if (accountId.Value is null)
        {
            throw new SdkFormatException(
                "AccountId must be created via AccountId.Create — the default (uninitialized) AccountId is not valid.");
        }

        _accountId = accountId;
        _payloads = payloads ?? throw new ArgumentNullException(nameof(payloads));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _hasChannel = hasChannel ?? throw new ArgumentNullException(nameof(hasChannel));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _resolver = resolver ?? (() => null);
        _logger = logger;
    }

    /// <summary>
    /// Begins one QR pairing attempt and runs it to its terminal state: emits
    /// <see cref="PairingStatus.Code"/> per server-issued ref on the rotation
    /// schedule, then exactly one terminal (<c>Success</c>, <c>Timeout</c>,
    /// <c>LoggedOut</c>, or <c>ClientOutdated</c>).
    /// </summary>
    /// <param name="cancellationToken">
    /// Host cancellation: aborts the attempt with no terminal event (an
    /// <see cref="OperationCanceledException"/> surfaces here, unwrapped).
    /// </param>
    /// <exception cref="OperationCanceledException">The host cancelled.</exception>
    /// <exception cref="InvalidOperationException">Another attempt is live.</exception>
    /// <exception cref="Errors.SdkStoreException">The registration bootstrap found no credential row.</exception>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Attempt attempt;
        lock (_gate)
        {
            if (_attempt is { } live && live.Terminal == 0)
            {
                throw new InvalidOperationException(
                    $"A pairing attempt is already live (started {live.StartedAt:o}); " +
                    "wait for its terminal event or cancel it before starting another.");
            }

            attempt = new Attempt(
                _timeProvider.GetUtcNow(),
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
            _attempt = attempt;
        }

        try
        {
            // Fail closed (T-3-07): a store write/read failure aborts the start —
            // no pairing frame is sent, no Code event fires.
            await _payloads.EnsureRegistrationMaterialAsync(attempt.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ClearIfCurrent(attempt);
            throw;
        }
        catch (Exception)
        {
            ClearIfCurrent(attempt);
            throw;
        }

        try
        {
            await RunAttemptAsync(attempt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (attempt.Terminal != 0)
        {
            // A terminal already fired its event — the cancellation below is just
            // the mechanism that unwinds the in-flight wait, not an outcome.
            return;
        }
        finally
        {
            ClearIfCurrent(attempt);
            attempt.Cts.Dispose();
        }
    }

    /// <summary>
    /// Stanza-received handler (subscribes to the connection's internal stanza
    /// event): routes pair-success notifications to <c>Success</c> and correlates
    /// ref/error replies to the pending pairing request by node id.
    /// </summary>
    /// <param name="sender">The connection (unused).</param>
    /// <param name="node">The inbound <c>iq</c> or <c>notification</c> node (never mutated).</param>
    internal void OnStanzaReceived(object? sender, BinaryNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        Attempt? attempt = Volatile.Read(ref _attempt);
        if (attempt is null || attempt.Terminal != 0)
        {
            return;
        }

        if (TryExtractPairSuccess(node, out string? user))
        {
            Terminate(attempt, PairingStatus.Success, user);
            return;
        }

        if (!string.Equals(node.Tag, "iq", StringComparison.Ordinal))
        {
            return;
        }

        string? pendingId = Volatile.Read(ref attempt.PendingId);
        if (pendingId is null
            || !node.Attrs.TryGetValue("id", out string? id)
            || !string.Equals(id, pendingId, StringComparison.Ordinal))
        {
            return;
        }

        if (TryExtractRef(node, out string? pairingRef))
        {
            attempt.PendingRef?.TrySetResult(new RefOutcome.Ref(pairingRef));
            return;
        }

        if (IsErrorCode(node, StaleClientCode))
        {
            attempt.PendingRef?.TrySetResult(new RefOutcome.Error405());
        }

        // Any other iq error is ignored: rotation and the attempt timeout bound
        // the attempt, and only a 405 carries version meaning.
    }

    /// <summary>
    /// Pairing-terminal handler (subscribes to the connection's 03-02 seam):
    /// every terminal disconnect ends the live attempt — 405 as
    /// <c>ClientOutdated</c>, every other terminal reason as <c>LoggedOut</c>.
    /// </summary>
    /// <param name="sender">The connection (unused).</param>
    /// <param name="e">The terminal reason.</param>
    internal void OnPairingTerminal(object? sender, PairingTerminalEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        Attempt? attempt = Volatile.Read(ref _attempt);
        if (attempt is null || attempt.Terminal != 0)
        {
            return;
        }

        if (e.Reason == DisconnectReason.ClientOutdated)
        {
            Terminate(attempt, PairingStatus.ClientOutdated, null);
        }
        else
        {
            Terminate(attempt, PairingStatus.LoggedOut, null);
        }
    }

    /// <summary>
    /// Channel-ready handler (subscribes to the connection's internal channel
    /// signal): wakes channel waiters so a pending pairing request is re-issued
    /// on the fresh channel after a 503-class reconnect.
    /// </summary>
    /// <param name="sender">The connection (unused).</param>
    /// <param name="e">Empty.</param>
    internal void OnChannelReady(object? sender, EventArgs e)
    {
        Interlocked.Increment(ref _channelPulses);
        TaskCompletionSource old = Interlocked.Exchange(
            ref _channelSignal,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        old.TrySetResult();
    }

    /// <summary>
    /// Pair-success verify-then-persist seam (plan 03-04): verifies the
    /// pair-success container and writes the credential row. Not implemented in
    /// this plan — <c>Success</c> is an unverified passthrough until 03-04.
    /// </summary>
    internal Task CompletePairSuccessAsync(BinaryNode node, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Pair-success verification and credential persistence arrive in plan 03-04.");

    private async Task RunAttemptAsync(Attempt attempt)
    {
        int codeIndex = 0;
        bool honorRotationGate = true;
        while (true)
        {
            attempt.Cts.Token.ThrowIfCancellationRequested();

            DateTimeOffset now = _timeProvider.GetUtcNow();
            TimeSpan elapsed = now - attempt.StartedAt;
            if (elapsed >= PairingQrPayload.AttemptTimeout)
            {
                Terminate(attempt, PairingStatus.Timeout, null);
                return;
            }

            if (codeIndex >= MaxCodesPerAttempt)
            {
                // All six codes issued: wait out the window without re-issuing,
                // then the loop top reports the timeout.
                await Task.Delay(
                    PairingQrPayload.AttemptTimeout - elapsed,
                    _timeProvider,
                    attempt.Cts.Token).ConfigureAwait(false);
                continue;
            }

            // Rotation gate (ban-safety): the next ref is requested only after
            // the live code's lifetime elapses — never hammered back-to-back.
            // Error/drop recoveries bypass it to re-issue promptly.
            now = _timeProvider.GetUtcNow();
            if (honorRotationGate && now < attempt.NextIssueAt)
            {
                await Task.Delay(
                    attempt.NextIssueAt - now,
                    _timeProvider,
                    attempt.Cts.Token).ConfigureAwait(false);
                continue;
            }

            TimeSpan remaining = PairingQrPayload.AttemptTimeout - (_timeProvider.GetUtcNow() - attempt.StartedAt);
            if (remaining <= TimeSpan.Zero)
            {
                // The clock jumped past the window mid-iteration: report it at
                // the top instead of arming a non-positive delay.
                continue;
            }

            RefOutcome outcome = await RequestRefAsync(attempt, remaining).ConfigureAwait(false);
            switch (outcome)
            {
                case RefOutcome.Ref r:
                    Emit(PairingStatus.Code, await BuildQrPayloadAsync(r.Value, attempt.Cts.Token).ConfigureAwait(false));
                    attempt.NextIssueAt = _timeProvider.GetUtcNow() + PairingQrPayload.RotationFor(codeIndex);
                    codeIndex++;
                    honorRotationGate = true;
                    break;
                case RefOutcome.Error405:
                    if (attempt.Refetches < MaxVersionRefetches)
                    {
                        attempt.Refetches++;
                        await RefetchVersionAsync(attempt).ConfigureAwait(false);
                        honorRotationGate = false;
                    }
                    else
                    {
                        Terminate(attempt, PairingStatus.ClientOutdated, null);
                        return;
                    }

                    break;
                case RefOutcome.Retry retry:
                    // Recovery re-issues bypass the rotation gate; anything else
                    // re-checks it (a slow reply already consumed the lifetime).
                    honorRotationGate = !retry.ReissuePromptly;
                    break;
            }
        }
    }

    private async Task<RefOutcome> RequestRefAsync(Attempt attempt, TimeSpan wait)
    {
        DateTimeOffset deadline = _timeProvider.GetUtcNow() + wait;
        if (!await WaitForChannelAsync(attempt, deadline).ConfigureAwait(false))
        {
            // No channel before the reply deadline: the window is spent — the
            // loop top reports the timeout (never a prompt re-issue).
            return new RefOutcome.Retry(ReissuePromptly: false);
        }

        string id = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"pair-{Interlocked.Increment(ref _requestSequence)}");
        var pending = new TaskCompletionSource<RefOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Publish the waiter BEFORE the id (release): a pump thread that observes
        // the new id is guaranteed to observe the waiter too.
        attempt.PendingRef = pending;
        Volatile.Write(ref attempt.PendingId, id);

        try
        {
            await _sender(BuildPairDeviceRequest(id), attempt.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SdkException)
        {
            // The channel died under the send: the loop re-waits for the next
            // ready pulse instead of spinning on a dead channel.
            return new RefOutcome.Retry(ReissuePromptly: true);
        }

        int knownPulses = Volatile.Read(ref _channelPulses);
        Task pulseWait = WaitForPulseChangeAsync(knownPulses, attempt.Cts.Token);
        Task delay = Task.Delay(wait, _timeProvider, attempt.Cts.Token);
        Task winner = await Task.WhenAny(pending.Task, pulseWait, delay).ConfigureAwait(false);
        if (ReferenceEquals(winner, pending.Task))
        {
            return await pending.Task.ConfigureAwait(false);
        }

        // A reconnect swapped the channel mid-wait: re-issue promptly on the
        // fresh channel. (A rotation-gated re-issue never reaches this wait —
        // the loop holds it. A terminal cancels the token, so the delay/pulse
        // waits throw OCE and the StartAsync boundary turns them into a return.)
        // The reply wait expiring means the attempt window is spent: the loop
        // top reports the timeout before any re-issue.
        return new RefOutcome.Retry(ReissuePromptly: !ReferenceEquals(winner, delay));
    }

    private async Task<bool> WaitForChannelAsync(Attempt attempt, DateTimeOffset deadline)
    {
        while (true)
        {
            attempt.Cts.Token.ThrowIfCancellationRequested();
            if (_hasChannel())
            {
                return true;
            }

            Task signal = Volatile.Read(ref _channelSignal).Task;
            if (_hasChannel())
            {
                return true;
            }

            TimeSpan remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            Task delay = Task.Delay(remaining, _timeProvider, attempt.Cts.Token);
            Task winner = await Task.WhenAny(signal, delay).ConfigureAwait(false);
            if (ReferenceEquals(winner, delay))
            {
                attempt.Cts.Token.ThrowIfCancellationRequested();

                // No channel before the deadline: the window is spent — the loop
                // top reports the timeout (never a prompt re-issue).
                return false;
            }
        }
    }

    private async Task WaitForPulseChangeAsync(int knownPulses, CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref _channelPulses) == knownPulses)
        {
            Task signal = Volatile.Read(ref _channelSignal).Task;
            if (Volatile.Read(ref _channelPulses) != knownPulses)
            {
                return;
            }

            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RefetchVersionAsync(Attempt attempt)
    {
        WaVersionResolver? resolver = _resolver();
        if (resolver is null)
        {
            return;
        }

        // 03-RESEARCH.md Pattern 4 405 invalidation: drop the rejected revision so
        // the next resolve re-fetches instead of retrying a known-rejected value.
        // A fetch failure keeps the bundled pin (never poison, never throw) —
        // only cancellation escapes.
        resolver.Invalidate();
        await resolver.GetVersionAsync(attempt.Cts.Token).ConfigureAwait(false);
    }

    private async Task<string> BuildQrPayloadAsync(string pairingRef, CancellationToken cancellationToken)
    {
        // Independent read of the persisted row per code (D-06 read side): the QR
        // always carries stored material, never a cached copy.
        CredentialRecord? row = await _credentials.GetAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            throw new SdkStoreException("Pairing credential material disappeared mid-attempt.");
        }

        byte[] noisePrivate = (byte[])row.NoiseKeyPrivate.Clone();
        byte[] identityPrivate = (byte[])row.IdentityKeyPrivate.Clone();
        byte[] advSecret = (byte[])row.AdvSecret.Clone();
        try
        {
            byte[] noisePublic = DeriveX25519Public(noisePrivate);
            try
            {
                byte[] identityPublic = DeriveX25519Public(identityPrivate);
                try
                {
                    return PairingQrPayload.Build(pairingRef, noisePublic, identityPublic, advSecret, row.Platform);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(identityPublic);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(noisePublic);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(noisePrivate);
            CryptographicOperations.ZeroMemory(identityPrivate);
            CryptographicOperations.ZeroMemory(advSecret);
        }
    }

    private static byte[] DeriveX25519Public(byte[] privateKey)
    {
        byte[] copy = (byte[])privateKey.Clone();
        try
        {
            using Key key = Key.Import(KeyAgreementAlgorithm.X25519, copy, KeyBlobFormat.RawPrivateKey);
            return key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    private static BinaryNode BuildPairDeviceRequest(string id) =>
        BinaryNode.FromChildren(
            "iq",
            [BinaryNode.Create(PairDeviceTag)],
            new Dictionary<string, string>
            {
                ["id"] = id,
                ["type"] = "get",
            });

    private static bool TryExtractRef(BinaryNode node, [NotNullWhen(true)] out string? pairingRef)
    {
        pairingRef = null;
        if (!string.Equals(node.Attrs.GetValueOrDefault("type"), "result", StringComparison.Ordinal))
        {
            return false;
        }

        BinaryNode? pairDevice = FindChild(node, PairDeviceTag);
        string? reference = pairDevice?.Attrs.GetValueOrDefault(RefAttribute);
        if (string.IsNullOrEmpty(reference))
        {
            return false;
        }

        pairingRef = reference;
        return true;
    }

    private static bool IsErrorCode(BinaryNode node, string code)
    {
        if (!string.Equals(node.Attrs.GetValueOrDefault("type"), "error", StringComparison.Ordinal))
        {
            return false;
        }

        BinaryNode? error = FindChild(node, ErrorTag);
        return error is not null
            && string.Equals(error.Attrs.GetValueOrDefault(CodeAttribute), code, StringComparison.Ordinal);
    }

    private static bool TryExtractPairSuccess(BinaryNode node, out string? user)
    {
        user = null;
        BinaryNode? success = FindChild(node, PairSuccessTag);
        if (success is null)
        {
            return false;
        }

        // Unverified passthrough (03-04 boundary): carry the scripted user attr
        // (else node text, else null) without interpreting it.
        user = success.Attrs.GetValueOrDefault(UserAttribute) ?? success.Text;
        return true;
    }

    private static BinaryNode? FindChild(BinaryNode node, string tag)
    {
        if (node.Children is null)
        {
            return null;
        }

        foreach (BinaryNode child in node.Children)
        {
            if (string.Equals(child.Tag, tag, StringComparison.Ordinal))
            {
                return child;
            }
        }

        return null;
    }

    private void Terminate(Attempt attempt, PairingStatus status, string? payload)
    {
        lock (_gate)
        {
            if (attempt.Terminal != 0)
            {
                return;
            }

            attempt.Terminal = 1;
            if (ReferenceEquals(_attempt, attempt))
            {
                _attempt = null;
            }
        }

        Emit(status, payload);
        try
        {
            attempt.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The attempt already unwound and disposed its source — the event
            // above is still the single terminal.
        }
    }

    private void Emit(PairingStatus status, string? payload)
    {
        EventHandler<PairingStateChangedEventArgs>? handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        try
        {
            handlers.Invoke(this, new PairingStateChangedEventArgs(status, payload));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Pairing event handler failed for account {AccountId}.", _accountId.Value);
        }
    }

    private void ClearIfCurrent(Attempt attempt)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_attempt, attempt))
            {
                _attempt = null;
            }
        }
    }

    private sealed class Attempt
    {
        internal Attempt(DateTimeOffset startedAt, CancellationTokenSource cts)
        {
            StartedAt = startedAt;
            NextIssueAt = startedAt;
            Cts = cts;
        }

        internal DateTimeOffset StartedAt { get; }

        internal DateTimeOffset NextIssueAt { get; set; }

        internal CancellationTokenSource Cts { get; }

        internal int Terminal;

        internal int Refetches;

        internal string? PendingId;

        internal TaskCompletionSource<RefOutcome>? PendingRef;
    }

    private abstract record RefOutcome
    {
        internal sealed record Ref(string Value) : RefOutcome;

        internal sealed record Error405 : RefOutcome;

        internal sealed record Retry(bool ReissuePromptly) : RefOutcome;
    }
}
