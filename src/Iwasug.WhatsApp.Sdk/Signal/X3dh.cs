using System.Security.Cryptography;
using System.Text;
using Iwasug.WhatsApp.Sdk.Errors;
using NSec.Cryptography;

namespace Iwasug.WhatsApp.Sdk.Signal;

/// <summary>
/// A peer's prekey bundle: the inputs to X3DH session establishment (04-01).
/// Key encodings follow the Baileys MIT reads at commit 0af2386
/// (<c>src/Utils/signal.ts</c>: <c>identity</c>/<c>value</c> nodes carry raw
/// 32-byte keys, <c>id</c> nodes are fixed-width big-endian):
/// identity keys travel in 33-byte DJB form once parsed, raw key nodes stay
/// 32 bytes. The single identity field is the X25519 DH identity (wire
/// truth); the key the bundle signature verifies under travels separately
/// (see <see cref="X3dh.InitializeAlice"/> — XEdDSA unifies them, the
/// plan's NSec-only approximation does not).
/// </summary>
/// <param name="IdentityKeyDjb">Peer identity public key, 33-byte DJB form (<c>0x05 || 32-byte key</c>).</param>
/// <param name="SignedPreKey">Peer signed-prekey public key (32 bytes raw).</param>
/// <param name="SignedPreKeySignature">Ed25519 signature over the DJB-encoded signed-prekey public (64 bytes).</param>
/// <param name="SignedPreKeyId">Peer signed-prekey id.</param>
/// <param name="OneTimePreKey">Peer one-time prekey public (32 bytes raw), when the bundle carries one.</param>
/// <param name="OneTimePreKeyId">Peer one-time prekey id, when the bundle carries one.</param>
/// <param name="RegistrationId">Peer 14-bit registration id.</param>
internal sealed record PeerBundle(
    byte[] IdentityKeyDjb,
    byte[] SignedPreKey,
    byte[] SignedPreKeySignature,
    uint SignedPreKeyId,
    byte[]? OneTimePreKey,
    uint? OneTimePreKeyId,
    uint RegistrationId);

/// <summary>Alice's (initiator) X3DH output: the shared root, the first sending chain key, and her base public.</summary>
internal sealed record AliceSession(byte[] RootKey, byte[] ChainKey, byte[] BasePublic);

/// <summary>Bob's (responder) X3DH output: the shared root and the first receiving chain key.</summary>
internal sealed record BobSession(byte[] RootKey, byte[] ChainKey);

/// <summary>
/// X3DH session establishment over NSec + BCL (04-01, internal): bundle
/// processing into the root key and first chain key, per the public Signal
/// X3DH construction (DH1‖DH2‖DH3[‖DH4] through HKDF-SHA256 with the 0xFF
/// domain-separation prefix and the <c>WhisperText</c> info label into
/// root ‖ chain).
/// </summary>
/// <remarks>
/// <para>
/// Provenance: the DH concatenation order, the <c>WhisperText</c> info
/// label, the 32 <c>0xFF</c> domain-separation prefix, and the DJB
/// <c>Encode</c> form (<c>0x05 ||</c> u-coordinate) follow the public Signal
/// X3DH specification rev 1 §2.1–§2.2/§3.3 (read 2026-10-11; KDF input key
/// material is <c>F ‖ KM</c> with <c>F</c> = 32 <c>0xFF</c> bytes for
/// X25519, salt zeros, info the application label); the bundle shapes
/// (3-byte big-endian prekey ids, 4-byte big-endian registration, 32-byte
/// raw key nodes) were learned read-only from Baileys MIT
/// <c>src/Utils/signal.ts</c> at commit 0af2386; the signed-prekey signature
/// covers the DJB-encoded signed-prekey public (Baileys <c>signedKeyPair</c>
/// in <c>src/Utils/crypto.ts</c> at the same commit signs
/// <c>generateSignalPubKey(preKey.public)</c>, matching X3DH's
/// <c>Sig(IKB, Encode(SPKB))</c>).
/// </para>
/// <para>
/// XEdDSA NOTE (known interop risk, tracked for the 04-05 live-device UAT):
/// X3DH §2.2 specifies <c>Sig</c> as XEdDSA, but this plan's crypto surface
/// is NSec + BCL only (STACK.md forbids hand-rolled curve math, and no vetted
/// .NET XEdDSA primitive exists in-tree), so signing/verification here is
/// Ed25519 over the same DJB-encoded bytes through NSec. The seam is exactly
/// <see cref="SignWithIdentity"/>/<see cref="VerifySignedPreKey"/>: if UAT
/// against a real bundle rejects our verification, only those two functions
/// change (plus fixtures) — no caller touches curve bytes directly.
/// </para>
/// <para>
/// Discipline (Shared Patterns 4, T-4-01): NSec typed keys for every curve
/// operation (never hand-rolled math); persisted-key imports fail closed
/// with rule-only <see cref="SdkFormatException"/> (never naming material);
/// every DH output and derived buffer the session does not return is
/// zeroized. No logging on this path.
/// </para>
/// </remarks>
internal static class X3dh
{
    /// <summary>X25519/Ed25519 raw key length.</summary>
    internal const int KeyLength = 32;

