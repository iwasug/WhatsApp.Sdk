using System.Threading;
using System.Threading.Channels;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Hosting;
using Iwasug.WhatsApp.Sdk.Messaging;
using Iwasug.WhatsApp.Sdk.Noise;
using Iwasug.WhatsApp.Sdk.Safety;
using Iwasug.WhatsApp.Sdk.Stores;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iwasug.WhatsApp.Sdk.Accounts;

/// <summary>Default composition factory over existing seams (store-first, fail-clean).</summary>
internal sealed class DefaultAccountRuntimeFactory : IAccountRuntimeFactory
{
    private readonly StoreRegistry? _registry;
    private readonly StoreRegistryOptions? _storeOptions;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly TimeProvider _timeProvider;
    private readonly int _channelCapacity;
    private readonly int _eventChannelCapacity;
    private readonly ConnectionOptions? _defaultConnection;
    private readonly SafetyOptions? _defaultSafety;
    private int _seq;

    public DefaultAccountRuntimeFactory(
        StoreRegistry? registry = null,
        ILoggerFactory? loggerFactory = null,
        TimeProvider? timeProvider = null,
        int channelCapacity = 128,
        int eventChannelCapacity = 128,
        ConnectionOptions? defaultConnection = null,
        SafetyOptions? defaultSafety = null,
        StoreRegistryOptions? storeOptions = null)
    {
        _registry = registry;
        _loggerFactory = loggerFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _channelCapacity = channelCapacity <= 0 ? 128 : channelCapacity;
        _eventChannelCapacity = eventChannelCapacity <= 0 ? 128 : eventChannelCapacity;
        _defaultConnection = defaultConnection;
        _defaultSafety = defaultSafety;
        _storeOptions = storeOptions;
    }

    public ValueTask<AccountRuntime> CreateAsync(AccountId accountId, CancellationToken cancellationToken = default)
    {
        return CreateAsync(new AccountDescriptor { AccountId = accountId.Value }, cancellationToken);
    }

    public async ValueTask<AccountRuntime> CreateAsync(AccountDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        if (descriptor is null) throw new ArgumentNullException(nameof(descriptor));
        cancellationToken.ThrowIfCancellationRequested();

        AccountId accountId = AccountId.Create(descriptor.AccountId);

        ConnectionOptions connection = ConnectionOptions.ValidateOptions(descriptor.Connection ?? _defaultConnection);
        SafetyOptions safety = SafetyOptions.Validate(descriptor.Safety ?? _defaultSafety);

        StoreRegistry registry = _registry ?? new StoreRegistry(_storeOptions ?? new StoreRegistryOptions());

        StoreSession session;
        try
        {
            session = await registry.CreateSessionAsync(accountId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            throw;
        }

        WaConnection? connectionInstance = null;
        try
        {
            connectionInstance = new WaConnection(
                accountId,
                () => throw new NotSupportedException("No transport wired in Wave 1 factory."),
                new EmptyNoiseHandshakePayloads(),
                connection,
                _timeProvider,
                (_loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WaConnection>());

            connectionInstance.StoreRegistry = registry;
            connectionInstance.SafetyPolicy = safety;

            var logger = (_loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<AccountRuntime>();
            var safetyLogger = (_loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SafetyGate>();

            ISafetyGate gate = new SafetyGate(
                _timeProvider,
                safety,
                safetyLogger,
                _ => new ValueTask<StoreSession>(session),
                (evt, token) => ValueTask.CompletedTask);

            Messenger messenger = connectionInstance.Messaging;

            Channel<OutboundEnvelope> queue = Channel.CreateBounded<OutboundEnvelope>(
                new BoundedChannelOptions(_channelCapacity)
                {
                    SingleReader = false,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait,
                });

            var cts = new CancellationTokenSource();

            var runtime = new AccountRuntime(
                accountId,
                session,
                connectionInstance,
                messenger,
                gate,
                queue,
                () => session,
                logger,
                cts,
                connection,
                safety);

            runtime.LastConnectedAt = _timeProvider.GetUtcNow().AddTicks(Interlocked.Increment(ref _seq));
            return runtime;
        }
        catch
        {
            if (connectionInstance is not null)
            {
                try { await connectionInstance.DisposeAsync().ConfigureAwait(false); }
                catch { }
            }
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch { }
            throw;
        }
    }

    private sealed class EmptyNoiseHandshakePayloads : INoiseHandshakePayloads
    {
        public byte[] GetClientHello() => Array.Empty<byte>();
        public byte[] GetClientFinish() => Array.Empty<byte>();
    }
}