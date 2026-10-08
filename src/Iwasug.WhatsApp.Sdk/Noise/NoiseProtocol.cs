namespace Iwasug.WhatsApp.Sdk.Noise;

/// <summary>
/// Wire constants for the WhatsApp Noise XX variant, confirmed read-only against the
/// MIT-licensed Baileys sources in <c>02-SPIKE-NOTES.md</c> (F1–F3, F5). These values
/// are inputs to the handshake (02-03) and framing (02-04) — they are never negotiated.
/// </summary>
/// <remarks>
/// <para>
/// Fail-closed rule: a peer that disagrees on <see cref="ProtocolName"/> (after
/// null-padding to <see cref="PaddedProtocolNameLength"/> bytes) or on the
/// <c>WA</c> header / <see cref="DictVersion"/> bytes aborts the handshake — the SDK
/// never attempts a second cipher suite or dictionary version (F1/F2 fallbacks).
/// </para>
/// <para>
/// Provenance: cipher suite and header bytes from Baileys <c>src/Defaults/index.ts</c>
/// (<c>NOISE_MODE</c>, <c>NOISE_WA_HEADER</c>) at commit <c>0af2386</c>; framing width
/// (3-byte big-endian) from <c>src/Utils/noise-handler.ts</c> (<c>encodeFrame</c> /
/// <c>processData</c>). No token-dictionary bytes live here — they land in 02-04 with
/// NOTICE attribution. No whatsmeow (MPL-2.0) source was consulted.
/// </para>
/// </remarks>
internal static class NoiseProtocol
{
    /// <summary>
    /// Noise protocol name for the WhatsApp variant: <c>Noise_XX_25519_AESGCM_SHA256</c>.
    /// On the wire it is null-padded to <see cref="PaddedProtocolNameLength"/> bytes
    /// (F1: 28-char name + 4 <c>0x00</c>). The STACK.md ChaChaPoly line is superseded
    /// for this path — the transport cipher is BCL <c>AesGcm</c>; NSec is needed for
    /// X25519 key agreement only.
    /// </summary>
    internal const string ProtocolName = "Noise_XX_25519_AESGCM_SHA256";

    /// <summary>
    /// Null-padded construction length of <see cref="ProtocolName"/> in bytes: the ASCII
    /// name bytes followed by <c>0x00</c> up to exactly 32 bytes (F1).
    /// </summary>
    internal const int PaddedProtocolNameLength = 32;

    /// <summary>
    /// Connection intro header (F2/F5): <c>'W','A', protocol 6, dictionary 3</c>
    /// (<c>[0x57, 0x41, 0x06, 0x03]</c>). Message-A opens with these bytes followed by a
    /// 3-byte big-endian length and the <c>HandshakeMessage</c> protobuf — never a bare
    /// pubkey (F2 fail-closed fallback).
    /// </summary>
    internal static ReadOnlySpan<byte> WaHeader => [0x57, 0x41, 0x06, 0x03];

    /// <summary>
    /// Pinned token-dictionary version carried in <see cref="WaHeader"/> (F1/O5).
    /// A peer advertising any other dictionary version fails closed with a loud log
    /// (02-RESEARCH Pitfall table: downgrade via dictionary mismatch).
    /// </summary>
    internal const int DictVersion = 3;

    /// <summary>
    /// Maximum accepted frame payload in bytes (16 MiB, per 02-RESEARCH Pitfall 3).
    /// Decoders enforce this BEFORE allocating for an untrusted length: a length prefix
    /// above the cap aborts the connection — it is never allocated (V5 input validation).
    /// </summary>
    internal const int MaxFrameBytes = 16 * 1024 * 1024;
}
