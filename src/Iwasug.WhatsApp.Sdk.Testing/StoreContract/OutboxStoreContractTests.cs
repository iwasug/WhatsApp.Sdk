using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Testing.Rules;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Testing.StoreContract;

/// <summary>
/// Store-contract suite for <see cref="IOutboxStore"/> - durable outbox primitive ops only
/// (EXT-01). Consumers subclass and bind their factory via <see cref="CreateFactory"/>.
/// </summary>
public abstract class OutboxStoreContractTests
{
    private static readonly Jid Destination = Jid.Parse("15551234567@s.whatsapp.net");

    /// <summary>Supplies the factory under test; the subclass binds the implementation.</summary>
    protected abstract IOutboxStoreFactory CreateFactory();

    [Fact]
    public async Task EnqueueAsync_then_GetDueAsync_round_trips_entry()
    {
        IOutboxStoreFactory factory = CreateFactory();
        IOutboxStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var entry = new OutboxEntry(
            "msg-1",
            Destination,
            new OutboxPayload(OutboxPayloadKind.Text, Text: "hello"),
            DateTimeOffset.UtcNow);

        OutboxEntry enqueued = await store.EnqueueAsync(entry, CancellationToken.None);

        IReadOnlyList<OutboxEntry> due = await store.GetDueAsync(10, CancellationToken.None);

        due.Should().ContainSingle();
        due[0].Id.Should().Be("msg-1");
        due[0].Destination.ToString().Should().Be(Destination.ToString());
        due[0].Payload.Kind.Should().Be(OutboxPayloadKind.Text);
        due[0].Payload.Text.Should().Be("hello");
        due[0].State.Should().Be(OutboxState.Pending);
        enqueued.Id.Should().Be("msg-1");
        await store.DisposeAsync();
    }

    [Fact]
    public async Task MarkSentAsync_excludes_entry_from_GetDueAsync()
    {
        IOutboxStoreFactory factory = CreateFactory();
        IOutboxStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var entry = new OutboxEntry("msg-1", Destination, new OutboxPayload(), DateTimeOffset.UtcNow);
        await store.EnqueueAsync(entry, CancellationToken.None);

        await store.MarkSentAsync("msg-1", CancellationToken.None);

        IReadOnlyList<OutboxEntry> due = await store.GetDueAsync(10, CancellationToken.None);
        due.Should().NotContain(e => e.Id == "msg-1");
        await store.DisposeAsync();
    }

