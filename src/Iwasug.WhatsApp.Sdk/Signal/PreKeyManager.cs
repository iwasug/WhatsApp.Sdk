using System.Security.Cryptography;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.Runtime;

namespace Iwasug.WhatsApp.Sdk.Signal;

/// <summary>One generated one-time prekey (id plus raw key pair).</summary>
/// <param name="Id">24-bit prekey id (3-byte big-endian on the wire).</param>
/// <param name="Public">One-time public key (32 bytes raw). Never logged.</param>
/// <param name="Private">One-time private key (32 bytes raw). Never logged.</param>
internal sealed record OneTimePreKey(int Id, byte[] Public, byte[] Private);

/// <summary>Own freshly generated prekey material, ready to persist and upload.</summary>
/// <param name="SignedPreKeyId">Signed-prekey id.</param>
/// <param name="SignedPreKeyPublic">Signed-prekey public (32 bytes raw). Never logged.</param>
/// <param name="SignedPreKeyPrivate">Signed-prekey private (32 bytes raw): persisted in the own-upload checkpoint so a restart keeps serving it. Never logged.</param>
/// <param name="SignedPreKeySignature">Ed25519 signature over the DJB-encoded signed-prekey public (64 bytes). Never logged.</param>
/// <param name="OneTimeKeys">One-time batch (ids sequential from the requested start).</param>
internal sealed record OwnPreKeyMaterial(
    int SignedPreKeyId,
    byte[] SignedPreKeyPublic,
    byte[] SignedPreKeyPrivate,
    byte[] SignedPreKeySignature,
    IReadOnlyList<OneTimePreKey> OneTimeKeys);

/// <summary>Why a peer bundle fetch failed (typed, never an exception through the pump path).</summary>
internal enum PeerBundleFailure
{
    /// <summary>The peer (or its prekey material) is not on the server.</summary>
    PeerNotFound,

    /// <summary>The server refused or the reply was unusable.</summary>
    ServerError,
}

/// <summary>Typed outcome of a peer bundle fetch.</summary>
internal abstract record PeerBundleFetchOutcome
{
    /// <summary>The bundle parsed byte-exact into typed values.</summary>
    internal sealed record Fetched(PeerBundle Bundle) : PeerBundleFetchOutcome;

    /// <summary>The fetch failed typed (see <see cref="PeerBundleFailure"/>).</summary>
    internal sealed record Failed(PeerBundleFailure Reason) : PeerBundleFetchOutcome;
}

/// <summary>Typed outcome of ensuring a session for a peer address.</summary>
internal abstract record EnsureSessionOutcome
{
    /// <summary>A session was built from a fetched bundle and persisted.</summary>
    internal sealed record Created(DoubleRatchet Session) : EnsureSessionOutcome;

    /// <summary>A live session already existed: no fetch was issued.</summary>
    internal sealed record Existing : EnsureSessionOutcome;

    /// <summary>The peer identity changed since pinning: no session was built and nothing was re-trusted (host decides).</summary>
    internal sealed record IdentityChanged : EnsureSessionOutcome;

    /// <summary>The bundle fetch failed typed (see <see cref="PeerBundleFailure"/>).</summary>
    internal sealed record FetchFailed(PeerBundleFailure Reason) : EnsureSessionOutcome;
}

/// <summary>
/// Own/peer prekey management over <see cref="QueryRouter"/> (04-01, internal):
/// own-key generation plus the <c>encrypt</c>-namespace upload IQ, and peer
/// bundle fetch/parse plus X3DH session creation for genuinely missing
/// sessions.
/// </summary>
/// <remarks>
/// <para>
/// Wire provenance (behavioral reference, independently written C#): the
/// upload IQ shape (<c>registration</c> 4-byte big-endian,
/// <c>KEY_BUNDLE_TYPE</c> single <c>0x05</c> type byte, <c>identity</c> raw
/// 32 bytes, <c>list</c> of <c>key</c> nodes with 3-byte big-endian ids,
/// <c>skey</c> with id/value/signature) and the fetch-response parse
/// (per-user <c>registration</c>/<c>identity</c>/<c>key</c>/<c>skey</c>
/// children with the same widths, <c>identity</c> DJB-encoded for Signal use)
/// were learned read-only from Baileys MIT <c>src/Utils/signal.ts</c> at
/// commit 0af2386; the batch counts below come from 04-RESEARCH Pattern 3
/// (whatsmeow <c>prekeys.go</c> behavioral read: batch 50, initial upload
/// 812, re-upload floor 5). The signed-prekey signature covers the
/// DJB-encoded signed-prekey public (Baileys <c>signedKeyPair</c>).
/// </para>
/// <para>
/// Discipline: every durable write (own one-time keys, own checkpoint,
/// identity pin, peer bundle) completes before the stanza that depends on
/// it is sent or used (D-06); bundle fetches happen only for genuinely
/// absent sessions (04-RESEARCH Security Domain); key material never reaches
/// logs, exceptions, or events (T-4-01 - no logging on this path at all).
/// </para>
/// </remarks>
internal sealed class PreKeyManager
{
    /// <summary>Prekey id wire width: 3-byte big-endian (Baileys <c>encodeBigEndian(id, 3)</c>).</summary>
    internal const int PreKeyIdWidth = 3;

