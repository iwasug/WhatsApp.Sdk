using System.Buffers.Binary;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Noise;

namespace Iwasug.WhatsApp.Sdk.Transport;

/// <summary>
/// Shared WhatsApp framing for handshake and encrypted pump payloads: a three-byte
/// big-endian body length with no flag interpretation.
/// </summary>
internal static class WaFrame
{
    private const int PrefixWidth = 3;

    internal static byte[] Frame(ReadOnlySpan<byte> body)
    {
        if (body.Length > NoiseProtocol.MaxFrameBytes)
        {
            throw new SdkTransportException("Frame body exceeds the maximum frame size.");
        }

        byte[] framed = new byte[PrefixWidth + body.Length];
        framed[0] = (byte)(body.Length >> 16);
        framed[1] = (byte)(body.Length >> 8);
        framed[2] = (byte)body.Length;
        body.CopyTo(framed.AsSpan(PrefixWidth));
        return framed;
    }

    internal static ReadOnlySpan<byte> Deframe(ReadOnlySpan<byte> framed)
    {
        if (framed.Length < PrefixWidth)
        {
            throw new SdkTransportException("Frame is truncated before its length prefix.");
        }

        int length = (framed[0] << 16) | (framed[1] << 8) | framed[2];
        if (length > NoiseProtocol.MaxFrameBytes)
        {
            throw new SdkTransportException("Frame length exceeds the maximum frame size.");
        }

        if (framed.Length - PrefixWidth < length)
        {
            throw new SdkTransportException("Frame is truncated before its declared payload.");
        }

        if (framed.Length - PrefixWidth != length)
        {
            throw new SdkTransportException("Frame contains trailing bytes after its declared payload.");
        }

        return framed.Slice(PrefixWidth, length);
    }
}
