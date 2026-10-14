using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Messaging;
using Iwasug.WhatsApp.Sdk.Noise;
using Iwasug.WhatsApp.Sdk.Pairing;
using Iwasug.WhatsApp.Sdk.Signal;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.InMemory;
using Iwasug.WhatsApp.Sdk.Tests.Transport;
using Xunit;
using static Iwasug.WhatsApp.Sdk.Tests.Messaging.MessagingLoopbackTests;

namespace Iwasug.WhatsApp.Sdk.Tests.Signal;

/// <summary>
/// The MSG-02 sender-key tracer (04-05 Task 1): one scripted group send fans SKDMs
/// pairwise to exactly the member device set (own devices and the phone excluded),
/// appends ONE signed <c>skmsg</c> ciphertext with the deterministic fixture-pinned
/// <c>phash</c>, and a member-side decrypt yields the exact plaintext with a verified
/// Ed25519 signature; a repeat send with a live key issues NO new SKDM fanout; a
/// tampered signature fails typed with no ack and no event; the bounded iteration
/// window handles out-of-order delivery and rejects over-gaps and replays typed.
/// </summary>
/// <remarks>
/// Wire layouts come from the MIT-licensed Baileys <c>src/Signal/Group/*</c> behavioral
/// facts (NOTICE rows); every byte here is independently constructed — no upstream
/// code was copied. All keys are loopback-only fixture material; assertions never
/// print raw secrets.
/// </remarks>
public sealed class SenderKeyTests
{
    private static readonly Jid Group = Jid.Parse("120363012345678901@g.us");

    /// <summary>Fixture-pinned phash values (the "2:" + unpadded base64 of the first 6 SHA-256 bytes over the ordinal-sorted AD-string concat).</summary>
    private const string PhashDevices5253 = "2:gQ0lr3BX";

    private const string PhashDevices5254 = "2:pUAVIafN";

    private const string PhashDevice52 = "2:2m1jHzgl";

    // -----------------------------------------------------------------------
    // Unit facts: SKDM roundtrip, receive-only states, serialization.
    // -----------------------------------------------------------------------

    [Fact]
    public void skdm_distribution_roundtrip_and_receive_only_state_never_sends()
    {
        SenderKey sender = SenderKey.Create();
        uint senderKeyId = sender.ActiveKeyId;

        byte[] wire = sender.BuildDistribution();
        wire[0].Should().Be(SignalCipher.VersionByte, "the SKDM wire is version-prefixed (0x33)");
        SenderKey.TryParseDistribution(wire, out SenderKeyDistribution? distribution).Should().BeTrue();
        distribution!.SenderKeyId.Should().Be(senderKeyId, "the distribution names the sender-key generation");
        distribution.Iteration.Should().Be(0, "the distribution is built BEFORE the send's encryption advances the chain");
        distribution.ChainKey.Should().HaveCount(SenderKey.ChainKeyLength);
        distribution.SigningKeyPublic.Should().HaveCount(SenderKey.SigningKeyLength, "the SKDM carries the RAW Ed25519 public key, never DJB form");

        // A member without a stored key stores it from the SKDM and can decrypt.
        SenderKey receiver = SenderKey.Empty();
        receiver.StoreDistribution(distribution);
        byte[] skmsg = sender.Encrypt(MessageBuilder.BuildPlainText("hello group"));
        SenderKeyDecryptOutcome outcome = receiver.Decrypt(skmsg);
        SenderKeyDecryptOutcome.Decrypted decrypted = outcome.Should().BeOfType<SenderKeyDecryptOutcome.Decrypted>().Which;
        Message.Parser.ParseFrom(decrypted.Plaintext).Conversation.Should().Be("hello group");

        // A receive-only state carries no signing private: it can never send.
        FluentActions.Invoking(() => receiver.Encrypt(MessageBuilder.BuildPlainText("nope")))
            .Should().Throw<SdkFormatException>();

        // The record survives the SDK-owned serialization roundtrip.
        byte[] record = sender.Serialize();
        SenderKey restored = SenderKey.Deserialize(record);
        restored.ActiveKeyId.Should().Be(senderKeyId);
        byte[] afterRestart = restored.Encrypt(MessageBuilder.BuildPlainText("after restart"));
        PlaintextOf(receiver.Decrypt(afterRestart)).Should().Be("after restart", "the deserialized chain continues exactly where the original left off");
    }

