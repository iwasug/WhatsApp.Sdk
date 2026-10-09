using Iwasug.WhatsApp.Sdk.Errors;

namespace Iwasug.WhatsApp.Sdk.Domain;

/// <summary>
/// Immutable parsed JID value type (D-16): construction parses, the components are
/// exposed, and <see cref="ToString"/> always reconstructs the canonical string.
/// Not a raw string, not a class hierarchy.
/// </summary>
/// <remarks>
/// <para>
/// Canonical grammar (Phase-1 planner decision): <c>user[.agent[.device]]@server</c> —
/// split on the LAST <c>@</c> (the local part may contain dots but never <c>@</c>); the
/// optional local-part suffix is an alphanumeric <c>.agent</c> token, optionally followed
/// by a plain numeric <c>.device</c> (a device implies an agent). <see cref="Parse"/>
/// accepts only inputs whose canonical reconstruction equals the input, so
/// <c>Parse(x).ToString() == x</c> holds for every accepted value; malformed input
/// throws <see cref="SdkFormatException"/> — there is no silently-constructed Jid.
/// </para>
/// <para>
/// Case sensitivity (resolved encoding edge): JIDs are compared by exact string equality
/// of the canonical form — ordinal, no Unicode normalization, no grapheme folding, no
/// case folding. <c>"A@s.whatsapp.net"</c> and <c>"a@s.whatsapp.net"</c> are distinct
/// values and unequal records.
/// </para>
/// <para>
/// Scope guard (Pitfall 6 / D-15): this type carries no wire vocabulary — no node trees,
/// message ids, or receipt types. Server-set validation, <c>@lid</c> handling, and deeper
/// device/agent canonicalization arrive with Phases 2/4; Phase 1 ships parse, reject, and
/// round-trip.
/// </para>
/// </remarks>
public sealed record class Jid
{
    /// <summary>The user part (phone/LID portion) of the JID, without agent or device.</summary>
    public string User { get; }

    /// <summary>The server part of the JID (e.g. <c>s.whatsapp.net</c>).</summary>
    public string Server { get; }

    /// <summary>The optional agent component of the local part, when present.</summary>
    public string? Agent { get; }

    /// <summary>The optional device component of the local part, when present.</summary>
    public int? Device { get; }

    private Jid(string user, string server, string? agent, int? device)
    {
        User = user;
        Server = server;
        Agent = agent;
        Device = device;
    }

