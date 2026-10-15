using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Messaging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Isolation;

public class TwoAccountRuntimeTests
{
    [Fact]
    public async Task Two_Accounts_Run_Concurrently_With_Zero_CrossReads()
    {
        var registry = new Iwasug.WhatsApp.Sdk.Stores.StoreRegistry(new Iwasug.WhatsApp.Sdk.Stores.StoreRegistryOptions());
        var factory = new DefaultAccountRuntimeFactory(registry);

        AccountId accountA = AccountId.Create($"iso-a-{Guid.NewGuid():N}");
        AccountId accountB = AccountId.Create($"iso-b-{Guid.NewGuid():N}");

        AccountRuntime runtimeA = await factory.CreateAsync(accountA, TestContext.Current.CancellationToken);
        AccountRuntime runtimeB = await factory.CreateAsync(accountB, TestContext.Current.CancellationToken);
        try
        {
            // Reference inequality of the per-account graphs.
            runtimeA.StoreSession.Should().NotBeSameAs(runtimeB.StoreSession);
            runtimeA.Messenger.Should().NotBeSameAs(runtimeB.Messenger);
            runtimeA.Connection.Should().NotBeSameAs(runtimeB.Connection);
            runtimeA.OutboundQueue.Should().NotBeSameAs(runtimeB.OutboundQueue);
            runtimeA.LinkedCts.Should().NotBeSameAs(runtimeB.LinkedCts);
            ReferenceEquals(runtimeA.SafetyGate, runtimeB.SafetyGate).Should().BeFalse();

            // Store write on A is not visible under B.
            await runtimeA.StoreSession.Recipients.MarkContactedAsync("cross-read-key", TestContext.Current.CancellationToken);
            (await runtimeA.StoreSession.Recipients.HasContactedAsync("cross-read-key", TestContext.Current.CancellationToken)).Should().BeTrue();
            (await runtimeB.StoreSession.Recipients.HasContactedAsync("cross-read-key", TestContext.Current.CancellationToken)).Should().BeFalse();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var drainA = DrainEventsAsync(runtimeA.Messenger, cts.Token);
            var drainB = DrainEventsAsync(runtimeB.Messenger, cts.Token);

            Task tA = Task.Run(async () =>
            {
                for (int i = 0; i < 50; i++)
                {
                    await runtimeA.OutboundQueue.Writer.WriteAsync(
                        new OutboundEnvelope(accountA, $"A{i}"), cts.Token);
                }
                await runtimeA.DisposeAsync();
            }, cts.Token);

            Task tB = Task.Run(async () =>
            {
                for (int i = 0; i < 100; i++)
                {
                    await runtimeB.OutboundQueue.Writer.WriteAsync(
                        new OutboundEnvelope(accountB, $"B{i}"), cts.Token);
                }
            }, cts.Token);

            await Task.WhenAll(tA, tB);
            cts.Cancel();
            await Task.WhenAll(drainA, drainB);

            // Surviving account remains healthy.
            runtimeB.OutboundQueue.Writer.TryWrite(new OutboundEnvelope(accountB, "final")).Should().BeTrue();
            runtimeB.Messenger.Should().NotBeNull();
        }
        finally
        {
            try { await runtimeB.DisposeAsync(); } catch { }
        }
    }

