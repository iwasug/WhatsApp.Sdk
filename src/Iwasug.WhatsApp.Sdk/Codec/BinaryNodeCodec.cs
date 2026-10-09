using System.Text;
using Iwasug.WhatsApp.Sdk.Errors;

namespace Iwasug.WhatsApp.Sdk.Codec;

/// <summary>
/// Stateless token-dictionary codec between <see cref="BinaryNode"/> values and their
/// wire bytes: <c>Encode</c> renders a node, <c>Decode</c> parses it back.
/// </summary>
/// <remarks>
/// <para>
/// Pure functions over spans (RESEARCH.md Architecture Patterns): no shared mutable
/// state, so concurrent per-account connections may encode/decode without
/// synchronization. The token table itself lives in <see cref="TokenDictionary"/>
/// (Baileys MIT port with NOTICE attribution); this file holds only the independently
/// written composition — tag constants, list framing, nibble/hex packing, and JID
/// forms — derived from the documented protocol behavior. No whatsmeow (MPL-2.0)
/// source was consulted.
/// </para>
/// <para>
/// Layer split (02-SPIKE-NOTES.md O5): the compression-flag byte (<c>0x00</c> plain /
/// <c>0x02</c> zlib) belongs to <c>Transport.FrameReader</c>, which strips or inflates
/// it before node parsing. These functions see flag-free node bytes only — unlike the
/// reference <c>decodeBinaryNode</c>, which takes the flagged buffer. The split is
/// behavior-equivalent and keeps each layer's evil-input surface reviewable in one
/// file.
/// </para>
/// <para>
/// Two upstream quirks are replicated deliberately for byte parity (the reference
/// implementation is the debugging lifeline for wire-compat work): an agent-only JID
/// (<c>user_agent@server</c>, no device) encodes as a plain JID pair with the agent
/// dropped; a string content that takes no string form (not a token, nibble, hex, or
/// JID) encodes as raw bytes and therefore decodes as byte content, not text —
/// only token/nibble/hex/JID strings survive the round trip as <c>Text</c>; and
/// duplicate attribute keys on the wire resolve last-wins on read while construction
/// still rejects duplicates.
/// </para>
/// <para>
/// Message discipline (T-01-01/T-01-12, extended to the wire): every malformed input
/// throws <see cref="SdkFormatException"/> naming the violated rule — never embedding
/// node bytes. Lengths are validated against the buffered remainder BEFORE allocating.
/// </para>
/// </remarks>
public static class BinaryNodeCodec
{
    private const int ListEmpty = 0;
    private const int Dictionary0 = 236;
    private const int Dictionary1 = 237;
    private const int Dictionary2 = 238;
    private const int Dictionary3 = 239;
    private const int InteropJid = 245;
    private const int FbJid = 246;
    private const int AdJid = 247;
    private const int List8 = 248;
    private const int List16 = 249;
    private const int JidPair = 250;
    private const int Hex8 = 251;
    private const int Binary8 = 252;
    private const int Binary20 = 253;
    private const int Binary32 = 254;
    private const int Nibble8 = 255;
    private const int PackedMax = 127;
    internal const int MaxNestedDepth = 64;

    /// <summary>
    /// Renders a node to its canonical wire bytes (flag-free; framing is the
    /// transport's job).
    /// </summary>
    /// <param name="node">The node to encode.</param>
    /// <returns>The canonical encoding; decoding it returns an equal node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> is null.</exception>
    public static byte[] Encode(BinaryNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var buffer = new List<byte>();
        EncodeNode(node, buffer);
        return [.. buffer];
    }

