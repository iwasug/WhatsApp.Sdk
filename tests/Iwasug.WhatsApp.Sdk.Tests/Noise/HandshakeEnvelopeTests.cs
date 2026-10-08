using AwesomeAssertions;
using Google.Protobuf;
using Iwasug.WhatsApp.Sdk.Noise;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Noise;

/// <summary>
/// Golden-wire tests for the outer Noise handshake protobuf envelope.
/// The fixed values are synthetic and intentionally carry no real key material.
/// </summary>
public sealed class HandshakeEnvelopeTests
{
    private static readonly byte[] SyntheticEphemeral = CreateSyntheticEphemeral();
    private static readonly byte[] SyntheticPayload = [0x01, 0x02, 0x03, 0x04];

    [Fact]
    public void ClientHello_envelope_uses_wire_tag_0x12()
    {
        var envelope = new HandshakeMessage
        {
            ClientHello = new ClientHello
            {
                Ephemeral = ByteString.CopyFrom(SyntheticEphemeral),
                Payload = ByteString.CopyFrom(SyntheticPayload),
            },
        };

        byte[] wire = envelope.ToByteArray();

        wire[0].Should().Be(0x12);
    }

    [Fact]
    public void ServerHello_envelope_uses_wire_tag_0x1A()
    {
        var envelope = new HandshakeMessage
        {
            ServerHello = new ServerHello
            {
                Ephemeral = ByteString.CopyFrom(SyntheticEphemeral),
                Payload = ByteString.CopyFrom(SyntheticPayload),
            },
        };

        byte[] wire = envelope.ToByteArray();

        wire[0].Should().Be(0x1A);
    }

    [Fact]
    public void ClientFinish_envelope_uses_wire_tag_0x22()
    {
        var envelope = new HandshakeMessage
        {
            ClientFinish = new ClientFinish
            {
                StaticKey = ByteString.CopyFrom(SyntheticEphemeral),
                Payload = ByteString.CopyFrom(SyntheticPayload),
            },
        };

        byte[] wire = envelope.ToByteArray();

        wire[0].Should().Be(0x22);
    }

    [Fact]
    public void ServerHello_field_3_golden_bytes_parse_to_server_shape()
    {
        var serverHello = new ServerHello
        {
            Ephemeral = ByteString.CopyFrom(SyntheticEphemeral),
            Payload = ByteString.CopyFrom(SyntheticPayload),
        };
        byte[] inner = serverHello.ToByteArray();
        byte[] wire = WrapLengthDelimited(0x1A, inner);

        HandshakeMessage parsed = HandshakeMessage.Parser.ParseFrom(wire);

        parsed.ServerHello.Should().NotBeNull();
        parsed.ServerHello.Ephemeral.Length.Should().Be(32);
        parsed.ServerHello.Ephemeral.ToByteArray().Should().Equal(SyntheticEphemeral);
        parsed.ClientHello.Should().BeNull();
        parsed.ClientFinish.Should().BeNull();
    }

    [Fact]
    public void ClientHello_envelope_round_trips_without_other_envelopes()
    {
        var envelope = new HandshakeMessage
        {
            ClientHello = new ClientHello
            {
                Ephemeral = ByteString.CopyFrom(SyntheticEphemeral),
                Payload = ByteString.CopyFrom(SyntheticPayload),
            },
        };

        HandshakeMessage parsed = HandshakeMessage.Parser.ParseFrom(envelope.ToByteArray());

        parsed.ClientHello.Should().NotBeNull();
        parsed.ClientHello.Ephemeral.ToByteArray().Should().Equal(SyntheticEphemeral);
        parsed.ClientHello.Payload.ToByteArray().Should().Equal(SyntheticPayload);
        parsed.ServerHello.Should().BeNull();
        parsed.ClientFinish.Should().BeNull();
    }

    private static byte[] WrapLengthDelimited(byte tag, byte[] value)
    {
        value.Length.Should().BeLessThan(128);
        return [tag, (byte)value.Length, .. value];
    }

    private static byte[] CreateSyntheticEphemeral() =>
        [
            0x2A, 0x2A, 0x2A, 0x2A, 0x2A, 0x2A, 0x2A, 0x2A,
            0x2A, 0x2A, 0x2A, 0x2A, 0x2A, 0x2A, 0x2A, 0x2A,
            0x2A, 0x2A, 0x2A, 0x2A, 0x2A, 0x2A, 0x2A, 0x2A,
            0x2A, 0x2A, 0x2A, 0x2A, 0x2A, 0x2A, 0x2A, 0x2A,
        ];
}
