using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Messaging;
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
}