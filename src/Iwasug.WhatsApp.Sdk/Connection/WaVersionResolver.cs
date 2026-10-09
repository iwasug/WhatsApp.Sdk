using System.Globalization;
using System.Text.RegularExpressions;
using Iwasug.WhatsApp.Sdk.Errors;
using Microsoft.Extensions.Logging;

namespace Iwasug.WhatsApp.Sdk.Connection;

/// <summary>
/// One WA Web app version: the triple carried in the pairing-attribute build pin and
/// the runtime-fetched revision (primary.secondary.tertiary, e.g.
/// <c>2.3000.1043857760</c>).
/// </summary>
/// <param name="Primary">App version primary.</param>
/// <param name="Secondary">App version secondary.</param>
/// <param name="Tertiary">App version tertiary (the <c>client_revision</c>).</param>
internal readonly record struct WaVersion(uint Primary, uint Secondary, uint Tertiary)
{
    /// <inheritdoc />
    public override string ToString() => $"{Primary}.{Secondary}.{Tertiary}";

    /// <summary>
    /// Parses a dotted override (<c>primary.secondary.tertiary</c>, all segments
    /// unsigned integers, exactly three segments) without throwing.
    /// </summary>
    /// <param name="text">The candidate override text.</param>
    /// <param name="version">The parsed version on success.</param>
    /// <returns><c>true</c> when <paramref name="text"/> is a well-shaped triple.</returns>
    internal static bool TryParseDotted(string? text, out WaVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        string[] parts = text.Split('.');
        if (parts.Length != 3
            || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint primary)
            || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint secondary)
            || !uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint tertiary))
        {
            return false;
        }

        version = new WaVersion(primary, secondary, tertiary);
        return true;
    }
}

/// <summary>
/// Fetches the WhatsApp Web service-worker script whose <c>client_revision</c> is the
/// runtime version source. Behind an interface so tests substitute the bytes
/// (InternalsVisibleTo covers the tests project) — no test performs a real fetch.
/// </summary>
internal interface IWaVersionFetcher
{
    /// <summary>Downloads the raw script text.</summary>
    /// <param name="cancellationToken">Caller cancellation (unwrapped).</param>
    /// <returns>The script text.</returns>
    Task<string> FetchSwJsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Production <see cref="IWaVersionFetcher"/> over <see cref="HttpClient"/>
/// (in-box <c>System.Net.Http</c> — no new package). HTTPS-only to the canonical
/// host: anything else is a tampering signal (T-3-04), rejected at construction.
/// </summary>
internal sealed class HttpSwJsFetcher : IWaVersionFetcher
{
    private readonly HttpClient _httpClient;
    private readonly string _requestUri;

    /// <summary>Creates the fetcher over a caller-owned <see cref="HttpClient"/>.</summary>
    /// <param name="httpClient">Caller-owned client (lifetime stays with the caller).</param>
    /// <param name="requestUri">Script address; must be HTTPS (defaults to the canonical host).</param>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="requestUri"/> is not an HTTPS address.</exception>
    internal HttpSwJsFetcher(HttpClient httpClient, string requestUri = WaVersionResolver.SwJsUri)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (!Uri.TryCreate(requestUri, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Version script addresses must use HTTPS.", nameof(requestUri));
        }

        _httpClient = httpClient;
        _requestUri = requestUri;
    }

    /// <inheritdoc />
    public Task<string> FetchSwJsAsync(CancellationToken cancellationToken) =>
        _httpClient.GetStringAsync(_requestUri, cancellationToken);
}

/// <summary>
/// The single home of the WA Web version (03-RESEARCH.md Architecture Patterns
/// Pattern 4): the bundled pairing-attribute build pin plus the runtime
/// <c>client_revision</c>, with a six-hour cache invalidated on 405.
/// </summary>
/// <remarks>
/// <para>
/// Derivation (re-derivable without guessing): the mechanism is an HTTP GET of
/// <c>https://web.whatsapp.com/sw.js</c> with a <c>client_revision</c> regex, the
/// cache is ~6h, the fallback is the bundled pin
/// <c>[2, 3000, 1043857760]</c> (Baileys <c>src/Utils/generics.ts</c>
/// <c>baileysVersion</c> at commit <c>0af2386</c>), and a 405 clears the cache so the
/// next attempt re-resolves instead of retrying a known-rejected value — all recorded
/// in 03-RESEARCH.md Pattern 4. Review status is Assumptions-Log A5: the extraction
/// mechanism is verified in Baileys source, the live fetch was not run — so a fetch
/// failure keeps the bundled pin and logs loudly (never attacker content, T-3-04).
/// </para>
/// <para>
/// Read-only and stateless apart from its cache: no QR, no pairing state, no reconnect
/// policy. One instance per connection (per-account isolation — the cache is an
/// instance field, never static). This class never logs key material — it handles
/// version numbers only — and never surfaces a fetch failure as
/// <see cref="DisconnectReason.ClientOutdated"/>: only a real 405 does that.
/// </para>
/// </remarks>
internal sealed partial class WaVersionResolver
{
    /// <summary>The canonical WhatsApp Web service-worker address (HTTPS only).</summary>
    internal const string SwJsUri = "https://web.whatsapp.com/sw.js";

    /// <summary>Bundled app-version primary (Baileys <c>baileysVersion[0]</c>).</summary>
    internal const uint BundledPrimary = 2;