    /// <summary>Registration id wire width: 4-byte big-endian (Baileys <c>encodeBigEndian(id)</c> default).</summary>
    internal const int RegistrationIdWidth = 4;

    /// <summary>Key-bundle type byte (Baileys <c>KEY_BUNDLE_TYPE</c>).</summary>
    internal const byte KeyBundleType = 0x05;

    /// <summary>Maximum registration id (14-bit, CredentialRecord precedent).</summary>
    internal const uint MaxRegistrationId = 16383;

    /// <summary>Maximum prekey id (24-bit: the 3-byte wire width).</summary>
    internal const int MaxPreKeyId = 0xFFFFFF;

    /// <summary>One-time keys per upload batch (04-RESEARCH Pattern 3).</summary>
    internal const int UploadBatchSize = 50;

    /// <summary>One-time keys uploaded on first registration (04-RESEARCH Pattern 3).</summary>
    internal const int InitialUploadTarget = 812;

    /// <summary>Server-side floor that triggers a re-upload (04-RESEARCH Pattern 3).</summary>
    internal const int ReuploadThreshold = 5;

    /// <summary>The <c>encrypt</c> namespace server.</summary>
    internal const string EncryptServer = "s.whatsapp.net";

    private readonly QueryRouter _router;
    private readonly DurableWriteRetryPolicy _retry;

