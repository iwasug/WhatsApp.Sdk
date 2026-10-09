using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Noise;
using Iwasug.WhatsApp.Sdk.Pairing;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.InMemory;
using Iwasug.WhatsApp.Sdk.Tests.Transport;
using Iwasug.WhatsApp.Sdk.Transport;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Connection;

/// <summary>
/// Terminal-logout credential clearing proofs (03-04 Task 3, CONN-01 +
/// CONN-03): 401/403/406 each remove the persisted row before the disconnect
/// completes, transient codes leave it byte-identical, double-clear is
/// idempotent, the next pairing attempt regenerates, and the connection
/// vocabulary still holds exactly four states.
/// </summary>
/// <remarks>
/// Output discipline (V6/T-3-07): assertions compare event codes, row
/// presence, and public row-equality only — private key bytes never enter an
/// assertion message.
/// </remarks>
public sealed class ConnectionCredentialTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    private static byte[] FixedBytes(int start)
    {
        byte[] bytes = new byte[32];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(start + i);
        }

        return bytes;
    }

    private static byte[] FixedSignature()
    {
        byte[] bytes = new byte[64];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(0xA0 + i);
        }

        return bytes;
    }

    private static CredentialRecord PairedSeedRow() =>
        new(
            new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero),
            "android",
            FixedBytes(0x01),
            FixedBytes(0x21),
            FixedBytes(0x41),
            FixedBytes(0x61),
            "1234567890",
            1,
            42);

    private static RegistrationDefaults DummyRegistrationDefaults() =>
        new(
            FixedBytes(0x11),
            7,
            FixedBytes(0x31),
            FixedSignature(),
            42,
            "android",
            2,
            3000,
            1043857760);

    private static AccountId NewAccount(string hint) =>
        AccountId.Create($"credential-{hint}-" + Guid.NewGuid().ToString("N"));

    private static ConnectionOptions CredentialTestOptions() => new()
    {
        ConnectTimeoutMs = 5_000,
        QueryTimeoutMs = 5_000,
        RetryBaseDelayMs = 0,
        RetryMaxDelayMs = 0,
    };

    private sealed class PairSource
    {
        internal readonly ConcurrentQueue<LoopbackFrameTransport> Peers = new();

        internal int Created;

        internal IFrameTransport CreateClient()
        {
            (LoopbackFrameTransport left, LoopbackFrameTransport right) = LoopbackFrameTransport.CreatePair();
            Peers.Enqueue(right);
            Interlocked.Increment(ref Created);
            return left;
        }
    }

    private static async Task<LoopbackFrameTransport> TakePeerAsync(PairSource pairs, CancellationToken cancellationToken)
    {
        LoopbackFrameTransport? peer;
        while (!pairs.Peers.TryDequeue(out peer))
        {
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }

        return peer!;
    }

    private static async Task<byte[]> ReceiveOrThrowAsync(IFrameTransport transport, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte>? frame = await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Peer transport closed unexpectedly.");

        return frame.Value.ToArray();
    }

    private static async Task<NoiseTransport> CompletePeerHandshakeAsync(
        IFrameTransport peer,
        CancellationToken cancellationToken)
    {
        byte[] header = await ReceiveOrThrowAsync(peer, cancellationToken).ConfigureAwait(false);
        NoiseHandshake.ValidateIntroHeader(header);

        byte[] messageAFrame = await ReceiveOrThrowAsync(peer, cancellationToken).ConfigureAwait(false);
        using var responder = new NoiseHandshakeResponder(Encoding.ASCII.GetBytes("server-hello"));
        byte[] messageA = WaFrame.Deframe(messageAFrame).ToArray();
        responder.ReadMessageA(messageA);
        byte[] messageB = responder.WriteMessageB();
        await peer.SendAsync(WaFrame.Frame(messageB), cancellationToken).ConfigureAwait(false);

        byte[] messageCFrame = await ReceiveOrThrowAsync(peer, cancellationToken).ConfigureAwait(false);
        byte[] messageC = WaFrame.Deframe(messageCFrame).ToArray();
        return responder.ReadMessageC(messageC);
    }

    private static async Task SendPeerNodeAsync(
        IFrameTransport peer,
        NoiseTransport noise,
        BinaryNode node,
        CancellationToken cancellationToken)
    {
        byte[] encrypted = noise.Encrypt(BinaryNodeCodec.Encode(node));
        byte[] flagged = new byte[encrypted.Length + 1];
        encrypted.CopyTo(flagged.AsSpan(1));
        await peer.SendAsync(WaFrame.Frame(flagged), cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendPeerSuccessAsync(
        IFrameTransport peer,
        NoiseTransport noise,
        CancellationToken cancellationToken)
    {
        byte[] nodeBytes = BinaryNodeCodec.Encode(BinaryNode.Create("success"));
        byte[] encrypted = noise.Encrypt(nodeBytes);
        byte[] flagged = new byte[encrypted.Length + 1];
        encrypted.CopyTo(flagged.AsSpan(1));
        await peer.SendAsync(WaFrame.Frame(flagged), cancellationToken).ConfigureAwait(false);
    }

    private static BinaryNode FailureNode(string reason) =>
        BinaryNode.Create("failure", new Dictionary<string, string> { ["reason"] = reason });

    private static async Task HoldPeerTillDoneAsync(IFrameTransport peer, CancellationToken cancellationToken)
    {
        try
        {
            while (await peer.ReceiveAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        await peer.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task WaitForConnectionAsync(
        ConcurrentQueue<ConnectionStateChangedEventArgs> states,
        Func<ConnectionStateChangedEventArgs, bool> predicate,
        string what)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);
        while (!timeout.IsCancellationRequested)
        {
            foreach (ConnectionStateChangedEventArgs e in states)
            {
                if (predicate(e))
                {
                    return;
                }
            }

            await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for connection event: {what}.");
    }

    private sealed class TerminalRun
    {
        internal required WaConnection Connection { get; init; }

        internal required Task PeerTask { get; init; }

        internal required ConcurrentQueue<ConnectionStateChangedEventArgs> States { get; init; }

        internal required ConcurrentQueue<DisconnectReason> Terminals { get; init; }
    }

    private static async Task<TerminalRun> DriveTerminalAsync(
        AccountId account,
        PairSource pairs,
        ICredentialStore store,
        int code,
        CancellationToken testToken)
    {
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        var terminals = new ConcurrentQueue<DisconnectReason>();
        var payloads = new HandshakePayloadProvider(account, store, DummyRegistrationDefaults());
        var connection = new WaConnection(account, pairs.CreateClient, payloads, CredentialTestOptions());
        connection.StateChanged += (_, e) => states.Enqueue(e);
        connection.PairingTerminal += (_, e) => terminals.Enqueue(e.Reason);

        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport peer = await TakePeerAsync(pairs, testToken);
            NoiseTransport noise = await CompletePeerHandshakeAsync(peer, testToken);
            try
            {
                await SendPeerNodeAsync(peer, noise, FailureNode(code.ToString()), testToken);
                await HoldPeerTillDoneAsync(peer, testToken);
            }
            finally
            {
                noise.Dispose();
            }
        }, testToken);

        Task connectTask = connection.ConnectAsync(testToken);
        Func<Task> connect = () => connectTask;
        (await connect.Should().ThrowAsync<SdkConnectionException>())
            .Which.Message.Should().Contain("logged out");

        return new TerminalRun
        {
            Connection = connection,
            PeerTask = peerTask,
            States = states,
            Terminals = terminals,
        };
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(406)]
    public async Task Logout_code_clears_credentials_and_surfaces_logged_out(int code)
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        AccountId account = NewAccount($"logout-{code}");

        ICredentialStoreFactory factory = new InMemoryStoreFactory();
        ICredentialStore store = await factory.CreateAsync(account, testToken);
        await store.PutAsync(PairedSeedRow(), testToken);

        TerminalRun run = await DriveTerminalAsync(account, pairs, store, code, testToken);

        CredentialRecord? after = await store.GetAsync(testToken);
        after.Should().BeNull("a terminal logout removes the credential row before the disconnect completes");

        run.States.Should().ContainSingle(
            e => e.State == ConnectionState.LoggedOut, "the terminal surfaces exactly once");
        run.States.Single(e => e.State == ConnectionState.LoggedOut).StatusCode.Should().Be(code);
        run.Terminals.Should().Equal(
            [(DisconnectReason)code], "the pairing channel carries the terminal reason");

        await run.Connection.DisposeAsync();
        await run.PeerTask;
        await store.DisposeAsync();
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(503)]
    [InlineData(515)]
    public async Task Transient_code_preserves_credentials_byte_equal(int code)
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        var states = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        AccountId account = NewAccount($"transient-{code}");

        ICredentialStoreFactory factory = new InMemoryStoreFactory();
        ICredentialStore store = await factory.CreateAsync(account, testToken);
        CredentialRecord seed = PairedSeedRow();
        await store.PutAsync(seed, testToken);

        var payloads = new HandshakePayloadProvider(account, store, DummyRegistrationDefaults());
        await using var connection = new WaConnection(account, pairs.CreateClient, payloads, CredentialTestOptions());
        connection.StateChanged += (_, e) => states.Enqueue(e);

        // The transient drop reconnects on a fresh channel (zero backoff in
        // these options), which the peer answers with <success> — a drop must
        // never force re-pairing.
        var peerTask = Task.Run(async () =>
        {
            LoopbackFrameTransport first = await TakePeerAsync(pairs, testToken);
            NoiseTransport firstNoise = await CompletePeerHandshakeAsync(first, testToken);
            try
            {
                await SendPeerNodeAsync(first, firstNoise, FailureNode(code.ToString()), testToken);
                await HoldPeerTillDoneAsync(first, testToken);
            }
            finally
            {
                firstNoise.Dispose();
            }

            LoopbackFrameTransport second = await TakePeerAsync(pairs, testToken);
            NoiseTransport secondNoise = await CompletePeerHandshakeAsync(second, testToken);
            try
            {
                await SendPeerSuccessAsync(second, secondNoise, testToken);
                await HoldPeerTillDoneAsync(second, testToken);
            }
            finally
            {
                secondNoise.Dispose();
            }
        }, testToken);

        await connection.ConnectAsync(testToken);
        await WaitForConnectionAsync(states, e => e.State == ConnectionState.Open, "open after transient recovery");

        CredentialRecord? after = await store.GetAsync(testToken);
        after.Should().NotBeNull("a transient drop never clears the row");
        after!.NoiseKeyPrivate.Should().Equal(seed.NoiseKeyPrivate);
        after.IdentityKeyPrivate.Should().Equal(seed.IdentityKeyPrivate);
        after.SigningKeyPrivate.Should().Equal(seed.SigningKeyPrivate);
        after.AdvSecret.Should().Equal(seed.AdvSecret, "the row is byte-identical before and after");
        after.PairedUser.Should().Be(seed.PairedUser);
        after.Device.Should().Be(seed.Device);
        pairs.Created.Should().Be(2, "the transient reconnects on a fresh channel");

        testCts.Cancel();
        await peerTask;
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Cleared_session_restarts_pairing_from_registration()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        AccountId account = NewAccount("next-attempt");

        ICredentialStoreFactory factory = new InMemoryStoreFactory();
        ICredentialStore store = await factory.CreateAsync(account, testToken);
        CredentialRecord seed = PairedSeedRow();
        await store.PutAsync(seed, testToken);
        string clearedIdentityHex = Convert.ToHexString(seed.IdentityKeyPrivate);

        TerminalRun run = await DriveTerminalAsync(account, pairs, store, 401, testToken);
        (await store.GetAsync(testToken)).Should().BeNull("precondition: the logout cleared the row");

        // The next pairing attempt takes the registration branch: with no
        // channel installed it cannot send frames, but generation plus the
        // awaited persist run before any frame exists.
        PairingCoordinator pairing = run.Connection.Pairing;
        await run.Connection.DisposeAsync();
        using var pairingCts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        Task pairingTask = pairing.StartAsync(pairingCts.Token);

        CredentialRecord? fresh = null;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (!timeout.IsCancellationRequested)
            {
                fresh = await store.GetAsync(testToken);
                if (fresh is not null)
                {
                    break;
                }

                await Task.Delay(10, testToken);
            }
        }

        fresh.Should().NotBeNull("the next attempt regenerates from the registration branch");
        Convert.ToHexString(fresh!.IdentityKeyPrivate).Should().NotBe(
            clearedIdentityHex, "a fresh identity is generated — never the cleared one");
        fresh.PairedUser.Should().BeNull("the fresh row is unpaired until pair-success");

        pairingCts.Cancel();
        Func<Task> attempt = () => pairingTask;
        await attempt.Should().ThrowAsync<OperationCanceledException>();

        await run.PeerTask;
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Double_clear_is_idempotent_with_single_logged_out()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken testToken = testCts.Token;
        var pairs = new PairSource();
        AccountId account = NewAccount("double-clear");

        ICredentialStoreFactory factory = new InMemoryStoreFactory();
        ICredentialStore store = await factory.CreateAsync(account, testToken);
        await store.PutAsync(PairedSeedRow(), testToken);

        TerminalRun run = await DriveTerminalAsync(account, pairs, store, 401, testToken);
        (await store.GetAsync(testToken)).Should().BeNull("precondition: the logout cleared the row");

        // The terminal already stopped the loop, so a second terminal cannot
        // arrive on this connection — the idempotent contract holds at the
        // store seam a repeated terminal would call through.
        (await store.RemoveAsync(testToken)).Should().BeFalse("removing the cleared row returns false, never throws");

        await Task.Delay(200, testToken);
        run.States.Count(e => e.State == ConnectionState.LoggedOut).Should().Be(1, "the terminal fires exactly once");
        run.Terminals.Should().HaveCount(1);

        await run.Connection.DisposeAsync();
        await run.PeerTask;
        await store.DisposeAsync();
    }

    [Fact]
    public void ConnectionState_vocabulary_stays_at_four_values()
    {
        string[] names = Enum.GetNames<ConnectionState>();

        names.Should().Equal("Connecting", "Open", "Closing", "LoggedOut");
    }
}
