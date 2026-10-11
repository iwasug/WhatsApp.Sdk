using System.Collections.Concurrent;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;

namespace Iwasug.WhatsApp.Sdk.Messaging;

/// <summary>
/// Resolves a group's participant list for SKDM fanout ONLY (04-05, MSG-02;
/// the node shape is pinned during implementation from Baileys'
/// <c>src/Socket/groups.ts</c> groupMetadata query — MIT, read-only, NOTICE row):
/// <c>&lt;iq type="get" xmlns="w:g2" to="{group}"&gt;&lt;query
/// request="interactive"/&gt;&lt;/iq&gt;</c>, reply
/// <c>&lt;iq type="result"&gt;&lt;group id="…"&gt;&lt;participant jid="…" /&gt;…&lt;/group&gt;&lt;/iq&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// SCOPE GUARD (Open Question 7 RESOLVED): participants-for-fanout only. Full group
/// read-side, membership sync, and admin surfaces are <b>GRPM-01, deferred to v2</b> —
/// this class never exposes metadata beyond the participant JIDs one group send
/// needs, and nothing here implements group notifications or membership changes.
/// </para>
/// <para>
/// Policy (DeviceDirectory TTL discipline, D-08): the participant cache is
/// process-local and SDK-owned — a restart re-queries; a typed ack <c>phash</c>
/// mismatch invalidates the entry. Responses are parsed bounds-checked (a
/// malformed participant child fails the whole resolution with a rule-only
/// <see cref="SdkFormatException"/> — no partial list); a reply without a
/// <c>group</c> node is the typed not-a-member failure, so a group send fails
/// closed with zero frames instead of fanning out to nowhere (T-4-16). Nothing
/// here logs.
/// </para>
/// </remarks>
internal sealed class GroupDirectory
{
    /// <summary>The group JID server token (e.g. <c>123-456@g.us</c>).</summary>
    internal const string GroupServer = "g.us";

    /// <summary>The <c>w:g2</c> group namespace (Baileys groupMetadata shape).</summary>
    internal const string GroupNamespace = "w:g2";

    /// <summary>The pinned query-kind attribute on the participants request.</summary>
    internal const string InteractiveRequest = "interactive";

    /// <summary>Default cache TTL (process-local; same SDK-owned policy as <see cref="DeviceDirectory.DefaultTtl"/>).</summary>
    internal static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly Func<QueryRouter?> _routerProvider;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _ttl;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (IReadOnlyList<Jid> Members, DateTimeOffset CachedAt)> _cache = new(StringComparer.Ordinal);

    /// <summary>Creates a directory issuing group queries through <paramref name="routerProvider"/> (per-attempt routers).</summary>
    internal GroupDirectory(Func<QueryRouter?> routerProvider, TimeProvider? timeProvider = null, TimeSpan? ttl = null)
    {
        _routerProvider = routerProvider ?? throw new ArgumentNullException(nameof(routerProvider));
        _clock = timeProvider ?? TimeProvider.System;
        _ttl = ttl ?? DefaultTtl;
    }

    /// <summary>
    /// Resolves the participant set for <paramref name="group"/>: a fresh cache
    /// entry wins with zero queries; an expired entry re-queries. Concurrent
    /// resolves serialize behind one gate so two group sends never race the cache.
    /// </summary>
    /// <param name="group">The group chat JID (<c>…@g.us</c>).</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>The participant JIDs in canonical form (may be empty — the caller fails typed).</returns>
    /// <exception cref="SdkFormatException">The reply lacks a <c>group</c> node (unknown group / not a member) or any participant child violates its shape rule.</exception>
    /// <exception cref="SdkConnectionException">No open channel (the router was absent) or the group query failed.</exception>
    internal async Task<IReadOnlyList<Jid>> ResolveAsync(Jid group, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string key = group.ToString();
            if (_cache.TryGetValue(key, out (IReadOnlyList<Jid> Members, DateTimeOffset CachedAt) hit)
                && _clock.GetUtcNow() - hit.CachedAt < _ttl)
            {
                return hit.Members;
            }

            IReadOnlyList<Jid> members = await QueryParticipantsAsync(group, cancellationToken).ConfigureAwait(false);
            _cache[key] = (members, _clock.GetUtcNow());
            return members;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Drops the cached participant set for <paramref name="group"/> (a group ack's
    /// <c>phash</c> mismatch means the member device set moved — the next resolve
    /// re-queries).
    /// </summary>
    internal void Invalidate(Jid group)
    {
        ArgumentNullException.ThrowIfNull(group);
        _cache.TryRemove(group.ToString(), out _);
    }

    private async Task<IReadOnlyList<Jid>> QueryParticipantsAsync(Jid group, CancellationToken cancellationToken)
    {
        QueryRouter? router = _routerProvider()
            ?? throw new SdkConnectionException("The connection is not open: the group query was not sent.");

        byte[] reply = await router.SendQueryAsync(
            BinaryNodeCodec.Encode(BuildGroupNode(group)),
            cancellationToken).ConfigureAwait(false);

        BinaryNode response;
        try
        {
            response = BinaryNodeCodec.Decode(reply);
        }
        catch (SdkFormatException ex)
        {
            throw new SdkFormatException("Group participants reply failed node validation.", ex);
        }

        return ParseParticipants(response);
    }

    /// <summary>Builds the pinned <c>w:g2</c> participants query node (fixture seam).</summary>
    internal static BinaryNode BuildGroupNode(Jid group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return BinaryNode.FromChildren(
            "iq",
            [BinaryNode.FromChildren(
                "query",
                [],
                new Dictionary<string, string>(StringComparer.Ordinal) { ["request"] = InteractiveRequest })],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["xmlns"] = GroupNamespace,
                ["type"] = "get",
                ["to"] = group.ToString(),
            });
    }

    /// <summary>
    /// Parses the reply bounds-checked into the participant list: a missing
    /// <c>group</c> node is the typed not-a-member failure; a malformed
    /// <c>participant</c> child fails the whole resolution (no partial list).
    /// </summary>
    internal static IReadOnlyList<Jid> ParseParticipants(BinaryNode response)
    {
        ArgumentNullException.ThrowIfNull(response);
        BinaryNode? group = FirstChild(response, "group");
        if (group is null)
        {
            throw new SdkFormatException("The group participants reply carries no group node: the group is unknown or this account is not a member.");
        }

        var members = new List<Jid>();
        IReadOnlyList<BinaryNode>? children = group.Children;
        if (children is null)
        {
            return members;
        }

        foreach (BinaryNode child in children)
        {
            if (!string.Equals(child.Tag, "participant", StringComparison.Ordinal))
            {
                continue;
            }

            if (!child.Attrs.TryGetValue("jid", out string? memberJid)
                || string.IsNullOrEmpty(memberJid)
                || !Jid.TryParse(memberJid, out Jid? parsed)
                || parsed is null)
            {
                throw new SdkFormatException("The group participants reply carries a malformed participant child.");
            }

            members.Add(parsed);
        }

        return members;
    }

    private static BinaryNode? FirstChild(BinaryNode? parent, string tag)
    {
        IReadOnlyList<BinaryNode>? children = parent?.Children;
        if (children is null)
        {
            return null;
        }

        foreach (BinaryNode child in children)
        {
            if (string.Equals(child.Tag, tag, StringComparison.Ordinal))
            {
                return child;
            }
        }

        return null;
    }
}
