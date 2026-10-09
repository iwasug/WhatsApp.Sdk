using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Iwasug.WhatsApp.Sdk.Errors;

namespace Iwasug.WhatsApp.Sdk.Pairing;

/// <summary>
/// Builds the serialized <c>ClientPayload</c> bytes carried opaquely in Noise message C —
/// the registration content (no stored credentials) and the login content (stored
/// credentials), through Google.Protobuf over the hand-written
/// <c>Proto/client_payload.proto</c> schema.
/// </summary>
/// <remarks>
/// <para>
/// Role provenance (Baileys MIT, commit 0af2386, read-only): registration vs login
/// contents, flag polarities, and the devicePairingData children come from
/// <c>src/Utils/validate-connection.ts</c> (generateRegistrationNode /
/// generateLoginNode); the md5 build hash, big-endian encodings, bundled app version,
/// and <c>KEY_BUNDLE_TYPE = [5]</c> come from <c>src/Utils/generics.ts</c> and
/// <c>src/Defaults/index.ts</c>. Wire field numbers are repo-local v1 (see the
/// .proto header) — the committed fixtures pin them. No whatsmeow (MPL-2.0) bytes
/// were involved (NOTICE.md provenance rule).
/// </para>
/// <para>
/// This factory never logs (T-3-01: it handles identity material); messages name
/// rules and operations only. Inputs are validated fail-closed (V5) before any byte
/// is emitted; protobuf copies every input, so callers keep ownership of their arrays.
/// </para>
/// </remarks>
internal static class ClientPayloadFactory
{
    /// <summary>Repo-local v1 connect type for wifi-unknown (Baileys role WIFI_UNKNOWN).</summary>
    internal const uint ConnectTypeWifiUnknown = 1;

    /// <summary>Repo-local v1 connect reason for user-activated (Baileys role USER_ACTIVATED).</summary>
    internal const uint ConnectReasonUserActivated = 1;

    /// <summary>Repo-local v1 platform for android companions (Baileys role ANDROID).</summary>
    internal const uint PlatformAndroid = 1;

    /// <summary>Repo-local v1 platform for web/desktop companions (Baileys role WEB).</summary>
    internal const uint PlatformWeb = 2;

    /// <summary>Repo-local v1 release channel (Baileys role RELEASE).</summary>
    internal const uint ReleaseChannelRelease = 1;

    /// <summary>Repo-local v1 web sub-platform (Baileys role WEB_BROWSER).</summary>
    internal const uint WebSubPlatformWebBrowser = 1;

    /// <summary>Repo-local v1 device-props platform type for android phones (Baileys role ANDROID_PHONE).</summary>
    internal const uint DevicePropsAndroidPhone = 1;

    /// <summary>Repo-local v1 device-props platform type for desktop companions (Baileys role CHROME).</summary>
    internal const uint DevicePropsChrome = 2;

    /// <summary>Key-bundle type byte (Baileys KEY_BUNDLE_TYPE = [5], src/Defaults/index.ts).</summary>
    internal static readonly byte[] KeyBundleType = [5];

    /// <summary>Fixed companion descriptor version (Baileys companion object: 10.15.7).</summary>
    internal const uint CompanionVersionPrimary = 10;

    /// <summary>Fixed companion descriptor version (Baileys companion object: 10.15.7).</summary>
    internal const uint CompanionVersionSecondary = 15;

    /// <summary>Fixed companion descriptor version (Baileys companion object: 10.15.7).</summary>
    internal const uint CompanionVersionTertiary = 7;

    /// <summary>X25519 public key size for identity and signed-prekey values.</summary>
    internal const int PublicKeySize = 32;

    /// <summary>Ed25519 signature size for the signed-prekey signature.</summary>
    internal const int SignatureSize = 64;

    /// <summary>Signal registration id domain (14-bit, Baileys generateRegistrationId masks 16383).</summary>
    internal const int MaxRegistrationId = 16383;

