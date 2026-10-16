using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Messaging;
using Iwasug.WhatsApp.Sdk.Options;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.InMemory;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Messaging;

public sealed class OutboxPumpTests
{
    private sealed class FakeSender
    {
        public int Calls { get; private set; }
        public SendOutcome Outcome { get; set; } = new("id", SendOutcomeStatus.Sent);

        public Task<SendOutcome> SendAsync(OutboxEntry entry, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Outcome);
        }
    }

    [Fact]
    public async Task Due_row_survives_restart_and_marks_sent()
    {
        var factory = new InMemoryStoreFactory();
        var store = await ((IOutboxStoreFactory)factory).CreateAsync(AccountId.Create("pump-test"), TestContext.Current.CancellationToken);
        var entry = new OutboxEntry("id-1", Jid.Parse("15551234567@s.whatsapp.net"), new OutboxPayload(OutboxPayloadKind.Text, Text: "hi"), DateTimeOffset.UtcNow);
        await store.EnqueueAsync(entry, TestContext.Current.CancellationToken);

        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = new OutboxOptions { MaxAttempts = 3, InitialBackoff = TimeSpan.Zero, MaxBackoff = TimeSpan.Zero, PollInterval = TimeSpan.Zero, PollFloor = TimeSpan.Zero };
        var sender = new FakeSender();
        var pump = new OutboxPump(store, (e, ct) => sender.SendAsync(e, ct), clock, options);

        // Recompute from store after a fresh pump instance.
        var pump2 = new OutboxPump(store, (e, ct) => sender.SendAsync(e, ct), clock, options);
        await pump2.ProcessOnceAsync(TestContext.Current.CancellationToken);

        sender.Calls.Should().Be(1);
        var due = await store.GetDueAsync(10, TestContext.Current.CancellationToken);
        due.Should().BeEmpty();

        await pump2.DisposeAsync();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Permanent_validation_failure_dead_letters_immediately()
    {
        var factory = new InMemoryStoreFactory();
        var store = await ((IOutboxStoreFactory)factory).CreateAsync(AccountId.Create("pump-dead"), TestContext.Current.CancellationToken);
        var entry = new OutboxEntry("id-2", Jid.Parse("15551234567@s.whatsapp.net"), new OutboxPayload(OutboxPayloadKind.Text, Text: null), DateTimeOffset.UtcNow);
        await store.EnqueueAsync(entry, TestContext.Current.CancellationToken);

        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = new OutboxOptions { MaxAttempts = 3, InitialBackoff = TimeSpan.Zero, MaxBackoff = TimeSpan.Zero, PollInterval = TimeSpan.Zero, PollFloor = TimeSpan.Zero };
        var sender = new FakeSender();
        var pump = new OutboxPump(store, (e, ct) => sender.SendAsync(e, ct), clock, options);

        await pump.ProcessOnceAsync(TestContext.Current.CancellationToken);

        sender.Calls.Should().Be(0);
        var due = await store.GetDueAsync(10, TestContext.Current.CancellationToken);
        due.Should().BeEmpty();

        await pump.DisposeAsync();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Transient_failure_schedules_retry_until_dead_letter()
    {
        var factory = new InMemoryStoreFactory();
        var store = await ((IOutboxStoreFactory)factory).CreateAsync(AccountId.Create("pump-retry"), TestContext.Current.CancellationToken);
        var entry = new OutboxEntry("id-3", Jid.Parse("15551234567@s.whatsapp.net"), new OutboxPayload(OutboxPayloadKind.Text, Text: "hi"), DateTimeOffset.UtcNow);
        await store.EnqueueAsync(entry, TestContext.Current.CancellationToken);

        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = new OutboxOptions { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero, MaxBackoff = TimeSpan.Zero, PollInterval = TimeSpan.Zero, PollFloor = TimeSpan.Zero };
        var sender = new FakeSender { Outcome = new SendOutcome("id", SendOutcomeStatus.ServerError) };
        var pump = new OutboxPump(store, (e, ct) => sender.SendAsync(e, ct), clock, options);

        await pump.ProcessOnceAsync(TestContext.Current.CancellationToken); // attempt 1, schedule retry
        await pump.ProcessOnceAsync(TestContext.Current.CancellationToken); // attempt 2 -> dead letter

        sender.Calls.Should().Be(2);
        var due = await store.GetDueAsync(10, TestContext.Current.CancellationToken);
        due.Should().BeEmpty();

        await pump.DisposeAsync();
        await store.DisposeAsync();
    }
}
