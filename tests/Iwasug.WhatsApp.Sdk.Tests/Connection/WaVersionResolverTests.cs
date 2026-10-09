using System.Security.Cryptography;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Pairing;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.InMemory;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Connection;

/// <summary>
/// <see cref="WaVersionResolver"/> proofs, all offline: the fetcher is always
/// substituted (a real HTTP call in this file is a bug), time rides
/// <see cref="FakeTimeProvider"/>. Covers the six-hour single-flight cache,
/// 405 invalidation, failure-keeps-the-pin, and override precedence through
/// <see cref="ConnectionOptions.WaVersionOverride"/> into the handshake payload.
/// </summary>
public sealed class WaVersionResolverTests
{
    private const uint FetchedRevision = 1049999999;

    private sealed class CountingFetcher : IWaVersionFetcher
    {
        private readonly Func<string> _script;
        internal int Calls;

        internal CountingFetcher(Func<string> script) => _script = script;

        public Task<string> FetchSwJsAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_script());
        }
    }

    private sealed class ThrowingFetcher : IWaVersionFetcher
    {
        internal int Calls;

        public Task<string> FetchSwJsAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("Simulated fetch failure.");
        }
    }

    private static string DoubleQuotedScript() =>
        "self.__WB_MANIFEST=[];var x={\"client_revision\": 1049999999};";

    [Fact]
    public async Task GetVersion_caches_one_fetch_inside_six_hours_and_single_flights_concurrent_callers()
    {
        var fakeTime = new FakeTimeProvider();
        var fetcher = new CountingFetcher(DoubleQuotedScript);
        var resolver = new WaVersionResolver(fetcher, fakeTime);

        WaVersion first = await resolver.GetVersionAsync(CancellationToken.None);
        first.Should().Be(new WaVersion(2, 3000, FetchedRevision));
        fetcher.Calls.Should().Be(1);

        for (int i = 0; i < 5; i++)
        {
            (await resolver.GetVersionAsync(CancellationToken.None)).Should().Be(first);
        }

        fetcher.Calls.Should().Be(1, "five requests inside the window must not fetch again");

        var racing = new WaVersionResolver(new CountingFetcher(DoubleQuotedScript), fakeTime);
        WaVersion[] results = await Task.WhenAll(
            Enumerable.Range(0, 5).Select(_ => racing.GetVersionAsync(CancellationToken.None)));
        results.Should().AllBeEquivalentTo(results[0]);

        fakeTime.Advance(TimeSpan.FromHours(5) + TimeSpan.FromMinutes(59));
        await resolver.GetVersionAsync(CancellationToken.None);
        fetcher.Calls.Should().Be(1, "the window is still fresh just under six hours");

        fakeTime.Advance(TimeSpan.FromMinutes(2));
        WaVersion refreshed = await resolver.GetVersionAsync(CancellationToken.None);
        refreshed.Should().Be(first);
        fetcher.Calls.Should().Be(2, "past six hours the next request re-fetches");
    }

    [Fact]
    public async Task Invalidate_forces_refetch_on_next_request()
    {
        var fetcher = new CountingFetcher(DoubleQuotedScript);
        var resolver = new WaVersionResolver(fetcher, new FakeTimeProvider());

        await resolver.GetVersionAsync(CancellationToken.None);
        fetcher.Calls.Should().Be(1);

        resolver.Invalidate();

        WaVersion after = await resolver.GetVersionAsync(CancellationToken.None);
        after.Should().Be(new WaVersion(2, 3000, FetchedRevision));
        fetcher.Calls.Should().Be(2, "the ClientOutdated entry point must drop the cached window");
    }

    [Fact]
    public async Task Fetch_failure_or_malformed_script_yields_bundled_pin_without_poisoning_cache()
    {
        var failing = new ThrowingFetcher();
        var resolver = new WaVersionResolver(failing, new FakeTimeProvider());

        (await resolver.GetVersionAsync(CancellationToken.None)).Should().Be(WaVersionResolver.BundledPin);
        (await resolver.GetVersionAsync(CancellationToken.None)).Should().Be(WaVersionResolver.BundledPin);
        failing.Calls.Should().Be(2, "a failed fetch must not poison the cache — the next attempt retries");

        var malformed = new WaVersionResolver(
            new CountingFetcher(static () => "no revision here"),
            new FakeTimeProvider());
        (await malformed.GetVersionAsync(CancellationToken.None)).Should().Be(
            new WaVersion(2, 3000, 1043857760),
            "malformed content keeps the pin, never attacker content");
        WaVersionResolver.BundledPin.Should().Be(new WaVersion(2, 3000, 1043857760));
    }

    [Theory]
    [InlineData("var x={'client_revision':42};", 42u)]
    [InlineData("var x={\"client_revision\" :  1043857760};", 1043857760u)]
    public void ParseClientRevision_accepts_single_or_double_quoted_assignment(string script, uint revision)
    {
        WaVersionResolver.ParseClientRevision(script).Should().Be(new WaVersion(2, 3000, revision));
    }

    [Fact]
    public async Task ResolveAsync_override_wins_over_resolver_and_flows_into_handshake_payload()
    {
        var fetcher = new CountingFetcher(DoubleQuotedScript);
        var resolver = new WaVersionResolver(fetcher, new FakeTimeProvider());
        var options = new ConnectionOptions { WaVersionOverride = "2.3000.777" };

        WaVersion resolved = await resolver.ResolveAsync(options, CancellationToken.None);
        resolved.Should().Be(new WaVersion(2, 3000, 777));
        fetcher.Calls.Should().Be(0, "the override must win without any fetch");

        WaVersion runtime = await resolver.ResolveAsync(null, CancellationToken.None);
        runtime.Should().Be(new WaVersion(2, 3000, FetchedRevision));
        fetcher.Calls.Should().Be(1, "with no override the resolver value flows through");

        AccountId account = AccountId.Create("version-wire-" + Guid.NewGuid().ToString("N"));
        ICredentialStoreFactory factory = new InMemoryStoreFactory();
        ICredentialStore store = await factory.CreateAsync(account, CancellationToken.None);
        await store.PutAsync(
            new CredentialRecord(
                DateTimeOffset.UtcNow,
                "android",
                RandomNumberGenerator.GetBytes(32),
                RandomNumberGenerator.GetBytes(32),
                RandomNumberGenerator.GetBytes(32),
                RandomNumberGenerator.GetBytes(32),
                "15551234567",
                1,
                42),
            CancellationToken.None);
        var defaults = new RegistrationDefaults(
            RandomNumberGenerator.GetBytes(32), 7, RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(64), 42, "android", 2, 3000, 1043857760);

        var loginProvider = new HandshakePayloadProvider(account, store, defaults, resolved);
        await loginProvider.LoadCredentialsAsync(CancellationToken.None);
        ClientPayload login = ClientPayload.Parser.ParseFrom(loginProvider.GetClientFinish());
        login.UserAgent.AppVersion.Tertiary.Should().Be(777u, "the override must reach the login wire bytes");

        var registrationProvider = new HandshakePayloadProvider(
            account, store, defaults, new WaVersion(2, 3000, 778));
        ClientPayload registration = ClientPayload.Parser.ParseFrom(registrationProvider.GetClientFinish());
        registration.UserAgent.AppVersion.Tertiary.Should().Be(
            778u, "the override must reach the registration wire bytes");
    }

    [Fact]
    public void ValidateOptions_accepts_a_well_shaped_override()
    {
        var options = new ConnectionOptions { WaVersionOverride = "2.3000.1043857760" };

        ConnectionOptions.ValidateOptions(options).Should().BeSameAs(options);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("2.3000.x")]
    [InlineData("2.3000.4294967296")]
    [InlineData(" 2.3000.1")]
    public void ValidateOptions_rejects_malformed_override_before_any_io(string overrideText)
    {
        Action act = () => ConnectionOptions.ValidateOptions(new ConnectionOptions { WaVersionOverride = overrideText });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
