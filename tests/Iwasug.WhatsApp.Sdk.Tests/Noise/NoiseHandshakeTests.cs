using System.Text;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Noise;
using Iwasug.WhatsApp.Sdk.Tests.Transport;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Noise;

/// <summary>
/// Two-party handshake proofs (02-03, CONN-04 re-handshake hygiene, T-2-03):
/// initiator and responder complete over a duplex-pipe fake with zero
/// sockets, agree on transport keys, exchange AEAD traffic both ways, then a
/// simulated 515 re-handshake mints a fresh ephemeral while the old keys are
/// provably zeroized. Follows the FaultingBindingTests fake-harness pattern.
/// </summary>
/// <remarks>
/// Output discipline (V6/T-2-10): assertions compare transcript hashes and
/// packet prefixes (public wire data), lengths, and equality booleans — raw
/// key bytes never enter an assertion message.
/// </remarks>
public sealed class NoiseHandshakeTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private sealed class StaticPayloads : INoiseHandshakePayloads
    {
        private readonly string _hello;
        private readonly string _finish;

        internal StaticPayloads(string hello, string finish)
        {
            _hello = hello;
            _finish = finish;
        }

        public byte[] GetClientHello() => Encoding.ASCII.GetBytes(_hello);

        public byte[] GetClientFinish() => Encoding.ASCII.GetBytes(_finish);
    }

    private static async Task<byte[]> ReceiveOrThrowAsync(
        LoopbackFrameTransport transport,
        CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte>? frame = await transport.ReceiveAsync(cancellationToken);
        return frame is null
            ? throw new InvalidOperationException("Pipe closed before the handshake completed.")
            : frame.Value.ToArray();
    }

    private static async Task<(NoiseTransport Initiator, NoiseTransport Responder, byte[] MessageA, byte[] MessageC)> CompleteHandshakeAsync(
        LoopbackFrameTransport client,
        LoopbackFrameTransport peer,
        NoiseHandshake initiator,
        NoiseHandshakeResponder responder,
        CancellationToken cancellationToken)
    {
        await client.SendAsync(initiator.WriteMessageA(), cancellationToken);
        byte[] messageA = await ReceiveOrThrowAsync(peer, cancellationToken);
        responder.ReadMessageA(messageA);

        await peer.SendAsync(responder.WriteMessageB(), cancellationToken);
        byte[] messageB = await ReceiveOrThrowAsync(client, cancellationToken);
        NoiseTransport initiatorTransport = initiator.ReadMessageB(messageB);

        await client.SendAsync(initiator.WriteMessageC(), cancellationToken);
        byte[] messageC = await ReceiveOrThrowAsync(peer, cancellationToken);
        NoiseTransport responderTransport = responder.ReadMessageC(messageC);

        return (initiatorTransport, responderTransport, messageA, messageC);
    }

    [Fact]
    public async Task Two_parties_complete_over_duplex_pipe_with_matching_keys_and_aead_both_ways()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        (LoopbackFrameTransport client, LoopbackFrameTransport peer) = LoopbackFrameTransport.CreatePair();
        await using var clientLifetime = client;
        await using var peerLifetime = peer;

        var payloads = new StaticPayloads("client-hello", "client-finish");
        byte[] serverHello = Encoding.ASCII.GetBytes("server-hello");
        using var initiator = new NoiseHandshake(payloads);
        using var responder = new NoiseHandshakeResponder(serverHello);

        (NoiseTransport initiatorTransport, NoiseTransport responderTransport, _, _) =
            await CompleteHandshakeAsync(client, peer, initiator, responder, testToken);

        Convert.ToHexString(initiator.HandshakeHash).Should().Be(Convert.ToHexString(responder.HandshakeHash));

        byte[] ping = Encoding.ASCII.GetBytes("ping");
        byte[] packet = initiatorTransport.Encrypt(ping);
        packet.AsSpan(0, 12).ToArray().Should().Equal(new byte[12], "the first send nonce starts at 0");
        responderTransport.Decrypt(packet).Should().Equal(ping);

        byte[] pong = Encoding.ASCII.GetBytes("pong");
        byte[] reply = responderTransport.Encrypt(pong);
        initiatorTransport.Decrypt(reply).Should().Equal(pong);

        byte[] second = initiatorTransport.Encrypt(Encoding.ASCII.GetBytes("again"));
        second.AsSpan(0, 8).ToArray().Should().Equal(new byte[8]);
        second[11].Should().Be(1, "the send counter advances sequentially from 0");
        responderTransport.Decrypt(second).Should().Equal(Encoding.ASCII.GetBytes("again"));

        initiator.IsKeyMaterialZeroized.Should().BeFalse("live handshake keys are in use, never zeroed");
        initiatorTransport.IsKeysZeroized.Should().BeFalse("live transport keys are in use, never zeroed");

        initiatorTransport.Dispose();
        responderTransport.Dispose();
    }

    [Fact]
    public async Task Rehandshake_after_515_mints_fresh_ephemeral_and_zeroizes_old_keys()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        var payloads = new StaticPayloads("client-hello", "client-finish");
        byte[] serverHello = Encoding.ASCII.GetBytes("server-hello");

        (LoopbackFrameTransport firstClient, LoopbackFrameTransport firstPeer) = LoopbackFrameTransport.CreatePair();
        await using var firstClientLifetime = firstClient;
        await using var firstPeerLifetime = firstPeer;
        using var firstInitiator = new NoiseHandshake(payloads);
        using var firstResponder = new NoiseHandshakeResponder(serverHello);
        (NoiseTransport firstInitiatorTransport, NoiseTransport firstResponderTransport, byte[] firstMessageA, _) =
            await CompleteHandshakeAsync(firstClient, firstPeer, firstInitiator, firstResponder, testToken);
        string firstHash = Convert.ToHexString(firstInitiator.HandshakeHash);

        byte[] probe = firstInitiatorTransport.Encrypt("pre-restart"u8.ToArray());
        firstResponderTransport.Decrypt(probe).Should().Equal("pre-restart"u8.ToArray());

        // 515 restart: the attempt's handshakes and transports are disposed —
        // every secret they held must be provably wiped, never carried over.
        firstInitiator.Dispose();
        firstResponder.Dispose();
        firstInitiatorTransport.Dispose();
        firstResponderTransport.Dispose();
        firstInitiator.IsKeyMaterialZeroized.Should().BeTrue();
        firstResponder.IsKeyMaterialZeroized.Should().BeTrue();
        firstInitiatorTransport.IsKeysZeroized.Should().BeTrue();
        firstResponderTransport.IsKeysZeroized.Should().BeTrue();

        // Fresh handshake on a fresh channel: new ephemeral, new transcript,
        // working keys — the old secrets contribute nothing.
        (LoopbackFrameTransport secondClient, LoopbackFrameTransport secondPeer) = LoopbackFrameTransport.CreatePair();
        await using var secondClientLifetime = secondClient;
        await using var secondPeerLifetime = secondPeer;
        using var secondInitiator = new NoiseHandshake(payloads);
        using var secondResponder = new NoiseHandshakeResponder(serverHello);
        (NoiseTransport secondInitiatorTransport, NoiseTransport secondResponderTransport, byte[] secondMessageA, _) =
            await CompleteHandshakeAsync(secondClient, secondPeer, secondInitiator, secondResponder, testToken);

        secondMessageA.Should().NotEqual(firstMessageA, "each handshake mints a fresh ephemeral — never reuse e");
        Convert.ToHexString(secondInitiator.HandshakeHash).Should().NotBe(firstHash);

        byte[] after = secondInitiatorTransport.Encrypt("post-restart"u8.ToArray());
        secondResponderTransport.Decrypt(after).Should().Equal("post-restart"u8.ToArray());
        after.AsSpan(0, 12).ToArray().Should().Equal(new byte[12], "the re-handshake transport restarts its nonces from 0");
    }

    [Fact]
    public void Cancelled_handshake_zeroizes_inflight_keys()
    {
        var payloads = new StaticPayloads("client-hello", "client-finish");

        var initiator = new NoiseHandshake(payloads);
        initiator.IsKeyMaterialZeroized.Should().BeFalse("created keys are live, not wiped");
        byte[] messageA = initiator.WriteMessageA();
        messageA.Should().NotBeEmpty();

        // Abort before message B: disposing mid-handshake wipes the ephemeral,
        // the static, and the partial chaining state — never silently dropped.
        initiator.Dispose();
        initiator.IsKeyMaterialZeroized.Should().BeTrue();
        Assert.Throws<ObjectDisposedException>(() => initiator.WriteMessageA());
        Assert.Throws<ObjectDisposedException>(() => initiator.ReadMessageB(new byte[64]));
        initiator.Dispose();

        var responder = new NoiseHandshakeResponder(Encoding.ASCII.GetBytes("server-hello"));
        responder.ReadMessageA(messageA);
        responder.IsKeyMaterialZeroized.Should().BeFalse("a live responder still needs its keys");
        responder.Dispose();
        responder.IsKeyMaterialZeroized.Should().BeTrue();
        Assert.Throws<ObjectDisposedException>(() => responder.WriteMessageB());
        responder.Dispose();
    }

    [Theory]
    [InlineData("")]
    [InlineData("57")]
    [InlineData("57414c")]
    [InlineData("57410602")]
    [InlineData("5741060300")]
    [InlineData("57410603ffffffff")]
    public void Handshake_rejects_malformed_message_b_fail_closed(string messageBHex)
    {
        using var initiator = new NoiseHandshake(new StaticPayloads("hello", "finish"));
        initiator.WriteMessageA();

        byte[] messageB = Convert.FromHexString(messageBHex);
        Assert.Throws<SdkConnectionException>(() => initiator.ReadMessageB(messageB));
        initiator.IsKeyMaterialZeroized.Should().BeFalse("a rejected peer message must not wipe our own keys");
    }

    [Theory]
    [InlineData("57410603")]
    [InlineData("5741060300ff")]
    public void Handshake_rejects_malformed_message_a_fail_closed(string messageAHex)
    {
        using var responder = new NoiseHandshakeResponder(Encoding.ASCII.GetBytes("server-hello"));

        byte[] messageA = Convert.FromHexString(messageAHex);
        Assert.Throws<SdkConnectionException>(() => responder.ReadMessageA(messageA));
    }

    [Fact]
    public void Handshake_rejects_out_of_order_calls()
    {
        using var initiator = new NoiseHandshake(new StaticPayloads("hello", "finish"));
        Assert.Throws<InvalidOperationException>(() => initiator.ReadMessageB(new byte[64]));
        Assert.Throws<InvalidOperationException>(() => initiator.WriteMessageC());
        _ = initiator.WriteMessageA();
        Assert.Throws<InvalidOperationException>(() => initiator.WriteMessageA());
        initiator.Dispose();
        Assert.Throws<ObjectDisposedException>(() => initiator.WriteMessageA());
    }

    [Fact]
    public async Task Proto_envelope_carries_baileys_faithful_shapes()
    {
        using var testCts = new CancellationTokenSource(WaitTimeout);
        CancellationToken testToken = testCts.Token;
        (LoopbackFrameTransport client, LoopbackFrameTransport peer) = LoopbackFrameTransport.CreatePair();
        await using var clientLifetime = client;
        await using var peerLifetime = peer;

        byte[] hello = Encoding.ASCII.GetBytes("client-hello");
        byte[] finish = Encoding.ASCII.GetBytes("client-finish");
        byte[] serverHello = Encoding.ASCII.GetBytes("server-hello");
        var payloads = new StaticPayloads("client-hello", "client-finish");
        using var initiator = new NoiseHandshake(payloads);
        using var responder = new NoiseHandshakeResponder(serverHello);

        await client.SendAsync(initiator.WriteMessageA(), testToken);
        byte[] rawA = await ReceiveOrThrowAsync(peer, testToken);
        responder.ReadMessageA(rawA);
        await peer.SendAsync(responder.WriteMessageB(), testToken);
        byte[] rawB = await ReceiveOrThrowAsync(client, testToken);
        NoiseTransport initiatorTransport = initiator.ReadMessageB(rawB);
        await client.SendAsync(initiator.WriteMessageC(), testToken);
        byte[] rawC = await ReceiveOrThrowAsync(peer, testToken);
        NoiseTransport responderTransport = responder.ReadMessageC(rawC);

        HandshakeMessage messageA = HandshakeMessage.Parser.ParseFrom(rawA);
        messageA.ClientHello.Should().NotBeNull();
        messageA.ServerHello.Should().BeNull();
        messageA.ClientFinish.Should().BeNull();
        messageA.ClientHello.Ephemeral.Length.Should().Be(32);
        messageA.ClientHello.Payload.ToByteArray().Should().Equal(hello);

        HandshakeMessage messageB = HandshakeMessage.Parser.ParseFrom(rawB);
        messageB.ServerHello.Should().NotBeNull();
        messageB.ClientHello.Should().BeNull();
        messageB.ClientFinish.Should().BeNull();
        messageB.ServerHello.Ephemeral.Length.Should().Be(32);
        messageB.ServerHello.StaticKey.Length.Should().Be(48, "an encrypted 32-byte static key plus the 16-byte tag");
        messageB.ServerHello.Payload.Length.Should().BeGreaterThan(16);

        HandshakeMessage messageC = HandshakeMessage.Parser.ParseFrom(rawC);
        messageC.ClientFinish.Should().NotBeNull();
        messageC.ClientHello.Should().BeNull();
        messageC.ServerHello.Should().BeNull();
        messageC.ClientFinish.StaticKey.Length.Should().Be(48);
        messageC.ClientFinish.Payload.ToByteArray().Should().NotEqual(finish, "message C carries the finish encrypted, never in the clear");

        initiatorTransport.Dispose();
        responderTransport.Dispose();
    }

    [Fact]
    public void Writer_output_starts_with_the_client_hello_wire_tag_0x12()
    {
        using var initiator = new NoiseHandshake(new StaticPayloads("hello", "finish"));
        byte[] messageA = initiator.WriteMessageA();
        messageA.Should().NotBeEmpty();
        messageA[0].Should().Be(
            0x12,
            "the bare client_hello envelope opens with protobuf tag 18 (field 2, length-delimited) — no WA header prepended");
    }

    [Fact]
    public void Intro_header_validation_accepts_the_current_version_and_rejects_mismatches()
    {
        Action current = () => NoiseHandshake.ValidateIntroHeader(new byte[] { 0x57, 0x41, 0x06, 0x03 });
        current.Should().NotThrow("the pinned dictionary version is the only accepted intro");

        Action staleDictionary = () => NoiseHandshake.ValidateIntroHeader(new byte[] { 0x57, 0x41, 0x06, 0x02 });
        staleDictionary.Should()
            .Throw<SdkConnectionException>()
            .WithMessage("*dictionary version mismatch*", "a wrong version byte names the dictionary rule");

        Action wrongMagic = () => NoiseHandshake.ValidateIntroHeader(new byte[] { 0x57, 0x41, 0x05, 0x03 });
        wrongMagic.Should()
            .Throw<SdkConnectionException>()
            .WithMessage("*Unexpected protocol header*", "any other prefix names the protocol-header rule");
    }
}