    /// <summary>
    /// Parses flag-free wire bytes back into a node.
    /// </summary>
    /// <param name="data">The encoded node bytes.</param>
    /// <returns>The parsed, immutable node.</returns>
    /// <exception cref="SdkFormatException">
    /// The bytes are empty, truncated, or violate any wire rule (bad list size tag,
    /// token overrun, truncated packed/string form, bad JID form). The message names
    /// the rule only — no node bytes are embedded.
    /// </exception>
    public static BinaryNode Decode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            throw new SdkFormatException("Encoded node must not be empty.");
        }

        var reader = new WireReader(data);
        return reader.ReadNode();
    }

    /// <summary>
    /// Attempts to parse flag-free wire bytes without throwing on malformed input
    /// (the <c>Jid.TryParse</c> companion precedent).
    /// </summary>
    /// <param name="data">Candidate encoded node bytes.</param>
    /// <param name="node">The parsed node when decoding succeeds; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> when <paramref name="data"/> decoded; <c>false</c> otherwise.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> data, out BinaryNode? node)
    {
        try
        {
            node = Decode(data);
            return true;
        }
        catch (SdkFormatException)
        {
            node = null;
            return false;
        }
    }

    private static void EncodeNode(BinaryNode node, List<byte> buffer)
    {
        bool hasContent = node.Children is not null || node.Text is not null || node.Data is not null;
        WriteListStart(buffer, 2 * node.Attrs.Count + 1 + (hasContent ? 1 : 0));
        WriteString(buffer, node.Tag);

        foreach (KeyValuePair<string, string> attr in node.Attrs)
        {
            WriteString(buffer, attr.Key);
            WriteString(buffer, attr.Value);
        }

        if (node.Text is not null)
        {
            WriteString(buffer, node.Text);
        }
        else if (node.Data is not null)
        {
            WriteByteLength(buffer, node.Data.Length);
            buffer.AddRange(node.Data);
        }
        else if (node.Children is not null)
        {
            WriteListStart(buffer, node.Children.Count);
            foreach (BinaryNode child in node.Children)
            {
                EncodeNode(child, buffer);
            }
        }
    }

    private static void WriteListStart(List<byte> buffer, int size)
    {
        if (size == 0)
        {
            buffer.Add(ListEmpty);
        }
        else if (size < 256)
        {
            buffer.Add(List8);
            buffer.Add((byte)size);
        }
        else
        {
            buffer.Add(List16);
            buffer.Add((byte)((size >> 8) & 0xFF));
            buffer.Add((byte)(size & 0xFF));
        }
    }

    private static void WriteString(List<byte> buffer, string? value)
    {
        if (value is null)
        {
            buffer.Add(ListEmpty);
            return;
        }

        if (value.Length == 0)
        {
            WriteRawString(buffer, value);
            return;
        }

        if (TokenDictionary.TryGetToken(value, out TokenDictionary.TokenRef token))
        {
            if (token.Dict >= 0)
            {
                buffer.Add((byte)(Dictionary0 + token.Dict));
            }

            buffer.Add((byte)token.Index);
        }
        else if (IsNibble(value))
        {
            WritePackedBytes(buffer, value, Nibble8);
        }
        else if (IsHex(value))
        {
            WritePackedBytes(buffer, value, Hex8);
        }
        else if (JidDecode(value) is { } jid)
        {
            WriteJid(buffer, jid);
        }
        else
        {
            WriteRawString(buffer, value);
        }
    }

    private static void WriteJid(List<byte> buffer, DecodedJid jid)
    {
        if (jid.Device.HasValue)
        {
            buffer.Add(AdJid);
            buffer.Add((byte)(jid.DomainType & 0xFF));
            buffer.Add((byte)(jid.Device.Value & 0xFF));
            WriteString(buffer, jid.User);
        }
        else
        {
            // Upstream quirk (replicated for byte parity): the agent component is not
            // representable in the pair form, so an agent-only JID degrades to a plain
            // pair with the agent dropped.
            buffer.Add(JidPair);
            if (jid.User.Length == 0)
            {
                buffer.Add(ListEmpty);
            }
            else
            {
                WriteString(buffer, jid.User);
            }

            WriteString(buffer, jid.Server);
        }
    }

    private static void WriteRawString(List<byte> buffer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteByteLength(buffer, bytes.Length);
        buffer.AddRange(bytes);
    }

    private static void WriteByteLength(List<byte> buffer, int length)
    {
        if (length >= 1 << 20)
        {
            buffer.Add(Binary32);
            buffer.Add((byte)((length >> 24) & 0xFF));
            buffer.Add((byte)((length >> 16) & 0xFF));
            buffer.Add((byte)((length >> 8) & 0xFF));
            buffer.Add((byte)(length & 0xFF));
        }
        else if (length >= 256)
        {
            buffer.Add(Binary20);
            buffer.Add((byte)((length >> 16) & 0x0F));
            buffer.Add((byte)((length >> 8) & 0xFF));
            buffer.Add((byte)(length & 0xFF));
        }
        else
        {
            buffer.Add(Binary8);
            buffer.Add((byte)length);
        }
    }

    private static void WritePackedBytes(List<byte> buffer, string value, int tag)
    {
        buffer.Add((byte)tag);

        int byteCount = (value.Length + 1) / 2;
        buffer.Add((byte)(byteCount | (value.Length % 2 == 0 ? 0 : 0x80)));

        for (int i = 0; i < value.Length / 2; i++)
        {
            buffer.Add((byte)((PackChar(value[2 * i], tag) << 4) | PackChar(value[2 * i + 1], tag)));
        }

        if (value.Length % 2 != 0)
        {
            buffer.Add((byte)((PackChar(value[^1], tag) << 4) | PackChar('\0', tag)));
        }
    }

    private static int PackChar(char c, int tag)
    {
        if (tag == Nibble8)
        {
            return c switch
            {
                '-' => 10,
                '.' => 11,
                '\0' => 15,
                >= '0' and <= '9' => c - '0',
                _ => throw new SdkFormatException("Packed string contains a character outside the nibble alphabet."),
            };
        }

        return c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'A' and <= 'F' => 10 + (c - 'A'),
            >= 'a' and <= 'f' => 10 + (c - 'a'),
            '\0' => 15,
            _ => throw new SdkFormatException("Packed string contains a character outside the hex alphabet."),
        };
    }

    private static bool IsNibble(string value)
    {
        if (value.Length == 0 || value.Length > PackedMax)
        {
            return false;
        }

        foreach (char c in value)
        {
            if ((c < '0' || c > '9') && c != '-' && c != '.')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsHex(string value)
    {
        if (value.Length == 0 || value.Length > PackedMax)
        {
            return false;
        }

        foreach (char c in value)
        {
            if ((c < '0' || c > '9') && (c < 'A' || c > 'F'))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record DecodedJid(string User, string Server, int DomainType, int? Device);

    private static DecodedJid? JidDecode(string value)
    {
        int separator = value.IndexOf('@');
        if (separator < 0)
        {
            return null;
        }

        string userCombined = value[..separator];
        string server = value[(separator + 1)..];

        string userAgent = userCombined;
        int? device = null;
        int colon = userCombined.IndexOf(':');
        if (colon >= 0)
        {
            userAgent = userCombined[..colon];
            string deviceText = userCombined[(colon + 1)..];
            if (deviceText.Length != 0)
            {
                // Upstream leniency (replicated): a non-numeric device degrades to 0
                // via `|| 0` on the AD-JID branch.
                device = int.TryParse(deviceText, out int parsed) ? parsed : 0;
            }
        }

        string user = userAgent;
        string? agent = null;
        int underscore = userAgent.IndexOf('_');
        if (underscore >= 0)
        {
            user = userAgent[..underscore];
            agent = userAgent[(underscore + 1)..];
        }

        int domainType = server switch
        {
            "lid" => 1,
            "hosted" => 128,
            "hosted.lid" => 129,
            _ => agent is not null && int.TryParse(agent, out int agentType) ? agentType : 0,
        };

        return new DecodedJid(user, server, domainType, device);
    }

    /// <summary>
    /// Cursor decoder over flag-free node bytes. Every bounds failure throws a
    /// rule-naming <see cref="SdkFormatException"/> before any allocation beyond the
    /// exact remaining length.
    /// </summary>
    private ref struct WireReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _index;

        internal WireReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _index = 0;
        }

        internal BinaryNode ReadNode(int depth = 0)
        {
            if (depth > MaxNestedDepth)
            {
                throw new SdkFormatException("Encoded node exceeds the maximum nesting depth.");
            }

            int listSize = ReadListSize(ReadByte());
            string header = ReadString(ReadByte(), depth);
            if (listSize == 0 || header.Length == 0)
            {
                throw new SdkFormatException("Encoded node must carry a tag.");
            }

            var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
            int attributeCount = (listSize - 1) >> 1;
            for (int i = 0; i < attributeCount; i++)
            {
                string key = ReadString(ReadByte(), depth);
                string attrValue = ReadString(ReadByte(), depth);
                attrs[key] = attrValue;
            }

            string? text = null;
            byte[]? bytes = null;
            List<BinaryNode>? childList = null;

            if (listSize % 2 == 0)
            {
                int tag = ReadByte();
                if (tag == ListEmpty || tag == List8 || tag == List16)
                {
                    int count = ReadListSize(tag);
                    int initialCapacity = tag == List16 ? Math.Min(count, 32) : count;
                    childList = new List<BinaryNode>(initialCapacity);
                    for (int i = 0; i < count; i++)
                    {
                        childList.Add(ReadNode(depth + 1));
                    }
                }
                else
                {
                    switch (tag)
                    {
                        case Binary8:
                            bytes = ReadBytes(ReadByte());
                            break;
                        case Binary20:
                            bytes = ReadBytes(ReadInt20());
                            break;
                        case Binary32:
                            bytes = ReadBytes(ReadInt32());
                            break;
                        default:
                            text = ReadString(tag, depth);
                            break;
                    }
                }
            }

            if (childList is not null)
            {
                return BinaryNode.FromChildren(header, childList, attrs);
            }

            if (bytes is not null)
            {
                return BinaryNode.FromBytes(header, bytes, attrs);
            }

            if (text is not null)
            {
                return BinaryNode.FromText(header, text, attrs);
            }

            return BinaryNode.Create(header, attrs);
        }

        private byte ReadByte()
        {
            CheckRemaining(1);
            return _data[_index++];
        }

        private byte[] ReadBytes(int count)
        {
            CheckRemaining(count);
            byte[] result = _data.Slice(_index, count).ToArray();
            _index += count;
            return result;
        }

        private string ReadRawChars(int count)
        {
            CheckRemaining(count);
            string result = Encoding.UTF8.GetString(_data.Slice(_index, count));
            _index += count;
            return result;
        }

        private int ReadInt(int count)
        {
            CheckRemaining(count);
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                value |= _data[_index++] << ((count - 1 - i) * 8);
            }

            return value;
        }

        private int ReadInt20()
        {
            CheckRemaining(3);
            int value = ((_data[_index] & 0x0F) << 16) | (_data[_index + 1] << 8) | _data[_index + 2];
            _index += 3;
            return value;
        }

        private int ReadInt32()
        {
            CheckRemaining(4);
            uint value = ((uint)_data[_index] << 24) | ((uint)_data[_index + 1] << 16) | ((uint)_data[_index + 2] << 8) | _data[_index + 3];
            _index += 4;

            // The remainder is addressable memory, so a declared length beyond it is
            // truncation — and the check fires before any allocation. (A separate
            // range check is unnecessary: the remainder itself never exceeds
            // int.MaxValue, so anything beyond range is already beyond remainder.)
            if (value > (uint)(_data.Length - _index))
            {
                throw new SdkFormatException("Encoded node is truncated.");
            }

            return (int)value;
        }

        private int ReadListSize(int tag)
        {
            return tag switch
            {
                ListEmpty => 0,
                List8 => ReadByte(),
                List16 => ReadInt(2),
                _ => throw new SdkFormatException("Node list header uses an invalid size tag."),
            };
        }

        private string ReadString(int tag, int depth)
        {
            // The JID forms below recurse once per nesting level through this reader,
            // so the same depth budget that bounds ReadNode must bound them too —
            // otherwise a crafted JidPair chain costs one stack frame per input byte
            // and a few kilobytes exhaust the stack (uncatchable on modern .NET).
            if (depth > MaxNestedDepth)
            {
                throw new SdkFormatException("Encoded node exceeds the maximum nesting depth.");
            }

            if (tag >= 1 && tag < TokenDictionary.SingleByteTokens.Length)
            {
                return TokenDictionary.GetSingleByteToken(tag);
            }

            return tag switch
            {
                Dictionary0 or Dictionary1 or Dictionary2 or Dictionary3 => TokenDictionary.GetDoubleByteToken(tag - Dictionary0, ReadByte()),
                ListEmpty => string.Empty,
                Binary8 => ReadRawChars(ReadByte()),
                Binary20 => ReadRawChars(ReadInt20()),
                Binary32 => ReadRawChars(ReadInt32()),
                JidPair => ReadJidPair(depth),
                FbJid => ReadFbJid(depth),
                InteropJid => ReadInteropJid(depth),
                AdJid => ReadAdJid(depth),
                Hex8 or Nibble8 => ReadPacked(tag),
                _ => throw new SdkFormatException("String field uses an unrecognized token tag."),
            };
        }

        private string ReadJidPair(int depth)
        {
            string user = ReadString(ReadByte(), depth + 1);
            string server = ReadString(ReadByte(), depth + 1);
            if (server.Length == 0)
            {
                throw new SdkFormatException("JID pair form requires a non-empty server part.");
            }

            return user + "@" + server;
        }

        private string ReadAdJid(int depth)
        {
            int domainType = ReadByte();
            int device = ReadByte();
            string user = ReadString(ReadByte(), depth + 1);
            string server = domainType switch
            {
                1 => "lid",
                128 => "hosted",
                129 => "hosted.lid",
                _ => "s.whatsapp.net",
            };

            return device == 0 ? user + "@" + server : user + ":" + device + "@" + server;
        }

        private string ReadFbJid(int depth)
        {
            string user = ReadString(ReadByte(), depth + 1);
            int device = ReadInt(2);
            string server = ReadString(ReadByte(), depth + 1);
            return user + ":" + device + "@" + server;
        }

        private string ReadInteropJid(int depth)
        {
            string user = ReadString(ReadByte(), depth + 1);
            int device = ReadInt(2);
            int integrator = ReadInt(2);

            int rewind = _index;
            string server = "interop";
            try
            {
                server = ReadString(ReadByte(), depth + 1);
            }
            catch (SdkFormatException)
            {
                _index = rewind;
            }

            return integrator + "-" + user + ":" + device + "@" + server;
        }

        private string ReadPacked(int tag)
        {
            int start = ReadByte();
            int count = start & 0x7F;
            var sb = new StringBuilder(count * 2);
            for (int i = 0; i < count; i++)
            {
                int packed = ReadByte();
                sb.Append(UnpackChar(tag, (packed & 0xF0) >> 4));
                sb.Append(UnpackChar(tag, packed & 0x0F));
            }

            if (start >> 7 != 0 && sb.Length > 0)
            {
                sb.Length -= 1;
            }

            return sb.ToString();
        }

        private static char UnpackChar(int tag, int value)
        {
            if (tag == Nibble8)
            {
                return value switch
                {
                    >= 0 and <= 9 => (char)('0' + value),
                    10 => '-',
                    11 => '.',
                    15 => '\0',
                    _ => throw new SdkFormatException("Packed string contains an invalid nibble value."),
                };
            }

            // Wire nibbles are 4 bits, so value is always 0..15 here: 0-9 render as
            // digits, 10-15 as uppercase A-F (the reference unpackHex equivalent).
            return value < 10 ? (char)('0' + value) : (char)('A' + value - 10);
        }

        private void CheckRemaining(int count)
        {
            if (_index + count > _data.Length)
            {
                throw new SdkFormatException("Encoded node is truncated.");
            }
        }
    }
}
