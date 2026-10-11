using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Messaging;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Messaging;

/// <summary>
/// Wire-exact proofs for one message (04-02 Task 1, MSG-01 encoding half):
/// typed input → protobuf → <c>BinaryNode</c> → encoded bytes → decoded node
/// → parsed protobuf, with every byte pinned by
/// <c>message-wire-fixtures.json</c>. A field-number or attribute change fails
/// here instead of against the live server.
/// </summary>
/// <remarks>
/// <para>
/// Fixture provenance: schemas schema-sourced field-for-field from Baileys MIT
/// <c>WAProto/WAProto.proto</c> at commit 0af2386 (read 2026-10-10); every
/// pinned byte is this repository's own protoc output over the hand-written
/// <c>Proto/message.proto</c> plus this repository's own
/// <c>BinaryNodeCodec</c> encoding — no whatsmeow bytes anywhere.
/// </para>
/// <para>
/// Output discipline (V6/T-4-05): assertions compare hex strings of wire data
/// and the fixed public inputs only — message text never enters an assertion
/// message beyond the fixed fixture inputs it already pins.
/// </para>
/// </remarks>
public sealed class MessageWireFixtureTests
{
    private sealed record FixtureText(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("protobufHex")] string ProtobufHex);

    private sealed record FixtureQuote(
        [property: JsonPropertyName("replyText")] string ReplyText,
        [property: JsonPropertyName("stanzaId")] string StanzaId,
        [property: JsonPropertyName("participant")] string Participant,
        [property: JsonPropertyName("quotedText")] string QuotedText,
        [property: JsonPropertyName("remoteJid")] string RemoteJid,
        [property: JsonPropertyName("protobufHex")] string ProtobufHex);

    private sealed record FixtureNode(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("to")] string To,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("t")] string T,
        [property: JsonPropertyName("deviceJid")] string DeviceJid,
        [property: JsonPropertyName("encType")] string EncType,
        [property: JsonPropertyName("ciphertextHex")] string CiphertextHex,
        [property: JsonPropertyName("nodeHex")] string NodeHex);

    private sealed record WireFixtures(
        [property: JsonPropertyName("provenance")] string Provenance,
        [property: JsonPropertyName("plainAscii")] FixtureText PlainAscii,
        [property: JsonPropertyName("plainNonAscii")] FixtureText PlainNonAscii,
        [property: JsonPropertyName("quote")] FixtureQuote Quote,
        [property: JsonPropertyName("messageNode")] FixtureNode MessageNode);

    private static WireFixtures LoadFixtures()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "message-wire-fixtures.json");
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<WireFixtures>(json)
            ?? throw new InvalidOperationException("Message wire fixture failed to deserialize.");
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    [Fact]
    public void fixture_provenance_names_the_baileys_schema_source()
    {
        WireFixtures fixtures = LoadFixtures();

        fixtures.Provenance.Should().Contain("Baileys");
    }

    [Fact]
    public void conversation_plain_ascii_pins_protobuf_bytes()
    {
        WireFixtures fixtures = LoadFixtures();

        byte[] plaintext = MessageBuilder.BuildPlainText(fixtures.PlainAscii.Text);

        Hex(plaintext).Should().Be(
            fixtures.PlainAscii.ProtobufHex,
            $"wire bytes for input of length {fixtures.PlainAscii.Text.Length}");
        Message parsed = MessageBuilder.ParseMessage(plaintext);
        parsed.Conversation.Should().Be(fixtures.PlainAscii.Text);
        parsed.ExtendedTextMessage.Should().BeNull();
    }

    [Fact]
    public void conversation_non_ascii_pins_utf8_protobuf_bytes_without_normalization()
    {
        WireFixtures fixtures = LoadFixtures();

        byte[] plaintext = MessageBuilder.BuildPlainText(fixtures.PlainNonAscii.Text);

        Hex(plaintext).Should().Be(
            fixtures.PlainNonAscii.ProtobufHex,
            $"wire bytes for input of length {fixtures.PlainNonAscii.Text.Length}");
        Encoding.UTF8.GetBytes(fixtures.PlainNonAscii.Text).Length.Should()
            .BeGreaterThan(fixtures.PlainNonAscii.Text.Length, "the fixture input is multi-byte UTF-8");
        Message parsed = MessageBuilder.ParseMessage(plaintext);
        parsed.Conversation.Should().Be(fixtures.PlainNonAscii.Text);
    }

    [Fact]
    public void extended_text_quote_pins_protobuf_bytes_with_context_info()
    {
        WireFixtures fixtures = LoadFixtures();
        FixtureQuote quote = fixtures.Quote;

        byte[] plaintext = MessageBuilder.BuildQuotedText(
            quote.ReplyText,
            new QuotedMessageRef(quote.StanzaId, quote.Participant, quote.QuotedText, quote.RemoteJid));

        Hex(plaintext).Should().Be(quote.ProtobufHex, "quote payload bytes are pinned");
        Message parsed = MessageBuilder.ParseMessage(plaintext);
        parsed.Conversation.Should().BeEmpty("a quote-carrying payload never rides conversation");
        parsed.ExtendedTextMessage.Should().NotBeNull();
        parsed.ExtendedTextMessage!.Text.Should().Be(quote.ReplyText);
        parsed.ExtendedTextMessage.ContextInfo.StanzaId.Should().Be(quote.StanzaId);
        parsed.ExtendedTextMessage.ContextInfo.Participant.Should().Be(quote.Participant);
        parsed.ExtendedTextMessage.ContextInfo.RemoteJID.Should().Be(quote.RemoteJid);
        parsed.ExtendedTextMessage.ContextInfo.QuotedMessage.Should().NotBeNull();
        parsed.ExtendedTextMessage.ContextInfo.QuotedMessage!.Conversation.Should().Be(quote.QuotedText);
    }

    [Fact]
    public void node_roundtrip_enc_child_data_is_byte_identical_to_pinned_ciphertext()
    {
        WireFixtures fixtures = LoadFixtures();
        FixtureNode pinned = fixtures.MessageNode;
        byte[] ciphertext = Convert.FromHexString(pinned.CiphertextHex);

        BinaryNode node = MessageBuilder.AssembleMessageNode(
            Jid.Parse(pinned.To),
            pinned.Id,
            pinned.Type,
            long.Parse(pinned.T),
            [new DeviceCiphertext(Jid.Parse(pinned.DeviceJid), pinned.EncType, ciphertext)]);
        byte[] encoded = BinaryNodeCodec.Encode(node);

        Hex(encoded).Should().Be(pinned.NodeHex, $"wire bytes for message {pinned.Id}");
        BinaryNode decoded = BinaryNodeCodec.Decode(encoded);
        decoded.Tag.Should().Be("message");
        decoded.Attrs["id"].Should().Be(pinned.Id);
        decoded.Attrs["to"].Should().Be(pinned.To);
        decoded.Attrs["type"].Should().Be(pinned.Type);
        decoded.Attrs["t"].Should().Be(pinned.T);
        decoded.Children.Should().HaveCount(1);
        BinaryNode recipient = decoded.Children![0];
        recipient.Tag.Should().Be("to");
        recipient.Attrs["jid"].Should().Be(pinned.DeviceJid);
        recipient.Children.Should().HaveCount(1);
        BinaryNode enc = recipient.Children![0];
        enc.Tag.Should().Be("enc");
        enc.Attrs["v"].Should().Be("2");
        enc.Attrs["type"].Should().Be(pinned.EncType);
        enc.Text.Should().BeNull("ciphertext never rides node text (Pitfall 8)");
        enc.Data.Should().NotBeNull();
        Hex(enc.Data!).Should().Be(pinned.CiphertextHex, "the decoded enc Data equals the pinned ciphertext");
        BinaryNodeCodec.Encode(decoded).Should().Equal(encoded, "the node round-trips byte-identically");
    }

    [Fact]
    public void cross_contamination_quote_never_parses_as_plain_conversation()
    {
        WireFixtures fixtures = LoadFixtures();

        Message fromQuote = MessageBuilder.ParseMessage(Convert.FromHexString(fixtures.Quote.ProtobufHex));
        Message fromPlain = MessageBuilder.ParseMessage(Convert.FromHexString(fixtures.PlainAscii.ProtobufHex));

        fromQuote.Conversation.Should().BeEmpty("quote payloads carry no conversation field");
        fromQuote.ExtendedTextMessage.Should().NotBeNull();
        fromPlain.ExtendedTextMessage.Should().BeNull("plain payloads carry no extended-text field");
        fromPlain.Conversation.Should().NotBeEmpty();
    }

    [Fact]
    public void builder_rejects_null_empty_and_malformed_inputs_fail_closed()
    {
        Action nullText = () => MessageBuilder.BuildPlainText(null!);
        Action emptyText = () => MessageBuilder.BuildPlainText(string.Empty);
        Action nullQuoteText = () => MessageBuilder.BuildQuotedText(
            null!, new QuotedMessageRef("id", null, "quoted", null));
        Action nullDestination = () => MessageBuilder.AssembleMessageNode(
            null!, "id", "text", 1720000000L,
            [new DeviceCiphertext(Jid.Parse("2222222222:3@s.whatsapp.net"), "msg", [0x01])]);
        Action emptyId = () => MessageBuilder.AssembleMessageNode(
            Jid.Parse("2222222222@s.whatsapp.net"), string.Empty, "text", 1720000000L,
            [new DeviceCiphertext(Jid.Parse("2222222222:3@s.whatsapp.net"), "msg", [0x01])]);
        Action emptyDevices = () => MessageBuilder.AssembleMessageNode(
            Jid.Parse("2222222222@s.whatsapp.net"), "id", "text", 1720000000L, []);

        nullText.Should().Throw<SdkFormatException>("no empty message reaches the wire");
        emptyText.Should().Throw<SdkFormatException>();
        nullQuoteText.Should().Throw<SdkFormatException>();
        nullDestination.Should().Throw<SdkFormatException>();
        emptyId.Should().Throw<SdkFormatException>();
        emptyDevices.Should().Throw<SdkFormatException>();
    }
}