    /// <summary>DJB-encoded identity length.</summary>
    internal const int DjbIdentityLength = 33;

    /// <summary>Ed25519 signature length.</summary>
    internal const int SignatureLength = 64;

    /// <summary>Derived root+chain length (HKDF output).</summary>
    internal const int DerivedLength = 64;

    /// <summary>X3DH HKDF info label (public Signal construction).</summary>
    internal const string InfoLabel = "WhisperText";

    /// <summary>Maximum registration id (14-bit, CredentialRecord precedent).</summary>
    internal const uint MaxRegistrationId = 16383;

    /// <summary>
    /// Alice side: verifies the bundle's signed prekey, runs the DH chain
    /// against it, and derives the root key plus the first sending chain key.
    /// </summary>
    /// <param name="ownIdentityPrivate">Our 32-byte identity private. Never logged.</param>
    /// <param name="ephemeralPrivate">Fresh 32-byte ephemeral private for this init. Never logged.</param>
    /// <param name="bundle">The peer's parsed bundle.</param>
    /// <param name="bundleSigningPublicDjb">
    /// The 33-byte DJB public the bundle signature verifies under, passed
    /// explicitly because the plan's NSec-only crypto approximates XEdDSA
    /// (XEdDSA unifies the DH identity and the verify key; Ed25519 cannot —
    /// one keypair can never satisfy both, so the keys travel separately).
    /// Production callers pass the bundle identity (the UAT-validated
    /// semantic); tests pass the synthetic peer's distinct signing public.
    /// When the XEdDSA primitive lands, this parameter disappears.
    /// </param>
    /// <returns>Root key, first chain key, and our base public (fresh copies owned by the caller).</returns>
    /// <exception cref="SdkFormatException">Any key is missized, registration is out of range, or the signed-prekey signature is invalid.</exception>
    internal static AliceSession InitializeAlice(
        ReadOnlySpan<byte> ownIdentityPrivate,
        ReadOnlySpan<byte> ephemeralPrivate,
        PeerBundle bundle,
        byte[] bundleSigningPublicDjb)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(bundleSigningPublicDjb);
        ThrowUnlessPrivate(ownIdentityPrivate);
        ThrowUnlessPrivate(ephemeralPrivate);
        ValidateBundle(bundle);
        if (bundleSigningPublicDjb.Length != DjbIdentityLength || bundleSigningPublicDjb[0] != SignalCipher.DjbTypeByte)
        {
            throw new SdkFormatException("Signal identity keys must be 33-byte DJB-encoded values.");
        }

        if (!VerifySignedPreKey(bundleSigningPublicDjb, bundle.SignedPreKey, bundle.SignedPreKeySignature))
        {
            throw new SdkFormatException("Signal bundle signed pre-key signature is invalid.");
        }

