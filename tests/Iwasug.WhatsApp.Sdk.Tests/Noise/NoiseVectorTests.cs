using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Noise;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Noise;

/// <summary>
/// Cacophony vector proofs for <c>Noise_XX_25519_AESGCM_SHA256</c> (02-03,
/// CONN-04 crypto correctness, T-2-05): the spec core in
/// <see cref="NoiseSymmetricState"/> reproduces the canonical handshake and
/// transport ciphertexts byte-for-byte from the committed golden fixture.
/// Crypto correctness is imported, never self-generated.
/// </summary>
/// <remarks>
/// <para>
/// Fixture provenance: the single <c>Noise_XX_25519_AESGCM_SHA256</c> entry
/// extracted verbatim from <c>rweather/noise-c</c> (MIT)
/// <c>tests/vector/noise-c-basic.txt</c> — no values invented, converted, or
/// reordered. Message semantics follow noise-c's <c>test-vector.c</c>
/// driver: fixed static/ephemeral keys and prologues pin both parties; the
/// first three messages are the XX handshake (initiator, responder,
/// initiator); the remaining three are transport samples alternating
/// responder→initiator, initiator→responder, responder→initiator with
/// per-direction nonces from 0 and empty associated data.
/// </para>
/// <para>
/// Output discipline (V6/T-2-10): assertions compare hex strings of
/// ciphertexts/payloads/hashes (public vector data) and SHA-256 fingerprints
/// of direction keys — raw key bytes never enter an assertion message, so
/// even a failing run cannot print key material.
/// </para>
/// </remarks>
public sealed class NoiseVectorTests
{
    private sealed record VectorMessage(
        [property: JsonPropertyName("payload")] string Payload,
        [property: JsonPropertyName("ciphertext")] string Ciphertext);

