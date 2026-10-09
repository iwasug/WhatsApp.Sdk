using System.Reflection;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Pairing;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Pairing;

/// <summary>
/// Pairing-surface proofs (03-03 Task 1, CONN-01): the QR payload composes the
/// exact wire string, rotation/timeout follow the shared constants on an injected
/// clock (never wall time), the reference stays opaque on the public surface, and
/// the status vocabulary is complete.
/// </summary>
/// <remarks>
/// <para>
/// Fixture discipline: the expected wire string below is a literal pinned against
/// independently-computed base64 (not produced by the type under test) — a join
/// order or prefix edit fails here, not on a live phone. Fixed inputs are public
/// fixture material only; assertion messages never carry key bytes.
/// </para>
/// </remarks>
public sealed class PairingQrTests
{
    private const string ExpectedWireString =
        "https://wa.me/settings/linked_devices#" +
        "TEST-REF-01," +
        "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=," +
        "ISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+P0A=," +
        "QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVpbXF1eX2A=," +
        "android";

    private static byte[] FixedBytes(int start, int length = 32)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(start + i);
        }

        return bytes;
    }

    [Fact]
    public void Build_composes_the_exact_wire_string()
    {
        string actual = PairingQrPayload.Build(
            "TEST-REF-01",
            FixedBytes(0x01),
            FixedBytes(0x21),
            FixedBytes(0x41),
            "android");

        actual.Should().Be(
            ExpectedWireString,
            "the host renders this exact string — join order and prefix are the wire contract");
    }

    [Fact]
    public void Rotation_policy_first_code_60s_then_20s_with_160s_timeout()
    {
        // The clock is injected (FakeTimeProvider): instants flow into the pure
        // schedule — this test never sleeps real time.
        var clock = new FakeTimeProvider();
        DateTimeOffset start = clock.GetUtcNow();

        PairingQrPayload.FirstCodeLifetime.Should().Be(TimeSpan.FromSeconds(60));
        PairingQrPayload.RotationInterval.Should().Be(TimeSpan.FromSeconds(20));
        PairingQrPayload.AttemptTimeout.Should().Be(TimeSpan.FromSeconds(160));

        PairingQrPayload.RotationFor(0).Should().Be(TimeSpan.FromSeconds(60));
        PairingQrPayload.RotationFor(1).Should().Be(TimeSpan.FromSeconds(20));
        PairingQrPayload.RotationFor(5).Should().Be(TimeSpan.FromSeconds(20));

        PairingQrPayload.CodeExpiry(start, 0).Should().Be(start + TimeSpan.FromSeconds(60));
        PairingQrPayload.CodeExpiry(start, 1).Should().Be(start + TimeSpan.FromSeconds(20));

        PairingQrPayload.AttemptExpired(start, start + TimeSpan.FromSeconds(159)).Should().BeFalse();
        PairingQrPayload.AttemptExpired(start, start + TimeSpan.FromSeconds(160)).Should().BeTrue();
    }

    [Fact]
    public void Build_passes_the_reference_through_opaque_with_no_interpreting_member()
    {
        const string trickyRef = "ref,with,commas#and-fragments";
        string payload = PairingQrPayload.Build(
            trickyRef,
            FixedBytes(0x01),
            FixedBytes(0x21),
            FixedBytes(0x41),
            "android");

        payload.Should().Contain(
            PairingQrPayload.WirePrefix + trickyRef + ",",
            "the reference travels byte-for-byte from input to string");

        // The opacity boundary (T-3-06, CONN-01 requirement): no public member may
        // split, decode, or parse the reference — asserted on the public surface.
        string[] forbiddenFragments = ["Split", "Decode", "Parse"];
        MemberInfo[] members = typeof(PairingQrPayload).GetMembers(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
        foreach (MemberInfo member in members)
        {
            foreach (string fragment in forbiddenFragments)
            {
                member.Name.Should().NotContain(
                    fragment,
                    $"public member {member.Name} must not interpret the QR reference");
            }
        }
    }

    [Fact]
    public void PairingStatus_carries_exactly_five_states_with_status_and_payload()
    {
        string[] names = Enum.GetNames<PairingStatus>();
        names.Should().HaveCount(5, "the host observes every pairing outcome through this one vocabulary");
        foreach (string expected in new[] { "Code", "Success", "Timeout", "LoggedOut", "ClientOutdated" })
        {
            names.Should().Contain(expected);
        }

        var args = new PairingStateChangedEventArgs(PairingStatus.Code, ExpectedWireString);
        args.Status.Should().Be(PairingStatus.Code);
        args.Payload.Should().Be(ExpectedWireString);

        var terminal = new PairingStateChangedEventArgs(PairingStatus.Timeout, null);
        terminal.Status.Should().Be(PairingStatus.Timeout);
        terminal.Payload.Should().BeNull();
    }

    [Fact]
    public void Rotation_over_time_issues_codes_on_schedule_and_ends_in_timeout()
    {
        // Drive the injected clock through 0+60+20+20+20+20+20 = 160 seconds: six
        // codes on schedule, then the attempt ends at the timeout boundary.
        var clock = new FakeTimeProvider();
        DateTimeOffset start = clock.GetUtcNow();
        DateTimeOffset now = start;

        TimeSpan[] expectedLifetimes =
        [
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(20),
        ];

        for (int codeIndex = 0; codeIndex < expectedLifetimes.Length; codeIndex++)
        {
            PairingQrPayload.AttemptExpired(start, now).Should().BeFalse(
                $"code {codeIndex} is still inside the attempt window");
            TimeSpan lifetime = PairingQrPayload.RotationFor(codeIndex);
            lifetime.Should().Be(expectedLifetimes[codeIndex]);
            DateTimeOffset expiry = PairingQrPayload.CodeExpiry(now, codeIndex);
            expiry.Should().Be(now + expectedLifetimes[codeIndex]);

            // A code payload builds at every step of the schedule.
            string payload = PairingQrPayload.Build(
                $"REF-{codeIndex}",
                FixedBytes(0x01),
                FixedBytes(0x21),
                FixedBytes(0x41),
                "android");
            payload.Should().StartWith(PairingQrPayload.WirePrefix);

            clock.Advance(lifetime);
            now = clock.GetUtcNow();
        }

        now.Should().Be(start + PairingQrPayload.AttemptTimeout);
        PairingQrPayload.AttemptExpired(start, now).Should().BeTrue(
            "the attempt ends at the timeout boundary with the Timeout status");
    }

    [Fact]
    public void Build_rejects_malformed_inputs_without_echoing_material()
    {
        Action emptyRef = () => PairingQrPayload.Build(
            string.Empty, FixedBytes(0x01), FixedBytes(0x21), FixedBytes(0x41), "android");
        emptyRef.Should().Throw<SdkFormatException>();

        Action shortNoise = () => PairingQrPayload.Build(
            "ref", FixedBytes(0x01, 16), FixedBytes(0x21), FixedBytes(0x41), "android");
        shortNoise.Should().Throw<SdkFormatException>();

        Action shortAdv = () => PairingQrPayload.Build(
            "ref", FixedBytes(0x01), FixedBytes(0x21), FixedBytes(0x41, 16), "android");
        shortAdv.Should().Throw<SdkFormatException>();

        Action emptyPlatform = () => PairingQrPayload.Build(
            "ref", FixedBytes(0x01), FixedBytes(0x21), FixedBytes(0x41), string.Empty);
        emptyPlatform.Should().Throw<SdkFormatException>();
    }
}