    /// <summary>
    /// Builds the registration content: devicePairingData with the md5 build hash,
    /// encoded device props, big-endian registration id, key type, identity key, and
    /// signed-prekey triple — with passive=false, pull=false.
    /// </summary>
    /// <param name="identityPublic">Registration identity public key (32 bytes, the <c>eIdent</c> source).</param>
    /// <param name="signedPreKeyId">Signed-prekey id (encoded 3-byte big-endian).</param>
    /// <param name="signedPreKeyValue">Signed-prekey public value (32 bytes).</param>
    /// <param name="signedPreKeySignature">Signed-prekey signature (64 bytes).</param>
    /// <param name="registrationId">Signal registration id (0–16383, encoded 4-byte big-endian).</param>
    /// <param name="platform">Companion platform label (<c>android</c> selects the android shape).</param>
    /// <param name="appPrimary">App version primary.</param>
    /// <param name="appSecondary">App version secondary.</param>
    /// <param name="appTertiary">App version tertiary.</param>
    /// <returns>The serialized <c>ClientPayload</c> bytes.</returns>
    /// <exception cref="SdkFormatException">An input violates its shape rule (message names the rule only).</exception>
    internal static byte[] Registration(
        byte[] identityPublic,
        int signedPreKeyId,
        byte[] signedPreKeyValue,
        byte[] signedPreKeySignature,
        int registrationId,
        string platform,
        uint appPrimary,
        uint appSecondary,
        uint appTertiary)
    {
        ArgumentNullException.ThrowIfNull(identityPublic);
        ArgumentNullException.ThrowIfNull(signedPreKeyValue);
        ArgumentNullException.ThrowIfNull(signedPreKeySignature);
        ArgumentNullException.ThrowIfNull(platform);
        if (identityPublic.Length != PublicKeySize)
        {
            throw new SdkFormatException("Registration identity keys must be 32 bytes.");
        }

        if (signedPreKeyValue.Length != PublicKeySize)
        {
            throw new SdkFormatException("Registration signed-prekey values must be 32 bytes.");
        }

        if (signedPreKeySignature.Length != SignatureSize)
        {
            throw new SdkFormatException("Registration signed-prekey signatures must be 64 bytes.");
        }

        if ((uint)registrationId > MaxRegistrationId)
        {
            throw new SdkFormatException("Registration ids must fit the 14-bit registration domain.");
        }

        if (signedPreKeyId < 0 || signedPreKeyId > 0xFFFFFF)
        {
            throw new SdkFormatException("Registration signed-prekey ids must fit 3 big-endian bytes.");
        }

        if (platform.Length == 0)
        {
            throw new SdkFormatException("Registration platform labels must not be empty.");
        }

        byte[] buildHash = MD5.HashData(Encoding.UTF8.GetBytes($"{appPrimary}.{appSecondary}.{appTertiary}"));
        byte[] deviceProps = BuildDeviceProps(platform);

        var pairingData = new ClientPayload.Types.DevicePairingData
        {
            BuildHash = ByteString.CopyFrom(buildHash),
            DeviceProps = ByteString.CopyFrom(deviceProps),
            ERegid = ByteString.CopyFrom(EncodeBigEndian(registrationId, 4)),
            EKeytype = ByteString.CopyFrom(KeyBundleType),
            EIdent = ByteString.CopyFrom(identityPublic),
            ESkeyId = ByteString.CopyFrom(EncodeBigEndian(signedPreKeyId, 3)),
            ESkeyVal = ByteString.CopyFrom(signedPreKeyValue),
            ESkeySig = ByteString.CopyFrom(signedPreKeySignature),
        };
        CryptographicOperations.ZeroMemory(buildHash);

        var payload = new ClientPayload
        {
            ConnectType = ConnectTypeWifiUnknown,
            ConnectReason = ConnectReasonUserActivated,
            UserAgent = BuildUserAgent(platform, appPrimary, appSecondary, appTertiary),
            Passive = false,
            Pull = false,
            DevicePairingData = pairingData,
        };
        if (!IsAndroid(platform))
        {
            payload.WebInfo = new ClientPayload.Types.WebInfo { WebSubPlatform = WebSubPlatformWebBrowser };
        }

        return payload.ToByteArray();
    }

