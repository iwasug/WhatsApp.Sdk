using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Messaging;
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
// --send <jid> <text>: messaging mode — send one message (group JID for groups), then stream events
// --reply <jid> <quoted-id> <text>: messaging mode — send one quoted reply, then stream events
// --listen: messaging mode — stream typed events until Ctrl+C
// --account <id>: messaging mode — pick which stored account to log in as

ICredentialStore? store = null;
WaConnection? connection = null;
PairingCoordinator? pairing = null;
string? codeDigits = null;
bool selfTestRequested = false;
string? selfTestStoreDir = null;
string? sendJid = null;
string? sendText = null;
string? replyJid = null;
string? quotedMessageId = null;
string? replyText = null;
bool listenRequested = false;
string? accountIdArg = null;

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
        case "--send":
            if (i + 2 < cliArgs.Length)
            {
                sendJid = cliArgs[++i];
                sendText = cliArgs[++i];
            }
            else
            {
                Console.Error.WriteLine("Error: --send requires <jid> <text>");
                Environment.Exit(1);
            }
            break;
        case "--reply":
            if (i + 3 < cliArgs.Length)
            {
                replyJid = cliArgs[++i];
                quotedMessageId = cliArgs[++i];
                replyText = cliArgs[++i];
            }
            else
            {
                Console.Error.WriteLine("Error: --reply requires <jid> <quoted-message-id> <text>");
                Environment.Exit(1);
            }
            break;
        case "--listen":
            listenRequested = true;
            break;
        case "--account":
            if (i + 1 < cliArgs.Length)
            {
                accountIdArg = cliArgs[++i];
            }
            else
            {
                Console.Error.WriteLine("Error: --account requires an account id");
                Environment.Exit(1);
            }
            break;
        default:
            PrintUsage();
            Environment.Exit(1);
            break;
    }
}

// Messaging-mode validation: the messaging flags need paired credentials in the
// store (a fresh store would attempt registration, not login) and are mutually
// exclusive with the pairing modes.
bool messagingRequested = sendJid is not null || replyJid is not null || listenRequested;
if (messagingRequested && (selfTestRequested || codeDigits is not null))
{
    Console.Error.WriteLine("Error: --send/--reply/--listen cannot be combined with --self-test or --code");
    Environment.Exit(1);
}
if (messagingRequested && selfTestStoreDir is null)
{
    Console.Error.WriteLine("Error: messaging mode requires --store <dir> with paired credentials");
    Console.Error.WriteLine("       pair first: Iwasug.WhatsApp.Sdk.PairingSample --code <digits> --store <dir>");
    Environment.Exit(1);
}
if (accountIdArg is not null && !messagingRequested)
{
    Console.Error.WriteLine("Error: --account is only used with --send/--reply/--listen");
    Environment.Exit(1);
}

// Create account ID. Pairing modes mint a fresh account per run (one linked
// device per file); messaging mode REUSES the account already stored in
// --store <dir> — credentials are one file per account id, so a fresh id would
// never find the paired credentials. --account picks among several.
var accId = AccountId.Create(messagingRequested
    ? ResolveStoredAccountId(selfTestStoreDir!, accountIdArg)
    : $"sample-{Guid.NewGuid().ToString("N")[..8]}");

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

// Four live modes, all on the public API only: --code requests a pairing
// code for the phone digits, --store <dir> persists to disk first, the
// messaging flags (--send/--reply/--listen) log in from a previously paired
// store and exercise send/receive, and the default runs QR pairing. Both
// pairing modes connect first (the coordinator waits for the channel-ready
// pulse before it can send anything) and exit only after the attempt's
// terminal event.
if (messagingRequested)
{
    await RunMessagingAsync(sendJid, sendText, replyJid, quotedMessageId, replyText);
}
else if (codeDigits is not null)
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

