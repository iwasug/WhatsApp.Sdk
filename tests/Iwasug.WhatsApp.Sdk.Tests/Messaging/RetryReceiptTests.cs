using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Messaging;
using Iwasug.WhatsApp.Sdk.Signal;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Testing.FaultInjection;
using Xunit;
using static Iwasug.WhatsApp.Sdk.Tests.Messaging.MessageCorrelationTests;
using static Iwasug.WhatsApp.Sdk.Tests.Messaging.MessagingLoopbackTests;

namespace Iwasug.WhatsApp.Sdk.Tests.Messaging;

/// <summary>
/// The MSG-06 retry-exchange proof (04-04 Task 2): a failed decrypt emits the
/// wire-exact Pattern-5 retry receipt (registration child, keys node from retry #2)
/// with the per-(sender, id) count capped at 5 while the typed
/// <see cref="Messenger.DecryptionFailed"/> event never caps, and a peer retry for
/// our message is answered with a same-id resend re-encrypted from the recent cache
/// (fresh session when the retry carries <c>&lt;keys&gt;</c>) — refusing wrong
/// requesters (T-4-12) and degrading to a logged no-op when the cache cannot
/// satisfy the resend (Pitfall 6), never throwing through the receive pump.
/// </summary>
public sealed class RetryReceiptTests
{
    /// <summary>Garbage prekey-message ciphertext: the proven decrypt-failure input (MessagingLoopbackTests precedent).</summary>
    private static byte[] GarbageWire() => RandomNumberGenerator.GetBytes(64);

    private static byte[] BigEndian(uint value, int width)
    {
        var bytes = new byte[width];
        for (int i = width - 1; i >= 0; i--)
        {
            bytes[i] = (byte)(value & 0xFF);
            value >>= 8;
        }

        return bytes;
    }

    /// <summary>Pattern-5 inbound retry receipt as the scripted peer sends it (children bounds-checked like the wire).</summary>
    private static BinaryNode BuildPeerRetryReceipt(
        string fromDeviceJid, string messageId, int count, byte[]? registration, BinaryNode? keys)
    {
        const string t = "1700000000";
        var children = new List<BinaryNode>
        {
            BinaryNode.Create(
                "retry",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["count"] = count.ToString(CultureInfo.InvariantCulture),
                    ["id"] = messageId,
                    ["t"] = t,
                    ["v"] = "1",
                }),
        };
        if (registration is not null)
        {
            children.Add(BinaryNode.FromBytes("registration", registration, null));
        }

        if (keys is not null)
        {
            children.Add(keys);
        }

