using System.Buffers;
using System.IO.Compression;
using System.IO.Pipelines;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Noise;
using Iwasug.WhatsApp.Sdk.Transport;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Transport;

/// <summary>
/// Framing proofs (CONN-04, T-2-06, Pitfall 3): all complete frames parsed per read,
/// partials suspended, the zlib flag inflated, and evil lengths aborted before any
/// allocation — all over the spike-confirmed 3-byte big-endian prefix.
/// </summary>
public sealed class FrameReaderTests
{
    // Hand-pinned golden: node iq{id=1,type=get} is F8 05 19 08 55 04 29
    // (BinaryNodeRoundTripTests); framed plain it is prefix(8) + 0x00 + node bytes.
    private static readonly byte[] GoldenNodeBytes = [0xF8, 0x05, 0x19, 0x08, 0x55, 0x04, 0x29];
    private static readonly byte[] GoldenFrame =
        [0x00, 0x00, 0x08, 0x00, 0xF8, 0x05, 0x19, 0x08, 0x55, 0x04, 0x29];

    [Fact]
    public void TryReadFrame_pins_golden_frame_bytes()
    {
        var buffer = new ReadOnlySequence<byte>(GoldenFrame);

        FrameReader.TryReadFrame(ref buffer, out byte[]? frame).Should().BeTrue();
        frame.Should().Equal(GoldenNodeBytes);
        buffer.Length.Should().Be(0);
    }

    [Fact]
    public async Task ReadAllAsync_parses_all_coalesced_frames_per_read()
    {
        byte[] first = Frame(GoldenNodeBytes);
        byte[] second = Frame(GoldenNodeBytes);
        byte[] coalesced = [.. first, .. second];
        Pipe pipe = Feed([coalesced]);

        List<byte[]> frames = await FrameReader.ReadAllAsync(pipe.Reader, CancellationToken.None);

        frames.Count.Should().Be(2);
        frames[0].Should().Equal(GoldenNodeBytes);
        frames[1].Should().Equal(GoldenNodeBytes);
    }

    [Fact]
    public async Task ReadAllAsync_reassembles_split_byte_by_byte_delivery()
    {
        byte[] framed = Frame(GoldenNodeBytes);
        byte[][] chunks = new byte[framed.Length][];
        for (int i = 0; i < framed.Length; i++)
        {
            chunks[i] = [framed[i]];
        }

        Pipe pipe = Feed(chunks);

        List<byte[]> frames = await FrameReader.ReadAllAsync(pipe.Reader, CancellationToken.None);

        frames.Count.Should().Be(1);
        frames[0].Should().Equal(GoldenNodeBytes);
    }

    [Fact]
    public void TryReadFrame_suspends_on_a_partial_prefix_leaving_the_buffer_untouched()
    {
        var buffer = new ReadOnlySequence<byte>([0x00, 0x00]);

        FrameReader.TryReadFrame(ref buffer, out byte[]? frame).Should().BeFalse();
        frame.Should().BeNull();
        buffer.Length.Should().Be(2);
    }

    [Fact]
    public void TryReadFrame_suspends_on_a_partial_payload()
    {
        byte[] framed = Frame(GoldenNodeBytes);
        var buffer = new ReadOnlySequence<byte>(framed[..5]);

        FrameReader.TryReadFrame(ref buffer, out byte[]? frame).Should().BeFalse();
        frame.Should().BeNull();
        buffer.Length.Should().Be(5);
    }

    [Fact]
    public void TryReadFrame_suspends_on_a_max_prefix_without_allocating()
    {
        // The 3-byte width caps encodable lengths at 0xFFFFFF (16 MiB - 1), one below
        // the 16 MiB cap — so a hostile max prefix only parks (no throw, no
        // allocation from the prefix alone). Cap enforcement that CAN fire lives on
        // the inflate path (bomb test below) and stays as defense-in-depth here.
        var buffer = new ReadOnlySequence<byte>([0xFF, 0xFF, 0xFF]);

        FrameReader.TryReadFrame(ref buffer, out byte[]? frame).Should().BeFalse();
        frame.Should().BeNull();
        buffer.Length.Should().Be(3);
    }

    [Fact]
    public async Task ReadAllAsync_throws_on_a_partial_frame_stranded_by_close()
    {
        byte[] framed = Frame(GoldenNodeBytes);
        Pipe pipe = Feed([framed[..5]]);

        Func<Task> read = () => FrameReader.ReadAllAsync(pipe.Reader, CancellationToken.None);

        (await read.Should().ThrowAsync<SdkTransportException>()).WithMessage("*partial frame*");
    }

    [Fact]
    public void TryReadFrame_throws_on_an_empty_payload_frame()
    {
        var buffer = new ReadOnlySequence<byte>([0x00, 0x00, 0x00]);

        Func<bool> read = () => FrameReader.TryReadFrame(ref buffer, out _);

        read.Should().Throw<SdkTransportException>().WithMessage("*must not be empty*");
    }

    [Fact]
    public async Task ReadAllAsync_inflates_the_zlib_flag()
    {
        Pipe pipe = Feed([Frame(GoldenNodeBytes, compress: true)]);

        List<byte[]> frames = await FrameReader.ReadAllAsync(pipe.Reader, CancellationToken.None);

        frames.Count.Should().Be(1);
        frames[0].Should().Equal(GoldenNodeBytes);
    }

    [Fact]
    public void TryReadFrame_throws_on_a_corrupt_compression_envelope()
    {
        byte[] payload = [0x02, 0x11, 0x22, 0x33, 0x44];
        byte[] framed = [(byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length, .. payload];
        var buffer = new ReadOnlySequence<byte>(framed);

        Func<bool> read = () => FrameReader.TryReadFrame(ref buffer, out _);

        read.Should().Throw<SdkTransportException>().WithMessage("*compression envelope*");
    }

    [Fact]
    public async Task TryReadFrame_bounds_zlib_bombs_by_the_same_cap()
    {
        // 17 MiB of zeros compresses to kilobytes on the wire but must still abort
        // when the inflated output crosses the cap.
        byte[] zeros = new byte[NoiseProtocol.MaxFrameBytes + 1024 * 1024];
        Pipe pipe = Feed([Frame(zeros, compress: true)]);

        Func<Task> read = () => FrameReader.ReadAllAsync(pipe.Reader, CancellationToken.None);

        (await read.Should().ThrowAsync<SdkTransportException>()).WithMessage("*maximum frame size*");
    }

    private static byte[] Frame(byte[] nodeBytes, bool compress = false)
    {
        byte[] payload;
        if (compress)
        {
            using var output = new MemoryStream();
            output.WriteByte(0x02);
            using (var deflater = new ZLibStream(output, CompressionMode.Compress, leaveOpen: true))
            {
                deflater.Write(nodeBytes, 0, nodeBytes.Length);
            }

            payload = output.ToArray();
        }
        else
        {
            payload = [0x00, .. nodeBytes];
        }

        return [(byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length, .. payload];
    }

    private static Pipe Feed(byte[][] chunks)
    {
        var pipe = new Pipe();
        foreach (byte[] chunk in chunks)
        {
            pipe.Writer.Write(chunk);
        }

        pipe.Writer.Complete();
        return pipe;
    }
}
