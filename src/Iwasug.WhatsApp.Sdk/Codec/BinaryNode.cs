using System.Collections.ObjectModel;
using System.Text;
using Iwasug.WhatsApp.Sdk.Errors;

namespace Iwasug.WhatsApp.Sdk.Codec;

/// <summary>
/// Immutable parsed binary node value type (D-16): construction validates, the
/// components are exposed, and <see cref="ToString"/> always renders the XML
/// debug view. Not a dictionary, not a class hierarchy.
/// </summary>
/// <remarks>
/// <para>
/// Shape reference: the Baileys MIT <c>BinaryNode</c> TypeScript type
/// (<c>src/WABinary/types.ts</c>: <c>tag</c>, <c>attrs</c>, <c>content</c> of
/// children-list | string | bytes) — independently written C#, behavioral
/// reference only, no whatsmeow (MPL-2.0) bytes (NOTICE.md provenance rule).
/// </para>
/// <para>
/// Content forms: at most one of <see cref="Children"/>, <see cref="Text"/>, or
/// <see cref="Data"/> is set; all three <c>null</c> is a content-less node
/// (e.g. a ping). The static factories enforce this — there is no invalid
/// BinaryNode. An empty children list decodes to the same wire shape as a
/// content-less node, so <see cref="FromChildren"/> normalizes it.
/// </para>
/// <para>
/// Equality is the default record equality: <see cref="Tag"/> compares by value;
/// <see cref="Attrs"/>, <see cref="Children"/>, and <see cref="Data"/> compare
/// by reference (read-only wrappers and arrays do not override equality).
/// Structural comparison is the codec/tests' job via explicit
/// <c>SequenceEqual</c>-style asserts, never <c>==</c>.
/// </para>
/// <para>
/// Scope guard (Jid-style): this type carries no wire vocabulary beyond Phase 2
/// needs — no stanza routing, message ids, receipt types, or query correlation
/// (the router owns ids), no byte encoding (BinaryNodeCodec owns bytes), no
/// framing (FrameReader owns frames). Phase 4 message shapes build ON this type;
/// changing it later breaks every call site — this shape is one-way (D-16).
/// </para>
/// <para>
/// Companion convention (Jid.TryParse precedent):
/// <c>BinaryNodeCodec.Decode</c> throws <see cref="SdkFormatException"/> naming
/// the violated rule (never embedding node bytes), and
/// <c>BinaryNodeCodec.TryDecode</c> catches it — implemented with the codec in
/// Task 2, locked here so the shape never grows a second parsing entry point.
/// </para>
/// </remarks>
public sealed record class BinaryNode
{
    /// <summary>The node tag (element name), never empty.</summary>
    public string Tag { get; }

    /// <summary>
    /// Node attributes with ordinal key comparison, never <c>null</c> (empty
    /// when the node carries none). Defensively copied at construction.
    /// </summary>
    public IReadOnlyDictionary<string, string> Attrs { get; }

    /// <summary>Child nodes, when this node carries children; otherwise <c>null</c>.</summary>
    public IReadOnlyList<BinaryNode>? Children { get; }

    /// <summary>String content, when this node carries text; otherwise <c>null</c>.</summary>
    public string? Text { get; }

    /// <summary>
    /// Opaque byte content, when this node carries bytes; otherwise <c>null</c>.
    /// Defensively copied at construction — treat as read-only afterwards.
    /// </summary>
    public byte[]? Data { get; }

    private BinaryNode(
        string tag,
        IReadOnlyDictionary<string, string> attrs,
        IReadOnlyList<BinaryNode>? children,
        string? text,
        byte[]? data)
    {
        int forms = (children is null ? 0 : 1) + (text is null ? 0 : 1) + (data is null ? 0 : 1);
        if (forms > 1)
        {
            throw new SdkFormatException("Binary node content must use at most one form: children, text, or bytes.");
        }

        Tag = tag;
        Attrs = attrs;
        Children = children;
        Text = text;
        Data = data;
    }

    /// <summary>
    /// Creates a content-less node (e.g. a ping stanza).
    /// </summary>
    /// <param name="tag">Node tag (element name).</param>
    /// <param name="attrs">Node attributes; <c>null</c> means none.</param>
    /// <returns>The immutable node.</returns>
    /// <exception cref="SdkFormatException">
    /// <paramref name="tag"/> is null or empty, an attribute key or value is
    /// null, or attribute keys are not unique. The message names the violated
    /// rule only — the offending value is never embedded (message discipline,
    /// T-01-01/T-01-06).
    /// </exception>
    public static BinaryNode Create(string tag, IReadOnlyDictionary<string, string>? attrs = null)
    {
        return new BinaryNode(ThrowIfInvalidTag(tag), CopyAttrs(attrs), null, null, null);
    }

    /// <summary>
    /// Creates a node carrying child nodes.
    /// </summary>
    /// <param name="tag">Node tag (element name).</param>
    /// <param name="children">Child nodes; an empty sequence normalizes to a content-less node.</param>
    /// <param name="attrs">Node attributes; <c>null</c> means none.</param>
    /// <returns>The immutable node.</returns>
    /// <exception cref="SdkFormatException">
    /// <paramref name="tag"/> is null or empty, <paramref name="children"/> is
    /// null or contains a null child, an attribute key or value is null, or
    /// attribute keys are not unique. The message names the violated rule only.
    /// </exception>
    public static BinaryNode FromChildren(
        string tag,
        IEnumerable<BinaryNode> children,
        IReadOnlyDictionary<string, string>? attrs = null)
    {
        if (children is null)
        {
            throw new SdkFormatException("Binary node children must not be null.");
        }

        BinaryNode[] copied = children.ToArray();
        foreach (BinaryNode child in copied)
        {
            if (child is null)
            {
                throw new SdkFormatException("Binary node children must not contain null.");
            }
        }

        IReadOnlyList<BinaryNode>? content = copied.Length == 0 ? null : copied;
        return new BinaryNode(ThrowIfInvalidTag(tag), CopyAttrs(attrs), content, null, null);
    }

