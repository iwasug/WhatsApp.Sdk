using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Iwasug.WhatsApp.Sdk.Hosting;
using Iwasug.WhatsApp.Sdk.Messaging;

namespace Iwasug.WhatsApp.Sdk.Accounts;

/// <summary>
/// Composition-ready account lifecycle seam: registers, observes, and removes many
/// WhatsApp accounts in one process. Each account gets an isolated per-account graph
/// (store session, connection, messenger, safety gate, outbound queue, cancellation).
/// </summary>
public interface IWhatsAppSessionManager : IAsyncDisposable
{
#pragma warning disable RS0026
    /// <summary>Registers a new account. Throws <see cref="Errors.SdkDuplicateAccountException"/> when the id already exists.</summary>
    ValueTask AddAccountAsync(AccountId accountId, CancellationToken cancellationToken = default);

    /// <summary>Registers a new account from an <see cref="Hosting.AccountDescriptor"/>. Throws <see cref="Errors.SdkDuplicateAccountException"/> when the id already exists.</summary>
    ValueTask AddAccountAsync(Hosting.AccountDescriptor descriptor, CancellationToken cancellationToken = default);

    /// <summary>Removes and disposes an account. Unknown ids are a no-op.</summary>
    ValueTask RemoveAccountAsync(AccountId accountId, CancellationToken cancellationToken = default);

    /// <summary>Rebuilds one account's graph on a fresh store session. Throws <see cref="Errors.SdkUnknownAccountException"/> for unknown ids.</summary>
    ValueTask RestartAccountAsync(AccountId accountId, CancellationToken cancellationToken = default);

    /// <summary>Returns the status snapshot for one account, or null when unknown.</summary>
    AccountStatus? GetStatus(AccountId accountId);

    /// <summary>Returns status snapshots for every registered account.</summary>
    IReadOnlyCollection<AccountStatus> GetStatuses();

    /// <summary>
    /// Resolves the live connection for <paramref name="accountId"/> (07 CR-01) — the
    /// handle a host needs to start pairing or drive the lifecycle itself. Returns
    /// <c>false</c> for unknown ids. The returned instance is owned by the manager and is
    /// disposed with the account: callers must not dispose it.
    /// </summary>
    bool TryGetConnection(AccountId accountId, [NotNullWhen(true)] out Connection.WaConnection? connection);

    /// <summary>
    /// Resolves the live per-account messenger for <paramref name="accountId"/> (07 WR-08) —
    /// the handle a host needs to send and to observe one account's event stream. Returns
    /// <c>false</c> for unknown ids. The returned instance is owned by the manager and is
    /// disposed with the account: callers must not dispose it.
    /// </summary>
    bool TryGetMessenger(AccountId accountId, [NotNullWhen(true)] out Messenger? messenger);

    /// <summary>Merged event stream across all accounts.</summary>
    IAsyncEnumerable<AccountEvent> ReadEventsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Event stream for one account. Throws <see cref="Errors.SdkUnknownAccountException"/>
    /// for unknown ids. Because this is an async iterator, that exception (and resolution of
    /// the account itself) happens at the first <c>MoveNextAsync</c>, not when the method is
    /// called — the returned enumerable is inert until enumerated.
    /// </summary>
    IAsyncEnumerable<WhatsAppEvent> ReadAccountEventsAsync(AccountId accountId, CancellationToken cancellationToken = default);
}
#pragma warning restore RS0026