        return BinaryNode.FromChildren(
            "receipt",
            children,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["id"] = messageId,
                ["type"] = "retry",
                ["from"] = fromDeviceJid,
                ["t"] = t,
            });
    }

    /// <summary>
    /// A self-verifiable <c>&lt;keys&gt;</c> block for the peer device: raw Ed25519
    /// identity (the key the skey signature verifies under — the plan's XEdDSA
    /// approximation means identity and signing key are the same Ed25519 pair here),
    /// a one-time prekey, and the device's signed prekey with its real signature.
    /// </summary>
    private static BinaryNode BuildPeerKeysBlock(PeerDevice device)
    {
        byte[] identityRaw = SignalCipher.FromDjbIdentity(device.EdPublicDjb);
        return BinaryNode.FromChildren(
            "keys",
            [
                BinaryNode.FromBytes("type", [PreKeyManager.KeyBundleType], null),
                BinaryNode.FromBytes("identity", identityRaw, null),
                BinaryNode.FromChildren(
                    "key",
                    [
                        BinaryNode.FromBytes("id", BigEndian(device.OneTimePreKeyId, PreKeyManager.PreKeyIdWidth), null),
                        BinaryNode.FromBytes("value", device.OneTimePublic, null),
                    ],
                    null),
                BinaryNode.FromChildren(
                    "skey",
                    [
                        BinaryNode.FromBytes("id", BigEndian(device.SignedPreKeyId, PreKeyManager.PreKeyIdWidth), null),
                        BinaryNode.FromBytes("value", device.SignedPreKeyPublic, null),
                        BinaryNode.FromBytes("signature", device.SignedPreKeySignature, null),
                    ],
                    null),
                BinaryNode.FromBytes("device-identity", device.XIdentityDjb, null),
            ],
            null);
    }

    private static BinaryNode ExtractEnc(BinaryNode messageNode) =>
        (messageNode.Children ?? [])
            .SelectMany(c => c.Children ?? (IReadOnlyList<BinaryNode>)[])
            .Single(c => c.Tag == "enc");

    private static async Task WaitForMessageCountAsync(ScriptedPeer peer, int count, string what)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);
        while (!timeout.IsCancellationRequested)
        {
            if (peer.MessageNodes.Count >= count)
            {
                return;
            }

            try
            {
                await Task.Delay(10, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        throw new TimeoutException($"Timed out waiting for {count} message nodes: {what}.");
    }

    private static async Task WaitForQueueCountAsync<T>(ConcurrentQueue<T> queue, int count, string what)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);
        while (!timeout.IsCancellationRequested)
        {
            if (queue.Count >= count)
            {
                return;
            }

            try
            {
                await Task.Delay(10, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        throw new TimeoutException($"Timed out waiting for {count} queued items: {what}.");
    }

    private static async Task WaitForReceiptCountAsync(ScriptedPeer peer, int count, string what)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);
        while (!timeout.IsCancellationRequested)
        {
            if (peer.ReceiptNodes.Count >= count)
            {
                return;
            }

            try
            {
                await Task.Delay(10, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        throw new TimeoutException($"Timed out waiting for {count} receipt nodes: {what}.");
    }

    private static async Task WaitForReceiptEventsAsync(ConcurrentQueue<ReceiptReceivedEventArgs> events, int count, string what)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);
        while (!timeout.IsCancellationRequested)
        {
            if (events.Count >= count)
            {
                return;
            }

            try
            {
                await Task.Delay(10, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        throw new TimeoutException($"Timed out waiting for {count} receipt events: {what}.");
    }

    [Fact]
    public async Task emit_receipt_for_failed_decrypt_is_pattern5_exact_with_registration()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("retry-emit", token, seedReceivePair: true);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        var failures = new ConcurrentQueue<DecryptionFailedEventArgs>();
        rig.Messenger.DecryptionFailed += (_, e) => failures.Enqueue(e);

        await peer.SendStanzaAsync(BuildInboundMessageNode("emit-1", rig.DeviceA.Jid, GarbageWire(), "pkmsg"));
        await WaitForAsync(peer.Log, entry => entry == "receipt:retry:emit-1", "the retry receipt flowed out");

        failures.Should().HaveCount(1, "every failure raises exactly one typed event");
        DecryptionFailedEventArgs failure = failures.Single();
        failure.MessageId.Should().Be("emit-1");
        failure.RetryCount.Should().Be(1, "the first failure for this (sender, id) counts one");
        failure.Chat.Should().Be(rig.Chat);
        failure.Sender.ToString().Should().Be(rig.DeviceA.Jid);

        peer.ReceiptNodes.Should().HaveCount(1);
        peer.ReceiptNodes.TryDequeue(out BinaryNode? receipt).Should().BeTrue();
        receipt!.Attrs["type"].Should().Be("retry");
        receipt.Attrs["id"].Should().Be("emit-1");
        receipt.Attrs["to"].Should().Be(rig.DeviceA.Jid, "the retry receipt targets the failing sender device");
        long receiptT = long.Parse(receipt.Attrs["t"], CultureInfo.InvariantCulture);
        receiptT.Should().BeGreaterThanOrEqualTo(1_700_000_000, "the original message t rides the receipt");

        IReadOnlyList<BinaryNode> children = receipt.Children ?? [];
        BinaryNode retryChild = children.Single(c => c.Tag == "retry");
        retryChild.Attrs["count"].Should().Be("1");
        retryChild.Attrs["id"].Should().Be("emit-1");
        retryChild.Attrs["v"].Should().Be("1");
        retryChild.Attrs["t"].Should().Be(receipt.Attrs["t"], "the original t rides both forms verbatim");
        children.Single(c => c.Tag == "registration").Data.Should().Equal(
            new byte[] { 0x00, 0x00, 0x03, 0x09 }, "the 4-byte big-endian own registration id (777)");
        children.Should().NotContain(
            c => c.Tag == "keys", "retry #1 with a live session carries no keys block");
        peer.Log.Should().NotContain("ack:emit-1", "a failed decrypt is never acked");
    }

    [Fact]
    public async Task cap_stops_receipts_after_five_per_id_but_the_typed_event_never_stops()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("retry-cap", token, seedReceivePair: true);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        var failures = new ConcurrentQueue<DecryptionFailedEventArgs>();
        rig.Messenger.DecryptionFailed += (_, e) => failures.Enqueue(e);

        for (int i = 0; i < 6; i++)
        {
            await peer.SendStanzaAsync(BuildInboundMessageNode("cap-1", rig.DeviceA.Jid, GarbageWire(), "pkmsg"));
        }

        await WaitForQueueCountAsync(failures, 6, "six typed failures for one id");
        await Task.Delay(500, token); // slack so a broken cap would have time to leak a 6th receipt

        peer.ReceiptNodes.Should().HaveCount(5, "the sixth failure for one id emits no wire receipt (cap = 5)");
        var counts = new List<int>();
        while (peer.ReceiptNodes.TryDequeue(out BinaryNode? node))
        {
            counts.Add(int.Parse(
                (node.Children ?? []).Single(c => c.Tag == "retry").Attrs["count"],
                CultureInfo.InvariantCulture));
        }

        counts.Should().BeEquivalentTo(new[] { 1, 2, 3, 4, 5 }, "the wire receipts carry counts 1 through 5 exactly once each");
        failures.Select(f => f.RetryCount).Should().BeEquivalentTo(
            new[] { 1, 2, 3, 4, 5, 6 }, "the typed event fires for every failure, uncapped");

        // The cap is per (sender, message id): a different id starts its own count.
        await peer.SendStanzaAsync(BuildInboundMessageNode("cap-2", rig.DeviceA.Jid, GarbageWire(), "pkmsg"));
        await WaitForReceiptCountAsync(peer, 1, "a distinct id still emits");
        peer.ReceiptNodes.TryDequeue(out BinaryNode? other).Should().BeTrue();
        (other!.Children ?? []).Single(c => c.Tag == "retry").Attrs["count"].Should().Be(
            "1", "the counter is keyed per id, not global");
        other.Attrs["id"].Should().Be("cap-2");
    }

    [Fact]
    public async Task keys_node_rides_retry_two_with_a_fresh_one_time_prekey_consumed_from_the_store()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("retry-keys", token, seedReceivePair: true);
        await SeedOwnPrekeysAsync(rig.PreKeys, count: 5, firstId: 100, token);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        (await rig.PreKeys.GetPreKeyAsync(100, token))
            .Should().NotBeNull("the seeded one-time prekey exists before the exchange");

        // Two failures for the SAME id: the count is per (sender, message id), so only
        // the second failure for one id carries the keys block.
        await peer.SendStanzaAsync(BuildInboundMessageNode("keys-1", rig.DeviceA.Jid, GarbageWire(), "pkmsg"));
        await peer.SendStanzaAsync(BuildInboundMessageNode("keys-1", rig.DeviceA.Jid, GarbageWire(), "pkmsg"));
        await WaitForReceiptCountAsync(peer, 2, "both retry receipts");

        // Frame processing order is not guaranteed: match the receipts by their wire count.
        var receipts = new List<BinaryNode>();
        while (peer.ReceiptNodes.TryDequeue(out BinaryNode? node))
        {
            receipts.Add(node);
        }

        BinaryNode first = receipts.Single(r =>
            (r.Children ?? []).Single(c => c.Tag == "retry").Attrs["count"] == "1");
        BinaryNode second = receipts.Single(r =>
            (r.Children ?? []).Single(c => c.Tag == "retry").Attrs["count"] == "2");
        (first.Children ?? []).Should().NotContain(
            c => c.Tag == "keys", "retry #1 with a live session carries no keys block");

        IReadOnlyList<BinaryNode> secondChildren = second.Children ?? [];
        secondChildren.Single(c => c.Tag == "retry").Attrs["count"].Should().Be("2");
        BinaryNode keys = secondChildren.Single(c => c.Tag == "keys");

        IReadOnlyList<BinaryNode> keyParts = keys.Children ?? [];
        keyParts.Single(c => c.Tag == "type").Data.Should().Equal(new byte[] { 0x05 }, "the key-bundle type byte");
        keyParts.Single(c => c.Tag == "identity").Data.Should().HaveCount(32, "the raw 32-byte identity");
        BinaryNode key = keyParts.Single(c => c.Tag == "key");
        key.Children!.Single(c => c.Tag == "id").Data.Should().Equal(
            BigEndian(100, PreKeyManager.PreKeyIdWidth), "the fresh one-time prekey id, 3-byte big-endian");
        key.Children!.Single(c => c.Tag == "value").Data.Should().HaveCount(32);
        BinaryNode skey = keyParts.Single(c => c.Tag == "skey");
        skey.Children!.Single(c => c.Tag == "id").Data.Should().Equal(
            BigEndian(22, PreKeyManager.PreKeyIdWidth), "the seeded signed-prekey id");
        skey.Children!.Single(c => c.Tag == "value").Data.Should().HaveCount(32);
        skey.Children!.Single(c => c.Tag == "signature").Data.Should().HaveCount(64);
        keyParts.Single(c => c.Tag == "device-identity").Data.Should().HaveCount(33, "the DJB-encoded identity");

        (await rig.PreKeys.GetPreKeyAsync(100, token))
            .Should().BeNull("the emitted one-time prekey is spent (D-06: store before wire)");
        (await rig.PreKeys.GetPreKeyAsync(101, token))
            .Should().NotBeNull("exactly one prekey was consumed");
    }

    [Fact]
    public async Task ordering_removes_the_consumed_one_time_prekey_before_the_retry_node_reaches_the_wire()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("retry-order", token, seedReceivePair: true);
        await SeedOwnPrekeysAsync(rig.PreKeys, count: 5, firstId: 100, token);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        await peer.SendStanzaAsync(BuildInboundMessageNode("ord-1", rig.DeviceA.Jid, GarbageWire(), "pkmsg"));
        await WaitForReceiptCountAsync(peer, 1, "the first (keys-less) retry receipt");

        // The second failure for the SAME id: count 2 → the keys block rides and the
        // consumed prekey must already be gone by the time the peer sees the node.
        await peer.SendStanzaAsync(BuildInboundMessageNode("ord-1", rig.DeviceA.Jid, GarbageWire(), "pkmsg"));
        await WaitForReceiptCountAsync(peer, 2, "the keys-carrying retry receipt");

        // The peer observing the node proves the removal already happened: the store
        // write completes before SendNodeAsync is even called (D-06 ordering).
        (await rig.PreKeys.GetPreKeyAsync(100, token))
            .Should().BeNull("the consumed prekey is removed before the retry node reaches the wire");

        peer.ReceiptNodes.TryDequeue(out BinaryNode? first).Should().BeTrue();
        (first!.Children ?? []).Should().NotContain(c => c.Tag == "keys");
        peer.ReceiptNodes.TryDequeue(out BinaryNode? second).Should().BeTrue();
        BinaryNode keys = (second!.Children ?? []).Single(c => c.Tag == "keys");
        keys.Children!.Single(c => c.Tag == "key").Children!
            .Single(c => c.Tag == "id").Data.Should().Equal(
                BigEndian(100, PreKeyManager.PreKeyIdWidth),
                "the wire names exactly the id the store already consumed");
    }

    [Fact]
    public async Task resend_same_id_reencrypts_cache_plaintext_and_preserves_id_and_original_t()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("retry-resend", token);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        var retryEvents = new ConcurrentQueue<ReceiptReceivedEventArgs>();
        rig.Messenger.ReceiptReceived += (_, e) => retryEvents.Enqueue(e);

        SendOutcome first = await rig.Messenger.SendMessageAsync(rig.Chat, "retry payload", token);
        first.Status.Should().Be(SendOutcomeStatus.Sent);
        await WaitForAsync(peer.Log, entry => entry == "message:" + first.MessageId, "the original send");
        peer.MessageNodes.TryPeek(out BinaryNode? original).Should().BeTrue();
        string originalId = original!.Attrs["id"];
        string originalT = original.Attrs["t"];

        byte[] registration = BigEndian(rig.DeviceA.RegistrationId, 4);
        await peer.SendStanzaAsync(BuildPeerRetryReceipt(rig.DeviceA.Jid, originalId, 1, registration, keys: null));
        await WaitForReceiptEventsAsync(retryEvents, 1, "the typed retry-receipt event");
        retryEvents.Single().Kind.Should().Be(ReceiptKind.Retry);
        retryEvents.Single().MessageIds.Should().Equal(originalId);

        await WaitForMessageCountAsync(peer, 2, "the same-id resend");
        peer.MessageNodes.TryDequeue(out BinaryNode? node1).Should().BeTrue();
        peer.MessageNodes.TryDequeue(out BinaryNode? node2).Should().BeTrue();

        node2!.Attrs["id"].Should().Be(originalId, "the resend reuses the SAME id (Pattern 5)");
        node2.Attrs["t"].Should().Be(originalT, "the original t rides the resend verbatim");
        node2.Attrs["type"].Should().Be("text");
        node2.Attrs["to"].Should().Be(rig.DeviceA.Jid, "the resend targets the requester device only");
        node2.Attrs["device_fanout"].Should().Be("false", "1:1 resends set device_fanout=false");
        BinaryNode resendEnc = ExtractEnc(node2);
        resendEnc.Attrs["type"].Should().Be("msg", "the live session continues as a regular message");
        node2.Children.Should().NotContain(
            c => c.Tag == "device-identity", "device-identity rides only pkmsg (Pattern 2)");

        // The peer's mirrored Bob decrypts both frames in arrival order: same plaintext, both ids.
        DoubleRatchet bob = peer.BobRatchets[rig.DeviceA.Jid];
        BinaryNode originalEnc = ExtractEnc(node1!);
        SignalDecryptOutcome.Decrypted plainOriginal = bob.Decrypt(
            originalEnc.Data!,
            string.Equals(originalEnc.Attrs["type"], "pkmsg", StringComparison.Ordinal),
            token).Should().BeOfType<SignalDecryptOutcome.Decrypted>().Which;
        SignalDecryptOutcome.Decrypted plainResend = bob.Decrypt(
            resendEnc.Data!,
            string.Equals(resendEnc.Attrs["type"], "pkmsg", StringComparison.Ordinal),
            token).Should().BeOfType<SignalDecryptOutcome.Decrypted>().Which;
        Message.Parser.ParseFrom(plainOriginal.Plaintext).Conversation.Should().Be("retry payload");
        Message.Parser.ParseFrom(plainResend.Plaintext).Conversation.Should().Be(
            "retry payload", "the cache plaintext is re-encrypted for the requester");

        await Task.Delay(300, token);
        peer.MessageNodes.Should().BeEmpty("one resend per retry receipt — never a resend loop");
    }

    [Fact]
    public async Task fresh_session_retry_keys_rebuilds_the_session_and_answers_with_pkmsg()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("retry-fresh", token);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        SendOutcome first = await rig.Messenger.SendMessageAsync(rig.Chat, "fresh session", token);
        first.Status.Should().Be(SendOutcomeStatus.Sent);
        await WaitForAsync(peer.Log, entry => entry == "message:" + first.MessageId, "the original send");

        SignalSessionRecord? before = await rig.Sessions.GetAsync(rig.DeviceA.Jid, token);
        before.Should().NotBeNull("the seeded session exists before the rebuild");
        (await rig.Identity.GetAsync(rig.DeviceA.Jid, token))
            .Should().BeNull("the send path found a live session and never pinned an identity");

        byte[] registration = BigEndian(rig.DeviceA.RegistrationId, 4);
        BinaryNode keys = BuildPeerKeysBlock(rig.DeviceA);
        await peer.SendStanzaAsync(BuildPeerRetryReceipt(
            rig.DeviceA.Jid, first.MessageId, 2, registration, keys));

        await WaitForMessageCountAsync(peer, 2, "the fresh-session resend");
        peer.MessageNodes.TryDequeue(out BinaryNode? original).Should().BeTrue();
        peer.MessageNodes.TryDequeue(out BinaryNode? resend).Should().BeTrue();

        resend!.Attrs["id"].Should().Be(first.MessageId, "same id even across a session rebuild");
        BinaryNode resendEnc = ExtractEnc(resend);
        resendEnc.Attrs["type"].Should().Be(
            "pkmsg", "a rebuilt session answers with a prekey message (fresh Alice)");

        SignalSessionRecord? after = await rig.Sessions.GetAsync(rig.DeviceA.Jid, token);
        after.Should().NotBeNull("the rebuilt session is persisted (D-06) before the wire");
        after!.SessionState.SequenceEqual(before!.SessionState)
            .Should().BeFalse("the rebuilt session replaced the seeded one");

        SignalIdentityRecord? pin = await rig.Identity.GetAsync(rig.DeviceA.Jid, token);
        pin.Should().NotBeNull("first contact pins the keys-block identity (TOFU, D-06)");
        pin!.IdentityKeyDjb.Should().Equal(
            rig.DeviceA.EdPublicDjb, "the pin is the requester identity the signature verified under");

        await Task.Delay(300, token);
        peer.MessageNodes.Should().BeEmpty("exactly one resend per retry receipt");
    }

    [Fact]
    public async Task wrong_requester_gets_zero_resend_frames_and_the_receive_path_continues()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("retry-wrong", token);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        SendOutcome first = await rig.Messenger.SendMessageAsync(rig.Chat, "not for you", token);
        first.Status.Should().Be(SendOutcomeStatus.Sent);
        await WaitForAsync(peer.Log, entry => entry == "message:" + first.MessageId, "the original send");

        // A requester outside the original recipient set (T-4-12): different user entirely.
        string wrongRequester = DeviceJid("15550001111", 7);
        byte[] registration = BigEndian(rig.DeviceA.RegistrationId, 4);
        await peer.SendStanzaAsync(BuildPeerRetryReceipt(
            wrongRequester, first.MessageId, 1, registration, keys: null));
        await Task.Delay(500, token);

        peer.MessageNodes.Should().HaveCount(
            1, "zero resend frames for a requester outside the recipient set — no throw, no leak");

        // The receive path continues: the legitimate requester's retry is still answered.
        await peer.SendStanzaAsync(BuildPeerRetryReceipt(
            rig.DeviceA.Jid, first.MessageId, 2, registration, keys: null));
        await WaitForMessageCountAsync(peer, 2, "the pump continued to the next frame");
        peer.MessageNodes.TryDequeue(out BinaryNode? original).Should().BeTrue();
        peer.MessageNodes.TryDequeue(out BinaryNode? resend).Should().BeTrue();
        resend!.Attrs["id"].Should().Be(first.MessageId, "the valid retry still gets its same-id resend");
        original!.Attrs["id"].Should().Be(first.MessageId);
    }

    [Fact]
    public async Task degrade_cache_miss_sends_no_resend_no_throw_and_the_pump_continues()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync(
            "retry-degrade", token,
            cacheFactoryOverride: fixedStores =>
                new FaultingStoreFactory(recentMessageCache: fixedStores) { Mode = FaultMode.FailWrites });
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        var receiptEvents = new ConcurrentQueue<ReceiptReceivedEventArgs>();
        rig.Messenger.ReceiptReceived += (_, e) => receiptEvents.Enqueue(e);

        // The cache write fails on the send path (D-20: the send itself still succeeds),
        // so the later retry finds no resend source — the documented degrade outcome.
        SendOutcome first = await rig.Messenger.SendMessageAsync(rig.Chat, "no resend source", token);
        first.Status.Should().Be(SendOutcomeStatus.Sent, "a cache failure never fails the send (D-20)");
        await WaitForAsync(peer.Log, entry => entry == "message:" + first.MessageId, "the original send");

        byte[] registration = BigEndian(rig.DeviceA.RegistrationId, 4);
        await peer.SendStanzaAsync(BuildPeerRetryReceipt(
            rig.DeviceA.Jid, first.MessageId, 1, registration, keys: null));
        await WaitForReceiptEventsAsync(receiptEvents, 1, "the retry receipt parsed into a typed event");
        receiptEvents.Single().Kind.Should().Be(ReceiptKind.Retry);
        await Task.Delay(500, token);

        peer.MessageNodes.Should().HaveCount(
            1, "an unsatisfiable cache degrades to no resend — never a throw (Pitfall 6)");

        // The pump is not wedged: the next scripted frame still reaches the typed event path.
        await peer.SendStanzaAsync(BinaryNode.Create(
            "receipt",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["id"] = "dr-1",
                ["type"] = "delivery",
                ["from"] = rig.DeviceA.Jid,
                ["t"] = "1700000001",
            }));
        await WaitForReceiptEventsAsync(receiptEvents, 2, "the frame after the degrade still flows");
        receiptEvents.ElementAt(1).Kind.Should().Be(ReceiptKind.Delivery, "the dispatcher kept processing");
        receiptEvents.ElementAt(1).MessageIds.Should().Equal("dr-1");
    }
}