    [Fact]
    public void skdm_distribution_malformed_wire_fails_closed()
    {
        SenderKey sender = SenderKey.Create();
        byte[] wire = sender.BuildDistribution();

        byte[] badVersion = (byte[])wire.Clone();
        badVersion[0] = 0x22;
        SenderKey.TryParseDistribution(badVersion, out _).Should().BeFalse("a wrong version byte fails closed");

        SenderKey.TryParseDistribution([], out _).Should().BeFalse("empty input fails closed");
        SenderKey.TryParseDistribution([0x33], out _).Should().BeFalse("a version byte with no body fails closed");

        byte[] garbage = new byte[64];
        RandomNumberGenerator.Fill(garbage);
        garbage[0] = SignalCipher.VersionByte;
        SenderKey.TryParseDistribution(garbage, out _).Should().BeFalse("unparsable protobuf fails closed");
    }

    // -----------------------------------------------------------------------
    // Unit facts: bounded iteration handling.
    // -----------------------------------------------------------------------

    [Fact]
    public void iteration_out_of_order_decrypt_within_bound_and_failures_are_typed()
    {
        SenderKey sender = SenderKey.Create();
        SenderKey receiver = SenderKey.Empty();
        SenderKey.TryParseDistribution(sender.BuildDistribution(), out SenderKeyDistribution? distribution).Should().BeTrue();
        receiver.StoreDistribution(distribution!);

        byte[] m1 = sender.Encrypt(MessageBuilder.BuildPlainText("one"));
        byte[] m2 = sender.Encrypt(MessageBuilder.BuildPlainText("two"));
        byte[] m3 = sender.Encrypt(MessageBuilder.BuildPlainText("three"));

        // Out-of-order within the bound: 3 first (skips forward, caching seeds 0/1), then 1, then 2.
        PlaintextOf(receiver.Decrypt(m3)).Should().Be("three");
        PlaintextOf(receiver.Decrypt(m1)).Should().Be("one", "the skipped seed was cached");
        PlaintextOf(receiver.Decrypt(m2)).Should().Be("two");

        // Replay of a consumed iteration: typed OldCounter, chain untouched.
        receiver.Decrypt(m3).Should().BeOfType<SenderKeyDecryptOutcome.Failed>()
            .Which.Reason.Should().Be(SenderKeyDecryptFailure.OldCounter, "a replayed iteration never decrypts twice");

        // A gap beyond the bound fails typed.
        SenderKey farSender = SenderKey.Create();
        SenderKey farReceiver = SenderKey.Empty();
        SenderKey.TryParseDistribution(farSender.BuildDistribution(), out SenderKeyDistribution? farDistribution).Should().BeTrue();
        farReceiver.StoreDistribution(farDistribution!);
        byte[] last = [];
        for (int i = 0; i <= SenderKey.MaxFutureGap + 1; i++)
        {
            last = farSender.Encrypt(MessageBuilder.BuildPlainText("x"));
        }

        farReceiver.Decrypt(last).Should().BeOfType<SenderKeyDecryptOutcome.Failed>()
            .Which.Reason.Should().Be(SenderKeyDecryptFailure.GapTooLarge, "a gap beyond MaxFutureGap fails closed");
    }

