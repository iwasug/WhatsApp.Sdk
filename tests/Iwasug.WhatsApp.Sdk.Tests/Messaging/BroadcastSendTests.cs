using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Messaging;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Messaging;

public sealed class BroadcastSendTests
{
    [Fact]
    public void Broadcast_types_construct()
    {
        var outcome = new BroadcastRecipientOutcome(
            Jid.Parse("15551234567@s.whatsapp.net"),
            new SendOutcome("MSG", SendOutcomeStatus.Sent));
        var options = new BroadcastOptions(TimeSpan.FromMilliseconds(5));
        options.InterRecipientDelay.Should().Be(TimeSpan.FromMilliseconds(5));
        outcome.Result.Status.Should().Be(SendOutcomeStatus.Sent);
    }
}