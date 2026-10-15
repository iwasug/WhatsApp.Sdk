using System.Collections.Concurrent;
using System.Security.Cryptography;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Messaging;
using Iwasug.WhatsApp.Sdk.Signal;
using Xunit;
using static Iwasug.WhatsApp.Sdk.Tests.Messaging.MessageCorrelationTests;
using static Iwasug.WhatsApp.Sdk.Tests.Messaging.MessagingLoopbackTests;

namespace Iwasug.WhatsApp.Sdk.Tests.Messaging;

/// <summary>
/// The MSG-07 typed-stream proof (04-05 Task 2): one vocabulary, two consumption
/// shapes. Causally forced ordering with no duplicates, prompt cancellation with
/// zero events observed after it, bounded-channel backpressure that delivers every
/// scripted event (count == scripted, channel observed at capacity), an in-flight
/// send settled cancelled through the same token as the stream, and the vocabulary
/// contract — exactly the four fact types, with connection state and logout staying
/// <c>EventHandler</c> events on <c>WaConnection</c> (CONN-05).
/// </summary>
public sealed class MessagingEventTests
{
    [Fact]
    public async Task ordering_stream_yields_message_then_receipt_then_send_ack_in_causal_sequence_with_no_duplicates()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("evt-order", token, seedReceivePair: true);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;
        await using var drain = new StreamDrain(rig.Messenger, token);

        // 1. Inbound message → message fact (each fact is read before the next node
        // is scripted, so the observed order is the causally forced one).
        await SendInboundAsync(rig, peer, "evt-msg-1", "stream hello", token);
        var received = (MessageReceivedEvent)await drain.ReadAsync();
        received.Text.Should().Be("stream hello");
        received.Chat.Should().Be(rig.Chat);
        received.Sender.ToString().Should().Be(rig.DeviceA.Jid);
        received.QuotedMessageId.Should().Be("evt-msg-1", "the wire id is quotable back through ReplyAsync");
        received.Key.Id.Should().Be("evt-msg-1");

        // 2. Inbound receipt → receipt fact.
        await peer.SendStanzaAsync(BuildPeerReceiptNode(
            "read", "evt-r1", $"{PeerUser}@{PeerServer}", "1720000000", ["evt-r2"], participant: null));
        var receipt = (ReceiptReceivedEvent)await drain.ReadAsync();
        receipt.Kind.Should().Be(ReceiptKind.Read);
        receipt.MessageIds.Should().Equal("evt-r1", "evt-r2");
        receipt.Chat.Should().Be(rig.Chat);
        receipt.Participant.Should().BeNull("a 1:1 receipt carries no participant");

        // 3. Outbound send settles → its terminal ack fact, carrying the settled outcome.
        SendOutcome outcome = await rig.Messenger.SendMessageAsync(rig.Chat, "ordering ping", token);
        outcome.Status.Should().Be(SendOutcomeStatus.Sent);
        var acked = (MessageAckedEvent)await drain.ReadAsync();
        acked.MessageId.Should().Be(outcome.MessageId, "the stream carries the same id the send settled with");
        acked.Chat.Should().Be(rig.Chat);
        acked.Status.Should().Be(SendOutcomeStatus.Sent);

