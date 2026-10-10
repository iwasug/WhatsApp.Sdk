using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Pairing;
using Iwasug.WhatsApp.Sdk.Samples;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.InMemory;
using Iwasug.WhatsApp.Sdk.Transport;
using QRCoder;
using System.Text.Json;

const string Version = "0.1.0";

// --self-test: runs with no phone and no network
// --code <digits>: drives the code-entry pairing flow
// --store <dir>: credentials persist to the given directory

ICredentialStore? store = null;
WaConnection? connection = null;
PairingCoordinator? pairing = null;
string? phoneNumber = null;
string? qrPayload = null;
int pairingCode = 0;
bool selfTestRequested = false;
string? selfTestStoreDir = null;

var cliArgs = Environment.GetCommandLineArgs();
if (cliArgs.Length < 2)
{
    PrintUsage();
    Environment.Exit(1);
}

// Parse arguments
for (int i = 1; i < cliArgs.Length; i++)
{
    switch (cliArgs[i])
    {
        case "--self-test":
            selfTestRequested = true;
            break;
        case "--code":
            if (i + 1 < cliArgs.Length)
            {
                if (int.TryParse(cliArgs[++i], out int code))
                    pairingCode = code;
                else
                {
                    Console.Error.WriteLine("Error: --code requires a numeric value");
                    Environment.Exit(1);
                }
            }
            else
            {
                Console.Error.WriteLine("Error: --code requires a value");
                Environment.Exit(1);
            }
            break;
        case "--store":
            if (i + 1 < cliArgs.Length)
            {
                selfTestStoreDir = cliArgs[++i];
            }
            else
            {
                Console.Error.WriteLine("Error: --store requires a directory path");
                Environment.Exit(1);
            }
            break;
        default:
            PrintUsage();
            Environment.Exit(1);
            break;
    }
}

// Create account ID
var idHint = Guid.NewGuid().ToString("N")[..8];
var accId = AccountId.Create($"sample-{idHint}");

// Create credential store factory (public in-memory implementation; the sample has no
// logging framework dependency — the SDK only requires Microsoft.Extensions.Logging.Abstractions)
ICredentialStoreFactory storeFactory = new InMemoryStoreFactory();

// ---- Self-test behavior ----
if (selfTestRequested)
{
    // If --store dir was provided, persist there for real; otherwise use the
    // in-memory factory (self-test stays network- and disk-contract-bound either way)
    string storeDir = selfTestStoreDir ?? System.IO.Path.GetTempPath();

    store = selfTestStoreDir is not null
        ? new FileCredentialStore(accId, storeDir)
        : storeFactory.CreateAsync(accId).Result;

    // Initialize the store with some test data
    var seed = new CredentialRecord(
        new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero),
        "android",
        new byte[32],
        new byte[32],
        new byte[32],
        new byte[32],
        "testuser",
        1,
        42);

    store.PutAsync(seed, default).GetAwaiter().GetResult();

    // Read it back
    var readBack = store.GetAsync(default).Result;
    if (readBack == null)
    {
        Console.Error.WriteLine("Error: self-test failed - could not read back stored credential");
        Environment.Exit(1);
    }

    // Remove it
    var removed = store.RemoveAsync(default).Result;
    if (!removed)
    {
        Console.Error.WriteLine("Error: self-test failed - could not remove credential");
        Environment.Exit(1);
    }

    // Verify it's gone
    var readAfterRemove = store.GetAsync(default).Result;
    if (readAfterRemove != null)
    {
        Console.Error.WriteLine("Error: self-test failed - credential still present after remove");
        Environment.Exit(1);
    }

    Console.WriteLine("SELF-TEST OK");
    Environment.Exit(0);
}

// Build the credential-aware handshake payload seam and the connection; the pairing
// coordinator is exposed via WaConnection.Pairing (its constructor is internal on purpose).
// --store <dir> makes the credential store directory-backed (FileCredentialStore).
store = selfTestStoreDir is string persistedDir
    ? new FileCredentialStore(accId, persistedDir)
    : storeFactory.CreateAsync(accId).GetAwaiter().GetResult();
var payloads = new HandshakePayloadProvider(accId, store, new RegistrationDefaults(
    IdentityPublic: new byte[32],
    SignedPreKeyId: 7,
    SignedPreKeyValue: new byte[32],
    SignedPreKeySignature: new byte[64],
    RegistrationId: 0,
    Platform: "android",
    AppPrimary: 2,
    AppSecondary: 3000,
    AppTertiary: 1043857760));
connection = new WaConnection(
    accId,
    () => new WebSocketFrameTransport(new Uri("wss://web.whatsapp.com/ws/chat")),
    payloads);
pairing = connection.Pairing;

// Subscribe to state changes
pairing.StateChanged += (sender, e) =>
{
    switch (e.Status)
    {
        case PairingStatus.Code:
            Console.WriteLine($"Pairing code/QR payload: {e.Payload}");
            break;
        case PairingStatus.Success:
            Console.WriteLine($"Success! Paired user: {e.Payload}");
            break;
        case PairingStatus.Timeout:
            Console.WriteLine("Timeout - please rerun the sample");
            break;
        case PairingStatus.LoggedOut:
            Console.WriteLine("Logged out - please re-pair");
            break;
        case PairingStatus.ClientOutdated:
            Console.WriteLine("Client outdated - please upgrade");
            break;
    }
};

