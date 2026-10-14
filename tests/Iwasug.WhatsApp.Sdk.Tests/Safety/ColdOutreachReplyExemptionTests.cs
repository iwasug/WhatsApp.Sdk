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
/// SAF-04 reply exemption: a reply to a never-contacted recipient never consumes the
/// cold-outreach budget, while a normal first send to that recipient does.
/// </summary>
public sealed class ColdOutreachReplyExemptionTests
{
    private static Jid Chat(string user) => Jid.Parse($"{user}@s.whatsapp.net");

    private static async Task<StoreSession> CreateStoresAsync(string account)
    {
        var registry = new StoreRegistry(new StoreRegistryOptions());
        return await registry.CreateSessionAsync(AccountId.Create(account));
    }

    private static SafetyOptions NoJitter(int coldCap) => new()
    {
        AllowUnsafeThroughput = true,
        MaxMessagesPerMinute = 100,
        MaxColdOutreachPerDay = coldCap,
        BaseInterSendJitter = TimeSpan.Zero,
        // Zero pacing: the 05-04 presence pause is a mandatory gate stage, but
        // these pre-pacing tests prove reply-exemption behavior, not pause timing.
        PacingPresenceMinMs = 0,
        PacingPresenceMaxMs = 0,
    };

    [Fact]
    public async Task reply_exempt_from_cap_when_budget_exhausted()
    {
        var clock = new FakeTimeProvider();
        StoreSession session = await CreateStoresAsync("cold-reply-exempt");
        var budget = new ColdOutreachBudget(clock, 1);

        // Spend the single daily slot on a normal cold send.
        (await budget.CheckAsync(
            new SendContext(Chat("15550000061"), IsReply: false),
            session.Recipients,
            CancellationToken.None)).Allowed.Should().BeTrue();

        // A reply to a never-contacted recipient is still admitted: replies never
        // consult the cap, even when normal cold outreach is exhausted.
        (bool allowed, bool isCold) = await budget.CheckAsync(
            new SendContext(Chat("15550000062"), IsReply: true),
            session.Recipients,
            CancellationToken.None);

        allowed.Should().BeTrue();
        isCold.Should().BeTrue("the reply target is still a first contact — exempt, not free");
    }

    [Fact]
    public async Task reply_sends_do_not_consume_daily_budget()
    {
        var clock = new FakeTimeProvider();
        StoreSession session = await CreateStoresAsync("cold-reply-no-consume");
        var budget = new ColdOutreachBudget(clock, 1);

        // Any number of exempt replies first...
        for (int i = 0; i < 5; i++)
        {
            (await budget.CheckAsync(
                new SendContext(Chat($"1555000007{i}"), IsReply: true),
                session.Recipients,
                CancellationToken.None)).Allowed.Should().BeTrue();
        }

        // ...then a normal first send to a stranger still has its full slot.
        (await budget.CheckAsync(
            new SendContext(Chat("15550000080"), IsReply: false),
            session.Recipients,
            CancellationToken.None)).Allowed.Should().BeTrue();

        // And only now is the single slot spent.
        Func<Task<(bool Allowed, bool IsCold)>> spent = () => budget.CheckAsync(
            new SendContext(Chat("15550000081"), IsReply: false),
            session.Recipients,
            CancellationToken.None);
        await spent.Should().ThrowAsync<SafetyRuleViolationException>();
    }

    [Fact]
    public async Task reply_exempt_from_mark_normal_send_marks()
    {
        var clock = new FakeTimeProvider();
        StoreSession session = await CreateStoresAsync("cold-reply-mark");
        var gate = new SafetyGate(
            clock,
            NoJitter(coldCap: 30),
            logger: null,
            resolveStores: _ => ValueTask.FromResult(session),
            publish: (_, _) => ValueTask.CompletedTask);

        // A successful reply does not mark the recipient (marking is normal-sends only).
        SendOutcome reply = await gate.ExecuteSendAsync(
            new SendContext(Chat("15550000091"), IsReply: true),
            CancellationToken.None,
            _ => Task.FromResult(new SendOutcome("reply-1", SendOutcomeStatus.Sent)));
        reply.Status.Should().Be(SendOutcomeStatus.Sent);
        (await session.Recipients.HasContactedAsync("15550000091@s.whatsapp.net", CancellationToken.None))
            .Should().BeFalse("reply exemption covers the cap AND the mark");

        // The follow-up normal send to the same recipient is therefore still cold —
        // and marks on success.
        SendOutcome normal = await gate.ExecuteSendAsync(
            new SendContext(Chat("15550000091"), IsReply: false),
            CancellationToken.None,
            _ => Task.FromResult(new SendOutcome("normal-1", SendOutcomeStatus.Sent)));
        normal.Status.Should().Be(SendOutcomeStatus.Sent);
        (await session.Recipients.HasContactedAsync("15550000091@s.whatsapp.net", CancellationToken.None)).Should().BeTrue();
    }
}
