#pragma warning disable xUnit1051
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Tasks;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Hosting;
using Iwasug.WhatsApp.Sdk.Messaging;
using AwesomeAssertions;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Messaging;

public class EventStreamCancellationTests
{
    private sealed record TestEvent(DateTimeOffset Timestamp) : WhatsAppEvent(Timestamp);

    private static AccountRuntime GetRuntime(Iwasug.WhatsApp.Sdk.Accounts.SessionManager manager, AccountId id)
    {
        var field = typeof(Iwasug.WhatsApp.Sdk.Accounts.SessionManager).GetField("_accounts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var dict = (ConcurrentDictionary<AccountId, AccountRuntime>)field.GetValue(manager)!;
        return dict[id];
    }

    [Fact]
    public async Task FusedReaderReceivesEventWithTag()
    {
        await using var manager = new Iwasug.WhatsApp.Sdk.Accounts.SessionManager(new Iwasug.WhatsApp.Sdk.Accounts.DefaultAccountRuntimeFactory());
        var id = AccountId.Create($"esc-{Guid.NewGuid():N}");
        await manager.AddAccountAsync(id, TestContext.Current.CancellationToken);

        var evt = new TestEvent(DateTimeOffset.UtcNow);
        var runtime = GetRuntime(manager, id);
        runtime.Messenger.TryPublishEvent(evt);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        await using var en = manager.ReadEventsAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        (await en.MoveNextAsync()).Should().BeTrue();
        en.Current.AccountId.Should().Be(id);
        en.Current.Event.Should().Be(evt);
    }

    [Fact]
    public async Task CancellationUnwindsFusedReader()
    {
        await using var manager = new Iwasug.WhatsApp.Sdk.Accounts.SessionManager(new Iwasug.WhatsApp.Sdk.Accounts.DefaultAccountRuntimeFactory());
        var id = AccountId.Create($"esc2-{Guid.NewGuid():N}");
        await manager.AddAccountAsync(id, TestContext.Current.CancellationToken);
        _ = GetRuntime(manager, id);

        using var cts = new CancellationTokenSource();
        var task = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in manager.ReadEventsAsync(cts.Token)) { }
            }
            catch (OperationCanceledException) { }
        });

        await Task.Delay(100, TestContext.Current.CancellationToken);
        cts.Cancel();
        await task.WaitAsync(TimeSpan.FromSeconds(2));
        task.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task FusedReaderSeesAccountAddedAfterItStarted()
    {
        // 07 WR-04: the reader parks on the already-enumerated (quiet) account; a
        // later registration must wake it, or the new account's events are starved
        // until some pre-existing account emits. A bounded cancel keeps a regression
        // (no membership signal) from hanging the suite.
        await using var manager = new Iwasug.WhatsApp.Sdk.Accounts.SessionManager(new Iwasug.WhatsApp.Sdk.Accounts.DefaultAccountRuntimeFactory());
        var first = AccountId.Create($"esc3-{Guid.NewGuid():N}");
        var late = AccountId.Create($"esc4-{Guid.NewGuid():N}");
        await manager.AddAccountAsync(first, TestContext.Current.CancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var readerTask = Task.Run<AccountEvent?>(async () =>
        {
            await foreach (var e in manager.ReadEventsAsync(cts.Token))
            {
                if (e.AccountId == late) return e;
            }
            return null;
        });

        // Let the reader enumerate the first account and park on its idle stream.
        await Task.Delay(200, TestContext.Current.CancellationToken);

        await manager.AddAccountAsync(late, TestContext.Current.CancellationToken);
        var evt = new TestEvent(DateTimeOffset.UtcNow);
        GetRuntime(manager, late).Messenger.TryPublishEvent(evt);

        var received = await readerTask.WaitAsync(TimeSpan.FromSeconds(4));
        received.Should().NotBeNull();
        received!.AccountId.Should().Be(late);
        received.Event.Should().Be(evt);
    }
}
