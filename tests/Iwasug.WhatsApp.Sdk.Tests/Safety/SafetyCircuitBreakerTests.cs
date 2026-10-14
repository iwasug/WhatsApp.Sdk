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
/// SAF-05 gate integration proofs (05-04 Task 3): the breaker trips through the
/// full <c>ExecuteSendAsync</c> sequence, emits <c>AccountRestricted</c> exactly
/// once per opening, recovers on a successful half-open probe, and the stage
/// order (circuit → active-hours → rate-limits → … → inner → record) holds
/// behaviorally. Cancellation propagates with no events.
/// </summary>
public sealed class SafetyCircuitBreakerTests
{
    private static Jid Chat(string user) => Jid.Parse($"{user}@s.whatsapp.net");

    private static DateTimeOffset At(int hour, int day = 14) =>
        new(2026, 10, day, hour, 0, 0, TimeSpan.Zero);

    private static async Task<StoreSession> CreateStoresAsync(string account)
    {
        var registry = new StoreRegistry(new StoreRegistryOptions());
        return await registry.CreateSessionAsync(AccountId.Create(account));
    }

    private static SafetyOptions GateOptions(
        TimeSpan? windowStart = null,
        TimeSpan? windowEnd = null,
        int perMinute = 1000) => new()
    {
        AllowUnsafeThroughput = true,
        DeclaredUnsafeAcknowledgment = "Circuit-breaker test with zero jitter and pacing.",
        MaxMessagesPerMinute = perMinute,
        MaxMessagesPerHour = 1000,
        MaxMessagesPerDay = 1000,
        MaxColdOutreachPerDay = 1000,
        BaseInterSendJitter = TimeSpan.Zero,
        ActiveHoursStart = windowStart ?? TimeSpan.Zero,
        ActiveHoursEnd = windowEnd ?? TimeSpan.FromHours(24),
        PacingPresenceMinMs = 0,
        PacingPresenceMaxMs = 0,
    };

    private static SafetyGate CreateGate(
        FakeTimeProvider clock,
        StoreSession session,
        SafetyOptions options,
        List<AccountRestrictedEventArgs>? restricted = null,
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
        if (restricted is not null)
        {
            gate.AccountRestricted += (_, args) => restricted.Add(args);
        }

        return gate;
    }

    private static SendOutcome ServerError(string id) =>
        new(id, SendOutcomeStatus.ServerError, Error: 429);

    [Fact]
    public async Task gate_records_server_errors_then_trips_breaker_once()
    {
        var clock = new FakeTimeProvider(At(12));
        StoreSession session = await CreateStoresAsync("breaker-gate-trip");
        var restricted = new List<AccountRestrictedEventArgs>();
        var published = new List<WhatsAppEvent>();
        SafetyGate gate = CreateGate(clock, session, GateOptions(), restricted, published);
        int innerCalls = 0;
        Task<SendOutcome> Inner(CancellationToken ct)
        {
            innerCalls++;
            return Task.FromResult(ServerError($"srv-{innerCalls}"));
        }

        // Five failing sends settle their ServerError outcomes without throwing.
        for (int i = 0; i < 5; i++)
        {
            SendOutcome outcome = await gate.ExecuteSendAsync(
                new SendContext(Chat($"1555100000{i}"), IsReply: false), CancellationToken.None, Inner);
            outcome.Status.Should().Be(SendOutcomeStatus.ServerError);
        }

        innerCalls.Should().Be(5);
        restricted.Should().HaveCount(1, "the fifth failure is the single Open transition");
        published.OfType<AccountRestrictedEvent>().Should().HaveCount(1, "the stream twin fires alongside the handler event exactly once");

        // The sixth send refuses before the inner send, with no second event.
        Func<Task> blocked = () => gate.ExecuteSendAsync(
            new SendContext(Chat("15551000009"), IsReply: false), CancellationToken.None, Inner);
        (await blocked.Should().ThrowAsync<SafetyRuleViolationException>())
            .And.Rule.Should().Be(SafetyRule.CircuitOpen);

        innerCalls.Should().Be(5, "an open breaker never reaches the inner send");
        restricted.Should().HaveCount(1, "no duplicate AccountRestricted while already open");
        published.OfType<AccountRestrictedEvent>().Should().HaveCount(1);
    }

