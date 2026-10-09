using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Noise;
using Iwasug.WhatsApp.Sdk.Stores;

namespace Iwasug.WhatsApp.Sdk.Pairing;

/// <summary>
/// Credential-aware handshake payload seam (internal, 03-01): the connection discovers
/// this interface on its <see cref="INoiseHandshakePayloads"/> to load the persisted
/// static identity before the handshake and to re-read the credential row for the
/// <c>&lt;success&gt;</c> Open gate. Connections built over a bare
/// <see cref="INoiseHandshakePayloads"/> (pre-Phase-3 doubles) simply never match it.
/// </summary>
internal interface ICredentialHandshakePayloads : INoiseHandshakePayloads
{
    /// <summary>
    /// Loads the credential snapshot for this handshake attempt: reads the store once,
    /// remembers the login/registration decision for <see cref="INoiseHandshakePayloads.GetClientFinish"/>,
    /// and returns a fresh copy of the persisted Noise static private key (or <c>null</c>
    /// when no row exists, in which case the handshake mints a fresh identity).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A fresh copy of the static private key, or <c>null</c> when no row exists.</returns>
    /// <exception cref="Errors.SdkStoreException">The read failed (D-19) — never returned as missing.</exception>
    Task<byte[]?> LoadCredentialsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Independent credential read for the Open gate: a second read after
    /// <c>&lt;success&gt;</c> dispatch, proving the row is durable (D-06).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored row, or <c>null</c> when missing.</returns>
    /// <exception cref="Errors.SdkStoreException">The read failed (D-19) — never returned as missing.</exception>
    Task<CredentialRecord?> ReadCredentialsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Injected registration inputs for the no-credential branch of
/// <see cref="HandshakePayloadProvider"/>: the registration content is not sourced from
/// any store until plan 03-03, so both directions stay constructible from ctor inputs.
/// </summary>
/// <param name="IdentityPublic">Registration identity public key (32 bytes).</param>
/// <param name="SignedPreKeyId">Signed-prekey id.</param>
/// <param name="SignedPreKeyValue">Signed-prekey public value (32 bytes).</param>
/// <param name="SignedPreKeySignature">Signed-prekey signature (64 bytes).</param>
/// <param name="RegistrationId">Signal registration id (14-bit).</param>
/// <param name="Platform">Companion platform label.</param>
/// <param name="AppPrimary">App version primary.</param>
/// <param name="AppSecondary">App version secondary.</param>
/// <param name="AppTertiary">App version tertiary.</param>
internal sealed record RegistrationDefaults(
    byte[] IdentityPublic,
    int SignedPreKeyId,
    byte[] SignedPreKeyValue,
    byte[] SignedPreKeySignature,
    int RegistrationId,
    string Platform,
    uint AppPrimary,
    uint AppSecondary,
    uint AppTertiary);

/// <summary>
/// First production <see cref="INoiseHandshakePayloads"/> (03-01 tracer): decides
/// registration vs login by credential-row presence and hands the connection the
/// persisted Noise static key so the handshake identity is stable across restarts.
/// </summary>
/// <remarks>
/// <para>
/// Behavior provenance (whatsmeow <c>handshake.go</c> doHandshake, MPL-2.0, read-only):
/// message C carries <c>ClientFinish{Static, Payload}</c> where Static is the
/// store's persisted Noise public key (never a fresh one on resume) and Payload is
/// the marshalled ClientPayload selected by stored-credential presence
/// (<c>cli.Store.GetClientPayload()</c>); a missing payload fails the handshake.
/// The C# is independently written — no code copied. Baileys roles for the two
/// contents live in <see cref="ClientPayloadFactory"/>.
/// </para>
/// <para>
/// Secret hygiene (T-3-01): this provider never logs and never embeds key material
/// in exceptions — messages name rules and operations only. It retains no private
/// key bytes between calls: <see cref="LoadCredentialsAsync"/> returns a fresh copy
/// the handshake imports and zeroizes, and the finish snapshot keeps only the public
/// login decision (username/device/platform), never key material.
/// </para>
/// </remarks>
internal sealed class HandshakePayloadProvider : ICredentialHandshakePayloads
{
    private readonly AccountId _accountId;
    private readonly ICredentialStore _credentials;
    private readonly RegistrationDefaults _registrationDefaults;
    private Snapshot _snapshot = Snapshot.Registration;

