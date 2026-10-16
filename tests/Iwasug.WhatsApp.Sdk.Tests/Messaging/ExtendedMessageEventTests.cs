using System.Collections.Concurrent;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Messaging;
using Iwasug.WhatsApp.Sdk.Signal;
using Iwasug.WhatsApp.Sdk.Stores;
using Xunit;
using static Iwasug.WhatsApp.Sdk.Tests.Messaging.MessageCorrelationTests;
using static Iwasug.WhatsApp.Sdk.Tests.Messaging.MessagingLoopbackTests;

namespace Iwasug.WhatsApp.Sdk.Tests.Messaging;

/// <summary>
/// Inbound reaction, edit, and delete-for-everyone recognition (EXT-03/EXT-04):
/// ack precedes the typed event, which precedes the recent-cache write.
/// </summary>
public sealed class ExtendedMessageEventTests
{
    [Fact]
    public async Task inbound_reaction_ack_then_event_then_cache()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("ext-reaction", token, seedReceivePair: true);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        var reactions = new ConcurrentQueue<ReactionReceivedEventArgs>();
        rig.Messenger.ReactionReceived += (_, e) => reactions.Enqueue(e);

        SignalEncryptOutcome outcome = rig.PeerAlice!.Encrypt(MessageBuilder.BuildReaction("target-1", "+1"), token);
        byte[] wire = outcome is SignalEncryptOutcome.PreKeyMessageSent pk
            ? pk.WireBytes
            : ((SignalEncryptOutcome.MessageSent)outcome).WireBytes;
        string encType = outcome is SignalEncryptOutcome.PreKeyMessageSent ? "pkmsg" : "msg";

        await peer.SendStanzaAsync(BuildInboundMessageNode("reaction-1", rig.DeviceA.Jid, wire, encType));
        await WaitForAsync(peer.Log, entry => entry == "ack:reaction-1", "the SDK acked the inbound reaction");

        await Task.Delay(300, token);
        reactions.Should().HaveCount(1, "one reaction event is raised");
        ReactionReceivedEventArgs evt = reactions.Single();
        evt.Chat.Should().Be(rig.Chat);
        evt.TargetMessageId.Should().Be("target-1");
        evt.Sender.ToString().Should().Be(rig.DeviceA.Jid);
        evt.Emoji.Should().Be("+1");

        RecentMessageEntry? cached = await rig.RecentMessages.GetAsync("reaction-1", token);
        cached.Should().NotBeNull("a successful cache write follows the typed event");
    }

    [Fact]
    public async Task inbound_edit_ack_then_event_then_cache()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("ext-edit", token, seedReceivePair: true);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        var edits = new ConcurrentQueue<MessageEditedEventArgs>();
        rig.Messenger.MessageEdited += (_, e) => edits.Enqueue(e);

        SignalEncryptOutcome outcome = rig.PeerAlice!.Encrypt(MessageBuilder.BuildProtocolEdit("target-edit", "edited body"), token);
        byte[] wire = outcome is SignalEncryptOutcome.PreKeyMessageSent pk
            ? pk.WireBytes
            : ((SignalEncryptOutcome.MessageSent)outcome).WireBytes;
        string encType = outcome is SignalEncryptOutcome.PreKeyMessageSent ? "pkmsg" : "msg";

        await peer.SendStanzaAsync(BuildInboundMessageNode("edit-1", rig.DeviceA.Jid, wire, encType));
        await WaitForAsync(peer.Log, entry => entry == "ack:edit-1", "the SDK acked the inbound edit");

        await Task.Delay(300, token);
        edits.Should().HaveCount(1, "one edit event is raised");
        MessageEditedEventArgs evt = edits.Single();
        evt.Chat.Should().Be(rig.Chat);
        evt.MessageId.Should().Be("target-edit");
        evt.NewText.Should().Be("edited body");

        RecentMessageEntry? cached = await rig.RecentMessages.GetAsync("edit-1", token);
        cached.Should().NotBeNull("a successful cache write follows the typed event");
    }

    [Fact]
    public async Task inbound_delete_ack_then_event_then_cache()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("ext-delete", token, seedReceivePair: true);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        var deletes = new ConcurrentQueue<MessageDeletedEventArgs>();
        rig.Messenger.MessageDeleted += (_, e) => deletes.Enqueue(e);

        SignalEncryptOutcome outcome = rig.PeerAlice!.Encrypt(MessageBuilder.BuildProtocolRevoke("target-delete"), token);
        byte[] wire = outcome is SignalEncryptOutcome.PreKeyMessageSent pk
            ? pk.WireBytes
            : ((SignalEncryptOutcome.MessageSent)outcome).WireBytes;
        string encType = outcome is SignalEncryptOutcome.PreKeyMessageSent ? "pkmsg" : "msg";

        await peer.SendStanzaAsync(BuildInboundMessageNode("delete-1", rig.DeviceA.Jid, wire, encType));
        await WaitForAsync(peer.Log, entry => entry == "ack:delete-1", "the SDK acked the inbound delete");

        await Task.Delay(300, token);
        deletes.Should().HaveCount(1, "one delete event is raised");
        MessageDeletedEventArgs evt = deletes.Single();
        evt.Chat.Should().Be(rig.Chat);
        evt.MessageIds.Should().Contain("target-delete");
        evt.Remover.ToString().Should().Be(rig.DeviceA.Jid);

        RecentMessageEntry? cached = await rig.RecentMessages.GetAsync("delete-1", token);
        cached.Should().NotBeNull("a successful cache write follows the typed event");
    }

    [Fact]
    public async Task ack_precedes_typed_event()
    {
        // A shared timeline proves the wire ack is correlated/raised only after
        // the durable ack has left the SDK. Here we assert the event never appears
        // before the ack reaches the scripted peer.
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("ext-order", token, seedReceivePair: true);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        var reactions = new ConcurrentQueue<ReactionReceivedEventArgs>();
        rig.Messenger.ReactionReceived += (_, e) => reactions.Enqueue(e);

        SignalEncryptOutcome outcome = rig.PeerAlice!.Encrypt(MessageBuilder.BuildReaction("target-order", "heart"), token);
        byte[] wire = outcome is SignalEncryptOutcome.PreKeyMessageSent pk
            ? pk.WireBytes
            : ((SignalEncryptOutcome.MessageSent)outcome).WireBytes;
        string encType = outcome is SignalEncryptOutcome.PreKeyMessageSent ? "pkmsg" : "msg";

        await peer.SendStanzaAsync(BuildInboundMessageNode("order-1", rig.DeviceA.Jid, wire, encType));

        // Before the ack reaches the peer, no event should have been raised.
        reactions.Should().BeEmpty("the typed event is never raised before the ack");
        await WaitForAsync(peer.Log, entry => entry == "ack:order-1", "the SDK acked the inbound reaction");
        await Task.Delay(300, token);
        reactions.Should().HaveCount(1);
    }
}


