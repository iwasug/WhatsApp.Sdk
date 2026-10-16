using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Safety;

namespace Iwasug.WhatsApp.Sdk.Messaging;

/// <summary>
/// Real (Phase 8) <see cref="IPacingPresence"/> implementation: an account-scoped
/// instance replacing the Phase-5 no-op seam. Outgoing pacing uses the
/// best-effort composing signal; explicit host calls use <see cref="SendAsync"/>
/// and fail closed. No static state is retained (Pitfall 6).
/// </summary>
internal sealed class PresenceManager : IPacingPresence
{
    private readonly WaConnection _connection;

    public PresenceManager(WaConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <summary>
    /// Account/host-facing presence send. Throws on connection or cancellation
    /// failures (fail-closed). Does not require an ack — the wire node is
    /// one-way, so a server reply <c>ack</c> is not awaited.
    /// </summary>
    public async Task SendAsync(PresenceState state, Jid? chat, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BinaryNode node = BuildNode(state, chat);
        await _connection.SendNodeAsync(node, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task SendComposingAsync(bool bestEffort, CancellationToken cancellationToken)
    {
        if (!bestEffort)
        {
            return SendAsync(PresenceState.Composing, null, cancellationToken);
        }

        return BestEffortAsync(PresenceState.Composing, null, cancellationToken);
    }

    private async Task BestEffortAsync(PresenceState state, Jid? chat, CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(state, chat, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Best-effort presence: a failing signal never fails the send promise.
        }
    }

    private static BinaryNode BuildNode(PresenceState state, Jid? chat)
    {
        Dictionary<string, string> attrs = new(StringComparer.Ordinal);
        if (chat is not null)
        {
            attrs["to"] = chat.ToString();
        }

        switch (state)
        {
            case PresenceState.Available:
                attrs["type"] = "available";
                return BinaryNode.Create("presence", attrs);
            case PresenceState.Unavailable:
                attrs["type"] = "unavailable";
                return BinaryNode.Create("presence", attrs);
            case PresenceState.Composing:
                return BinaryNode.FromChildren("chatstate", [BinaryNode.Create("composing")], attrs);
            case PresenceState.Recording:
                return BinaryNode.FromChildren("chatstate", [BinaryNode.Create("recording")], attrs);
            case PresenceState.Paused:
                return BinaryNode.FromChildren("chatstate", [BinaryNode.Create("paused")], attrs);
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown presence state.");
        }
    }
}