# Phase 06: Media Pipeline - Pattern Map

**Mapped:** 2026-10-14
**Files analyzed:** 18 (10 new source, 6 modified source/schema, 2 shared test/provenance surfaces + 7 test files referenced)
**Analogs found:** 17 / 18

All analog paths below are git-TRACKED source (verified `git ls-files` non-empty for the primary seam files; no `.gsd/capabilities/` mirror paths appear in this document).

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/Iwasug.WhatsApp.Sdk/Media/MediaCrypto.cs` (new) | service (crypto envelope) | transform (in-memory + streaming) | `src/Iwasug.WhatsApp.Sdk/Signal/SignalCipher.cs` | role-match |
| `src/Iwasug.WhatsApp.Sdk/Media/MediaConn.cs` (new) | service (conn cache) | request-response (IQ) | `src/Iwasug.WhatsApp.Sdk/Messaging/DeviceDirectory.cs` | exact |
| `src/Iwasug.WhatsApp.Sdk/Media/MediaUploader.cs` (new) | service (HTTP upload) | request-response (HTTP POST) | `src/Iwasug.WhatsApp.Sdk/Connection/WaVersionResolver.cs` (`HttpSwJsFetcher`) + `src/Iwasug.WhatsApp.Sdk/Messaging/DeviceDirectory.cs` (router seam) | role-match |
| `src/Iwasug.WhatsApp.Sdk/Media/MediaDownloader.cs` (new) | service (HTTP download + verify) | request-response + streaming | `src/Iwasug.WhatsApp.Sdk/Connection/WaVersionResolver.cs` + `src/Iwasug.WhatsApp.Sdk/Signal/SignalCipher.cs` (verify order) | role-match |
| `src/Iwasug.WhatsApp.Sdk/Media/MediaRetryTracker.cs` (new) | utility (bounded counter) | event-driven + stateful | `src/Iwasug.WhatsApp.Sdk/Messaging/ReceiptHandler.cs` (retry counters) | exact |
| `src/Iwasug.WhatsApp.Sdk/Media/MediaKind.cs` + DTOs (`MediaSendRequest`/`MediaMetadata`, options) (new) | model / DTO | transform | `src/Iwasug.WhatsApp.Sdk/Messaging/MessageBuilder.cs` (`QuotedMessageRef`/`DeviceCiphertext` records) | role-match |
| `src/Iwasug.WhatsApp.Sdk/Media/MediaEvents.cs` + `MessagingEvents.cs` extension (new/modified) | events/vocab | event-driven + streaming | `src/Iwasug.WhatsApp.Sdk/Messaging/MessagingEvents.cs` + `src/Iwasug.WhatsApp.Sdk/Messaging/SafetyEvents.cs` | exact |
| `src/Iwasug.WhatsApp.Sdk/Proto/message.proto` (modified: image=3/document=7/audio=8 + retry messages) | config/schema | — | self (`Proto/message.proto:1-78`) + `src/Iwasug.WhatsApp.Sdk/Iwasug.WhatsApp.Sdk.csproj:62-70` | exact |
| `src/Iwasug.WhatsApp.Sdk/Messaging/MessageBuilder.cs` (modified: media plaintext builders + mediatype attrs) | utility/builder | transform (proto → bytes) | self (`MessageBuilder.cs:58-206`) | exact |
| `src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs` (modified: `type="media"` node + group skmsg mediatype) | service | request-response (correlated send) | self (`MessageSender.cs:96-220, 273-417, 780-810`) | exact |
| `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs` (modified: `SendImageAsync`/`SendDocumentAsync`/`SendVoiceNoteAsync`) | controller/facade | request-response | self (`Messenger.cs:184-193, 212-221, 247-286`) | exact |
| `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs` (modified: media branch + `<rmr>` routing + ack-before-download) | dispatcher | event-driven | self (`MessageDispatcher.cs:90-263, 363-450, 557-614`) | exact |
| `src/Iwasug.WhatsApp.Sdk/Messaging/ReceiptHandler.cs` (modified or new `MediaRetryReceipts` helper: server-error + `<rmr>`) | service | event-driven + request-response | self (`ReceiptHandler.cs:136-242, 282-317`) | exact |
| `src/Iwasug.WhatsApp.Sdk/Safety/SafetyOptions.cs` (modified: `MaxImageBytes`/`MaxDocumentBytes`/`MaxAudioBytes`/`MaxThumbnailBytes`) | config/options | transform/validation | self (`SafetyOptions.cs:65-148`) | exact |
| `src/Iwasug.WhatsApp.Sdk/Safety/HumanPacing.cs` (reused, no edit: `recording` vs `composing` pause) | utility/seam | request-response | self (`HumanPacing.cs:150-182`) | exact |
| `tests/.../Media/MediaCryptoFixtureTests.cs` + `TestData/media-crypto-fixtures.json` (new) | test + fixture | transform | `tests/.../Noise/NoiseVectorTests.cs` + `tests/.../Messaging/MessageWireFixtureTests.cs` | exact |
| `tests/.../Media/Media{Upload,Send,Download,Retry,Safety}Tests.cs` (new) + `SendPathAuditTests.cs` extension | test | request-response + loopback | `tests/.../Messaging/MessagingLoopbackTests.cs` + `tests/.../Messaging/RecentCachePolicyTests.cs` + `tests/.../Safety/SendPathAuditTests.cs` | exact |
| `PublicAPI.Unshipped.txt` + `NOTICE.md` (modified) | config/doc | — | existing entries + `Proto/message.proto:1-22` header | exact |

## Pattern Assignments

### `Media/MediaCrypto.cs` (service/crypto, transform + streaming)

**Analog:** `src/Iwasug.WhatsApp.Sdk/Signal/SignalCipher.cs`

**Imports pattern** (`SignalCipher.cs:1-10`, `X3dh.cs:1-15` — BCL-only, no NSec for symmetric crypto):
```csharp
using System.Security.Cryptography;
using System.Text;
using Iwasug.WhatsApp.Sdk.Errors;
```

**HKDF expansion pattern** (`SignalCipher.cs:167-194`) — copy the derive → split → zeroize-okm shape exactly; change only the constants (112 bytes, zero salt, per-type info):
```csharp
byte[] okm = HKDF.DeriveKey(
    HashAlgorithmName.SHA256,
    chainMessageKey.ToArray(),
    MessageKeyMaterialLength,          // MediaCrypto: 112 (not 80)
    new byte[ChainMessageKeyLength],   // MediaCrypto: empty/zero salt per D-60
    Encoding.ASCII.GetBytes(MessageKeyInfoLabel));  // MediaCrypto: "WhatsApp Image Keys" | "WhatsApp Audio Keys" | "WhatsApp Document Keys"
