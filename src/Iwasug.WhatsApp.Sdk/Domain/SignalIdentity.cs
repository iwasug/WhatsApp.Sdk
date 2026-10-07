namespace Iwasug.WhatsApp.Sdk.Domain;

/// <summary>
/// Typed domain record stored by <see cref="Stores.IIdentityStore"/> (D-04: typed domain
/// objects at the interface level, never byte blobs).
/// </summary>
/// <remarks>
/// Phase-1 placeholder fields: real Signal identity key material (Ed25519 public keys and
/// their signatures) arrives with Phase 4, at which point NSec typed keys replace this
/// field shape additively. Message-discipline rule (T-01-08): record contents are never
/// echoed into exception messages.
/// </remarks>
/// <param name="IdentityKeyPublic">
/// The peer's public identity key in its SDK-owned encoding, or <c>null</c> until real
/// key material arrives in Phase 4. Its encoding is an internal implementation detail,
/// not a public contract.
/// </param>
public sealed record SignalIdentityRecord(string? IdentityKeyPublic);