    /// <summary>
    /// Parses a canonical JID string into its components.
    /// </summary>
    /// <param name="value">Canonical JID string: <c>user[.agent[.device]]@server</c>.</param>
    /// <returns>The parsed, immutable JID.</returns>
    /// <exception cref="SdkFormatException">
    /// <paramref name="value"/> is null, empty, whitespace-only, contains control
    /// characters or whitespace, lacks an <c>@</c>, has an empty user or server part,
    /// contains more than one <c>@</c>, has an empty or non-alphanumeric agent token, or
    /// has a device component that is not a plain (non-zero-padded) number. The message
    /// names the violated rule only — the offending value is never embedded (message
    /// discipline, T-01-01/T-01-06).
    /// </exception>
    public static Jid Parse(string value)
    {
        if (value is null)
        {
            throw new SdkFormatException("JID value must not be null.");
        }

        if (value.Length == 0)
        {
            throw new SdkFormatException("JID value must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new SdkFormatException("JID value must not be whitespace-only.");
        }

        if (ContainsWhitespace(value))
        {
            throw new SdkFormatException("JID value must not contain whitespace.");
        }

        if (ContainsControlCharacter(value))
        {
            throw new SdkFormatException("JID value must not contain control characters.");
        }

        int separator = value.LastIndexOf('@');
        if (separator < 0)
        {
            throw new SdkFormatException("JID value must contain an '@' separator between the user part and the server.");
        }

        if (separator == 0)
        {
            throw new SdkFormatException("JID user part must not be empty.");
        }

        string local = value[..separator];
        string server = value[(separator + 1)..];

        if (local.Contains('@', StringComparison.Ordinal))
        {
            throw new SdkFormatException("JID value must contain exactly one '@' separator.");
        }

        if (server.Length == 0)
        {
            throw new SdkFormatException("JID server part must not be empty.");
        }

        string[] segments = local.Split('.');
        return segments.Length switch
        {
            1 => Create(segments[0], server, null, null, value),
            2 => ParseAgentSuffix(segments, server, value),
            _ => ParseAgentDeviceSuffix(segments, server, value),
        };
    }

    /// <summary>
    /// Attempts to parse a canonical JID string without throwing on malformed input.
    /// </summary>
    /// <param name="value">Candidate JID string.</param>
    /// <param name="jid">The parsed JID when parsing succeeds; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> when <paramref name="value"/> parsed; <c>false</c> otherwise.</returns>
    public static bool TryParse(string value, out Jid? jid)
    {
        try
        {
            jid = Parse(value);
            return true;
        }
        catch (SdkFormatException)
        {
            jid = null;
            return false;
        }
    }

    /// <summary>
    /// Reconstructs the canonical JID string: <c>User[.Agent[.Device]]@Server</c>.
    /// Round-trips exactly for every value <see cref="Parse"/> accepts.
    /// </summary>
    public override string ToString()
    {
        // Canonical reconstruction (D-16): never Unicode-normalized, never case-folded.
        string local = Agent is null
            ? User
            : Device is null
                ? $"{User}.{Agent}"
                : $"{User}.{Agent}.{Device.Value}";
        return $"{local}@{Server}";
    }

    private static Jid ParseAgentSuffix(string[] segments, string server, string value)
    {
        string user = segments[0];
        string agent = segments[1];

        if (user.Length == 0)
        {
            throw new SdkFormatException("JID user part must not be empty.");
        }

        if (agent.Length == 0)
        {
            throw new SdkFormatException("JID agent component must not be empty.");
        }

        if (!IsAlphanumeric(agent))
        {
            throw new SdkFormatException("JID agent component must be alphanumeric.");
        }

        return Create(user, server, agent, null, value);
    }

    private static Jid ParseAgentDeviceSuffix(string[] segments, string server, string value)
    {
        string user = string.Join('.', segments[..^2]);
        string agent = segments[^2];
        string deviceText = segments[^1];

        if (user.Length == 0)
        {
            throw new SdkFormatException("JID user part must not be empty.");
        }

        if (agent.Length == 0)
        {
            throw new SdkFormatException("JID agent component must not be empty.");
        }

        if (!IsAlphanumeric(agent))
        {
            throw new SdkFormatException("JID agent component must be alphanumeric.");
        }

        if (!IsPlainNumber(deviceText))
        {
            throw new SdkFormatException("JID device component must be a plain number without leading zeros.");
        }

        if (!int.TryParse(deviceText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int device))
        {
            throw new SdkFormatException("JID device component is outside the supported numeric range.");
        }

        return Create(user, server, agent, device, value);
    }

    private static Jid Create(string user, string server, string? agent, int? device, string value)
    {
        var jid = new Jid(user, server, agent, device);

        // Round-trip invariant (T-01-06): Parse only accepts inputs whose canonical
        // reconstruction is byte-identical to the input — a component-splitting bug can
        // never ship a value whose ToString() differs from what was parsed.
        if (!string.Equals(jid.ToString(), value, StringComparison.Ordinal))
        {
            throw new SdkFormatException("JID value is not in canonical form.");
        }

        return jid;
    }

    private static bool IsAlphanumeric(string value)
    {
        foreach (char c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPlainNumber(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        if (value.Length > 1 && value[0] == '0')
        {
            return false; // zero-padded devices are non-canonical — deferred to Phase 2/4.
        }

        foreach (char c in value)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsWhitespace(string value)
    {
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsControlCharacter(string value)
    {
        foreach (char c in value)
        {
            if (char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }
}
