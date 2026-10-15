using System.Threading;
using System.Threading.Channels;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Errors;
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
    private readonly ILoggerFactory? _loggerFactory;
    private readonly TimeProvider _timeProvider;
    private readonly int _channelCapacity;
    private int _seq;

    public DefaultAccountRuntimeFactory(
        StoreRegistry? registry = null,
        ILoggerFactory? loggerFactory = null,
        TimeProvider? timeProvider = null,
        int channelCapacity = 128)
    {
        _registry = registry;
        _loggerFactory = loggerFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _channelCapacity = channelCapacity <= 0 ? 128 : channelCapacity;
    }

    public async ValueTask<AccountRuntime> CreateAsync(AccountId accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        StoreRegistry registry = _registry ?? new StoreRegistry(new StoreRegistryOptions());

        StoreSession session;
        try
        {
            session = await registry.CreateSessionAsync(accountId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            throw;
        }

        WaConnection? connection = null;
        try
        {
            connection = new WaConnection(
                accountId,
                () => throw new NotSupportedException("No transport wired in Wave 1 factory."),
                new EmptyNoiseHandshakePayloads(),
                null,
                _timeProvider,
                (_loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WaConnection>());

            connection.StoreRegistry = registry;
            connection.SafetyPolicy = SafetyOptions.Validate(null);

            var logger = (_loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<AccountRuntime>();
            var safetyLogger = (_loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SafetyGate>();

            ISafetyGate gate = new SafetyGate(
                _timeProvider,
                SafetyOptions.Validate(null),
                safetyLogger,
                _ => new ValueTask<StoreSession>(session),
                (evt, token) => ValueTask.CompletedTask);

            // Force messenger creation (built over connection).
            Messenger messenger = connection.Messaging;

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
                connection,
                messenger,
                gate,
                queue,
                () => session,
                logger,
                cts);

            runtime.LastConnectedAt = _timeProvider.GetUtcNow().AddTicks(Interlocked.Increment(ref _seq));
            return runtime;
        }
        catch
        {
            if (connection is not null)
            {
                try { await connection.DisposeAsync().ConfigureAwait(false); }
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