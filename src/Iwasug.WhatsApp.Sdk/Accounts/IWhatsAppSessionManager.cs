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

    /// <summary>Merged event stream across all accounts.</summary>
    IAsyncEnumerable<AccountEvent> ReadEventsAsync(CancellationToken cancellationToken = default);

    /// <summary>Event stream for one account. Throws <see cref="Errors.SdkUnknownAccountException"/> for unknown ids.</summary>
    IAsyncEnumerable<WhatsAppEvent> ReadAccountEventsAsync(AccountId accountId, CancellationToken cancellationToken = default);
}
#pragma warning restore RS0026