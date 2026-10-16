#pragma warning disable xUnit1051
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Messaging;
using Iwasug.WhatsApp.Sdk.Safety;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.InMemory;
using Iwasug.WhatsApp.Sdk.Testing.FaultInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Safety;

/// <summary>
/// SAF-04 cold-outreach budget: daily cap enforcement, hard block before the inner
/// send, UTC-midnight reset, and mark-on-success semantics through the gate.
/// </summary>
public sealed class ColdOutreachBudgetTests
{
    private static Jid Chat(string user) => Jid.Parse($"{user}@s.whatsapp.net");

    private static async Task<StoreSession> CreateStoresAsync(string account)
    {
        var registry = new StoreRegistry(new StoreRegistryOptions());
        return await registry.CreateSessionAsync(AccountId.Create(account));
    }

    private static SafetyGate CreateGate(
        FakeTimeProvider clock,
        StoreSession session,
        SafetyOptions options,
        Action<AccountRestrictedEventArgs>? onRestricted = null,
        Action<WhatsAppEvent>? onPublished = null)
    {
        var gate = new SafetyGate(
            clock,
            options,
            logger: null,
            resolveStores: _ => ValueTask.FromResult(session),
            publish: (evt, _) =>
            {
                onPublished?.Invoke(evt);
                return ValueTask.CompletedTask;
            });
        if (onRestricted is not null)
        {
            gate.AccountRestricted += (_, args) => onRestricted(args);
        }

        return gate;
    }

    private static SafetyOptions NoJitter(int coldCap) => new()
    {
        AllowUnsafeThroughput = true,
        DeclaredUnsafeAcknowledgment = "Cold-outreach test with zero jitter and pacing.",
        MaxMessagesPerMinute = 100,
        MaxColdOutreachPerDay = coldCap,
        BaseInterSendJitter = TimeSpan.Zero,
        // Zero pacing: the 05-04 presence pause is a mandatory gate stage, but
        // these pre-pacing tests prove cold-outreach behavior, not pause timing.
        PacingPresenceMinMs = 0,
        PacingPresenceMaxMs = 0,
    };

    [Fact]
    public async Task first_contact_counts_against_daily_cap()
    {
        var clock = new FakeTimeProvider();
        StoreSession session = await CreateStoresAsync("cold-first-contact");
        var budget = new ColdOutreachBudget(clock, 30);

        (bool allowed, bool isCold) = await budget.CheckAsync(
            new SendContext(Chat("15550000001"), IsReply: false),
            session.Recipients,
            CancellationToken.None);

        allowed.Should().BeTrue();
        isCold.Should().BeTrue("a never-messaged recipient is cold and consumes budget");
    }

    [Fact]
    public async Task contacted_recipient_bypasses_cap_without_consuming()
    {
        var clock = new FakeTimeProvider();
        StoreSession session = await CreateStoresAsync("cold-contacted-bypass");
        await session.Recipients.MarkContactedAsync("15550000002@s.whatsapp.net", CancellationToken.None);
        var budget = new ColdOutreachBudget(clock, 1);

        // Exhaust the single cold slot with a stranger, then prove the contacted
        // recipient is still admitted (contacted sends never consult the cap).
        (bool strangerAllowed, _) = await budget.CheckAsync(
            new SendContext(Chat("15550000003"), IsReply: false),
            session.Recipients,
            CancellationToken.None);
        strangerAllowed.Should().BeTrue();

        (bool allowed, bool isCold) = await budget.CheckAsync(
            new SendContext(Chat("15550000002"), IsReply: false),
            session.Recipients,
            CancellationToken.None);

        allowed.Should().BeTrue();
        isCold.Should().BeFalse("an already-contacted recipient is never cold");
    }

    [Fact]
    public async Task cap_block_raises_account_restricted_before_inner_send()
    {
        var clock = new FakeTimeProvider();
        StoreSession session = await CreateStoresAsync("cold-cap-block");
        AccountRestrictedEventArgs? restricted = null;
        WhatsAppEvent? published = null;
        SafetyGate gate = CreateGate(
            clock,
            session,
            NoJitter(coldCap: 2),
            onRestricted: args => restricted = args,
            onPublished: evt => published = evt);
        int innerCalls = 0;
        Task<SendOutcome> Inner(CancellationToken ct)
        {
            innerCalls++;
            return Task.FromResult(new SendOutcome($"id-{innerCalls}", SendOutcomeStatus.Sent));
        }

        // Two cold normal sends consume the cap of two (each marks on success).
        await gate.ExecuteSendAsync(new SendContext(Chat("15550000011"), IsReply: false), CancellationToken.None, Inner);
        await gate.ExecuteSendAsync(new SendContext(Chat("15550000012"), IsReply: false), CancellationToken.None, Inner);

        // The third cold send hard-blocks with the typed violation BEFORE the inner send.
        Func<Task> act = () => gate.ExecuteSendAsync(
            new SendContext(Chat("15550000013"), IsReply: false), CancellationToken.None, Inner);

        (await act.Should().ThrowAsync<SafetyRuleViolationException>())
            .And.Rule.Should().Be(SafetyRule.ColdOutreach);
        innerCalls.Should().Be(2, "the blocked send must never reach the inner send");
        restricted.Should().NotBeNull("cap exhaustion raises AccountRestricted once");
        published.Should().BeOfType<AccountRestrictedEvent>("the stream twin is published alongside the handler event");
        (await session.Recipients.HasContactedAsync("15550000013@s.whatsapp.net", CancellationToken.None))
            .Should().BeFalse("a blocked send marks nothing");
    }

