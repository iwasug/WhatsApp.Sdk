using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Hosting;
using Microsoft.Extensions.Logging;
using AwesomeAssertions;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Hosting;

public class ScopedLoggerTests
{
    private sealed class FakeLogger : ILogger<FakeLogger>
    {
        public readonly List<(LogLevel Level, EventId EventId, object? State, Exception? Exception)> Entries = new();
        public readonly List<IDictionary<string, object>> Scopes = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            if (state is IDictionary<string, object> dict) Scopes.Add(dict);
            return new DummyDisposable();
        }
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, eventId, state, exception));
    }

    private sealed class DummyDisposable : IDisposable { public void Dispose() { } }

    [Fact]
    public void Constructor_NullFactory_Throws()
    {
        var id = AccountId.Create("a");
        Action act = () => _ = new AccountScopedLogger<FakeLogger>(id, (ILoggerFactory)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var id = AccountId.Create("a");
        Action act = () => _ = new AccountScopedLogger<FakeLogger>(id, (ILogger<FakeLogger>)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Log_StampsAccountIdScope()
    {
        var id = AccountId.Create("acct-1");
        var inner = new FakeLogger();
        var logger = new AccountScopedLogger<FakeLogger>(id, inner);
        using (logger.BeginScope("outer")) { }
        logger.Log(LogLevel.Information, new EventId(7, "name"), "state", null, (s, e) => s?.ToString() ?? "");
        inner.Scopes.Should().ContainSingle();
        inner.Scopes[0]["AccountId"].Should().Be("acct-1");
        inner.Entries.Should().ContainSingle();
        inner.Entries[0].EventId.Id.Should().Be(7);
    }

    [Fact]
    public void TwoLoggers_DoNotCrossTag()
    {
        var id1 = AccountId.Create("acct-A");
        var id2 = AccountId.Create("acct-B");
        var inner1 = new FakeLogger();
        var inner2 = new FakeLogger();
        var l1 = new AccountScopedLogger<FakeLogger>(id1, inner1);
        var l2 = new AccountScopedLogger<FakeLogger>(id2, inner2);
        l1.Log(LogLevel.Warning, default, "x", null, (s, e) => "x");
        l2.Log(LogLevel.Error, default, "y", null, (s, e) => "y");
        inner1.Scopes[0]["AccountId"].Should().Be("acct-A");
        inner2.Scopes[0]["AccountId"].Should().Be("acct-B");
    }
}
