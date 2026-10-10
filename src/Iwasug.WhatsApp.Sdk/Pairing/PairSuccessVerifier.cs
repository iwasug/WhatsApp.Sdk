using System.Security.Cryptography;
using Google.Protobuf;
using NSec.Cryptography;

namespace Iwasug.WhatsApp.Sdk.Pairing;

/// <summary>
/// Why a pair-success container was rejected. Rule names only — never byte
/// content — so reasons are safe to log and to surface in diagnostics.
/// </summary>
internal enum PairSuccessRejectReason
{
    /// <summary>The bytes are not a decodable container (truncated, non-canonical, or wrongly shaped).</summary>
    Malformed,

    /// <summary>A field verification requires is absent (details, covered user, or account key).</summary>
    MissingField,

    /// <summary>The container carries a field number this schema does not define.</summary>
    UnknownField,

    /// <summary>The HMAC does not match (wrong secret or tampered details).</summary>
    HmacMismatch,

    /// <summary>The account signature does not verify under the HMAC-authenticated account key.</summary>
    AccountSignatureInvalid,

    /// <summary>The device signature does not verify under the row-pinned device key.</summary>
    DeviceSignatureInvalid,
}

/// <summary>
/// The extracted pair-success payload: the paired identity and the refreshed
/// key material the credential row stores. Reachable only through the
/// <see cref="PairSuccessResult.Verified"/> branch — a rejected container
/// exposes nothing usable.
/// </summary>
/// <param name="PairedUser">Paired JID user (digits).</param>
/// <param name="Device">Companion device index.</param>
/// <param name="RegistrationId">Server-issued Signal registration id refresh.</param>
internal sealed record PairSuccessPayload(string PairedUser, int Device, int RegistrationId);

/// <summary>
/// Discriminated pair-success verdict: either verified (carrying the payload)
/// or rejected (carrying only a log-safe reason). Never <c>null</c>, never an
/// exception for hostile input — exceptions mean the caller is buggy.
/// </summary>
internal abstract record PairSuccessResult
{
    private PairSuccessResult()
    {
    }

    /// <summary>The container verified: the payload is safe to persist.</summary>
    /// <param name="Payload">The extracted payload.</param>
    internal sealed record Verified(PairSuccessPayload Payload) : PairSuccessResult;

    /// <summary>The container failed closed: no payload, no side effects.</summary>
    /// <param name="Reason">The log-safe rule that failed.</param>
    internal sealed record Rejected(PairSuccessRejectReason Reason) : PairSuccessResult;
}

/// <summary>
/// Fail-closed gate for inbound pair-success containers (03-04, CONN-01,
/// T-3-08): strict-parse, then HMAC with the stored adv secret in fixed time,
/// then the Ed25519 account and device signatures — the FIRST failure returns
/// <c>Rejected</c> before anything is written, replied, or emitted.
/// </summary>
/// <remarks>
/// <para>
/// Provenance (NOTICE.md "whatsmeow rule"): the verification CHAIN (HMAC
/// over the details with the adv secret, account/device signature prefixes
/// <c>{6,0}</c>/<c>{6,1}</c>, verify-before-any-side-effect ordering) follows
/// the recorded 03-RESEARCH.md Code Example 4, which cites whatsmeow
/// <c>pair.go</c> behavioral read-only and Baileys
/// <c>src/Utils/validate-connection.ts</c> (configureSuccessfulPairing)
/// read-only. No upstream code or bytes were copied; the C# below is
/// independently written against this repo's hand-written
/// <c>Proto/adv.proto</c>.
/// </para>
/// <para>
/// Key story (repo-local v1, documented honestly): the HMAC authenticates the
/// envelope — only a holder of the adv secret (the phone that scanned our
/// QR) can produce it. The claimed account key rides INSIDE the HMAC-covered
/// details, so swapping it breaks the HMAC; the account signature is checked
/// under that HMAC-authenticated key. The device key is pinned to the
/// persisted credential row (derived from its Ed25519 signing private), which
/// the caller supplies — a device signature from any other key fails closed.
/// </para>
/// <para>
/// Strictness (no library unknown-field surfacing relied upon): the raw
/// container bytes are walked by hand for field numbers — unknown numbers,
/// duplicate fields, and malformed encodings fail closed — and the HMAC and
/// both signatures cover the EXACT received details bytes sliced from that
/// walk, never a re-serialization.
/// </para>
/// <para>
/// Secret hygiene (T-3-01/T-3-07): no logging here (the coordinator is the
/// sole log site and logs rule names only); key copies and MAC buffers are
/// zeroized in <c>finally</c>; primitives come from the BCL
/// (<c>HMACSHA256</c>, <c>CryptographicOperations.FixedTimeEquals</c>) and
/// NSec (Ed25519) — never hand-rolled.
/// </para>
/// </remarks>
internal static class PairSuccessVerifier
{
    /// <summary>HMAC-SHA256 output size.</summary>
    internal const int HmacSize = 32;

