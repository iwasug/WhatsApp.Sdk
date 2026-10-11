using System.Security.Cryptography;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Noise;
using Iwasug.WhatsApp.Sdk.Stores;
using NSec.Cryptography;

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
/// Registration inputs for the no-row branch of <see cref="HandshakePayloadProvider"/>:
/// the signed-prekey triple and app version stay constructor inputs because the row
/// carries no prekey fields (prekey lifecycle has not landed — documented boundary).
/// Row-based fields (identity public, registration id, platform) are overridden from
/// the persisted row whenever one exists, so hosts only ever supply the prekey
/// material here. Public since 03-06 (plan Behavior 5): the sample host builds a
/// provider over these, exactly as any first-party host must.
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
public sealed record RegistrationDefaults(
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
/// the handshake imports and zeroizes, and the finish snapshot keeps only public
/// material (username/device/platform/identity public), never private key bytes.
/// </para>
/// <para>
/// Public construction seam (03-06 plan Behavior 5): hosts build one provider per
/// attempt over their <see cref="ICredentialStore"/> and hand it to
/// <see cref="Connection.WaConnection"/> — pairing via <c>WaConnection.Pairing</c>
/// requires credential-aware payloads, so this is the host-facing entry point the
/// sample consumes. The internal credential seam itself stays internal; only the
/// type and its lifecycle-relevant members are public.
/// </para>
/// </remarks>
public sealed class HandshakePayloadProvider : ICredentialHandshakePayloads
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
    public HandshakePayloadProvider(
        AccountId accountId,
        ICredentialStore credentials,
        RegistrationDefaults registrationDefaults)
        : this(accountId, credentials, registrationDefaults, appVersion: null)
    {
    }

    /// <summary>
    /// Creates the provider with an explicit app version (used by the connection when it
    /// owns a version resolver).
    /// </summary>
    /// <param name="accountId">Opaque account key — must come from <see cref="AccountId.Create"/>.</param>
    /// <param name="credentials">The account's credential store (the instance IS the namespace, D-02).</param>
    /// <param name="registrationDefaults">Registration inputs for the no-row branch.</param>
    /// <param name="appVersion">
    /// Explicit app version for the handshake payload (override ?? resolver value from
    /// <see cref="ConnectionOptions"/>); <c>null</c> keeps the registration defaults.
    /// </param>
    /// <exception cref="SdkFormatException"><paramref name="accountId"/> is the default (uninitialized) value.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="credentials"/> or <paramref name="registrationDefaults"/> is null.</exception>
    internal HandshakePayloadProvider(
        AccountId accountId,
        ICredentialStore credentials,
        RegistrationDefaults registrationDefaults,
        WaVersion? appVersion)
    {
        if (accountId.Value is null)
        {
            throw new SdkFormatException(
                "AccountId must be created via AccountId.Create — the default (uninitialized) AccountId is not valid.");
        }

        _accountId = accountId;
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _registrationDefaults = registrationDefaults ?? throw new ArgumentNullException(nameof(registrationDefaults));
        CurrentAppVersion = appVersion;
    }

    /// <summary>
    /// The app version the next <see cref="GetClientFinish"/> emits (set per attempt by
    /// the connection when it owns a version resolver; <c>null</c> keeps the
    /// registration defaults).
    /// </summary>
    internal WaVersion? CurrentAppVersion { get; set; }

    /// <summary>The account this provider loads credentials for.</summary>
    internal AccountId AccountId => _accountId;

    /// <summary>
    /// The account's credential store (the instance IS the namespace, D-02): the
    /// pairing coordinator reads QR key material from the same row the handshake
    /// path loads, so both directions observe one persisted identity.
    /// </summary>
    internal ICredentialStore Credentials => _credentials;

    /// <summary>
    /// Registration-bootstrap seam (03-03 Task 3): when no credential row exists,
    /// generates fresh identity material (X25519 noise keypair, Ed25519 signing
    /// key, identity key, 32-byte adv secret) and persists it before returning —
    /// the write is awaited (D-06) before any handshake or registration payload
    /// is constructed from it, so a crash after generation still resumes.
    /// </summary>
    /// <remarks>
    /// Implemented as a snapshot-refreshing load (the handshake's own
    /// <see cref="LoadCredentialsAsync"/>): one generation path serves the
    /// coordinator-first start and the connect handshake alike, and any later
    /// <c>GetClientFinish</c> is row-based either way. A present row is never
    /// overwritten (idempotent resume). A throwing read or <c>PutAsync</c>
    /// propagates unwrapped — pairing fails closed instead of continuing
    /// undegraded. The returned static copy is zeroized here (unlike the
    /// handshake, this caller imports nothing).
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Errors.SdkStoreException">The read or persist failed — pairing cannot start.</exception>
    internal async Task EnsureRegistrationMaterialAsync(CancellationToken cancellationToken)
    {
        byte[]? unused = await LoadCredentialsAsync(cancellationToken).ConfigureAwait(false);
        if (unused is not null)
        {
            CryptographicOperations.ZeroMemory(unused);
        }
    }

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
        WaVersion version = CurrentAppVersion
            ?? new WaVersion(
                _registrationDefaults.AppPrimary,
                _registrationDefaults.AppSecondary,
                _registrationDefaults.AppTertiary);
        if (snapshot.HasRow && snapshot.Username is not null)
        {
            return ClientPayloadFactory.Login(
                snapshot.Username,
                snapshot.Device,
                snapshot.Platform,
                version.Primary,
                version.Secondary,
                version.Tertiary);
        }

        // Row-based registration (03-03 Task 3): the identity public and the
        // registration id come from the persisted row (generated-and-persisted
        // before this payload could exist); the signed-prekey triple stays on
        // the injected defaults until prekey lifecycle lands (documented
        // boundary — the row carries no prekey fields). Without a row the
        // injected defaults serve both, as before.
        bool rowRegistration = snapshot.IdentityPublic is not null;
        byte[] eIdent = rowRegistration ? snapshot.IdentityPublic! : _registrationDefaults.IdentityPublic;
        int eRegid = rowRegistration ? snapshot.RegistrationId : _registrationDefaults.RegistrationId;
        string ePlatform = rowRegistration ? snapshot.Platform : _registrationDefaults.Platform;
        return ClientPayloadFactory.Registration(
            eIdent,
            _registrationDefaults.SignedPreKeyId,
            _registrationDefaults.SignedPreKeyValue,
            _registrationDefaults.SignedPreKeySignature,
            eRegid,
            ePlatform,
            version.Primary,
            version.Secondary,
            version.Tertiary);
    }

    /// <inheritdoc />
    public async Task<byte[]?> LoadCredentialsAsync(CancellationToken cancellationToken)
    {
        CredentialRecord? row = await _credentials.GetAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            // No row: generate and persist fresh identity (awaited, D-06), then
            // handshake with the persisted static — a crash after generation
            // resumes from the row instead of minting a second identity. A
            // throwing read or write propagates to the connection, which
            // recycles it as a 408 — failure is never conflated with missing
            // (D-19).
            row = await GenerateAndPersistAsync(cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrEmpty(row.PairedUser))
        {
            // A row with no paired identity yet (freshly generated or seeded):
            // the handshake still uses the persisted static (stable across
            // restarts) while message C carries row-based registration. The
            // derived public transfers to the snapshot (public key material is
            // safe to retain; private bytes never are).
            byte[] identityPublic = DeriveX25519Public(row.IdentityKeyPrivate);
            Volatile.Write(ref _snapshot, Snapshot.FromUnpairedRow(row, identityPublic));
            return (byte[])row.NoiseKeyPrivate.Clone();
        }

        Volatile.Write(ref _snapshot, new Snapshot(true, row.PairedUser, row.Device, row.Platform, null, 0));
        return (byte[])row.NoiseKeyPrivate.Clone();
    }

    /// <summary>
    /// Generates fresh pairing identity material and persists it (D-06: the
    /// <c>PutAsync</c> is awaited before the row is returned or used).
    /// </summary>
    /// <remarks>
    /// Platform CSPRNG only — any 32 bytes is a valid X25519/Ed25519 seed
    /// (clamping derives the scalar), so no curve math runs here; NSec derives
    /// the publics. On a persist failure the fresh buffers are zeroized before
    /// the exception propagates (fail-closed, nothing half-stored is trusted).
    /// </remarks>
    private async Task<CredentialRecord> GenerateAndPersistAsync(CancellationToken cancellationToken)
    {
        byte[] noise = RandomNumberGenerator.GetBytes(32);
        byte[] identity = RandomNumberGenerator.GetBytes(32);
        byte[] signing = RandomNumberGenerator.GetBytes(32);
        byte[] adv = RandomNumberGenerator.GetBytes(32);
        var row = new CredentialRecord(
            DateTimeOffset.UtcNow,
            _registrationDefaults.Platform,
            noise,
            identity,
            signing,
            adv,
            null,
            0,
            RandomNumberGenerator.GetInt32(0, 16384));

        bool persisted = false;
        try
        {
            await _credentials.PutAsync(row, cancellationToken).ConfigureAwait(false);
            persisted = true;
        }
        finally
        {
            if (!persisted)
            {
                CryptographicOperations.ZeroMemory(noise);
                CryptographicOperations.ZeroMemory(identity);
                CryptographicOperations.ZeroMemory(signing);
                CryptographicOperations.ZeroMemory(adv);
            }
        }

        return row;
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

    /// <inheritdoc />
    public Task<CredentialRecord?> ReadCredentialsAsync(CancellationToken cancellationToken) =>
        _credentials.GetAsync(cancellationToken);

    private sealed class Snapshot
    {
        internal static readonly Snapshot Registration = new(false, null, 0, string.Empty, null, 0);

        internal Snapshot(bool hasRow, string? username, int device, string platform, byte[]? identityPublic, int registrationId)
        {
            HasRow = hasRow;
            Username = username;
            Device = device;
            Platform = platform;
            IdentityPublic = identityPublic;
            RegistrationId = registrationId;
        }

        internal static Snapshot FromUnpairedRow(CredentialRecord row, byte[] identityPublic) =>
            new(true, null, row.Device, row.Platform, identityPublic, row.RegistrationId);

        internal bool HasRow { get; }

        internal string? Username { get; }

        internal int Device { get; }

        internal string Platform { get; }

        internal byte[]? IdentityPublic { get; }

        internal int RegistrationId { get; }
    }
}
