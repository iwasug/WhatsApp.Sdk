using System.Collections.Concurrent;
using System.Globalization;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Messaging;
using Iwasug.WhatsApp.Sdk.Signal;
using Xunit;
using static Iwasug.WhatsApp.Sdk.Tests.Messaging.MessagingLoopbackTests;
using static Iwasug.WhatsApp.Sdk.Tests.Messaging.MessageCorrelationTests;

namespace Iwasug.WhatsApp.Sdk.Tests.Messaging;

/// <summary>
/// The MSG-04 receipt proof (04-04 Task 1): the opt-in delivery receipt flows out once
/// per decrypted message, <c>MarkReadAsync</c> groups per-message keys into one
/// Pattern-4 receipt per sender (first id in the attribute, the rest as list children),
/// inbound receipts parse into typed events, and the no-opt-in default stays silent.
/// </summary>
public sealed class ReceiptTests
{
    [Fact]
    public async Task delivery_receipt_opt_in_emits_exactly_one_node_per_decrypted_message()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("rcpt-delivery", token, seedReceivePair: true);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;
        rig.Messenger.SetDeliveryReceipts(rig.Chat, true);

        var events = new ConcurrentQueue<MessageReceivedEventArgs>();
        rig.Messenger.MessageReceived += (_, e) => events.Enqueue(e);

        SignalEncryptOutcome sent = rig.PeerAlice!.Encrypt(MessageBuilder.BuildPlainText("receipt me"), token);
        byte[] wire = ((SignalEncryptOutcome.PreKeyMessageSent)sent).WireBytes;
        await peer.SendStanzaAsync(BuildInboundMessageNode("rcpt-1", rig.DeviceA.Jid, wire, "pkmsg"));

        await WaitForAsync(peer.Log, entry => entry == "ack:rcpt-1", "the message was acked");
        await WaitForCountAsync(events, 1, "the message event");
        await WaitForAsync(
            peer.Log, entry => entry == "receipt:delivery:rcpt-1", "the delivery receipt flowed out");

