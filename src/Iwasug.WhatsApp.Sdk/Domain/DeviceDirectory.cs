using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Iwasug.WhatsApp.Sdk.Domain;

namespace Iwasug.WhatsApp.Sdk.Domain;

/// <summary>
/// Resolves a peer's device set for message sending (04-03, internal).
/// Maps a recipient JID to one or more device JIDs by querying the host's
/// device directory / address book. Implementations may back by an EF Core
/// store, a Dapper query, an in-memory dictionary, or any persistent store.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Iwasug.WhatsApp.Sdk.Messaging.MessageSender"/> depends on this abstraction to discover
/// which devices a peer has online before attempting encryption and send.
/// </para>
/// <para>
/// Host implementations should ensure <c>ResolveAsync</c> is fast and
/// side-effect free (no network I/O in the critical path if possible), or
/// provide a warm-up/cache layer. The SDK does not impose a particular
/// storage schema — only that <c>ResolveAsync</c> returns the set of device
/// JIDs reachable for the given recipient.
/// </para>
/// </remarks>
public class DeviceDirectory
{
    /// <summary>
    /// Resolves the set of device JIDs for the given recipient.
    /// </summary>
    /// <param name="recipient">The recipient JID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that resolves to the set of device JIDs.</returns>
    /// <exception cref="Iwasug.WhatsApp.Sdk.Errors.SdkFormatException">Thrown if the recipient JID is invalid.</exception>
    public virtual async Task<IReadOnlyList<Jid>> ResolveAsync(Jid recipient, CancellationToken cancellationToken = default)
    {
        // Default implementation: return empty list (no devices resolved).
        // Hosts should replace this with their own resolution logic.
        await Task.Delay(0);
        return System.Collections.ObjectModel.ReadOnlyCollection<Jid>.Empty;
    }
}