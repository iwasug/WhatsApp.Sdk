using System.Text;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Errors;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Codec;

/// <summary>
/// Codec proofs (CONN-04 framing input validation, T-2-06/T-2-13): every content form
/// round-trips byte-identically through the token-dictionary codec, hand-pinned goldens
/// lock exact bytes, and every evil input throws a rule-naming
/// <see cref="SdkFormatException"/> without embedding node bytes.
/// </summary>
public sealed class BinaryNodeRoundTripTests
{
    [Fact]
    public void Encode_pins_hand_computed_bytes_for_a_contentless_ping()
    {
        // 'ping' is single-byte token 86 (0x56): LIST_8(1) + tag.
        byte[] encoded = BinaryNodeCodec.Encode(BinaryNode.Create("ping"));

        Convert.ToHexString(encoded).Should().Be("F80156");
    }

    [Fact]
    public void Encode_pins_hand_computed_bytes_for_token_attrs()
    {
        // iq=25, id=8, '1'=85, type=4, get=41; list size 2*2+1=5.
        var node = BinaryNode.Create("iq", new Dictionary<string, string>
        {
            ["id"] = "1",
            ["type"] = "get",
        });

        Convert.ToHexString(BinaryNodeCodec.Encode(node)).Should().Be("F8051908550429");
    }

    [Fact]
    public void Encode_then_decode_round_trips_text_content_byte_identically()
    {
        // Nibble-form text takes the string path, so Text survives the round trip.
        var node = BinaryNode.FromText("body", "1234567890", new Dictionary<string, string>
        {
            ["id"] = "ABCD-1234",
            ["type"] = "text",
        });

        byte[] encoded = BinaryNodeCodec.Encode(node);
        BinaryNode decoded = BinaryNodeCodec.Decode(encoded);

        AssertNodesEqual(node, decoded);
        BinaryNodeCodec.Encode(decoded).Should().Equal(encoded);
    }

    [Fact]
    public void Encode_coerces_raw_string_content_to_bytes_per_reference_behavior()
    {
        // "hello world!" takes no string form (not a token, nibble, hex, or JID), so it
        // encodes as BINARY_8 raw — which decodes as byte content, exactly like the
        // reference implementation. Only token/nibble/hex/JID strings survive as Text.
        var node = BinaryNode.FromText("message", "hello world!");

        BinaryNode decoded = BinaryNodeCodec.Decode(BinaryNodeCodec.Encode(node));

        decoded.Text.Should().BeNull();
        decoded.Data.Should().Equal(Encoding.UTF8.GetBytes("hello world!"));
    }

    [Fact]
    public void Encode_then_decode_round_trips_byte_content_byte_identically()
    {
        var node = BinaryNode.FromBytes("media", [0x00, 0x01, 0x02, 0xFF, 0x80], new Dictionary<string, string>
        {
            ["type"] = "image",
        });

        byte[] encoded = BinaryNodeCodec.Encode(node);
        BinaryNode decoded = BinaryNodeCodec.Decode(encoded);

        AssertNodesEqual(node, decoded);
        BinaryNodeCodec.Encode(decoded).Should().Equal(encoded);
    }

    [Fact]
    public void Encode_then_decode_round_trips_nested_children_byte_identically()
    {
        var node = BinaryNode.FromChildren("iq", new[]
        {
            BinaryNode.FromChildren("query", new[]
            {
                BinaryNode.FromText("ping", "1"),
            }, new Dictionary<string, string> { ["xmlns"] = "w:p" }),
            BinaryNode.Create("passive"),
        }, new Dictionary<string, string>
        {
            ["id"] = "q-7",
            ["type"] = "get",
            ["to"] = "s.whatsapp.net",
        });

        byte[] encoded = BinaryNodeCodec.Encode(node);
        BinaryNode decoded = BinaryNodeCodec.Decode(encoded);

        AssertNodesEqual(node, decoded);
        BinaryNodeCodec.Encode(decoded).Should().Equal(encoded);
    }