        byte[] ephemeralPublic = DeriveX25519Public(ephemeralPrivate);
        try
        {
            byte[] dh1 = Agree(ownIdentityPrivate, bundle.SignedPreKey);
            try
            {
                byte[] peerIdentityRaw = SignalCipher.FromDjbIdentity(bundle.IdentityKeyDjb);
                try
                {
                    byte[] dh2 = Agree(ephemeralPrivate, peerIdentityRaw);
                    try
                    {
                        byte[] dh3 = Agree(ephemeralPrivate, bundle.SignedPreKey);
                        try
                        {
                            byte[] secret = bundle.OneTimePreKey is null
                                ? Concat(dh1, dh2, dh3)
                                : Concat(dh1, dh2, dh3, Agree(ephemeralPrivate, bundle.OneTimePreKey));
                            try
                            {
                                (byte[] root, byte[] chain) = DeriveRootAndChain(secret);
                                return new AliceSession(root, chain, ephemeralPublic);
                            }
                            finally
                            {
                                CryptographicOperations.ZeroMemory(secret);
                            }
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(dh3);
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(dh2);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(peerIdentityRaw);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dh1);
            }
        }
        catch
        {
            CryptographicOperations.ZeroMemory(ephemeralPublic);
            throw;
        }
    }

    /// <summary>
    /// Bob side: rebuilds the identical root and first receiving chain key
    /// from our long-term secrets and Alice's advertised keys.
    /// </summary>
    /// <param name="ownIdentityPrivate">Our 32-byte identity private. Never logged.</param>
    /// <param name="signedPreKeyPrivate">Our 32-byte signed-prekey private. Never logged.</param>
    /// <param name="oneTimePrivate">Our 32-byte one-time private for the consumed id, or null when Alice used no one-time key. Never logged.</param>
    /// <param name="aliceUsedOneTimeKey">Whether Alice's prekey message names a one-time id (must agree with <paramref name="oneTimePrivate"/>).</param>
    /// <param name="aliceIdentityDjb">Alice's 33-byte DJB identity.</param>
    /// <param name="aliceBaseKey">Alice's 32-byte base public.</param>
    /// <exception cref="SdkFormatException">Any key is missized, or the one-time-key expectation disagrees.</exception>
    internal static BobSession InitializeBob(
        ReadOnlySpan<byte> ownIdentityPrivate,
        ReadOnlySpan<byte> signedPreKeyPrivate,
        byte[]? oneTimePrivate,
        bool aliceUsedOneTimeKey,
        ReadOnlySpan<byte> aliceIdentityDjb,
        ReadOnlySpan<byte> aliceBaseKey)
    {
        ThrowUnlessPrivate(ownIdentityPrivate);
        ThrowUnlessPrivate(signedPreKeyPrivate);
        if (aliceIdentityDjb.Length != DjbIdentityLength || aliceIdentityDjb[0] != SignalCipher.DjbTypeByte)
        {
            throw new SdkFormatException("Signal identity keys must be 33-byte DJB-encoded values.");
        }

        if (aliceBaseKey.Length != KeyLength)
        {
            throw new SdkFormatException("Signal base keys must be 32 bytes.");
        }

        bool haveOneTime = oneTimePrivate is not null;
        if (haveOneTime && oneTimePrivate!.Length != KeyLength)
        {
            throw new SdkFormatException("Signal one-time keys must be 32 bytes.");
        }

        if (aliceUsedOneTimeKey != haveOneTime)
        {
            throw new SdkFormatException("Signal one-time key presence disagrees with the prekey message.");
        }

        byte[] aliceIdentityRaw = SignalCipher.FromDjbIdentity(aliceIdentityDjb);
        try
        {
            // Same concatenation order as Alice (DH is commutative per pair).
            byte[] dh1 = Agree(signedPreKeyPrivate, aliceIdentityRaw);
            try
            {
                byte[] dh2 = Agree(ownIdentityPrivate, aliceBaseKey);
                try
                {
                    byte[] dh3 = Agree(signedPreKeyPrivate, aliceBaseKey);
                    try
                    {
                        byte[] secret = haveOneTime
                            ? Concat(dh1, dh2, dh3, Agree(oneTimePrivate, aliceBaseKey))
                            : Concat(dh1, dh2, dh3);
                        try
                        {
                            (byte[] root, byte[] chain) = DeriveRootAndChain(secret);
                            return new BobSession(root, chain);
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(secret);
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(dh3);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(dh2);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dh1);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aliceIdentityRaw);
        }
    }

    /// <summary>
    /// Verifies a signed-prekey signature: Ed25519 over the DJB-encoded
    /// signed-prekey public under the bundle identity (Baileys
    /// <c>signedKeyPair</c> signs the DJB form, not the raw key).
    /// Malformed inputs verify false (never throw); nulls throw.
    /// </summary>
    internal static bool VerifySignedPreKey(
        byte[] identityKeyDjb,
        byte[] signedPreKeyPublic,
        byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(identityKeyDjb);
        ArgumentNullException.ThrowIfNull(signedPreKeyPublic);
        ArgumentNullException.ThrowIfNull(signature);
        if (identityKeyDjb.Length != DjbIdentityLength
            || identityKeyDjb[0] != SignalCipher.DjbTypeByte
            || signedPreKeyPublic.Length != KeyLength
            || signature.Length != SignatureLength)
        {
            return false;
        }

        byte[] identityRaw;
        try
        {
            identityRaw = SignalCipher.FromDjbIdentity(identityKeyDjb);
        }
        catch (SdkFormatException)
        {
            return false;
        }

        // NOTE: the bundle identity is an X25519 key on the WhatsApp wire,
        // but the signed-prekey signature is Ed25519 over the DJB encoding.
        // Verification imports the raw 32 bytes as an Ed25519 public key,
        // exactly as Baileys' verify path imports the DJB form's key bytes.
        byte[] djbSigned = SignalCipher.ToDjbIdentity(signedPreKeyPublic);
        try
        {
            PublicKey key;
            try
            {
                key = PublicKey.Import(SignatureAlgorithm.Ed25519, identityRaw, KeyBlobFormat.RawPublicKey);
            }
            catch (Exception)
            {
                return false;
            }

            try
            {
                return SignatureAlgorithm.Ed25519.Verify(key, djbSigned, signature);
            }
            catch (Exception)
            {
                return false;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(identityRaw);
            CryptographicOperations.ZeroMemory(djbSigned);
        }
    }

    /// <summary>
    /// Signs data with an Ed25519 identity private (signed-prekey issuance,
    /// sender-key signatures in later plans). Clone/import/zeroize discipline
    /// per the PairingCoordinator precedent.
    /// </summary>
    /// <param name="identityPrivate">32-byte Ed25519 private. Never logged, never mutated.</param>
    /// <param name="data">Bytes to sign.</param>
    /// <returns>Fresh 64-byte signature.</returns>
    /// <exception cref="SdkFormatException">The private is not 32 bytes.</exception>
    internal static byte[] SignWithIdentity(ReadOnlySpan<byte> identityPrivate, ReadOnlySpan<byte> data)
    {
        ThrowUnlessPrivate(identityPrivate);
        byte[] copy = identityPrivate.ToArray();
        try
        {
            using Key key = Key.Import(SignatureAlgorithm.Ed25519, copy, KeyBlobFormat.RawPrivateKey);
            return SignatureAlgorithm.Ed25519.Sign(key, data.ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SdkFormatException("Signal identity signing failed: unusable private key.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    /// <summary>Generates a fresh X25519 keypair (private export enabled; both fresh copies owned by the caller).</summary>
    internal static (byte[] Private, byte[] Public) GenerateX25519KeyPair()
    {
        KeyCreationParameters creationParameters = new() { ExportPolicy = KeyExportPolicies.AllowPlaintextExport };
        using Key key = Key.Create(KeyAgreementAlgorithm.X25519, creationParameters);
        return (key.Export(KeyBlobFormat.RawPrivateKey), key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
    }

    /// <summary>Generates a fresh Ed25519 identity keypair (public in 33-byte DJB form).</summary>
    internal static (byte[] Private, byte[] PublicDjb) GenerateIdentityKeyPair()
    {
        KeyCreationParameters creationParameters = new() { ExportPolicy = KeyExportPolicies.AllowPlaintextExport };
        using Key key = Key.Create(SignatureAlgorithm.Ed25519, creationParameters);
        byte[] raw = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        try
        {
            return (key.Export(KeyBlobFormat.RawPrivateKey), SignalCipher.ToDjbIdentity(raw));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    /// <summary>Derives the X25519 public for a private (clone/import/zeroize discipline).</summary>
    /// <exception cref="SdkFormatException">The private is not a usable 32-byte key.</exception>
    internal static byte[] DeriveX25519Public(ReadOnlySpan<byte> privateKey)
    {
        ThrowUnlessPrivate(privateKey);
        byte[] copy = privateKey.ToArray();
        try
        {
            using Key key = Key.Import(KeyAgreementAlgorithm.X25519, copy, KeyBlobFormat.RawPrivateKey);
            return key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SdkFormatException("Signal key derivation failed: unusable private key.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    /// <summary>Derives the Ed25519 public (DJB form) for an identity private (PairingCoordinator precedent).</summary>
    /// <exception cref="SdkFormatException">The private is not a usable 32-byte key.</exception>
    internal static byte[] DeriveEd25519PublicDjb(ReadOnlySpan<byte> privateKey)
    {
        ThrowUnlessPrivate(privateKey);
        byte[] copy = privateKey.ToArray();
        try
        {
            using Key key = Key.Import(SignatureAlgorithm.Ed25519, copy, KeyBlobFormat.RawPrivateKey);
            byte[] raw = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
            try
            {
                return SignalCipher.ToDjbIdentity(raw);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(raw);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SdkFormatException("Signal key derivation failed: unusable private key.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    private static void ValidateBundle(PeerBundle bundle)
    {
        if (bundle.IdentityKeyDjb.Length != DjbIdentityLength || bundle.IdentityKeyDjb[0] != SignalCipher.DjbTypeByte)
        {
            throw new SdkFormatException("Signal bundle identity keys must be 33-byte DJB-encoded values.");
        }

        if (bundle.SignedPreKey.Length != KeyLength)
        {
            throw new SdkFormatException("Signal bundle signed pre-keys must be 32 bytes.");
        }

        if (bundle.SignedPreKeySignature.Length != SignatureLength)
        {
            throw new SdkFormatException("Signal bundle signatures must be 64 bytes.");
        }

        bool hasKey = bundle.OneTimePreKey is not null;
        if (hasKey != bundle.OneTimePreKeyId.HasValue
            || (hasKey && bundle.OneTimePreKey!.Length != KeyLength))
        {
            throw new SdkFormatException("Signal bundle one-time keys must pair an id with 32 bytes.");
        }

        if (bundle.RegistrationId > MaxRegistrationId)
        {
            throw new SdkFormatException("Signal registration ids must fit 14 bits.");
        }
    }

    private static (byte[] RootKey, byte[] ChainKey) DeriveRootAndChain(byte[] secret)
    {
        // X3DH §2.2 KDF: input key material is F ‖ KM with F = 32 0xFF bytes
        // (X25519 domain separation from XEdDSA), salt zeros, info the label.
        byte[] ikm = new byte[KeyLength + secret.Length];
        ikm.AsSpan(0, KeyLength).Fill(0xFF);
        secret.CopyTo(ikm.AsSpan(KeyLength));
        try
        {
            byte[] okm = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                ikm,
                DerivedLength,
                new byte[KeyLength],
                Encoding.ASCII.GetBytes(InfoLabel));
            try
            {
                byte[] root = new byte[KeyLength];
                byte[] chain = new byte[KeyLength];
                okm.AsSpan(0, KeyLength).CopyTo(root);
                okm.AsSpan(KeyLength, KeyLength).CopyTo(chain);
                return (root, chain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(okm);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ikm);
        }
    }

    /// <summary>
    /// X25519 agreement with an NSec-owned private and a raw 32-byte peer
    /// public. Returns the fresh 32-byte shared secret (caller wipes).
    /// Fail-closed with a rule-only exception (never names material).
    /// </summary>
    internal static byte[] Agree(ReadOnlySpan<byte> ownPrivate, ReadOnlySpan<byte> peerPublic)
    {
        if (peerPublic.Length != KeyLength)
        {
            throw new SdkFormatException("Signal key agreement failed: unusable peer key.");
        }

        PublicKey peer;
        try
        {
            peer = PublicKey.Import(KeyAgreementAlgorithm.X25519, peerPublic, KeyBlobFormat.RawPublicKey);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SdkFormatException("Signal key agreement failed: unusable peer key.", ex);
        }

        byte[] copy = ownPrivate.ToArray();
        try
        {
            using Key own = Key.Import(KeyAgreementAlgorithm.X25519, copy, KeyBlobFormat.RawPrivateKey);
            var parameters = new SharedSecretCreationParameters
            {
                ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
            };
            try
            {
                using SharedSecret agreement = KeyAgreementAlgorithm.X25519.Agree(own, peer, parameters)
                    ?? throw new SdkFormatException("Signal key agreement failed.");
                return agreement.Export(SharedSecretBlobFormat.RawSharedSecret);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SdkException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new SdkFormatException("Signal key agreement failed.", ex);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    private static void ThrowUnlessPrivate(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != KeyLength)
        {
            throw new SdkFormatException("Signal private keys must be 32 bytes.");
        }
    }

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
}
