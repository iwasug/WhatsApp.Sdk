namespace Iwasug.WhatsApp.Sdk.Domain;

/// <summary>
/// Typed domain record for the pre-key bundle stored by
/// <see cref="Stores.IPreKeyStore"/> (D-04: typed domain objects, not byte blobs).
/// </summary>
/// <remarks>
/// <para>
/// Phase-4 shape: the two Phase-1 identifier parameters stay first, and the
/// key material Phase 4 needs rides alongside them additively with defaults,
/// so every existing call site keeps compiling. The byte arrays are SDK-owned
/// serialization (opaque to hosts), cloned on the store read/write boundary
/// (CredentialRecord precedent), and never logged (T-4-01).
/// </para>
/// <para>
/// Two flavors share this shape: peer bundles fetched from the server carry
/// public material only (private fields stay null); the own-upload checkpoint
/// additionally carries the signed-prekey private so a restart can keep
/// serving the uploaded signed prekey without regenerating it.
/// </para>
/// </remarks>
/// <param name="SignedPreKeyId">Identifier of the current signed pre-key.</param>
/// <param name="OneTimePreKeyId">Identifier of the next one-time pre-key to hand out.</param>
/// <param name="IdentityKey">Peer identity public key (32 bytes raw), or null when unknown. Never logged.</param>
/// <param name="SignedPreKeyPublic">Signed-prekey public key (32 bytes raw), or null when unknown. Never logged.</param>
/// <param name="SignedPreKeySignature">Ed25519 signature over the DJB-encoded signed-prekey public (64 bytes), or null when unknown. Never logged.</param>
/// <param name="SignedPreKeyPrivate">Own signed-prekey private (32 bytes) — own-upload checkpoint only, never populated for peer bundles. Never logged.</param>
/// <param name="OneTimePreKeyPublic">One-time-prekey public key (32 bytes raw), or null when the bundle carries none. Never logged.</param>
/// <param name="RegistrationId">14-bit registration id (0 when unknown).</param>
public sealed record PreKeyBundle(int SignedPreKeyId, int OneTimePreKeyId,
    byte[]? IdentityKey = null,
    byte[]? SignedPreKeyPublic = null,
    byte[]? SignedPreKeySignature = null,
    byte[]? SignedPreKeyPrivate = null,
    byte[]? OneTimePreKeyPublic = null,
    int RegistrationId = 0);

/// <summary>
/// Typed domain record for a single stored one-time pre-key (D-04).
/// </summary>
/// <remarks>
/// Phase-4 shape: the Phase-1 identifier stays first; the key pair rides
/// alongside additively. The private is SDK-owned, cloned on the store
/// boundary, and never logged (CredentialRecord precedent, T-4-01).
/// </remarks>
/// <param name="PreKeyId">Identifier of this one-time pre-key.</param>
/// <param name="PublicKey">One-time-prekey public key (32 bytes raw), or null until generated. Never logged.</param>
/// <param name="PrivateKey">One-time-prekey private key (32 bytes raw), or null until generated. Never logged.</param>
public sealed record PreKeyRecordEntry(int PreKeyId,
    byte[]? PublicKey = null,
    byte[]? PrivateKey = null);
