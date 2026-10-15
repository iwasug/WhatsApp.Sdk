using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Media;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Messaging;
using Iwasug.WhatsApp.Sdk.Safety;
using Iwasug.WhatsApp.Sdk.Stores;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public class MediaSafetyTests
{
    [Fact]
    public void MimetypeExecutableRejected()
    {
        Assert.Throws<Iwasug.WhatsApp.Sdk.Errors.SdkFormatException>(() =>
            MediaMetadata.ValidateMimetype(MediaKind.Document, "application/x-msdownload"));
    }

    [Fact]
    public void TrackerMaxRequests() => Assert.Equal(3, MediaRetryTracker.MaxRequestsPerId);

    private static Jid Chat(string user) => Jid.Parse($"{user}@s.whatsapp.net");

    private static async Task<StoreSession> CreateStoresAsync(string account)
    {
        var registry = new StoreRegistry(new StoreRegistryOptions());
        return await registry.CreateSessionAsync(AccountId.Create(account));
    }

    [Fact]
    public async Task SafetyGateRefusalNeverInvokesInnerForMediaSend()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 15, 0, 0, TimeSpan.Zero));
        StoreSession session = await CreateStoresAsync("media-safety-refusal");
        var options = new SafetyOptions
        {
            AllowUnsafeThroughput = true,
            DeclaredUnsafeAcknowledgment = "refusal fact",
            MaxMessagesPerMinute = 1000,
            MaxMessagesPerHour = 1000,
            MaxMessagesPerDay = 1000,
            MaxColdOutreachPerDay = 1000,
            BaseInterSendJitter = TimeSpan.Zero,
            ActiveHoursStart = TimeSpan.FromHours(10),
            ActiveHoursEnd = TimeSpan.FromHours(11),
            PacingPresenceMinMs = 0,
            PacingPresenceMaxMs = 0,
        };
        var gate = new SafetyGate(clock, options, null, _ => ValueTask.FromResult(session), (_, _) => ValueTask.CompletedTask);

        int innerCalls = 0;
        Task<SendOutcome> Inner(CancellationToken _) { innerCalls++; return Task.FromResult(new SendOutcome("x", SendOutcomeStatus.Sent)); }

        Func<Task> act = () => gate.ExecuteSendAsync(new SendContext(Chat("1555000001"), IsReply: false), CancellationToken.None, Inner);
        await act.Should().ThrowAsync<SafetyRuleViolationException>();

        innerCalls.Should().Be(0, "a refusing safety gate never reaches the inner send");
    }
}