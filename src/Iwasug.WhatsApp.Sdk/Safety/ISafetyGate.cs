using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Messaging;

namespace Iwasug.WhatsApp.Sdk.Safety;

/// <summary>The context for one gated send.</summary>
/// <param name="Chat">The destination chat JID.</param>
/// <param name="IsReply">Whether the send is a reply (cold-outreach exemption).</param>
public sealed record SendContext(Jid Chat, bool IsReply);

/// <summary>The single mandatory gate seam every public send traverses.</summary>
public interface ISafetyGate
{
    /// <summary>Executes the safety sequence, then invokes <paramref name="innerSend"/> only if all stages pass.</summary>
    Task<SendOutcome> ExecuteSendAsync(
        SendContext context,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<SendOutcome>> innerSend);
}
