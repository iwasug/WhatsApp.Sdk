using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;

namespace Iwasug.WhatsApp.Sdk.Messaging;

/// <summary>
/// Resolves a peer's device set for message fanout over the USync <c>devices</c> IQ
/// (04-03; 04-RESEARCH Pattern 3, whatsmeow <c>user.go</c> behavioral reference):
/// <c>&lt;iq xmlns="usync" type="get" to="s.whatsapp.net"&gt;&lt;usync sid mode last index
/// context="message"&gt;&lt;query&gt;&lt;devices version="2"/&gt;&lt;/query&gt;&lt;list&gt;
/// &lt;user jid="…"/&gt;&lt;/list&gt;&lt;/usync&gt;&lt;/iq&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// Policy (04-RESEARCH Open Question 5 RESOLVED): the device cache is process-local and
/// SDK-owned — a restart re-queries; the host's stores never see it. The TTL and the
/// own-device exclusion are policy, never store parameters (D-08).
/// </para>
/// <para>
/// Own-device exclusion (Pitfall 3 / T-4-11): devices whose base user (the digits before
/// the <c>:</c> device suffix) equals the sending account's paired user are filtered from
/// every resolved set, so a send never encrypts the same plaintext for the account's own
/// devices (self-echo). The phone device (<c>:0</c>) is excluded per Pattern 3.
/// </para>
/// <para>
/// Discipline: responses are parsed bounds-checked (a malformed device child fails the
/// whole resolution with a rule-only <see cref="SdkFormatException"/> — no partial list);
/// JID node attrs are canonical <see cref="Jid.ToString"/> forms only; nothing here logs.
/// </para>
/// </remarks>
internal sealed class DeviceDirectory
{
    /// <summary>Default cache TTL (process-local; 04-RESEARCH OQ5 — SDK-owned policy).</summary>
    internal static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    /// <summary>The <c>usync</c> namespace server.</summary>
    internal const string UsyncServer = "s.whatsapp.net";

    private readonly Func<QueryRouter?> _routerProvider;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _ttl;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (IReadOnlyList<Jid> Devices, DateTimeOffset CachedAt)> _cache = new(StringComparer.Ordinal);

    /// <summary>Creates a directory issuing USync queries through <paramref name="routerProvider"/> (per-attempt routers).</summary>
    internal DeviceDirectory(Func<QueryRouter?> routerProvider, TimeProvider? timeProvider = null, TimeSpan? ttl = null)
    {
        _routerProvider = routerProvider ?? throw new ArgumentNullException(nameof(routerProvider));
        _clock = timeProvider ?? TimeProvider.System;
        _ttl = ttl ?? DefaultTtl;
    }

    /// <summary>
    /// Resolves the non-own device set for <paramref name="peer"/>: a fresh cache entry
    /// wins with zero queries; an expired entry re-queries. Concurrent resolves for any
    /// peer serialize behind one gate so two sends never race the cache.
    /// </summary>
    /// <param name="peer">The recipient chat JID.</param>
    /// <param name="ownPairedUser">The sending account's paired user digits (own-family exclusion).</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>The resolved, own-excluded device JIDs (may be empty for a phone-only peer).</returns>
    /// <exception cref="SdkFormatException">The peer JID, the reply, or any device child violates its shape rule.</exception>
    /// <exception cref="SdkConnectionException">No open channel (the router was absent) or the USync query failed.</exception>
    internal async Task<IReadOnlyList<Jid>> ResolveAsync(Jid peer, string ownPairedUser, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentException.ThrowIfNullOrEmpty(ownPairedUser);
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string key = peer.ToString();
            if (_cache.TryGetValue(key, out (IReadOnlyList<Jid> Devices, DateTimeOffset CachedAt) hit)
                && _clock.GetUtcNow() - hit.CachedAt < _ttl)
            {
                return hit.Devices;
            }

            IReadOnlyList<Jid> devices = await QueryDevicesAsync(peer, ownPairedUser, cancellationToken).ConfigureAwait(false);
            _cache[key] = (devices, _clock.GetUtcNow());
            return devices;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Drops the cached set for <paramref name="peer"/> (Pitfall 3: a <c>phash</c>
    /// mismatch means the peer set moved — the next resolve re-queries).
    /// </summary>
    internal void Invalidate(Jid peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        _cache.TryRemove(peer.ToString(), out _);
    }

    /// <summary>
    /// Computes the server <c>phash</c> form over a resolved device set
    /// (04-RESEARCH Pattern 6, verbatim algorithm): AD-string device JIDs sorted,
    /// concatenated, SHA-256, then <c>2:</c> + unpadded standard base64 of the first
    /// 6 hash bytes.
    /// </summary>
    internal static string ComputePhash(IReadOnlyList<Jid> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        string[] forms = new string[devices.Count];
        for (int i = 0; i < devices.Count; i++)
        {
            forms[i] = devices[i].ToString();
        }

        Array.Sort(forms, StringComparer.Ordinal);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(forms)));
        return string.Create(CultureInfo.InvariantCulture, $"2:{Convert.ToBase64String(hash, 0, 6).TrimEnd('=')}");
    }

