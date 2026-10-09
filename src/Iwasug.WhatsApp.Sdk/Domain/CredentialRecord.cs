namespace Iwasug.WhatsApp.Sdk.Domain;

/// <summary>
/// Typed domain record for the per-account pairing credential row stored by
/// <see cref="Stores.ICredentialStore"/> (D-04: typed domain objects, not byte blobs).
/// </summary>
/// <remarks>
/// <para>
/// A single mutable row per account: <c>PutAsync</c> is last-write-wins, and the store
/// persists whatever the SDK's pairing layer writes. Durability class
/// (D-05): must-not-lose — the credential row is the artifact that lets a restarted
/// process resume without re-pairing (CONN-03).
/// </para>
/// <para>
/// Secret hygiene (T-3-01): the private key fields and <see cref="AdvSecret"/> are
/// SDK-owned serialization, never a public contract for hosts to reinterpret, and they
/// must never reach logs, exception messages, or event args — messages name rules and
/// operations only. The byte arrays are owned by whoever holds the record: stores
/// clone on the read/write boundary so a caller mutating its own array after a write
/// cannot silently rewrite the persisted row.
/// </para>
/// </remarks>
/// <param name="CreatedAt">When this account's credential row was first persisted (UTC).</param>
/// <param name="Platform">Companion platform label the row was paired as (for example <c>android</c>).</param>
/// <param name="NoiseKeyPrivate">X25519 handshake static private key (32 bytes) — the resume identity. Never logged.</param>
/// <param name="IdentityKeyPrivate">Registration identity private key (32 bytes, the <c>eIdent</c> source). Never logged.</param>
/// <param name="SigningKeyPrivate">Ed25519 device-signing private key (32 bytes, the pair-success device signature source). Never logged.</param>
/// <param name="AdvSecret">Account-verification secret (32 bytes). Never logged.</param>
/// <param name="PairedUser">Paired JID user (digits) once pairing completed; null before the first pair-success.</param>
/// <param name="Device">Companion device index.</param>
/// <param name="RegistrationId">Signal registration id (14-bit).</param>
public sealed record CredentialRecord(
    DateTimeOffset CreatedAt,
    string Platform,
    byte[] NoiseKeyPrivate,
    byte[] IdentityKeyPrivate,
    byte[] SigningKeyPrivate,
    byte[] AdvSecret,
    string? PairedUser,
    int Device,
    int RegistrationId);