    [Fact]
    public void tamper_signature_and_body_fail_typed_with_no_plaintext()
    {
        SenderKey sender = SenderKey.Create();
        SenderKey receiver = SenderKey.Empty();
        SenderKey.TryParseDistribution(sender.BuildDistribution(), out SenderKeyDistribution? distribution).Should().BeTrue();
        receiver.StoreDistribution(distribution!);

        byte[] skmsg = sender.Encrypt(MessageBuilder.BuildPlainText("secret"));

        byte[] flippedSignature = (byte[])skmsg.Clone();
        flippedSignature[^1] ^= 0x01;
        var badSignature = receiver.Decrypt(flippedSignature).Should().BeOfType<SenderKeyDecryptOutcome.Failed>().Which;
        badSignature.Reason.Should().Be(SenderKeyDecryptFailure.BadSignature, "the signature verifies BEFORE any seed derivation (T-4-15)");

        byte[] flippedBody = (byte[])skmsg.Clone();
        flippedBody[skmsg.Length - SignalCipher.SignatureLength - 1] ^= 0x01; // last ciphertext byte: proto stays parseable
        receiver.Decrypt(flippedBody).Should().BeOfType<SenderKeyDecryptOutcome.Failed>()
            .Which.Reason.Should().Be(SenderKeyDecryptFailure.BadSignature, "a tampered body invalidates the signature too");

        receiver.Decrypt(RandomNumberGenerator.GetBytes(96)).Should().BeOfType<SenderKeyDecryptOutcome.Failed>()
            .Which.Reason.Should().BeOneOf(
                SenderKeyDecryptFailure.Truncated,
                SenderKeyDecryptFailure.MalformedMessage,
                SenderKeyDecryptFailure.BadVersion);

        // The untampered original still decrypts: the failures above never mutated the chain.
        PlaintextOf(receiver.Decrypt(skmsg)).Should().Be("secret");
    }