    [Fact]
    public void Encode_then_decode_round_trips_all_string_encodings()
    {
        // Token, double-byte token, nibble, uppercase-hex, JID pair, AD-JID, raw,
        // and empty-string attr values each take a different wire path.
        var node = BinaryNode.Create("iq", new Dictionary<string, string>
        {
            ["single"] = "get",
            ["double"] = "protocol",
            ["nibble"] = "1234567890-.",
            ["hex"] = "ABCDEF0123456789",
            ["pair"] = "123@s.whatsapp.net",
            ["ad"] = "123:4@s.whatsapp.net",
            ["raw"] = "hello world! zzz",
            ["empty"] = string.Empty,
            ["custom-key-zzz"] = "custom-value-zzz",
        });

        byte[] encoded = BinaryNodeCodec.Encode(node);
        BinaryNode decoded = BinaryNodeCodec.Decode(encoded);

        AssertNodesEqual(node, decoded);
        BinaryNodeCodec.Encode(decoded).Should().Equal(encoded);
    }

    [Fact]
    public void Decode_maps_jid_forms_to_their_canonical_strings()
    {
        var pair = BinaryNode.FromText("to", "123@s.whatsapp.net");
        var ad = BinaryNode.FromText("to", "123:4@s.whatsapp.net");

        BinaryNodeCodec.Decode(BinaryNodeCodec.Encode(pair)).Text.Should().Be("123@s.whatsapp.net");
        BinaryNodeCodec.Decode(BinaryNodeCodec.Encode(ad)).Text.Should().Be("123:4@s.whatsapp.net");
    }

    [Fact]
    public void Decode_normalizes_empty_children_to_a_contentless_node()
    {
        // LIST_EMPTY as the content tag decodes to an empty list upstream; the approved
        // BinaryNode shape normalizes that to content-less (FromChildren rule).
        byte[] wire = [0xF8, 0x02, 0x56, 0x00];
        BinaryNode decoded = BinaryNodeCodec.Decode(wire);

        decoded.Tag.Should().Be("ping");
        decoded.Children.Should().BeNull();
        decoded.Text.Should().BeNull();
        decoded.Data.Should().BeNull();
        BinaryNodeCodec.Encode(decoded).Should().Equal([0xF8, 0x01, 0x56]);
    }

    [Fact]
    public void TryDecode_returns_true_for_valid_bytes_and_false_for_garbage()
    {
        byte[] valid = BinaryNodeCodec.Encode(BinaryNode.Create("ping"));

        BinaryNodeCodec.TryDecode(valid, out BinaryNode? node).Should().BeTrue();
        node.Should().NotBeNull();
        node!.Tag.Should().Be("ping");

        BinaryNodeCodec.TryDecode([0xF0], out BinaryNode? rejected).Should().BeFalse();
        rejected.Should().BeNull();

        BinaryNodeCodec.TryDecode([], out BinaryNode? empty).Should().BeFalse();
        empty.Should().BeNull();
    }

    [Fact]
    public void Decode_throws_rule_naming_errors_for_every_truncation_of_a_valid_encoding()
    {
        var node = BinaryNode.FromChildren("iq", new[]
        {
            BinaryNode.FromText("body", "sekret-bytes-zzz"),
        }, new Dictionary<string, string> { ["id"] = "1" });
        byte[] encoded = BinaryNodeCodec.Encode(node);

        encoded.Length.Should().BeGreaterThan(4);
        for (int cut = 1; cut < encoded.Length; cut++)
        {
            byte[] prefix = encoded[..cut];
            Func<BinaryNode> decode = () => BinaryNodeCodec.Decode(prefix);

            decode.Should().Throw<SdkFormatException>().WithMessage("*truncated*");
            try
            {
                decode();
            }
            catch (SdkFormatException ex)
            {
                ex.Message.Should().NotContain("sekret-bytes-zzz");
            }
        }
    }

    [Fact]
    public void Decode_throws_on_an_unrecognized_string_tag()
    {
        // 0xF0 (240) is no single token, dictionary selector, list, binary, JID, or
        // packed form — an unrecognized string tag.
        Func<BinaryNode> decode = () => BinaryNodeCodec.Decode([0xF8, 0x01, 0xF0]);

        decode.Should().Throw<SdkFormatException>().WithMessage("*token tag*");
    }

    [Fact]
    public void Decode_throws_on_a_truncated_nibble_form()
    {
        // Content NIBBLE_8 claiming two packed bytes with only one present.
        Func<BinaryNode> decode = () => BinaryNodeCodec.Decode([0xF8, 0x02, 0x56, 0xFF, 0x02, 0x12]);

        decode.Should().Throw<SdkFormatException>().WithMessage("*truncated*");
    }