    /// <summary>Ed25519 signature size.</summary>
    internal const int SignatureSize = 64;

    /// <summary>Ed25519 public key size.</summary>
    internal const int PublicKeySize = 32;

    /// <summary>Adv secret size (matches the credential row invariant).</summary>
    internal const int AdvSecretSize = 32;

    /// <summary>Account-signature domain prefix (03-RESEARCH.md Code Example 4).</summary>
    private static readonly byte[] AccountSignaturePrefix = [6, 0];

    /// <summary>Device-signature domain prefix (03-RESEARCH.md Code Example 4).</summary>
    private static readonly byte[] DeviceSignaturePrefix = [6, 1];

    /// <summary>Top-level container fields this schema defines (adv.proto v1).</summary>
    private static readonly HashSet<int> ContainerFields = [1, 2, 3, 4];

    /// <summary>AdvDetails fields this schema defines (adv.proto v1).</summary>
    private static readonly HashSet<int> DetailsFields = [1, 2, 3, 4];

    /// <summary>
    /// Verifies one pair-success container against the stored adv secret and
    /// the row-pinned device public key.
    /// </summary>
    /// <param name="containerBytes">The raw container bytes from the stanza (never mutated).</param>
    /// <param name="advSecret">The stored adv secret (32 bytes; never mutated, never logged).</param>
    /// <param name="expectedDevicePublicKey">The row-pinned Ed25519 device public key (32 bytes).</param>
    /// <returns>
    /// <c>Verified</c> with the extracted payload, or <c>Rejected</c> with the
    /// first failing rule — exposing nothing usable on rejection.
    /// </returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="ArgumentException">The container is empty or a key argument has the wrong length.</exception>
    internal static PairSuccessResult Verify(
        byte[] containerBytes,
        byte[] advSecret,
        byte[] expectedDevicePublicKey)
    {
        ArgumentNullException.ThrowIfNull(containerBytes);
        ArgumentNullException.ThrowIfNull(advSecret);
        ArgumentNullException.ThrowIfNull(expectedDevicePublicKey);
        if (containerBytes.Length == 0)
        {
            throw new ArgumentException("The pair-success container must not be empty.", nameof(containerBytes));
        }

        if (advSecret.Length != AdvSecretSize)
        {
            throw new ArgumentException("The adv secret must be 32 bytes.", nameof(advSecret));
        }

        if (expectedDevicePublicKey.Length != PublicKeySize)
        {
            throw new ArgumentException("The device public key must be 32 bytes.", nameof(expectedDevicePublicKey));
        }

        // Strict walk of the RAW container bytes: unknown field numbers,
        // duplicate fields, and malformed encodings fail closed before any
        // cryptographic check runs.
        WalkOutcome containerWalk = WalkFields(containerBytes, ContainerFields, out HashSet<int> containerSeen);
        if (containerWalk == WalkOutcome.Malformed)
        {
            return new PairSuccessResult.Rejected(PairSuccessRejectReason.Malformed);
        }

        if (containerWalk == WalkOutcome.UnknownField)
        {
            return new PairSuccessResult.Rejected(PairSuccessRejectReason.UnknownField);
        }

        if (!containerSeen.Contains(1))
        {
            return new PairSuccessResult.Rejected(PairSuccessRejectReason.MissingField);
        }

        if (!TryGetLengthDelimitedPayload(containerBytes, 1, out byte[]? detailsBytes) || detailsBytes is null)
        {
            return new PairSuccessResult.Rejected(PairSuccessRejectReason.Malformed);
        }

        try
        {
            WalkOutcome detailsWalk = WalkFields(detailsBytes, DetailsFields, out HashSet<int> detailsSeen);
            if (detailsWalk == WalkOutcome.Malformed)
            {
                return new PairSuccessResult.Rejected(PairSuccessRejectReason.Malformed);
            }

            if (detailsWalk == WalkOutcome.UnknownField)
            {
                return new PairSuccessResult.Rejected(PairSuccessRejectReason.UnknownField);
            }

            if (!detailsSeen.Contains(1) || !detailsSeen.Contains(4))
            {
                return new PairSuccessResult.Rejected(PairSuccessRejectReason.MissingField);
            }

            PairSuccessContainer container;
            try
            {
                container = PairSuccessContainer.Parser.ParseFrom(containerBytes);
            }
            catch (InvalidProtocolBufferException)
            {
                // Undecodable content (e.g. invalid UTF-8 in a string field).
                return new PairSuccessResult.Rejected(PairSuccessRejectReason.Malformed);
            }

            if (container.Details is null
                || string.IsNullOrEmpty(container.Details.PairedUser)
                || container.Details.AccountPublic.Length == 0)
            {
                return new PairSuccessResult.Rejected(PairSuccessRejectReason.MissingField);
            }

            if (container.Hmac.Length != HmacSize
                || container.AccountSignature.Length != SignatureSize
                || container.DeviceSignature.Length != SignatureSize
                || container.Details.AccountPublic.Length != PublicKeySize)
            {
                return new PairSuccessResult.Rejected(PairSuccessRejectReason.Malformed);
            }

            byte[] accountPublic = container.Details.AccountPublic.ToByteArray();
            try
            {
                byte[] computed = HMACSHA256.HashData(advSecret, detailsBytes);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(computed, container.Hmac.ToByteArray()))
                    {
                        return new PairSuccessResult.Rejected(PairSuccessRejectReason.HmacMismatch);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(computed);
                }

                byte[] accountInput = Concat(AccountSignaturePrefix, detailsBytes);
                try
                {
                    if (!VerifyEd25519(accountPublic, accountInput, container.AccountSignature.ToByteArray()))
                    {
                        return new PairSuccessResult.Rejected(PairSuccessRejectReason.AccountSignatureInvalid);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(accountInput);
                }

                byte[] deviceInput = Concat(DeviceSignaturePrefix, detailsBytes, accountPublic);
                try
                {
                    if (!VerifyEd25519(expectedDevicePublicKey, deviceInput, container.DeviceSignature.ToByteArray()))
                    {
                        return new PairSuccessResult.Rejected(PairSuccessRejectReason.DeviceSignatureInvalid);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(deviceInput);
                }

                // Wire numerics are uint32 but the persisted row stores int (WR-01):
                // a device index >= 2^31 casts negative and a registration id beyond
                // the 14-bit domain would make every future handshake throw from
                // ClientPayloadFactory — the send-side guards never run on a received
                // pair-success, so the verifier rejects out-of-domain values here,
                // before anything is persisted (authenticated or not).
                if (container.Details.RegistrationId > ClientPayloadFactory.MaxRegistrationId
                    || container.Details.Device > int.MaxValue)
                {
                    return new PairSuccessResult.Rejected(PairSuccessRejectReason.Malformed);
                }

                return new PairSuccessResult.Verified(
                    new PairSuccessPayload(
                        container.Details.PairedUser,
                        (int)container.Details.Device,
                        (int)container.Details.RegistrationId));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(accountPublic);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(detailsBytes);
        }
    }

    /// <summary>
    /// Verifies one Ed25519 signature with NSec (libsodium): public-key import
    /// plus verification, no private material anywhere on this path.
    /// </summary>
    private static bool VerifyEd25519(byte[] publicKey, byte[] data, byte[] signature)
    {
        // NSec PublicKey carries no native resources (unlike Key) — import,
        // verify, and let the GC reclaim it; only Key needs deterministic
        // disposal on this path.
        PublicKey key = PublicKey.Import(SignatureAlgorithm.Ed25519, publicKey, KeyBlobFormat.RawPublicKey);
        return SignatureAlgorithm.Ed25519.Verify(key, data, signature);
    }

    /// <summary>Concatenates read-only segments into one fresh buffer.</summary>
    private static byte[] Concat(params byte[][] segments)
    {
        int total = 0;
        foreach (byte[] segment in segments)
        {
            total += segment.Length;
        }

        byte[] result = new byte[total];
        int offset = 0;
        foreach (byte[] segment in segments)
        {
            segment.CopyTo(result.AsSpan(offset));
            offset += segment.Length;
        }

        return result;
    }

    /// <summary>One strict-walk outcome for a raw protobuf message.</summary>
    private enum WalkOutcome
    {
        /// <summary>Every field number is known and every encoding is canonical.</summary>
        Ok,

        /// <summary>Truncated varint, overlong value, duplicate field, or illegal wire type.</summary>
        Malformed,

        /// <summary>A field number outside the schema's allowed set.</summary>
        UnknownField,
    }

    /// <summary>
    /// Walks one protobuf message's raw tag varints (values are skipped, never
    /// interpreted): collects the distinct field numbers seen and classifies
    /// the strictness outcome. Malformed encodings and duplicates report
    /// <c>Malformed</c>; out-of-schema numbers report <c>UnknownField</c>.
    /// </summary>
    private static WalkOutcome WalkFields(ReadOnlySpan<byte> input, HashSet<int> allowed, out HashSet<int> seen)
    {
        seen = new HashSet<int>();
        int pos = 0;
        while (pos < input.Length)
        {
            if (!TryReadVarint(input, ref pos, out ulong tag))
            {
                return WalkOutcome.Malformed;
            }

            int field = (int)(tag >> 3);
            int wire = (int)(tag & 7);
            if (field <= 0 || !seen.Add(field))
            {
                return WalkOutcome.Malformed;
            }

            if (!allowed.Contains(field))
            {
                return WalkOutcome.UnknownField;
            }

            switch (wire)
            {
                case 0:
                    if (!TryReadVarint(input, ref pos, out _))
                    {
                        return WalkOutcome.Malformed;
                    }

                    break;
                case 1:
                    pos += 8;
                    if (pos > input.Length)
                    {
                        return WalkOutcome.Malformed;
                    }

                    break;
                case 2:
                    if (!TryReadVarint(input, ref pos, out ulong length)
                        || length > (ulong)(input.Length - pos))
                    {
                        return WalkOutcome.Malformed;
                    }

                    pos += (int)length;
                    break;
                case 5:
                    pos += 4;
                    if (pos > input.Length)
                    {
                        return WalkOutcome.Malformed;
                    }

                    break;
                default:
                    // Groups (3/4) and reserved wire types never appear in
                    // this schema's protoc output — fail closed.
                    return WalkOutcome.Malformed;
            }
        }

        return WalkOutcome.Ok;
    }

    /// <summary>
    /// Slices the length-delimited payload of one field number from raw message
    /// bytes (first occurrence wins; the strict walk above already rejected
    /// duplicates). Returns <c>false</c> when the field is absent, not
    /// length-delimited, or malformed.
    /// </summary>
    private static bool TryGetLengthDelimitedPayload(ReadOnlySpan<byte> input, int fieldNumber, out byte[]? payload)
    {
        payload = null;
        int pos = 0;
        while (pos < input.Length)
        {
            if (!TryReadVarint(input, ref pos, out ulong tag))
            {
                return false;
            }

            int field = (int)(tag >> 3);
            int wire = (int)(tag & 7);
            if (wire != 2)
            {
                // Not length-delimited: skip by wire type to keep walking.
                switch (wire)
                {
                    case 0:
                        if (!TryReadVarint(input, ref pos, out _))
                        {
                            return false;
                        }

                        continue;
                    case 1:
                        pos += 8;
                        if (pos > input.Length)
                        {
                            return false;
                        }

                        continue;
                    case 5:
                        pos += 4;
                        if (pos > input.Length)
                        {
                            return false;
                        }

                        continue;
                    default:
                        return false;
                }
            }

            if (!TryReadVarint(input, ref pos, out ulong length)
                || length > (ulong)(input.Length - pos))
            {
                return false;
            }

            if (field == fieldNumber)
            {
                payload = input.Slice(pos, (int)length).ToArray();
                return true;
            }

            pos += (int)length;
        }

        return false;
    }

    /// <summary>Reads one protobuf varint; <c>false</c> on truncation or overflow.</summary>
    private static bool TryReadVarint(ReadOnlySpan<byte> input, ref int pos, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (true)
        {
            if (pos >= input.Length || shift >= 70)
            {
                return false;
            }

            byte b = input[pos++];
            if (shift == 63 && b > 1)
            {
                return false;
            }

            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }
    }
}