    /// <summary>
    /// Creates a node carrying string content.
    /// </summary>
    /// <param name="tag">Node tag (element name).</param>
    /// <param name="text">String content.</param>
    /// <param name="attrs">Node attributes; <c>null</c> means none.</param>
    /// <returns>The immutable node.</returns>
    /// <exception cref="SdkFormatException">
    /// <paramref name="tag"/> is null or empty, <paramref name="text"/> is null,
    /// an attribute key or value is null, or attribute keys are not unique. The
    /// message names the violated rule only.
    /// </exception>
    public static BinaryNode FromText(
        string tag,
        string text,
        IReadOnlyDictionary<string, string>? attrs = null)
    {
        if (text is null)
        {
            throw new SdkFormatException("Binary node text content must not be null.");
        }

        return new BinaryNode(ThrowIfInvalidTag(tag), CopyAttrs(attrs), null, text, null);
    }

    /// <summary>
    /// Creates a node carrying opaque byte content.
    /// </summary>
    /// <param name="tag">Node tag (element name).</param>
    /// <param name="data">Byte content; copied at construction.</param>
    /// <param name="attrs">Node attributes; <c>null</c> means none.</param>
    /// <returns>The immutable node.</returns>
    /// <exception cref="SdkFormatException">
    /// <paramref name="tag"/> is null or empty, an attribute key or value is
    /// null, or attribute keys are not unique. The message names the violated
    /// rule only.
    /// </exception>
    public static BinaryNode FromBytes(
        string tag,
        ReadOnlySpan<byte> data,
        IReadOnlyDictionary<string, string>? attrs = null)
    {
        return new BinaryNode(ThrowIfInvalidTag(tag), CopyAttrs(attrs), null, null, data.ToArray());
    }

    /// <summary>
    /// Renders the XML debug view in the binaryNodeToString spirit (own
    /// implementation): <c>&lt;tag attr="value"&gt;…&lt;/tag&gt;</c>, self-closing
    /// when content-less, byte content shown as its length only.
    /// </summary>
    /// <remarks>
    /// Byte payloads are never dumped (message discipline — debug views end up in
    /// logs) and markup characters are escaped, so this method never throws for
    /// any node the factories accept.
    /// </remarks>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append('<');
        sb.Append(Tag);
        foreach (KeyValuePair<string, string> attr in Attrs)
        {
            sb.Append(' ');
            EscapeXmlInto(sb, attr.Key);
            sb.Append("=\"");
            EscapeXmlInto(sb, attr.Value);
            sb.Append('"');
        }

        if (Children is null && Text is null && Data is null)
        {
            sb.Append("/>");
            return sb.ToString();
        }

        sb.Append('>');
        if (Children is not null)
        {
            foreach (BinaryNode child in Children)
            {
                sb.Append(child.ToString());
            }
        }
        else if (Text is not null)
        {
            EscapeXmlInto(sb, Text);
        }
        else if (Data is not null)
        {
            sb.Append('<');
            sb.Append(Data.Length);
            sb.Append(" bytes>");
        }
        else
        {
            // Unreachable via the factories (the private constructor enforces at
            // most one form, and this branch needs exactly none set past the
            // content-less check above) — defensive so rendering stays total.
        }

        sb.Append("</");
        sb.Append(Tag);
        sb.Append('>');
        return sb.ToString();
    }

    private static string ThrowIfInvalidTag(string tag)
    {
        if (tag is null)
        {
            throw new SdkFormatException("Binary node tag must not be null.");
        }

        if (tag.Length == 0)
        {
            throw new SdkFormatException("Binary node tag must not be empty.");
        }

        return tag;
    }

    private static IReadOnlyDictionary<string, string> CopyAttrs(IReadOnlyDictionary<string, string>? attrs)
    {
        if (attrs is null)
        {
            return new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var copied = new Dictionary<string, string>(attrs.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> kvp in attrs)
        {
            if (kvp.Key is null)
            {
                throw new SdkFormatException("Binary node attribute keys must not be null.");
            }

            if (kvp.Value is null)
            {
                throw new SdkFormatException("Binary node attribute values must not be null.");
            }

            try
            {
                copied.Add(kvp.Key, kvp.Value);
            }
            catch (ArgumentException ex)
            {
                throw new SdkFormatException("Binary node attribute keys must be unique.", ex);
            }
        }

        return new ReadOnlyDictionary<string, string>(copied);
    }

    private static void EscapeXmlInto(StringBuilder sb, string value)
    {
        foreach (char c in value)
        {
            switch (c)
            {
                case '&':
                    sb.Append("&amp;");
                    break;
                case '<':
                    sb.Append("&lt;");
                    break;
                case '>':
                    sb.Append("&gt;");
                    break;
                case '"':
                    sb.Append("&quot;");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
    }
}