try
{
    byte[] aes = new byte[AesKeyLength];   // 32
    byte[] hmac = new byte[HmacKeyLength]; // 32
    byte[] iv = new byte[IvLength];        // 16 (refKey[80:112] discarded, never stored)
    okm.AsSpan(0, AesKeyLength).CopyTo(aes);
    // ...
    return (aes, hmac, iv);
}
finally
{
    CryptographicOperations.ZeroMemory(okm);
}
```

**AES-CBC helper pattern** (`SignalCipher.cs:654-674`) — copy verbatim; media uses PKCS7 (not `PaddingMode.None` — the Signal path pads manually to 160; media relies on BCL PKCS7 per 06-RESEARCH D-61, pinned by round-trip fixture):
```csharp
private static byte[] AesCbcEncrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> plaintext)
{
    using var aes = Aes.Create();
    aes.Key = key.ToArray();
    aes.IV = iv.ToArray();
    aes.Mode = CipherMode.CBC;
    aes.Padding = PaddingMode.None;  // MediaCrypto: PaddingMode.PKCS7 (fixture-pinned)
    using ICryptoTransform encryptor = aes.CreateEncryptor();
    return encryptor.TransformFinalBlock(plaintext.ToArray(), 0, plaintext.Length);
}
```

**HMAC-truncate + zeroize pattern** (`SignalCipher.cs:624-652`) — copy the build-input → `HMACSHA256.HashData` → truncate → zeroize-both shape; media truncates to **10** bytes (not 8):
```csharp
byte[] full = HMACSHA256.HashData(hmacKey.ToArray(), input);  // MediaCrypto: input = iv || ciphertext
try
{
    return full.AsSpan(0, MacLength).ToArray();  // MediaCrypto: MacLength = 10
}
finally
{
    CryptographicOperations.ZeroMemory(full);
}
```

**Constant-time verify pattern** (`SignalCipher.cs:410-422`) — copy for the MAC check; length-gate first, `FixedTimeEquals`, zeroize computed MAC:
```csharp
byte[] computedMac = ComputeWhisperMac(hmacKey, version, body, senderIdentityDjb, receiverIdentityDjb);
try
{
    if (receivedMac.Length != MacLength
        || !CryptographicOperations.FixedTimeEquals(computedMac, receivedMac))
    {
        return new SignalDecryptOutcome.Failed(SignalDecryptFailure.BadMac);
    }
}
finally
{
    CryptographicOperations.ZeroMemory(computedMac);
}
```

**Length-check throw pattern** (`SignalCipher.cs:676-682`, `X3dh.cs:583-589`) — fail-closed, rule-only, never embeds bytes:
```csharp
private static void ThrowUnlessKey(ReadOnlySpan<byte> key, int expected)
{
    if (key.Length != expected)
    {
        throw new SdkFormatException("Signal key material has an unexpected length.");
    }
}
// MediaCrypto equivalents: "Media keys must be 32 bytes.", "Media hashes must be 32 bytes.",
// "Media blobs must be longer than the 10-byte MAC."
```

**GCM pattern for media-retry (D-62):** no in-repo GCM caller exists outside Noise (`AesGcm` in `NoiseHandshake` per 04-PATTERNS). Use the in-box `AesGcm` BCL API directly (`Encrypt(nonce, plaintext, ciphertext, tag, aad)`), 12-byte nonce, AAD = message-id UTF-8 bytes, `HKDF.DeriveKey(SHA256, mediaKey, 32, salt: [], info: "WhatsApp Media Retry Notification")`. Pin with the `media-retry-fixtures.json` loopback before live use.

**Copy these conventions:** `internal static class` with `internal const` lengths/labels at top (cf. `SignalCipher.cs:115-158`); every temporary key buffer zeroized (`CryptographicOperations.ZeroMemory` — see `X3dh.cs:494-522` ikm/okm nesting); streaming encrypt accumulates `SHA256(plaintext)` + `SHA256(ciphertext||mac)` incrementally (research D-61 order); `ZeroMemory(mediaKey/cipherKey/macKey)` after use; **no logging on this path** (class remarks at `SignalCipher.cs:104-111`).

---

### `Media/MediaConn.cs` (service/cache, request-response IQ)

**Analog:** `src/Iwasug.WhatsApp.Sdk/Messaging/DeviceDirectory.cs`

**Class skeleton + TTL cache pattern** (`DeviceDirectory.cs:37-57, 70-104`):
```csharp
internal sealed class DeviceDirectory
{
    internal static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);
    internal const string UsyncServer = "s.whatsapp.net";
    private readonly Func<QueryRouter?> _routerProvider;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _ttl;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (IReadOnlyList<Jid> Devices, DateTimeOffset CachedAt)> _cache = new(StringComparer.Ordinal);
    // MediaConn: cache {auth, hosts[], fetchedAt} per account; key = account-scoped singleton (no JID key);
    // refresh when now > fetchedAt + ttl (ttl from <media_conn ttl/auth_ttl> attrs, SDK-owned default when absent).
}
```

**Query seam pattern** (`DeviceDirectory.cs:126-146`) — copy the router-absent → `SdkConnectionException`, `SendQueryAsync` → `BinaryNodeCodec.Decode` with `SdkFormatException` wrap:
```csharp
QueryRouter? router = _routerProvider()
    ?? throw new SdkConnectionException("The connection is not open: the device query was not sent.");
byte[] reply = await router.SendQueryAsync(
    BinaryNodeCodec.Encode(BuildUsyncNode(peer)),
    cancellationToken).ConfigureAwait(false);
BinaryNode response;
try
{
    response = BinaryNodeCodec.Decode(reply);
}
catch (SdkFormatException ex)
{
    throw new SdkFormatException("Device-list reply failed node validation.", ex);
}
```

**Node-build seam pattern** (`DeviceDirectory.cs:148-182`, `GroupDirectory.cs:131-147`) — media-conn request shape per 06-RESEARCH D-63:
```csharp
// Analog (DeviceDirectory.BuildUsyncNode): BinaryNode.FromChildren("iq", [...], {xmlns,type,to})
// MediaConn: <iq xmlns="w:m" type="set" to="s.whatsapp.net"><media_conn/></iq>
internal static BinaryNode BuildMediaConnNode() =>
    BinaryNode.FromChildren("iq",
        [BinaryNode.Create("media_conn", null)],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["xmlns"] = "w:m",
            ["type"] = "set",
            ["to"] = DeviceDirectory.UsyncServer,
        });
