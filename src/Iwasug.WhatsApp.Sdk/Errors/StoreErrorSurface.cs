using Iwasug.WhatsApp.Sdk.Accounts;
using Microsoft.Extensions.Logging;

namespace Iwasug.WhatsApp.Sdk.Errors;

/// <summary>
/// D-21 error-surfacing discipline at host boundaries: one shared wrap-and-log shape so every
/// SDK-internal failure crossing to a host caller is (a) an <see cref="SdkStoreException"/>
/// with the original exception preserved as the inner exception and (b) logged once, at
/// Warning, naming the operation and the account id — never record payloads, key values, or
/// the inner exception's message content (T-01-12).
/// </summary>
/// <remarks>
/// Internal by design (D-21 declares no typed event surface): this is SDK-internal glue, not
/// public API. Callers throw the returned exception unchanged.
/// </remarks>
internal static class StoreErrorSurface
{
    /// <summary>
    /// Logs <paramref name="ex"/> (when a logger is present) and wraps it as an
    /// <see cref="SdkStoreException"/> — passing through an exception that is already
    /// SDK-typed so double-wrapping never hides the original failure.
    /// </summary>
    /// <param name="ex">The failure to surface.</param>
    /// <param name="logger">Optional logger; logging is skipped when absent (D-21).</param>
    /// <param name="operation">Human-readable operation name used in the log entry and the wrapped message.</param>
    /// <param name="account">The account the operation ran for; only its id enters the log.</param>
    /// <returns>The exception the caller should throw (wrap-or-passthrough).</returns>
    internal static SdkStoreException LogAndWrap(Exception ex, ILogger? logger, string operation, AccountId account)
    {
        SdkStoreException wrapped = ex as SdkStoreException ?? new SdkStoreException($"{operation} failed.", ex);

        logger?.LogWarning(
            ex,
            "{Operation} failed for account {AccountId}.",
            operation,
            account.Value);

        return wrapped;
    }
}