    private async Task<IReadOnlyList<Jid>> QueryDevicesAsync(Jid peer, string ownPairedUser, CancellationToken cancellationToken)
    {
        QueryRouter? router = _routerProvider()
            ?? throw new SdkConnectionException("The connection is not open: the device query was not sent.");

        byte[] reply = await router.SendQueryAsync(
            BinaryNodeCodec.Encode(BuildUsyncNode(peer)),
            cancellationToken).ConfigureAwait(false);

        BinaryNode response;
        try
        {
            response = BinaryNodeCodec.Decode(reply);
        }
        catch (SdkFormatException ex)
        {
            throw new SdkFormatException("Device-list reply failed node validation.", ex);
        }

        return ParseDevices(response, peer, ownPairedUser);
    }

    internal static BinaryNode BuildUsyncNode(Jid peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        return BinaryNode.FromChildren(
            "iq",
            [BinaryNode.FromChildren(
                "usync",
                [
                    BinaryNode.FromChildren(
                        "query",
                        [BinaryNode.FromChildren(
                            "devices",
                            [],
                            new Dictionary<string, string>(StringComparer.Ordinal) { ["version"] = "2" })]),
                    BinaryNode.FromChildren(
                        "list",
                        [BinaryNode.Create(
                            "user",
                            new Dictionary<string, string>(StringComparer.Ordinal) { ["jid"] = peer.ToString() })]),
                ],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sid"] = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8],
                    ["mode"] = "query",
                    ["last"] = "true",
                    ["index"] = "0",
                    ["context"] = "message",
                })],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["xmlns"] = "usync",
                ["type"] = "get",
                ["to"] = UsyncServer,
            });
    }

    /// <summary>Parses the USync reply bounds-checked into the own-excluded device list.</summary>
    internal static IReadOnlyList<Jid> ParseDevices(BinaryNode response, Jid peer, string ownPairedUser)
    {
        BinaryNode? list = FirstChild(FirstChild(response, "usync"), "list");
        IReadOnlyList<BinaryNode>? users = list?.Children;
        if (users is null)
        {
            throw new SdkFormatException("Device-list reply carries no user list.");
        }

        var devices = new List<Jid>();
        foreach (BinaryNode user in users)
        {
            if (!string.Equals(user.Tag, "user", StringComparison.Ordinal))
            {
                continue;
            }

            BinaryNode? userDevices = FirstChild(user, "devices");
            IReadOnlyList<BinaryNode>? children = userDevices?.Children;
            if (children is null)
            {
                continue;
            }

            foreach (BinaryNode device in children)
            {
                if (!string.Equals(device.Tag, "device", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!device.Attrs.TryGetValue("jid", out string? deviceJid)
                    || string.IsNullOrEmpty(deviceJid)
                    || !Jid.TryParse(deviceJid, out Jid? parsed)
                    || parsed is null)
                {
                    throw new SdkFormatException("Device-list reply carries a malformed device child.");
                }

                if (IsOwnDevice(parsed, ownPairedUser) || IsPhoneDevice(parsed))
                {
                    continue;
                }

                devices.Add(parsed);
            }
        }

        return devices;
    }

    /// <summary>Own-device family exclusion (Pitfall 3 / T-4-11): the paired user's devices never enter a fanout set.</summary>
    internal static bool IsOwnDevice(Jid device, string ownPairedUser)
    {
        return string.Equals(BaseUser(device), ownPairedUser, StringComparison.Ordinal);
    }

    /// <summary>
    /// Phone-device exclusion (Pattern 3): the primary phone (device 0) is never an
    /// encryption target. On the wire the phone is the bare user JID — the codec's
    /// AD-JID form drops a zero device — and some servers spell it explicitly as
    /// <c>user:0</c>; both forms are excluded.
    /// </summary>
    internal static bool IsPhoneDevice(Jid device)
    {
        string user = device.User;
        int colon = user.LastIndexOf(':');
        return colon < 0
            || (colon < user.Length - 1 && user[(colon + 1)..] == "0");
    }

    /// <summary>The user digits before the <c>:</c> device suffix (the JID's <c>User</c> token carries the full form).</summary>
    internal static string BaseUser(Jid device)
    {
        int colon = device.User.LastIndexOf(':');
        return colon < 0 ? device.User : device.User[..colon];
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
