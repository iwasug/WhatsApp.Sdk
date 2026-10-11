using System.Collections.Concurrent;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Messaging;
using Iwasug.WhatsApp.Sdk.Noise;
using Iwasug.WhatsApp.Sdk.Pairing;
using Iwasug.WhatsApp.Sdk.Signal;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.InMemory;
using Iwasug.WhatsApp.Sdk.Tests.Messaging;
using Iwasug.WhatsApp.Sdk.Tests.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static Iwasug.WhatsApp.Sdk.Tests.Messaging.MessagingLoopbackTests;

namespace Iwasug.WhatsApp.Sdk.Tests.Messaging;

/// <summary>
/// The MSG-03 correlation matrix (04-03 Task 2): id format, ack correlation (wrong ids
/// never complete), deterministic timeout on the injected clock with the no-auto-retry
/// proof, server-error mapping, phash invalidation + typed warning, duplicate-ack no-op,
/// gate-releasing cancellation, and serialized parallel sends (one outcome each).
/// </summary>
public sealed class MessageCorrelationTests
{
    /// <summary>One wired loopback rig: seeded SDK-side session, connection, scripted peer.</summary>
    internal sealed class Rig : IAsyncDisposable
    {
        required internal InMemoryStoreFactory Factory { get; init; }
        required internal WaConnection Connection { get; init; }
        required internal Messenger Messenger { get; init; }
        required internal PeerDevice DeviceA { get; init; }
        required internal Jid Chat { get; init; }
        required internal IRecentMessageCacheStore RecentMessages { get; init; }
        required internal IPreKeyStore PreKeys { get; init; }
        required internal DoubleRatchet? PeerAlice { get; init; }
        required internal Task<ScriptedPeer> PeerReady { get; init; }
        required internal Task PeerTask { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await PeerTask;
            await Factory.DisposeAsync();
        }
    }

    internal static async Task<Rig> BuildRigAsync(
        string hint,
        CancellationToken runToken,
        TimeProvider? clock = null,
        Action<ScriptedPeer>? configurePeer = null,
        Func<FixedStores, IRecentMessageCacheStoreFactory>? cacheFactoryOverride = null,
        bool seedReceivePair = false)
    {
        AccountId account = NewAccount(hint);
        OwnKeys own = CreateOwnKeys(777);
        var peers = new ConcurrentQueue<LoopbackFrameTransport>();

        var factory = new InMemoryStoreFactory();
        ISignalSessionStore sessions = await factory.CreateAsync(account, runToken);
        IIdentityStore identities = await ((IIdentityStoreFactory)factory).CreateAsync(account, runToken);
        IPreKeyStore preKeys = await ((IPreKeyStoreFactory)factory).CreateAsync(account, runToken);
        IRecentMessageCacheStore cache = await ((IRecentMessageCacheStoreFactory)factory).CreateAsync(account, runToken);
        ISafetyStateStore safety = await ((ISafetyStateStoreFactory)factory).CreateAsync(account, runToken);
        ICredentialStore credentials = await ((ICredentialStoreFactory)factory).CreateAsync(account, runToken);
        HandshakePayloadProvider payloads = await CreateSeededPayloadsAsync(account, credentials, own, runToken);

        PeerDevice deviceA = CreatePeerDevice(DeviceJid(PeerUser, 52), 22, 1101, 1234);
        DoubleRatchet? peerAlice = null;
        DoubleRatchet? bobA = null;
        if (seedReceivePair)
        {
            // Receiver role: the SDK holds Bob at the peer address; the peer holds Alice.
            peerAlice = await SeedReceivePairAsync(sessions, own, deviceA, runToken);
        }
        else
        {
            bobA = await SeedSessionPairAsync(sessions, own, deviceA, runToken);
        }

        var fixedStores = new FixedStores
        {
            Sessions = sessions, Identity = identities, PreKeys = preKeys,
            RecentMessages = cache, Safety = safety, Credentials = credentials,
        };
        var registryOptions = new StoreRegistryOptions
        {
            SignalSessionFactory = fixedStores, IdentityFactory = fixedStores, PreKeyFactory = fixedStores,
            RecentMessageCacheFactory = cacheFactoryOverride?.Invoke(fixedStores) ?? fixedStores,
            SafetyStateFactory = fixedStores, CredentialFactory = fixedStores,
        };
        var connection = new WaConnection(account, CreateClient(peers), payloads, TracerOptions(), clock);
        connection.StoreRegistry = new StoreRegistry(registryOptions);

        var peerReady = new TaskCompletionSource<ScriptedPeer>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task peerTask = Task.Run(async () =>
        {
            ScriptedPeer p = await CompletePeerHandshakeAsync(peers, runToken);
            if (bobA is not null)
            {
                p.BobRatchets[deviceA.Jid] = bobA;
            }

            p.UsyncDevices = [
                deviceA.Jid,
                DeviceJid(OwnUser, OwnDevice),
                DeviceJid(PeerUser, 0)];
            configurePeer?.Invoke(p);
            peerReady.SetResult(p);
            await p.RunAsync(runToken);
        }, runToken);

        return new Rig
        {
            Factory = factory,
            Connection = connection,
            Messenger = connection.Messaging,
            DeviceA = deviceA,
            Chat = Jid.Parse($"{PeerUser}@{PeerServer}"),
            RecentMessages = cache,
            PreKeys = preKeys,
            PeerAlice = peerAlice,
            PeerReady = peerReady.Task,
            PeerTask = peerTask,
        };
    }

