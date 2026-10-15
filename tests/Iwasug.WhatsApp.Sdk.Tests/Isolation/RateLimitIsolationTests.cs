using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Messaging;
using Iwasug.WhatsApp.Sdk.Safety;
using Iwasug.WhatsApp.Sdk.Stores;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Isolation;

public class RateLimitIsolationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static Jid Chat(string user) => Jid.Parse($"{user}@s.whatsapp.net");

    private static SafetyOptions GateOptions(int perMinute = 1000) => new()
    {
        AllowUnsafeThroughput = true,
        DeclaredUnsafeAcknowledgment = "Rate-limit isolation test with zero jitter and pacing.",
        MaxMessagesPerMinute = perMinute,
        MaxMessagesPerHour = 1000,
        MaxMessagesPerDay = 1000,
        MaxColdOutreachPerDay = 1000,
        BaseInterSendJitter = TimeSpan.Zero,
        ActiveHoursStart = TimeSpan.Zero,
        ActiveHoursEnd = TimeSpan.FromHours(24),
        PacingPresenceMinMs = 0,
        PacingPresenceMaxMs = 0,
    };

    private static async Task<StoreSession> CreateStoresAsync(string account)
    {
        var registry = new StoreRegistry(new StoreRegistryOptions());
        return await registry.CreateSessionAsync(AccountId.Create(account));
    }

    private static SafetyGate CreateGate(FakeTimeProvider clock, StoreSession session, SafetyOptions options,
        List<AccountRestrictedEventArgs>? restricted = null, List<BudgetExhaustedEventArgs>? exhausted = null)
    {
        SafetyGate gate = new(
            clock,
            options,
            logger: null,
            resolveStores: _ => ValueTask.FromResult(session),
            publish: (_, _) => ValueTask.CompletedTask);
        if (restricted is not null)
            gate.AccountRestricted += (_, args) => restricted.Add(args);
        if (exhausted is not null)
            gate.BudgetExhausted += (_, args) => exhausted.Add(args);
        return gate;
    }

    private static SendOutcome ServerError(string id) =>
        new(id, SendOutcomeStatus.ServerError, Error: 429);

    [Fact]
    public async Task Tripping_A_Breaker_Does_Not_Affect_B()
    {
        var clockA = new FakeTimeProvider(Now);
        var clockB = new FakeTimeProvider(Now);

        StoreSession sessionA = await CreateStoresAsync($"rl-a-{Guid.NewGuid():N}");
        StoreSession sessionB = await CreateStoresAsync($"rl-b-{Guid.NewGuid():N}");

        var restrictedA = new List<AccountRestrictedEventArgs>();
        SafetyGate gateA = CreateGate(clockA, sessionA, GateOptions(), restrictedA);
        SafetyGate gateB = CreateGate(clockB, sessionB, GateOptions());

        int innerCallsA = 0;
        Task<SendOutcome> InnerA(CancellationToken ct)
        {
            innerCallsA++;
            return Task.FromResult(ServerError($"a-{innerCallsA}"));
        }

        // Trip A's breaker.
        for (int i = 0; i < 5; i++)
        {
            SendOutcome outcome = await gateA.ExecuteSendAsync(
                new SendContext(Chat($"155500000{i}"), IsReply: false), CancellationToken.None, InnerA);
            outcome.Status.Should().Be(SendOutcomeStatus.ServerError);
        }

        // A refuses; B still admits.
        Func<Task> blockedA = () => gateA.ExecuteSendAsync(
            new SendContext(Chat("1555000009"), IsReply: false), CancellationToken.None, InnerA);
        (await blockedA.Should().ThrowAsync<SafetyRuleViolationException>()).And.Rule.Should().Be(SafetyRule.CircuitOpen);

        int innerCallsB = 0;
        Task<SendOutcome> InnerB(CancellationToken ct)
        {
            innerCallsB++;
            return Task.FromResult(new SendOutcome($"b-{innerCallsB}", SendOutcomeStatus.Sent));
        }

        SendOutcome bOutcome = await gateB.ExecuteSendAsync(
            new SendContext(Chat("1555999999"), IsReply: false), CancellationToken.None, InnerB);
        bOutcome.Status.Should().Be(SendOutcomeStatus.Sent);
        innerCallsB.Should().Be(1);
    }

    [Fact]
    public async Task Exhausting_A_Rate_Limit_Does_Not_Delay_B()
    {
        var clockA = new FakeTimeProvider(Now);
        var clockB = new FakeTimeProvider(Now);

        StoreSession sessionA = await CreateStoresAsync($"rl-rl-a-{Guid.NewGuid():N}");
        StoreSession sessionB = await CreateStoresAsync($"rl-rl-b-{Guid.NewGuid():N}");

        // A has a per-minute budget of 1.
        SafetyGate gateA = CreateGate(clockA, sessionA, GateOptions(perMinute: 1));
        SafetyGate gateB = CreateGate(clockB, sessionB, GateOptions(perMinute: 1000));

        int aCalls = 0;
        Task<SendOutcome> InnerA(CancellationToken ct)
        {
            aCalls++;
            return Task.FromResult(new SendOutcome($"a-{aCalls}", SendOutcomeStatus.Sent));
        }

        // First A send succeeds, second is rate-limited.
        SendOutcome firstA = await gateA.ExecuteSendAsync(
            new SendContext(Chat("1555111111"), IsReply: false), CancellationToken.None, InnerA);
        firstA.Status.Should().Be(SendOutcomeStatus.Sent);

        Func<Task> secondA = () => gateA.ExecuteSendAsync(
            new SendContext(Chat("1555222222"), IsReply: false), CancellationToken.None, InnerA);
        try
        {
            await secondA();
        }
        catch (SafetyRuleViolationException ex)
        {
            ex.Rule.Should().Be(SafetyRule.RateLimit);
        }

        // B admits promptly on its own clock.
        int bCalls = 0;
        Task<SendOutcome> InnerB(CancellationToken ct)
        {
            bCalls++;
            return Task.FromResult(new SendOutcome($"b-{bCalls}", SendOutcomeStatus.Sent));
        }

        DateTimeOffset before = clockB.GetUtcNow();
        SendOutcome bOutcome = await gateB.ExecuteSendAsync(
            new SendContext(Chat("1555333333"), IsReply: false), CancellationToken.None, InnerB);
        bOutcome.Status.Should().Be(SendOutcomeStatus.Sent);
        clockB.GetUtcNow().Should().Be(before, "B's clock does not advance because of A's exhaustion");
    }

    [Fact]
    public async Task Independent_TimeProviders_Do_Not_Leak()
    {
        var clockA = new FakeTimeProvider(Now);
        var clockB = new FakeTimeProvider(Now);

        StoreSession sessionA = await CreateStoresAsync($"rl-tp-a-{Guid.NewGuid():N}");
        StoreSession sessionB = await CreateStoresAsync($"rl-tp-b-{Guid.NewGuid():N}");

        SafetyGate gateA = CreateGate(clockA, sessionA, GateOptions(), restricted: null);
        SafetyGate gateB = CreateGate(clockB, sessionB, GateOptions());

        // Advance A's clock arbitrarily; B's timing remains independent.
        clockA.Advance(TimeSpan.FromHours(5));

        int bCalls = 0;
        Task<SendOutcome> InnerB(CancellationToken ct)
        {
            bCalls++;
            return Task.FromResult(new SendOutcome($"b-{bCalls}", SendOutcomeStatus.Sent));
        }

        SendOutcome outcome = await gateB.ExecuteSendAsync(
            new SendContext(Chat("1555444444"), IsReply: false), CancellationToken.None, InnerB);
        outcome.Status.Should().Be(SendOutcomeStatus.Sent);
        clockB.GetUtcNow().Should().Be(Now);
    }

    [Fact]
    public async Task No_Cross_Account_Safety_Events()
    {
        var clockA = new FakeTimeProvider(Now);
        var clockB = new FakeTimeProvider(Now);

        StoreSession sessionA = await CreateStoresAsync($"rl-ev-a-{Guid.NewGuid():N}");
        StoreSession sessionB = await CreateStoresAsync($"rl-ev-b-{Guid.NewGuid():N}");

        var restrictedA = new List<AccountRestrictedEventArgs>();
        var restrictedB = new List<AccountRestrictedEventArgs>();
        SafetyGate gateA = CreateGate(clockA, sessionA, GateOptions(), restrictedA);
        SafetyGate gateB = CreateGate(clockB, sessionB, GateOptions(), restrictedB);

        int innerCalls = 0;
        Task<SendOutcome> Inner(CancellationToken ct)
        {
            innerCalls++;
            return Task.FromResult(ServerError($"e-{innerCalls}"));
        }

        for (int i = 0; i < 5; i++)
        {
            await gateA.ExecuteSendAsync(
                new SendContext(Chat($"155550000{i}"), IsReply: false), CancellationToken.None, Inner);
        }

        restrictedA.Should().HaveCount(1);
        restrictedB.Should().BeEmpty("A-only budget mutations must not raise B safety events");
    }

    [Fact]
    public async Task Manager_Status_Reflects_Real_Breaker_State()
    {
        // 07 CR-02 / IN-10: the manager's status must reflect the real gate every send
        // traverses — tripping it through a send must surface SafetyCircuitState.Open.
        await using var manager = new Iwasug.WhatsApp.Sdk.Accounts.SessionManager(
            new DefaultAccountRuntimeFactory(defaultSafety: GateOptions()));
        AccountId a = AccountId.Create($"rl-mgr-a-{Guid.NewGuid():N}");
        AccountId b = AccountId.Create($"rl-mgr-b-{Guid.NewGuid():N}");
        await manager.AddAccountAsync(a, TestContext.Current.CancellationToken);
        await manager.AddAccountAsync(b, TestContext.Current.CancellationToken);

        IReadOnlyCollection<AccountStatus> statuses = manager.GetStatuses();
        statuses.Should().HaveCount(2);
        statuses.Select(s => s.AccountId).Should().Contain(new[] { a, b });
        foreach (AccountStatus status in statuses)
        {
            status.CircuitState.Should().Be(SafetyCircuitState.Closed, "no outcome has been recorded yet");
        }

        // Trip A's real gate (the one every send traverses) with five consecutive server errors.
        manager.TryGetRuntime(a, out var runtimeA).Should().BeTrue();
        for (int i = 0; i < 5; i++)
        {
            SendOutcome outcome = await runtimeA!.Messenger.SafetyGate.ExecuteSendAsync(
                new SendContext(Chat($"155500000{i}"), IsReply: false),
                TestContext.Current.CancellationToken,
                _ => Task.FromResult(ServerError($"mgr-a-{i}")));
            outcome.Status.Should().Be(SendOutcomeStatus.ServerError);
        }

        manager.GetStatus(a)!.CircuitState.Should().Be(SafetyCircuitState.Open);
        manager.GetStatus(b)!.CircuitState.Should().Be(SafetyCircuitState.Closed, "A's breaker must not affect B");
    }

    [Fact]
    public async Task Status_Facts_Have_Real_Writers_Not_Fabricated_Seeds()
    {
        // 07 WR-01: RecentFailures/LastConnectedAt/LastActivityAt/LastErrorRuleName must
        // reflect real facts — never a construction-time seed or an always-null field.
        var clock = new FakeTimeProvider(Now);
        await using var manager = new Iwasug.WhatsApp.Sdk.Accounts.SessionManager(
            new DefaultAccountRuntimeFactory(defaultSafety: GateOptions(), timeProvider: clock));
        AccountId a = AccountId.Create($"wr01-a-{Guid.NewGuid():N}");
        await manager.AddAccountAsync(a, TestContext.Current.CancellationToken);

        AccountStatus initial = manager.GetStatus(a)!;
        initial.RecentFailures.Should().Be(0);
        initial.LastConnectedAt.Should().BeNull("an account that never opened must not report a connection time");
        initial.LastActivityAt.Should().BeNull("no activity has happened yet");
        initial.LastErrorRuleName.Should().BeNull();

        // Five consecutive server errors trip the real breaker: every settled send and the
        // breaker-open AccountRestricted fact must be recorded on the status snapshot.
        manager.TryGetRuntime(a, out var runtime).Should().BeTrue();
        for (int i = 0; i < 5; i++)
        {
            await runtime!.Messenger.SafetyGate.ExecuteSendAsync(
                new SendContext(Chat($"155510000{i}"), IsReply: false),
                TestContext.Current.CancellationToken,
                _ => Task.FromResult(ServerError($"wr01-{i}")));
        }

        AccountStatus after = manager.GetStatus(a)!;
        after.RecentFailures.Should().Be(1, "the single AccountRestricted fact for the breaker opening is counted exactly once (07 IN-10)");
        after.LastActivityAt.Should().Be(Now);
        after.LastErrorRuleName.Should().Be(nameof(SafetyRule.CircuitOpen));
        after.LastConnectedAt.Should().BeNull("no open transition has happened, even though faults did");
    }
}