    /// <summary>Bundled app-version secondary (Baileys <c>baileysVersion[1]</c>).</summary>
    internal const uint BundledSecondary = 3000;

    /// <summary>Bundled app-version tertiary (Baileys <c>baileysVersion[2]</c>).</summary>
    internal const uint BundledTertiary = 1043857760;

    /// <summary>The documented fallback pin when no runtime revision is available.</summary>
    internal static readonly WaVersion BundledPin = new(BundledPrimary, BundledSecondary, BundledTertiary);

    /// <summary>Revision cache window (Pattern 4's ~6h).</summary>
    internal static readonly TimeSpan CacheWindow = TimeSpan.FromHours(6);

    private readonly IWaVersionFetcher _fetcher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WaVersionResolver>? _logger;
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private readonly object _cacheLock = new();
    private (WaVersion Value, DateTimeOffset At)? _entry;

    /// <summary>Creates the resolver over a script fetcher.</summary>
    /// <param name="fetcher">Script source (production: <see cref="HttpSwJsFetcher"/>; tests substitute).</param>
    /// <param name="timeProvider">Clock for the cache window; defaults to system time.</param>
    /// <param name="logger">Optional logger (Abstractions only).</param>
    /// <exception cref="ArgumentNullException"><paramref name="fetcher"/> is null.</exception>
    internal WaVersionResolver(
        IWaVersionFetcher fetcher,
        TimeProvider? timeProvider = null,
        ILogger<WaVersionResolver>? logger = null)
    {
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the version for one attempt: <see cref="ConnectionOptions.WaVersionOverride"/>
    /// wins when present (tests and loopback supply a revision without network access);
    /// otherwise the cached runtime revision flows through.
    /// </summary>
    /// <param name="options">Connection options carrying the optional override.</param>
    /// <param name="cancellationToken">Caller cancellation (unwrapped).</param>
    /// <returns>The override triple, or the cached/fetched runtime revision.</returns>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    internal Task<WaVersion> ResolveAsync(ConnectionOptions? options, CancellationToken cancellationToken = default)
    {
        if (options?.WaVersionOverride is string text
            && WaVersion.TryParseDotted(text, out WaVersion pinned))
        {
            return Task.FromResult(pinned);
        }

        return GetVersionAsync(cancellationToken);
    }

    /// <summary>
    /// Returns the runtime revision: one shared fetch per <see cref="CacheWindow"/>,
    /// concurrent callers sharing the single in-flight request. A failed or malformed
    /// fetch yields <see cref="BundledPin"/> and never poisons the cache — the next
    /// call retries.
    /// </summary>
    /// <param name="cancellationToken">Caller cancellation (unwrapped).</param>
    /// <returns>The fetched revision, or the bundled pin on any fetch failure.</returns>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    internal async Task<WaVersion> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        if (TryGetFresh(out WaVersion cached))
        {
            return cached;
        }

        await _fetchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A concurrent caller may have populated the cache while this call waited.
            if (TryGetFresh(out cached))
            {
                return cached;
            }

            try
            {
                string script = await _fetcher.FetchSwJsAsync(cancellationToken).ConfigureAwait(false);
                WaVersion version = ParseClientRevision(script);
                WriteEntry(version);
                return version;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Loud fallback (A5): the bundled pin, never attacker content, and the
                // window stays usable for the next attempt (T-3-04). The message names
                // the rule only — fetched script bytes never enter logs.
                _logger?.LogWarning(
                    ex,
                    "WhatsApp Web version fetch failed: keeping the bundled pin.");
                return BundledPin;
            }
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    /// <summary>
    /// Clears the cached revision so the next pairing attempt re-fetches before
    /// deciding — the ClientOutdated (405) disconnect path calls this (T-3-05).
    /// </summary>
    internal void Invalidate()
    {
        lock (_cacheLock)
        {
            _entry = null;
        }

        _logger?.LogDebug("WhatsApp Web version cache invalidated.");
    }

    /// <summary>
    /// Extracts the tertiary revision from raw script text (tolerant of minified
    /// single- or double-quoted assignment).
    /// </summary>
    /// <param name="script">Raw <c>sw.js</c> text.</param>
    /// <returns>The bundled primary/secondary with the extracted tertiary.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="script"/> is null.</exception>
    /// <exception cref="SdkFormatException">No well-shaped <c>client_revision</c> is present (message names the rule only).</exception>
    internal static WaVersion ParseClientRevision(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        Match match = ClientRevisionPattern().Match(script);
        Group capture = match.Groups[1].Success ? match.Groups[1] : match.Groups[2];
        if (!capture.Success
            || !ulong.TryParse(
                capture.ValueSpan,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out ulong revision)
            || revision > uint.MaxValue)
        {
            throw new SdkFormatException("Version scripts must carry a numeric client_revision.");
        }

        return new WaVersion(BundledPrimary, BundledSecondary, (uint)revision);
    }

    private bool TryGetFresh(out WaVersion version)
    {
        lock (_cacheLock)
        {
            if (_entry is { } entry && _timeProvider.GetUtcNow() - entry.At < CacheWindow)
            {
                version = entry.Value;
                return true;
            }

            version = default;
            return false;
        }
    }

    private void WriteEntry(WaVersion version)
    {
        lock (_cacheLock)
        {
            _entry = (version, _timeProvider.GetUtcNow());
        }
    }

    [GeneratedRegex(@"""client_revision""\s*:\s*(\d+)|'client_revision'\s*:\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ClientRevisionPattern();
}