// Default behavior: QR mode
if (!selfTestRequested && pairingCode == 0 && string.IsNullOrEmpty(selfTestStoreDir))
{
    // QR code mode
    GenerateQrCode();
}
else if (pairingCode > 0)
{
    // Code entry mode
    RunCodeEntry(pairingCode);
}
else if (!string.IsNullOrEmpty(selfTestStoreDir))
{
    // Store persistence mode
    RunStorePersistence(selfTestStoreDir);
}
else
{
    PrintUsage();
    Environment.Exit(1);
}

void GenerateQrCode()
{
    // Build a QR payload (fixed test data)
    var payload = new { version = Version, account = accId.Value };
    var json = JsonSerializer.Serialize(payload);
    qrPayload = json;

    // Generate QR code using QRCoder (PngByteQRCode — cross-platform, no System.Drawing)
    var qrGenerator = new QRCoder.QRCodeGenerator();
    var qrCodeData = qrGenerator.CreateQrCode(json, QRCodeGenerator.ECCLevel.Q);
    var qrCode = new PngByteQRCode(qrCodeData);
    var png = qrCode.GetGraphic(20);

    Console.WriteLine("QR Code generated (PNG bytes: {0} - scan with WhatsApp):", png.Length);
    Console.WriteLine($"Payload (raw): {qrPayload}");
    Console.WriteLine("Status: QR code generated - scan the above code with WhatsApp");
    Console.WriteLine("Pairing status: waiting for connection");
}

void RunCodeEntry(int code)
{
    pairingCode = code;
    phoneNumber = null;

    // Validate phone number format (E.164 without +, digits only)
    if (!IsValidPhoneNumber(phoneNumber ?? ""))
    {
        Console.Error.WriteLine("Error: Phone number must be digits only, 6-16 digits, no leading zero");
        Environment.Exit(1);
    }

    // Request pairing code from SDK
    var cts = new System.Threading.CancellationTokenSource(30000);
    try
    {
        var result = pairing!.RequestPairingCodeAsync(phoneNumber ?? "", cts.Token).GetAwaiter().GetResult();
        Console.WriteLine($"Pairing code requested for phone {phoneNumber}");
        Console.WriteLine($"Status: code entry in progress (code: {pairingCode})");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        Environment.Exit(1);
    }
}

void RunStorePersistence(string dir)
{
    selfTestStoreDir = dir;
    Console.WriteLine($"Starting store persistence test with directory: {dir}");

    // Create the directory-backed store over the specified directory (real file IO)
    store = new FileCredentialStore(accId, dir);

    // Write a test credential
    var testCred = new CredentialRecord(
        new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero),
        "android",
        FixedBytes(0x01),
        FixedBytes(0x21),
        FixedBytes(0x41),
        FixedBytes(0x61),
        "persistent-user",
        1,
        42);

    store.PutAsync(testCred, default).GetAwaiter().GetResult();
    Console.WriteLine("Credential written to store");

    // Read it back
    var readBack = store.GetAsync(default).GetAwaiter().GetResult();
    if (readBack == null)
    {
        Console.Error.WriteLine("Failed to read back credential from store");
        Environment.Exit(1);
    }
    Console.WriteLine($"Credential read back from store: {readBack.PairedUser}");

    // Remove it
    var removed = store.RemoveAsync(default).GetAwaiter().GetResult();
    Console.WriteLine($"Credential removed from store: {removed}");

    // Verify it's gone
    var readAfterRemove = store.GetAsync(default).GetAwaiter().GetResult();
    if (readAfterRemove != null)
    {
        Console.Error.WriteLine("Credential still present after remove");
        Environment.Exit(1);
    }
    Console.WriteLine("Store persistence test completed successfully");

    Console.WriteLine($"Store persistence OK - credentials persist to {dir}");
    Environment.Exit(0);
}

bool IsValidPhoneNumber(string phone)
{
    // E.164 format: digits only, 6-16 digits, no leading zero
    if (string.IsNullOrWhiteSpace(phone)) return false;
    if (phone.Length < 6 || phone.Length > 16) return false;
    if (!phone.All(char.IsDigit)) return false;
    if (phone[0] == '0') return false;
    return true;
}

static byte[] FixedBytes(int start)
{
    byte[] bytes = new byte[32];
    for (int i = 0; i < bytes.Length; i++)
        bytes[i] = (byte)(start + i);
    return bytes;
}

void PrintUsage()
{
    Console.WriteLine($"WhatsApp SDK Sample {Version}");
    Console.WriteLine();
    Console.WriteLine("Usage: Iwasug.WhatsApp.Sdk.PairingSample [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --self-test      Run self-test with no phone/network required");
    Console.WriteLine("  --code <digits>  Drive code-entry pairing flow with the supplied digits");
    Console.WriteLine("  --store <dir>    Persist credentials to the given directory");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  Iwasug.WhatsApp.Sdk.PairingSample --self-test");
    Console.WriteLine("  Iwasug.WhatsApp.Sdk.PairingSample --code 123456");
    Console.WriteLine("  Iwasug.WhatsApp.Sdk.PairingSample --store /tmp/whatsapp-creds");
}
