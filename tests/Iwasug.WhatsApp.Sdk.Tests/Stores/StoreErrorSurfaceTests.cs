using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Errors;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Stores;

/// <summary>
/// D-21 error-surfacing discipline (T-01-12): LogAndWrap wraps into SdkStoreException with
/// the original exception preserved, logs once at Warning naming the operation and account —
/// never record payloads — and passes through already-SDK-typed exceptions unchanged.
/// </summary>
/// <remarks>
/// The logger fake is hand-written (a recording ILogger) rather than NSubstitute: NSubstitute
/// cannot reliably intercept the generic <c>ILogger.Log&lt;TState&gt;</c> call (the framework's
/// strongly-typed state parameter cannot be expressed as an Arg.Any matcher), so a recording
/// fake is the dependable mechanism for asserting log content.
/// </remarks>
public sealed class StoreErrorSurfaceTests
{
    private sealed record LogEntry(LogLevel Level, Exception? Exception, string Message);

    private sealed class RecordingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, exception, formatter(state, exception)));
    }

    [Fact]
    public void Wraps_non_sdk_exception_preserving_inner_and_logs_operation_without_payload()
    {
        var logger = new RecordingLogger();
        var account = AccountId.Create("acct-1");
        var original = new InvalidOperationException("record payload secret-xyz must not leak");

        var wrapped = StoreErrorSurface.LogAndWrap(original, logger, "identity put", account);

        wrapped.Should().BeOfType<SdkStoreException>();
        wrapped.InnerException.Should().BeSameAs(original);

        logger.Entries.Should().ContainSingle();
        var entry = logger.Entries[0];
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Exception.Should().BeSameAs(original);
        entry.Message.Should().Contain("identity put");
        entry.Message.Should().Contain("acct-1");

        // Payload discipline (T-01-12): neither the log entry nor the surfaced message
        // carries the inner exception's payload-like content.
        entry.Message.Should().NotContain("secret-xyz");
        wrapped.Message.Should().NotContain("secret-xyz");
    }

    [Fact]
    public void Passes_through_an_already_SdkStoreException_without_double_wrapping()
    {
        var logger = new RecordingLogger();
        var account = AccountId.Create("acct-2");
        var inner = new InvalidOperationException("boom");
        var original = new SdkStoreException("already typed", inner);

        var wrapped = StoreErrorSurface.LogAndWrap(original, logger, "identity put", account);

        wrapped.Should().BeSameAs(original);
        wrapped.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void Null_logger_skips_logging_but_still_wraps()
    {
        var account = AccountId.Create("acct-3");
        var original = new InvalidOperationException("boom");

        var wrapped = StoreErrorSurface.LogAndWrap(original, null, "identity put", account);

        wrapped.Should().BeOfType<SdkStoreException>();
        wrapped.InnerException.Should().BeSameAs(original);
    }
}