    [Fact]
    public async Task gate_half_open_probe_success_closes_breaker()
    {
        var clock = new FakeTimeProvider(At(12));
        StoreSession session = await CreateStoresAsync("breaker-gate-probe");
        var restricted = new List<AccountRestrictedEventArgs>();
        SafetyGate gate = CreateGate(clock, session, GateOptions(), restricted);
        Task<SendOutcome> Fail(CancellationToken _) => Task.FromResult(ServerError("srv"));
        Task<SendOutcome> Succeed(CancellationToken _) => Task.FromResult(new SendOutcome("ok", SendOutcomeStatus.Sent));

        for (int i = 0; i < 5; i++)
        {
            await gate.ExecuteSendAsync(new SendContext(Chat($"155510001{i}"), IsReply: false), CancellationToken.None, Fail);
        }

        restricted.Should().HaveCount(1);

        // Backoff (30s) elapses: the next send is the single half-open probe.
        clock.Advance(TimeSpan.FromSeconds(30));
        SendOutcome probe = await gate.ExecuteSendAsync(
            new SendContext(Chat("1555100019"), IsReply: false), CancellationToken.None, Succeed);
        probe.Status.Should().Be(SendOutcomeStatus.Sent);

        // Closed again: follow-up sends flow with no further events.
        SendOutcome next = await gate.ExecuteSendAsync(
            new SendContext(Chat("1555100020"), IsReply: false), CancellationToken.None, Succeed);
        next.Status.Should().Be(SendOutcomeStatus.Sent);
        restricted.Should().HaveCount(1, "recovery fires no event");
    }

    [Fact]
    public async Task gate_circuit_open_checked_before_active_hours()
    {
        // Seconds-precision window: the trip sends run inside it, then the clock
        // moves outside the window but still inside the 30s breaker backoff — so
        // the circuit refusal must win, proving CheckCircuit runs before
        // CheckActiveHours. (Moving hours past the backoff would rightly admit
        // the half-open probe instead.)
        var tripAt = new DateTimeOffset(2026, 10, 14, 12, 0, 10, TimeSpan.Zero);
        var outsideAt = new DateTimeOffset(2026, 10, 14, 12, 0, 25, TimeSpan.Zero);
        var clock = new FakeTimeProvider(tripAt);
        StoreSession session = await CreateStoresAsync("breaker-gate-order-circuit");
        SafetyGate gate = CreateGate(
            clock,
            session,
            GateOptions(
                new TimeSpan(12, 0, 0),
                new TimeSpan(12, 0, 20)));
        Task<SendOutcome> Fail(CancellationToken _) => Task.FromResult(ServerError("srv"));

        for (int i = 0; i < 5; i++)
        {
            await gate.ExecuteSendAsync(new SendContext(Chat($"155510003{i}"), IsReply: false), CancellationToken.None, Fail);
        }

        // 15s after the trip: outside the 20s window, inside the 30s backoff.
        clock.SetUtcNow(outsideAt);
        int innerCalls = 0;
        Func<Task> act = () => gate.ExecuteSendAsync(
            new SendContext(Chat("1555100039"), IsReply: false),
            CancellationToken.None,
            ct =>
            {
                innerCalls++;
                return Task.FromResult(new SendOutcome("x", SendOutcomeStatus.Sent));
            });

        (await act.Should().ThrowAsync<SafetyRuleViolationException>())
            .And.Rule.Should().Be(SafetyRule.CircuitOpen);
        innerCalls.Should().Be(0);
    }

