using System.Collections.Concurrent;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Messaging;
using Iwasug.WhatsApp.Sdk.Signal;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Testing.FaultInjection;
using Iwasug.WhatsApp.Sdk.Tests.Messaging;
using Xunit;
using static Iwasug.WhatsApp.Sdk.Tests.Messaging.MessageCorrelationTests;
using static Iwasug.WhatsApp.Sdk.Tests.Messaging.MessagingLoopbackTests;

namespace Iwasug.WhatsApp.Sdk.Tests.Messaging;

/// <summary>
/// Task 3 receive-path policy (04-03): the D-20 recent-cache write policy (a failing
/// cache never breaks the receive path; a healthy write is durable and independently
/// observable; the 30-day retention horizon drives the purge primitive and a purged
/// entry degrades to the no-resend outcome) and the prekey-readiness trigger
/// (below-floor batch → exactly one upload IQ per connection; healthy batch → zero).
/// </summary>
public sealed class RecentCachePolicyTests
{
    private static async Task SendInboundAsync(
        ScriptedPeer peer, DoubleRatchet peerAlice, string messageId, string text, CancellationToken token)
    {
        SignalEncryptOutcome outcome = peerAlice.Encrypt(MessageBuilder.BuildPlainText(text), token);
        byte[] wire = outcome is SignalEncryptOutcome.PreKeyMessageSent pk
            ? pk.WireBytes
            : ((SignalEncryptOutcome.MessageSent)outcome).WireBytes;
        string encType = outcome is SignalEncryptOutcome.PreKeyMessageSent ? "pkmsg" : "msg";
        await peer.SendStanzaAsync(BuildInboundMessageNode(messageId, DeviceJid(PeerUser, 52), wire, encType));
    }

    /// <summary>
    /// The recent-cache write and its horizon purge run AFTER the ack (D-06 ordering:
    /// persist → ack → event → cache), so observing `ack:…` in the peer log does not yet
    /// prove the store state the assertions below expect — poll the store until
    /// <paramref name="done"/> holds, failing with the established <c>WaitForAsync</c>
    /// TimeoutException shape if it never does.
    /// </summary>
    private static async Task<RecentMessageEntry?> WaitForCacheStateAsync(
        IRecentMessageCacheStore store, string messageId, Func<RecentMessageEntry?, bool> done, CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);
        while (!timeout.IsCancellationRequested)
        {
            RecentMessageEntry? entry = await store.GetAsync(messageId, token);
            if (done(entry))
            {
                return entry;
            }

            try
            {
                await Task.Delay(10, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        throw new TimeoutException($"Timed out waiting for recent-cache state of '{messageId}'.");
    }

    [Fact]
    public async Task cache_write_failure_log_and_continue_still_acks_and_raises_the_event()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync(
            "cache-fault", token,
            seedReceivePair: true,
            cacheFactoryOverride: fixedStores =>
                new FaultingStoreFactory(recentMessageCache: fixedStores) { Mode = FaultMode.FailWrites });
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;
        var events = new ConcurrentQueue<MessageReceivedEventArgs>();
        rig.Messenger.MessageReceived += (_, e) => events.Enqueue(e);

        // The cache store throws on every write; the receive path must not notice.
        await SendInboundAsync(peer, rig.PeerAlice!, "cache-1", "survives a broken cache", token);
        await WaitForAsync(peer.Log, entry => entry == "ack:cache-1", "the ack went out despite the cache failure");
        await WaitForCountAsync(events, 1, "the event fired despite the cache failure");
        events.Should().HaveCount(1, "the event fired exactly once despite the cache failure");
        events.Single().Text.Should().Be("survives a broken cache");

        // The pump keeps running: a later message flows the same path.
        await SendInboundAsync(peer, rig.PeerAlice!, "cache-2", "still flowing", token);
        await WaitForAsync(peer.Log, entry => entry == "ack:cache-2", "the pump survives the cache failure");
        await WaitForCountAsync(events, 2, "the second event");
    }

    [Fact]
    public async Task cache_write_success_write_observable_by_an_independent_read()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("cache-observe", token, seedReceivePair: true);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        await SendInboundAsync(peer, rig.PeerAlice!, "obs-1", "hello cache", token);
        await WaitForAsync(peer.Log, entry => entry == "ack:obs-1", "the message was processed");

        RecentMessageEntry? entry = await WaitForCacheStateAsync(
            rig.RecentMessages, "obs-1", e => e is not null, token);
        entry.Should().NotBeNull("the cache write is durable and observable by an independent read");
        entry!.ChatJid.Should().Be(rig.Chat.ToString());
        entry.Sender.Should().Be(rig.DeviceA.Jid);
        entry.Text.Should().Be("hello cache");
        entry.TimestampUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        entry.ResendPayload.Should().NotBeNull("the marshalled plaintext rides the entry for MSG-06 resend");
        Message.Parser.ParseFrom(entry.ResendPayload!).Conversation.Should().Be("hello cache");
    }

