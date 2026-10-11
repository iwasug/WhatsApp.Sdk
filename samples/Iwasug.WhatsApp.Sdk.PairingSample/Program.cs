using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Pairing;
using Iwasug.WhatsApp.Sdk.Samples;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.InMemory;
using Iwasug.WhatsApp.Sdk.Transport;
using QRCoder;

const string Version = "0.1.0";

// --self-test: runs with no phone and no network
// --code <digits>: phone number (E.164 without +) for the code-entry pairing flow
// --store <dir>: credentials persist to the given directory

ICredentialStore? store = null;
WaConnection? connection = null;
PairingCoordinator? pairing = null;
string? codeDigits = null;
bool selfTestRequested = false;
string? selfTestStoreDir = null;

// Set by the StateChanged handler on the attempt's terminal event
// (Success/Timeout/LoggedOut/ClientOutdated). The code mode returns from
// RequestPairingCodeAsync before its companion tail ends, so the sample
// awaits this before exiting — it never dies mid-attempt.
var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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
            // The digits are the phone number (E.164 without '+'). The SDK
            // owns validation — this sample keeps no second validator that
            // could disagree with the typed PairingCodeRequestException.
            if (i + 1 < cliArgs.Length && cliArgs[i + 1].Length > 0)
            {
                codeDigits = cliArgs[++i];
            }
            else
            {
                Console.Error.WriteLine("Error: --code requires the phone number's digits");
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
    store = selfTestStoreDir is not null
        ? new FileCredentialStore(accId, selfTestStoreDir)
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

// Subscribe to state changes: print every status, render the QR for real
// Code payloads (QR mode), and signal the terminal event exactly once.
pairing.StateChanged += (sender, e) =>
{
    switch (e.Status)
    {
        case PairingStatus.Code:
            Console.WriteLine($"Pairing code/QR payload: {e.Payload}");
            if (codeDigits is null && e.Payload is not null)
            {
                // QR mode: the payload is the coordinator's real server-issued
                // ref — never the fabricated placeholder this sample printed
                // before the pairing coordinator was wired up.
                PrintQr(e.Payload);
            }
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

    if (e.Status is PairingStatus.Success or PairingStatus.Timeout
        or PairingStatus.LoggedOut or PairingStatus.ClientOutdated)
    {
        terminal.TrySetResult();
    }
};

// Three live modes, all on the public API only: --code requests a pairing
// code for the phone digits, --store <dir> persists to disk first, and the
// default runs QR pairing. Both pairing modes connect first (the coordinator
// waits for the channel-ready pulse before it can send anything) and exit
// only after the attempt's terminal event.
if (codeDigits is not null)
{
    await RunCodeEntryAsync(codeDigits);
}
else if (!string.IsNullOrEmpty(selfTestStoreDir))
{
    RunStorePersistence(selfTestStoreDir);
}
else
{
    await RunQrModeAsync();
}

async Task RunQrModeAsync()
{
    Console.WriteLine("QR mode: connecting to WhatsApp...");
    try
    {
        // Pairing was already accessed above, so the coordinator exists
        // before the connection opens and no channel-ready pulse is missed.
        await connection!.ConnectAsync();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: connection failed: {ex.Message}");
        Environment.Exit(1);
        return;
    }

    Console.WriteLine("Scan the QR code below with WhatsApp (Linked devices > Link a device).");
    try
    {
        // Runs the QR attempt to its terminal event: every Code event above
        // prints a fresh payload (refs rotate on the server's schedule), and
        // StartAsync returns only once the attempt ends.
        await pairing!.StartAsync();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: pairing failed: {ex.Message}");
        Environment.Exit(1);
        return;
    }

    await terminal.Task;
    Console.WriteLine("Pairing attempt finished (see status above).");
}

async Task RunCodeEntryAsync(string digits)
{
    Console.WriteLine("Code mode: connecting to WhatsApp...");
    try
    {
        await connection!.ConnectAsync();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: connection failed: {ex.Message}");
        Environment.Exit(1);
        return;
    }

    string display;
    try
    {
        // One validator only: the SDK's own. A bad number surfaces as a
        // typed PairingCodeRequestException naming the violated rule.
        display = await pairing!.RequestPairingCodeAsync(digits);
    }
    catch (PairingCodeRequestException ex)
    {
        Console.Error.WriteLine($"Error: invalid phone number ({ex.Reason}): {ex.Message}");
        Environment.Exit(1);
        return;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        Environment.Exit(1);
        return;
    }

    Console.WriteLine($"Pairing code for {digits}: {display}");
    Console.WriteLine("Enter this code in WhatsApp on the phone; waiting for confirmation...");
    await terminal.Task;
}

void PrintQr(string payload)
{
    try
    {
        var qrGenerator = new QRCodeGenerator();
        using QRCodeData qrCodeData = qrGenerator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        byte[] png = new PngByteQRCode(qrCodeData).GetGraphic(8);
        Console.WriteLine($"QR code (PNG, {png.Length} bytes) - scan with WhatsApp, Linked devices");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"(QR rendering unavailable: {ex.Message}) Use the payload above as text.");
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
    Console.WriteLine("  --code <digits>  Phone number (digits, E.164 without +) - request a pairing code");
    Console.WriteLine("  --store <dir>    Persist credentials to the given directory");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  Iwasug.WhatsApp.Sdk.PairingSample --self-test");
    Console.WriteLine("  Iwasug.WhatsApp.Sdk.PairingSample --code 15551234567");
    Console.WriteLine("  Iwasug.WhatsApp.Sdk.PairingSample --store /tmp/whatsapp-creds");
}