        // No duplicates: after the three facts nothing else reaches the stream.
        await Task.Delay(300, token);
        rig.Messenger.BufferedEventCount.Should().Be(0, "one fact = one event; no duplicate is published");
    }

    [Fact]
    public async Task cancellation_mid_enumeration_throws_unwrapped_oce_and_no_event_is_observed_afterwards()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("evt-cancel", token, seedReceivePair: true);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        using var cancelCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using IAsyncEnumerator<WhatsAppEvent> stream =
            rig.Messenger.ReadEventsAsync(cancelCts.Token).GetAsyncEnumerator(cancelCts.Token);

        // The stream yields while the token is live.
        await SendInboundAsync(rig, peer, "cxl-1", "before cancel", token);
        (await stream.MoveNextAsync()).Should().BeTrue("the stream yields an event while the token is live");
        stream.Current.Should().BeOfType<MessageReceivedEvent>()
            .Which.Text.Should().Be("before cancel");

        cancelCts.Cancel();
        OperationCanceledException thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stream.MoveNextAsync().AsTask());
        thrown.CancellationToken.Should().Be(cancelCts.Token, "cancellation surfaces unwrapped with the caller's token");

        // The pump keeps publishing after cancellation (dispatch runs on its own
        // token): the fact is buffered, but the cancelled observer never sees it.
        await SendInboundAsync(rig, peer, "cxl-2", "after cancel", token);
        await WaitForAsync(peer.Log, entry => entry == "ack:cxl-2", "the second message was processed post-cancel");
        await WaitForBufferedAsync(rig.Messenger, 1, TimeSpan.FromSeconds(10), token);

        bool yieldedAfterCancel = false;
        try
        {
            yieldedAfterCancel = await stream.MoveNextAsync();
            if (yieldedAfterCancel)
            {
                _ = stream.Current;
            }
        }
        catch (OperationCanceledException)
        {
            yieldedAfterCancel = false;
        }

        yieldedAfterCancel.Should().BeFalse("no event is observed after cancellation");
    }

    [Fact]
    public async Task backpressure_burst_fills_the_bounded_channel_to_capacity_and_the_drain_delivers_every_event()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("evt-backpressure", token, seedReceivePair: true);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        // A non-draining consumer: the stream is open but never advanced.
        await using IAsyncEnumerator<WhatsAppEvent> stream =
            rig.Messenger.ReadEventsAsync(token).GetAsyncEnumerator(token);

        const int burst = 300;

        // The first message initializes the receiving chain causally; the burst
        // follows with regular-form messages whose decrypts serialize behind the
        // per-address session gate (out-of-order within the chain is staged).
        await SendInboundAsync(rig, peer, "bp-000", "burst 0", token);
        await WaitForAsync(peer.Log, entry => entry == "ack:bp-000", "the chain-initializing message was processed");

        for (int i = 1; i < burst; i++)
        {
            await SendInboundAsync(rig, peer, $"bp-{i:D3}", $"burst {i}", token);
        }

        // The bounded channel fills exactly to capacity and the pump is slowed
        // there — never beyond capacity, never dropping (T-4-17).
        Messenger.EventChannelCapacity.Should().Be(128, "the capacity is documented and fixed");
        await WaitForBufferedAsync(rig.Messenger, Messenger.EventChannelCapacity, TimeSpan.FromSeconds(50), token);
        rig.Messenger.BufferedEventCount.Should().Be(
            Messenger.EventChannelCapacity,
            "a non-draining consumer fills the channel to exactly its capacity");

        // Drain: every scripted message arrives exactly once (no silent drops).
        var ids = new List<string>(burst);
        for (int i = 0; i < burst; i++)
        {
            (await stream.MoveNextAsync()).Should().BeTrue("the pump resumes as the consumer drains");
            var message = (MessageReceivedEvent)stream.Current;
            message.Text.Should().StartWith("burst ");
            ids.Add(message.Key.Id);
        }

        ids.Should().HaveCount(burst, "the delivered count equals the scripted count");
        ids.Distinct(StringComparer.Ordinal).Should().HaveCount(burst, "every scripted message is delivered exactly once");
        ids.OrderBy(id => id, StringComparer.Ordinal).Should().Equal(
            Enumerable.Range(0, burst).Select(i => $"bp-{i:D3}"),
            "the delivered id set matches the scripted id set");
    }

    [Fact]
    public async Task in_flight_send_cancelled_through_the_stream_token_settles_cancelled_and_releases_the_gate()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("evt-inflight", token, configurePeer: p => p.AutoAck = false, safetyOptions: NoSafetyDelays());
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        // The host awaits the stream and the send under ONE token.
        using var cancelCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using IAsyncEnumerator<WhatsAppEvent> stream =
            rig.Messenger.ReadEventsAsync(cancelCts.Token).GetAsyncEnumerator(cancelCts.Token);

        Task<SendOutcome> sendTask = rig.Messenger.SendMessageAsync(rig.Chat, "in flight", cancelCts.Token);
        await WaitForAsync(
            peer.Log, entry => entry.StartsWith("message:", StringComparison.Ordinal), "the send reached the wire");
        cancelCts.Cancel();

        OperationCanceledException thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);
        thrown.GetType().Should().Be(typeof(OperationCanceledException), "cancellation surfaces unwrapped");
        thrown.CancellationToken.Should().Be(cancelCts.Token);

        // The stream shares the send's token: nothing yields after cancellation.
        bool yielded = false;
        try
        {
            yielded = await stream.MoveNextAsync();
            if (yielded)
            {
                _ = stream.Current;
            }
        }
        catch (OperationCanceledException)
        {
            yielded = false;
        }

        yielded.Should().BeFalse("the stream honours the same token the in-flight send was cancelled with");

        // The gate released with the cancelled send: the next send settles normally.
        peer.AutoAck = true;
        SendOutcome second = await rig.Messenger.SendMessageAsync(rig.Chat, "after cancel", token);
        second.Status.Should().Be(SendOutcomeStatus.Sent, "the cancelled send released the send gate");
    }

    [Fact]
    public async Task vocabulary_stream_facts_match_the_eventhandler_twins_and_the_hierarchy_has_no_connection_record()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("evt-vocab", token, seedReceivePair: true);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;
        await using var drain = new StreamDrain(rig.Messenger, token);

        var messageHandlers = new ConcurrentQueue<MessageReceivedEventArgs>();
        var receiptHandlers = new ConcurrentQueue<ReceiptReceivedEventArgs>();
        var failureHandlers = new ConcurrentQueue<DecryptionFailedEventArgs>();
        rig.Messenger.MessageReceived += (_, e) => messageHandlers.Enqueue(e);
        rig.Messenger.ReceiptReceived += (_, e) => receiptHandlers.Enqueue(e);
        rig.Messenger.DecryptionFailed += (_, e) => failureHandlers.Enqueue(e);

        // 1. One received message → the same facts on BOTH surfaces (the EventHandler
        // twin fires before the stream write inside one emit point).
        await SendInboundAsync(rig, peer, "vocab-msg", "same facts", token);
        var received = (MessageReceivedEvent)await drain.ReadAsync();
        MessageReceivedEventArgs receivedArgs = messageHandlers.Single();
        received.Key.Should().Be(receivedArgs.Key);
        received.Chat.Should().Be(receivedArgs.Chat);
        received.Sender.Should().Be(receivedArgs.Sender);
        received.IsFromMe.Should().Be(receivedArgs.IsFromMe);
        received.Timestamp.Should().Be(receivedArgs.Timestamp);
        received.Text.Should().Be(receivedArgs.Text);
        received.QuotedMessageId.Should().Be(receivedArgs.QuotedMessageId);

        // 2. One receipt → the same facts on BOTH surfaces.
        await peer.SendStanzaAsync(BuildPeerReceiptNode(
            "delivery", "vocab-r1", $"{PeerUser}@{PeerServer}", "1720000001", [], participant: null));
        var receipt = (ReceiptReceivedEvent)await drain.ReadAsync();
        ReceiptReceivedEventArgs receiptArgs = receiptHandlers.Single();
        receipt.Kind.Should().Be(receiptArgs.Kind);
        receipt.MessageIds.Should().Equal(receiptArgs.MessageIds);
        receipt.Chat.Should().Be(receiptArgs.Chat);
        receipt.Participant.Should().Be(receiptArgs.Participant);
        receipt.Timestamp.Should().Be(receiptArgs.Timestamp);

        // 3. One decrypt failure → the same facts on BOTH surfaces.
        await peer.SendStanzaAsync(BuildInboundMessageNode("vocab-bad", rig.DeviceA.Jid, GarbageWire(), "pkmsg"));
        var failed = (DecryptionFailedEvent)await drain.ReadAsync();
        DecryptionFailedEventArgs failedArgs = failureHandlers.Single();
        failed.Chat.Should().Be(failedArgs.Chat);
        failed.Sender.Should().Be(failedArgs.Sender);
        failed.MessageId.Should().Be(failedArgs.MessageId);
        failed.RetryCount.Should().Be(failedArgs.RetryCount);
        failed.Timestamp.Should().Be(failedArgs.Timestamp);

        // 4. One settled send → the awaited SendOutcome itself reaches the stream
        // (the ack fact has no EventHandler twin — the outcome IS its twin).
        SendOutcome outcome = await rig.Messenger.SendMessageAsync(rig.Chat, "vocab send", token);
        var acked = (MessageAckedEvent)await drain.ReadAsync();
        acked.MessageId.Should().Be(outcome.MessageId);
        acked.Chat.Should().Be(rig.Chat);
        acked.Status.Should().Be(outcome.Status);
        acked.Error.Should().Be(outcome.Error);
        acked.T.Should().Be(outcome.T);
        acked.ServerId.Should().Be(outcome.ServerId);

        // Exactly one EventHandler fact per scripted node: one emit point per fact.
        messageHandlers.Should().HaveCount(1);
        receiptHandlers.Should().HaveCount(1);
        failureHandlers.Should().HaveCount(1);

        // The hierarchy vocabulary: exactly the four fact types, with no
        // connection-state or logout record (they stay on WaConnection, CONN-05).
        Type[] facts = typeof(WhatsAppEvent).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(WhatsAppEvent).IsAssignableFrom(t))
            .ToArray();
        facts.Select(t => t.Name).Should().BeEquivalentTo(
            new[] { "MessageReceivedEvent", "ReceiptReceivedEvent", "MessageAckedEvent", "DecryptionFailedEvent", "BudgetExhaustedEvent", "ThrottleEngagedEvent", "AccountRestrictedEvent", "MediaReceivedEvent" },
            "the stream vocabulary is exactly the four fact types");
        string hierarchyNames = string.Join(' ', facts.Select(t => t.FullName ?? t.Name)).ToLowerInvariant();
        hierarchyNames.Should().NotContain(
            "connection", "connection state stays an EventHandler event on WaConnection (CONN-05)");
        hierarchyNames.Should().NotContain(
            "logout", "logout stays an EventHandler event on WaConnection (CONN-05)");
    }

    /// <summary>Sequential reader over one long-lived ReadEventsAsync enumeration (causal, one fact at a time).</summary>
    private sealed class StreamDrain : IAsyncDisposable
    {
        private readonly IAsyncEnumerator<WhatsAppEvent> _enumerator;

        internal StreamDrain(Messenger messenger, CancellationToken token)
            => _enumerator = messenger.ReadEventsAsync(token).GetAsyncEnumerator(token);

        internal async Task<WhatsAppEvent> ReadAsync()
        {
            if (await _enumerator.MoveNextAsync())
            {
                return _enumerator.Current;
            }

            throw new TimeoutException("The event stream completed before the expected event arrived.");
        }

        public ValueTask DisposeAsync() => _enumerator.DisposeAsync();
    }

    private static async Task SendInboundAsync(Rig rig, ScriptedPeer peer, string messageId, string text, CancellationToken token)
    {
        SignalEncryptOutcome outcome = rig.PeerAlice!.Encrypt(MessageBuilder.BuildPlainText(text), token);
        byte[] wire = outcome is SignalEncryptOutcome.PreKeyMessageSent pk
            ? pk.WireBytes
            : ((SignalEncryptOutcome.MessageSent)outcome).WireBytes;
        string encType = outcome is SignalEncryptOutcome.PreKeyMessageSent ? "pkmsg" : "msg";
        await peer.SendStanzaAsync(BuildInboundMessageNode(messageId, rig.DeviceA.Jid, wire, encType));
    }

    private static byte[] GarbageWire() => RandomNumberGenerator.GetBytes(64);

    private static async Task WaitForBufferedAsync(Messenger messenger, int expected, TimeSpan timeout, CancellationToken token)
    {
        using var poll = new CancellationTokenSource(timeout);
        while (messenger.BufferedEventCount < expected)
        {
            try
            {
                await Task.Delay(10, poll.Token);
            }
            catch (OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();
                break;
            }
        }

        if (messenger.BufferedEventCount < expected)
        {
            throw new TimeoutException(
                $"Timed out waiting for {expected} buffered events; the channel holds {messenger.BufferedEventCount}.");
        }
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
