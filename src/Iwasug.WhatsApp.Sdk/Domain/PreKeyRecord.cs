namespace Iwasug.WhatsApp.Sdk.Domain;

/// <summary>
/// Typed domain record for the pre-key bundle stored by
/// <see cref="Stores.IPreKeyStore"/> (D-04: typed domain objects, not byte blobs).
/// </summary>
/// <remarks>
/// Phase-1 shape: identifiers only — the key material itself (signed pre-key public key,
/// one-time pre-key public keys, signature) is added additively with Phase 4's clean-room
/// Signal implementation. Consume-and-save sequencing (consume a one-time pre-key, then
/// save the updated session) is the SDK's job per D-07 — the store offers single-key CRUD
/// only, never a transaction API.
/// </remarks>
/// <param name="SignedPreKeyId">Identifier of the current signed pre-key.</param>
/// <param name="OneTimePreKeyId">Identifier of the next one-time pre-key to hand out.</param>
public sealed record PreKeyBundle(int SignedPreKeyId, int OneTimePreKeyId);

/// <summary>
/// Typed domain record for a single stored one-time pre-key (D-04).
/// </summary>
/// <remarks>
/// Key material arrives additively with Phase 4; identifiers alone are the Phase-1
/// contract so the store seam, reference implementation, and contract kit can be proven
/// before crypto lands.
/// </remarks>
/// <param name="PreKeyId">Identifier of this one-time pre-key.</param>
public sealed record PreKeyRecordEntry(int PreKeyId);