async Task RunMessagingAsync(
    string? targetJid,
    string? text,
    string? replyToJid,
    string? quotedId,
    string? replyBody)
{
    Console.WriteLine("Messaging mode: logging in from the stored credentials...");

    // Refuse a fresh store BEFORE connecting: without paired credentials the
    // handshake would attempt registration instead of login.
    var existing = await store!.GetAsync(default);
    if (existing is null)
    {
        Console.Error.WriteLine($"Error: no paired credentials in {selfTestStoreDir}");
        Console.Error.WriteLine("       pair first: Iwasug.WhatsApp.Sdk.PairingSample --code <digits> --store <dir>");
        Environment.Exit(1);
        return;
    }

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (s, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    try
    {
        await connection!.ConnectAsync(cts.Token);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Cancelled before open.");
        Environment.Exit(0);
        return;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: connection failed: {ex.Message}");
        Environment.Exit(1);
        return;
    }

    var messaging = connection.Messaging;

    // Optional send/reply legs (public API only). A group JID (@g.us) takes the
    // sender-key path; anything else is the 1:1 path. The awaited SendOutcome is
    // the idempotency contract's single terminal result — never a silent drop.
    if (targetJid is not null)
    {
        var jid = Jid.Parse(targetJid);
        try
        {
            var outcome = jid.Server == "g.us"
                ? await messaging.SendGroupMessageAsync(jid, text!, cts.Token)
                : await messaging.SendMessageAsync(jid, text!, cts.Token);
            Console.WriteLine(
                $"Send outcome: {outcome.Status} id={outcome.MessageId} " +
                $"server_id={outcome.ServerId ?? "-"} t={outcome.T ?? "-"} error={outcome.Error?.ToString() ?? "-"}");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Send cancelled (never touched the wire).");
            Environment.Exit(0);
            return;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: send failed: {ex.Message}");
            Environment.Exit(1);
            return;
        }
    }

    if (replyToJid is not null)
    {
        try
        {
            var outcome = await messaging.ReplyAsync(Jid.Parse(replyToJid), quotedId!, replyBody!, cts.Token);
            Console.WriteLine(
                $"Reply outcome: {outcome.Status} id={outcome.MessageId} " +
                $"server_id={outcome.ServerId ?? "-"} t={outcome.T ?? "-"} error={outcome.Error?.ToString() ?? "-"}");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Reply cancelled (never touched the wire).");
            Environment.Exit(0);
            return;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: reply failed: {ex.Message}");
            Environment.Exit(1);
            return;
        }
    }

    // Stream typed events until Ctrl+C. After a send this keeps the receipt
    // round trip observable (delivery/read for the sent message id); --listen
    // alone is the pure receive side. The channel is bounded (backpressure, no
    // silent drops) — this loop drains promptly, which is the contract.
    Console.WriteLine("Streaming events (Ctrl+C to exit)...");
    try
    {
        await foreach (var ev in messaging.ReadEventsAsync(cts.Token))
        {
            PrintEvent(ev);
        }
    }
    catch (OperationCanceledException)
    {
        // Ctrl+C: the stream closed via cancellation — the documented shutdown path.
    }

    Console.WriteLine("Done.");
    Environment.Exit(0);
}

void PrintEvent(WhatsAppEvent ev)
{
    // The sample console is the HOST display surface (UAT SC 1-5 needs the
    // received text visible to compare against the phone) — not an SDK log
    // site. SDK log sites never emit plaintext, ciphertext, or key bytes
    // (T-4-15); this loop prints ids, statuses, and message text only.
    switch (ev)
    {
        case MessageReceivedEvent m:
            Console.WriteLine($"[message] chat={m.Chat} from={m.Sender} id={m.Key.Id} fromMe={m.IsFromMe} quoted={m.QuotedMessageId ?? "-"}");
            if (m.Text is not null)
            {
                Console.WriteLine($"          text: {m.Text}");
            }
            break;
        case ReceiptReceivedEvent r:
            Console.WriteLine($"[receipt] {r.Kind} chat={r.Chat} ids={string.Join(",", r.MessageIds)} t={r.Timestamp:O}");
            break;
        case MessageAckedEvent a:
            Console.WriteLine($"[acked] {a.Status} id={a.MessageId} server_id={a.ServerId ?? "-"} t={a.T ?? "-"}");
            break;
        case DecryptionFailedEvent f:
            Console.WriteLine($"[decrypt-failed] chat={f.Chat} sender={f.Sender} id={f.MessageId} attempt={f.RetryCount}");
            break;
        default:
            Console.WriteLine($"[{ev.GetType().Name}] t={ev.Timestamp:O}");
            break;
    }
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

static string ResolveStoredAccountId(string storeDir, string? requested)
{
    // Credentials are one JSON file per account id inside the store dir
    // (FileCredentialStore). Resolve which one messaging mode should log in
    // as — or exit with the exact remediation.
    if (requested is not null)
    {
        if (!File.Exists(Path.Combine(storeDir, $"{requested}.json")))
        {
            Console.Error.WriteLine($"Error: account '{requested}' not found in {storeDir}");
            Environment.Exit(1);
            return requested;
        }
        return requested;
    }

    var files = Directory.Exists(storeDir)
        ? Directory.GetFiles(storeDir).Where(f =>
            f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && !f.Contains(".tmp.", StringComparison.OrdinalIgnoreCase)).ToArray()
        : Array.Empty<string>();
    if (files.Length == 0)
    {
        Console.Error.WriteLine($"Error: no paired credentials in {storeDir}");
        Console.Error.WriteLine("       pair first: Iwasug.WhatsApp.Sdk.PairingSample --code <digits> --store <dir>");
        Environment.Exit(1);
        return string.Empty;
    }
    if (files.Length > 1)
    {
        var ids = string.Join(", ", files.Select(Path.GetFileNameWithoutExtension));
        Console.Error.WriteLine($"Error: multiple accounts in {storeDir}: {ids}");
        Console.Error.WriteLine("       pick one with --account <id>");
        Environment.Exit(1);
        return string.Empty;
    }
    return Path.GetFileNameWithoutExtension(files[0])!;
}

void PrintUsage()
{
    Console.WriteLine($"WhatsApp SDK Sample {Version}");
    Console.WriteLine();
    Console.WriteLine("Usage: Iwasug.WhatsApp.Sdk.PairingSample [options]");
    Console.WriteLine();
    Console.WriteLine("Pairing options:");
    Console.WriteLine("  --self-test      Run self-test with no phone/network required");
    Console.WriteLine("  --code <digits>  Phone number (digits, E.164 without +) - request a pairing code");
    Console.WriteLine("  --store <dir>    Persist credentials to the given directory");
    Console.WriteLine();
    Console.WriteLine("Messaging options (require --store <dir> with paired credentials;");
    Console.WriteLine("stream events until Ctrl+C, so receipts for sent messages stay observable):");
    Console.WriteLine("  --send <jid> <text>            Send one message (…@g.us JID = group/sender-key path)");
    Console.WriteLine("  --reply <jid> <quoted-id> <text>  Send one quoted reply");
    Console.WriteLine("  --listen                       Stream typed events only (receive side)");
    Console.WriteLine("  --account <id>                 Pick the stored account when the dir holds several");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  Iwasug.WhatsApp.Sdk.PairingSample --self-test");
    Console.WriteLine("  Iwasug.WhatsApp.Sdk.PairingSample --code 15551234567");
    Console.WriteLine("  Iwasug.WhatsApp.Sdk.PairingSample --store /tmp/whatsapp-creds");
    Console.WriteLine("  Iwasug.WhatsApp.Sdk.PairingSample --code 15551234567 --store /tmp/whatsapp-creds");
    Console.WriteLine("  Iwasug.WhatsApp.Sdk.PairingSample --store /tmp/whatsapp-creds --send 15551234567@s.whatsapp.net hello");
    Console.WriteLine("  Iwasug.WhatsApp.Sdk.PairingSample --store /tmp/whatsapp-creds --send 123-456789@g.us hello-group");
    Console.WriteLine("  Iwasug.WhatsApp.Sdk.PairingSample --store /tmp/whatsapp-creds --listen");
}