```

**Parse discipline** (`DeviceDirectory.cs:184-234`, `GroupDirectory.cs:154-189`) — bounds-checked, whole-resolution failure, rule-only messages, never partial lists. MediaConn parses `<media_conn auth ttl auth_ttl max_buckets>` + `<host hostname>` children; missing/empty host list → `SdkFormatException("Media-conn reply carries no hosts.")`.

**Copy these conventions:** `Func<QueryRouter?>` ctor injection (`Messenger.RouterProvider => () => _connection.CurrentRouter`, `Messenger.cs:617`; `WaConnection.CurrentRouter => _channel?.Router`, `WaConnection.cs:367`); `TimeProvider` injected, null → `TimeProvider.System`; `SemaphoreSlim(1,1)` gate serializing refresh; `Invalidate` method (cf. `DeviceDirectory.cs:100-104`); nothing logs; force-refresh overload for upload-failure host rotation (D-64).

---

### `Media/MediaUploader.cs` (service, HTTP POST to mmg)

**Analog:** `src/Iwasug.WhatsApp.Sdk/Connection/WaVersionResolver.cs` (`HttpSwJsFetcher`, lines 63-93) for HttpClient discipline + `DeviceDirectory` for conn resolution

**HttpClient ownership pattern** (`WaVersionResolver.cs:78-92`) — copy the caller-owned/null-guarded shape; MediaUploader owns a per-account `SocketsHttpHandler`-backed `HttpClient` (header isolation, Phase-7-safe — do NOT share a static):
```csharp
internal HttpSwJsFetcher(HttpClient httpClient, string requestUri = WaVersionResolver.SwJsUri)
{
    ArgumentNullException.ThrowIfNull(httpClient);
    if (!Uri.TryCreate(requestUri, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
    {
        throw new ArgumentException("Version script addresses must use HTTPS.", nameof(requestUri));
    }
    _httpClient = httpClient;
    _requestUri = requestUri;
}
// MediaUploader: ctor takes (HttpClient httpClient, MediaConn conn, TimeProvider, SafetyOptions caps, ResiliencePipeline?);
// upload path is https://{host}/mms/{mmsType}/{token}?auth={urlenc(auth)}&token={token}
// mmsType: image→"image" | audio/ptt→"audio" | document→"document"; token = base64url-nopad(fileEncSha256).
```

**Headers + timeout policy (06-RESEARCH D-64):** `Content-Type: application/octet-stream`, `Origin: https://web.whatsapp.com` (+ `Referer`), exact `Content-Length`; 30 s upload timeout (Baileys `UPLOAD_TIMEOUT` adopted as SDK-owned tunable); `CancellationToken` end-to-end; optional `IProgress<long>`; `Polly.Core` 8.8.0 `ResiliencePipeline` for transient HTTP retries only (never WS reconnect — STACK.md constraint). Host rotation: first conn host, on transport failure force-refresh conn and try next host.

**Upload-inside-gate ordering (D-64/D-68):** upload happens INSIDE `ISafetyGate.ExecuteSendAsync` after admission, before the inner E2EE send — refused sends never touch mmg. The uploader itself performs NO gating; `Messenger` composes gate → upload → `MessageSender`. Byte-cap check (`MaxImageBytes`/`MaxDocumentBytes`/`MaxAudioBytes`/`MaxThumbnailBytes`) runs BEFORE encryption/upload and throws rule-only `SafetyRuleViolationException`.

**Thumbnail discipline (D-64):** host supplies `jpegThumbnail` bytes; SDK validates `≤ 64 KiB` and passes through; no image-processing dependency (no SkiaSharp/ImageSharp). XML docs carry the 32-px JPEG recipe pointer.

---

### `Media/MediaDownloader.cs` (service, HTTP GET + verify-then-decrypt streaming)

**Analog:** `WaVersionResolver.HttpSwJsFetcher` (HTTP discipline) + `SignalCipher.DecryptParsedMessage` (verify order) + `MessageDispatcher` (ordering)

**URL + validation pattern** (06-RESEARCH D-65):
```text
require directPath starts with "/" else SdkFormatException (fail-closed)
url := https://{host}{directPath}&hash={base64url(fileEncSha256)}&mms-type={image|audio|document}&__wa-mms=
host := first media-conn host (refresh non-force); GET with Origin/Referer (+User-Agent browser string); honor Retry-After; ≤5 attempts across hosts for NETWORK errors only
```

**Verify-then-decrypt order (D-62) — copy the three-gate sequence, fail-closed, nothing delivered until all pass:**
```text
require len(blob) > 10, fileEncSha256/fileSha256 present and 32 bytes
require SHA256(blob) == fileEncSha256 → else typed MacMismatch/hash error (no decrypt attempt)
split blob → ciphertext, mac = blob[-10:]; require HMAC-SHA256(macKey, iv || ciphertext)[0:10] == mac (FixedTimeEquals) → else fail
plaintext := AES-256-CBC-Decrypt(cipherKey, derived iv, ciphertext) (PKCS7; padding error = fail)
require SHA256(plaintext) == fileSha256 → else fail
```
Terminal statuses → typed outcomes, NO host retry: 403/404/410 = EXPIRED → media-retry flow (not host error); hash/MAC failures = fail-closed errors (never deliver partial bytes). Verify `Content-Length` against `fileLength` when present; size-cap the stream (D-67); temp-file-backed `Stream` for large media so slow hosts apply backpressure without RAM blowup; host owns disposal.

**Ordering:** ack-before-download is preserved — the dispatcher sends the ack after durable session writes and BEFORE invoking the downloader (existing D-06 ordering untouched; download never delays the ack).

---

### `Media/MediaRetryTracker.cs` (utility, bounded per-msg-id counter)

**Analog:** `src/Iwasug.WhatsApp.Sdk/Messaging/ReceiptHandler.cs` (lines 34-70, 207-274)

**Cap + bound constants pattern** (`ReceiptHandler.cs:36-56`):
```csharp
internal const int RetryCountCap = 5;      // MediaRetryTracker: MaxRetryRequests = 3 (D-66 cap, not 5)
internal const int MaxRetryCountKeys = 4096; // MediaRetryTracker: copy 4096 verbatim (evict >1h / cap 4096 per D-69)
```

**Counter + eviction pattern** (`ReceiptHandler.cs:207-215, 248-274`) — copy the `ConcurrentDictionary<string, RetryCounter>` + monotonic touch sequence + oldest-half eviction:
```csharp
RetryCounter counter = _retryCounts.AddOrUpdate(
    string.Create(CultureInfo.InvariantCulture, $"{sender}:{messageId}"),
    _ => new RetryCounter(1, Interlocked.Increment(ref _retryTouchSequence)),
    (_, existing) => new RetryCounter(existing.Count + 1, Interlocked.Increment(ref _retryTouchSequence)));
// MediaRetryTracker: key = message id only (not sender:id); stores (attempts, firstRequestUtc);
// evict entries older than 1h / oldest half past 4096 — same EvictOldestRetryCounters shape.
private readonly record struct RetryCounter(int Count, long LastTouched);
```

**Copy these conventions:** process-local, never durable (D-69); cap exhaustion → typed `MediaDownloadExpired` outcome/event and stop (4th request never sent); `TimeProvider`-injected for deterministic cap tests; `ConcurrentDictionary` with ordinal comparer.

---

### Media DTOs / options (`MediaKind.cs`, send-request records)

**Analog:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageBuilder.cs` (lines 18-30)

```csharp
internal sealed record QuotedMessageRef(
    string StanzaId,
    string? Participant,
    string QuotedText,
    string? RemoteJid);

internal sealed record DeviceCiphertext(Jid Device, string EncType, byte[] Ciphertext);
```

**Copy these conventions:** `internal sealed record` with XML-doc'd positional params; `byte[]` fields documented as caller-owned (clone on boundary — `CredentialRecord` hygiene per 04-PATTERNS); public send-request DTOs carry host-supplied `Stream`/path + `Mimetype` + `Caption`/`FileName`/`Width`/`Height`/`Seconds`/`Waveform`/`Thumbnail` — validation lives in the uploader/builder (fail-closed `SdkFormatException` / `SafetyRuleViolationException`), never in the record ctor. Public enum `MediaKind { Image, Document, VoiceNote }` maps to KDF info strings + `mmsType` + `mediatype` attr in ONE match site (exhaustive switch, `StringComparison.Ordinal`).

---

### Media events (`MediaEvents.cs` + dispatcher emit sites)

**Analog:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessagingEvents.cs` (lines 22-29, 207-214) + `src/Iwasug.WhatsApp.Sdk/Messaging/SafetyEvents.cs` (lines 8-50)

**EventArgs twin pattern** (`MessagingEvents.cs:22-29`):
```csharp
public sealed record MessageReceivedEventArgs(
    Domain.MessageKeyRecord Key,
    Domain.Jid Chat,
    Domain.Jid Sender,
    bool IsFromMe,
    DateTimeOffset Timestamp,
    string? Text,
    string? QuotedMessageId = null);
```

**Stream twin pattern** (`MessagingEvents.cs:207-214`, `SafetyEvents.cs:29-34`):
```csharp
public sealed record MessageReceivedEvent(
    Domain.MessageKeyRecord Key,
    Domain.Jid Chat,
    Domain.Jid Sender,
    bool IsFromMe,
    DateTimeOffset Timestamp,
    string? Text,
    string? QuotedMessageId) : WhatsAppEvent(Timestamp);
```

**Media shape (D-67):** `MediaReceivedEventArgs(Key, Chat, Sender, IsFromMe, Timestamp, Kind, Mimetype, FileLength, Caption?, FileName?, Seconds?, Ptt, Stream Content)` + `MediaReceivedEvent` stream twin carrying the same fields. Handler contract text copied on every event ("return quickly, never call DisposeAsync, exceptions logged and swallowed" — `MessagingEvents.cs:10-14`). Host owns `Stream` disposal; undisposed streams bounded by temp-file cleanup policy (documented). Key/URL bytes never in event args (only metadata + handle). Emit from the SAME site as `RaiseMessageReceivedAsync` (`Messenger.cs:356-362`) through `PublishAsync` backpressure (`Messenger.cs:429-439`):
```csharp
private async ValueTask PublishAsync(WhatsAppEvent evt, CancellationToken cancellationToken)
{
    try
    {
        await _events.Writer.WriteAsync(evt, cancellationToken).ConfigureAwait(false);
    }
    catch (ChannelClosedException)
    {
        // Stream completed by DisposeAsync: nothing can observe this fact anymore.
    }
}
```

---

### `Proto/message.proto` (modified: media branches + retry messages)

**Analog:** self, `src/Iwasug.WhatsApp.Sdk/Proto/message.proto:1-78`

**Provenance header pattern** (lines 1-22) — extend the Baileys-commit line to name the new tables; keep the whatsmeow-exclusion sentence verbatim:
```proto
// Copyright (c) Iwa Sugriwa. Licensed under the MIT License.
//
// PROVENANCE (NOTICE.md "whatsmeow rule" + provenance-recording policy):
//   - This schema is HAND-WRITTEN for this repository. No bytes were copied
//     from whatsmeow (MPL-2.0) or from any other upstream .proto file.
//   - Field numbers and field roles were learned read-only from the
//     MIT-licensed Baileys sources at commit 0af2386: WAProto/WAProto.proto
//     (... existing text ... + ImageMessage fields url=1/mimetype=2/caption=3/
//     fileSha256=4/fileLength=5/height=6/width=7/mediaKey=8/fileEncSha256=9/
//     directPath=11/mediaKeyTimestamp=12/jpegThumbnail=16/contextInfo=17,
//     DocumentMessage fields url=1/mimetype=2/title=3/fileSha256=4/fileLength=5/
//     pageCount=6/mediaKey=7/fileName=8/fileEncSha256=9/directPath=10/
//     mediaKeyTimestamp=11/jpegThumbnail=16/contextInfo=17/caption=20,
//     AudioMessage fields url=1/mimetype=2/fileSha256=3/fileLength=4/seconds=5/
//     ptt=6/mediaKey=7/fileEncSha256=8/directPath=9/mediaKeyTimestamp=10/
//     contextInfo=17/streamingSidecar=18/waveform=19,
//     Message branches imageMessage=3/documentMessage=7/audioMessage=8,
//     MediaRetryNotification{stanzaId=1,directPath=2,result=3,messageSecret=4 omitted}/
//     ServerErrorReceipt{stanzaId=1}). No whatsmeow bytes were involved.
```

**Branch-addition pattern** (lines 73-78) — additive only; pinned numbers `1/2/6/35` MUST NOT move:
```proto
message Message {
  string conversation = 1;
  SenderKeyDistributionMessage senderKeyDistributionMessage = 2;
  ImageMessage imageMessage = 3;
  ExtendedTextMessage extendedTextMessage = 6;
  DocumentMessage documentMessage = 7;
  AudioMessage audioMessage = 8;
  MessageContextInfo messageContextInfo = 35;
}
```

**csproj wiring** (`Iwasug.WhatsApp.Sdk.csproj:62-70`) — no new item needed (same file extended); if retry messages go in a new `media_retry.proto`, add one item beside the existing ones:
```xml
<Protobuf Include="Proto\message.proto" GrpcServices="None" OutputOptions="internal_access" />
```
Keep `internal_access` (generated types stay out of the public surface; genuinely public media surface goes in `PublicAPI.Unshipped.txt`). Add one `NOTICE.md` row per upstream read when code lands (repo law).

---

### `Messaging/MessageBuilder.cs` (modified: media plaintext builders)

**Analog:** self, `MessageBuilder.cs:58-206`

**Builder-method pattern** (`MessageBuilder.cs:88-96, 160-181`) — one method per media kind, fail-closed, rule-only messages:
```csharp
internal static byte[] BuildPlainText(string text)
{
    if (string.IsNullOrEmpty(text))
    {
        throw new SdkFormatException("Message text must not be null or empty.");
    }

    return new Message { Conversation = text }.ToByteArray();
}
// Media: BuildImageMessage(MediaUploadResult r, string? caption, ...),
// BuildDocumentMessage(..., string fileName, ...), BuildVoiceNoteMessage(..., uint seconds, ...)
// each validating: mediaKey 32 B, fileSha256/fileEncSha256 32 B, directPath non-empty + "/"-prefixed,
// fileLength > 0 + within caps, mimetype on allow-list, fileName sanitized, seconds/dimensions ranged,
// ptt=true forced on voice notes, waveform 64 B × 0–100 when present.
```

**Field-number constants pattern** (`MessageBuilder.cs:60-67`) — add `ImageMessageFieldNumber = 3`, `DocumentMessageFieldNumber = 7`, `AudioMessageFieldNumber = 8` beside the existing `ConversationFieldNumber = 1` / `ExtendedTextMessageFieldNumber = 6`.

**Parse seam** (`MessageBuilder.cs:190-206`) — reuse `ParseMessage` verbatim for the receive path (typed parse, `InvalidProtocolBufferException` → `SdkFormatException`).

**Node seam stays shared:** `AssembleMessageNode` (`MessageBuilder.cs:222-297`) is reused unchanged — the media delta is the `messageType` argument (`"media"`, not `"text"`) plus a `mediatype` attr on each `enc` child (see MessageSender section). Constants `EncVersion = "2"` (`MessageBuilder.cs:70`) and `EncTypeMessage/PreKey/SenderKey` (`MessageBuilder.cs:73-79`) are reused; add `MediaTypeImage = "image"`, `MediaTypeDocument = "document"`, `MediaTypeAudio = "audio"`, `MediaTypePtt = "ptt"` constants in the same block.

---

### `Messaging/MessageSender.cs` (modified: media node attrs)

**Analog:** self, `MessageSender.cs:96-220, 273-417, 780-810, 894-919`

**Send-method pattern** (`MessageSender.cs:96-105`) — add `SendMediaAsync(chat, plaintext, displayText, mediaType, ct)` + `SendGroupMediaAsync` following the `SendAsync → SendPayloadAsync` split (public validation → shared `SendPayloadAsync`-equivalent core so gate, one-network-send, ack correlation, D-20 cache write stay shared):
```csharp
internal Task<SendOutcome> SendAsync(Jid chat, string text, CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(chat);
    if (string.IsNullOrEmpty(text))
    {
        throw new SdkFormatException("Message text must not be null or empty.");
    }

    return SendPayloadAsync(chat, text, MessageBuilder.BuildPlainText(text), cancellationToken);
}
```

**Node-attr delta (exact strings — 06-RESEARCH wire deltas):**
```csharp
// Current text path (MessageSender.cs:208-217 and :389-399) hardcodes ["type"] = "text".
// Media path MUST set ["type"] = "media" on the <message> node for all three kinds,
// and mediatype="image|document|audio|ptt" on every 1:1 enc child (voice note with ptt=true → "ptt")
// and on the group skmsg enc child. Everything else (per-device <to jid> fanout, enc v="2"
// type="msg|pkmsg", device-identity on pkmsg, phash, t, id, SKDM fanout) is IDENTICAL — reuse
// MessageBuilder.AssembleMessageNode + BuildRecipientChildren, do not fork.
```

**Recipient-children pattern** (`MessageSender.cs:894-913`) — extend the attr dict with `mediatype`:
```csharp
yield return BinaryNode.FromChildren(
    "to",
    [BinaryNode.FromBytes(
        "enc",
        device.Ciphertext,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["v"] = MessageBuilder.EncVersion,
            ["type"] = device.EncType,
            // Media: ["mediatype"] = mediaType ("image"|"document"|"audio"|"ptt")
        })],
    new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["jid"] = device.Device.ToString(),
    });
```

**Reuse verbatim:** `GenerateMessageId` 3EB0+18 (`MessageSender.cs:915-919`); `TryCacheRecentAsync` D-20 (`MessageSender.cs:739-772`) — cached `ResendPayload` already carries the media branch incl. `mediaKey` (no shape change); `AwaitAckAsync` one-network-send + exactly-one-outcome (`MessageSender.cs:780-810`); D-06 persist-before-wire (`MessageSender.cs:191-193`).

---

### `Messaging/Messenger.cs` (modified: three gated media APIs)

**Analog:** self, `Messenger.cs:184-221, 247-286, 614-619`

**Gated-send pattern** (`Messenger.cs:184-193`) — copy exactly per new API; upload happens INSIDE the gate delegate (after admission, before inner send):
```csharp
public async Task<SendOutcome> SendMessageAsync(Jid chat, string text, CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
    await EnsureOwnPrekeysAsync(cancellationToken).ConfigureAwait(false);
    return await _safety.ExecuteSendAsync(
        new SendContext(chat, IsReply: false),
        cancellationToken,
        ct => _sender.SendAsync(chat, text, ct)).ConfigureAwait(false);
}
// Media: SendImageAsync(chat, imageStream/file, mimetype, caption?, dimensions?, thumbnail?, ct),
// SendDocumentAsync(chat, ..., fileName, ...), SendVoiceNoteAsync(chat, ..., seconds, waveform?, ...)
// each: ThrowIfCancellationRequested → ThrowIf disposed → EnsureOwnPrekeysAsync →
// _safety.ExecuteSendAsync(new SendContext(chat, IsReply: false), ct, async innerCt => { upload → build → _sender.SendMediaAsync(...) })
```

**Reply-exemption note:** `ReplyAsync` uses `IsReply: true` (`Messenger.cs:282-285`); media sends are NOT replies (`IsReply: false`) so cold-outreach marking applies normally. Voice notes request the `recording` pacing pause, images/documents the `composing` pause (SAF-06 `HumanPacing` seam — the gate owns the pause; Messenger passes no pacing arg).

**Seam access:** `ResolveStoresAsync` (`Messenger.cs:614-615`), `RouterProvider` (`Messenger.cs:617`), `Clock` (`Messenger.cs:619`), `Connection.SendNodeAsync` (`WaConnection.cs:831-871`) — media helpers resolve through these, never new globals. `PublicAPI.Unshipped.txt` entries for the three APIs + event types (PublicApiAnalyzers, `csproj:30-31, 82-85`).

---

### `Messaging/MessageDispatcher.cs` (modified: media branch + `<rmr>` routing)

**Analog:** self, `MessageDispatcher.cs:90-263, 363-450, 505-614, 675-704`

**Dispatch-key pattern** (`MessageDispatcher.cs:90-104, 557-614`) — test `<rmr>` presence BEFORE `type="retry"` handling (disjoint mechanisms sharing only the recent cache):
```csharp
private async Task HandleAsync(BinaryNode node, CancellationToken cancellationToken)
{
    switch (node.Tag)
    {
        case "message":
            await HandleMessageAsync(node, cancellationToken).ConfigureAwait(false);
            break;
        case "ack":
            await HandleAckAsync(node, cancellationToken).ConfigureAwait(false);
            break;
        case "receipt":
            await HandleReceipt(node, cancellationToken).ConfigureAwait(false);
            break;
    }
}
// Media-retry: inbound receipt carrying an <rmr> child → media-retry handler (NOT Pattern-5 decrypt-retry);
// dispatch key = presence of <rmr> (FirstChild(node,"rmr") != null), orthogonal to type="retry".
// Group media-retry adds participant on <rmr> (mirror the group-ack participant precedent,
// MessageDispatcher.cs:505-521).
```

**Receive-branch pattern** (`MessageDispatcher.cs:190-263`) — after outer E2EE decrypt, parse `Message`; if `imageMessage`/`documentMessage`/`audioMessage` present → media branch (else existing text path untouched). Ordering contract preserved verbatim: persist → ack → event → cache (D-06/Pitfall 4), ack BEFORE any media download:
```csharp
await repository.SaveUnlockedAsync(address, session, cancellationToken).ConfigureAwait(false);
// ... SKDM branch ...
await SendAckAsync(messageId, from, cancellationToken).ConfigureAwait(false);

string? text = message.Conversation.Length == 0 ? null : message.Conversation;
var key = new MessageKeyRecord(chat, isFromMe, messageId, string.Empty);
await _owner.RaiseMessageReceivedAsync(
    new MessageReceivedEventArgs(key, chat, from, isFromMe, timestamp, text, messageId),
    cancellationToken).ConfigureAwait(false);
// Media: validate media fields fail-closed → download+verify (D-62/D-65) → RaiseMediaReceivedAsync;
// on 403/404/410 → automatic server-error receipt + <rmr> recovery (host-invisible).
```

**Helpers to reuse:** `FirstChild` (`MessageDispatcher.cs:687-704`), `ParseTimestamp` (`MessageDispatcher.cs:675-685`), per-address `SignalSessionGate` (`MessageDispatcher.cs:154-156`), `TryEmitRetryReceiptAsync` best-effort shape (`MessageDispatcher.cs:482-503`), group `participant` attr on ack (`MessageDispatcher.cs:505-521`), `HandleGroupMessageAsync` participant-parse + sender-key decrypt + persist→ack→event→cache (`MessageDispatcher.cs:363-450`).

**Field validation (D-67):** `mediaKey` 32 B, `fileSha256`/`fileEncSha256` 32 B, `directPath` non-empty + `/`-prefixed after host-prefixing, `fileLength` within caps, mimetype on allow-list, `fileName` sanitized (strip directories, reject traversal), `seconds`/dimensions range-checked (all untrusted on receive) — each a rule-only `SdkFormatException` / typed failure, never partial delivery.

---

### `Messaging/ReceiptHandler.cs` (media-retry receipt seam)

**Analog:** self, `ReceiptHandler.cs:136-242, 282-317`

**Receipt-build pattern** (`ReceiptHandler.cs:144-187`) — server-error receipt per 06-RESEARCH D-66a:
```csharp
// Analog: BuildReceiptNode("read"|"delivery", chat, ids, participant, timestamp) with
// ["id"], ["type"], ["to"], ["t"] + optional ["participant"], list/item children for >1 id.
// Media-retry request node:
//   <receipt id="{msgId}" to="{own-non-AD-JID}" type="server-error">
//     <encrypt><enc_p>{gcm-ciphertext}</enc_p><enc_iv>{12B iv}</enc_iv></encrypt>
//     <rmr jid="{chat}" from_me="{true|false}" [participant="{sender}"]/>
// `to` is the OWN account JID (not the peer); from_me reflects original direction; participant only for groups.
```

**Capped-emission pattern** (`ReceiptHandler.cs:198-242`) — copy the count→event→cap→keys→D-06-remove→send shape; media cap is 3 (not 5), tracker is `MediaRetryTracker` (not `_retryCounts`):
```csharp
await _owner.RaiseDecryptionFailedAsync(
    new DecryptionFailedEventArgs(chat, sender, messageId, count, originalTimestamp),
    cancellationToken).ConfigureAwait(false);
if (count > RetryCountCap)
{
    return; // Pattern-5 cap: receipts stop, the typed event is the host's signal
}
```

**Response routing (D-66b):** `<error code>` present → typed failure (code 2 = terminal); else `<encrypt><enc_p>/<enc_iv>` → GCM-decrypt → `MediaRetryNotification`; `result == SUCCESS(1)` → re-download from fresh `directPath`, verify, deliver, cache fresh path on recent-cache entry; `NOT_FOUND(2)`/`DECRYPTION_ERROR(3)`/`GENERAL_ERROR(0)` → typed failure; no further auto-requests past the cap. Sending the retry receipt is protocol traffic (dispatcher/whitelisted egress, NOT safety-gated) — add to the `SendPathAuditTests` whitelist enum (see Tests).

---

### `Safety/SafetyOptions.cs` (modified: byte caps)

**Analog:** self, `SafetyOptions.cs:65-148`

**Defaults + Validate pattern** (lines 65-70):
```csharp
public static SafetyOptions SafeDefaults => new();

internal static SafetyOptions Validate(SafetyOptions? options)
{
    SafetyOptions resolved = options ?? SafeDefaults;

    if (resolved.MaxMessagesPerMinute <= 0)
    {
        throw new ArgumentOutOfRangeException(nameof(options), "MaxMessagesPerMinute must be positive.");
    }
    // ...
    return resolved;
}
```

**New fields (D-68, tunables — 06-RESEARCH OQ2/A4):** `MaxImageBytes`, `MaxDocumentBytes`, `MaxAudioBytes` (starters e.g. 16/64/16 MiB — heuristics, not WA policy; XML docs mark tunable) + `MaxThumbnailBytes = 65536`. Validate positive; oversize fails closed BEFORE upload with rule-only `SafetyRuleViolationException` (no threshold values in the message — `SafetyGateException.cs:25-41` discipline). `AllowUnsafeThroughput` opt-out precedent (`SafetyOptions.cs:125-145`) applies to caps identically. Risk-ledger additions: media uploads are high-bandwidth activity (upload-then-send doubles observable traffic); bulk media fan-out is the fastest ban path.

---

## Shared Patterns

### Gate (every public media send)
**Source:** `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs:184-193` + `src/Iwasug.WhatsApp.Sdk/Safety/ISafetyGate.cs:6-18` + `src/Iwasug.WhatsApp.Sdk/Safety/SafetyGate.cs:51-79`
```csharp
public sealed record SendContext(Jid Chat, bool IsReply);

return await _safety.ExecuteSendAsync(
    new SendContext(chat, IsReply: false),
    cancellationToken,
    ct => _sender.SendAsync(chat, text, ct)).ConfigureAwait(false);
```
**Apply to:** `SendImageAsync`, `SendDocumentAsync`, `SendVoiceNoteAsync` (all with `IsReply: false`); `EnsureOwnPrekeysAsync` runs before the gate; cancellation first + `ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this)` (`Messenger.cs:186-188`). Upload inside the gate delegate — never an independent send loop, never ungated egress.

### Error handling
**Source:** `src/Iwasug.WhatsApp.Sdk/Errors/SdkException.cs:14-23` + `src/Iwasug.WhatsApp.Sdk/Safety/SafetyGateException.cs:10-41`
```csharp
protected SdkException(string message, Exception? inner = null)
    : base(message, inner)
{
}
```
**Apply to:** all `Media/*` files. Validation → `SdkFormatException` (rule-only, never embeds values — `MessageBuilder.cs:90-93`); not-open/no-router → `SdkConnectionException` (`DeviceDirectory.cs:128-129`); transport failure → `SdkTransportException` (`WaConnection.cs:867-870`); cap/policy refusal → `SafetyRuleViolationException(rule, "Safety rule violated.")` (`HumanPacing.cs:146`); cancellation always rethrown unwrapped (`MessageSender.cs:564-567` pattern); receive-path faults degrade via D-20 log-and-continue, never throw through the pump (`MessageDispatcher.cs:246-262`).

### Secret hygiene + no-log discipline
**Source:** `src/Iwasug.WhatsApp.Sdk/Signal/SignalCipher.cs:104-111` + `src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs:44-48` + `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs:887-891`
```csharp
// WaConnection.cs:887-891 — the ONLY log shape near sensitive paths (account id, never bytes):
_logger?.LogError(
    ex,
    "Channel-ready handler failed for account {AccountId}.",
    _accountId.Value);
```
**Apply to:** all media paths. No log call ever sees plaintext, ciphertext, `mediaKey`/`cipherKey`/`macKey`, hashes, URLs, or `direct_path`; `CryptographicOperations.ZeroMemory` on every key temporary (`X3dh.cs:494-522`); `FixedTimeEquals` for MAC compare (`SignalCipher.cs:414`); cached `ResendPayload` now embeds `mediaKey` — the no-keys-in-logs rule extends to every media debug site; exceptions name rules only.

### Validation (fail-closed, bounds-checked)
**Source:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageBuilder.cs:88-96` + `src/Iwasug.WhatsApp.Sdk/Messaging/DeviceDirectory.cs:215-222`
```csharp
if (string.IsNullOrEmpty(text))
{
    throw new SdkFormatException("Message text must not be null or empty.");
}
```
**Apply to:** media-field validation (32-B lengths, `/`-prefixed `directPath`, MIME allow-list + executable reject-list, filename sanitize, size caps, waveform 64×0–100), USync/media-conn/group reply parsing (malformed child fails the whole resolution, no partial list).

### Clock / TimeProvider + cancellation
**Source:** `src/Iwasug.WhatsApp.Sdk/Messaging/DeviceDirectory.cs:51-57` + `src/Iwasug.WhatsApp.Sdk/Safety/HumanPacing.cs:102-108`
```csharp
_clock = timeProvider ?? TimeProvider.System;
await Task.Delay(TimeSpan.FromMilliseconds(pauseMs), _clock, cancellationToken).ConfigureAwait(false);
```
**Apply to:** `MediaConn` TTL, `MediaRetryTracker` expiry, upload/download timeouts, pacing. `cancellationToken.ThrowIfCancellationRequested()` first in every public method; tokens pass through unwrapped.

### Correlated IQ (media-conn)
**Source:** `src/Iwasug.WhatsApp.Sdk/Connection/QueryRouter.cs:98-139` + `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs:367, 467-475`
```csharp
long sequence = Interlocked.Increment(ref _counter);
string id = string.Create(CultureInfo.InvariantCulture, $"q-{sequence}");
// ...register TCS → sender delegate → Task.WhenAny(reply, timeout) → exactly-one-terminal-outcome
internal QueryRouter? CurrentRouter => _channel?.Router;
```
**Apply to:** `MediaConn` queries via `Messenger.RouterProvider`. Never-retried, never-reused ids; `TimeProvider` timeout (FakeTimeProvider-testable).

### Stanza send (all outbound media + retry receipts)
**Source:** `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs:831-871`
```csharp
internal async ValueTask SendNodeAsync(BinaryNode node, CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(node);
    cancellationToken.ThrowIfCancellationRequested();
    ThrowIfDisposed();
    Channel? channel = _channel
        ?? throw new SdkConnectionException("The connection has no open channel: the stanza was not sent.");
    // ...Noise.Encrypt → flag-prefix frame → Transport.SendAsync; typed ladder; OCE rethrown unwrapped
}
```
**Apply to:** media message sends (via `MessageSender.AwaitAckAsync`), server-error retry receipts, `<rmr>` responses. Ciphertext rides `enc` `Data`, never `Text` (Pitfall 8).

### Durable writes (D-06) vs best-effort (D-20)
**Source:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs:191-193` + `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs:200-223` + `src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs:739-772`
```csharp
// D-06: the ratchet advanced — persist the new state before anything rides the wire.
await PersistSessionAsync(stores, device, ratchet, cancellationToken).ConfigureAwait(false);
```
**Apply to:** session persists before media node leaves; one-time-prekey spend before wire claims it; recent-cache writes log-and-continue (`try/catch` swallowing non-cancellation after ack+event). Media-conn/retry-tracker state is process-local, never durable (D-69).

### Test shapes
**Source:** `tests/.../Noise/NoiseVectorTests.cs:64-78` + `tests/.../Messaging/MessageWireFixtureTests.cs:82-88` + `tests/.../Messaging/MessagingLoopbackTests.cs:41-62` + `tests/.../Safety/SendPathAuditTests.cs:50-58`
```csharp
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

internal static SafetyOptions NoSafetyDelays() => new()
{
    AllowUnsafeThroughput = true,
    DeclaredUnsafeAcknowledgment = "Loopback test pins policy timing to zero; production defaults untouched.",
    MaxMessagesPerMinute = 1000,
    MaxMessagesPerHour = 1000,
    MaxMessagesPerDay = 1000,
    MaxColdOutreachPerDay = 1000,
    BaseInterSendJitter = TimeSpan.Zero,
    PacingPresenceMinMs = 0,
    PacingPresenceMaxMs = 0,
};
```
**Apply to:** `media-crypto-fixtures.json` (fixed key × {Image,Audio,Document} → iv/cipherKey/macKey hex + full blob/fileSha256/fileEncSha256 hex + tampers) loaded via the `LoadVector` shape; hex-compare wire data + `Fingerprint` for key material (never print raw keys); `NoSafetyDelays()` for all loopback send proofs; `RecentCachePolicyTests.WaitForCacheStateAsync` polling + `FaultingStoreFactory` for D-20 facts; `HttpMessageHandler` stub for mmg (capture path/query/headers/body); fake media-conn IQ + fake `<rmr>` loopback harness (extend `MessagingLoopbackTests.ScriptedPeer`).
**Extend (exact files/classes):** `tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SendPathAuditTests.cs` (`SendPathAuditTests.every_send_node_egress_site_is_whitelisted_or_gated` — add gated-caller rows for the three media APIs; add `ProtocolEgress.MediaUpload`/`MediaRetry` whitelist entries for `MediaUploader`'s mmg POST path and the server-error receipt sender); `tests/.../Messaging/MessageWireFixtureTests.cs` (media-branch round-trips + baseline `1/2/6/35` stability fact); `tests/.../Messaging/RecentCachePolicyTests.cs` (media `ResendPayload` → retry-correlator parse-back fact); `tests/.../Messaging/MessagingLoopbackTests.cs` (media send/receive/expiry loopbacks).

## Patterns That Must NOT Be Copied

| Forbidden pattern | Where it lives | Why + what to do instead |
|-------------------|----------------|--------------------------|
| `["type"] = "text"` hardcode | `MessageSender.cs:215, 398`; resend node `MessageSender.cs:727` | Media `<message>` nodes MUST use `type="media"`; group media `skmsg` keeps `type="media"` + `mediatype` attr. Attr facts per kind incl. `ptt → mediatype="ptt"` gate this. |
| `enc` children without `mediatype` | `MessageBuilder.cs:271-284`, `MessageSender.cs:894-913` (current text shape) | 1:1 media `enc` children REQUIRE `mediatype="image\|document\|audio\|ptt"`; group `skmsg` enc carries the same attr. Forgetting it sends undecryptable-by-policy/misrendered messages (Pitfall 6). |
| Baileys' unverified download (decrypt-stream without MAC/hash checks) | Upstream Baileys `downloadEncryptedContent` behavior (06-RESEARCH Pitfall 5) | NEVER copy — SDK follows the whatsmeow verify order D-62 (enc-hash → MAC → decrypt → plain-hash), fail-closed, nothing delivered until all three pass. Review gate checks the order. |
| Random-IV prepend / generic `aesEncrypt` helper reuse | 06-RESEARCH Pitfall 2 | Media IV is HKDF-derived and stored NOWHERE on the wire; IV is NOT prepended; MAC covers `iv \|\| ciphertext`. Separate `MediaCrypto` encrypt path that never touches the generic helper; blob-bytes fixture pins it. |
| Full 32-byte HMAC compare against the 10-byte wire MAC | 06-RESEARCH Pitfall 3 | Truncate BOTH sides to 10 bytes (`Truncate(10)` constants) + tamper fixture. Comparing full-vs-truncated always fails. |
| `fileSha256` ↔ `fileEncSha256` swap | 06-RESEARCH Pitfall 4 | `fileSha256 = SHA256(plaintext)`; `fileEncSha256 = SHA256(ciphertext \|\| mac)` (INCLUDES the MAC). Named wrapper types or distinct param names + a swapped-fields negative fact. |
| Key reuse across media kinds / cached-key re-upload | 06-RESEARCH Pitfall 1 | Fresh 32-byte CSPRNG `mediaKey` per upload, zeroized after; golden fixture asserts Image≠Document expansion from the same key. Voice notes use the Audio info — `ptt` is a proto flag only, NOT a KDF input. |
| Upload outside the safety gate / independent media send loop | Anti-pattern vs `Messenger.cs:189-192` gate | Upload happens INSIDE `ExecuteSendAsync` after admission; refused sends never touch mmg; audit test fails any new ungated egress. |
| Shared static `HttpClient` | Anti-pattern vs D-64 | One `SocketsHttpHandler`-backed `HttpClient` per account (header isolation, Phase-7-safe). |
| `byte[]` payload media events | Anti-pattern vs D-67 | Typed `MediaReceivedEvent` + `Stream` handle (temp-file-backed, size-capped, host-disposed). No unbounded `byte[]` buffering. |
| 403/404/410 surfaced to host as errors | Anti-pattern vs D-65/D-66 | 403/404/410 uniformly enter the media-retry flow (`server-error` + `<rmr>`); only cap-exhaustion or `<error>` surfaces as `MediaDownloadExpired`. 410 means gone-then-recover, not host error (Pitfall 9). |
| Reusing the Pattern-5 decrypt-retry counter for media-retry | Anti-pattern vs `ReceiptHandler.cs:207-215` | Disjoint mechanisms: same-id resend re-sends OUR bytes; media-retry re-fetches THEIR re-uploaded bytes. Separate `MediaRetryTracker` (≤3/msg), `<rmr>`-routed BEFORE `type="retry"` handling. |
| `ptt=false` voice notes / mp3-bytes-labeled-ogg / SDK transcode | 06-RESEARCH Pitfall 7 | Voice-note API forces `ptt=true`; mimetype allow-list fail-closed (`audio/ogg; codecs=opus`, Opus-in-Ogg — SDK validates the label, never transcodes). Plain `SendAudioFileAsync` is out of scope. |
| Trusting `fileName`/`mimetype`/`seconds`/dimensions on receive | 06-RESEARCH Pitfall 8 | Sanitize (strip directories, reject traversal), executable-MIME reject list, range checks; SDK never writes to disk itself. |
| Copying whatsmeow code/`.proto`/generated bytes | Repo law (`message.proto:1-14`, 06-RESEARCH HC-1) | Clean-room: hand-written schemas from Baileys MIT @0af2386 only; provenance header + `NOTICE.md` row per upstream read. |
| `type="retry"` handling before `<rmr>` presence check | `MessageDispatcher.cs:605-613` order | Media-retry responses route on `<rmr>` presence FIRST; conflating them with Pattern-5 decrypt retries breaks both correlations. |
| Download-before-ack / cache-before-ack reorderings | `MessageDispatcher.cs:200-257` ordering | Persist → ack → event → cache is absolute; download happens after the ack, cache write after the event (D-20). Ack-before-persist or event-before-ack fails the review gate. |

## No Analog Found

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| `Media/MediaCrypto.cs` streaming large-file path (file/Stream → temp-file ciphertext, constant memory) | service | streaming file-I/O | No streaming-cipher path exists in-repo (Signal paths are bounded in-memory messages); use BCL `CryptoStream` + incremental `SHA256`/`HMACSHA256` (`TransformBlock`/`TransformFinalBlock`) with the D-61 order; temp-file discipline is new (document cleanup + disposal). |
| mmg HTTP transport (`MediaUploader` POST / `MediaDownloader` GET over `HttpClient` + `Polly.Core` pipeline) | service | request-response (HTTP) | Only HTTP client in-repo is `HttpSwJsFetcher.GetStringAsync` (trivial GET); multipart/octet-stream POST, `Retry-After`, 5-attempt host fan-out, and `ResiliencePipeline` have no precedent — STACK.md pre-authorizes `Polly.Core` 8.8.0 for this exact HTTP-only use. |
| `MediaRetryNotification`/`ServerErrorReceipt` GCM framing (`enc_p`/`enc_iv` tag-suffix convention) | crypto + codec | transform | No GCM-tag-suffix framing exists in-repo; pin via the `media-retry-fixtures.json` encrypt→parse→decrypt loopback before live use (D-62). |

## Metadata

**Analog search scope:** `src/Iwasug.WhatsApp.Sdk/Messaging/*`, `Safety/*`, `Signal/*`, `Connection/WaConnection.cs` + `QueryRouter.cs`, `Codec/*`, `Stores/IRecentMessageCacheStore.cs`, `Domain/*`, `Proto/*`, `Errors/*`, `*.csproj`, `tests/.../Safety/*`, `tests/.../Messaging/*`, `tests/.../Noise/*`, `.planning/phases/05-ban-safety-layer/05-PATTERNS.md`, `.planning/phases/04-signal-e2ee-messaging/04-PATTERNS.md`, `.planning/phases/06-media-pipeline/06-RESEARCH.md`
**Files scanned:** 18 analogs read in full or targeted non-overlapping sections (no range re-read); test inventory via glob (70 test files)
**Pattern extraction date:** 2026-10-14