    [Fact]
    public async Task retention_trim_horizon_purges_stale_entries_through_the_purge_primitive()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("cache-trim", token, seedReceivePair: true);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        await rig.RecentMessages.PutAsync(
            new RecentMessageEntry(
                "stale-1", rig.Chat.ToString(), DateTimeOffset.UtcNow - TimeSpan.FromDays(31),
                "stale body", rig.DeviceA.Jid, null),
            token);

        await SendInboundAsync(peer, rig.PeerAlice!, "fresh-1", "fresh", token);
        await WaitForAsync(peer.Log, entry => entry == "ack:fresh-1", "the fresh message was processed");

        // The purge runs after the ack on the write path — wait for it, don't race it.
        RecentMessageEntry? stale = await WaitForCacheStateAsync(
            rig.RecentMessages, "stale-1", e => e is null, token);
        stale.Should().BeNull("entries older than the 30-day horizon are purged on the write path (D-20)");
        (await rig.RecentMessages.GetAsync("fresh-1", token))
            .Should().NotBeNull("the fresh entry survives the horizon purge");
    }

    [Fact]
    public async Task no_resend_after_horizon_purged_entry_has_no_resend_source()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("cache-noresend", token, seedReceivePair: true);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        await rig.RecentMessages.PutAsync(
            new RecentMessageEntry(
                "purged-1", rig.Chat.ToString(), DateTimeOffset.UtcNow - TimeSpan.FromDays(45),
                "gone", rig.DeviceA.Jid, null),
            token);

        await SendInboundAsync(peer, rig.PeerAlice!, "purge-drive", "drive the purge", token);
        await WaitForAsync(peer.Log, entry => entry == "ack:purge-drive", "the write path ran");

        // The purge runs after the ack on the write path — wait for it, don't race it.
        RecentMessageEntry? lookup = await WaitForCacheStateAsync(
            rig.RecentMessages, "purged-1", e => e is null, token);
        lookup.Should().BeNull(
            "the resend lookup finds nothing after the horizon: plan 04-04's retry path degrades to the " +
            "no-resend outcome (D-05 lossy cache) — a null read, never a throw and never a fabricate");
    }

    [Fact]
    public async Task prekey_low_triggers_exactly_one_upload_flight_per_connection()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("prekey-low", token);
        await SeedOwnPrekeysAsync(rig.PreKeys, count: 3, firstId: 100, token); // below the Pattern-3 floor of 5
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        SendOutcome first = await rig.Messenger.SendMessageAsync(rig.Chat, "one", token);
        first.Status.Should().Be(SendOutcomeStatus.Sent);
        peer.UploadQueries.Should().Be(1, "a below-floor one-time batch triggers exactly one upload IQ");

        SendOutcome second = await rig.Messenger.SendMessageAsync(rig.Chat, "two", token);
        second.Status.Should().Be(SendOutcomeStatus.Sent);
        peer.UploadQueries.Should().Be(1, "the re-uploaded batch is healthy and the check latches — single flight per connection");
    }

    [Fact]
    public async Task prekey_healthy_batch_issues_zero_upload_iqs()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("prekey-healthy", token);
        await SeedOwnPrekeysAsync(rig.PreKeys, count: 7, firstId: 100, token); // above the floor
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        SendOutcome first = await rig.Messenger.SendMessageAsync(rig.Chat, "one", token);
        first.Status.Should().Be(SendOutcomeStatus.Sent);
        peer.UploadQueries.Should().Be(0, "a healthy one-time batch issues zero upload IQs");

        SendOutcome second = await rig.Messenger.SendMessageAsync(rig.Chat, "two", token);
        second.Status.Should().Be(SendOutcomeStatus.Sent);
        peer.UploadQueries.Should().Be(0, "no host involvement is needed while the batch stays healthy");
    }
}
