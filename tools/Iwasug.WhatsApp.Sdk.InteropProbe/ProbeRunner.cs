using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Pairing;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.InMemory;
using Iwasug.WhatsApp.Sdk.Transport;

namespace Iwasug.WhatsApp.Sdk.InteropProbe;

/// <summary>
/// Connect/observe/decide runner for the interop drift probe (09-RESEARCH
/// Pattern 3): builds a fresh-registration connection over the injected
/// transport, starts the pairing attempt on the live stream, observes the
/// pairing surface, and classifies the outcome as accepted (0), drift (10),
/// or infra (20) — never persisting credentials and never printing material
/// (T-9-06).
/// </summary>
/// <remarks>
/// <para>
/// Bounded by construction (T-9-07): every wait races the terminal decision
/// against the injected <see cref="TimeSpan"/> and the connect task, so a
/// silent or unreachable server classifies as infra instead of hanging.
/// </para>
/// <para>
/// Testability seam: the transport factory is injected, so the whole decision
/// path runs over a scripted loopback peer (real keys, scripted bytes) with no
/// network; the live entry point injects the real WebSocket transport.
/// </para>
/// <para>
/// The runner never throws for a classified outcome (it returns one) — an
/// <see cref="OperationCanceledException"/> surfaces only when the caller
/// cancels. The result pairs with the entry point's single
/// <c>PROBE_RESULT</c> evidence line.
/// </para>
/// </remarks>
public sealed class ProbeRunner
{
    private readonly AccountId _accountId;
    private readonly Func<IFrameTransport> _transportFactory;
    private readonly TimeSpan _timeout;

    /// <summary>Creates one probe run.</summary>
    /// <param name="accountId">Fresh account key (one probe, one throwaway identity).</param>
    /// <param name="transportFactory">Mints the transport per connect attempt; tests inject a scripted loopback pair.</param>
    /// <param name="timeout">The hard bound on the whole decision; elapsing classifies as infra.</param>
    /// <exception cref="ArgumentNullException"><paramref name="transportFactory"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is not positive.</exception>
    public ProbeRunner(AccountId accountId, Func<IFrameTransport> transportFactory, TimeSpan timeout)
    {
        _accountId = accountId;
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The probe bound must be positive.");
        }

        _timeout = timeout;
    }

    /// <summary>
    /// Runs one probe decision end to end. Returns the classified outcome; the
    /// process exits with <see cref="ProbeOutcome.ProcessExitCode"/>.
    /// </summary>
    /// <param name="cancellationToken">Host cancellation (e.g. Ctrl+C): aborts with an <see cref="OperationCanceledException"/>.</param>
    /// <returns>The classified probe outcome — always produced, for any server behavior.</returns>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public async Task<ProbeOutcome> RunAsync(CancellationToken cancellationToken = default)
    {
        // Fresh in-memory store, fresh registration material, throwaway account
        // (T-9-06): the probe never reads, writes, or persists credentials.
        // The factory's primary CreateAsync returns the signal-session store; the
        // credential seam is the explicit interface member (sample precedent).
        var factory = new InMemoryStoreFactory();
        ICredentialStoreFactory storeFactory = factory;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        WaConnection? connection = null;
        Task? connectTask = null;
        Task? pairingTask = null;

        try
        {
            ICredentialStore store = await storeFactory.CreateAsync(_accountId, linked.Token).ConfigureAwait(false);
            var payloads = new HandshakePayloadProvider(_accountId, store, CreateFreshRegistrationDefaults());
            connection = new WaConnection(_accountId, _transportFactory, payloads);

            // Subscribe before connecting so no status can be missed (the
            // coordinator holds the channel-ready pulse, but the first Code can
            // race the connect await). First event decides; TrySetResult keeps
            // the decision one-shot — the runner never re-decides or continues.
            var completed = new TaskCompletionSource<ProbeOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Pairing.StateChanged += (_, e) => completed.TrySetResult(ProbeClassifier.FromStatus(e.Status));

            // The verdict vocabulary IS the pairing surface: the attempt must
            // be live for a Code (acceptance) or ClientOutdated (405) terminal
            // to be observable, so start it before awaiting the connect task.
            // The coordinator's attempt loop gates its first send on the
            // connection's channel-ready pulse, so this order is safe.
            pairingTask = connection.Pairing.StartAsync(linked.Token);
            connectTask = connection.ConnectAsync(linked.Token);

            Task delayTask = Task.Delay(_timeout, linked.Token);
            List<Task> races = [completed.Task, delayTask, connectTask, pairingTask];

            while (true)
            {
                Task winner = await Task.WhenAny(races).ConfigureAwait(false);

                if (winner == completed.Task)
                {
                    return await completed.Task.ConfigureAwait(false);
                }

                if (winner == delayTask)
                {
                    // The bound elapsed. Host cancel surfaces as the operation's
                    // own OCE; otherwise the verdict is infra — a timeout is a
                    // classified exit, never a stuck process (T-9-07).
                    linked.Token.ThrowIfCancellationRequested();
                    return completed.Task.IsCompleted
                        ? await completed.Task.ConfigureAwait(false)
                        : new ProbeOutcome(ProbeExitCode.Infra, "bounded wait elapsed");
                }

                // The connect task finished (opened, faulted, or was canceled).
                if (completed.Task.IsCompleted)
                {
                    // On the 405 path the terminal signal and the connect fault
                    // race; the server's verdict wins whenever it arrived.
                    return await completed.Task.ConfigureAwait(false);
                }

                if (winner.IsFaulted)
                {
                    return ProbeClassifier.FromException(winner.Exception!.GetBaseException());
                }

                if (winner.IsCanceled)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    return new ProbeOutcome(ProbeExitCode.Infra, "operation canceled");
                }

                // The connection opened; keep observing until the verdict or bound.
                races.Remove(winner);
            }
        }
        finally
        {
            // Teardown is best-effort and ordered so nothing here can replace a
            // classified outcome (T-9-07): cancel the shared token first (the
            // monitor, the attempt loop, and any in-flight connect unwind),
            // observe the settled pairing and connect tasks, then dispose the
            // connection and the store.
            linked.Cancel();

            if (pairingTask is not null)
            {
                await ObserveSettledAsync(pairingTask).ConfigureAwait(false);
            }

            if (connectTask is not null)
            {
                await ObserveSettledAsync(connectTask).ConfigureAwait(false);
            }

            if (connection is not null)
            {
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A dispose fault after a classified verdict is swallowed:
                    // the process contract reports exactly one outcome.
                }
            }

            try
            {
                await factory.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Same one-outcome rule as above.
            }
        }
    }

    /// <summary>
    /// Fresh-registration defaults in the sample's observed shape (zero
    /// material, Platform android, App 2/3000/1043857760): enough for the
    /// server to reach the QR-ref / 405 decision the probe measures.
    /// </summary>
    private static RegistrationDefaults CreateFreshRegistrationDefaults() => new(
        IdentityPublic: new byte[32],
        SignedPreKeyId: 7,
        SignedPreKeyValue: new byte[32],
        SignedPreKeySignature: new byte[64],
        RegistrationId: 0,
        Platform: "android",
        AppPrimary: 2,
        AppSecondary: 3000,
        AppTertiary: 1043857760);

    /// <summary>Observes a settled task during teardown; cancellation and SDK faults never escape.</summary>
    private static async Task ObserveSettledAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (SdkException)
        {
        }
    }
}
