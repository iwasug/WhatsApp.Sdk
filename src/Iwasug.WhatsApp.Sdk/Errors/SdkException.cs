namespace Iwasug.WhatsApp.Sdk.Errors;

/// <summary>
/// Base type for every exception the SDK throws (D-17: typed exception hierarchy; async
/// SDK APIs throw, never Result types and never swallowed failures).
/// </summary>
/// <remarks>
/// Failure surfacing model (D-21): a store failure reaches the awaiting caller as an
/// exception AND as an <c>ILogger</c> warning/error from the SDK; there is no typed
/// store-failure event yet. Message discipline: exception messages name the operation or
/// the violated rule only — they never embed key material, store record payloads, host
/// account values, or other sensitive content.
/// </remarks>
public abstract class SdkException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="SdkException"/> class.</summary>
    /// <param name="message">Rule/operation description — no sensitive payload values.</param>
    /// <param name="inner">Optional inner exception (e.g. the underlying store error).</param>
    protected SdkException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// A durable store operation failed (D-21). For reads this is the "failure" half of
/// failure ≠ missing (D-19): a read failure throws this exception and is never mapped to
/// <c>null</c>. Note D-20: recent-message cache write failures log and continue instead
/// of surfacing this exception.
/// </summary>
public sealed class SdkStoreException : SdkException
{
    /// <summary>Initializes a new instance of the <see cref="SdkStoreException"/> class.</summary>
    /// <param name="message">Operation description — no store payload values.</param>
    /// <param name="inner">The underlying store error, when available.</param>
    public SdkStoreException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// A value failed validation while parsing or formatting — e.g.
/// <see cref="Accounts.AccountId.Create"/> or (from plan 01-02) <c>Jid.Parse</c>.
/// </summary>
public sealed class SdkFormatException : SdkException
{
    /// <summary>Initializes a new instance of the <see cref="SdkFormatException"/> class.</summary>
    /// <param name="message">The violated rule — the offending value itself is never embedded.</param>
    /// <param name="inner">Optional inner exception.</param>
    public SdkFormatException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The host supplied an account id whose account already exists (D-26): uniqueness is
/// validated by the SDK account registry and rejected with this exception.
/// </summary>
public sealed class SdkDuplicateAccountException : SdkException
{
    /// <summary>Initializes a new instance of the <see cref="SdkDuplicateAccountException"/> class.</summary>
    /// <param name="message">Operation description — no host account values.</param>
    /// <param name="inner">Optional inner exception.</param>
    public SdkDuplicateAccountException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
