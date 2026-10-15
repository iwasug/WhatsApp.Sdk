using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Hosting;
using Iwasug.WhatsApp.Sdk.Messaging;
using Microsoft.Extensions.Logging;

namespace Iwasug.WhatsApp.Sdk.Accounts;

/// <summary>Atomic multi-account registry over <see cref="AccountRuntime"/>.</summary>
internal sealed class SessionManager : IWhatsAppSessionManager, IDisposable
{
    private readonly ConcurrentDictionary<AccountId, AccountRuntime> _accounts = new();
    private readonly ConcurrentDictionary<AccountId, byte> _registrations = new();
    private readonly ConcurrentDictionary<AccountId, AccountDescriptor> _descriptors = new();
    private readonly IAccountRuntimeFactory _factory;
    private readonly ILogger<SessionManager>? _logger;
    private readonly object _lifecycleGate = new();

    // 07 WR-03: serializes Add/Remove/Restart so a restart's remove+add pair is
    // atomic with respect to every other mutation (no resurrection, no spurious
    // duplicate), and a registration can never race the shutdown snapshot.
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    // 07 WR-04: broadcast signal for account-set changes. Readers snapshot the
    // current source before enumerating accounts and race it against the per-account
    // reads, so an account added (or removed) while every existing account is quiet
    // wakes the fused reader instead of waiting for the next pre-existing event.
    private readonly object _membershipLock = new();
    private TaskCompletionSource _membershipSignal = CreateMembershipSignal();
    private int _disposed;

    public SessionManager(IAccountRuntimeFactory factory, ILogger<SessionManager>? logger = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger;
    }

    public async ValueTask AddAccountAsync(AccountId accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AccountDescriptor descriptor = new AccountDescriptor { AccountId = accountId.Value };

        ThrowIfDisposed();

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AddAccountCoreAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async ValueTask AddAccountAsync(AccountDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        if (descriptor is null) throw new ArgumentNullException(nameof(descriptor));
        cancellationToken.ThrowIfCancellationRequested();

        ThrowIfDisposed();

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AddAccountCoreAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async ValueTask AddAccountCoreAsync(AccountDescriptor descriptor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AccountId accountId = AccountId.Create(descriptor.AccountId);

        AccountRuntime runtime = await _factory.CreateAsync(descriptor, cancellationToken).ConfigureAwait(false);

        bool disposed;
        bool duplicate = false;
        lock (_lifecycleGate)
        {
            // 07 WR-03: the disposed flag and the registration are decided under one
            // lock, so DisposeAsync's drain snapshot can never miss a runtime that
            // was created concurrently — add-after-dispose disposes the runtime and
            // throws instead of leaking it past shutdown.
            disposed = Volatile.Read(ref _disposed) != 0;
            if (!disposed)
            {
                if (_accounts.TryAdd(accountId, runtime))
                {
                    _registrations[accountId] = 0;
                    _descriptors[accountId] = descriptor;
                }
                else
                {
                    duplicate = true;
                }
            }
        }

        if (disposed)
        {
            await DisposeRuntimeQuietlyAsync(runtime).ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(SessionManager));
        }

        if (duplicate)
        {
            await DisposeRuntimeQuietlyAsync(runtime).ConfigureAwait(false);
            throw new SdkDuplicateAccountException("An account with this id is already registered.");
        }

        _logger?.LogInformation("Account registered {AccountId}.", accountId.Value);
        SignalMembershipChange();
    }

    public async ValueTask RemoveAccountAsync(AccountId accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ThrowIfDisposed();

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RemoveAccountCoreAsync(accountId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async ValueTask RemoveAccountCoreAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_accounts.TryRemove(accountId, out var runtime)) return;
        _registrations.TryRemove(accountId, out _);
        _descriptors.TryRemove(accountId, out _);
        SignalMembershipChange();

        try
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Account dispose failed {AccountId}.", accountId.Value);
        }
    }

    public async ValueTask RestartAccountAsync(AccountId accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ThrowIfDisposed();

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RestartAccountCoreAsync(accountId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async ValueTask RestartAccountCoreAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        // 07 WR-03: remove+add run in one critical section (the mutation gate):
        // a concurrent remove can no longer be undone by the re-add, and a
        // concurrent add can no longer race the restart into a spurious duplicate
        // for the account the restart itself just removed.
        if (!_accounts.ContainsKey(accountId)) throw new SdkUnknownAccountException("Unknown account.");

        AccountDescriptor? descriptor = _descriptors.TryGetValue(accountId, out var stored) ? stored : null;

        await RemoveAccountCoreAsync(accountId, cancellationToken).ConfigureAwait(false);
        await AddAccountCoreAsync(
            descriptor ?? new AccountDescriptor { AccountId = accountId.Value },
            cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(SessionManager));
        }
    }

    private static TaskCompletionSource CreateMembershipSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Wakes every fused reader currently parked on the account set (07 WR-04):
    /// the current signal is swapped for a fresh one under the lock, and the old
    /// one completes — readers awaiting it re-enumerate <c>_accounts</c>.
    /// </summary>
    private void SignalMembershipChange()
    {
        TaskCompletionSource previous;
        lock (_membershipLock)
        {
            previous = _membershipSignal;
            _membershipSignal = CreateMembershipSignal();
        }

        previous.TrySetResult();
    }

