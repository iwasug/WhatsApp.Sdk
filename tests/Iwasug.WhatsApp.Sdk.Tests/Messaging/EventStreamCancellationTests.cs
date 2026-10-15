#pragma warning disable xUnit1051
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Tasks;
using Iwasug.WhatsApp.Sdk.Accounts;
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
}
