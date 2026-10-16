using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
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
/// success <c>&lt;notification id&gt;&lt;pair-success/&gt;</c> whose
/// <c>pair-success</c> child carries the <c>PairSuccessContainer</c> bytes as
/// binary content (an <c>iq</c> carrying <c>pair-success</c> is accepted too),
/// and the coordinator's reply <c>&lt;iq id type="result"&gt;
/// &lt;pair-success-ack/&gt;</c> correlated by the notification's
/// <c>id</c> attribute. The ref rides an attribute,
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
/// Success ordering (03-04, T-3-08/T-3-09, mandatory): a pair-success stanza
/// never emits <c>Success</c> directly — it is enqueued for the
/// verify-then-persist drain (<see cref="CompletePairSuccessAsync"/>), which
/// runs verify → awaited <c>PutAsync</c> → independent read → <c>iq</c> reply
/// → <c>Success</c> event, every step awaited in order with no
/// fire-and-forget. A rejected container stops there (log-safe reason, no
/// write, no reply, no event — the attempt continues to its natural timeout
/// or terminal); a throwing <c>PutAsync</c> surfaces as a pairing failure
/// (no <c>Success</c>, no reply), never as success.
/// </para>
/// <para>
/// Code flow (03-05, CONN-02): <see cref="RequestPairingCodeAsync"/> validates
/// the host's phone number, generates the 8-character code client-side,
/// sends the link-code request iq, and returns the <c>XXXX-XXXX</c> display
/// string once the server's ref reply arrives — while a companion driver
/// (watchdog + hello-ack + the shared verify-then-persist drain) owns the
/// attempt tail. The tail's only outcomes are the same pairing events.
/// </para>
/// <para>
/// Companion handshake routing (03-05 Task 3, T-3-11): the stanza handler
/// recognizes <c>companion-hello</c> (iq or notification) and completes the
/// attempt's hello signal, off which the driver sends the id-correlated
/// <c>companion-hello-ack</c>. A <c>companion-finish</c> child is processed
/// ONLY after that hello on the same live attempt (sequence gate — a
/// notification that skips the handshake is dropped before any
/// verification), and ONLY under the live attempt's bundle key: the GCM tag
/// is verified by <see cref="LinkCodeCrypto.UnprotectBundle"/> and a
/// mismatch fails closed (log-safe reason, no queue, no write, no event).
/// The authenticated plaintext is a <c>PairSuccessContainer</c> that enters
/// the SAME drain as the QR path (<see cref="CompletePairSuccessAsync"/>):
/// the code flow adds zero new paths into credential persistence.
/// Epoch binding is implicit in key material: requesting a new code starts a
/// new attempt whose bundle key is derived from that code alone, so a finish
/// sealed under a prior code fails GCM authentication and is ignored —
/// never written, never emitted (single live code at any time; the attempt,
/// not a counter, is the epoch). The driver task captures every fault
/// internally (no unobserved exceptions); hosts observe the tail through
/// events alone.
/// </para>
/// <para>
/// Secret hygiene (T-3-06/T-3-07): the reference is opaque pass-through (never
/// parsed, never logged); QR key material is derived per code from the persisted
/// row and zeroized immediately after the payload string is built; pairing
/// codes, link keys, bundle keys, and salts are zeroized at the attempt
/// boundary and never logged, never embedded in exceptions or events beyond
/// the <c>XXXX-XXXX</c> display string the host must render (itself a
/// single-use secret — render it, never log or persist it). Platform/NSec
/// CSPRNG only; no hand-rolled curve math (public keys
/// derive via NSec X25519 import/export).
/// </para>
/// </remarks>
public sealed class PairingCoordinator
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
    private const string PairSuccessAckTag = "pair-success-ack";
    private const string CodeAttribute = "code";
    private const string StaleClientCode = "405";

    /// <summary>Link-code request child carrying the phone attribute and the wrapped ephemeral.</summary>
    private const string LinkCodeRequestTag = "link-code-request";

    /// <summary>Binary child of the link-code request carrying the 80-byte wrapped ephemeral.</summary>
    private const string LinkCodeWrapTag = "link-code-wrap";

    /// <summary>Server reply child carrying the pairing ref the phone consumes.</summary>
    private const string LinkCodeRefTag = "link-code-ref";

    /// <summary>Phone-number attribute on the link-code request (digits-only E.164 without '+').</summary>
    private const string PhoneAttribute = "phone";

    /// <summary>Server-initiated companion handshake opener (iq or notification child).</summary>
    private const string CompanionHelloTag = "companion-hello";

    /// <summary>Coordinator acknowledgement of the companion hello (iq result, id-correlated).</summary>
    private const string CompanionHelloAckTag = "companion-hello-ack";

    /// <summary>Companion finish child carrying the GCM-protected bundle as binary content.</summary>
    private const string CompanionFinishTag = "companion-finish";

    private readonly AccountId _accountId;
    private readonly HandshakePayloadProvider _payloads;
    private readonly ICredentialStore _credentials;
    private readonly Func<BinaryNode, CancellationToken, ValueTask> _sender;
    private readonly Func<bool> _hasChannel;
    private readonly Func<byte[], byte[], byte[], PairSuccessResult> _verify;
    private readonly TimeProvider _timeProvider;
    private readonly Func<WaVersionResolver?> _resolver;
    private readonly Func<int, byte[]> _randomBytes;
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
    /// <param name="pairSuccessVerifier">
    /// Pair-success verification strategy (defaults to <see cref="PairSuccessVerifier.Verify"/>):
    /// takes the container bytes, the stored adv secret, and the row-pinned device public key.
    /// A seam for the ordering proofs, which observe the verify step explicitly.
    /// </param>
    /// <param name="timeProvider">Clock for rotation/timeout delays; defaults to system time.</param>
    /// <param name="resolver">Live lookup of the connection's version resolver (405 invalidation path).</param>
    /// <param name="logger">Optional per-account logger (Abstractions only).</param>
    /// <param name="randomBytes">
    /// Pairing-code randomness seam: returns exactly the requested byte count
    /// (code bytes, salts, IVs, ephemeral seeds) in a fresh buffer the
    /// coordinator then OWNS — salts, IVs, and seeds are zeroized when the
    /// attempt unwinds (secret hygiene), so an injector must not retain the
    /// returned arrays. Defaults to the platform CSPRNG; tests inject a
    /// scripted sequence for determinism.
    /// </param>
    /// <exception cref="SdkFormatException"><paramref name="accountId"/> is the default (uninitialized) value.</exception>
    /// <exception cref="ArgumentNullException">Any required seam is null.</exception>
    internal PairingCoordinator(
        AccountId accountId,
        HandshakePayloadProvider payloads,
        ICredentialStore credentials,
        Func<BinaryNode, CancellationToken, ValueTask> sender,
        Func<bool> hasChannel,
        Func<byte[], byte[], byte[], PairSuccessResult>? pairSuccessVerifier = null,
        TimeProvider? timeProvider = null,
        Func<WaVersionResolver?>? resolver = null,
        ILogger<PairingCoordinator>? logger = null,
        Func<int, byte[]>? randomBytes = null)
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
        _verify = pairSuccessVerifier ?? PairSuccessVerifier.Verify;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _resolver = resolver ?? (() => null);
        _randomBytes = randomBytes ?? RandomNumberGenerator.GetBytes;
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

        // The attempt loop and the pair-success drain run concurrently and are
        // both joined here: the drain owns verify → write → read → reply →
        // Success, the loop owns rotation/timeout/refetch. Neither is
        // fire-and-forget — both tasks are awaited on every exit path below.
        Task attemptTask = RunAttemptAsync(attempt);
        Task drainTask = DrainPairSuccessAsync(attempt);
        try
        {
            await Task.WhenAll(attemptTask, drainTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A store fault captured by the drain surfaces here as the pairing
            // failure outcome (never as Success) — with the original stack.
            if (attempt.DrainError is not null)
            {
                ExceptionDispatchInfo.Capture(attempt.DrainError).Throw();
            }

            if (attempt.Terminal != 0)
            {
                // A terminal already fired its event — the cancellation is just
                // the mechanism that unwinds the in-flight wait, not an outcome.
                return;
            }

            throw;
        }
        catch (Exception)
        {
            // The attempt loop faulted while the drain is still parked: unwind
            // the sibling, join both, then surface the original fault.
            try
            {
                attempt.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                await Task.WhenAll(attemptTask, drainTask).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            throw;
        }
        finally
        {
            ClearIfCurrent(attempt);
            attempt.Cts.Dispose();
        }
    }

    /// <summary>
    /// Requests one pairing code for the host's user to type on their phone
    /// (03-05, CONN-02): validates the phone number, generates the code
    /// client-side (the server never supplies it), sends the link-code
    /// request iq, and returns the <c>XXXX-XXXX</c> display string once the
    /// server's ref reply arrives — also raised as the <c>Code</c> event.
    /// </summary>
    /// <remarks>
    /// The returned task completes at code issue; the attempt stays live under
    /// a companion driver whose only outcomes are the pairing events
    /// (<c>Success</c> through the verified path, <c>Timeout</c> when the
    /// window closes, <c>LoggedOut</c>/<c>ClientOutdated</c> on terminal
    /// disconnects). Cancelling <paramref name="cancellationToken"/> after the
    /// code is issued aborts the attempt with no terminal event (same
    /// semantics as <see cref="StartAsync"/>). The code itself is never logged
    /// and never embedded in exceptions — failures name the operation only.
    /// </remarks>
    /// <param name="phoneNumber">
    /// Digits-only E.164 without '+' (longer than six digits, no leading
    /// zero). Validated before any generation or wire work.
    /// </param>
    /// <param name="cancellationToken">Host cancellation: aborts with no terminal event.</param>
    /// <returns>The display-ready <c>XXXX-XXXX</c> code.</returns>
    /// <exception cref="OperationCanceledException">The host cancelled.</exception>
    /// <exception cref="InvalidOperationException">Another pairing attempt (QR or code) is live.</exception>
    /// <exception cref="PairingCodeRequestException">The phone number violates its input contract.</exception>
    /// <exception cref="Errors.SdkConnectionException">
    /// The server rejected the request, the reply never arrived inside the
    /// attempt window, or no channel was available — the attempt is cleared.
    /// </exception>
    /// <exception cref="Errors.SdkStoreException">The registration bootstrap found no credential row.</exception>
    public async Task<string> RequestPairingCodeAsync(string phoneNumber, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(phoneNumber);

        PhoneNumberValidation validation = PairingCodeGenerator.ValidatePhoneNumber(phoneNumber);
        if (validation != PhoneNumberValidation.Valid)
        {
            throw new PairingCodeRequestException(
                MapValidationReason(validation),
                ValidationMessageFor(validation));
        }

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
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken),
                IsCodeAttempt: true);
            _attempt = attempt;
        }

        byte[]? salt = null;
        byte[]? linkKey = null;
        try
        {
            try
            {
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

            string rawCode = PairingCodeGenerator.GenerateCode(_randomBytes);
            byte[] codeAscii = Encoding.ASCII.GetBytes(rawCode);
            salt = _randomBytes(LinkCodeCrypto.SaltSize);
            byte[] iv = _randomBytes(LinkCodeCrypto.CounterSize);
            byte[] ephemeralSeed = _randomBytes(LinkCodeCrypto.KeySize);
            try
            {
                linkKey = LinkCodeCrypto.DeriveKey(codeAscii, salt);
                byte[] ephemeralPublic = DeriveX25519Public(ephemeralSeed);
                byte[] wrapped;
                try
                {
                    wrapped = LinkCodeCrypto.WrapEphemeral(linkKey, salt, iv, ephemeralPublic);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(ephemeralPublic);
                }

                byte[] bundleKey = LinkCodeCrypto.DeriveBundleKey(linkKey, salt);
                attempt.BundleKey = bundleKey;

                // The reply's ref is the phone's business (opaque pass-through,
                // never parsed, never logged): for the SDK the reply is the
                // server's acceptance of the code request — a rejection
                // surfaces here as a descriptive error before any code exists.
                _ = await RequestCodeRefAsync(
                    attempt, phoneNumber, wrapped, attempt.Cts.Token).ConfigureAwait(false);

                _logger?.LogInformation(
                    "Pairing code issued for account {AccountId}.",
                    _accountId.Value);
                string display = PairingCodeGenerator.ToDisplay(rawCode);
                Emit(PairingStatus.Code, display);

                // The request half is done: the companion driver owns the tail
                // (watchdog + hello-ack + the shared verify-then-persist
                // drain). Tracked through the attempt lifecycle with every
                // fault captured inside — hosts observe the tail via events.
                _ = RunCodeCompanionAsync(attempt);
                return display;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(codeAscii);
                CryptographicOperations.ZeroMemory(iv);
                CryptographicOperations.ZeroMemory(ephemeralSeed);
            }
        }
        catch
        {
            ClearIfCurrent(attempt);
            attempt.Cts.Dispose();
            throw;
        }
        finally
        {
            if (salt is not null)
            {
                CryptographicOperations.ZeroMemory(salt);
            }

            if (linkKey is not null)
            {
                CryptographicOperations.ZeroMemory(linkKey);
            }
        }
    }

    private static PairingCodeValidationReason MapValidationReason(PhoneNumberValidation validation) =>
        validation switch
        {
            PhoneNumberValidation.NonDigits => PairingCodeValidationReason.NonDigits,
            PhoneNumberValidation.TooShort => PairingCodeValidationReason.TooShort,
            PhoneNumberValidation.LeadingZero => PairingCodeValidationReason.LeadingZero,
            _ => throw new SdkFormatException("Phone-number validation produced an unknown outcome."),
        };

    private static string ValidationMessageFor(PhoneNumberValidation validation) =>
        validation switch
        {
            PhoneNumberValidation.NonDigits =>
                "The pairing phone number must contain digits only (E.164 without '+').",
            PhoneNumberValidation.TooShort =>
                "The pairing phone number must be longer than six digits.",
            PhoneNumberValidation.LeadingZero =>
                "The pairing phone number must not start with zero.",
            _ => throw new SdkFormatException("Phone-number validation produced an unknown outcome."),
        };

    /// <summary>
    /// Sends one link-code request iq (phone attribute plus the 80-byte
    /// wrapped ephemeral) and awaits the server's ref reply with
    /// id-attribute correlation. A 405 runs the bounded version-refetch
    /// before re-issuing; any other error, a missing channel, or an expired
    /// window fails the request with a descriptive, log-safe error.
    /// </summary>
    /// <param name="attempt">The live code attempt (owns the pending waiter).</param>
    /// <param name="phoneNumber">The validated phone number (validated by the caller).</param>
    /// <param name="wrapped">The 80-byte wrapped ephemeral (never mutated).</param>
    /// <param name="cancellationToken">The attempt's token (unwrapped).</param>
    /// <returns>The server-issued pairing ref the phone consumes.</returns>
    private async Task<string> RequestCodeRefAsync(
        Attempt attempt,
        string phoneNumber,
        byte[] wrapped,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = attempt.StartedAt + PairingQrPayload.AttemptTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await WaitForChannelAsync(attempt, deadline).ConfigureAwait(false))
            {
                throw new SdkConnectionException(
                    "The pairing code request found no channel inside the attempt window: the code was not issued.");
            }

            string id = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"pair-code-{Interlocked.Increment(ref _requestSequence)}");
            var pending = new TaskCompletionSource<CodeRefOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            attempt.PendingCodeRef = pending;
            Volatile.Write(ref attempt.PendingId, id);

            try
            {
                try
                {
                    await _sender(BuildLinkCodeRequest(id, phoneNumber, wrapped), attempt.Cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (SdkException ex)
                {
                    throw new SdkConnectionException(
                        "The pairing code request was not sent: the channel is unavailable.", ex);
                }

                TimeSpan remaining = deadline - _timeProvider.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    throw new SdkConnectionException(
                        "The pairing code request timed out waiting for the server reply: the code was not issued.");
                }

                Task delay = Task.Delay(remaining, _timeProvider, attempt.Cts.Token);
                Task winner = await Task.WhenAny(pending.Task, delay).ConfigureAwait(false);
                if (!ReferenceEquals(winner, pending.Task))
                {
                    throw new SdkConnectionException(
                        "The pairing code request timed out waiting for the server reply: the code was not issued.");
                }

                CodeRefOutcome outcome = await pending.Task.ConfigureAwait(false);
                switch (outcome)
                {
                    case CodeRefOutcome.Ref r:
                        return r.Value;
                    case CodeRefOutcome.Error e when string.Equals(e.Code, StaleClientCode, StringComparison.Ordinal):
                        if (attempt.Refetches < MaxVersionRefetches)
                        {
                            attempt.Refetches++;
                            await RefetchVersionAsync(attempt).ConfigureAwait(false);
                            continue;
                        }

                        Terminate(attempt, PairingStatus.ClientOutdated, null);
                        throw new SdkConnectionException(
                            "The pairing code request was rejected as client-outdated: upgrade the client build.");
                    case CodeRefOutcome.Error e:
                        throw new SdkConnectionException(
                            $"The pairing code request was rejected by the server (code {e.Code}).");
                    default:
                        throw new SdkConnectionException(
                            "The pairing code request received an unrecognized reply: the code was not issued.");
                }
            }
            finally
            {
                Volatile.Write(ref attempt.PendingId, null);
                attempt.PendingCodeRef = null;
            }
        }
    }

    /// <summary>
    /// Code-attempt tail driver (03-05): joins the hello-ack waiter, the
    /// attempt-window watchdog, and the shared verify-then-persist drain —
    /// the same drain the QR path runs, never a second write path. Every
    /// fault is captured inside (unexpected faults log and end the attempt
    /// in <c>Timeout</c>); hosts observe the tail through pairing events
    /// alone, so this task never needs joining and never faults.
    /// </summary>
    /// <param name="attempt">The live code attempt whose tail is driven.</param>
    private async Task RunCodeCompanionAsync(Attempt attempt)
    {
        Task drain = DrainPairSuccessAsync(attempt);
        Task helloAck = SendHelloAckOnHelloAsync(attempt);
        Task watchdog = WatchdogTimeoutAsync(attempt);
        try
        {
            await Task.WhenAll(drain, helloAck, watchdog).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A terminal event, the window watchdog, or host cancel unwound
            // the tail — never a failure outcome, never swallowed silently.
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex, "Code companion driver fault for account {AccountId}: ending the attempt.", _accountId.Value);
            Terminate(attempt, PairingStatus.Timeout, null);
            try
            {
                await Task.WhenAll(drain, helloAck, watchdog).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
        finally
        {
            ClearIfCurrent(attempt);
            ZeroizeCodeMaterial(attempt);
            attempt.Cts.Dispose();
        }

        if (attempt.DrainError is not null)
        {
            _logger?.LogError(
                attempt.DrainError,
                "Pair-success persist failed on the code path for account {AccountId}: success withheld.",
                _accountId.Value);
        }
    }

    /// <summary>
    /// Sends the companion-hello acknowledgement once the server's hello
    /// arrives (id-correlated to the hello stanza). A send failure logs and
    /// ends this waiter only — the finish can still arrive and authenticate.
    /// </summary>
    /// <param name="attempt">The live code attempt.</param>
    private async Task SendHelloAckOnHelloAsync(Attempt attempt)
    {
        try
        {
            await attempt.HelloSignal.Task.WaitAsync(attempt.Cts.Token).ConfigureAwait(false);
            string replyId = attempt.HelloReplyId
                ?? $"companion-hello-{Interlocked.Increment(ref _requestSequence)}";
            await _sender(BuildCompanionHelloAck(replyId), attempt.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SdkException ex)
        {
            _logger?.LogWarning(
                ex, "Companion hello acknowledgement failed for account {AccountId}.", _accountId.Value);
        }
    }

    /// <summary>
    /// Ends the code attempt in <c>Timeout</c> when the whole-attempt window
    /// elapses without a verified finish (A3: a typed outcome, never a hang).
    /// Suppressed while the attempt's persist window is open (WR-02): a
    /// durable pair-success outranks a timer that fired mid-write, and the
    /// drain terminates with <c>Success</c> once the store sequence lands.
    /// </summary>
    /// <param name="attempt">The live code attempt.</param>
    private async Task WatchdogTimeoutAsync(Attempt attempt)
    {
        try
        {
            await Task.Delay(PairingQrPayload.AttemptTimeout, _timeProvider, attempt.Cts.Token).ConfigureAwait(false);
            Terminate(attempt, PairingStatus.Timeout, null);
        }
        catch (OperationCanceledException)
        {
            // A terminal event or host cancel unwound the wait.
            throw;
        }
    }

    /// <summary>
    /// Zeroizes the live code attempt's bundle key at the attempt boundary.
    /// </summary>
    /// <param name="attempt">The ending code attempt.</param>
    private static void ZeroizeCodeMaterial(Attempt attempt)
    {
        byte[]? bundleKey = Interlocked.Exchange(ref attempt.BundleKey, null);
        if (bundleKey is not null)
        {
            CryptographicOperations.ZeroMemory(bundleKey);
        }
    }

    /// <summary>
    /// Stanza-received handler (subscribes to the connection's internal stanza
    /// event): enqueues pair-success containers for the verify-then-persist
    /// drain and correlates ref/error replies to the pending pairing request
    /// by node id.
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

        if (TryExtractPairSuccessContainer(node, out string? replyId, out byte[]? container)
            && container is not null)
        {
            // Handled here, fully: a pair-success stanza never falls through
            // to iq correlation below. Without a live attempt it is ignored;
            // otherwise the drain verifies it before any side effect.
            lock (_gate)
            {
                if (!ReferenceEquals(_attempt, attempt) || attempt.Terminal != 0)
                {
                    return;
                }

                attempt.SuccessQueue.Enqueue(new SuccessItem(container, replyId));
                attempt.SuccessSignal.TrySetResult();
            }

            return;
        }

        if (attempt.IsCodeAttempt && TryExtractCompanionHello(node, out string? helloId))
        {
            // Companion handshake step 1 (03-05 Task 3): the first hello on the
            // live attempt arms the ack waiter. Under the gate so a stale
            // attempt (already replaced) never arms anything.
            lock (_gate)
            {
                if (!ReferenceEquals(_attempt, attempt) || attempt.Terminal != 0)
                {
                    return;
                }

                if (!attempt.HelloReceived)
                {
                    attempt.HelloReceived = true;
                    attempt.HelloReplyId = helloId;
                    attempt.HelloSignal.TrySetResult();
                }
            }

            return;
        }

        if (attempt.IsCodeAttempt
            && TryExtractCompanionFinish(node, out string? finishReplyId, out byte[]? sealedBundle)
            && sealedBundle is not null)
        {
            // Companion handshake step 2: sequence gate, then GCM
            // authentication under the live code's bundle key — only the
            // authenticated container reaches the verified drain.
            HandleCompanionFinish(attempt, finishReplyId, sealedBundle);
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

        if (attempt.PendingCodeRef is not null)
        {
            // Code-attempt correlation (03-05): the ref reply accepts the
            // request; any error reply rejects it with its numeric code
            // (405 runs the bounded refetch in the request loop). A
            // correlated-but-unrecognized reply is ignored — the reply wait
            // bounds it, never a silent hang.
            if (TryExtractCodeRef(node, out string? codeRef))
            {
                attempt.PendingCodeRef.TrySetResult(new CodeRefOutcome.Ref(codeRef));
                return;
            }

            if (TryGetErrorCode(node, out string? errorCode) && errorCode is not null)
            {
                attempt.PendingCodeRef.TrySetResult(new CodeRefOutcome.Error(errorCode));
            }

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
    /// Companion-finish handler (03-05 Task 3): sequence-gates the finish on
    /// the hello of the SAME live attempt, authenticates the sealed bundle
    /// under the live code's bundle key (GCM tag verified — a stale code's
    /// material never opens), and hands the authenticated container to the
    /// shared verify-then-persist drain. Every rejection is log-safe and
    /// side-effect free: no queue, no write, no reply, no event.
    /// </summary>
    /// <param name="attempt">The live code attempt that received the finish.</param>
    /// <param name="replyId">The stanza's <c>id</c> for reply correlation (may be null).</param>
    /// <param name="sealedBundle">The GCM-protected bundle (<c>nonce ‖ ciphertext ‖ tag</c>).</param>
    private void HandleCompanionFinish(Attempt attempt, string? replyId, byte[] sealedBundle)
    {
        if (!attempt.HelloReceived)
        {
            // Bypass proof (T-3-11): a finish that skips the handshake is
            // dropped before any verification runs — sequence state, not
            // timestamps, gates the flow.
            _logger?.LogWarning(
                "Companion finish arrived before the companion hello for account {AccountId}: ignored.",
                _accountId.Value);
            return;
        }

        byte[]? bundleKey = Volatile.Read(ref attempt.BundleKey);
        if (bundleKey is null)
        {
            // No live code on this attempt (a rotated or ended attempt): the
            // finish cannot authenticate and is ignored.
            _logger?.LogWarning(
                "Companion finish arrived with no live pairing code for account {AccountId}: ignored.",
                _accountId.Value);
            return;
        }

        byte[]? container;
        try
        {
            container = LinkCodeCrypto.UnprotectBundle(bundleKey, sealedBundle);
        }
        catch (ArgumentException)
        {
            _logger?.LogWarning(
                "Companion finish was misshapen for account {AccountId}: ignored.",
                _accountId.Value);
            return;
        }
        catch (CryptographicException)
        {
            // Wrong key (a prior code's epoch) or tampered bytes: fail closed
            // with a log-safe reason — no code, no key bytes, ever.
            _logger?.LogWarning(
                "Companion finish failed bundle authentication for account {AccountId}: ignored.",
                _accountId.Value);
            return;
        }

        lock (_gate)
        {
            if (!ReferenceEquals(_attempt, attempt) || attempt.Terminal != 0)
            {
                return;
            }

            attempt.SuccessQueue.Enqueue(new SuccessItem(container, replyId));
            attempt.SuccessSignal.TrySetResult();
        }
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
    /// Pair-success drain (03-04): dequeues every pair-success container the
    /// stanza handler enqueued and runs each through
    /// <see cref="CompletePairSuccessAsync"/> until one verifies (which ends
    /// the attempt in <c>Success</c>) or the attempt ends another way.
    /// Awaited by <see cref="StartAsync"/> — never fire-and-forget.
    /// </summary>
    /// <param name="attempt">The live attempt whose queue is drained.</param>
    private async Task DrainPairSuccessAsync(Attempt attempt)
    {
        try
        {
            while (true)
            {
                SuccessItem[] batch;
                Task wait;
                lock (_gate)
                {
                    if (attempt.SuccessQueue.Count > 0)
                    {
                        batch = attempt.SuccessQueue.ToArray();
                        attempt.SuccessQueue.Clear();
                        wait = Task.CompletedTask;
                    }
                    else
                    {
                        batch = [];
                        wait = attempt.SuccessSignal.Task;
                    }
                }

                if (batch.Length == 0)
                {
                    await wait.WaitAsync(attempt.Cts.Token).ConfigureAwait(false);
                    continue;
                }

                foreach (SuccessItem item in batch)
                {
                    // A store fault here (FailWrites) is the pairing failure
                    // outcome, never Success: captured below, surfaced by
                    // StartAsync after the sibling loop unwinds.
                    PairSuccessPayload? payload = await CompletePairSuccessAsync(
                        item,
                        Volatile.Read(ref attempt.PendingId),
                        attempt,
                        attempt.Cts.Token).ConfigureAwait(false);
                    if (payload is not null)
                    {
                        Terminate(attempt, PairingStatus.Success, payload.PairedUser);
                        return;
                    }
                }

                lock (_gate)
                {
                    // Re-arm only when nothing arrived mid-batch: a queued item
                    // takes the batch path above, so waiting on an already
                    // completed signal (a busy spin) is impossible.
                    if (attempt.SuccessQueue.Count == 0 && attempt.SuccessSignal.Task.IsCompleted)
                    {
                        attempt.SuccessSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Terminal, timeout, or host cancel unwinds the wait — never a
            // failure outcome, never swallowed.
            throw;
        }
        catch (Exception ex)
        {
            attempt.DrainError = ex;
            try
            {
                attempt.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// Pair-success verify-then-persist (03-04): verifies the container
    /// fail-closed, writes the credential row and awaits it, confirms the
    /// write through an independent read, and only then replies to the
    /// server. Returns the verified payload, or <c>null</c> when the
    /// container was rejected or no row exists (no write, no reply, no
    /// event — the attempt continues). Opens the attempt's persist window
    /// (WR-02) around the store sequence so a watchdog <c>Timeout</c> racing
    /// a slow write cannot discard the durable outcome.
    /// </summary>
    /// <param name="item">The enqueued container plus its reply correlation id.</param>
    /// <param name="fallbackReplyId">The pending request id when the stanza carried none.</param>
    /// <param name="attempt">The live attempt owning the persist window (WR-02).</param>
    /// <param name="cancellationToken">The attempt's token (unwrapped).</param>
    /// <returns>The verified payload, or <c>null</c> for a non-success outcome.</returns>
    /// <exception cref="Errors.SdkStoreException">
    /// The write failed or the independent read missed it — the pairing
    /// failure outcome (surfaces from <c>StartAsync</c>, never as Success).
    /// </exception>
    /// <exception cref="OperationCanceledException">The attempt ended mid-sequence.</exception>
    private async Task<PairSuccessPayload?> CompletePairSuccessAsync(
        SuccessItem item,
        string? fallbackReplyId,
        Attempt attempt,
        CancellationToken cancellationToken)
    {
        // Fail-closed first touch: the row supplies the adv secret AND the
        // row-pinned device key. A missing row cannot verify — stop with no
        // write, no reply, no event. A throwing read propagates as the
        // pairing failure (a broken store must not look like "no success
        // yet").
        CredentialRecord? row = await _credentials.GetAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            _logger?.LogWarning(
                "Pair-success arrived with no credential row for account {AccountId}: success withheld.",
                _accountId.Value);
            return null;
        }

        byte[] advSecret = (byte[])row.AdvSecret.Clone();
        byte[] signingPrivate = (byte[])row.SigningKeyPrivate.Clone();
        try
        {
            byte[] devicePublic = DeriveEd25519Public(signingPrivate);
            try
            {
                PairSuccessResult verdict = _verify(item.Container, advSecret, devicePublic);
                if (verdict is not PairSuccessResult.Verified verified)
                {
                    var rejected = (PairSuccessResult.Rejected)verdict;
                    _logger?.LogWarning(
                        "Pair-success verification failed for account {AccountId}: {Reason}.",
                        _accountId.Value,
                        rejected.Reason);
                    return null;
                }

                CredentialRecord updated = row with
                {
                    PairedUser = verified.Payload.PairedUser,
                    Device = verified.Payload.Device,
                    RegistrationId = verified.Payload.RegistrationId,
                };

                // WR-02: open the persist window under the gate before the
                // first store touch so a watchdog Timeout racing a slow write
                // cannot cancel it and discard a durable outcome. Closed on
                // fault below; the success path leaves it open for
                // Terminate(Success) to clear while taking the terminal.
                lock (_gate)
                {
                    attempt.PersistWindow = 1;
                }

                try
                {
                    await _credentials.PutAsync(updated, cancellationToken).ConfigureAwait(false);

                    CredentialRecord? observed = await _credentials.GetAsync(cancellationToken).ConfigureAwait(false);
                    if (observed is null
                        || !string.Equals(observed.PairedUser, verified.Payload.PairedUser, StringComparison.Ordinal)
                        || observed.Device != verified.Payload.Device)
                    {
                        throw new SdkStoreException(
                            "Pair-success write was not observable by an independent read: success withheld.");
                    }

                    string replyId = item.ReplyId
                        ?? fallbackReplyId
                        ?? $"pair-success-{Interlocked.Increment(ref _requestSequence)}";
                    await _sender(BuildPairSuccessReply(replyId), cancellationToken).ConfigureAwait(false);
                    return verified.Payload;
                }
                catch
                {
                    // Fault path: the durable outcome never materialized, so a
                    // subsequent watchdog Timeout must terminate normally.
                    lock (_gate)
                    {
                        attempt.PersistWindow = 0;
                    }

                    throw;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(devicePublic);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(advSecret);
            CryptographicOperations.ZeroMemory(signingPrivate);
        }
    }

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

    /// <summary>
    /// Derives the Ed25519 device public key from the row-pinned signing
    /// private (the pair-success device-signature pin). Same clone/import/
    /// zeroize discipline as <see cref="DeriveX25519Public"/> — NSec derives,
    /// never hand-rolled curve math.
    /// </summary>
    /// <param name="privateSeed">The 32-byte signing private (never mutated).</param>
    private static byte[] DeriveEd25519Public(byte[] privateSeed)
    {
        byte[] copy = (byte[])privateSeed.Clone();
        try
        {
            using Key key = Key.Import(SignatureAlgorithm.Ed25519, copy, KeyBlobFormat.RawPrivateKey);
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

    /// <summary>
    /// Builds the link-code request: an <c>iq get</c> carrying the validated
    /// phone number as a string attribute (attr values roundtrip losslessly)
    /// and the 80-byte wrapped ephemeral as binary content (content-slot raw
    /// strings would decode as <c>Data</c>, never as text — the 03-03
    /// content-vs-attr codec asymmetry).
    /// </summary>
    /// <param name="id">The request id for reply correlation.</param>
    /// <param name="phoneNumber">The validated phone number (digits-only, never logged).</param>
    /// <param name="wrapped">The 80-byte wrapped ephemeral.</param>
    private static BinaryNode BuildLinkCodeRequest(string id, string phoneNumber, byte[] wrapped) =>
        BinaryNode.FromChildren(
            "iq",
            [BinaryNode.FromChildren(
                LinkCodeRequestTag,
                [BinaryNode.FromBytes(LinkCodeWrapTag, wrapped)],
                new Dictionary<string, string>
                {
                    [PhoneAttribute] = phoneNumber,
                })],
            new Dictionary<string, string>
            {
                ["id"] = id,
                ["type"] = "get",
            });

    /// <summary>
    /// Builds the companion-hello acknowledgement: an <c>iq result</c>
    /// correlated by the hello stanza's id.
    /// </summary>
    /// <param name="id">The hello stanza's id (or a generated fallback).</param>
    private static BinaryNode BuildCompanionHelloAck(string id) =>
        BinaryNode.FromChildren(
            "iq",
            [BinaryNode.Create(CompanionHelloAckTag)],
            new Dictionary<string, string>
            {
                ["id"] = id,
                ["type"] = "result",
            });

    /// <summary>
    /// Builds the success reply: an <c>iq result</c> correlated by the
    /// <c>id</c> attribute (the QueryRouter correlation discipline — pairing
    /// stanzas flow through the stanza event, but the reply still carries the
    /// id the server matches on).
    /// </summary>
    /// <param name="id">The incoming pair-success stanza's id (or the pending request's).</param>
    private static BinaryNode BuildPairSuccessReply(string id) =>
        BinaryNode.FromChildren(
            "iq",
            [BinaryNode.Create(PairSuccessAckTag)],
            new Dictionary<string, string>
            {
                ["id"] = id,
                ["type"] = "result",
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

    private static bool IsErrorCode(BinaryNode node, string code) =>
        TryGetErrorCode(node, out string? actual)
        && string.Equals(actual, code, StringComparison.Ordinal);

    /// <summary>
    /// Extracts the numeric error code from an <c>iq error</c> stanza, if any.
    /// </summary>
    /// <param name="node">The inbound stanza (never mutated).</param>
    /// <param name="errorCode">The error's <c>code</c> attribute (protocol metadata, safe to surface).</param>
    private static bool TryGetErrorCode(BinaryNode node, out string? errorCode)
    {
        errorCode = null;
        if (!string.Equals(node.Attrs.GetValueOrDefault("type"), "error", StringComparison.Ordinal))
        {
            return false;
        }

        BinaryNode? error = FindChild(node, ErrorTag);
        string? code = error?.Attrs.GetValueOrDefault(CodeAttribute);
        if (string.IsNullOrEmpty(code))
        {
            return false;
        }

        errorCode = code;
        return true;
    }

    /// <summary>
    /// Extracts the server-issued pairing ref from a link-code reply: an
    /// <c>iq result</c> whose <c>link-code-ref</c> child carries it as an
    /// attribute (never text content — the content-vs-attr asymmetry).
    /// </summary>
    /// <param name="node">The inbound stanza (never mutated).</param>
    /// <param name="codeRef">The opaque ref (the phone's business — never parsed).</param>
    private static bool TryExtractCodeRef(BinaryNode node, [NotNullWhen(true)] out string? codeRef)
    {
        codeRef = null;
        if (!string.Equals(node.Attrs.GetValueOrDefault("type"), "result", StringComparison.Ordinal))
        {
            return false;
        }

        BinaryNode? linkRef = FindChild(node, LinkCodeRefTag);
        string? reference = linkRef?.Attrs.GetValueOrDefault(RefAttribute);
        if (string.IsNullOrEmpty(reference))
        {
            return false;
        }

        codeRef = reference;
        return true;
    }

    /// <summary>
    /// Extracts the pair-success container bytes from a stanza: the
    /// <c>pair-success</c> child must carry the <c>PairSuccessContainer</c>
    /// bytes as binary content. Legacy shapes without binary content (the
    /// 03-03 unverified <c>user</c> passthrough) are NOT extracted — an
    /// unverified payload can never reach the drain.
    /// </summary>
    /// <param name="node">The inbound stanza (never mutated).</param>
    /// <param name="replyId">The stanza's <c>id</c> attribute for reply correlation (may be null).</param>
    /// <param name="container">The container bytes (a fresh copy).</param>
    private static bool TryExtractPairSuccessContainer(
        BinaryNode node,
        out string? replyId,
        [NotNullWhen(true)] out byte[]? container)
    {
        replyId = null;
        container = null;
        BinaryNode? success = FindChild(node, PairSuccessTag);
        if (success?.Data is null || success.Data.Length == 0)
        {
            return false;
        }

        replyId = node.Attrs.GetValueOrDefault("id");
        container = (byte[])success.Data.Clone();
        return true;
    }

    /// <summary>
    /// Detects the server's companion handshake opener: any <c>iq</c> or
    /// <c>notification</c> carrying a <c>companion-hello</c> child. The
    /// stanza's <c>id</c> correlates the acknowledgement (null when absent —
    /// the driver then generates one).
    /// </summary>
    /// <param name="node">The inbound stanza (never mutated).</param>
    /// <param name="helloId">The stanza's <c>id</c> attribute for reply correlation (may be null).</param>
    private static bool TryExtractCompanionHello(BinaryNode node, out string? helloId)
    {
        helloId = null;
        if (FindChild(node, CompanionHelloTag) is null)
        {
            return false;
        }

        helloId = node.Attrs.GetValueOrDefault("id");
        return true;
    }

    /// <summary>
    /// Extracts the GCM-protected bundle from a companion finish: the
    /// <c>companion-finish</c> child must carry it as binary content (the
    /// same content-vs-attr discipline as <c>pair-success</c>). A child
    /// without binary content is not a finish — never routed to the drain.
    /// </summary>
    /// <param name="node">The inbound stanza (never mutated).</param>
    /// <param name="replyId">The stanza's <c>id</c> attribute for reply correlation (may be null).</param>
    /// <param name="bundle">The sealed bundle bytes (a fresh copy).</param>
    private static bool TryExtractCompanionFinish(
        BinaryNode node,
        out string? replyId,
        [NotNullWhen(true)] out byte[]? bundle)
    {
        replyId = null;
        bundle = null;
        BinaryNode? finish = FindChild(node, CompanionFinishTag);
        if (finish?.Data is null || finish.Data.Length == 0)
        {
            return false;
        }

        replyId = node.Attrs.GetValueOrDefault("id");
        bundle = (byte[])finish.Data.Clone();
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

            // WR-02: a durable pair-success supersedes the watchdog. The
            // persist window is open only while the write/read/reply sequence
            // is in flight; a Timeout firing then would cancel it and discard
            // an outcome the store already holds. The persist path closes the
            // window before rethrowing a fault, so a Timeout after a failed
            // write still terminates normally, and LoggedOut/ClientOutdated
            // (real server verdicts) are never suppressed.
            if (status == PairingStatus.Timeout && attempt.PersistWindow != 0)
            {
                return;
            }

            attempt.PersistWindow = 0;
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
        internal Attempt(DateTimeOffset startedAt, CancellationTokenSource cts, bool IsCodeAttempt = false)
        {
            StartedAt = startedAt;
            NextIssueAt = startedAt;
            Cts = cts;
            this.IsCodeAttempt = IsCodeAttempt;
            HelloReceived = false;
            HelloReplyId = null;
        }

        internal DateTimeOffset StartedAt { get; }

        internal DateTimeOffset NextIssueAt { get; set; }

        internal CancellationTokenSource Cts { get; }

        internal int Terminal;

        internal int Refetches;

        internal string? PendingId;

        internal TaskCompletionSource<RefOutcome>? PendingRef;

        /// <summary>
        /// Whether this attempt issues a pairing code (03-05) rather than QR
        /// payloads: the request half is client-generated, and the tail waits
        /// on the companion handshake instead of rotating refs.
        /// </summary>
        internal bool IsCodeAttempt { get; }

        /// <summary>
        /// Pending link-code request waiter (code attempts only): the stanza
        /// handler completes it with the server's ref or error reply,
        /// correlated by <see cref="PendingId"/>.
        /// </summary>
        internal TaskCompletionSource<CodeRefOutcome>? PendingCodeRef;

        /// <summary>
        /// The live code's bundle key (code attempts only): the companion
        /// finish authenticates under this key, which implicitly binds the
        /// finish to the live code epoch. Zeroized at the attempt boundary.
        /// </summary>
        internal byte[]? BundleKey;

        /// <summary>
        /// Completed when the companion hello arrives (code attempts only);
        /// the driver sends the hello acknowledgement off this signal.
        /// </summary>
        internal TaskCompletionSource HelloSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Whether the companion hello arrived (sequence gate for the finish).</summary>
        internal bool HelloReceived;

        /// <summary>The hello stanza's id for acknowledgement correlation (null when absent).</summary>
        internal string? HelloReplyId;

        /// <summary>
        /// Pair-success containers awaiting the verify-then-persist drain.
        /// Enqueued by the stanza handler, dequeued by the drain — both under
        /// <see cref="_gate"/>.
        /// </summary>
        internal readonly Queue<SuccessItem> SuccessQueue = new();

        /// <summary>
        /// Completed whenever <see cref="SuccessQueue"/> gains an item; re-armed
        /// by the drain when it empties the queue.
        /// </summary>
        internal TaskCompletionSource SuccessSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// A non-cancellation drain fault (e.g. FailWrites <c>PutAsync</c>),
        /// surfaced by <c>StartAsync</c> as the pairing failure outcome.
        /// </summary>
        internal Exception? DrainError;

        /// <summary>
        /// Non-zero while the pair-success persist sequence is in flight
        /// (WR-02): opened under <c>_gate</c> immediately before the store
        /// write, closed on a fault or when <c>Terminate</c> takes the terminal.
        /// While open, a watchdog <c>Timeout</c> is suppressed so a durable
        /// outcome is not discarded by a timer that fired during a slow store.
        /// </summary>
        internal int PersistWindow;
    }

    /// <summary>
    /// One enqueued pair-success arrival: the container bytes plus the
    /// stanza's reply-correlation id (null when the stanza carried none).
    /// </summary>
    /// <param name="Container">The container bytes (owned by the queue).</param>
    /// <param name="ReplyId">The stanza <c>id</c> for reply correlation.</param>
    private sealed class SuccessItem(byte[] Container, string? ReplyId)
    {
        internal byte[] Container { get; } = Container;

        internal string? ReplyId { get; } = ReplyId;
    }

    private abstract record RefOutcome
    {
        internal sealed record Ref(string Value) : RefOutcome;

        internal sealed record Error405 : RefOutcome;

        internal sealed record Retry(bool ReissuePromptly) : RefOutcome;
    }

    /// <summary>
    /// One link-code request reply (code attempts only): the server's ref
    /// (acceptance — the ref itself is the phone's business) or its numeric
    /// error code (rejection, surfaced descriptively).
    /// </summary>
    private abstract record CodeRefOutcome
    {
        internal sealed record Ref(string Value) : CodeRefOutcome;

        internal sealed record Error(string Code) : CodeRefOutcome;
    }
}
