using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.InMemory;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Stores;

public sealed class OutboxStoreTests
{
    [Fact]
    public async Task Enqueue_then_GetDue_round_trips_entry()
    {
        var factory = new InMemoryStoreFactory();
        var store = await ((IOutboxStoreFactory)factory).CreateAsync(AccountId.Create("outbox-test"), TestContext.Current.CancellationToken);

        var entry = new OutboxEntry(
            "id-1",
            Jid.Parse("15551234567@s.whatsapp.net"),
            new OutboxPayload(OutboxPayloadKind.Text, Text: "hello"),
            DateTimeOffset.UtcNow);
        await store.EnqueueAsync(entry, TestContext.Current.CancellationToken);

        IReadOnlyList<OutboxEntry> due = await store.GetDueAsync(10, TestContext.Current.CancellationToken);
        due.Should().ContainSingle(e => e.Id == "id-1");

        await store.DisposeAsync();
    }
}