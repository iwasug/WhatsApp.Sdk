#pragma warning disable xUnit1051
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Messaging;
using Iwasug.WhatsApp.Sdk.Safety;
using Iwasug.WhatsApp.Sdk.Stores;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Safety;

/// <summary>
/// SAF-03 gate-level warm-up enforcement proofs (05-06): a day-0 account is
/// refused on its 21st send of a UTC day with
/// <see cref="SafetyRuleViolationException"/> carrying
/// <see cref="SafetyRule.RateLimit"/> before the inner send, and the refusal
/// emits <c>BudgetExhausted</c> exactly once (handler twin + stream twin).
/// The harness mirrors <c>SafetyCircuitBreakerTests</c> — raised
/// cold-outreach cap, full-day active window, zero jitter and zero pacing
/// presence — so only the warm-up ceiling can trip.
/// </summary>
public sealed class SafetyGateWarmupTests
{
    private static Jid Chat(string user) => Jid.Parse($"{user}@s.whatsapp.net");

    private static DateTimeOffset At(int hour, int day = 14) =>
        new(2026, 10, day, hour, 0, 0, TimeSpan.Zero);

    private static async Task<StoreSession> CreateStoresAsync(string account)
    {
        var registry = new StoreRegistry(new StoreRegistryOptions());
        return await registry.CreateSessionAsync(AccountId.Create(account));
    }

    private static SafetyOptions GateOptions() => new()
    {
        AllowUnsafeThroughput = true,
        DeclaredUnsafeAcknowledgment = "Warm-up ceiling test with zero jitter and pacing.",
        MaxMessagesPerMinute = 1000,
        MaxMessagesPerHour = 1000,
        MaxMessagesPerDay = 1000,
        MaxColdOutreachPerDay = 1000,
        BaseInterSendJitter = TimeSpan.Zero,
        ActiveHoursStart = TimeSpan.Zero,
        ActiveHoursEnd = TimeSpan.FromHours(24),
        PacingPresenceMinMs = 0,
        PacingPresenceMaxMs = 0,
    };

    private static SafetyGate CreateGate(
        FakeTimeProvider clock,
        StoreSession session,
        SafetyOptions options,
        List<BudgetExhaustedEventArgs>? exhausted = null,
        List<WhatsAppEvent>? published = null)
    {
        var gate = new SafetyGate(
            clock,
            options,
            logger: null,
            resolveStores: _ => ValueTask.FromResult(session),
            publish: (evt, _) =>
            {
                published?.Add(evt);
                return ValueTask.CompletedTask;
            });
        if (exhausted is not null)
        {
            gate.BudgetExhausted += (_, args) => exhausted.Add(args);
        }

        return gate;
    }

    [Fact]
    public async Task warmup_ceiling_rejects_21st_send_before_inner()
    {
        var clock = new FakeTimeProvider(At(12));
        StoreSession session = await CreateStoresAsync("warmup-gate-ceiling");
        SafetyGate gate = CreateGate(clock, session, GateOptions());
        int innerCalls = 0;
        Task<SendOutcome> Inner(CancellationToken ct)
        {
            innerCalls++;
            return Task.FromResult(new SendOutcome($"warmup-{innerCalls}", SendOutcomeStatus.Sent));
        }

        for (int i = 0; i < 20; i++)
        {
            SendOutcome outcome = await gate.ExecuteSendAsync(
                new SendContext(Chat($"155520000{i:D2}"), IsReply: false), CancellationToken.None, Inner);
            outcome.Status.Should().Be(SendOutcomeStatus.Sent);
        }

        innerCalls.Should().Be(20);

        Func<Task> over = () => gate.ExecuteSendAsync(
            new SendContext(Chat("1555200099"), IsReply: false), CancellationToken.None, Inner);
        (await over.Should().ThrowAsync<SafetyRuleViolationException>())
            .And.Rule.Should().Be(SafetyRule.RateLimit);
        innerCalls.Should().Be(20, "an over-ceiling send never reaches the inner send");
    }

    [Fact]
    public async Task warmup_rejection_emits_budget_exhausted_once()
    {
        var clock = new FakeTimeProvider(At(12));
        StoreSession session = await CreateStoresAsync("warmup-gate-events");
        var exhausted = new List<BudgetExhaustedEventArgs>();
        var published = new List<WhatsAppEvent>();
        var restricted = new List<AccountRestrictedEventArgs>();
        SafetyGate gate = CreateGate(clock, session, GateOptions(), exhausted, published);
        gate.AccountRestricted += (_, args) => restricted.Add(args);
        int innerCalls = 0;
        Task<SendOutcome> Inner(CancellationToken _)
        {
            innerCalls++;
            return Task.FromResult(new SendOutcome($"wu-{innerCalls}", SendOutcomeStatus.Sent));
        }

        for (int i = 0; i < 20; i++)
        {
            await gate.ExecuteSendAsync(
                new SendContext(Chat($"155530000{i:D2}"), IsReply: false), CancellationToken.None, Inner);
        }

        Func<Task> over = () => gate.ExecuteSendAsync(
            new SendContext(Chat("1555300099"), IsReply: false), CancellationToken.None, Inner);
        (await over.Should().ThrowAsync<SafetyRuleViolationException>())
            .And.Rule.Should().Be(SafetyRule.RateLimit);

        exhausted.Should().HaveCount(1, "warm-up rejection emits BudgetExhausted exactly once");
        published.OfType<BudgetExhaustedEvent>().Should().HaveCount(1, "the stream twin fires alongside the handler event exactly once");
        restricted.Should().BeEmpty("warm-up rejection never raises AccountRestricted");
        published.OfType<AccountRestrictedEvent>().Should().BeEmpty("warm-up rejection never publishes an AccountRestricted stream event");
    }
}
