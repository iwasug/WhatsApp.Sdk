using System.Text.Json;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Messaging;
using Iwasug.WhatsApp.Sdk.Signal;
using Iwasug.WhatsApp.Sdk.Stores;
using Xunit;
using static Iwasug.WhatsApp.Sdk.Tests.Messaging.MessageCorrelationTests;
using static Iwasug.WhatsApp.Sdk.Tests.Messaging.MessagingLoopbackTests;

namespace Iwasug.WhatsApp.Sdk.Tests.Messaging;

/// <summary>
/// The MSG-05 quoting proof (04-04 Task 3): <c>Messenger.ReplyAsync</c> reads the
/// quoted message's id, sender, chat, and former plaintext from the recent-message
/// cache (the ONLY quote source, Pitfall 6) and sends a quote-carrying
/// extendedTextMessage payload through the exact same transport path as a plain
/// send — cache hit, group participant, missing-entry fail-closed, plain-vs-quote
/// payload divergence, and non-ASCII round-trip.
/// </summary>
/// <remarks>
/// A2 verification note: the fixture pins the quote inside <c>extendedTextMessage</c>
/// because WhatsApp renders quotes only through that form — the end-of-phase UAT
/// checklist (plan 04-05) confirms rendering on a real peer. These facts prove the
/// SDK emits the correct bytes and context fields; they do not prove the peer UI.
/// Output discipline (V6/T-4-05): assertions compare fixed fixture inputs and hex
/// of wire data only — no log call ever sees message text.
/// </remarks>
public sealed class QuotingTests
{
    private sealed record QuoteFixture(
        string ReplyText,
        string StanzaId,
        string Participant,
        string QuotedText,
        string RemoteJid,
        string ProtobufHex);

    private static QuoteFixture LoadQuoteFixture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "message-wire-fixtures.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement quote = doc.RootElement.GetProperty("quote");
        return new QuoteFixture(
            quote.GetProperty("replyText").GetString()!,
            quote.GetProperty("stanzaId").GetString()!,
            quote.GetProperty("participant").GetString()!,
            quote.GetProperty("quotedText").GetString()!,
            quote.GetProperty("remoteJid").GetString()!,
            quote.GetProperty("protobufHex").GetString()!);
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static BinaryNode ExtractEnc(BinaryNode messageNode) =>
        (messageNode.Children ?? [])
            .SelectMany(c => c.Children ?? (IReadOnlyList<BinaryNode>)[])
            .Single(c => c.Tag == "enc");

    /// <summary>Decrypts one sent message node with the peer's mirrored Bob and parses the plaintext <c>Message</c>.</summary>
    private static Message DecryptToMessage(ScriptedPeer peer, Rig rig, BinaryNode messageNode, CancellationToken token)
    {
        BinaryNode enc = ExtractEnc(messageNode);
        DoubleRatchet bob = peer.BobRatchets[rig.DeviceA.Jid];
        bool isPreKey = string.Equals(enc.Attrs["type"], "pkmsg", StringComparison.Ordinal);
        SignalDecryptOutcome.Decrypted plain = bob.Decrypt(enc.Data!, isPreKey, token)
            .Should().BeOfType<SignalDecryptOutcome.Decrypted>().Which;
        return Message.Parser.ParseFrom(plain.Plaintext);
    }

    private static byte[] DecryptToBytes(ScriptedPeer peer, Rig rig, BinaryNode messageNode, CancellationToken token)
    {
        BinaryNode enc = ExtractEnc(messageNode);
        DoubleRatchet bob = peer.BobRatchets[rig.DeviceA.Jid];
        bool isPreKey = string.Equals(enc.Attrs["type"], "pkmsg", StringComparison.Ordinal);
        SignalDecryptOutcome.Decrypted plain = bob.Decrypt(enc.Data!, isPreKey, token)
            .Should().BeOfType<SignalDecryptOutcome.Decrypted>().Which;
        return plain.Plaintext;
    }

    [Fact]
    public async Task cache_hit_reply_carries_the_cached_entry_and_pins_the_quote_fixture_bytes()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("quote-cache", token);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;
        QuoteFixture fx = LoadQuoteFixture();

        // The quoted message was cached under its id (outbound or inbound write).
        await rig.RecentMessages.PutAsync(
            new RecentMessageEntry(
                fx.StanzaId,
                fx.RemoteJid,
                DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
                fx.QuotedText,
                fx.Participant),
            token);

        SendOutcome outcome = await rig.Messenger.ReplyAsync(rig.Chat, fx.StanzaId, fx.ReplyText, token);
        outcome.Status.Should().Be(SendOutcomeStatus.Sent, "a quoted reply settles like any send");
        outcome.MessageId.Should().MatchRegex("^3EB0[0-9A-F]{36}$");
        await WaitForAsync(peer.Log, entry => entry == "message:" + outcome.MessageId, "the reply node flowed out");