    [Fact]
    public async Task gate_active_hours_checked_before_rate_limits()
    {
        // Single-send minute bucket with a narrow window; clock starts outside it.
        var clock = new FakeTimeProvider(At(20));
        StoreSession session = await CreateStoresAsync("breaker-gate-order-hours");
        SafetyGate gate = CreateGate(
            clock, session, GateOptions(TimeSpan.FromHours(9), TimeSpan.FromHours(17), perMinute: 1));
        int innerCalls = 0;
        Task<SendOutcome> Succeed(CancellationToken ct)
        {
            innerCalls++;
            return Task.FromResult(new SendOutcome($"ok-{innerCalls}", SendOutcomeStatus.Sent));
        }

        // Outside the window with a fresh bucket: hours must win, leaving the
        // bucket untouched for the in-window sends below.
        Func<Task> outside = () => gate.ExecuteSendAsync(
            new SendContext(Chat("1555100041"), IsReply: false), CancellationToken.None, Succeed);
        (await outside.Should().ThrowAsync<SafetyRuleViolationException>())
            .And.Rule.Should().Be(SafetyRule.InactiveHours);
        innerCalls.Should().Be(0);

        clock.SetUtcNow(At(12, day: 15));
        SendOutcome first = await gate.ExecuteSendAsync(
            new SendContext(Chat("1555100042"), IsReply: false), CancellationToken.None, Succeed);
        first.Status.Should().Be(SendOutcomeStatus.Sent);

        Func<Task> over = () => gate.ExecuteSendAsync(
            new SendContext(Chat("1555100043"), IsReply: false), CancellationToken.None, Succeed);
        (await over.Should().ThrowAsync<SafetyRuleViolationException>())
            .And.Rule.Should().Be(SafetyRule.RateLimit, "the untouched bucket admits exactly one in-window send");
    }

    [Fact]
    public async Task gate_active_hours_violation_skips_inner_and_breaker_record()
    {
        var clock = new FakeTimeProvider(At(20));
        StoreSession session = await CreateStoresAsync("breaker-gate-hours-skip");
        SafetyGate gate = CreateGate(
            clock, session, GateOptions(TimeSpan.FromHours(9), TimeSpan.FromHours(17)));
        int innerCalls = 0;
        Task<SendOutcome> Succeed(CancellationToken ct)
        {
            innerCalls++;
            return Task.FromResult(new SendOutcome($"ok-{innerCalls}", SendOutcomeStatus.Sent));
        }

        Func<Task> act = () => gate.ExecuteSendAsync(
            new SendContext(Chat("1555100051"), IsReply: false), CancellationToken.None, Succeed);
        (await act.Should().ThrowAsync<SafetyRuleViolationException>())
            .And.Rule.Should().Be(SafetyRule.InactiveHours);
        innerCalls.Should().Be(0, "a rejected stage never reaches the inner send");

        // The violation skipped RecordOutcome too: the breaker is still Closed,
        // so an in-window send flows with no circuit refusal.
        clock.SetUtcNow(At(12, day: 15));
        SendOutcome outcome = await gate.ExecuteSendAsync(
            new SendContext(Chat("1555100052"), IsReply: false), CancellationToken.None, Succeed);
        outcome.Status.Should().Be(SendOutcomeStatus.Sent);
    }

    [Fact]
    public async Task gate_cancelled_send_propagates_without_events()
    {
        var clock = new FakeTimeProvider(At(12));
        StoreSession session = await CreateStoresAsync("breaker-gate-cancel");
        var published = new List<WhatsAppEvent>();
        var restricted = new List<AccountRestrictedEventArgs>();
        var throttled = new List<ThrottleEngagedEventArgs>();
        var exhausted = new List<BudgetExhaustedEventArgs>();
        SafetyGate gate = CreateGate(clock, session, GateOptions(), restricted, published);
        gate.ThrottleEngaged += (_, args) => throttled.Add(args);
        gate.BudgetExhausted += (_, args) => exhausted.Add(args);
        int innerCalls = 0;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => gate.ExecuteSendAsync(
            new SendContext(Chat("1555100061"), IsReply: false),
            cts.Token,
            ct =>
            {
                innerCalls++;
                return Task.FromResult(new SendOutcome("x", SendOutcomeStatus.Sent));
            });

        await act.Should().ThrowAsync<OperationCanceledException>();
        innerCalls.Should().Be(0);
        published.Should().BeEmpty("a cancelled send emits no safety events");
        restricted.Should().BeEmpty();
        throttled.Should().BeEmpty();
        exhausted.Should().BeEmpty();
    }
}
