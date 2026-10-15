using System.Threading;
using System.Threading.Channels;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Hosting;
using Iwasug.WhatsApp.Sdk.Messaging;
using Iwasug.WhatsApp.Sdk.Noise;
using Iwasug.WhatsApp.Sdk.Pairing;
using Iwasug.WhatsApp.Sdk.Safety;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Transport;
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
    private readonly Uri? _endpoint;
    private readonly Func<AccountId, IFrameTransport>? _transportFactory;
    private readonly Func<AccountId, INoiseHandshakePayloads>? _handshakePayloadsFactory;
    private readonly RegistrationDefaults? _registrationDefaults;
    private int _seq;

    public DefaultAccountRuntimeFactory(
        StoreRegistry? registry = null,
        ILoggerFactory? loggerFactory = null,
        TimeProvider? timeProvider = null,
        int channelCapacity = 128,
        int eventChannelCapacity = 128,
        ConnectionOptions? defaultConnection = null,
        SafetyOptions? defaultSafety = null,
        StoreRegistryOptions? storeOptions = null,
        Uri? endpoint = null,
        Func<AccountId, IFrameTransport>? transportFactory = null,
        Func<AccountId, INoiseHandshakePayloads>? handshakePayloadsFactory = null,
        RegistrationDefaults? registrationDefaults = null)
    {
        _registry = registry;
        _loggerFactory = loggerFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _channelCapacity = channelCapacity <= 0 ? 128 : channelCapacity;
        _eventChannelCapacity = eventChannelCapacity <= 0 ? 128 : eventChannelCapacity;
        _defaultConnection = defaultConnection;
        _defaultSafety = defaultSafety;
        _storeOptions = storeOptions;
        _endpoint = endpoint;
        _transportFactory = transportFactory;
        _handshakePayloadsFactory = handshakePayloadsFactory;
        _registrationDefaults = registrationDefaults;
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
                ResolveTransportFactory(accountId, connection),
                ResolveHandshakePayloads(accountId, session),
                connection,
                _timeProvider,
                (_loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WaConnection>());

            connectionInstance.StoreRegistry = registry;
            connectionInstance.SafetyPolicy = safety;

            var logger = (_loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<AccountRuntime>();

            // 07 CR-02: exactly one gate per account — the real gate inside the messenger
            // that every send traverses. A second, factory-owned gate would never gate a
            // send and would leave AccountStatus.CircuitState permanently Closed.
            Messenger messenger = connectionInstance.Messaging;
            ISafetyGate gate = messenger.SafetyGate;

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

    /// <summary>
    /// Resolves the per-attempt transport factory (07 CR-01): the host override when
    /// configured, otherwise the production <see cref="WebSocketFrameTransport"/> against
    /// the configured/default endpoint. Internal so tests can prove the default does not
    /// throw and is the real transport.
    /// </summary>
    internal Func<IFrameTransport> ResolveTransportFactory(AccountId accountId, ConnectionOptions connection)
    {
        if (_transportFactory is not null)
        {
            return () => _transportFactory(accountId)
                ?? throw new InvalidOperationException("The configured TransportFactory returned null.");
        }

        Uri endpoint = _endpoint ?? new Uri(WebSocketFrameTransport.DefaultEndpoint);
        ILogger<WebSocketFrameTransport>? transportLogger = _loggerFactory?.CreateLogger<WebSocketFrameTransport>();
        return () => new WebSocketFrameTransport(endpoint, connection, transportLogger);
    }

    /// <summary>
    /// Resolves the credential-aware handshake payloads (07 CR-01): pairing and login
    /// both require a <see cref="HandshakePayloadProvider"/> over the account's credential
    /// store; a bare payload double makes <c>WaConnection.Pairing</c> throw.
    /// </summary>
    private INoiseHandshakePayloads ResolveHandshakePayloads(AccountId accountId, StoreSession session)
    {
        if (_handshakePayloadsFactory is not null)
        {
            return _handshakePayloadsFactory(accountId)
                ?? throw new InvalidOperationException("The configured HandshakePayloadsFactory returned null.");
        }

        RegistrationDefaults defaults = _registrationDefaults ?? PlaceholderRegistrationDefaults();
        if (_registrationDefaults is null)
        {
            (_loggerFactory ?? NullLoggerFactory.Instance)
                .CreateLogger<DefaultAccountRuntimeFactory>()
                .LogWarning(
                    "No RegistrationDefaults configured for {AccountId}: using placeholder registration material. " +
                    "First-time pairing/registration will be rejected by the server until real signed-prekey material " +
                    "is supplied; login-only accounts with an existing credential row are unaffected.",
                    accountId.Value);
        }

        return new HandshakePayloadProvider(accountId, session.Credentials, defaults);
    }

    /// <summary>
    /// Explicit placeholders (the PairingSample values) for the no-row registration branch.
    /// Not valid key material: first-time registration needs host-supplied
    /// <see cref="RegistrationDefaults"/>; this only keeps login-only accounts working
    /// without fabricating secrets, and the use is logged per account.
    /// </summary>
    private static RegistrationDefaults PlaceholderRegistrationDefaults() => new(
        IdentityPublic: new byte[32],
        SignedPreKeyId: 7,
        SignedPreKeyValue: new byte[32],
        SignedPreKeySignature: new byte[64],
        RegistrationId: 0,
        Platform: "android",
        AppPrimary: 2,
        AppSecondary: 3000,
        AppTertiary: 1043857760);
}