    /// <summary>
    /// Builds the login content: paired username + device with passive=true, pull=true —
    /// the resume path that never re-pairs while a credential row exists.
    /// </summary>
    /// <param name="username">Paired JID user (digits, encoded as a number).</param>
    /// <param name="device">Companion device index.</param>
    /// <param name="platform">Companion platform label (<c>android</c> selects the android shape).</param>
    /// <param name="appPrimary">App version primary.</param>
    /// <param name="appSecondary">App version secondary.</param>
    /// <param name="appTertiary">App version tertiary.</param>
    /// <returns>The serialized <c>ClientPayload</c> bytes.</returns>
    /// <exception cref="SdkFormatException">An input violates its shape rule (message names the rule only).</exception>
    internal static byte[] Login(
        string username,
        int device,
        string platform,
        uint appPrimary,
        uint appSecondary,
        uint appTertiary)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(platform);
        if (!ulong.TryParse(username, out ulong userNumber))
        {
            throw new SdkFormatException("Login usernames must be the paired JID user as digits.");
        }

        if (device < 0)
        {
            throw new SdkFormatException("Login device indexes must not be negative.");
        }

        if (platform.Length == 0)
        {
            throw new SdkFormatException("Login platform labels must not be empty.");
        }

        var payload = new ClientPayload
        {
            ConnectType = ConnectTypeWifiUnknown,
            ConnectReason = ConnectReasonUserActivated,
            UserAgent = BuildUserAgent(platform, appPrimary, appSecondary, appTertiary),
            Passive = true,
            Pull = true,
            Username = userNumber,
            Device = (uint)device,
            LidDbMigrated = false,
        };
        if (!IsAndroid(platform))
        {
            payload.WebInfo = new ClientPayload.Types.WebInfo { WebSubPlatform = WebSubPlatformWebBrowser };
        }

        return payload.ToByteArray();
    }

    private static ClientPayload.Types.UserAgent BuildUserAgent(
        string platform, uint appPrimary, uint appSecondary, uint appTertiary)
    {
        return new ClientPayload.Types.UserAgent
        {
            AppVersion = new ClientPayload.Types.AppVersion
            {
                Primary = appPrimary,
                Secondary = appSecondary,
                Tertiary = appTertiary,
            },
            Platform = IsAndroid(platform) ? PlatformAndroid : PlatformWeb,
            ReleaseChannel = ReleaseChannelRelease,
            OsVersion = "0.1",
            Device = "Desktop",
            OsBuildNumber = "0.1",
            LocaleLanguageIso6391 = "en",
            Mnc = "000",
            Mcc = "000",
            LocaleCountryIso31661Alpha2 = "US",
        };
    }

    private static byte[] BuildDeviceProps(string platform)
    {
        var props = new DeviceProps
        {
            Os = platform,
            PlatformType = IsAndroid(platform) ? DevicePropsAndroidPhone : DevicePropsChrome,
            RequireFullSync = true,
            HistorySyncConfig = new DeviceProps.Types.HistorySyncConfig
            {
                StorageQuotaMb = 10240,
                InlineInitialPayloadInE2EeMsg = true,
                SupportCallLogHistory = false,
                SupportBotUserAgentChatHistory = true,
                SupportCagReactionsAndPolls = true,
                SupportBizHostedMsg = true,
                SupportRecentSyncChunkMessageCountTuning = true,
                SupportHostedGroupMsg = true,
                SupportFbidBotChatHistory = true,
                SupportMessageAssociation = true,
                SupportGroupHistory = false,
            },
            Version = new DeviceProps.Types.AppVersion
            {
                Primary = CompanionVersionPrimary,
                Secondary = CompanionVersionSecondary,
                Tertiary = CompanionVersionTertiary,
            },
        };
        return props.ToByteArray();
    }

    private static bool IsAndroid(string platform) =>
        platform.Contains("android", StringComparison.OrdinalIgnoreCase);

    // Baileys encodeBigEndian(e, t): fixed-width big-endian (src/Utils/generics.ts).
    private static byte[] EncodeBigEndian(int value, int width)
    {
        byte[] encoded = new byte[width];
        int rest = value;
        for (int i = width - 1; i >= 0; i--)
        {
            encoded[i] = (byte)(rest & 0xFF);
            rest >>= 8;
        }

        return encoded;
    }
}