    [Fact]
    public async Task daily_counter_resets_at_utc_midnight()
    {
        var clock = new FakeTimeProvider();
        StoreSession session = await CreateStoresAsync("cold-midnight-reset");
        var budget = new ColdOutreachBudget(clock, 1);

        (await budget.CheckAsync(
            new SendContext(Chat("15550000021"), IsReply: false),
            session.Recipients,
            CancellationToken.None)).Allowed.Should().BeTrue();

        Func<Task<(bool Allowed, bool IsCold)>> blocked = () => budget.CheckAsync(
            new SendContext(Chat("15550000022"), IsReply: false),
            session.Recipients,
            CancellationToken.None);
        await blocked.Should().ThrowAsync<SafetyRuleViolationException>("the single daily slot is spent");

        clock.Advance(TimeSpan.FromHours(25));

        (await budget.CheckAsync(
            new SendContext(Chat("15550000022"), IsReply: false),
            session.Recipients,
            CancellationToken.None)).Allowed.Should().BeTrue("the counter resets at UTC midnight");
    }

    [Fact]
    public async Task mark_on_success_sent_and_timed_out_are_contacted()
    {
        var clock = new FakeTimeProvider();
        StoreSession session = await CreateStoresAsync("cold-mark-success");
        SafetyGate gate = CreateGate(clock, session, NoJitter(coldCap: 30));

        SendOutcome sent = await gate.ExecuteSendAsync(
            new SendContext(Chat("15550000031"), IsReply: false),
            CancellationToken.None,
            _ => Task.FromResult(new SendOutcome("sent-1", SendOutcomeStatus.Sent)));
        sent.Status.Should().Be(SendOutcomeStatus.Sent);

        SendOutcome timedOut = await gate.ExecuteSendAsync(
            new SendContext(Chat("15550000032"), IsReply: false),
            CancellationToken.None,
            _ => Task.FromResult(new SendOutcome("timeout-1", SendOutcomeStatus.TimedOut)));
        timedOut.Status.Should().Be(SendOutcomeStatus.TimedOut);

        // Timeout still means the frame left the process, so both recipients are contacted.
        (await session.Recipients.HasContactedAsync("15550000031@s.whatsapp.net", CancellationToken.None)).Should().BeTrue();
        (await session.Recipients.HasContactedAsync("15550000032@s.whatsapp.net", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task mark_not_on_rejection_server_error_leaves_uncontacted()
    {
        var clock = new FakeTimeProvider();
        StoreSession session = await CreateStoresAsync("cold-mark-reject");
        SafetyGate gate = CreateGate(clock, session, NoJitter(coldCap: 30));

        SendOutcome outcome = await gate.ExecuteSendAsync(
            new SendContext(Chat("15550000041"), IsReply: false),
            CancellationToken.None,
            _ => Task.FromResult(new SendOutcome("err-1", SendOutcomeStatus.ServerError, Error: 429)));

        outcome.Status.Should().Be(SendOutcomeStatus.ServerError);
        (await session.Recipients.HasContactedAsync("15550000041@s.whatsapp.net", CancellationToken.None))
            .Should().BeFalse("only sends that left the process (Sent/TimedOut) mark the recipient");
    }

    [Fact]
    public async Task mark_store_failure_is_swallowed_and_send_still_succeeds()
    {
        var clock = new FakeTimeProvider();
        var registry = new StoreRegistry(new StoreRegistryOptions());
        StoreSession baseSession = await registry.CreateSessionAsync(AccountId.Create("cold-mark-fault"));

        // Recipients seam fails every mark (FailWrites); all other seams stay healthy.
        IRecipientContactStoreFactory inner = new InMemoryStoreFactory();
        IRecipientContactStoreFactory faulting =
            new FaultingStoreFactory(recipientContact: inner) { Mode = FaultMode.FailWrites };
        IRecipientContactStore failingRecipients =
            await faulting.CreateAsync(AccountId.Create("cold-mark-fault"), CancellationToken.None);
        var session = new StoreSession(
            baseSession.SignalSessions,
            baseSession.Identity,
            baseSession.PreKeys,
            baseSession.RecentMessages,
            baseSession.SafetyState,
            baseSession.Credentials,
            failingRecipients,
            baseSession.Outbox,
            new bool[8]);

        SafetyGate gate = CreateGate(clock, session, NoJitter(coldCap: 30));

        // The D-20 log-and-continue contract: a failing mark must not break the
        // returned outcome — the Sent send still reports Sent.
        SendOutcome outcome = await gate.ExecuteSendAsync(
            new SendContext(Chat("15550000101"), IsReply: false),
            CancellationToken.None,
            _ => Task.FromResult(new SendOutcome("sent-fault", SendOutcomeStatus.Sent)));

        outcome.Status.Should().Be(SendOutcomeStatus.Sent);
        await session.DisposeAsync();
    }

    [Fact]
    public void recipient_key_is_bare_user_at_server()
    {
        Jid chat = Chat("15550000051");

        ColdOutreachBudget.RecipientKey(chat).Should().Be("15550000051@s.whatsapp.net");
    }

    [Fact]
    public void recipient_key_strips_device_and_holds_no_display_name_or_body()
    {
        // Same bare user with an agent/device suffix keys identically to the bare JID:
        // only User + Server flow into the key — never device, display name, or body.
        Jid withDevice = Jid.Parse("15550000052.12.3@s.whatsapp.net");
        Jid bare = Chat("15550000052");

        string key = ColdOutreachBudget.RecipientKey(withDevice);

        key.Should().Be(ColdOutreachBudget.RecipientKey(bare));
        key.Should().Be("15550000052@s.whatsapp.net");
        key.Should().Be($"{withDevice.User}@{withDevice.Server}");
    }
}