    /// <summary>
    /// Creates the provider over one account's credential store.
    /// </summary>
    /// <param name="accountId">Opaque account key — must come from <see cref="AccountId.Create"/>.</param>
    /// <param name="credentials">The account's credential store (the instance IS the namespace, D-02).</param>
    /// <param name="registrationDefaults">Registration inputs for the no-row branch.</param>
    /// <exception cref="SdkFormatException"><paramref name="accountId"/> is the default (uninitialized) value.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="credentials"/> or <paramref name="registrationDefaults"/> is null.</exception>
    internal HandshakePayloadProvider(
        AccountId accountId,
        ICredentialStore credentials,
        RegistrationDefaults registrationDefaults)
    {
        if (accountId.Value is null)
        {
            throw new SdkFormatException(
                "AccountId must be created via AccountId.Create — the default (uninitialized) AccountId is not valid.");
        }

        _accountId = accountId;
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _registrationDefaults = registrationDefaults ?? throw new ArgumentNullException(nameof(registrationDefaults));
    }

    /// <summary>The account this provider loads credentials for.</summary>
    internal AccountId AccountId => _accountId;

    /// <inheritdoc />
    public byte[] GetClientHello()
    {
        // Hello semantics stay opaque in this tracer: the handshake authenticates the
        // blob without parsing it, and registration versioning (which hello will
        // eventually carry) lands with the pairing coordinator. Empty is a valid
        // opaque blob — the handshake accepts zero-length payloads.
        return Array.Empty<byte>();
    }

    /// <inheritdoc />
    public byte[] GetClientFinish()
    {
        Snapshot snapshot = Volatile.Read(ref _snapshot);
        if (snapshot.HasRow && snapshot.Username is not null)
        {
            return ClientPayloadFactory.Login(
                snapshot.Username,
                snapshot.Device,
                snapshot.Platform,
                _registrationDefaults.AppPrimary,
                _registrationDefaults.AppSecondary,
                _registrationDefaults.AppTertiary);
        }

        return ClientPayloadFactory.Registration(
            _registrationDefaults.IdentityPublic,
            _registrationDefaults.SignedPreKeyId,
            _registrationDefaults.SignedPreKeyValue,
            _registrationDefaults.SignedPreKeySignature,
            _registrationDefaults.RegistrationId,
            _registrationDefaults.Platform,
            _registrationDefaults.AppPrimary,
            _registrationDefaults.AppSecondary,
            _registrationDefaults.AppTertiary);
    }

    /// <inheritdoc />
    public async Task<byte[]?> LoadCredentialsAsync(CancellationToken cancellationToken)
    {
        CredentialRecord? row = await _credentials.GetAsync(cancellationToken).ConfigureAwait(false);
        if (row is null || string.IsNullOrEmpty(row.PairedUser))
        {
            // No row (or a row with no paired identity yet): the handshake mints a
            // fresh identity and message C carries registration. A throwing read
            // propagates to the connection, which recycles it as a 408 — failure is
            // never conflated with missing (D-19).
            Volatile.Write(ref _snapshot, Snapshot.Registration);
            return null;
        }

        Volatile.Write(ref _snapshot, new Snapshot(true, row.PairedUser, row.Device, row.Platform));
        return (byte[])row.NoiseKeyPrivate.Clone();
    }

    /// <inheritdoc />
    public Task<CredentialRecord?> ReadCredentialsAsync(CancellationToken cancellationToken) =>
        _credentials.GetAsync(cancellationToken);

    private sealed class Snapshot
    {
        internal static readonly Snapshot Registration = new(false, null, 0, string.Empty);

        internal Snapshot(bool hasRow, string? username, int device, string platform)
        {
            HasRow = hasRow;
            Username = username;
            Device = device;
            Platform = platform;
        }

        internal bool HasRow { get; }

        internal string? Username { get; }

        internal int Device { get; }

        internal string Platform { get; }
    }
}
