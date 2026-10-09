using Iwasug.WhatsApp.Sdk.Errors;

namespace Iwasug.WhatsApp.Sdk.Pairing;

/// <summary>
/// Builds the wire-exact QR payload a host renders for phone scanning (CONN-01):
/// the <c>https://wa.me/settings/linked_devices#</c> string joining the
/// server-supplied reference, the Noise public key, the identity public key, the
/// adv secret, and the companion platform id — plus the rotation policy the
/// coordinator and the tests share from this one source.
/// </summary>
/// <remarks>
/// <para>
/// Behavior provenance (read-only): the join shape
/// (<c>ref,noise,identity,adv,platform</c>) and the rotation intervals (first code
/// 60s, subsequent 20s, attempt window ≈160s) follow the MIT-licensed Baileys
/// companion-registration client utility and the whatsmeow QR-channel behavior
/// (MPL-2.0, behavioral reference only — no code copied). See 03-RESEARCH.md
/// §Code Example 1.
/// </para>
/// <para>
/// Design boundary (CONN-01 requirement, T-3-06): the reference is opaque
/// pass-through — this type exposes no member that parses, splits, decodes, or
/// otherwise interprets it. Scan tracking stays server-side; reference decode is
/// not implemented.
/// </para>
/// <para>
/// Rotation is pure over instants: callers source <c>issuedAt</c>/<c>now</c> from
/// their injected clock (production: the coordinator's <c>TimeProvider</c>; tests:
/// <c>FakeTimeProvider</c>), so no real-time sleeping is ever needed to prove the
/// schedule. This type never logs (T-3-06: it handles the adv secret).
/// </para>
/// </remarks>
public static class PairingQrPayload
{
    /// <summary>The QR wire prefix (lives in exactly one place).</summary>
    public const string WirePrefix = "https://wa.me/settings/linked_devices#";

    /// <summary>Lifetime of the first code of an attempt (60 seconds).</summary>
    public static readonly TimeSpan FirstCodeLifetime = TimeSpan.FromSeconds(60);

    /// <summary>Lifetime of every code after the first (20 seconds).</summary>
    public static readonly TimeSpan RotationInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Whole-attempt window (60 + 5 × 20 = 160 seconds): six codes, then the
    /// attempt ends in <see cref="PairingStatus.Timeout"/>.
    /// </summary>
    public static readonly TimeSpan AttemptTimeout = FirstCodeLifetime + (RotationInterval * 5);

    /// <summary>X25519 public key size for the noise and identity inputs.</summary>
    public const int PublicKeySize = 32;

    /// <summary>Adv-secret size.</summary>
    public const int AdvSecretSize = 32;

    /// <summary>
    /// Composes the exact wire string: the prefix followed by the reference, the
    /// base64 Noise public key, the base64 identity public key, the base64 adv
    /// secret, and the platform id — comma-joined, in that order.
    /// </summary>
    /// <param name="pairingRef">Opaque server-supplied reference (passed through byte-for-byte).</param>
    /// <param name="noisePublic">Noise static public key (32 bytes).</param>
    /// <param name="identityPublic">Registration identity public key (32 bytes).</param>
    /// <param name="advSecret">Account-verification secret (32 bytes).</param>
    /// <param name="platformId">Companion platform id label.</param>
    /// <returns>The exact string the host renders as a QR code.</returns>
    /// <exception cref="SdkFormatException">An input violates its shape rule (message names the rule only — never the material).</exception>
    public static string Build(
        string pairingRef,
        ReadOnlySpan<byte> noisePublic,
        ReadOnlySpan<byte> identityPublic,
        ReadOnlySpan<byte> advSecret,
        string platformId)
    {
        ArgumentNullException.ThrowIfNull(pairingRef);
        ArgumentNullException.ThrowIfNull(platformId);
        if (pairingRef.Length == 0)
        {
            throw new SdkFormatException("Pairing references must not be empty.");
        }

        if (noisePublic.Length != PublicKeySize)
        {
            throw new SdkFormatException("Pairing noise keys must be 32 bytes.");
        }

        if (identityPublic.Length != PublicKeySize)
        {
            throw new SdkFormatException("Pairing identity keys must be 32 bytes.");
        }

        if (advSecret.Length != AdvSecretSize)
        {
            throw new SdkFormatException("Pairing adv secrets must be 32 bytes.");
        }

        if (platformId.Length == 0)
        {
            throw new SdkFormatException("Pairing platform ids must not be empty.");
        }

        return string.Concat(
            WirePrefix,
            pairingRef,
            ",",
            Convert.ToBase64String(noisePublic),
            ",",
            Convert.ToBase64String(identityPublic),
            ",",
            Convert.ToBase64String(advSecret),
            ",",
            platformId);
    }

    /// <summary>
    /// Lifetime of the <paramref name="codeIndex"/>-th code (0-based): the first
    /// code lives <see cref="FirstCodeLifetime"/>, every later code lives
    /// <see cref="RotationInterval"/>.
    /// </summary>
    /// <param name="codeIndex">0-based code position in the attempt.</param>
    /// <returns>The lifetime of that code.</returns>
    public static TimeSpan RotationFor(int codeIndex) =>
        codeIndex <= 0 ? FirstCodeLifetime : RotationInterval;

    /// <summary>
    /// When the <paramref name="codeIndex"/>-th code issued at
    /// <paramref name="issuedAt"/> expires.
    /// </summary>
    /// <param name="issuedAt">Instant the code was issued (from the caller's injected clock).</param>
    /// <param name="codeIndex">0-based code position in the attempt.</param>
    /// <returns>The expiry instant.</returns>
    public static DateTimeOffset CodeExpiry(DateTimeOffset issuedAt, int codeIndex) =>
        issuedAt + RotationFor(codeIndex);

    /// <summary>
    /// Whether the attempt started at <paramref name="attemptStartedAt"/> has
    /// exhausted <see cref="AttemptTimeout"/> as of <paramref name="now"/>.
    /// </summary>
    /// <param name="attemptStartedAt">Instant the attempt started (from the caller's injected clock).</param>
    /// <param name="now">Current instant (from the caller's injected clock).</param>
    /// <returns><c>true</c> once the whole-attempt window has elapsed.</returns>
    public static bool AttemptExpired(DateTimeOffset attemptStartedAt, DateTimeOffset now) =>
        now - attemptStartedAt >= AttemptTimeout;
}
