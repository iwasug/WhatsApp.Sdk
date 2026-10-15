using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
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
    private int _disposed;
    private readonly Channel<WhatsAppEvent> _events = Channel.CreateUnbounded<WhatsAppEvent>();

    public SessionManager(IAccountRuntimeFactory factory, ILogger<SessionManager>? logger = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger;
    }

    public async ValueTask AddAccountAsync(AccountId accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AccountDescriptor descriptor = new AccountDescriptor { AccountId = accountId.Value };

        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(SessionManager));
        }

        await AddAccountCoreAsync(descriptor, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AddAccountAsync(AccountDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        if (descriptor is null) throw new ArgumentNullException(nameof(descriptor));
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(SessionManager));
        }

        await AddAccountCoreAsync(descriptor, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AddAccountCoreAsync(AccountDescriptor descriptor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AccountId accountId = AccountId.Create(descriptor.AccountId);

        AccountRuntime runtime;
        try
        {
            runtime = await _factory.CreateAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            throw;
        }

        if (!_accounts.TryAdd(accountId, runtime))
        {
            try { await runtime.DisposeAsync().ConfigureAwait(false); } catch { }
            throw new SdkDuplicateAccountException("An account with this id is already registered.");
        }

        _registrations[accountId] = 0;
        _descriptors[accountId] = descriptor;
        _logger?.LogInformation("Account registered {AccountId}.", accountId.Value);
    }

    public async ValueTask RemoveAccountAsync(AccountId accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(SessionManager));
        }

        if (!_accounts.TryRemove(accountId, out var runtime)) return;
        _registrations.TryRemove(accountId, out _);
        _descriptors.TryRemove(accountId, out _);

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

        if (!_accounts.ContainsKey(accountId)) throw new SdkUnknownAccountException("Unknown account.");

        AccountDescriptor? descriptor = _descriptors.TryGetValue(accountId, out var stored) ? stored : null;

        await RemoveAccountAsync(accountId, cancellationToken).ConfigureAwait(false);

        if (descriptor is not null)
        {
            await AddAccountAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await AddAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
        }
    }

    public AccountStatus? GetStatus(AccountId accountId)
    {
        if (!_accounts.TryGetValue(accountId, out var runtime)) return null;
        return new AccountStatus(
            accountId,
            runtime.Connection.State,
            SafetyCircuitState.Closed,
            runtime.OutboundQueue.Reader.Count,
            0,
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

    public async IAsyncEnumerable<WhatsAppEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var evt in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
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
