using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Transport;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Connection;

/// <summary>
/// Router proofs (CONN-04/CONN-05, T-2-08, Pitfall 5): every query resolves exactly once
/// as reply, typed timeout, or connection-lost; late replies are discarded, never
/// misdelivered; closing fails fast. Follows the StoreRegistryTests injection pattern:
/// the sender delegate routes through a substituted <see cref="IFrameTransport"/> with
/// canned replies, and timeouts run on a <see cref="FakeTimeProvider"/>.
/// </summary>
public sealed class QueryRouterTests
{
    private static ReadOnlyMemory<byte> Payload(params byte[] bytes) => bytes;

    [Fact]
    public async Task SendQueryAsync_correlates_a_canned_reply_by_id()
    {
        var harness = new RouterHarness();
        byte[] body = [0x01, 0x02, 0x03];

        Task<byte[]> pending = harness.Router.SendQueryAsync(body, CancellationToken.None).AsTask();
        (string id, byte[] sent) = harness.SingleSent();
        sent.Should().Equal(body);

        await harness.DeliverCannedReplyAsync(id, [0x09, 0x08]);

        (await pending).Should().Equal([0x09, 0x08]);
        _ = harness.Transport.Received(1).SendAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendQueryAsync_assigns_unique_monotonic_ids_that_both_complete()
    {
        var harness = new RouterHarness();

        Task<byte[]> first = harness.Router.SendQueryAsync(Payload([0x01]), CancellationToken.None).AsTask();
        Task<byte[]> second = harness.Router.SendQueryAsync(Payload([0x02]), CancellationToken.None).AsTask();
        List<(string Id, byte[] Payload)> sent = harness.TwoSent();
        sent[0].Id.Should().NotBe(sent[1].Id);

        await harness.DeliverCannedReplyAsync(sent[0].Id, [0x0A]);
        await harness.DeliverCannedReplyAsync(sent[1].Id, [0x0B]);

        (await first).Should().Equal([0x0A]);
        (await second).Should().Equal([0x0B]);
    }

    [Fact]
    public async Task SendQueryAsync_times_out_on_fake_time_with_a_typed_rule_naming_error()
    {
        var harness = new RouterHarness(queryTimeoutMs: 500);
        byte[] payload = [0x07, 0x07];

        Task<byte[]> pending = harness.Router.SendQueryAsync(payload, CancellationToken.None).AsTask();
        harness.Time.Advance(TimeSpan.FromMilliseconds(501));

        Func<Task> observeTimeout = () => pending;
        SdkConnectionException thrown = (await observeTimeout.Should().ThrowAsync<SdkConnectionException>()).Which;
        thrown.Message.Should().Contain("timed out");
        thrown.Message.Should().NotContain("q-1");
    }

    [Fact]
    public async Task SendQueryAsync_discards_a_late_reply_after_timeout_without_misdelivery()
    {
        var harness = new RouterHarness(queryTimeoutMs: 500);

        Task<byte[]> expired = harness.Router.SendQueryAsync(Payload([0x01]), CancellationToken.None).AsTask();
        (string oldId, _) = harness.SingleSent();
        harness.Time.Advance(TimeSpan.FromMilliseconds(501));
        Func<Task> observeExpired = () => expired;
        _ = await observeExpired.Should().ThrowAsync<SdkConnectionException>();

        // The waiter is gone: the late arrival is discarded (false), never completed.
        harness.Router.TryDeliver(oldId, [0xFF]).Should().BeFalse();

        // The next query takes a fresh id — 64-bit monotonic, never reused while open.
        Task<byte[]> next = harness.Router.SendQueryAsync(Payload([0x02]), CancellationToken.None).AsTask();
        (string newId, _) = harness.SingleSent(1);
        newId.Should().NotBe(oldId);

        await harness.DeliverCannedReplyAsync(newId, [0x0B]);
        (await next).Should().Equal([0x0B]);
    }

    [Fact]
    public async Task SendQueryAsync_fails_fast_with_a_typed_error_once_closing()
    {
        var harness = new RouterHarness();
        harness.Router.NotifyConnectionClosing();

        Func<Task> send = () => harness.Router.SendQueryAsync(Payload([0x01]), CancellationToken.None).AsTask();

        (await send.Should().ThrowAsync<SdkConnectionException>()).WithMessage("*closing*");
        _ = harness.Transport.DidNotReceive().SendAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyConnectionClosing_fails_inflight_queries_as_connection_lost()
    {
        var harness = new RouterHarness();

        Task<byte[]> pending = harness.Router.SendQueryAsync(Payload([0x01]), CancellationToken.None).AsTask();
        harness.SingleSent();

        harness.Router.NotifyConnectionClosing();

        Func<Task> observeClosing = () => pending;
        (await observeClosing.Should().ThrowAsync<SdkConnectionException>()).WithMessage("*closing*");
    }

    [Fact]
    public async Task SendQueryAsync_removes_the_waiter_when_the_send_path_throws()
    {
        string? attemptedId = null;
        var router = new QueryRouter(
            (id, _, _) =>
            {
                attemptedId = id;
                return ValueTask.FromException(new SdkTransportException("Simulated send fault."));
            },
            new ConnectionOptions { QueryTimeoutMs = 500 },
            new FakeTimeProvider());

        Func<Task> send = () => router.SendQueryAsync(Payload([0x01]), CancellationToken.None).AsTask();

        (await send.Should().ThrowAsync<SdkTransportException>()).WithMessage("*send fault*");

        // The failed registration was evicted: no waiter lingers behind the id.
        attemptedId.Should().NotBeNull();
        router.TryDeliver(attemptedId!, [0xFF]).Should().BeFalse();
    }

    [Fact]
    public async Task SendQueryAsync_throws_unwrapped_on_caller_cancellation()
    {
        var harness = new RouterHarness();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> send = () => harness.Router.SendQueryAsync(Payload([0x01]), cts.Token).AsTask();

        await send.Should().ThrowAsync<OperationCanceledException>();
        _ = harness.Transport.DidNotReceive().SendAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisposeAsync_fails_pending_and_rejects_new_sends()
    {
        var harness = new RouterHarness();

        Task<byte[]> pending = harness.Router.SendQueryAsync(Payload([0x01]), CancellationToken.None).AsTask();
        harness.SingleSent();

        await harness.Router.DisposeAsync();

        Func<Task> observeDisposed = () => pending;
        (await observeDisposed.Should().ThrowAsync<SdkConnectionException>()).WithMessage("*disposed*");

        Func<Task> late = () => harness.Router.SendQueryAsync(Payload([0x02]), CancellationToken.None).AsTask();
        await late.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public void Constructor_rejects_out_of_range_options_before_any_io()
    {
        Func<QueryRouter> create = () => new QueryRouter(
            (_, _, _) => ValueTask.CompletedTask,
            new ConnectionOptions { QueryTimeoutMs = 0 },
            new FakeTimeProvider());

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BuildFrame_encodes_an_iq_node_with_the_id_attribute()
    {
        byte[] frame = QueryRouter.BuildFrame("q-7", new byte[] { 0x01, 0x02 });

        BinaryNode node = BinaryNodeCodec.Decode(frame);
        node.Tag.Should().Be("iq", "outbound queries ride binary-node correlation after the Phase-3 migration");
        node.Attrs.Should().ContainKey("id").WhoseValue.Should().Be("q-7");
        node.Data.Should().Equal([0x01, 0x02]);
    }

    [Fact]
    public void TryParseFrame_rejects_non_node_bytes_and_nodes_without_an_id()
    {
        QueryRouter.TryParseFrame([0x00], out _, out _).Should().BeFalse("garbage is not a node");

        byte[] noId = BinaryNodeCodec.Encode(BinaryNode.FromBytes("iq", [0x01]));
        QueryRouter.TryParseFrame(noId, out string? missingId, out byte[]? missingBody).Should().BeFalse("correlation needs the id attribute");
        missingId.Should().BeNull();
        missingBody.Should().BeNull();
    }

    /// <summary>
    /// Test harness (StoreRegistryTests <c>CreateWrappedFactory</c> precedent): one
    /// router over a substituted transport, sends captured with ids, replies delivered
    /// through the production node helpers (the single source of truth).
    /// </summary>
    private sealed class RouterHarness
    {
        internal readonly FakeTimeProvider Time = new();
        internal readonly IFrameTransport Transport = Substitute.For<IFrameTransport>();
        internal readonly QueryRouter Router;
        private readonly List<(string Id, byte[] Payload)> _sent = new();

        internal RouterHarness(int queryTimeoutMs = 60_000)
        {
            Router = new QueryRouter(SendAsync, new ConnectionOptions { QueryTimeoutMs = queryTimeoutMs }, Time);
        }

        internal (string Id, byte[] Payload) SingleSent(int index = 0)
        {
            lock (_sent)
            {
                _sent.Count.Should().BeGreaterThan(index);
                return _sent[index];
            }
        }

        internal List<(string Id, byte[] Payload)> TwoSent()
        {
            lock (_sent)
            {
                _sent.Count.Should().Be(2);
                return [.. _sent];
            }
        }

        internal async Task DeliverCannedReplyAsync(string id, byte[] body)
        {
            // Canned reply through the substituted transport (StoreRegistryTests
            // Returns-pattern): the pump parses the production node and delivers.
            byte[] frame = QueryRouter.BuildFrame(id, body);
            Transport.ReceiveAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult<ReadOnlyMemory<byte>?>(frame));

            ReadOnlyMemory<byte>? received = await Transport.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);
            received.Should().NotBeNull();
            QueryRouter.TryParseFrame(received!.Value.ToArray(), out string? parsed, out byte[]? parsedBody).Should().BeTrue();
            Router.TryDeliver(parsed!, parsedBody!).Should().BeTrue();
        }

        private ValueTask SendAsync(string id, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            lock (_sent)
            {
                _sent.Add((id, payload.ToArray()));
            }

            return Transport.SendAsync(QueryRouter.BuildFrame(id, payload), cancellationToken);
        }
    }
}
