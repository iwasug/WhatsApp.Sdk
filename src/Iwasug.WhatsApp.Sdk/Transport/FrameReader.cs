using System.Buffers;
using System.IO.Compression;
using System.IO.Pipelines;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Noise;

namespace Iwasug.WhatsApp.Sdk.Transport;

/// <summary>
/// Stateless WhatsApp frame parser: 3-byte big-endian length prefix, then the payload
/// whose first byte is the compression flag (<c>0x02</c> = zlib-inflate the rest,
/// otherwise strip the <c>0x00</c> prefix) per 02-SPIKE-NOTES.md F3/O5.
/// </summary>
/// <remarks>
/// <para>
/// Static pure parser over <see cref="ReadOnlySequence{T}"/> (the Pipelines fill/parse
/// loop shape from 02-RESEARCH.md): <see cref="TryReadFrame"/> parses ALL complete
/// frames per <c>ReadAsync</c> and suspends (returns <c>false</c>, buffer untouched)
/// on partials — one socket message may carry multiple frames or a frame fragment, so
/// fragmentation is structural, not an edge case (Pitfall 3).
/// </para>
/// <para>
/// V5 input validation: <see cref="NoiseProtocol.MaxFrameBytes"/> is enforced BEFORE
/// allocating — a length prefix above the cap throws (the connection aborts; it is
/// never allocated), and zlib output is bounded by the same cap against bombs. Every
/// malformed input throws <see cref="SdkTransportException"/> naming the rule only —
/// frame bytes never enter messages (message discipline, T-01-01/T-01-12).
/// </para>
/// </remarks>
internal static class FrameReader
{
    private const int PrefixWidth = 3;
    private const int CompressedFlag = 0x02;

    /// <summary>
    /// Parses one complete frame from the head of <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">
    /// The buffered bytes; advanced past the consumed frame on success, untouched when
    /// more data is needed.
    /// </param>
    /// <param name="frame">
    /// The deframed payload (flag stripped, inflated when flagged) on success;
    /// <c>null</c> when suspended.
    /// </param>
    /// <returns>
    /// <c>true</c> when a complete frame was parsed; <c>false</c> when the buffer
    /// holds only a partial frame and the caller must read more before retrying.
    /// </returns>
    /// <exception cref="SdkTransportException">
    /// The length prefix exceeds the maximum frame size, the payload is empty, or the
    /// compression envelope is invalid. The message names the rule only.
    /// </exception>
    internal static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out byte[]? frame)
    {
        frame = null;
        if (buffer.Length < PrefixWidth)
        {
            return false;
        }

        Span<byte> prefix = stackalloc byte[PrefixWidth];
        buffer.Slice(0, PrefixWidth).CopyTo(prefix);
        int length = (prefix[0] << 16) | (prefix[1] << 8) | prefix[2];

        if (length > NoiseProtocol.MaxFrameBytes)
        {
            throw new SdkTransportException("Frame length exceeds the maximum frame size.");
        }

        if (buffer.Length - PrefixWidth < length)
        {
            return false;
        }

        if (length == 0)
        {
            throw new SdkTransportException("Frame payload must not be empty.");
        }

        // The length is now proven to fit the buffered remainder, so this allocation
        // is bounded by bytes already in hand — never by an untrusted prefix alone.
        byte[] payload = buffer.Slice(PrefixWidth, length).ToArray();
        buffer = buffer.Slice(PrefixWidth + length);

        frame = (payload[0] & CompressedFlag) != 0
            ? Inflate(payload.AsSpan(1))
            : payload[1..];
        return true;
    }

    /// <summary>
    /// Fill/parse loop: reads <paramref name="reader"/> to completion, collecting every
    /// complete frame (golden-fixture driver and the 02-05 pump's shape reference).
    /// </summary>
    /// <param name="reader">The frame byte source.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All complete frames in arrival order.</returns>
    /// <exception cref="SdkTransportException">
    /// An evil length, an invalid compression envelope, or a partial frame stranded by
    /// transport close.
    /// </exception>
    internal static async Task<List<byte[]>> ReadAllAsync(PipeReader reader, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(reader);

        var frames = new List<byte[]>();
        while (true)
        {
            ReadResult read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = read.Buffer;
            while (TryReadFrame(ref buffer, out byte[]? frame))
            {
                frames.Add(frame!);
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
            if (read.IsCompleted)
            {
                if (!buffer.IsEmpty)
                {
                    throw new SdkTransportException("Frame stream ended with a partial frame.");
                }

                break;
            }
        }

        return frames;
    }

    private static byte[] Inflate(ReadOnlySpan<byte> compressed)
    {
        try
        {
            using var input = new MemoryStream(compressed.ToArray(), writable: false);
            using var inflater = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var chunk = new byte[8192];
            int total = 0;
            int read;
            while ((read = inflater.Read(chunk, 0, chunk.Length)) > 0)
            {
                total += read;
                if (total > NoiseProtocol.MaxFrameBytes)
                {
                    throw new SdkTransportException("Inflated frame exceeds the maximum frame size.");
                }

                output.Write(chunk, 0, read);
            }

            return output.ToArray();
        }
        catch (SdkTransportException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException || ex is IOException)
        {
            throw new SdkTransportException("Frame compression envelope is invalid.", ex);
        }
    }
}