        peer.MessageNodes.TryPeek(out BinaryNode? node).Should().BeTrue();
        node!.Attrs["id"].Should().Be(outcome.MessageId);
        node.Attrs["to"].Should().Be(rig.Chat.ToString(), "quoting changes the payload, not the transport");
        node.Attrs["type"].Should().Be("text");

        // Parsed proto fields: every ContextInfo field equals the cache entry.
        byte[] plaintext = DecryptToBytes(peer, rig, node, token);
        Message parsed = Message.Parser.ParseFrom(plaintext);
        parsed.Conversation.Should().BeEmpty("a quote-carrying payload never rides conversation");
        parsed.ExtendedTextMessage.Should().NotBeNull();
        ContextInfo context = parsed.ExtendedTextMessage!.ContextInfo;
        context.StanzaId.Should().Be(fx.StanzaId, "stanzaId = the quoted id");
        context.Participant.Should().Be(fx.Participant, "participant = the cached sender");
        context.RemoteJID.Should().Be(fx.RemoteJid, "remoteJid = the cached chat");
        context.QuotedMessage.Should().NotBeNull("the quoted render source rides");
        context.QuotedMessage!.Conversation.Should().Be(fx.QuotedText, "quotedMessage = the cached plaintext");
        parsed.ExtendedTextMessage.Text.Should().Be(fx.ReplyText);

        // Serialized bytes match plan 04-02's quote fixture exactly (end-to-end pin).
        Hex(plaintext).Should().Be(fx.ProtobufHex, "the reply payload equals the pinned fixture bytes");

