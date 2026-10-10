using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Google.Protobuf;
using Iwasug.WhatsApp.Sdk.Pairing;
using NSec.Cryptography;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Pairing;

/// <summary>
/// Fail-closed proofs (03-04 Task 1, CONN-01, T-3-08) for
/// <see cref="PairSuccessVerifier"/>: a well-formed container verifies with
/// its payload, and every forgery class — HMAC, account signature, device
/// signature, missing covered field, unknown field numbers — rejects with a
/// log-safe reason and exposes no usable payload.
/// </summary>
/// <remarks>
/// <para>
/// Fixture discipline (NoiseVectorTests/ClientPayloadTests precedent):
/// containers are built in-test from fixed seeds through this repo's own
/// protoc output plus NSec Ed25519 — no upstream bytes anywhere. The
/// committed <c>pair-success-container.json</c> pins the schema bytes so a
/// field-number edit fails CI instead of the live phone.
/// </para>
/// <para>
/// Output discipline (V6/T-3-07): assertions compare verdicts, reason codes,
/// and hex of PUBLIC values (account/device publics, serialized payloads)
/// only — private seeds and the verification secret never enter an assertion
/// argument or message, so even a failing run cannot print key material.
/// </para>
/// <para>
/// Constant-time discipline: the HMAC comparison uses
/// <c>CryptographicOperations.FixedTimeEquals</c> (never <c>==</c> or
/// <c>SequenceEqual</c> — asserted by the acceptance gate), and the
/// near-miss test below proves behaviorally that a same-length one-byte-off
/// HMAC rejects identically, with no early-exit oracle distinguishable by
/// outcome.
/// </para>
/// </remarks>
public sealed class PairSuccessVerifierTests
{
    private const string FixtureUser = "1234567890";
    private const uint FixtureDevice = 1;
    private const uint FixtureRegistrationId = 42;

    private sealed record PairSuccessFixture(
        [property: JsonPropertyName("provenance")] string Provenance,
        [property: JsonPropertyName("containerHex")] string ContainerHex,
        [property: JsonPropertyName("inputs")] PairSuccessFixtureInputs Inputs,
        [property: JsonPropertyName("expected")] PairSuccessFixtureExpected Expected);

    private sealed record PairSuccessFixtureInputs(
        [property: JsonPropertyName("pairedUser")] string PairedUser,
        [property: JsonPropertyName("device")] int Device,
        [property: JsonPropertyName("registrationId")] int RegistrationId,
        [property: JsonPropertyName("accountPublicHex")] string AccountPublicHex,
        [property: JsonPropertyName("devicePublicHex")] string DevicePublicHex);

    private sealed record PairSuccessFixtureExpected(
        [property: JsonPropertyName("verified")] bool Verified,
        [property: JsonPropertyName("pairedUser")] string PairedUser,
        [property: JsonPropertyName("device")] int Device,
        [property: JsonPropertyName("registrationId")] int RegistrationId);

    private sealed record BuiltContainer(
        byte[] Container,
        byte[] AccountPublic,
        byte[] DevicePublic);

