namespace Iwasug.WhatsApp.Sdk.Domain;

/// <summary>
/// Typed domain record stored by <see cref="Stores.IIdentityStore"/> (D-04: typed domain
/// objects at the interface level, never byte blobs).
/// </summary>
/// <remarks>
/// <para>
/// Phase-4 shape: the placeholder string field stays first for compatibility,
/// and real Signal identity key material rides alongside it additively. The
/// byte arrays are SDK-owned serialization (opaque to hosts), cloned on the
/// store read/write boundary so a caller mutating its own array after a write
/// cannot silently rewrite the persisted row (CredentialRecord precedent).
/// </para>
/// <para>
/// Message-discipline rule (T-01-08, T-4-01): record contents are never
/// echoed into exception messages, log sites, or event args.
/// </para>
/// </remarks>
/// <param name="IdentityKeyPublic">
/// The peer's public identity key in its SDK-owned encoding, or <c>null</c> until real
/// key material arrives. Kept from Phase 1; prefer <see cref="IdentityKeyDjb"/> for new code.
/// </param>
/// <param name="IdentityKeyDjb">
/// The peer's identity public key in 33-byte DJB form (<c>0x05 || 32-byte key</c>),
/// or <c>null</c> until pinned. SDK-owned encoding, never logged.
/// </param>
/// <param name="FirstUsePinned">
/// Trust-on-first-use pin flag: true once the identity is pinned on first
/// use. Identity-change handling is typed (a changed peer identity surfaces
/// an identity-changed outcome requiring a host decision) — never a silent
/// re-trust (04-RESEARCH Security Domain).
/// </param>
public sealed record SignalIdentityRecord(string? IdentityKeyPublic,
    byte[]? IdentityKeyDjb = null,
    bool FirstUsePinned = false);
