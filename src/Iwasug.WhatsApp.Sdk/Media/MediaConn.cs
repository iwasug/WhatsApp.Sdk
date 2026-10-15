using System.Collections.Concurrent;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Messaging;

namespace Iwasug.WhatsApp.Sdk.Media;

internal sealed class MediaConn
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(1);

    public sealed record Snapshot(string Auth, IReadOnlyList<string> Hosts, DateTimeOffset FetchedAt, TimeSpan Ttl, TimeSpan AuthTtl, int MaxBuckets);

    private readonly Func<QueryRouter?> _routerProvider;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Snapshot? _snapshot;

    public MediaConn(Func<QueryRouter?> routerProvider, TimeProvider? timeProvider = null)
    {
        _routerProvider = routerProvider ?? throw new ArgumentNullException(nameof(routerProvider));
        _clock = timeProvider ?? TimeProvider.System;
    }

    public async Task<Snapshot> GetAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && _snapshot is not null && !IsExpired(_snapshot, _clock.GetUtcNow()))
        {
            return _snapshot;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _snapshot is not null && !IsExpired(_snapshot, _clock.GetUtcNow()))
            {
                return _snapshot;
            }

            QueryRouter? router = _routerProvider()
                ?? throw new SdkConnectionException("The connection is not open: the media-conn query was not sent.");
            BinaryNode response;
            try
            {
                byte[] reply = await router.SendQueryAsync(
                    BinaryNodeCodec.Encode(BuildMediaConnNode()),
                    cancellationToken).ConfigureAwait(false);
                response = BinaryNodeCodec.Decode(reply);
            }
            catch (SdkFormatException ex)
            {
                throw new SdkFormatException("Media-conn reply failed node validation.", ex);
            }

            Snapshot snapshot = Parse(response, _clock.GetUtcNow());
            _snapshot = snapshot;
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => _snapshot = null;

    public Task<Snapshot> ForceRefreshAsync(CancellationToken cancellationToken = default) => GetAsync(true, cancellationToken);

    internal static BinaryNode BuildMediaConnNode() =>
        BinaryNode.FromChildren(
            "iq",
            [BinaryNode.Create("media_conn", null)],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["xmlns"] = "w:m",
                ["type"] = "set",
                ["to"] = DeviceDirectory.UsyncServer,
            });

    private static bool IsExpired(Snapshot snapshot, DateTimeOffset now)
    {
        TimeSpan ttl = snapshot.Ttl > TimeSpan.Zero && snapshot.AuthTtl > TimeSpan.Zero
            ? (snapshot.Ttl < snapshot.AuthTtl ? snapshot.Ttl : snapshot.AuthTtl)
            : (snapshot.Ttl > TimeSpan.Zero ? snapshot.Ttl : (snapshot.AuthTtl > TimeSpan.Zero ? snapshot.AuthTtl : DefaultTtl));
        return now > snapshot.FetchedAt + ttl;
    }

    internal static Snapshot Parse(BinaryNode response, DateTimeOffset fetchedAt)
    {
        BinaryNode? mediaConn = null;
        foreach (BinaryNode child in response.Children ?? Array.Empty<BinaryNode>())
        {
            if (child.Tag == "media_conn") { mediaConn = child; break; }
        }
        if (mediaConn is null) mediaConn = FirstChild(response, "media_conn");
        if (mediaConn is null)
        {
            throw new SdkFormatException("Media-conn reply carries no media_conn child.");
        }

        mediaConn.Attrs.TryGetValue("auth", out string? auth);
        if (string.IsNullOrEmpty(auth)) auth = string.Empty;

        int ttlSeconds = 0;
        int authTtlSeconds = 0;
        int maxBuckets = 0;
        if (mediaConn.Attrs.TryGetValue("ttl", out string? ttlStr)) int.TryParse(ttlStr, out ttlSeconds);
        if (mediaConn.Attrs.TryGetValue("auth_ttl", out string? authTtlStr)) int.TryParse(authTtlStr, out authTtlSeconds);
        if (mediaConn.Attrs.TryGetValue("max_buckets", out string? mbStr)) int.TryParse(mbStr, out maxBuckets);

        var hosts = new List<string>();
        foreach (BinaryNode host in mediaConn.Children ?? Array.Empty<BinaryNode>())
        {
            if (host.Tag != "host") continue;
            if (host.Attrs.TryGetValue("hostname", out string? hostname) && !string.IsNullOrEmpty(hostname))
            {
                hosts.Add(hostname);
            }
        }

        if (hosts.Count == 0)
        {
            throw new SdkFormatException("Media-conn reply carries no hosts.");
        }

        return new Snapshot(
            auth ?? string.Empty,
            hosts,
            fetchedAt,
            ttlSeconds > 0 ? TimeSpan.FromSeconds(ttlSeconds) : DefaultTtl,
            authTtlSeconds > 0 ? TimeSpan.FromSeconds(authTtlSeconds) : DefaultTtl,
            maxBuckets);
    }

    private static BinaryNode? FirstChild(BinaryNode? parent, string tag)
    {
        IReadOnlyList<BinaryNode>? children = parent?.Children;
        if (children is null) return null;
        foreach (BinaryNode child in children)
        {
            if (string.Equals(child.Tag, tag, StringComparison.Ordinal)) return child;
        }
        return null;
    }
}