    private static byte[] FixedBytes(int start, int length = 32)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(start + i);
        }

        return bytes;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    // Fixed build seeds (test-only fixture material): the same seeds produced
    // the committed golden fixture. Secrets flow only into builders below —
    // never into assertions.
    private static byte[] FixtureAccountSeed() => FixedBytes(0x50);

    private static byte[] FixtureDeviceSeed() => FixedBytes(0x70);

    private static byte[] FixtureSecret() => FixedBytes(0x61);

    private static byte[] Ed25519PublicOf(byte[] seed)
    {
        byte[] copy = (byte[])seed.Clone();
        try
        {
            using Key key = Key.Import(SignatureAlgorithm.Ed25519, copy, KeyBlobFormat.RawPrivateKey);
            return key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    private static byte[] Ed25519Sign(byte[] seed, byte[] data)
    {
        byte[] copy = (byte[])seed.Clone();
        try
        {
            using Key key = Key.Import(SignatureAlgorithm.Ed25519, copy, KeyBlobFormat.RawPrivateKey);
            return SignatureAlgorithm.Ed25519.Sign(key, data);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    private static byte[] Join(params byte[][] segments)
    {
        int total = 0;
        foreach (byte[] segment in segments)
        {
            total += segment.Length;
        }

        byte[] result = new byte[total];
        int offset = 0;
        foreach (byte[] segment in segments)
        {
            segment.CopyTo(result.AsSpan(offset));
            offset += segment.Length;
        }

        return result;
    }

    private static BuiltContainer BuildValidContainer(
        byte[] accountSeed,
        byte[] deviceSeed,
        byte[] secret,
        string pairedUser = FixtureUser,
        uint device = FixtureDevice,
        uint registrationId = FixtureRegistrationId)
    {
        byte[] accountPublic = Ed25519PublicOf(accountSeed);
        byte[] devicePublic = Ed25519PublicOf(deviceSeed);
        var details = new AdvDetails
        {
            PairedUser = pairedUser,
            Device = device,
            RegistrationId = registrationId,
            AccountPublic = ByteString.CopyFrom(accountPublic),
        };
        byte[] detailsBytes = details.ToByteArray();
        byte[] hmac = HMACSHA256.HashData(secret, detailsBytes);
        byte[] accountSignature = Ed25519Sign(accountSeed, Join([6, 0], detailsBytes));
        byte[] deviceSignature = Ed25519Sign(deviceSeed, Join([6, 1], detailsBytes, accountPublic));
        var container = new PairSuccessContainer
        {
            Details = details,
            Hmac = ByteString.CopyFrom(hmac),
            AccountSignature = ByteString.CopyFrom(accountSignature),
            DeviceSignature = ByteString.CopyFrom(deviceSignature),
        };
        return new BuiltContainer(container.ToByteArray(), accountPublic, devicePublic);
    }

    private static PairSuccessFixture LoadFixture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "pair-success-container.json");
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<PairSuccessFixture>(json)
            ?? throw new InvalidOperationException("Pair-success fixture failed to deserialize.");
    }

    private static void AssertRejected(PairSuccessResult result, PairSuccessRejectReason expected)
    {
        // The discriminated result enforces the fail-closed type contract: a
        // Rejected branch carries no payload to assert on — the BeOfType check
        // IS the "no extracted fields" proof.
        result.Should().BeOfType<PairSuccessResult.Rejected>(
            "a forged container must fail closed with zero usable payload");
        ((PairSuccessResult.Rejected)result).Reason.Should().Be(
            expected, "the first failing rule names the rejection");
    }

    [Fact]
    public void Well_formed_container_verifies_with_extracted_payload()
    {
        BuiltContainer built = BuildValidContainer(FixtureAccountSeed(), FixtureDeviceSeed(), FixtureSecret());

        PairSuccessResult result = PairSuccessVerifier.Verify(built.Container, FixtureSecret(), built.DevicePublic);

        result.Should().BeOfType<PairSuccessResult.Verified>();
        PairSuccessPayload payload = ((PairSuccessResult.Verified)result).Payload;
        payload.PairedUser.Should().Be(FixtureUser);
        payload.Device.Should().Be((int)FixtureDevice);
        payload.RegistrationId.Should().Be((int)FixtureRegistrationId);
    }

    [Fact]
    public void Forged_hmac_field_rejects_with_no_payload()
    {
        BuiltContainer built = BuildValidContainer(FixtureAccountSeed(), FixtureDeviceSeed(), FixtureSecret());

        // Flip one byte inside the HMAC-covered account key: the container
        // still parses, but the MAC no longer matches — fail closed.
        PairSuccessContainer parsed = PairSuccessContainer.Parser.ParseFrom(built.Container);
        byte[] tamperedKey = parsed.Details.AccountPublic.ToByteArray();
        tamperedKey[0] ^= 0xFF;
        parsed.Details.AccountPublic = ByteString.CopyFrom(tamperedKey);
        byte[] forged = parsed.ToByteArray();

        PairSuccessResult result = PairSuccessVerifier.Verify(forged, FixtureSecret(), built.DevicePublic);

        AssertRejected(result, PairSuccessRejectReason.HmacMismatch);
    }

    [Fact]
    public void Forged_account_signature_rejects()
    {
        BuiltContainer built = BuildValidContainer(FixtureAccountSeed(), FixtureDeviceSeed(), FixtureSecret());

        // Same claimed account key, but the signature comes from another key.
        PairSuccessContainer parsed = PairSuccessContainer.Parser.ParseFrom(built.Container);
        byte[] detailsBytes = parsed.Details.ToByteArray();
        parsed.AccountSignature = ByteString.CopyFrom(Ed25519Sign(FixedBytes(0x90), Join([6, 0], detailsBytes)));
        byte[] forged = parsed.ToByteArray();

        PairSuccessResult result = PairSuccessVerifier.Verify(forged, FixtureSecret(), built.DevicePublic);

        AssertRejected(result, PairSuccessRejectReason.AccountSignatureInvalid);
    }

    [Fact]
    public void Forged_device_signature_rejects()
    {
        BuiltContainer built = BuildValidContainer(FixtureAccountSeed(), FixtureDeviceSeed(), FixtureSecret());

        // Valid HMAC plus a valid account signature, but the device signature
        // comes from another key.
        PairSuccessContainer parsed = PairSuccessContainer.Parser.ParseFrom(built.Container);
        byte[] detailsBytes = parsed.Details.ToByteArray();
        byte[] accountPublic = parsed.Details.AccountPublic.ToByteArray();
        parsed.DeviceSignature = ByteString.CopyFrom(
            Ed25519Sign(FixedBytes(0x90), Join([6, 1], detailsBytes, accountPublic)));
        byte[] forged = parsed.ToByteArray();

        PairSuccessResult result = PairSuccessVerifier.Verify(forged, FixtureSecret(), built.DevicePublic);

        AssertRejected(result, PairSuccessRejectReason.DeviceSignatureInvalid);
    }

    [Fact]
    public void Missing_covered_user_rejects_before_crypto()
    {
        byte[] accountPublic = Ed25519PublicOf(FixtureAccountSeed());
        byte[] devicePublic = Ed25519PublicOf(FixtureDeviceSeed());
        var details = new AdvDetails
        {
            Device = FixtureDevice,
            RegistrationId = FixtureRegistrationId,
            AccountPublic = ByteString.CopyFrom(accountPublic),
        };
        byte[] detailsBytes = details.ToByteArray();
        var container = new PairSuccessContainer
        {
            Details = details,
            Hmac = ByteString.CopyFrom(HMACSHA256.HashData(FixtureSecret(), detailsBytes)),
            AccountSignature = ByteString.CopyFrom(Ed25519Sign(FixtureAccountSeed(), Join([6, 0], detailsBytes))),
            DeviceSignature = ByteString.CopyFrom(
                Ed25519Sign(FixtureDeviceSeed(), Join([6, 1], detailsBytes, accountPublic))),
        };

        PairSuccessResult result = PairSuccessVerifier.Verify(container.ToByteArray(), FixtureSecret(), devicePublic);

        AssertRejected(result, PairSuccessRejectReason.MissingField);
    }

    [Fact]
    public void Unknown_top_level_field_rejects()
    {
        BuiltContainer built = BuildValidContainer(FixtureAccountSeed(), FixtureDeviceSeed(), FixtureSecret());

        // Field 99 (varint 1) appended to an otherwise valid container:
        // protobuf tolerance would accept it — verification must not.
        byte[] forged = Join(built.Container, [0x98, 0x06, 0x01]);

        PairSuccessResult result = PairSuccessVerifier.Verify(forged, FixtureSecret(), built.DevicePublic);

        AssertRejected(result, PairSuccessRejectReason.UnknownField);
    }

    [Fact]
    public void Unknown_nested_field_rejects_despite_matching_hmac()
    {
        byte[] accountPublic = Ed25519PublicOf(FixtureAccountSeed());
        byte[] devicePublic = Ed25519PublicOf(FixtureDeviceSeed());
        var clean = new AdvDetails
        {
            PairedUser = FixtureUser,
            Device = FixtureDevice,
            RegistrationId = FixtureRegistrationId,
            AccountPublic = ByteString.CopyFrom(accountPublic),
        };

        // The unknown field lives INSIDE the HMAC envelope and the MAC is
        // computed over it — tolerance still must not leak into verification.
        byte[] detailsBytes = Join(clean.ToByteArray(), [0x98, 0x06, 0x01]);
        var details = AdvDetails.Parser.ParseFrom(detailsBytes);
        var container = new PairSuccessContainer
        {
            Details = details,
            Hmac = ByteString.CopyFrom(HMACSHA256.HashData(FixtureSecret(), detailsBytes)),
            AccountSignature = ByteString.CopyFrom(Ed25519Sign(FixtureAccountSeed(), Join([6, 0], detailsBytes))),
            DeviceSignature = ByteString.CopyFrom(
                Ed25519Sign(FixtureDeviceSeed(), Join([6, 1], detailsBytes, accountPublic))),
        };

        PairSuccessResult result = PairSuccessVerifier.Verify(container.ToByteArray(), FixtureSecret(), devicePublic);

        AssertRejected(result, PairSuccessRejectReason.UnknownField);
    }

    [Fact]
    public void Golden_fixture_pins_schema_bytes_and_verdict()
    {
        PairSuccessFixture fixture = LoadFixture();
        fixture.Inputs.PairedUser.Should().Be(FixtureUser);
        fixture.Expected.Verified.Should().BeTrue("the committed container is the valid reference");

        // The schema pin: rebuilding from the fixed seeds must reproduce the
        // committed bytes exactly — a field-number edit breaks this, not live pairing.
        BuiltContainer rebuilt = BuildValidContainer(FixtureAccountSeed(), FixtureDeviceSeed(), FixtureSecret());
        Hex(rebuilt.Container).Should().Be(
            fixture.ContainerHex, "a field-number change must fail CI, not the live server");
        Hex(rebuilt.AccountPublic).Should().Be(fixture.Inputs.AccountPublicHex);
        Hex(rebuilt.DevicePublic).Should().Be(fixture.Inputs.DevicePublicHex);

        // The verdict pin: the committed bytes verify against the committed
        // public inputs with the expected extracted payload.
        PairSuccessResult result = PairSuccessVerifier.Verify(
            Convert.FromHexString(fixture.ContainerHex),
            FixtureSecret(),
            Convert.FromHexString(fixture.Inputs.DevicePublicHex));

        result.Should().BeOfType<PairSuccessResult.Verified>();
        PairSuccessPayload payload = ((PairSuccessResult.Verified)result).Payload;
        payload.PairedUser.Should().Be(fixture.Expected.PairedUser);
        payload.Device.Should().Be(fixture.Expected.Device);
        payload.RegistrationId.Should().Be(fixture.Expected.RegistrationId);
    }

    [Fact]
    public void Near_miss_hmac_rejects_identically_to_forged_hmac()
    {
        BuiltContainer built = BuildValidContainer(FixtureAccountSeed(), FixtureDeviceSeed(), FixtureSecret());

        // Same length, one byte different — must reject with the identical
        // reason (no early-exit oracle distinguishable by outcome).
        PairSuccessContainer parsed = PairSuccessContainer.Parser.ParseFrom(built.Container);
        byte[] nearMiss = parsed.Hmac.ToByteArray();
        nearMiss[^1] ^= 0x01;
        parsed.Hmac = ByteString.CopyFrom(nearMiss);
        byte[] forged = parsed.ToByteArray();

        PairSuccessResult result = PairSuccessVerifier.Verify(forged, FixtureSecret(), built.DevicePublic);

        AssertRejected(result, PairSuccessRejectReason.HmacMismatch);
    }

    [Fact]
    public void Null_container_is_a_programming_error()
    {
        Action verify = () => PairSuccessVerifier.Verify(null!, FixtureSecret(), FixedBytes(0x70));

        verify.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Out_of_domain_registration_id_rejects_despite_valid_signatures()
    {
        // uint32.MaxValue passes HMAC and both signatures (it is inside the
        // authenticated envelope) yet casts to -1 as int — the verifier must
        // reject it rather than persist a row that bricks every future login.
        BuiltContainer built = BuildValidContainer(
            FixtureAccountSeed(), FixtureDeviceSeed(), FixtureSecret(),
            registrationId: uint.MaxValue);

        PairSuccessResult result = PairSuccessVerifier.Verify(built.Container, FixtureSecret(), built.DevicePublic);

        AssertRejected(result, PairSuccessRejectReason.Malformed);
    }

    [Fact]
    public void Out_of_domain_device_index_rejects_despite_valid_signatures()
    {
        // Same authenticated-but-out-of-domain shape for the device index: a
        // >= 2^31 value casts negative and ClientPayloadFactory.Login would
        // throw on every subsequent handshake — fail closed here instead.
        BuiltContainer built = BuildValidContainer(
            FixtureAccountSeed(), FixtureDeviceSeed(), FixtureSecret(),
            device: uint.MaxValue);

        PairSuccessResult result = PairSuccessVerifier.Verify(built.Container, FixtureSecret(), built.DevicePublic);

        AssertRejected(result, PairSuccessRejectReason.Malformed);
    }

    [Fact]
    public void Fixture_carries_provenance_without_upstream_bytes()
    {
        PairSuccessFixture fixture = LoadFixture();

        fixture.Provenance.Should().Contain("Baileys");
        fixture.Provenance.Should().Contain("no whatsmeow");
    }
}
