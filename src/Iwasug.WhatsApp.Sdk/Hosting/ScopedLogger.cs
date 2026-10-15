using System.Collections.Concurrent;
using Iwasug.WhatsApp.Sdk.Accounts;
using Microsoft.Extensions.Logging;

namespace Iwasug.WhatsApp.Sdk.Hosting;

/// <summary>
/// Per-account scoped logger (MUL-03 / 07-04): stamps every emitted entry with the
/// originating <see cref="Accounts.AccountId"/> via a short-lived <c>BeginScope</c>
/// around the inner logger call. The scope lives only across one <c>Log</c> call;
/// <c>IsEnabled</c> and <c>BeginScope</c> delegate to the inner logger.
/// </summary>
/// <remarks>
/// Construction rejects a null factory/logger (T-07-08: rule-only disclosure, no
/// host-payload, JID, store content, or inner-exception message text in tags).
/// </remarks>
public class AccountScopedLogger<T> : ILogger<T>
{
    private readonly AccountId _accountId;
    private readonly ILogger<T> _inner;

    /// <summary>Constructs from a host <see cref="ILoggerFactory"/> (one instance per account).</summary>
    public AccountScopedLogger(AccountId accountId, ILoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _accountId = accountId;
        _inner = factory.CreateLogger<T>();
    }

    /// <summary>Constructs from an already-created inner logger (tests / advanced hosts).</summary>
    public AccountScopedLogger(AccountId accountId, ILogger<T> innerLogger)
    {
        ArgumentNullException.ThrowIfNull(innerLogger);
        _accountId = accountId;
        _inner = innerLogger;
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
        _inner.BeginScope(state);

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        using (_inner.BeginScope(new Dictionary<string, object> { ["AccountId"] = _accountId.Value }))
        {
            _inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}

/// <summary>Alias for <see cref="AccountScopedLogger{T}"/> (plan 07-04 file name).</summary>
public sealed class ScopedLogger<T> : AccountScopedLogger<T>
{
    /// <inheritdoc />
    public ScopedLogger(AccountId accountId, ILoggerFactory factory) : base(accountId, factory) { }

    /// <inheritdoc />
    public ScopedLogger(AccountId accountId, ILogger<T> innerLogger) : base(accountId, innerLogger) { }
}
