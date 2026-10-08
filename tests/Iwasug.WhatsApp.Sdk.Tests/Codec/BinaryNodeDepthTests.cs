using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Errors;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Codec;

/// <summary>
/// Adversarial nesting and child-count proofs for the binary node decoder.
/// </summary>
public sealed class BinaryNodeDepthTests
{
    [Fact]
    public void Decode_accepts_a_64_deep_single_child_chain()
    {
        BinaryNode node = CreateSingleChildChain(64);

        BinaryNode decoded = BinaryNodeCodec.Decode(BinaryNodeCodec.Encode(node));

        GetDepth(decoded).Should().Be(64);
    }

    [Fact]
    public void Decode_rejects_a_200_deep_chain_with_the_nesting_depth_rule()
    {
        BinaryNode node = CreateSingleChildChain(200);
        Func<BinaryNode> decode = () => BinaryNodeCodec.Decode(BinaryNodeCodec.Encode(node));

        decode.Should()
            .Throw<SdkFormatException>()
            .WithMessage("Encoded node exceeds the maximum nesting depth.");
    }

    [Fact]
    public void Decode_round_trips_a_300_child_list16_node()
    {
        BinaryNode node = BinaryNode.FromChildren(
            "parent",
            Enumerable.Range(0, 300).Select(index => BinaryNode.Create($"child-{index}")));

        BinaryNode decoded = BinaryNodeCodec.Decode(BinaryNodeCodec.Encode(node));

        decoded.Tag.Should().Be("parent");
        decoded.Children.Should().NotBeNull();
        decoded.Children!.Count.Should().Be(300);
        decoded.Children.Select(child => child.Tag).Should().Equal(
            Enumerable.Range(0, 300).Select(index => $"child-{index}"));
    }

    [Fact]
    public void Decode_rejects_a_truncated_maximum_list16_count_with_the_truncation_rule()
    {
        // Root node: LIST_8(2), tag "ping", then LIST_16(65535) with no child bytes.
        byte[] wire = [0xF8, 0x02, 0x56, 0xF9, 0xFF, 0xFF];
        Func<BinaryNode> decode = () => BinaryNodeCodec.Decode(wire);

        decode.Should()
            .Throw<SdkFormatException>()
            .WithMessage("Encoded node is truncated.");
    }

    private static BinaryNode CreateSingleChildChain(int depth)
    {
        BinaryNode current = BinaryNode.Create("leaf");
        for (int i = 1; i < depth; i++)
        {
            current = BinaryNode.FromChildren($"node-{i}", [current]);
        }

        return current;
    }

    private static int GetDepth(BinaryNode node)
    {
        int depth = 1;
        BinaryNode? current = node;
        while (current.Children is not null)
        {
            depth++;
            current = current.Children[0];
        }

        return depth;
    }
}