    private static BinaryNode AckNode(string messageId, Jid chat) => BinaryNode.Create(
        "ack",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["class"] = "message",
            ["id"] = messageId,
            ["to"] = chat.ToString(),
        });

    [Fact]
    public void generated_ids_id_format_40_char_3eb0_uppercase_hex_and_never_repeat()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 256; i++)
        {
            string id = MessageSender.GenerateMessageId();
            id.Should().HaveLength(40, "3EB0 + 18 CSPRNG bytes = 40 chars");
            id.Should().MatchRegex("^3EB0[0-9A-F]{36}$", "the suffix is uppercase hex");
            ids.Add(id).Should().BeTrue("two generated ids are never equal");
        }

        ids.Should().HaveCount(256);
    }

    [Fact]
    public async Task ack_with_matching_id_completes_but_wrong_id_ack_does_not()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("corr-wrongid", token, configurePeer: p => p.AutoAck = false);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        Task<SendOutcome> sendTask = rig.Messenger.SendMessageAsync(rig.Chat, "hello", token);
        await WaitForAsync(
            peer.Log, entry => entry.StartsWith("message:", StringComparison.Ordinal), "the server saw the send");
        peer.MessageNodes.TryPeek(out BinaryNode? sent).Should().BeTrue();
        string realId = sent!.Attrs["id"];

        // An ack for a DIFFERENT id never completes the pending send.
        await peer.SendStanzaAsync(AckNode("3EB0" + new string('F', 36), rig.Chat));
        await Task.Delay(300, token);
        sendTask.IsCompleted.Should().BeFalse("a wrong-id ack is a correlation miss");

        await peer.SendStanzaAsync(AckNode(realId, rig.Chat));
        SendOutcome outcome = await sendTask;
        outcome.Status.Should().Be(SendOutcomeStatus.Sent);
        outcome.MessageId.Should().Be(realId);
    }

    [Fact]
    public async Task timeout_no_auto_retry_returns_timed_out_outcome_and_server_saw_exactly_one_node()
    {
        var fakeTime = new FakeTimeProvider();
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync(
            "corr-timeout", token, clock: fakeTime, configurePeer: p => p.AutoAck = false);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        Task<SendOutcome> sendTask = rig.Messenger.SendMessageAsync(rig.Chat, "hello", token);
        await WaitForAsync(
            peer.Log, entry => entry.StartsWith("message:", StringComparison.Ordinal), "the single network send");
        peer.MessageNodes.TryPeek(out BinaryNode? sent).Should().BeTrue();
        string sentId = sent!.Attrs["id"];

        fakeTime.Advance(TimeSpan.FromSeconds(6));
        SendOutcome outcome = await sendTask;
        outcome.Status.Should().Be(SendOutcomeStatus.TimedOut);
        outcome.MessageId.Should().Be(sentId, "the timed-out outcome carries the id");
        peer.MessageNodes.Count.Should().Be(1, "one logical send = exactly one network send — the SDK never re-sends");
    }

    [Fact]
    public async Task server_error_attr_maps_to_server_error_outcome_with_the_code()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("corr-error", token, configurePeer: p =>
        {
            p.AckAttrs = new Dictionary<string, string>(StringComparer.Ordinal) { ["error"] = "471" };
        });
        await rig.Connection.ConnectAsync(token);
        await rig.PeerReady;

        SendOutcome outcome = await rig.Messenger.SendMessageAsync(rig.Chat, "hello", token);
        outcome.Status.Should().Be(SendOutcomeStatus.ServerError);
        outcome.Error.Should().Be(471, "the nonzero error attribute rides the outcome");
        outcome.MessageId.Should().MatchRegex("^3EB0[0-9A-F]{36}$");
    }

    [Fact]
    public async Task phash_mismatch_invalidates_device_cache_and_raises_typed_warning()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("corr-phash", token);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        string expectedPhash = DeviceDirectory.ComputePhash([Jid.Parse(rig.DeviceA.Jid)]);
        var warnings = new ConcurrentQueue<DeviceSetChangedEventArgs>();
        rig.Messenger.DeviceSetChanged += (_, e) => warnings.Enqueue(e);

        // Send 1: the ack carries a stale phash → cache invalidated + typed warning.
        peer.AckAttrs = new Dictionary<string, string>(StringComparer.Ordinal) { ["phash"] = "2:stalephash" };
        SendOutcome first = await rig.Messenger.SendMessageAsync(rig.Chat, "one", token);
        first.Status.Should().Be(SendOutcomeStatus.Sent, "a phash mismatch does not fail an acked send — it warns");
        warnings.Should().HaveCount(1);
        DeviceSetChangedEventArgs warning = warnings.Single();
        warning.Chat.Should().Be(rig.Chat);
        warning.MessageId.Should().Be(first.MessageId);
        warning.ExpectedPhash.Should().Be(expectedPhash);
        warning.ReceivedPhash.Should().Be("2:stalephash");

        // Send 2: the cache was invalidated — the send re-queries usync; phash matches now.
        peer.AckAttrs = new Dictionary<string, string>(StringComparer.Ordinal) { ["phash"] = expectedPhash };
        await rig.Messenger.SendMessageAsync(rig.Chat, "two", token);

        // Send 3: the fresh cache entry is reused — no third usync, no second warning.
        peer.AckAttrs = null;
        await rig.Messenger.SendMessageAsync(rig.Chat, "three", token);

        peer.Log.Count(entry => entry == "usync").Should().Be(2, "mismatch invalidated the cache; a matching send reused it");
        warnings.Should().HaveCount(1, "a matching phash never warns");
    }

    [Fact]
    public async Task duplicate_ack_is_a_noop_and_the_outcome_is_unchanged()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("corr-dup", token, configurePeer: p => p.AutoAck = false);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        Task<SendOutcome> sendTask = rig.Messenger.SendMessageAsync(rig.Chat, "hello", token);
        await WaitForAsync(
            peer.Log, entry => entry.StartsWith("message:", StringComparison.Ordinal), "the server saw the send");
        peer.MessageNodes.TryPeek(out BinaryNode? sent).Should().BeTrue();
        string id = sent!.Attrs["id"];

        await peer.SendStanzaAsync(AckNode(id, rig.Chat));
        await peer.SendStanzaAsync(AckNode(id, rig.Chat)); // late duplicate
        SendOutcome outcome = await sendTask;
        outcome.Status.Should().Be(SendOutcomeStatus.Sent);
        outcome.MessageId.Should().Be(id);
        sendTask.IsCompletedSuccessfully.Should().BeTrue("exactly one terminal outcome, untouched by the duplicate");
    }

    [Fact]
    public async Task cancellation_throws_unwrapped_oce_releases_gate_and_next_send_succeeds()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("corr-cancel", token, configurePeer: p => p.AutoAck = false);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        using var cancelCts = new CancellationTokenSource();
        Task sendTask = rig.Messenger.SendMessageAsync(rig.Chat, "first", cancelCts.Token);
        await WaitForAsync(
            peer.Log, entry => entry.StartsWith("message:", StringComparison.Ordinal), "the send reached the wire");
        cancelCts.Cancel();

        OperationCanceledException thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);
        thrown.GetType().Should().Be(typeof(OperationCanceledException), "cancellation surfaces unwrapped");
        thrown.CancellationToken.Should().Be(cancelCts.Token);

        peer.AutoAck = true;
        SendOutcome second = await rig.Messenger.SendMessageAsync(rig.Chat, "second", token);
        second.Status.Should().Be(SendOutcomeStatus.Sent, "the cancelled send released the gate — the next send is not wedged");
    }

    [Fact]
    public async Task parallel_sends_serialize_and_settle_exactly_one_outcome_each()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken token = testCts.Token;
        await using Rig rig = await BuildRigAsync("corr-parallel", token);
        await rig.Connection.ConnectAsync(token);
        ScriptedPeer peer = await rig.PeerReady;

        Task<SendOutcome>[] sends = Enumerable.Range(0, 5)
            .Select(i => rig.Messenger.SendMessageAsync(rig.Chat, $"m{i}", token))
            .ToArray();
        SendOutcome[] outcomes = await Task.WhenAll(sends);

        outcomes.Should().OnlyContain(o => o.Status == SendOutcomeStatus.Sent);
        outcomes.Select(o => o.MessageId).Should().OnlyHaveUniqueItems();
        peer.MessageNodes.Count.Should().Be(5, "five sends, five nodes — none interleaved or lost");

        // The serialization proof: the peer's Bob ratchet decrypts every node in arrival
        // order (interleaved ratchet mutations would break the chain and fail here).
        var texts = new List<string>();
        while (peer.MessageNodes.TryDequeue(out BinaryNode? node))
        {
            BinaryNode enc = (node.Children ?? [])
                .SelectMany(c => c.Children ?? (IReadOnlyList<BinaryNode>)[])
                .Single(c => c.Tag == "enc");
            bool isPreKey = string.Equals(enc.Attrs["type"], "pkmsg", StringComparison.Ordinal);
            SignalDecryptOutcome.Decrypted plain = peer.BobRatchets[rig.DeviceA.Jid]
                .Decrypt(enc.Data!, isPreKey, token)
                .Should().BeOfType<SignalDecryptOutcome.Decrypted>().Which;
            texts.Add(Message.Parser.ParseFrom(plain.Plaintext).Conversation);
        }

        texts.OrderBy(t => t, StringComparer.Ordinal).Should().Equal("m0", "m1", "m2", "m3", "m4");
    }
}