    [Fact]
    public async Task ScheduleRetryAsync_transitions_state_and_is_due_when_elapsed()
    {
        IOutboxStoreFactory factory = CreateFactory();
        IOutboxStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var entry = new OutboxEntry("msg-1", Destination, new OutboxPayload(), DateTimeOffset.UtcNow);
        await store.EnqueueAsync(entry, CancellationToken.None);

        await store.ScheduleRetryAsync("msg-1", 2, DateTimeOffset.UtcNow.AddSeconds(-1), "rule-1", CancellationToken.None);

        IReadOnlyList<OutboxEntry> due = await store.GetDueAsync(10, CancellationToken.None);
        due.Should().ContainSingle();
        due[0].Id.Should().Be("msg-1");
        due[0].State.Should().Be(OutboxState.RetryScheduled);
        due[0].Attempt.Should().Be(2);
        due[0].LastErrorRuleName.Should().Be("rule-1");
        due[0].NextAttemptUtc.Should().NotBeNull();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task ScheduleRetryAsync_future_next_attempt_excludes_entry_from_GetDueAsync()
    {
        IOutboxStoreFactory factory = CreateFactory();
        IOutboxStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var entry = new OutboxEntry("msg-1", Destination, new OutboxPayload(), DateTimeOffset.UtcNow);
        await store.EnqueueAsync(entry, CancellationToken.None);

        await store.ScheduleRetryAsync("msg-1", 1, DateTimeOffset.UtcNow.AddMinutes(5), "rule-1", CancellationToken.None);

        IReadOnlyList<OutboxEntry> due = await store.GetDueAsync(10, CancellationToken.None);
        due.Should().NotContain(e => e.Id == "msg-1");
        await store.DisposeAsync();
    }

    [Fact]
    public async Task DeadLetterAsync_retains_entry()
    {
        IOutboxStoreFactory factory = CreateFactory();
        IOutboxStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var entry = new OutboxEntry("msg-1", Destination, new OutboxPayload(), DateTimeOffset.UtcNow);
        await store.EnqueueAsync(entry, CancellationToken.None);

        await store.DeadLetterAsync("msg-1", "boom", CancellationToken.None);

        // Dead-lettered row is no longer due but is retained (removable).
        IReadOnlyList<OutboxEntry> due = await store.GetDueAsync(10, CancellationToken.None);
        due.Should().NotContain(e => e.Id == "msg-1");
        (await store.TryRemoveAsync("msg-1", CancellationToken.None)).Should().BeTrue();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task TryRemoveAsync_removes_existing_entry_and_returns_true()
    {
        IOutboxStoreFactory factory = CreateFactory();
        IOutboxStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        var entry = new OutboxEntry("msg-1", Destination, new OutboxPayload(), DateTimeOffset.UtcNow);
        await store.EnqueueAsync(entry, CancellationToken.None);

        (await store.TryRemoveAsync("msg-1", CancellationToken.None)).Should().BeTrue();

        IReadOnlyList<OutboxEntry> due = await store.GetDueAsync(10, CancellationToken.None);
        due.Should().BeEmpty();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task TryRemoveAsync_missing_returns_false_not_throw()
    {
        IOutboxStoreFactory factory = CreateFactory();
        IOutboxStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        // missing != failure (D-19)
        (await store.TryRemoveAsync("absent", CancellationToken.None)).Should().BeFalse();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task GetDueAsync_empty_for_empty_store()
    {
        IOutboxStoreFactory factory = CreateFactory();
        IOutboxStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);

        // missing != failure (D-19)
        (await store.GetDueAsync(10, CancellationToken.None)).Should().BeEmpty();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Two_accounts_isolated_same_id()
    {
        IOutboxStoreFactory factory = CreateFactory();
        IOutboxStore storeA = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        IOutboxStore storeB = await factory.CreateAsync(AccountId.Create("acct-B"), CancellationToken.None);
        const string id = "shared";

        await storeA.EnqueueAsync(new OutboxEntry(id, Destination, new OutboxPayload(OutboxPayloadKind.Text, Text: "from-A"), DateTimeOffset.UtcNow), CancellationToken.None);

        (await storeA.GetDueAsync(10, CancellationToken.None)).Should().ContainSingle(e => e.Id == id);
        (await storeB.GetDueAsync(10, CancellationToken.None)).Should().BeEmpty();

        await storeB.EnqueueAsync(new OutboxEntry(id, Destination, new OutboxPayload(OutboxPayloadKind.Text, Text: "from-B"), DateTimeOffset.UtcNow), CancellationToken.None);
        IReadOnlyList<OutboxEntry> aDue = await storeA.GetDueAsync(10, CancellationToken.None);
        aDue.Should().ContainSingle();
        aDue[0].Payload.Text.Should().Be("from-A");
        await storeA.DisposeAsync();
        await storeB.DisposeAsync();
    }

    [Fact]
    public async Task Write_observed_by_independent_second_store_instance()
    {
        IOutboxStoreFactory factory = CreateFactory();
        var account = AccountId.Create("acct-A");

        await StoreContractRules.AssertWriteVisibleToIndependentReadAsync<IOutboxStore>(
            createStore: () => factory.CreateAsync(account, CancellationToken.None),
            writeAsync: writer => writer.EnqueueAsync(
                new OutboxEntry("msg-1", Destination, new OutboxPayload(), DateTimeOffset.UtcNow),
                CancellationToken.None),
            assertVisibleAsync: async reader =>
            {
                (await reader.GetDueAsync(10, CancellationToken.None))
                    .Should().Contain(e => e.Id == "msg-1",
                        "an awaited enqueue must be observable by an independent subsequent read (D-06 store-side)");
            });
    }

    [Fact]
    public async Task Parallel_interleaved_enqueues_produce_no_cross_reads()
    {
        IOutboxStoreFactory factory = CreateFactory();
        var accountA = AccountId.Create("acct-A");
        var accountB = AccountId.Create("acct-B");

        await StoreContractRules.AssertParallelAccessNoCrossReadsAsync<IOutboxStore>(
            createStoreAsync: accountId => factory.CreateAsync(accountId, CancellationToken.None),
            accountA: accountA,
            accountB: accountB,
            writeOnAAsync: (writer, i) => writer.EnqueueAsync(
                new OutboxEntry($"id-{i}", Destination, new OutboxPayload(), DateTimeOffset.UtcNow),
                CancellationToken.None),
            readLeakOnBAsync: async (reader, i) =>
            {
                IReadOnlyList<OutboxEntry> due = await reader.GetDueAsync(100, CancellationToken.None);
                return due.Any(e => e.Id == $"id-{i}");
            },
            iterations: 100);
    }
}