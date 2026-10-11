using System.Text;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Signal;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.InMemory;
using Iwasug.WhatsApp.Sdk.Stores.Runtime;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Signal;

/// <summary>
/// Prekey-management proofs (04-01 Task 3): own material is generated signed
/// and upload-ready with Pattern-3 byte widths, persists before the upload
/// IQ is sent, peer bundle replies parse into typed values, and a session is
/// fetched only when genuinely absent (never silently re-trusting).
/// </summary>
/// <remarks>
/// Router composition follows the QueryRouterTests harness precedent: the
/// manager rides a real <see cref="QueryRouter"/> over a capturing fake
/// sender, and scripted replies are delivered through
/// <see cref="QueryRouter.TryDeliver"/> (the single source of truth).
/// Output discipline (V6/T-4-01): assertions compare hex of public wire data
/// and fingerprints of secrets - never raw key bytes.
/// </remarks>
public sealed class PreKeyManagerTests
{
    private sealed record SyntheticPeer(
        byte[] XPrivate,
        byte[] XDjb,
        byte[] EdPrivate,
        byte[] EdDjb,
        byte[] SpkPrivate,
        byte[] SpkPublic,
        byte[] SpkSignature,
        uint SpkId,
        byte[] OtkPrivate,
        byte[] OtkPublic,
        uint OtkId,
        uint RegistrationId);

    private static SyntheticPeer CreateSyntheticPeer(uint spkId, uint otkId, uint registrationId)
    {
        (byte[] xPriv, byte[] xPub) = X3dh.GenerateX25519KeyPair();
        (byte[] edPriv, byte[] edPubDjb) = X3dh.GenerateIdentityKeyPair();
        (byte[] spkPriv, byte[] spkPub) = X3dh.GenerateX25519KeyPair();
        byte[] spkSig = X3dh.SignWithIdentity(edPriv, SignalCipher.ToDjbIdentity(spkPub));
        (byte[] otPriv, byte[] otPub) = X3dh.GenerateX25519KeyPair();
        return new SyntheticPeer(
            xPriv, SignalCipher.ToDjbIdentity(xPub),
            edPriv, edPubDjb,
            spkPriv, spkPub, spkSig, spkId,
            otPriv, otPub, otkId,
            registrationId);
    }

    private sealed class FakeRouter
    {
        internal readonly List<(string Id, byte[] Payload)> Sent = new();
        internal readonly QueryRouter Router;

        internal FakeRouter()
        {
            Router = new QueryRouter(
                SendAsync,
                new ConnectionOptions { QueryTimeoutMs = 5000 },
                new FakeTimeProvider());
        }

        private ValueTask SendAsync(string id, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            lock (Sent)
            {
                Sent.Add((id, payload.ToArray()));
            }

            return ValueTask.CompletedTask;
        }

        internal void ReplyLast(byte[] reply)
        {
            string id;
            lock (Sent)
            {
                Sent.Count.Should().BeGreaterThan(0);
                id = Sent[Sent.Count - 1].Id;
            }

            Router.TryDeliver(id, reply).Should().BeTrue();
        }

        internal async Task<T> RoundTripAsync<T>(Func<Task<T>> action, byte[] reply)
        {
            Task<T> pending = action();
            SpinWait.SpinUntil(() =>
            {
                lock (Sent)
                {
                    return Sent.Count > 0;
                }
            }, TimeSpan.FromSeconds(5)).Should().BeTrue("the manager issues exactly one query per operation");
            ReplyLast(reply);
            return await pending;
        }
    }

    private static BinaryNode BuildReplyNode(
        Jid peer, uint registrationId, byte[] identityRaw, byte[] spkRaw, byte[] spkSig, uint spkId,
        byte[]? otkRaw, uint? otkId, int errorCode = 0)
    {
        var userChildren = new List<BinaryNode>();
        if (errorCode != 0)
        {
            userChildren.Add(BinaryNode.FromText(
                "error", string.Empty,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["code"] = errorCode.ToString() }));
        }
        else
        {
            userChildren.Add(BinaryNode.FromBytes("registration", ToBigEndian(registrationId, 4)));
            userChildren.Add(BinaryNode.FromBytes("identity", identityRaw));
            if (otkRaw is not null && otkId.HasValue)
            {
                userChildren.Add(BinaryNode.FromChildren(
                    "key",
                    [BinaryNode.FromBytes("id", ToBigEndian(otkId.Value, 3)),
                     BinaryNode.FromBytes("value", otkRaw)]));
            }

            userChildren.Add(BinaryNode.FromChildren(
                "skey",
                [BinaryNode.FromBytes("id", ToBigEndian(spkId, 3)),
                 BinaryNode.FromBytes("value", spkRaw),
                 BinaryNode.FromBytes("signature", spkSig)]));
        }