    [Fact]
    public void phash_fixture_pinned_deterministic_over_sorted_ad_string_set()
    {
        Jid device52 = Jid.Parse(DeviceJid(PeerUser, 52));
        Jid device53 = Jid.Parse(DeviceJid(PeerUser, 53));
        Jid device54 = Jid.Parse(DeviceJid(PeerUser, 54));

        // Independent recomputation (SHA-256 over the ordinal-sorted AD-string concat, first 6 bytes, "2:" + unpadded base64).
        DeviceDirectory.ComputePhash([device52, device53]).Should().Be(PhashDevices5253, "the algorithm is fixture-pinned");
        DeviceDirectory.ComputePhash([device52, device54]).Should().Be(PhashDevices5254, "a different member set hashes differently");
        DeviceDirectory.ComputePhash([device52]).Should().Be(PhashDevice52);

        // Order independence: the algorithm sorts before hashing.
        DeviceDirectory.ComputePhash([device53, device52]).Should().Be(PhashDevices5253, "set order never changes the phash");

        // Cross-check against a from-scratch recomputation in this test.
        string[] forms = [device52.ToString(), device53.ToString()];
        Array.Sort(forms, StringComparer.Ordinal);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(forms)));
        string expected = "2:" + Convert.ToBase64String(hash, 0, 6).TrimEnd('=');
        DeviceDirectory.ComputePhash([device52, device53]).Should().Be(expected);
    }

    // -----------------------------------------------------------------------
    // Loopback facts: full group send on the scripted peer.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task skdm_distribution_fans_out_pairwise_one_skmsg_and_reuses_live_key_on_second_send()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        AccountId account = NewAccount("group-send");
        OwnKeys own = CreateOwnKeys(780);
        var peers = new ConcurrentQueue<LoopbackFrameTransport>();

        await using var factory = new InMemoryStoreFactory();
        ISignalSessionStore sessions = await factory.CreateAsync(account, token);
        IIdentityStore identities = await ((IIdentityStoreFactory)factory).CreateAsync(account, token);
        IPreKeyStore preKeys = await ((IPreKeyStoreFactory)factory).CreateAsync(account, token);
        IRecentMessageCacheStore cache = await ((IRecentMessageCacheStoreFactory)factory).CreateAsync(account, token);
        ISafetyStateStore safety = await ((ISafetyStateStoreFactory)factory).CreateAsync(account, token);
        ICredentialStore credentials = await ((ICredentialStoreFactory)factory).CreateAsync(account, token);
        HandshakePayloadProvider payloads = await CreateSeededPayloadsAsync(account, credentials, own, token);
        await SeedOwnPrekeysAsync(preKeys, count: 6, firstId: 300, token);

        PeerDevice deviceA = CreatePeerDevice(DeviceJid(PeerUser, 52), 22, 1101, 1234);
        PeerDevice deviceB = CreatePeerDevice(DeviceJid(PeerUser, 53), 23, 1102, 1235);
        DoubleRatchet bobA = await SeedSessionPairAsync(sessions, own, deviceA, token);
        DoubleRatchet bobB = await SeedSessionPairAsync(sessions, own, deviceB, token);

        var fixedStores = new FixedStores
        {
            Sessions = sessions, Identity = identities, PreKeys = preKeys,
            RecentMessages = cache, Safety = safety, Credentials = credentials,
        };
        var registryOptions = new StoreRegistryOptions
        {
            SignalSessionFactory = fixedStores, IdentityFactory = fixedStores, PreKeyFactory = fixedStores,
            RecentMessageCacheFactory = fixedStores, SafetyStateFactory = fixedStores, CredentialFactory = fixedStores,
        };
        await using var connection = new WaConnection(account, CreateClient(peers), payloads, TracerOptions());
        connection.StoreRegistry = new StoreRegistry(registryOptions);
        connection.SafetyPolicy = NoSafetyDelays();

        ScriptedPeer? peer = null;
        var peerTask = Task.Run(async () =>
        {
            peer = await CompletePeerHandshakeAsync(peers, token);
            peer!.BobRatchets[deviceA.Jid] = bobA;
            peer!.BobRatchets[deviceB.Jid] = bobB;
            peer!.GroupParticipants = [$"{PeerUser}@{PeerServer}"];
            peer!.UsyncDevices = [
                deviceA.Jid,
                deviceB.Jid,
                DeviceJid(OwnUser, OwnDevice),
                DeviceJid(PeerUser, 0)];
            await peer!.RunAsync(testCts.Token);
        }, token);

        await connection.ConnectAsync(token);
        Messenger messenger = connection.Messaging;

        SendOutcome outcome = await messenger.SendGroupMessageAsync(Group, "hello group", token);
        outcome.Status.Should().Be(SendOutcomeStatus.Sent);

        await WaitForAsync(peer!.Log, entry => entry.StartsWith("message:", StringComparison.Ordinal), "the peer received the group message node");
        peer.Log.Should().Contain(entry => entry == "group-info", "the members resolved through the w:g2 participants query");
        peer.MessageNodes.TryDequeue(out BinaryNode? node).Should().BeTrue();

        node!.Attrs["to"].Should().Be(Group.ToString());
        node.Attrs["type"].Should().Be("text");
        node.Attrs["phash"].Should().Be(PhashDevices5253, "phash is deterministic over the own-excluded member device set");

        // SKDM fanout: exactly one to-wrapped ciphertext per member device, own/phone excluded.
        BinaryNode participants = (node.Children ?? []).Single(c => c.Tag == "participants");
        var skdmByDevice = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (BinaryNode to in (participants.Children ?? []).Where(c => c.Tag == "to"))
        {
            BinaryNode enc = (to.Children ?? [])[0];
            enc.Attrs["type"].Should().Be("pkmsg", "fresh per-device sessions deliver SKDMs as prekey messages");
            skdmByDevice[to.Attrs["jid"]] = enc.Data!;
        }

        skdmByDevice.Keys.Should().Equal(new[] { deviceA.Jid, deviceB.Jid }, "own device and phone are excluded from the fanout (T-4-11)");

        // ONE skmsg ciphertext for the whole group, v=2.
        BinaryNode skmsgNode = (node.Children ?? []).Single(c => c.Tag == "enc");
        skmsgNode.Attrs["type"].Should().Be(MessageBuilder.EncTypeSenderKey);
        skmsgNode.Attrs["v"].Should().Be(MessageBuilder.EncVersion);

        // Member side: decrypt both SKDMs through the per-device sessions, store the key, decrypt the skmsg.
        SenderKey member = SenderKey.Empty();
        foreach ((string deviceJid, byte[] skdmWire) in skdmByDevice)
        {
            DoubleRatchet bob = peer.BobRatchets[deviceJid];
            var plain = bob.Decrypt(skdmWire, isPreKey: true, token)
                .Should().BeOfType<SignalDecryptOutcome.Decrypted>().Which;
            var wrapper = Message.Parser.ParseFrom(plain.Plaintext);
            wrapper.SenderKeyDistributionMessage.Should().NotBeNull("SKDMs ride inside a 1:1 message wrapper");
            wrapper.SenderKeyDistributionMessage.GroupId.Should().Be(Group.ToString());
            SenderKey.TryParseDistribution(
                wrapper.SenderKeyDistributionMessage.AxolotlSenderKeyDistributionMessage.ToByteArray(),
                out SenderKeyDistribution? distribution).Should().BeTrue();
            member.StoreDistribution(distribution!);
        }

        var first = member.Decrypt(skmsgNode.Data!)
            .Should().BeOfType<SenderKeyDecryptOutcome.Decrypted>().Which;
        Message.Parser.ParseFrom(first.Plaintext).Conversation.Should().Be("hello group", "the member decrypts the exact plaintext under a verified signature");

        // Reuse fact: a second send with the live key issues NO new SKDM fanout.
        SendOutcome second = await messenger.SendGroupMessageAsync(Group, "hello again", token);
        second.Status.Should().Be(SendOutcomeStatus.Sent);
        await WaitForAsync(peer.Log, entry => entry == "message:" + second.MessageId, "the second group message node arrived");
        peer.MessageNodes.TryDequeue(out BinaryNode? secondNode).Should().BeTrue();
        (secondNode!.Children ?? []).Should().NotContain(c => c.Tag == "participants", "a live sender key re-distributes nothing");
        BinaryNode secondSkmsg = (secondNode.Children ?? []).Single(c => c.Tag == "enc");
        secondSkmsg.Attrs["type"].Should().Be(MessageBuilder.EncTypeSenderKey);

        // Iteration fact: the chain advanced and the member decrypts the second message too.
        var secondPlain = member.Decrypt(secondSkmsg.Data!)
            .Should().BeOfType<SenderKeyDecryptOutcome.Decrypted>().Which;
        Message.Parser.ParseFrom(secondPlain.Plaintext).Conversation.Should().Be("hello again");

        await connection.DisposeAsync();
        await peerTask;
    }

    [Fact]
    public async Task group_send_not_a_member_and_empty_participants_fail_typed_with_zero_frames()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        AccountId account = NewAccount("group-empty");
        OwnKeys own = CreateOwnKeys(781);
        var peers = new ConcurrentQueue<LoopbackFrameTransport>();

        await using var factory = new InMemoryStoreFactory();
        ISignalSessionStore sessions = await factory.CreateAsync(account, token);
        IIdentityStore identities = await ((IIdentityStoreFactory)factory).CreateAsync(account, token);
        IPreKeyStore preKeys = await ((IPreKeyStoreFactory)factory).CreateAsync(account, token);
        IRecentMessageCacheStore cache = await ((IRecentMessageCacheStoreFactory)factory).CreateAsync(account, token);
        ISafetyStateStore safety = await ((ISafetyStateStoreFactory)factory).CreateAsync(account, token);
        ICredentialStore credentials = await ((ICredentialStoreFactory)factory).CreateAsync(account, token);
        HandshakePayloadProvider payloads = await CreateSeededPayloadsAsync(account, credentials, own, token);
        await SeedOwnPrekeysAsync(preKeys, count: 6, firstId: 400, token);

        var fixedStores = new FixedStores
        {
            Sessions = sessions, Identity = identities, PreKeys = preKeys,
            RecentMessages = cache, Safety = safety, Credentials = credentials,
        };
        var registryOptions = new StoreRegistryOptions
        {
            SignalSessionFactory = fixedStores, IdentityFactory = fixedStores, PreKeyFactory = fixedStores,
            RecentMessageCacheFactory = fixedStores, SafetyStateFactory = fixedStores, CredentialFactory = fixedStores,
        };
        await using var connection = new WaConnection(account, CreateClient(peers), payloads, TracerOptions());
        connection.StoreRegistry = new StoreRegistry(registryOptions);
        connection.SafetyPolicy = NoSafetyDelays();

        ScriptedPeer? peer = null;
        var peerTask = Task.Run(async () =>
        {
            peer = await CompletePeerHandshakeAsync(peers, token);
            peer!.GroupParticipants = null; // not-a-member reply: no group node
            await peer!.RunAsync(testCts.Token);
        }, token);

        await connection.ConnectAsync(token);
        Messenger messenger = connection.Messaging;

        // T-4-16: not-a-member fails typed BEFORE any node is built.
        await FluentActions.Awaiting(() => messenger.SendGroupMessageAsync(Group, "nope", token))
            .Should().ThrowAsync<SdkFormatException>();

        // Empty member list also fails typed with zero frames.
        peer!.GroupParticipants = [];
        await FluentActions.Awaiting(() => messenger.SendGroupMessageAsync(Group, "still nope", token))
            .Should().ThrowAsync<SdkFormatException>();

        await Task.Delay(300, token);
        peer.MessageNodes.Should().BeEmpty("neither failure ever put a message node on the wire");

        await connection.DisposeAsync();
        await peerTask;
    }

    // -----------------------------------------------------------------------
    // Loopback facts: inbound skmsg receive path + tamper.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task tamper_inbound_skmsg_signature_no_ack_no_event_and_valid_still_flows()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        AccountId account = NewAccount("group-recv");
        OwnKeys own = CreateOwnKeys(782);
        var peers = new ConcurrentQueue<LoopbackFrameTransport>();

        await using var factory = new InMemoryStoreFactory();
        ISignalSessionStore sessions = await factory.CreateAsync(account, token);
        IIdentityStore identities = await ((IIdentityStoreFactory)factory).CreateAsync(account, token);
        IPreKeyStore preKeys = await ((IPreKeyStoreFactory)factory).CreateAsync(account, token);
        IRecentMessageCacheStore cache = await ((IRecentMessageCacheStoreFactory)factory).CreateAsync(account, token);
        ISafetyStateStore safety = await ((ISafetyStateStoreFactory)factory).CreateAsync(account, token);
        ICredentialStore credentials = await ((ICredentialStoreFactory)factory).CreateAsync(account, token);
        HandshakePayloadProvider payloads = await CreateSeededPayloadsAsync(account, credentials, own, token);
        await SeedOwnPrekeysAsync(preKeys, count: 6, firstId: 500, token);

        // SDK-side Bob at the peer's device address; the peer holds Alice (canonical receive pair).
        PeerDevice peerDevice = CreatePeerDevice(DeviceJid(PeerUser, 52), 22, 1101, 1234);
        DoubleRatchet peerAlice = await SeedReceivePairAsync(sessions, own, peerDevice, token);

        var fixedStores = new FixedStores
        {
            Sessions = sessions, Identity = identities, PreKeys = preKeys,
            RecentMessages = cache, Safety = safety, Credentials = credentials,
        };
        var registryOptions = new StoreRegistryOptions
        {
            SignalSessionFactory = fixedStores, IdentityFactory = fixedStores, PreKeyFactory = fixedStores,
            RecentMessageCacheFactory = fixedStores, SafetyStateFactory = fixedStores, CredentialFactory = fixedStores,
        };
        await using var connection = new WaConnection(account, CreateClient(peers), payloads, TracerOptions());
        connection.StoreRegistry = new StoreRegistry(registryOptions);
        connection.SafetyPolicy = NoSafetyDelays();

        ScriptedPeer? peer = null;
        var peerTask = Task.Run(async () =>
        {
            peer = await CompletePeerHandshakeAsync(peers, token);
            await peer!.RunAsync(testCts.Token);
        }, token);

        await connection.ConnectAsync(token);
        Messenger messenger = connection.Messaging;
        var events = new ConcurrentQueue<MessageReceivedEventArgs>();
        messenger.MessageReceived += (_, e) => events.Enqueue(e);

        // The peer distributes its sender key 1:1 (SKDM wrapper): stored, acked, NO event, NO cache entry.
        SenderKey peerSenderKey = SenderKey.Create();
        byte[] skdmPlaintext = MessageBuilder.BuildSenderKeyDistribution(
            Group.ToString(), peerSenderKey.BuildDistribution());
        var skdmOutcome = peerAlice.Encrypt(skdmPlaintext, token);
        byte[] skdmWire = skdmOutcome is SignalEncryptOutcome.PreKeyMessageSent pk
            ? pk.WireBytes
            : ((SignalEncryptOutcome.MessageSent)skdmOutcome).WireBytes;
        string skdmType = skdmOutcome is SignalEncryptOutcome.PreKeyMessageSent ? "pkmsg" : "msg";
        await peer!.SendStanzaAsync(BuildInboundMessageNode("skdm-1", peerDevice.Jid, skdmWire, skdmType));
        await WaitForAsync(peer.Log, entry => entry == "ack:skdm-1", "the SKDM message is acked");
        events.Should().BeEmpty("a distribution is not a message: it never raises a message event");
        (await cache.GetAsync("skdm-1", token)).Should().BeNull("and it never lands in the recent cache");

        // Tamper fact: a flipped signature byte fails typed — no ack claims success, no event fires.
        byte[] tampered = peerSenderKey.Encrypt(MessageBuilder.BuildPlainText("tampered"));
        tampered[^1] ^= 0x20;
        await peer.SendStanzaAsync(BuildInboundGroupNode("grp-bad-1", Group, peerDevice.Jid, tampered));
        await Task.Delay(700, token);
        peer.Log.Should().NotContain(entry => entry == "ack:grp-bad-1", "no ack claims a failed signature");
        events.Should().BeEmpty("a tampered signature never raises a message event (T-4-15)");
        peer.ReceiptNodes.Should().Contain(
            n => n.Attrs.GetValueOrDefault("type") == "retry" && n.Attrs.GetValueOrDefault("id") == "grp-bad-1",
            "the typed failure answers with the Pattern-5 retry receipt");

        // An unknown-key skmsg (no stored sender key for that group/sender) also fails typed with no ack.
        await peer.SendStanzaAsync(BuildInboundGroupNode("grp-unknown-1", Group, DeviceJid(PeerUser, 53), peerSenderKey.Encrypt(MessageBuilder.BuildPlainText("lost key"))));
        await Task.Delay(500, token);
        peer.Log.Should().NotContain(entry => entry == "ack:grp-unknown-1", "a missing sender key never acks");
        events.Should().BeEmpty();

        // The pump continues: a valid skmsg decrypts, persists before its ack, and raises the group event.
        byte[] valid = peerSenderKey.Encrypt(MessageBuilder.BuildPlainText("group hello"));
        await peer.SendStanzaAsync(BuildInboundGroupNode("grp-1", Group, peerDevice.Jid, valid));
        await WaitForAsync(peer.Log, entry => entry == "ack:grp-1", "the SDK acked the valid group message");
        await WaitForCountAsync(events, 1, "the group message event");
        MessageReceivedEventArgs received = events.Single();
        received.Key.RemoteJid.ToString().Should().Be(Group.ToString(), "the chat is the group JID");
        received.Key.Participant.Should().Be(peerDevice.Jid, "the participant names the sending member device");
        received.Sender.ToString().Should().Be(peerDevice.Jid);
        received.IsFromMe.Should().BeFalse();
        received.Text.Should().Be("group hello");

        RecentMessageEntry? cachedGroup = await cache.GetAsync("grp-1", token);
        cachedGroup.Should().NotBeNull("the group message lands in the recent cache (D-20)");
        cachedGroup!.ChatJid.Should().Be(Group.ToString());
        cachedGroup.Sender.Should().Be(peerDevice.Jid);

        await connection.DisposeAsync();
        await peerTask;
    }

    private static string PlaintextOf(SenderKeyDecryptOutcome outcome)
    {
        var decrypted = outcome.Should().BeOfType<SenderKeyDecryptOutcome.Decrypted>().Which;
        return Message.Parser.ParseFrom(decrypted.Plaintext).Conversation;
    }

    internal static BinaryNode BuildInboundGroupNode(string messageId, Jid group, string participantJid, byte[] wire)
    {
        return BinaryNode.FromChildren(
            "message",
            [BinaryNode.FromBytes(
                "enc",
                wire,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["v"] = "2",
                    ["type"] = MessageBuilder.EncTypeSenderKey,
                })],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["id"] = messageId,
                ["from"] = group.ToString(),
                ["participant"] = participantJid,
                ["type"] = "text",
                ["t"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
    }
}