    [Fact]
    public void Decode_throws_on_an_invalid_nibble_value()
    {
        // NIBBLE_8, even length one byte: high nibble 12 is outside the nibble alphabet.
        Func<BinaryNode> decode = () => BinaryNodeCodec.Decode([0xF8, 0x02, 0x56, 0xFF, 0x01, 0xC0]);

        decode.Should().Throw<SdkFormatException>().WithMessage("*nibble*");
    }

    [Fact]
    public void Decode_throws_on_a_jid_pair_with_an_empty_server()
    {
        Func<BinaryNode> decode = () => BinaryNodeCodec.Decode([0xF8, 0x02, 0x56, 0xFA, 0x00, 0x00]);

        decode.Should().Throw<SdkFormatException>().WithMessage("*JID pair*");
    }

    [Fact]
    public void Decode_throws_without_allocating_for_a_giant_binary32_length()
    {
        // BINARY_32 declaring ~4 GiB with no payload behind it: the length check fires
        // against the buffered remainder before any allocation.
        Func<BinaryNode> decode = () => BinaryNodeCodec.Decode([0xF8, 0x02, 0x56, 0xFE, 0xFF, 0xFF, 0xFF, 0xFF]);

        decode.Should().Throw<SdkFormatException>().WithMessage("*truncated*");
    }

    [Fact]
    public void Decode_resolves_duplicate_attribute_keys_last_wins()
    {
        // Wire duplicates resolve last-wins like the reference implementation (the
        // BinaryNode unique-keys invariant still holds on the parsed result).
        // id=8, '1'=85, '2'=69.
        BinaryNode decoded = BinaryNodeCodec.Decode([0xF8, 0x05, 0x56, 0x08, 0x55, 0x08, 0x45]);

        decoded.Tag.Should().Be("ping");
        decoded.Attrs["id"].Should().Be("2");
    }

    [Fact]
    public void TokenDictionary_pins_table_sizes_and_spot_encodings()
    {
        TokenDictionary.SingleByteTokens.Length.Should().Be(236);
        TokenDictionary.DoubleByteTokens.Length.Should().Be(4);
        foreach (string[] table in TokenDictionary.DoubleByteTokens)
        {
            table.Length.Should().Be(256);
        }

        TokenDictionary.SingleByteTokens[86].Should().Be("ping");
        TokenDictionary.SingleByteTokens[25].Should().Be("iq");
        TokenDictionary.DoubleByteTokens[0][3].Should().Be("protocol");

        TokenDictionary.TryGetToken("iq", out TokenDictionary.TokenRef single).Should().BeTrue();
        single.Dict.Should().Be(-1);
        single.Index.Should().Be(25);

        TokenDictionary.TryGetToken("protocol", out TokenDictionary.TokenRef dbl).Should().BeTrue();
        dbl.Dict.Should().Be(0);
        dbl.Index.Should().Be(3);

        TokenDictionary.TryGetToken("no-such-token-zzz", out _).Should().BeFalse();
    }

    [Fact]
    public void TokenDictionary_bounds_checks_throw_rule_naming_errors()
    {
        Func<string> singleLow = () => TokenDictionary.GetSingleByteToken(0);
        Func<string> singleHigh = () => TokenDictionary.GetSingleByteToken(236);
        Func<string> dictHigh = () => TokenDictionary.GetDoubleByteToken(4, 0);

        singleLow.Should().Throw<SdkFormatException>().WithMessage("*token table range*");
        singleHigh.Should().Throw<SdkFormatException>().WithMessage("*token table range*");
        dictHigh.Should().Throw<SdkFormatException>().WithMessage("*token table range*");
    }

    private static void AssertNodesEqual(BinaryNode expected, BinaryNode actual)
    {
        actual.Tag.Should().Be(expected.Tag);
        actual.Attrs.Should().BeEquivalentTo(expected.Attrs);

        if (expected.Children is not null)
        {
            actual.Children.Should().NotBeNull();
            actual.Children!.Count.Should().Be(expected.Children.Count);
            foreach ((BinaryNode want, BinaryNode got) in expected.Children.Zip(actual.Children!))
            {
                AssertNodesEqual(want, got);
            }
        }
        else
        {
            actual.Children.Should().BeNull();
        }

        actual.Text.Should().Be(expected.Text);
        if (expected.Data is null)
        {
            actual.Data.Should().BeNull();
        }
        else
        {
            actual.Data.Should().NotBeNull();
            actual.Data!.Should().Equal(expected.Data);
        }
    }
}