    /// <summary>Creates a manager sending <c>encrypt</c>-namespace IQs through <paramref name="router"/>.</summary>
    internal PreKeyManager(QueryRouter router, DurableWriteRetryPolicy retryPolicy)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _retry = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
    }

    /// <summary>
    /// Generates own material: a fresh X25519 signed prekey signed (Ed25519,
    /// over the DJB form) by <paramref name="signingPrivate"/>, plus a batch
    /// of one-time prekeys with sequential ids from
    /// <paramref name="oneTimeStartId"/>.
    /// </summary>
    /// <param name="signingPrivate">Own Ed25519 identity signing private (32 bytes, e.g. CredentialRecord.SigningKeyPrivate). Never logged.</param>
    /// <param name="signedPreKeyId">Signed-prekey id (24-bit).</param>
    /// <param name="oneTimeStartId">First one-time id (24-bit; the batch must fit).</param>
    /// <param name="oneTimeCount">Batch size (at least 1).</param>
    /// <exception cref="SdkFormatException">Any key is missized, an id is out of range, or the batch is empty.</exception>
    internal OwnPreKeyMaterial GenerateOwnMaterial(
        byte[] signingPrivate, int signedPreKeyId, int oneTimeStartId, int oneTimeCount)
    {
        ArgumentNullException.ThrowIfNull(signingPrivate);
        if (signingPrivate.Length != X3dh.KeyLength)
        {
            throw new SdkFormatException("Signal signing keys must be 32 bytes.");
        }

        if (signedPreKeyId < 0 || signedPreKeyId > MaxPreKeyId
            || oneTimeStartId < 0 || oneTimeStartId > MaxPreKeyId
            || oneTimeCount < 1 || (long)oneTimeStartId + oneTimeCount - 1 > MaxPreKeyId)
        {
            throw new SdkFormatException("Signal prekey ids must fit 24 bits with a non-empty batch.");
        }

        (byte[] spkPriv, byte[] spkPub) = X3dh.GenerateX25519KeyPair();
        try
        {
            byte[] spkDjb = SignalCipher.ToDjbIdentity(spkPub);
            byte[] signature;
            try
            {
                signature = X3dh.SignWithIdentity(signingPrivate, spkDjb);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(spkDjb);
            }

            var batch = new List<OneTimePreKey>(oneTimeCount);
            try
            {
                for (int i = 0; i < oneTimeCount; i++)
                {
                    (byte[] priv, byte[] pub) = X3dh.GenerateX25519KeyPair();
                    batch.Add(new OneTimePreKey(oneTimeStartId + i, pub, priv));
                }

                return new OwnPreKeyMaterial(signedPreKeyId, spkPub, spkPriv, signature, batch);
            }
            catch
            {
                foreach (OneTimePreKey key in batch)
                {
                    CryptographicOperations.ZeroMemory(key.Public);
                    CryptographicOperations.ZeroMemory(key.Private);
                }

                throw;
            }
        }
        catch
        {
            CryptographicOperations.ZeroMemory(spkPriv);
            CryptographicOperations.ZeroMemory(spkPub);
            throw;
        }
    }

    /// <summary>
    /// Builds the <c>encrypt set</c> upload IQ for own material (04-RESEARCH
    /// Pattern 3 shape): <c>registration</c> (4-byte big-endian),
    /// <c>type</c> (<c>0x05</c>), <c>identity</c> (raw 32 bytes),
    /// <c>list</c> of one-time <c>key</c> nodes, and the <c>skey</c> node.
    /// </summary>
    /// <exception cref="SdkFormatException">Any key is missized or the registration id is out of range.</exception>
    internal BinaryNode BuildUploadNode(uint registrationId, byte[] identityPublic, OwnPreKeyMaterial material)
    {
        ArgumentNullException.ThrowIfNull(identityPublic);
        ArgumentNullException.ThrowIfNull(material);
        if (registrationId > MaxRegistrationId)
        {
            throw new SdkFormatException("Signal registration ids must fit 14 bits.");
        }

        if (identityPublic.Length != X3dh.KeyLength)
        {
            throw new SdkFormatException("Signal identity keys must be 32 bytes.");
        }

        if (material.SignedPreKeyPublic.Length != X3dh.KeyLength
            || material.SignedPreKeySignature.Length != SignalCipher.SignatureLength)
        {
            throw new SdkFormatException("Signal signed prekey material failed validation.");
        }

        var keys = new List<BinaryNode>(material.OneTimeKeys.Count);
        foreach (OneTimePreKey key in material.OneTimeKeys)
        {
            if (key.Public.Length != X3dh.KeyLength)
            {
                throw new SdkFormatException("Signal one-time keys must be 32 bytes.");
            }

            keys.Add(BinaryNode.FromChildren(
                "key",
                [BinaryNode.FromBytes("id", BigEndian((uint)key.Id, PreKeyIdWidth)),
                 BinaryNode.FromBytes("value", key.Public)]));
        }

        return BinaryNode.FromChildren(
            "iq",
            [BinaryNode.FromBytes("registration", BigEndian(registrationId, RegistrationIdWidth)),
             BinaryNode.FromBytes("type", [KeyBundleType]),
             BinaryNode.FromBytes("identity", identityPublic),
             BinaryNode.FromChildren("list", keys),
             BinaryNode.FromChildren(
                "skey",
                [BinaryNode.FromBytes("id", BigEndian((uint)material.SignedPreKeyId, PreKeyIdWidth)),
                 BinaryNode.FromBytes("value", material.SignedPreKeyPublic),
                 BinaryNode.FromBytes("signature", material.SignedPreKeySignature)])],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["xmlns"] = "encrypt",
                ["type"] = "set",
                ["to"] = EncryptServer,
            });
    }

    /// <summary>
    /// Persists own material (one-time keys, own checkpoint, self identity
    /// row) and then uploads it. Every durable write completes before the
    /// upload IQ is sent (D-06): a restart re-uploads from the checkpoint
    /// instead of minting divergent keys.
    /// </summary>
    internal async Task UploadOwnKeysAsync(
        uint registrationId,
        byte[] identityPublic,
        OwnPreKeyMaterial material,
        string ownAddress,
        IPreKeyStore preKeys,
        IIdentityStore identities,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identityPublic);
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(ownAddress);
        ArgumentNullException.ThrowIfNull(preKeys);
        ArgumentNullException.ThrowIfNull(identities);

        foreach (OneTimePreKey key in material.OneTimeKeys)
        {
            await _retry.ExecuteAsync<bool>(
                async ct =>
                {
                    await preKeys.PutPreKeyAsync(
                        key.Id,
                        new PreKeyRecordEntry(
                            key.Id, (byte[])key.Public.Clone(), (byte[])key.Private.Clone()), ct).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }

        int firstOneTimeId = material.OneTimeKeys.Count == 0 ? 0 : material.OneTimeKeys[0].Id;
        await _retry.ExecuteAsync<bool>(
            async ct =>
            {
                await preKeys.PutBundleAsync(
                    ownAddress,
                    new PreKeyBundle(
                        material.SignedPreKeyId,
                        firstOneTimeId,
                        (byte[])identityPublic.Clone(),
                        (byte[])material.SignedPreKeyPublic.Clone(),
                        (byte[])material.SignedPreKeySignature.Clone(),
                        (byte[])material.SignedPreKeyPrivate.Clone(),
                        null,
                        (int)registrationId), ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);

        byte[] identityDjb = SignalCipher.ToDjbIdentity(identityPublic);
        await _retry.ExecuteAsync<bool>(
            async ct =>
            {
                await identities.PutAsync(
                    ownAddress, new SignalIdentityRecord(null, identityDjb, FirstUsePinned: true), ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);

        BinaryNode node = BuildUploadNode(registrationId, identityPublic, material);
        await _router.SendQueryAsync(BinaryNodeCodec.Encode(node), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the <c>encrypt get</c> bundle-fetch IQ for <paramref name="peer"/>
    /// (04-RESEARCH Pattern 3 shape).
    /// </summary>
    internal BinaryNode BuildFetchNode(Jid peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        return BinaryNode.FromChildren(
            "iq",
            [BinaryNode.FromChildren(
                "key",
                [BinaryNode.Create(
                    "user",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["jid"] = peer.ToString(),
                        ["reason"] = "identity",
                    })])],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["xmlns"] = "encrypt",
                ["type"] = "get",
                ["to"] = EncryptServer,
            });
    }

    /// <summary>
    /// Fetches and parses the peer bundle through the router (exactly one
    /// query; the router owns timeouts and correlation).
    /// </summary>
    internal async Task<PeerBundleFetchOutcome> FetchBundleAsync(Jid peer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peer);
        BinaryNode node = BuildFetchNode(peer);
        byte[] reply = await _router.SendQueryAsync(
            BinaryNodeCodec.Encode(node), cancellationToken).ConfigureAwait(false);
        BinaryNode response = BinaryNodeCodec.Decode(reply);
        return ParseBundleResponse(response, peer);
    }

    /// <summary>
    /// Parses a bundle-fetch response into typed values: per-user
    /// <c>registration</c> (4-byte big-endian), <c>identity</c> (32 bytes),
    /// optional one-time <c>key</c> (3-byte id + 32-byte value), and
    /// <c>skey</c> (id/value/signature). An <c>error</c> child maps to a
    /// typed failure; missing required fields fail closed.
    /// </summary>
    /// <exception cref="SdkFormatException">The response is not a well-formed bundle reply.</exception>
    internal static PeerBundleFetchOutcome ParseBundleResponse(BinaryNode response, Jid peer)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(peer);

        BinaryNode? user = FindUser(response, peer.ToString());
        if (user is null)
        {
            return new PeerBundleFetchOutcome.Failed(PeerBundleFailure.PeerNotFound);
        }

        BinaryNode? error = FirstChild(user, "error");
        if (error is not null)
        {
            error.Attrs.TryGetValue("code", out string? code);
            return new PeerBundleFetchOutcome.Failed(
                string.Equals(code, "404", StringComparison.Ordinal)
                    ? PeerBundleFailure.PeerNotFound
                    : PeerBundleFailure.ServerError);
        }

        byte[]? registration = ChildData(user, "registration");
        byte[]? identity = ChildData(user, "identity");
        BinaryNode? skey = FirstChild(user, "skey");
        if (registration is null || identity is null || skey is null)
        {
            throw new SdkFormatException("Signal bundle replies must carry registration, identity, and a signed key.");
        }

        if (registration.Length != RegistrationIdWidth || identity.Length != X3dh.KeyLength)
        {
            throw new SdkFormatException("Signal bundle replies must carry fixed-width fields.");
        }

        uint registrationId = ReadBigEndian(registration);
        if (registrationId > MaxRegistrationId)
        {
            throw new SdkFormatException("Signal registration ids must fit 14 bits.");
        }

        byte[]? signedId = ChildData(skey, "id");
        byte[]? signedValue = ChildData(skey, "value");
        byte[]? signedSignature = ChildData(skey, "signature");
        if (signedId is null || signedValue is null || signedSignature is null
            || signedId.Length != PreKeyIdWidth
            || signedValue.Length != X3dh.KeyLength
            || signedSignature.Length != SignalCipher.SignatureLength)
        {
            throw new SdkFormatException("Signal bundle replies must carry fixed-width fields.");
        }

        byte[]? oneTimePublic = null;
        uint? oneTimeId = null;
        BinaryNode? oneTime = FirstChild(user, "key");
        if (oneTime is not null)
        {
            byte[]? keyId = ChildData(oneTime, "id");
            byte[]? keyValue = ChildData(oneTime, "value");
            if (keyId is null || keyValue is null
                || keyId.Length != PreKeyIdWidth
                || keyValue.Length != X3dh.KeyLength)
            {
                throw new SdkFormatException("Signal bundle replies must carry fixed-width fields.");
            }

            oneTimeId = ReadBigEndian(keyId);
            oneTimePublic = (byte[])keyValue.Clone();
        }

        return new PeerBundleFetchOutcome.Fetched(new PeerBundle(
            SignalCipher.ToDjbIdentity(identity),
            (byte[])signedValue.Clone(),
            (byte[])signedSignature.Clone(),
            ReadBigEndian(signedId),
            oneTimePublic,
            oneTimeId,
            registrationId));
    }

    /// <summary>
    /// Ensures a session for <paramref name="address"/>: a live session wins
    /// with zero queries; otherwise the peer bundle is fetched, the identity
    /// is pinned on first use (a changed identity reports
    /// <see cref="EnsureSessionOutcome.IdentityChanged"/> without building
    /// anything), and X3DH init yields a persisted session wired to Task 2.
    /// </summary>
    /// <param name="address">Session store key (full per-device AD JID).</param>
    /// <param name="peer">Peer JID to fetch when no session exists.</param>
    /// <param name="ownIdentityPrivate">Our 32-byte X25519 identity private. Never logged.</param>
    /// <param name="ownRegistrationId">Our 14-bit registration id.</param>
    /// <param name="bundleSigningPublicDjb">
    /// The DJB public the bundle signature verifies under. Production passes
    /// the bundle identity (the XEdDSA-seam approximation documented on
    /// <see cref="X3dh.InitializeAlice"/>); tests pass the synthetic
    /// peer's distinct signing public.
    /// </param>
    /// <param name="sessions">Session repository (persisted on creation).</param>
    /// <param name="identities">Identity store (pinned on first use).</param>
    /// <param name="preKeys">Prekey store (peer bundle checkpoint).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal async Task<EnsureSessionOutcome> EnsureSessionAsync(
        string address,
        Jid peer,
        byte[] ownIdentityPrivate,
        uint ownRegistrationId,
        byte[] bundleSigningPublicDjb,
        DoubleRatchet.SignalSessionRepository sessions,
        IIdentityStore identities,
        IPreKeyStore preKeys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(ownIdentityPrivate);
        ArgumentNullException.ThrowIfNull(bundleSigningPublicDjb);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(preKeys);
        if (ownIdentityPrivate.Length != X3dh.KeyLength)
        {
            throw new SdkFormatException("Signal private keys must be 32 bytes.");
        }

        if (ownRegistrationId > MaxRegistrationId)
        {
            throw new SdkFormatException("Signal registration ids must fit 14 bits.");
        }

        DoubleRatchet? existing = await sessions.LoadAsync(address, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return new EnsureSessionOutcome.Existing();
        }

        PeerBundleFetchOutcome fetched = await FetchBundleAsync(peer, cancellationToken).ConfigureAwait(false);
        if (fetched is PeerBundleFetchOutcome.Failed failed)
        {
            return new EnsureSessionOutcome.FetchFailed(failed.Reason);
        }

        PeerBundle bundle = ((PeerBundleFetchOutcome.Fetched)fetched).Bundle;
        SignalIdentityRecord? pinned = await identities.GetAsync(address, cancellationToken).ConfigureAwait(false);
        if (pinned?.IdentityKeyDjb is not null
            && !pinned.IdentityKeyDjb.AsSpan().SequenceEqual(bundle.IdentityKeyDjb))
        {
            return new EnsureSessionOutcome.IdentityChanged();
        }

        if (pinned?.IdentityKeyDjb is null)
        {
            byte[] pin = (byte[])bundle.IdentityKeyDjb.Clone();
            await _retry.ExecuteAsync<bool>(
                async ct =>
                {
                    await identities.PutAsync(address, new SignalIdentityRecord(null, pin, true), ct).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }

        byte[] identityRaw = SignalCipher.FromDjbIdentity(bundle.IdentityKeyDjb);
        await _retry.ExecuteAsync<bool>(
            async ct =>
            {
                await preKeys.PutBundleAsync(
                    address,
                    new PreKeyBundle(
                        (int)bundle.SignedPreKeyId,
                        (int)(bundle.OneTimePreKeyId ?? 0),
                        identityRaw,
                        (byte[])bundle.SignedPreKey.Clone(),
                        (byte[])bundle.SignedPreKeySignature.Clone(),
                        null,
                        bundle.OneTimePreKey is null ? null : (byte[])bundle.OneTimePreKey.Clone(),
                        (int)bundle.RegistrationId), ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);

        (byte[] ephemeralPrivate, _) = X3dh.GenerateX25519KeyPair();
        try
        {
            AliceSession aliceSession = X3dh.InitializeAlice(
                ownIdentityPrivate, ephemeralPrivate, bundle, bundleSigningPublicDjb);
            (byte[] sendPrivate, byte[] sendPublic) = X3dh.GenerateX25519KeyPair();
            DoubleRatchet ratchet = DoubleRatchet.InitializeAlice(
                aliceSession,
                sendPrivate,
                sendPublic,
                bundle,
                SignalCipher.ToDjbIdentity(X3dh.DeriveX25519Public(ownIdentityPrivate)),
                bundle.IdentityKeyDjb,
                ownRegistrationId);
            CryptographicOperations.ZeroMemory(sendPrivate);
            await sessions.SaveAsync(address, ratchet, cancellationToken).ConfigureAwait(false);
            return new EnsureSessionOutcome.Created(ratchet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ephemeralPrivate);
        }
    }

    private static BinaryNode? FirstChild(BinaryNode parent, string tag)
    {
        if (parent.Children is null)
        {
            return null;
        }

        foreach (BinaryNode child in parent.Children)
        {
            if (string.Equals(child.Tag, tag, StringComparison.Ordinal))
            {
                return child;
            }
        }

        return null;
    }

    private static BinaryNode? FindUser(BinaryNode response, string jid)
    {
        BinaryNode? list = FirstChild(response, "list");
        IReadOnlyList<BinaryNode>? candidates = list?.Children ?? response.Children;
        if (candidates is null)
        {
            return null;
        }

        foreach (BinaryNode child in candidates)
        {
            if (string.Equals(child.Tag, "user", StringComparison.Ordinal)
                && child.Attrs.TryGetValue("jid", out string? candidate)
                && string.Equals(candidate, jid, StringComparison.Ordinal))
            {
                return child;
            }
        }

        return null;
    }

    private static byte[]? ChildData(BinaryNode parent, string tag)
    {
        BinaryNode? child = FirstChild(parent, tag);
        return child?.Data;
    }

    private static byte[] BigEndian(uint value, int width)
    {
        if (width != PreKeyIdWidth && width != RegistrationIdWidth)
        {
            throw new SdkFormatException("Signal integer fields have fixed widths.");
        }

        if ((ulong)value >= (1UL << (8 * width)))
        {
            throw new SdkFormatException("Signal integer fields must fit their wire width.");
        }

        byte[] bytes = new byte[width];
        for (int i = 0; i < width; i++)
        {
            bytes[width - 1 - i] = (byte)(value & 0xFF);
            value >>= 8;
        }

        return bytes;
    }

    private static uint ReadBigEndian(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        uint value = 0;
        foreach (byte b in bytes)
        {
            value = (value << 8) | b;
        }

        return value;
    }
}