    private static async ValueTask DisposeRuntimeQuietlyAsync(AccountRuntime runtime)
    {
        try
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best effort: the runtime is being discarded either way.
        }
    }

    /// <summary>Resolves the live runtime for <paramref name="accountId"/> (07 WR-08 seam backing).</summary>
    internal bool TryGetRuntime(AccountId accountId, [NotNullWhen(true)] out AccountRuntime? runtime)
        => _accounts.TryGetValue(accountId, out runtime);

    public AccountStatus? GetStatus(AccountId accountId)
    {
        if (!_accounts.TryGetValue(accountId, out var runtime)) return null;
        SafetyCircuitState circuit = SafetyCircuitState.Closed;
        if (runtime.SafetyGate is Safety.SafetyGate sg)
        {
            circuit = sg.CircuitState switch
            {
                Safety.CircuitState.Closed => SafetyCircuitState.Closed,
                Safety.CircuitState.Open => SafetyCircuitState.Open,
                Safety.CircuitState.HalfOpen => SafetyCircuitState.HalfOpen,
                _ => SafetyCircuitState.Closed,
            };
        }
        return new AccountStatus(
            accountId,
            runtime.Connection.State,
            circuit,
            runtime.OutboundQueue.Reader.Count,
            runtime.RecentFailures,
            runtime.LastConnectedAt,
            runtime.LastActivityAt,
            runtime.LastErrorRuleName);
    }

    public IReadOnlyCollection<AccountStatus> GetStatuses()
    {
        var list = new List<AccountStatus>();
        foreach (var kvp in _accounts)
        {
            var status = GetStatus(kvp.Key);
            if (status is not null) list.Add(status);
        }
        return list;
    }

    /// <inheritdoc />
    public bool TryGetConnection(AccountId accountId, [NotNullWhen(true)] out WaConnection? connection)
    {
        if (_accounts.TryGetValue(accountId, out AccountRuntime? runtime))
        {
            connection = runtime.Connection;
            return true;
        }

        connection = null;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetMessenger(AccountId accountId, [NotNullWhen(true)] out Messenger? messenger)
    {
        if (_accounts.TryGetValue(accountId, out AccountRuntime? runtime))
        {
            messenger = runtime.Messenger;
            return true;
        }

        messenger = null;
        return false;
    }

    public async IAsyncEnumerable<AccountEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var enumerators = new Dictionary<AccountId, IAsyncEnumerator<WhatsAppEvent>>();
        var nextTasks = new Dictionary<AccountId, Task<bool>>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // 07 WR-04: snapshot the membership signal BEFORE enumerating accounts so a
                // change landing between the snapshot and the wait below can never be missed:
                // any change after the snapshot completes this source, and any change before
                // it is already reflected in the enumeration.
                Task membershipChanged;
                lock (_membershipLock)
                {
                    membershipChanged = _membershipSignal.Task;
                }

                foreach (var kvp in _accounts.ToArray())
                {
                    if (!enumerators.ContainsKey(kvp.Key))
                    {
                        var en = kvp.Value.Messenger.ReadEventsAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
                        enumerators[kvp.Key] = en;
                        nextTasks[kvp.Key] = en.MoveNextAsync().AsTask();
                    }
                }

                if (nextTasks.Count == 0)
                {
                    try { await membershipChanged.WaitAsync(cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { yield break; }
                    continue;
                }

                Task<bool> completed;
                try
                {
                    Task<Task<bool>> anyEvent = Task.WhenAny(nextTasks.Values);
                    Task first = await Task.WhenAny(anyEvent, membershipChanged).WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (first == membershipChanged)
                    {
                        // An account was added or removed while every enumerated account
                        // was quiet — re-enumerate instead of sleeping on stale tasks.
                        continue;
                    }

                    completed = await anyEvent.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                AccountId completedAccount = default;
                foreach (var kvp in nextTasks)
                {
                    if (kvp.Value == completed) { completedAccount = kvp.Key; break; }
                }
                if (!nextTasks.ContainsKey(completedAccount)) { continue; }
                bool hasItem;
                try { hasItem = await completed.ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
                catch { hasItem = false; }

                if (!hasItem)
                {
                    var en = enumerators[completedAccount];
                    enumerators.Remove(completedAccount);
                    nextTasks.Remove(completedAccount);
                    try { await en.DisposeAsync().ConfigureAwait(false); } catch { }
                    continue;
                }

                var currentEnumerator = enumerators[completedAccount];
                yield return new AccountEvent(completedAccount, currentEnumerator.Current);

                try
                {
                    nextTasks[completedAccount] = currentEnumerator.MoveNextAsync().AsTask();
                }
                catch
                {
                    enumerators.Remove(completedAccount);
                    nextTasks.Remove(completedAccount);
                    try { await currentEnumerator.DisposeAsync().ConfigureAwait(false); } catch { }
                }
            }
        }
        finally
        {
            foreach (var en in enumerators.Values)
            {
                try { await en.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }
    }

    public async IAsyncEnumerable<WhatsAppEvent> ReadAccountEventsAsync(AccountId accountId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_accounts.ContainsKey(accountId)) throw new SdkUnknownAccountException("Unknown account.");
        if (_accounts.TryGetValue(accountId, out var runtime))
        {
            await foreach (var evt in runtime.Messenger.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        KeyValuePair<AccountId, AccountRuntime>[] snapshot;
        lock (_lifecycleGate)
        {
            Interlocked.Exchange(ref _disposed, 1);
            snapshot = _accounts.ToArray();
        }

        // 07 WR-04: wake fused readers so they observe the drained account set promptly.
        SignalMembershipChange();

        foreach (var kvp in snapshot)
        {
            _accounts.TryRemove(new KeyValuePair<AccountId, AccountRuntime>(kvp.Key, kvp.Value));
            _registrations.TryRemove(kvp.Key, out _);
            _descriptors.TryRemove(kvp.Key, out _);
            try
            {
                await kvp.Value.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Account dispose failed {AccountId}.", kvp.Key.Value);
            }
        }
    }
    public void Dispose() { try { DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { } }
}
