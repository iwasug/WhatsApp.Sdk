using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Messaging;
using Iwasug.WhatsApp.Sdk.Options;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.InMemory;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Isolation;

public sealed class OutboxIsolationTests
{
    [Fact]
    public async Task Per_account_pumps_do_not_share_state()
    {
        var factory = new InMemoryStoreFactory();
        var storeA = await ((IOutboxStoreFactory)factory).CreateAsync(AccountId.Create("acct-A"), TestContext.Current.CancellationToken);
        var storeB = await ((IOutboxStoreFactory)factory).CreateAsync(AccountId.Create("acct-B"), TestContext.Current.CancellationToken);

        var now = DateTimeOffset.UtcNow;
        await storeA.EnqueueAsync(new OutboxEntry("a", Jid.Parse("15551234567@s.whatsapp.net"), new OutboxPayload(OutboxPayloadKind.Text, Text: "a"), now), TestContext.Current.CancellationToken);
        await storeB.EnqueueAsync(new OutboxEntry("b", Jid.Parse("15559876543@s.whatsapp.net"), new OutboxPayload(OutboxPayloadKind.Text, Text: "b"), now), TestContext.Current.CancellationToken);

        var clock = new FakeTimeProvider(now);
        var options = new OutboxOptions { MaxAttempts = 3, InitialBackoff = TimeSpan.Zero, MaxBackoff = TimeSpan.Zero, PollInterval = TimeSpan.Zero, PollFloor = TimeSpan.Zero };
        var pumpA = new OutboxPump(storeA, (e, ct) => Task.FromResult(new SendOutcome(e.Id, SendOutcomeStatus.Sent)), clock, options);
        var pumpB = new OutboxPump(storeB, (e, ct) => Task.FromResult(new SendOutcome(e.Id, SendOutcomeStatus.Sent)), clock, options);

        await pumpA.ProcessOnceAsync(TestContext.Current.CancellationToken);
        await pumpB.ProcessOnceAsync(TestContext.Current.CancellationToken);

        (await storeA.GetDueAsync(10, TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await storeB.GetDueAsync(10, TestContext.Current.CancellationToken)).Should().BeEmpty();

        await pumpA.DisposeAsync();
        await pumpB.DisposeAsync();
        await storeA.DisposeAsync();
        await storeB.DisposeAsync();
    }
}