        // The reply itself is a resend candidate: the quoted payload is cached under the reply id (D-20).
        RecentMessageEntry? replyEntry = await rig.RecentMessages.GetAsync(outcome.MessageId, token);
        replyEntry.Should().NotBeNull("every send caches its resend source");
        replyEntry!.Text.Should().Be(fx.ReplyText);
        Hex(replyEntry.ResendPayload!).Should().Be(fx.ProtobufHex, "a retry receipt for the reply resends the quoted bytes");
    }

    [Fact]
    public async Task group_reply_populates_participant_from_the_cached_sender_and_remotejid_from_the_group_chat()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("quote-group", token);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        Jid groupChat = Jid.Parse("120363012345678901@g.us");
        await rig.RecentMessages.PutAsync(
            new RecentMessageEntry(
                "3EB0GROUPQUOTED01",
                groupChat.ToString(),
                DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
                "group quoted text",
                rig.DeviceA.Jid),
            token);

        SendOutcome outcome = await rig.Messenger.ReplyAsync(groupChat, "3EB0GROUPQUOTED01", "reply in the group", token);
        outcome.Status.Should().Be(SendOutcomeStatus.Sent);
        await WaitForAsync(peer.Log, entry => entry == "message:" + outcome.MessageId, "the group reply node flowed out");

        peer.MessageNodes.TryPeek(out BinaryNode? node).Should().BeTrue();
        node!.Attrs["to"].Should().Be(groupChat.ToString(), "the node addresses the group chat");

        Message parsed = DecryptToMessage(peer, rig, node, token);
        ContextInfo context = parsed.ExtendedTextMessage!.ContextInfo;
        context.Participant.Should().Be(
            rig.DeviceA.Jid, "participant is populated from the cached sender for group messages");
        context.RemoteJID.Should().Be(groupChat.ToString(), "remoteJid is the group chat from the cache entry");
        context.StanzaId.Should().Be("3EB0GROUPQUOTED01");
        context.QuotedMessage!.Conversation.Should().Be("group quoted text");
    }

    [Fact]
    public async Task missing_entry_rejects_with_sdk_format_exception_before_any_frame()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("quote-miss", token);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        // Unknown quoted id: typed failure, zero frames — the SDK never downgrades to plain text.
        Func<Task> unknownId = () => rig.Messenger.ReplyAsync(
            rig.Chat, "3EB0NOTINTHECACHE000000000000000000000", "reply", token);
        await unknownId.Should().ThrowAsync<SdkFormatException>(
            "a quoted id absent from the cache rejects before any node exists");

        // Empty reply text: also rejected before any node exists.
        await rig.RecentMessages.PutAsync(
            new RecentMessageEntry(
                "3EB0VALIDQUOTED001",
                rig.Chat.ToString(),
                DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
                "valid quoted text",
                null),
            token);
        Func<Task> emptyText = () => rig.Messenger.ReplyAsync(rig.Chat, "3EB0VALIDQUOTED001", string.Empty, token);
        await emptyText.Should().ThrowAsync<SdkFormatException>("empty reply text never reaches the wire");

        // An unparseable cached chat is also a typed failure before any node exists.
        await rig.RecentMessages.PutAsync(
            new RecentMessageEntry(
                "3EB0BADCHATQUOTED1",
                "not a jid",
                DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
                "text",
                null),
            token);
        Func<Task> badChat = () => rig.Messenger.ReplyAsync(rig.Chat, "3EB0BADCHATQUOTED1", "reply", token);
        await badChat.Should().ThrowAsync<SdkFormatException>("an unparseable cached chat rejects fail-closed");

        await Task.Delay(300, token);
        peer.MessageNodes.Should().BeEmpty("every rule violation sent zero frames");
    }

    [Fact]
    public async Task plain_vs_quote_share_the_node_envelope_but_not_the_payload_shape()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("quote-shape", token, safetyOptions: NoSafetyDelays());
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        SendOutcome plain = await rig.Messenger.SendMessageAsync(rig.Chat, "plain text", token);
        plain.Status.Should().Be(SendOutcomeStatus.Sent);
        await WaitForAsync(peer.Log, entry => entry == "message:" + plain.MessageId, "the plain node flowed out");

        await rig.RecentMessages.PutAsync(
            new RecentMessageEntry(
                "3EB0QUOTEDFORSHAPE01",
                rig.Chat.ToString(),
                DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
                "quoted for shape",
                null),
            token);
        SendOutcome quoted = await rig.Messenger.ReplyAsync(rig.Chat, "3EB0QUOTEDFORSHAPE01", "quoted reply", token);
        quoted.Status.Should().Be(SendOutcomeStatus.Sent);
        await WaitForAsync(peer.Log, entry => entry == "message:" + quoted.MessageId, "the quoted node flowed out");

        peer.MessageNodes.TryDequeue(out BinaryNode? plainNode).Should().BeTrue();
        peer.MessageNodes.TryDequeue(out BinaryNode? quoteNode).Should().BeTrue();

        // Same id format, same node attrs — only the payload shape differs.
        plainNode!.Attrs["id"].Should().MatchRegex("^3EB0[0-9A-F]{36}$");
        quoteNode!.Attrs["id"].Should().MatchRegex("^3EB0[0-9A-F]{36}$");
        plainNode.Attrs["to"].Should().Be(quoteNode.Attrs["to"], "both address the same chat");
        plainNode.Attrs["type"].Should().Be(quoteNode.Attrs["type"], "both are type=text nodes");
        long.Parse(plainNode.Attrs["t"], System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeGreaterThan(0, "both carry a send-time t");
        long.Parse(quoteNode.Attrs["t"], System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeGreaterThan(0, "both carry a send-time t");
        plainNode.Attrs.Keys.Should().BeEquivalentTo(
            quoteNode.Attrs.Keys, "quoting never changes the node attribute shape");

        Message plainPayload = DecryptToMessage(peer, rig, plainNode, token);
        plainPayload.Conversation.Should().Be("plain text", "plain sends ride conversation");
        plainPayload.ExtendedTextMessage.Should().BeNull("plain sends carry no extendedTextMessage");

        Message quotePayload = DecryptToMessage(peer, rig, quoteNode, token);
        quotePayload.Conversation.Should().BeEmpty("quote sends never ride conversation");
        quotePayload.ExtendedTextMessage.Should().NotBeNull("quote sends ride extendedTextMessage with ContextInfo");
        quotePayload.ExtendedTextMessage!.ContextInfo.StanzaId.Should().Be("3EB0QUOTEDFORSHAPE01");
    }

    [Fact]
    public async Task non_ascii_quoted_text_round_trips_utf8_without_normalization()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("quote-nonascii", token);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        const string quotedText = "Hello, 世界 🌍 — quoted verbatim";
        const string replyText = "返信テキスト 🎉 — as typed";
        System.Text.Encoding.UTF8.GetByteCount(quotedText).Should().BeGreaterThan(
            quotedText.Length, "the fixture input is multi-byte UTF-8");

        await rig.RecentMessages.PutAsync(
            new RecentMessageEntry(
                "3EB0NONASCIIQUOTED1",
                rig.Chat.ToString(),
                DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
                quotedText,
                null),
            token);

        SendOutcome outcome = await rig.Messenger.ReplyAsync(rig.Chat, "3EB0NONASCIIQUOTED1", replyText, token);
        outcome.Status.Should().Be(SendOutcomeStatus.Sent);
        await WaitForAsync(peer.Log, entry => entry == "message:" + outcome.MessageId, "the non-ASCII reply flowed out");

        peer.MessageNodes.TryPeek(out BinaryNode? node).Should().BeTrue();
        byte[] plaintext = DecryptToBytes(peer, rig, node!, token);

        Message parsed = Message.Parser.ParseFrom(plaintext);
        parsed.ExtendedTextMessage!.Text.Should().Be(replyText, "the reply text round-trips exactly, never normalized");
        parsed.ExtendedTextMessage.ContextInfo.QuotedMessage!.Conversation.Should().Be(
            quotedText, "the multi-byte quoted text round-trips exactly, never normalized");

        // Byte pin: the payload equals the builder's output for the same inputs.
        byte[] expected = MessageBuilder.BuildQuotedText(
            replyText,
            new QuotedMessageRef("3EB0NONASCIIQUOTED1", null, quotedText, rig.Chat.ToString()));
        Hex(plaintext).Should().Be(Hex(expected), "UTF-8 bytes ride verbatim through cache, build, encrypt, and decrypt");
    }
}
