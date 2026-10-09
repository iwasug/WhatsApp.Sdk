using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Pairing;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Pairing;

/// <summary>
/// Golden-fixture proofs (03-01, CONN-03) for both ClientPayload directions: the
/// registration and login bytes are pinned as committed fixtures so a protobuf
/// field-number edit fails CI instead of the live server.
/// </summary>
/// <remarks>
/// <para>
/// Regression intent: the fixture hex values are THIS repository's own protoc output
/// for its hand-written schema (field roles from Baileys read-only, repo-local v1
/// numbers — see the .proto headers). A deliberate field-number mutation in
/// <c>client_payload.proto</c> changes the serialized bytes and fails the equality
/// asserts below; that is the mechanism working, not a brittle test.
/// </para>
/// <para>
/// Fixture provenance: committed JSON records deserialized with
/// <c>[JsonPropertyName]</c> (NoiseVectorTests precedent), each carrying a provenance
/// doc block. The fixed inputs are public fixture material (identity publics,
/// prekey publics, signatures-as-opaque-bytes) — no private key bytes appear anywhere
/// in this file, and assertion messages carry only hex of the serialized payloads.
/// </para>
/// </remarks>
public sealed class ClientPayloadTests
{
    private sealed record PayloadFixture(
        [property: JsonPropertyName("provenance")] string Provenance,
        [property: JsonPropertyName("direction")] string Direction,
        [property: JsonPropertyName("payloadHex")] string PayloadHex);

    private static byte[] FixedBytes(int start, int length = 32)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(start + i);
        }

        return bytes;
    }

    private static PayloadFixture LoadFixture(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", name);
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<PayloadFixture>(json)
            ?? throw new InvalidOperationException($"ClientPayload fixture {name} failed to deserialize.");
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    [Fact]
    public void Login_fixture_bytes_match_committed_protoc_output()
    {
        PayloadFixture fixture = LoadFixture("client-payload-login.json");
        fixture.Direction.Should().Be("login");

        byte[] actual = ClientPayloadFactory.Login("1234567890", 1, "android", 2, 3000, 1043857760);

        Hex(actual).Should().Be(fixture.PayloadHex, "a field-number change must fail CI, not the live server");
    }

    [Fact]
    public void Registration_fixture_bytes_match_committed_protoc_output()
    {
        PayloadFixture fixture = LoadFixture("client-payload-registration.json");
        fixture.Direction.Should().Be("registration");

        byte[] actual = ClientPayloadFactory.Registration(
            FixedBytes(0x11), 7, FixedBytes(0x31), FixedBytes(0xA0, 64), 42, "android", 2, 3000, 1043857760);

        Hex(actual).Should().Be(fixture.PayloadHex, "a field-number change must fail CI, not the live server");
    }

    [Fact]
    public void Login_decodes_with_login_shape_and_no_registration_fields()
    {
        PayloadFixture fixture = LoadFixture("client-payload-login.json");
        ClientPayload payload = ClientPayload.Parser.ParseFrom(Convert.FromHexString(fixture.PayloadHex));

        payload.Username.Should().Be(1234567890UL);
        payload.Device.Should().Be(1u);
        payload.Passive.Should().BeTrue();
        payload.Pull.Should().BeTrue();
        payload.DevicePairingData.Should().BeNull("the login fixture must never contain registration fields");
        payload.WebInfo.Should().BeNull("web_info stays absent on android companions");
    }

    [Fact]
    public void Registration_decodes_with_registration_shape_and_no_login_fields()
    {
        PayloadFixture fixture = LoadFixture("client-payload-registration.json");
        ClientPayload payload = ClientPayload.Parser.ParseFrom(Convert.FromHexString(fixture.PayloadHex));

        payload.Passive.Should().BeFalse();
        payload.Pull.Should().BeFalse();
        payload.Username.Should().Be(0, "registration carries no paired username");
        payload.Device.Should().Be(0, "registration carries no paired device");
        payload.DevicePairingData.Should().NotBeNull();
        payload.DevicePairingData!.BuildHash.Should().Equal(System.Security.Cryptography.MD5.HashData("2.3000.1043857760"u8.ToArray()));
        payload.DevicePairingData.ERegid.Should().Equal(new byte[] { 0x00, 0x00, 0x00, 0x2A }, "registration id 42 as 4-byte big-endian");
        payload.DevicePairingData.EKeytype.Should().Equal(new byte[] { 0x05 }, "key-bundle type byte");
        payload.DevicePairingData.EIdent.Should().Equal(FixedBytes(0x11));
        payload.DevicePairingData.ESkeyId.Should().Equal(new byte[] { 0x00, 0x00, 0x07 }, "signed-prekey id 7 as 3-byte big-endian");
        payload.DevicePairingData.ESkeyVal.Should().Equal(FixedBytes(0x31));
        payload.DevicePairingData.ESkeySig.Should().Equal(FixedBytes(0xA0, 64));
        payload.WebInfo.Should().BeNull("web_info stays absent on android companions");
    }

    [Fact]
    public void Fixtures_carry_provenance_without_upstream_bytes()
    {
        PayloadFixture login = LoadFixture("client-payload-login.json");
        PayloadFixture registration = LoadFixture("client-payload-registration.json");

        login.Provenance.Should().Contain("Baileys");
        login.Provenance.Should().Contain("no whatsmeow");
        registration.Provenance.Should().Contain("Baileys");
        registration.Provenance.Should().Contain("no whatsmeow");
    }
}