        return BinaryNode.FromChildren(
            "iq",
            [BinaryNode.FromChildren(
                "list",
                [BinaryNode.FromChildren(
                    "user",
                    userChildren,
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["jid"] = peer.ToString() })])]);
    }

    private static byte[] ToBigEndian(uint value, int width)
    {
        byte[] bytes = new byte[width];
        for (int i = 0; i < width; i++)
        {
            bytes[width - 1 - i] = (byte)(value & 0xFF);
            value >>= 8;
        }

        return bytes;
    }

    private static Jid PeerJid() => Jid.Parse("bob.phone.3@s.whatsapp.net");

    [Fact]
    public void signed_prekey_generation_signs_djb_form_and_verifies()
    {
        var router = new FakeRouter();
        var manager = new PreKeyManager(router.Router, new DurableWriteRetryPolicy());
        (byte[] edPriv, byte[] edDjb) = X3dh.GenerateIdentityKeyPair();

        OwnPreKeyMaterial material = manager.GenerateOwnMaterial(edPriv, 22, 1101, 3);

        material.SignedPreKeyId.Should().Be(22);
        material.SignedPreKeyPublic.Should().HaveCount(32);
        material.SignedPreKeyPrivate.Should().HaveCount(32);
        material.SignedPreKeySignature.Should().HaveCount(64);
        material.OneTimeKeys.Should().HaveCount(3);
        material.OneTimeKeys.Select(k => k.Id).Should().Equal(1101, 1102, 1103);
        foreach (OneTimePreKey key in material.OneTimeKeys)
        {
            key.Public.Should().HaveCount(32);
            key.Private.Should().HaveCount(32);
        }

        X3dh.VerifySignedPreKey(edDjb, material.SignedPreKeyPublic, material.SignedPreKeySignature)
            .Should().BeTrue("the signature covers the DJB-encoded signed-prekey public");

        byte[] flipped = (byte[])material.SignedPreKeySignature.Clone();
        flipped[0] ^= 0xFF;
        X3dh.VerifySignedPreKey(edDjb, material.SignedPreKeyPublic, flipped).Should().BeFalse();

        Action emptyBatch = () => manager.GenerateOwnMaterial(edPriv, 22, 1101, 0);
        emptyBatch.Should().Throw<SdkFormatException>("empty batches fail closed");

        Action idOverflow = () => manager.GenerateOwnMaterial(edPriv, 22, 0xFFFFFF, 2);
        idOverflow.Should().Throw<SdkFormatException>("ids must fit the 3-byte wire width");
    }

    [Fact]
    public async Task upload_persists_before_send_and_builds_pattern3_attrs()
    {
        PreKeyManager.UploadBatchSize.Should().Be(50);
        PreKeyManager.InitialUploadTarget.Should().Be(812);
        PreKeyManager.ReuploadThreshold.Should().Be(5);

        var router = new FakeRouter();
        var manager = new PreKeyManager(router.Router, new DurableWriteRetryPolicy());
        (byte[] edPriv, _) = X3dh.GenerateIdentityKeyPair();
        (byte[] xPriv, byte[] xPub) = X3dh.GenerateX25519KeyPair();
        OwnPreKeyMaterial material = manager.GenerateOwnMaterial(edPriv, 22, 1101, 3);

        var log = new List<string>();
        var preKeys = Substitute.For<IPreKeyStore>();
        preKeys.PutPreKeyAsync(Arg.Any<int>(), Arg.Any<PreKeyRecordEntry>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                lock (log)
                {
                    log.Add($"put-prekey:{call.Arg<int>()}");
                }

                return Task.CompletedTask;
            });
        preKeys.PutBundleAsync(Arg.Any<string>(), Arg.Any<PreKeyBundle>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                lock (log)
                {
                    log.Add($"put-bundle:{call.Arg<string>()}");
                }

                return Task.CompletedTask;
            });
        var identities = Substitute.For<IIdentityStore>();
        identities.PutAsync(Arg.Any<string>(), Arg.Any<SignalIdentityRecord>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                lock (log)
                {
                    log.Add($"put-identity:{call.Arg<string>()}");
                }

                return Task.CompletedTask;
            });

        Task pending = manager.UploadOwnKeysAsync(
            1234, xPub, material, "self.phone.0@s.whatsapp.net", preKeys, identities, CancellationToken.None);
        SpinWait.SpinUntil(() =>
        {
            lock (router.Sent)
            {
                return router.Sent.Count > 0;
            }
        }, TimeSpan.FromSeconds(5)).Should().BeTrue();
        lock (log)
        {
            log.Add("send");
        }

        router.ReplyLast(BinaryNodeCodec.Encode(BinaryNode.Create("iq")));
        await pending;

        List<string> snapshot;
        lock (log)
        {
            snapshot = [.. log];
        }

        snapshot.Should().Contain("put-prekey:1101");
        snapshot.Should().Contain("put-prekey:1103");
        snapshot.Should().Contain("put-bundle:self.phone.0@s.whatsapp.net");
        snapshot.Should().Contain("put-identity:self.phone.0@s.whatsapp.net");
        snapshot.IndexOf("send").Should().BeGreaterThan(snapshot.IndexOf("put-bundle:self.phone.0@s.whatsapp.net"),
            "every durable write completes before the upload IQ is sent");

        byte[] payload;
        lock (router.Sent)
        {
            payload = router.Sent[0].Payload;
        }

        BinaryNode node = BinaryNodeCodec.Decode(payload);
        node.Tag.Should().Be("iq");
        node.Attrs.Should().ContainKey("xmlns").WhoseValue.Should().Be("encrypt");
        node.Attrs.Should().ContainKey("type").WhoseValue.Should().Be("set");
        node.Attrs.Should().ContainKey("to").WhoseValue.Should().Be("s.whatsapp.net");

        BinaryNode Child(string tag) =>
            node.Children!.First(c => string.Equals(c.Tag, tag, StringComparison.Ordinal));
        Child("registration").Data.Should().Equal(ToBigEndian(1234, 4), "registration rides 4-byte big-endian");
        Child("type").Data.Should().Equal([0x05]);
        Child("identity").Data.Should().Equal(xPub);
        BinaryNode list = Child("list");
        list.Children.Should().HaveCount(3);
        foreach (BinaryNode key in list.Children!)
        {
            key.Children!.First(c => c.Tag == "id").Data.Should().HaveCount(3, "prekey ids ride 3-byte big-endian");
            key.Children!.First(c => c.Tag == "value").Data.Should().HaveCount(32);
        }

        BinaryNode skey = Child("skey");
        skey.Children!.First(c => c.Tag == "id").Data.Should().HaveCount(3);
        skey.Children!.First(c => c.Tag == "value").Data.Should().HaveCount(32);
        skey.Children!.First(c => c.Tag == "signature").Data.Should().HaveCount(64);
    }

    [Fact]
    public async Task parse_peer_bundle_response_into_typed_bundle()
    {
        var router = new FakeRouter();
        var manager = new PreKeyManager(router.Router, new DurableWriteRetryPolicy());
        SyntheticPeer peer = CreateSyntheticPeer(22, 1101, 1234);
        Jid jid = PeerJid();
        byte[] identityRaw = SignalCipher.FromDjbIdentity(peer.XDjb);

        BinaryNode reply = BuildReplyNode(
            jid, peer.RegistrationId, identityRaw, peer.SpkPublic, peer.SpkSignature, peer.SpkId,
            peer.OtkPublic, peer.OtkId);

        PeerBundleFetchOutcome outcome = await router.RoundTripAsync(
            () => manager.FetchBundleAsync(jid, CancellationToken.None),
            BinaryNodeCodec.Encode(reply));

        var bundle = outcome.Should().BeOfType<PeerBundleFetchOutcome.Fetched>().Subject.Bundle;
        Convert.ToHexString(bundle.IdentityKeyDjb).Should().Be(Convert.ToHexString(peer.XDjb));
        Convert.ToHexString(bundle.SignedPreKey).Should().Be(Convert.ToHexString(peer.SpkPublic));
        Convert.ToHexString(bundle.SignedPreKeySignature).Should().Be(Convert.ToHexString(peer.SpkSignature));
        bundle.SignedPreKeyId.Should().Be(22);
        bundle.OneTimePreKeyId.Should().Be(1101);
        Convert.ToHexString(bundle.OneTimePreKey!).Should().Be(Convert.ToHexString(peer.OtkPublic));
        bundle.RegistrationId.Should().Be(1234);
    }

    [Fact]
    public async Task parse_bundle_without_one_time_key_maps_absent()
    {
        var router = new FakeRouter();
        var manager = new PreKeyManager(router.Router, new DurableWriteRetryPolicy());
        SyntheticPeer peer = CreateSyntheticPeer(22, 1101, 1234);
        Jid jid = PeerJid();

        BinaryNode reply = BuildReplyNode(
            jid, peer.RegistrationId, SignalCipher.FromDjbIdentity(peer.XDjb),
            peer.SpkPublic, peer.SpkSignature, peer.SpkId, null, null);

        PeerBundleFetchOutcome outcome = await router.RoundTripAsync(
            () => manager.FetchBundleAsync(jid, CancellationToken.None),
            BinaryNodeCodec.Encode(reply));

        var bundle = outcome.Should().BeOfType<PeerBundleFetchOutcome.Fetched>().Subject.Bundle;
        bundle.OneTimePreKey.Should().BeNull();
        bundle.OneTimePreKeyId.Should().BeNull();
    }

    [Fact]
    public async Task error_node_maps_to_typed_failure()
    {
        var router = new FakeRouter();
        var manager = new PreKeyManager(router.Router, new DurableWriteRetryPolicy());
        Jid jid = PeerJid();

        PeerBundleFetchOutcome notFound = await router.RoundTripAsync(
            () => manager.FetchBundleAsync(jid, CancellationToken.None),
            BinaryNodeCodec.Encode(BuildReplyNode(jid, 0, [], [], [], 0, null, null, errorCode: 404)));
        notFound.Should().BeOfType<PeerBundleFetchOutcome.Failed>()
            .Which.Reason.Should().Be(PeerBundleFailure.PeerNotFound);

        var router2 = new FakeRouter();
        var manager2 = new PreKeyManager(router2.Router, new DurableWriteRetryPolicy());
        PeerBundleFetchOutcome serverError = await router2.RoundTripAsync(
            () => manager2.FetchBundleAsync(jid, CancellationToken.None),
            BinaryNodeCodec.Encode(BuildReplyNode(jid, 0, [], [], [], 0, null, null, errorCode: 500)));
        serverError.Should().BeOfType<PeerBundleFetchOutcome.Failed>()
            .Which.Reason.Should().Be(PeerBundleFailure.ServerError);
    }

    [Fact]
    public async Task no_fetch_when_session_exists()
    {
        await using var factory = new InMemoryStoreFactory();
        AccountId account = AccountId.Create("no-fetch");
        ISignalSessionStore sessions = await factory.CreateAsync(account, CancellationToken.None);
        IIdentityStore identities = await ((IIdentityStoreFactory)factory).CreateAsync(account, CancellationToken.None);
        IPreKeyStore preKeys = await ((IPreKeyStoreFactory)factory).CreateAsync(account, CancellationToken.None);
        var repository = new DoubleRatchet.SignalSessionRepository(sessions, new DurableWriteRetryPolicy());
        var router = new FakeRouter();
        var manager = new PreKeyManager(router.Router, new DurableWriteRetryPolicy());

        (DoubleRatchet alice, DoubleRatchet bob, PeerBundle bundle) = CreatePair(1001, 1234);
        string address = DoubleRatchet.SignalSessionRepository.AddressFor(Jid.Parse("bob.phone.3@s.whatsapp.net"));
        _ = alice;
        _ = bundle;
        await repository.SaveAsync(address, bob, CancellationToken.None);

        (byte[] ownPriv, _) = X3dh.GenerateX25519KeyPair();
        EnsureSessionOutcome outcome = await manager.EnsureSessionAsync(
            address, PeerJid(), ownPriv, 1001, bundle.IdentityKeyDjb,
            repository, identities, preKeys, CancellationToken.None);

        outcome.Should().BeOfType<EnsureSessionOutcome.Existing>();
        lock (router.Sent)
        {
            router.Sent.Should().BeEmpty("a live session issues zero queries");
        }
    }

    [Fact]
    public async Task ensure_session_from_fetched_bundle_is_persistable()
    {
        await using var factory = new InMemoryStoreFactory();
        AccountId account = AccountId.Create("ensure-wiring");
        ISignalSessionStore sessions = await factory.CreateAsync(account, CancellationToken.None);
        IIdentityStore identities = await ((IIdentityStoreFactory)factory).CreateAsync(account, CancellationToken.None);
        IPreKeyStore preKeys = await ((IPreKeyStoreFactory)factory).CreateAsync(account, CancellationToken.None);
        var repository = new DoubleRatchet.SignalSessionRepository(sessions, new DurableWriteRetryPolicy());
        var router = new FakeRouter();
        var manager = new PreKeyManager(router.Router, new DurableWriteRetryPolicy());

        SyntheticPeer bobKeys = CreateSyntheticPeer(22, 1101, 1234);
        (byte[] alicePriv, byte[] alicePub) = X3dh.GenerateX25519KeyPair();
        byte[] aliceDjb = SignalCipher.ToDjbIdentity(alicePub);
        Jid peerJid = PeerJid();
        string address = DoubleRatchet.SignalSessionRepository.AddressFor(peerJid);
        byte[] identityRaw = SignalCipher.FromDjbIdentity(bobKeys.XDjb);

        BinaryNode reply = BuildReplyNode(
            peerJid, bobKeys.RegistrationId, identityRaw,
            bobKeys.SpkPublic, bobKeys.SpkSignature, bobKeys.SpkId,
            bobKeys.OtkPublic, bobKeys.OtkId);

        Task<EnsureSessionOutcome> pending = manager.EnsureSessionAsync(
            address, peerJid, alicePriv, 1001, bobKeys.EdDjb,
            repository, identities, preKeys, CancellationToken.None);
        SpinWait.SpinUntil(() =>
        {
            lock (router.Sent)
            {
                return router.Sent.Count > 0;
            }
        }, TimeSpan.FromSeconds(5)).Should().BeTrue();
        router.ReplyLast(BinaryNodeCodec.Encode(reply));
        EnsureSessionOutcome outcome = await pending;

        var created = outcome.Should().BeOfType<EnsureSessionOutcome.Created>().Subject;
        (await repository.LoadAsync(address, CancellationToken.None)).Should().NotBeNull();

        SignalIdentityRecord? pinned = await identities.GetAsync(address, CancellationToken.None);
        pinned.Should().NotBeNull();
        pinned!.FirstUsePinned.Should().BeTrue();
        Convert.ToHexString(pinned.IdentityKeyDjb!).Should().Be(Convert.ToHexString(bobKeys.XDjb));

        (await preKeys.GetBundleAsync(address, CancellationToken.None)).Should().NotBeNull();

        // The fetched bundle yields a working session: Bob (built from the
        // same synthetic privates) decrypts Alice's first message (Task 1+3 wiring).
        BobSession bobSession = X3dh.InitializeBob(
            bobKeys.XPrivate, bobKeys.SpkPrivate, bobKeys.OtkPrivate, true,
            aliceDjb, created.Session.AliceBaseKey);
        var bob = DoubleRatchet.InitializeBob(
            bobSession, bobKeys.SpkPrivate, bobKeys.XDjb, aliceDjb, created.Session.AliceBaseKey,
            bobKeys.RegistrationId, 1001);

        SignalEncryptOutcome first = created.Session.Encrypt(
            Encoding.UTF8.GetBytes("wired session"), CancellationToken.None);
        byte[] preKeyWire = first.Should().BeOfType<SignalEncryptOutcome.PreKeyMessageSent>().Subject.WireBytes;
        SignalDecryptOutcome decrypted = bob.Decrypt(preKeyWire, isPreKey: true, CancellationToken.None);
        decrypted.Should().BeOfType<SignalDecryptOutcome.Decrypted>()
            .Which.Plaintext.Should().Equal(Encoding.UTF8.GetBytes("wired session"));
    }

    [Fact]
    public async Task identity_change_does_not_silently_retrust()
    {
        await using var factory = new InMemoryStoreFactory();
        AccountId account = AccountId.Create("identity-change");
        ISignalSessionStore sessions = await factory.CreateAsync(account, CancellationToken.None);
        IIdentityStore identities = await ((IIdentityStoreFactory)factory).CreateAsync(account, CancellationToken.None);
        IPreKeyStore preKeys = await ((IPreKeyStoreFactory)factory).CreateAsync(account, CancellationToken.None);
        var repository = new DoubleRatchet.SignalSessionRepository(sessions, new DurableWriteRetryPolicy());
        var router = new FakeRouter();
        var manager = new PreKeyManager(router.Router, new DurableWriteRetryPolicy());

        Jid peerJid = PeerJid();
        string address = DoubleRatchet.SignalSessionRepository.AddressFor(peerJid);
        (byte[] otherPriv, byte[] otherPub) = X3dh.GenerateX25519KeyPair();
        await identities.PutAsync(
            address,
            new SignalIdentityRecord(null, SignalCipher.ToDjbIdentity(otherPub), true),
            CancellationToken.None);

        SyntheticPeer bobKeys = CreateSyntheticPeer(22, 1101, 1234);
        byte[] identityRaw = SignalCipher.FromDjbIdentity(bobKeys.XDjb);
        BinaryNode reply = BuildReplyNode(
            peerJid, bobKeys.RegistrationId, identityRaw,
            bobKeys.SpkPublic, bobKeys.SpkSignature, bobKeys.SpkId,
            bobKeys.OtkPublic, bobKeys.OtkId);

        (byte[] ownPriv, _) = X3dh.GenerateX25519KeyPair();
        Task<EnsureSessionOutcome> pending = manager.EnsureSessionAsync(
            address, peerJid, ownPriv, 1001, bobKeys.EdDjb,
            repository, identities, preKeys, CancellationToken.None);
        SpinWait.SpinUntil(() =>
        {
            lock (router.Sent)
            {
                return router.Sent.Count > 0;
            }
        }, TimeSpan.FromSeconds(5)).Should().BeTrue();
        router.ReplyLast(BinaryNodeCodec.Encode(reply));
        EnsureSessionOutcome outcome = await pending;

        outcome.Should().BeOfType<EnsureSessionOutcome.IdentityChanged>();
        (await repository.LoadAsync(address, CancellationToken.None)).Should().BeNull("no session is built on a changed identity");
        (await preKeys.GetBundleAsync(address, CancellationToken.None)).Should().BeNull();
    }

    private static (DoubleRatchet Alice, DoubleRatchet Bob, PeerBundle Bundle) CreatePair(
        uint aliceReg, uint bobReg)
    {
        SyntheticPeer aliceKeys = CreateSyntheticPeer(1, 101, aliceReg);
        SyntheticPeer bobKeys = CreateSyntheticPeer(22, 1101, bobReg);
        var bundle = new PeerBundle(
            bobKeys.XDjb,
            bobKeys.SpkPublic,
            bobKeys.SpkSignature,
            bobKeys.SpkId,
            bobKeys.OtkPublic,
            bobKeys.OtkId,
            bobKeys.RegistrationId);

        (byte[] ephPriv, _) = X3dh.GenerateX25519KeyPair();
        AliceSession aliceSession = X3dh.InitializeAlice(aliceKeys.XPrivate, ephPriv, bundle, bobKeys.EdDjb);
        BobSession bobSession = X3dh.InitializeBob(
            bobKeys.XPrivate, bobKeys.SpkPrivate, bobKeys.OtkPrivate, true,
            aliceKeys.XDjb, aliceSession.BasePublic);
        (byte[] sendPriv, byte[] sendPub) = X3dh.GenerateX25519KeyPair();

        var alice = DoubleRatchet.InitializeAlice(
            aliceSession, sendPriv, sendPub, bundle, aliceKeys.XDjb, bobKeys.XDjb, aliceReg);
        var bob = DoubleRatchet.InitializeBob(
            bobSession, bobKeys.SpkPrivate, bobKeys.XDjb, aliceKeys.XDjb, aliceSession.BasePublic, bobReg, aliceReg);
        return (alice, bob, bundle);
    }
}