    [Fact]
    public async Task Factory_Wires_Real_Transport_And_Credential_Aware_Pairing()
    {
        var factory = new DefaultAccountRuntimeFactory();
        AccountId accountId = AccountId.Create($"cr01-{Guid.NewGuid():N}");

        // 07 CR-01: the default transport is the production WebSocket transport, not a throwing stub.
        Iwasug.WhatsApp.Sdk.Transport.IFrameTransport transport =
            factory.ResolveTransportFactory(accountId, new Iwasug.WhatsApp.Sdk.Connection.ConnectionOptions())();
        try
        {
            transport.Should().BeOfType<Iwasug.WhatsApp.Sdk.Transport.WebSocketFrameTransport>();
        }
        finally
        {
            await transport.DisposeAsync();
        }

        // 07 CR-01: pairing requires credential-aware payloads — a bare payload double
        // makes WaConnection.Pairing throw InvalidOperationException.
        AccountRuntime runtime = await factory.CreateAsync(accountId, TestContext.Current.CancellationToken);
        try
        {
            runtime.Connection.Pairing.Should().NotBeNull();
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task Factory_Honors_Configured_EventChannelCapacity()
    {
        // 07 WR-02: a host-configured capacity must bound the per-account event channel.
        var factory = new DefaultAccountRuntimeFactory(eventChannelCapacity: 7);
        AccountRuntime runtime = await factory.CreateAsync(
            AccountId.Create($"wr02-{Guid.NewGuid():N}"), TestContext.Current.CancellationToken);
        try
        {
            for (int i = 0; i < 7; i++)
            {
                runtime.Messenger.TryPublishEvent(
                    new BudgetExhaustedEvent("k", "Window", DateTimeOffset.MaxValue, 0, DateTimeOffset.UnixEpoch))
                    .Should().BeTrue();
            }

            runtime.Messenger.TryPublishEvent(
                new BudgetExhaustedEvent("k", "Window", DateTimeOffset.MaxValue, 0, DateTimeOffset.UnixEpoch))
                .Should().BeFalse("the configured capacity of 7 is full");
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    private static async Task<int> DrainEventsAsync(Messenger messenger, CancellationToken ct)
    {
        int count = 0;
        try
        {
            await foreach (var _ in messenger.ReadEventsAsync(ct))
            {
                count++;
            }
        }
        catch (OperationCanceledException)
        {
            // expected on cancellation
        }
        return count;
    }

    [Fact]
    public async Task RuntimeLoggers_CarryAccountIdScope()
    {
        // 07 WR-05: the account graph must log through AccountScopedLogger (MUL-03 / SC3).
        // The throwing transport faults the connect attempt, which makes WaConnection emit
        // a runtime-produced entry through its account-scoped logger; that entry must
        // carry the AccountId scope or multi-account logs are unattributable.
        var capture = new CapturingLoggerFactory();
        var factory = new DefaultAccountRuntimeFactory(
            loggerFactory: capture,
            transportFactory: _ => new ThrowingTransport());

        AccountId accountId = AccountId.Create($"wr05-{Guid.NewGuid():N}");
        AccountRuntime runtime = await factory.CreateAsync(accountId, TestContext.Current.CancellationToken);
        try
        {
            Task connect = runtime.Connection.ConnectAsync(TestContext.Current.CancellationToken);
            _ = connect.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);

            string? scopedAccountId = null;
            await WaitForAsync(
                () => capture.TryGetScopedAccountId("WaConnection", out scopedAccountId),
                TimeSpan.FromSeconds(5));
            scopedAccountId.Should().Be(accountId.Value);
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("The awaited condition was not reached in time.");
            }
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Captures each forwarded log entry together with the AccountId scope active on it.</summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly ConcurrentQueue<(string Category, string? ScopedAccountId)> _entries = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        public bool TryGetScopedAccountId(string categorySubstring, out string? accountId)
        {
            foreach ((string Category, string? ScopedAccountId) entry in _entries)
            {
                if (entry.Category.Contains(categorySubstring, StringComparison.Ordinal) && entry.ScopedAccountId is not null)
                {
                    accountId = entry.ScopedAccountId;
                    return true;
                }
            }

            accountId = null;
            return false;
        }

        private void Record(string category, string? scopedAccountId) => _entries.Enqueue((category, scopedAccountId));

        private sealed class CapturingLogger(CapturingLoggerFactory owner, string categoryName) : ILogger
        {
            private string? _scopedAccountId;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                if (state is IEnumerable<KeyValuePair<string, object>> values)
                {
                    foreach (KeyValuePair<string, object> kvp in values)
                    {
                        if (kvp.Key == "AccountId") _scopedAccountId = kvp.Value?.ToString();
                    }
                }

                return new ScopeHandle();
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => owner.Record(categoryName, _scopedAccountId);

            private sealed class ScopeHandle : IDisposable
            {
                public void Dispose() { }
            }
        }
    }

    /// <summary>Fails every send so the connect loop faults and logs immediately.</summary>
    private sealed class ThrowingTransport : Iwasug.WhatsApp.Sdk.Transport.IFrameTransport
    {
        public int? CloseStatusCode => null;

        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new InvalidOperationException("test transport: wire refused"));

        public ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ReadOnlyMemory<byte>?>(new InvalidOperationException("test transport: wire refused"));

        public ValueTask CloseAsync(int statusCode, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}