        peer.ReceiptNodes.Count.Should().Be(1, "exactly one delivery receipt per decrypted message");
        peer.ReceiptNodes.TryDequeue(out BinaryNode? receipt).Should().BeTrue();
        receipt!.Attrs["type"].Should().Be("delivery");
        receipt.Attrs["id"].Should().Be("rcpt-1");
        receipt.Attrs["to"].Should().Be(rig.Chat.ToString(), "the delivery receipt targets the bare chat form");
        long t = long.Parse(receipt.Attrs["t"], CultureInfo.InvariantCulture);
        t.Should().BeGreaterThanOrEqualTo(1_700_000_000, "t is always present, stamped at receipt time");
        receipt.Children.Should().BeNullOrEmpty("a delivery receipt is single-id: no list children");
    }

    [Fact]
    public async Task mark_read_single_id_receipt_has_no_list_children_and_repeats_the_wire_contract()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("rcpt-read-single", token);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        var key = new MessageKeyRecord(rig.Chat, FromMe: false, Id: "msg-a", Participant: string.Empty);
        await rig.Messenger.MarkReadAsync([key], token);
        await rig.Messenger.MarkReadAsync([key], token);
        await WaitForReceiptCountAsync(peer, 2, "calling twice sends two receipts (stateless protocol nodes)");

        var nodes = new List<BinaryNode>();
        while (peer.ReceiptNodes.TryDequeue(out BinaryNode? node))
        {
            nodes.Add(node);
        }

        foreach (BinaryNode receipt in nodes)
        {
            receipt.Attrs["type"].Should().Be("read");
            receipt.Attrs["id"].Should().Be("msg-a", "a single id rides the attribute only");
            receipt.Attrs["to"].Should().Be(rig.Chat.ToString());
            receipt.Attrs.Should().ContainKey("t", "t is always present");
            receipt.Attrs.Should().NotContainKey("participant", "participant appears only for group senders");
            receipt.Children.Should().BeNullOrEmpty("a single-id receipt has no list children");
        }
    }

    [Fact]
    public async Task mark_read_grouping_builds_one_receipt_per_sender_pair()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("rcpt-grouping", token);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        Jid groupChat = Jid.Parse("9876543210-123456789@g.us");
        var keys = new List<MessageKeyRecord>
        {
            new(groupChat, FromMe: false, Id: "id-a1", Participant: "2222222222:3@s.whatsapp.net"),
            new(groupChat, FromMe: false, Id: "id-a2", Participant: "2222222222:3@s.whatsapp.net"),
            new(groupChat, FromMe: false, Id: "id-b1", Participant: "2222222222:4@s.whatsapp.net"),
            new(rig.Chat, FromMe: false, Id: "id-c1", Participant: string.Empty),
        };
        await rig.Messenger.MarkReadAsync(keys, token);
        await WaitForReceiptCountAsync(peer, 3, "one receipt per (chat, participant) sender pair");

        var nodes = new List<BinaryNode>();
        while (peer.ReceiptNodes.TryDequeue(out BinaryNode? node))
        {
            nodes.Add(node);
        }

        BinaryNode senderA = nodes.Single(n => n.Attrs.TryGetValue("participant", out string? p) && p == "2222222222:3@s.whatsapp.net");
        senderA.Attrs["id"].Should().Be("id-a1", "the first id rides the attribute");
        senderA.Attrs["to"].Should().Be(groupChat.ToString());
        List<BinaryNode> itemsA = (senderA.Children ?? []).Single(c => c.Tag == "list")
            .Children!.Where(c => c.Tag == "item").ToList();
        // the first id rides the attribute, the rest ride list/item children
        itemsA.Select(i => i.Attrs["id"]).Should().Equal("id-a2");

        BinaryNode senderB = nodes.Single(n => n.Attrs.TryGetValue("participant", out string? p) && p == "2222222222:4@s.whatsapp.net");
        senderB.Attrs["id"].Should().Be("id-b1");
        senderB.Children.Should().BeNullOrEmpty("a single-id receipt has no list");

        BinaryNode bare = nodes.Single(n => !n.Attrs.ContainsKey("participant"));
        bare.Attrs["id"].Should().Be("id-c1");
        bare.Attrs["to"].Should().Be(rig.Chat.ToString(), "the 1:1 receipt targets the bare chat form");
        bare.Children.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task inbound_read_receipt_with_list_children_parses_into_typed_events()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("rcpt-inbound", token);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        var events = new ConcurrentQueue<ReceiptReceivedEventArgs>();
        rig.Messenger.ReceiptReceived += (_, e) => events.Enqueue(e);

        // 1:1 read receipt: ids in the attribute plus list children.
        BinaryNode readNode = BuildPeerReceiptNode(
            "read", "r-1", $"{PeerUser}@{PeerServer}", "1720000000", ["r-2", "r-3"], participant: null);
        await peer.SendStanzaAsync(readNode);
        await WaitForReceiptEventsAsync(events, 1, "the 1:1 read receipt");
        ReceiptReceivedEventArgs first = events.ElementAt(0);
        first.Kind.Should().Be(ReceiptKind.Read);
        first.MessageIds.Should().Equal("r-1", "r-2", "r-3");
        first.Chat.ToString().Should().Be($"{PeerUser}@{PeerServer}");
        first.Participant.Should().BeNull("1:1 receipts carry no participant");
        first.Timestamp.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1720000000), "the wire t attribute wins");

        // Group read receipt: participant attr parsed into the typed event.
        await peer.SendStanzaAsync(BuildPeerReceiptNode(
            "read", "g-1", "9876543210-123456789@g.us", "1720000001", ["g-2"], participant: "2222222222:3@s.whatsapp.net"));
        await WaitForReceiptEventsAsync(events, 2, "the group read receipt");
        ReceiptReceivedEventArgs second = events.ElementAt(1);
        second.Kind.Should().Be(ReceiptKind.Read);
        second.MessageIds.Should().Equal("g-1", "g-2");
        second.Chat.ToString().Should().Be("9876543210-123456789@g.us");
        second.Participant!.ToString().Should().Be("2222222222:3@s.whatsapp.net");

        // A duplicate receipt raises the event again: the host decides (T-4-14).
        await peer.SendStanzaAsync(readNode);
        await WaitForReceiptEventsAsync(events, 3, "the duplicate receipt");
        ReceiptReceivedEventArgs duplicate = events.ElementAt(2);
        duplicate.Kind.Should().Be(first.Kind, "a duplicate parses to the same kind");
        duplicate.MessageIds.Should().Equal(first.MessageIds, "a duplicate parses to the same ids");
        duplicate.Chat.Should().Be(first.Chat);
        duplicate.Timestamp.Should().Be(first.Timestamp);
    }

    [Fact]
    public async Task no_opt_in_delivery_receipts_stay_silent_until_the_host_opts_in()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("rcpt-noopt", token, seedReceivePair: true);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;
        rig.Messenger.IsDeliveryReceiptsEnabled(rig.Chat).Should().BeFalse("the gate is off until the host opts in");

        SignalEncryptOutcome sent1 = rig.PeerAlice!.Encrypt(MessageBuilder.BuildPlainText("silent"), token);
        byte[] wire1 = ((SignalEncryptOutcome.PreKeyMessageSent)sent1).WireBytes;
        await peer.SendStanzaAsync(BuildInboundMessageNode("rcpt-silent-1", rig.DeviceA.Jid, wire1, "pkmsg"));
        await WaitForAsync(peer.Log, entry => entry == "ack:rcpt-silent-1", "the first message flowed");
        await Task.Delay(500, token);
        peer.ReceiptNodes.Should().BeEmpty("delivery receipts stay silent until the host opts in");

        rig.Messenger.SetDeliveryReceipts(rig.Chat, true);
        rig.Messenger.IsDeliveryReceiptsEnabled(rig.Chat).Should().BeTrue();
        SignalEncryptOutcome sent2 = rig.PeerAlice.Encrypt(MessageBuilder.BuildPlainText("loud"), token);
        byte[] wire2 = sent2 is SignalEncryptOutcome.PreKeyMessageSent pk ? pk.WireBytes : ((SignalEncryptOutcome.MessageSent)sent2).WireBytes;
        string encType2 = sent2 is SignalEncryptOutcome.PreKeyMessageSent ? "pkmsg" : "msg";
        await peer.SendStanzaAsync(BuildInboundMessageNode("rcpt-silent-2", rig.DeviceA.Jid, wire2, encType2));
        await WaitForAsync(
            peer.Log, entry => entry == "receipt:delivery:rcpt-silent-2", "the opt-in receipt flowed out");
        peer.ReceiptNodes.Count.Should().Be(1, "opt-in turns the gate on mid-connection");
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

            await Task.Delay(10, timeout.Token);
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

            await Task.Delay(10, timeout.Token);
        }

        throw new TimeoutException($"Timed out waiting for {count} receipt events: {what}.");
    }

    private static BinaryNode BuildPeerReceiptNode(
        string type, string id, string from, string t, IReadOnlyList<string> listIds, string? participant)
    {
        var attrs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["type"] = type,
            ["from"] = from,
            ["t"] = t,
        };
        if (participant is not null)
        {
            attrs["participant"] = participant;
        }

        if (listIds.Count == 0)
        {
            return BinaryNode.Create("receipt", attrs);
        }

        var items = listIds
            .Select(i => BinaryNode.Create("item", new Dictionary<string, string>(StringComparer.Ordinal) { ["id"] = i }))
            .ToArray();
        return BinaryNode.FromChildren("receipt", [BinaryNode.FromChildren("list", items)], attrs);
    }
}