    private sealed record HandshakeVector(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("pattern")] string Pattern,
        [property: JsonPropertyName("dh")] string Dh,
        [property: JsonPropertyName("cipher")] string Cipher,
        [property: JsonPropertyName("hash")] string Hash,
        [property: JsonPropertyName("init_prologue")] string InitPrologue,
        [property: JsonPropertyName("init_static")] string InitStatic,
        [property: JsonPropertyName("init_ephemeral")] string InitEphemeral,
        [property: JsonPropertyName("resp_prologue")] string RespPrologue,
        [property: JsonPropertyName("resp_static")] string RespStatic,
        [property: JsonPropertyName("resp_ephemeral")] string RespEphemeral,
        [property: JsonPropertyName("messages")] VectorMessage[] Messages,
        [property: JsonPropertyName("handshake_hash")] string HandshakeHash);

    private sealed class OpaquePayloads : INoiseHandshakePayloads
    {
        public byte[] GetClientHello() => Array.Empty<byte>();

        public byte[] GetClientFinish() => Array.Empty<byte>();
    }

    private static HandshakeVector LoadVector()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "noise-xx-aesgcm-vectors.json");
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<HandshakeVector>(json)
            ?? throw new InvalidOperationException("Noise vector fixture failed to deserialize.");
    }

    private static string Fingerprint(byte[] keyMaterial)
    {
        byte[] digest = SHA256.HashData(keyMaterial);
        return Convert.ToHexString(digest);
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static void RunHandshakeExchange(
        HandshakeVector vector,
        out byte[] initiatorSend,
        out byte[] initiatorReceive,
        out byte[] responderSend,
        out byte[] responderReceive,
        out byte[] initiatorHash,
        out byte[] responderHash)
    {
        using var initiator = new NoiseHandshake(
            new OpaquePayloads(),
            Convert.FromHexString(vector.InitStatic),
            Convert.FromHexString(vector.InitEphemeral),
            Convert.FromHexString(vector.InitPrologue));
        using var responder = new NoiseHandshakeResponder(
            Array.Empty<byte>(),
            Convert.FromHexString(vector.RespStatic),
            Convert.FromHexString(vector.RespEphemeral),
            Convert.FromHexString(vector.RespPrologue));

        byte[] payload0 = Convert.FromHexString(vector.Messages[0].Payload);
        byte[] wire1 = initiator.WriteRawMessage1(payload0);
        Hex(wire1).Should().Be(vector.Messages[0].Ciphertext);
        byte[] helloBack = responder.ReadRawMessage1(wire1);
        Hex(helloBack).Should().Be(vector.Messages[0].Payload);

        byte[] payload1 = Convert.FromHexString(vector.Messages[1].Payload);
        byte[] wire2 = responder.WriteRawMessage2(payload1);
        Hex(wire2).Should().Be(vector.Messages[1].Ciphertext);
        (byte[] initSend, byte[] initRecv, byte[] recovered1) = initiator.CompleteRawMessage2(wire2);
        Hex(recovered1).Should().Be(vector.Messages[1].Payload);

        byte[] payload2 = Convert.FromHexString(vector.Messages[2].Payload);
        byte[] wire3 = initiator.WriteRawMessage3(payload2);
        Hex(wire3).Should().Be(vector.Messages[2].Ciphertext);
        (byte[] respSend, byte[] respRecv, byte[] recovered2) = responder.CompleteRawMessage3(wire3);
        Hex(recovered2).Should().Be(vector.Messages[2].Payload);

        // Convergence: the early preview keys (frozen-surface requirement —
        // the transport is minted at message B) must equal the token-ordered
        // split after message 3. Both derive from the identical post-se chain.
        (byte[] realSend, byte[] realRecv) = initiator.SplitTransportKeys();
        Fingerprint(realSend).Should().Be(Fingerprint(initSend));
        Fingerprint(realRecv).Should().Be(Fingerprint(initRecv));

        initiatorSend = initSend;
        initiatorReceive = initRecv;
        responderSend = respSend;
        responderReceive = respRecv;
        initiatorHash = initiator.HandshakeHash;
        responderHash = responder.HandshakeHash;
    }

    [Fact]
    public void Fixture_pins_xx_25519_aesgcm_sha256_suite()
    {
        HandshakeVector vector = LoadVector();

        vector.Name.Should().Be("Noise_XX_25519_AESGCM_SHA256");
        vector.Pattern.Should().Be("XX");
        vector.Dh.Should().Be("25519");
        vector.Cipher.Should().Be("AESGCM");
        vector.Hash.Should().Be("SHA256");
        vector.Messages.Should().HaveCount(6, "XX contributes 3 handshake messages plus 3 transport samples");

        Convert.FromHexString(vector.InitStatic).Should().HaveCount(32);
        Convert.FromHexString(vector.InitEphemeral).Should().HaveCount(32);
        Convert.FromHexString(vector.RespStatic).Should().HaveCount(32);
        Convert.FromHexString(vector.RespEphemeral).Should().HaveCount(32);
        Convert.FromHexString(vector.HandshakeHash).Should().HaveCount(32);
    }

    [Fact]
    public void XX_handshake_messages_match_cacophony_ciphertexts()
    {
        HandshakeVector vector = LoadVector();

        RunHandshakeExchange(
            vector,
            out byte[] initiatorSend,
            out byte[] initiatorReceive,
            out byte[] responderSend,
            out byte[] responderReceive,
            out byte[] initiatorHash,
            out byte[] responderHash);

        Hex(initiatorHash).Should().Be(vector.HandshakeHash);
        Hex(responderHash).Should().Be(vector.HandshakeHash);

        Fingerprint(initiatorSend).Should().Be(Fingerprint(responderReceive));
        Fingerprint(initiatorReceive).Should().Be(Fingerprint(responderSend));
        Fingerprint(initiatorSend).Should().NotBe(Fingerprint(initiatorReceive));
    }

    [Theory]
    [InlineData(3, true, 0UL)]
    [InlineData(4, false, 0UL)]
    [InlineData(5, true, 1UL)]
    public void XX_transport_messages_match_cacophony_ciphertexts(int messageIndex, bool responderSends, ulong nonce)
    {
        HandshakeVector vector = LoadVector();
        RunHandshakeExchange(
            vector,
            out byte[] initiatorSend,
            out byte[] initiatorReceive,
            out byte[] responderSend,
            out byte[] responderReceive,
            out _,
            out _);

        byte[] sendKey = responderSends ? responderSend : initiatorSend;
        byte[] receiveKey = responderSends ? initiatorReceive : responderReceive;
        byte[] payload = Convert.FromHexString(vector.Messages[messageIndex].Payload);

        byte[] sample = NoiseSymmetricState.AeadEncrypt(sendKey, nonce, Array.Empty<byte>(), payload);
        Hex(sample).Should().Be(vector.Messages[messageIndex].Ciphertext);

        byte[] recovered = NoiseSymmetricState.AeadDecrypt(receiveKey, nonce, Array.Empty<byte>(), sample);
        Hex(recovered).Should().Be(vector.Messages[messageIndex].Payload);

        byte[] tampered = (byte[])sample.Clone();
        tampered[tampered.Length - 1] ^= 0xFF;
        Assert.Throws<AuthenticationTagMismatchException>(
            () => NoiseSymmetricState.AeadDecrypt(receiveKey, nonce, Array.Empty<byte>(), tampered));
    }
}
