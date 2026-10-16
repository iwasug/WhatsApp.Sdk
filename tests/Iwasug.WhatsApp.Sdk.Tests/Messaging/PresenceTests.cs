using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Messaging;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Messaging;

public sealed class PresenceTests
{
    [Theory]
    [InlineData(PresenceState.Available)]
    [InlineData(PresenceState.Unavailable)]
    [InlineData(PresenceState.Composing)]
    [InlineData(PresenceState.Recording)]
    [InlineData(PresenceState.Paused)]
    public void PresenceState_declares_all_five_states(PresenceState state)
    {
        ((int)state).Should().BeGreaterThanOrEqualTo(0);
    }
}