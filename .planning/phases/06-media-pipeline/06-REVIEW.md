---
phase: 06-media-pipeline
reviewed: 2026-10-15T23:20:13Z
depth: standard
files_reviewed: 17
files_reviewed_list:
  - src/Iwasug.WhatsApp.Sdk/Media/MediaEvents.cs
  - src/Iwasug.WhatsApp.Sdk/Media/MediaDownloader.cs
  - src/Iwasug.WhatsApp.Sdk/Media/MediaRetryTracker.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/MessageBuilder.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/ReceiptHandler.cs
  - src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaDownloadTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaRetryCryptoTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaRetryTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaSafetyTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaSchemaTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaSendTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaTracerTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaUploadTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SendPathAuditTests.cs
findings:
  critical: 2
  warning: 11
  info: 2
  total: 15
status: issues_found
---

# Phase 06: Code Review Report

**Reviewed:** 2026-10-15T23:20:13Z
**Depth:** standard
**Files Reviewed:** 17
**Status:** issues_found

## Summary

Second (re-)review of the Phase 06 media pipeline covering the gap-closure work landed since
`4b767ec`: the 14 files touched by plans 06-04 / 06-05 / 06-06 (commits `e247b96`, `b9e044e`,
`6b2d358`, `1321a9d`, `d6598bc`, `8888c11`) plus the 3 files that were re-verified unchanged
(`MediaDownloader.cs`, `MessageBuilder.cs`, `MediaRetryTracker.cs`).

**Scope note:** the evaluation-scope resolver returned `status: degraded` (reason
`no-task-commit-rows`), so the incremental scope was derived from the actual file sets of the
phase-06 commits in `4b767ec..HEAD`; the 17-file list above is that union plus the
re-verification files.

**Re-verified prior findings (claims re-checked against current source):**

- Prior **WR-01** (content-length handling) — still present and, on tracing the real wire sizes,
  materially worse than reported: **upgraded to CR-01** (it rejects every legitimate download).
- Prior **WR-02** (`BuildDownloadUrl` host/direct-path hygiene) — still present → **WR-10** (carried).
- Prior **WR-03** (image mimetype hardcoded) — still present → **WR-11** (carried).
- Prior **IN-02** (`MediaRetryTracker` eviction race) — still present → **IN-02** (carried).
- Prior **IN-01** (dispatcher did not surface media facts) — **resolved** by 06-04: the media
  branch (MessageDispatcher.cs:245-267), `RaiseMediaReceivedAsync` (Messenger.cs:525-533), and
  the public `MediaReceived` event (PublicAPI.Unshipped.txt:991) are in place. Not re-carried.

**Key concerns:**

1. **CR-01** — the HTTP size check compares the response `Content-Length` (encrypted body =
   ciphertext + 10-byte MAC) against `fileLength` (plaintext length). The encrypted body is
   *always* larger, so any real response that carries `Content-Length` throws
   `SdkFormatException`, which the receive path swallows as "download fault": no event, no
   server-error receipt, no recovery. Both in-scope tests only pass because their stubs
   fabricate a `Content-Length` equal to the plaintext length while serving the larger encrypted
   body — one stub even comments "allow mismatch check". Real downloads of inbound media cannot
   succeed as written.
2. **CR-02** — `<rmr>` media-retry routing sits *behind* receipt-type parsing. Any notification
   type outside the six-value enum (notably `server-error` — the type this feature itself uses
   on the outbound side) silently kills the entire host-invisible expiry-recovery path, and
   typeless rmr traffic is mislabeled to hosts as `Delivery` receipts. The function's own comment
   ("route here regardless of receipt type") is contradicted by the code above it, and no test
   pins the inbound type or dispatcher routing.
3. **Plan-vs-implementation gaps for 06-05 and 06-04** — the planned public `MediaDownloadFailed`
   host signal does not exist (internal, zero raise sites, absent from PublicAPI) and cap
   exhaustion is silently ignored (WR-03); the planned recent-cache write for received media is
   missing (WR-04); the four planned loopback recovery facts were not implemented while
   06-05-SUMMARY claims the work complete (WR-09).

The remaining warnings cluster around recovery-state discipline (consumed-before-validated
pending context, unbounded map, unparsed error terminals), a silently dropped public parameter
(document thumbnail), a shared-Stream hazard across the two media event surfaces, the media
download running inside the per-address session gate, and test stubs that mask the defects above.

## Critical Issues

### CR-01: Size check compares response Content-Length to the plaintext fileLength — every legitimate download is rejected (carried: prior WR-01, upgraded)

**File:** `src/Iwasug.WhatsApp.Sdk/Media/MediaDownloader.cs:69-76`
**Issue:** `fileLength` is the **plaintext** byte length — pinned by the design
(06-RESEARCH.md:59/78/96 "plaintext length"), by the uploader (`MediaUploader.cs:135`
`data.Length`), and by the tracer test (`MediaTracerTests.cs:147`
`fileLength.Should().Be(originalBytes.Length)`). The blob the CDN actually serves is
`ciphertext || 10-byte MAC` (`MediaCrypto.cs:101`, `MacLength = 10` at `MediaCrypto.cs:15`), and
PKCS7 always pads, so the body length is **always ≥ fileLength + 11**. The check at :69-75
therefore throws `SdkFormatException("Media download size mismatch.")` for every real response
that carries a `Content-Length` header. In `HandleInboundMediaAsync` that exception lands in the
generic `catch { return; }` (MessageDispatcher.cs:625-628): no `MediaReceived` event, no
`MediaDownloadExpiredException`, no server-error receipt, no recovery — undetectable, total
failure of inbound media. The only reason the suite is green is that both stubs fabricate a
`Content-Length` of the *plaintext* length while serving the larger encrypted body:
`MediaDownloadTests.cs:89` (`= upload.FileLength`) and `MediaTracerTests.cs:93`
(`= 5`, with the comment "allow mismatch check"). A real server cannot send that header. The
original prior-WR-01 point also still holds: truncated bodies pass this check and are only
caught later by the hash/MAC gates (fail-closed, but with a less precise failure).
**Fix:** delete the header-vs-plaintext comparison; enforce size post-decrypt where the
semantic is well-defined:

```csharp
resp.EnsureSuccessStatusCode();

// Response body is ciphertext + 10-byte MAC; its length is always > plaintext fileLength.
// Do NOT compare Content-Length to fileLength here.
blob = await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
break;
```

```csharp
byte[] plaintext = MediaCrypto.VerifyAndDecrypt(blob, mediaKey, info, fileSha256, fileEncSha256);
if (plaintext.Length != fileLength)
{
    throw new SdkFormatException("Media plaintext length mismatch.");
}
```

### CR-02: `<rmr>` routing is gated behind receipt-type parsing — recovery silently dead for `server-error`-typed (or any unknown-typed) notifications

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs:864-875` (type gate) vs `:910-917` (rmr branch); `TryParseReceiptKind` at `:1103-1115`
**Issue:** The documented dispatch contract is presence-based: 06-RESEARCH.md:235-236 "dispatch
key = presence of `<rmr>`, orthogonal to `type="retry"`" and :245 "must test `<rmr>` presence
BEFORE `type="retry"` handling". The code's own comment at :913-914 claims "receipts carrying an
`<rmr>` child route here regardless of receipt type" — but `HandleReceipt` early-returns at
:867-875 unless `type` parses to delivery/read/played/sender/inactive/retry.
`server-error` — the exact type this feature uses on the outbound side
(`ReceiptHandler.cs:495`) and the natural symmetric value for the notification — is not in the
enum, so if inbound rmr notifications carry it, the `<rmr>` branch at :910 is unreachable and the
entire host-invisible expiry-recovery path (MED-02 / SC-2) never runs: pending contexts
registered at :620/:738 are never consumed. Even when the type does parse (the plan expected
`type="retry"` — 06-05-PLAN.md:93 — and that fact was never implemented, see WR-09), the raise at
:906-908 fires first, so typeless rmr protocol traffic masquerades to hosts as a `Delivery`
receipt. Nothing in the test suite pins the inbound type or the dispatcher routing
(`MediaRetryTests.cs:33-44` asserts only the outbound node shape), so this routing assumption is
unverified in both directions.
**Fix:** detect `<rmr>` before any type classification and route on presence; classify/raise
normal receipt facts only when no rmr child is present:

```csharp
BinaryNode? rmr = FirstChild(node, "rmr");
if (rmr is not null)
{
    if (node.Attrs.TryGetValue("id", out string? rmrId) && !string.IsNullOrEmpty(rmrId)
        && node.Attrs.TryGetValue("from", out string? rmrFrom)
        && Jid.TryParse(rmrFrom, out Jid? rmrFromJid) && rmrFromJid is not null)
    {
        await TryHandleMediaRetryReceiptAsync(node, rmr, rmrFromJid, participant, cancellationToken).ConfigureAwait(false);
    }
    return; // never fall through to Pattern-5 handling or raise a Delivery/Retry receipt for rmr traffic
}

string typeAttr = node.Attrs.TryGetValue("type", out string? type) && !string.IsNullOrEmpty(type) ? type : "delivery";
if (!TryParseReceiptKind(typeAttr, out ReceiptKind kind) || /* existing id/from guards */)
{
    return;
}
```

Add the dispatcher-level routing fact from 06-05-PLAN.md:93 (see WR-09).

## Warnings

### WR-01: Pending recovery context is consumed before any payload validation — one malformed or failed notification permanently destroys recovery

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs:950-963, 993-1002, 1005-1012, 1014-1017, 1029-1041`
**Issue:** `_pendingMediaRecoveries.TryRemove(receiptId, ...)` at :950 claims and deletes the
context before anything is validated: missing/short `<encrypt>` children return at :960-963,
GCM decrypt failure returns at :999-1002, proto parse failure at :1009-1012, non-success result
at :1014-1017, and a failed re-download at :1038-1041. Every one of those paths discards the only
copy of the recovery state (`mediaKey`, hashes, kind, filename, fileLength). A single malformed
or transient notification therefore guarantees the message can never be recovered again — a
later valid notification hits the `!TryRemove` early-return at :950-953 and is ignored — and
there is no "retryable-once" behavior for non-terminal `<error>` codes that D-66b
(06-RESEARCH.md:237) requires.
**Fix:** claim the entry but restore it on any retryable failure; delete only on delivered
success or a proven terminal condition:

```csharp
if (!_pendingMediaRecoveries.TryGetValue(receiptId, out PendingRecoveryContext? pending) || pending is null)
{
    return;
}
// ... all validation / decrypt / re-download as today ...
await _owner.RaiseMediaReceivedAsync(args, cancellationToken).ConfigureAwait(false);
_pendingMediaRecoveries.TryRemove(receiptId, out _); // consume only on success
```

For retryable paths (undecryptable payload, download fault), return without removing so a later
notification can retry (the tracker cap already bounds the request side). Adjust the defensive
`finally` at :1054-1061 accordingly (it currently deletes unconditionally).

### WR-02: D-66b terminals are unimplemented — `<error code>` child is never parsed and `Result != 1` drops silently

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs:955-1017`
**Issue:** The handler understands only the `<encrypt><enc_p><enc_iv>` success shape.
06-RESEARCH.md:237 requires `<error code="{n}"/>` → typed failure (code 2 = media gone →
terminal; else retryable-once), and 06-05-PLAN.md:110 requires "a non-success code without an
rmr child fails typed" plus error-code terminals. Today an error-carrying notification hits the
`encP is null` return at :960-963 (after consuming the pending — WR-01) and
`notification.Result != 1` returns silently at :1014-1017. Hosts can never distinguish "still
recovering" from "permanently gone", and `MediaRetryNotification.Result` NOT_FOUND /
GENERAL_ERROR terminals are indistinguishable from malformed traffic.
**Fix:** parse `FirstChild(node, "error")` (code attribute, bounds-checked) before the encrypt
branch and map terminal outcomes to the typed failure surface introduced in WR-03; treat
non-terminal codes per D-66b as retryable-once (re-register the pending per WR-01):

```csharp
BinaryNode? error = FirstChild(node, "error");
if (error is not null && int.TryParse(error.Attrs.GetValueOrDefault("code"), out int code))
{
    bool terminal = code == 2; // media gone on the phone
    await _owner.RaiseMediaDownloadFailedAsync(
        new MediaDownloadFailedEventArgs(pending.Key, pending.Chat, messageId, $"media-retry error {code}"), cancellationToken).ConfigureAwait(false);
    if (terminal) { _pendingMediaRecoveries.TryRemove(receiptId, out _); }
    return;
}
```

### WR-03: The planned public `MediaDownloadFailed` signal does not exist — event is internal, never raised, undeclared, and cap exhaustion is ignored

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs:120, 535-539`; `src/Iwasug.WhatsApp.Sdk/Media/MediaEvents.cs:47-59`; `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs:609-624, 727-741`; `src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt`
**Issue:** 06-05-PLAN.md:110/119/122 explicitly require: "on IsExhausted the typed
expiry-exhausted outcome is raised once — a public `MediaDownloadFailed` event on Messenger
mirroring `MediaReceived`, declared in `PublicAPI.Unshipped.txt` — the host's only signal …
carrying no key material". Implementation reality (verified):

- `MediaDownloadFailed` is `internal` (Messenger.cs:120); `MediaDownloadFailedEventArgs` is
  `internal` (MediaEvents.cs:47); neither appears anywhere in `PublicAPI.Unshipped.txt` (only
  `MediaReceived` is declared, line 991); grep shows **zero** call sites for
  `RaiseMediaDownloadFailedAsync` (Messenger.cs:535), and the helper does not even publish a
  stream twin despite every sibling raise doing handler-then-`PublishAsync`.
- `IsExhausted` (`MediaRetryTracker.cs:76-77`) is never called in `src/`.
- Callers discard the cap signal: `SendMediaRetryReceiptAsync` returns `false` when the 3-request
  cap is hit (`ReceiptHandler.cs:477-480`, no wire egress), but MessageDispatcher.cs:613 and :731
  ignore the result and still register a pending context (:620/:738) that no future request can
  ever satisfy.

Net effect: when recovery is exhausted or terminally fails, **nothing** reaches the host — the
exact failure mode MED-02 scale 2 forbids — and the in-memory pending entry leaks (WR-05).
06-05-SUMMARY's claim that this was delivered is inaccurate.
**Fix:** publicize the event/args (reason string only, no key material), declare both in
`PublicAPI.Unshipped.txt`, and wire the exhaustion signal exactly once at the dispatch site:

```csharp
// MessageDispatcher.cs ~613 (both 1:1 and group sites)
bool requestSent = await _owner.Receipts
    .SendMediaRetryReceiptAsync(key.Id, chat, isFromMe, participant: null, cancellationToken)
    .ConfigureAwait(false);
if (!requestSent)
{
    await _owner.RaiseMediaDownloadFailedAsync(
        new MediaDownloadFailedEventArgs(key, chat, key.Id, "media-retry cap exhausted"), cancellationToken)
        .ConfigureAwait(false);
    return; // no pending: no further request will ever be sent for this id
}
_pendingMediaRecoveries[key.Id] = new PendingRecoveryContext(...);
```

Also give the raise its standard twin (handler + `PublishAsync(new MediaDownloadFailedEvent(...))`)
or explicitly document handler-only delivery if the event should not travel the stream.

### WR-04: Received media is never written to the recent-message cache — 06-04 ordering and the D-69 correlator design are unimplemented

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs:631-632` (1:1) and `:748-749` (group)
**Issue:** 06-04-PLAN.md:84 sequences the media path "… then `RaiseMediaReceivedAsync` with the
verified Stream, **then recent-cache write**", and 06-RESEARCH.md:254/268 designate the cached
`ResendPayload` (which already carries the media branch including `mediaKey`) as the media-retry
correlator. The implementation raises the event and stops — no `RecentMessages.PutAsync`
anywhere in either media handler — while the text path (:294-303) and the send paths
(`MessageSender.cs:169`, `:390` via `TryCacheRecentAsync` at `:824-857`) do cache. Consequences:
hosts cannot quote/reply to received media (the cache is documented as the only quote source,
Messenger.cs:363-368), and the retry correlator became the ad-hoc in-memory
`_pendingMediaRecoveries` map (WR-05), losing all state on restart despite the design's
store-based approach (06-05-PLAN.md:101 even lists `IRecentMessageCacheStore.cs` for
modification — no store change landed).
**Fix:** plumb the decrypted raw plaintext (available at MessageDispatcher.cs:213/`decrypted.Plaintext`)
and the `StoreSession` into `HandleInboundMediaAsync`/`HandleInboundGroupMediaAsync`, and after
the event raise mirror the text-path write:

```csharp
await stores.RecentMessages.PutAsync(
    new RecentMessageEntry(key.Id, chat.ToString(), timestamp, caption, from.ToString(), rawPlaintext),
    cancellationToken).ConfigureAwait(false);
await stores.RecentMessages.PurgeExpiredAsync(timestamp - RetentionHorizon, cancellationToken).ConfigureAwait(false);
```

### WR-05: `_pendingMediaRecoveries` is unbounded — no cap, no eviction, no purge

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs:64` (adds at `:620`, `:738`; removes at `:950`, `:1059`)
**Issue:** Entries are removed only when an rmr notification for that exact id arrives. Every
other outcome leaves the `PendingRecoveryContext` (mediaKey, hashes, caption, filename) in the
dictionary for process lifetime: peer never re-uploads, request cap already exhausted at the
first expiry (WR-03 registers a pending anyway), notification consumed by a failure path
(WR-01), or the peer simply goes offline. 06-RESEARCH.md:270 explicitly requires the pending
tracker to be bounded ("evict entries older than 1 h / cap 4096 entries mirroring
`ReceiptHandler.MaxRetryCountKeys`"); `MediaRetryTracker` implements that discipline
(`EvictIfNeeded`, `:79-105`) but is not used for this map. On a long-lived multi-account host
processing a steady stream of expired media, the map grows without limit.
**Fix:** bound the map with the same discipline — either route pending contexts through a
tracker-style structure, or store `timestamp` + attempts and sweep on insert:

```csharp
private const int MaxPendingRecoveries = 4096;
private static readonly TimeSpan PendingRecoveryHorizon = TimeSpan.FromHours(1);

// on add:
if (_pendingMediaRecoveries.Count >= MaxPendingRecoveries)
{
    DateTimeOffset cutoff = _owner.Clock.GetUtcNow() - PendingRecoveryHorizon;
    foreach (var kv in _pendingMediaRecoveries.Where(kv => kv.Value.Timestamp < cutoff).ToList())
    {
        _pendingMediaRecoveries.TryRemove(kv.Key, out _);
    }
}
```

(The cache-based correlator in WR-04 would supersede this map entirely.)

### WR-06: `SendDocumentAsync` accepts a thumbnail and silently discards it

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs:271` vs `:286`; `src/Iwasug.WhatsApp.Sdk/Messaging/MessageBuilder.cs:229-256`
**Issue:** The public API takes `byte[]? thumbnail = null` (declared in
`PublicAPI.Unshipped.txt:298`) but line 286 calls
`BuildDocumentMessage(upload, mimetype, title, fileName, caption)` — the parameter is never
passed, `BuildDocumentMessage` has no thumbnail parameter at all, and `DocumentMessage`'s
`jpegThumbnail` field never gets set. Callers get no error and receivers render a blank preview:
silent caller-visible data loss on a public API. This is inconsistent with
`SendImageAsync`/`SendGroupImageAsync`, which honor the same parameter (Messenger.cs:265/:322 →
MessageBuilder.cs:204-222), and `SendGroupDocumentAsync` (Messenger.cs:328,
PublicAPI.Unshipped.txt:301) does not even expose the parameter — the two document surfaces
disagree.
**Fix:** thread the parameter through with the same 64 KiB cap as images:

```csharp
internal static byte[] BuildDocumentMessage(
    MediaUploadResult upload, string mimetype, string? title, string? fileName, string? caption,
    byte[]? thumbnail = null)
{
    ...
    if (thumbnail is not null && thumbnail.Length > Iwasug.WhatsApp.Sdk.Media.MediaMimeAllowList.MaxThumbnailBytes)
    {
        throw new SdkFormatException("Media thumbnails must not exceed 64 KiB.");
    }
    ...
    if (thumbnail is not null) doc.JpegThumbnail = ByteString.CopyFrom(thumbnail);
```

and pass `thumbnail` at Messenger.cs:286 (add the parameter to `SendGroupDocumentAsync` as well
for symmetry).

### WR-07: `MediaReceivedEvent` and the EventHandler twin share one `Stream` instance — double-consumption and double-dispose hazard

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs:525-533`; `src/Iwasug.WhatsApp.Sdk/Media/MediaEvents.cs:6-8, 26-28`
**Issue:** `RaiseMediaReceivedAsync` hands the literal same `args.Content` stream to both
surfaces, and both XML docs say "the host owns its disposal". A host that consumes the event
bus and also subscribes the handler (or any fan-out relay / the Phase-7 manager event fusion)
receives one disposable resource twice: the two consumers race the read position, and whichever
disposes first leaves the other seeing an empty/closed stream. `MediaEventReceived` twins for
plain messages carry no disposable state, so this is a new class of hazard introduced by the
media branch.
**Fix:** give each surface its own view over the immutable plaintext buffer (the download
returns a read-only `MemoryStream` over a `byte[]`, MediaDownloader.cs:97-98):

```csharp
internal async ValueTask RaiseMediaReceivedAsync(MediaReceivedEventArgs args, CancellationToken cancellationToken)
{
    MediaReceived?.Invoke(this, args);
    System.IO.Stream? twin = args.Content is System.IO.MemoryStream owned && owned.TryGetBuffer(out ArraySegment<byte> buf)
        ? new System.IO.MemoryStream(buf.Array!, buf.Offset, buf.Count, writable: false)
        : args.Content; // fallback: document single-consumer contract or copy
    await PublishAsync(new MediaReceivedEvent(
        args.Key, args.Chat, args.Sender, args.IsFromMe, args.Timestamp, args.Kind, args.Mimetype,
        args.FileLength, args.Caption, args.FileName, args.Seconds, args.Ptt, twin),
        cancellationToken).ConfigureAwait(false);
}
```

### WR-08: Media download and host event run inside the per-address `SignalSessionGate`

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs:176-309` (gate), media branch `:245-267`, download `:607`, event raise `:631` (group: `:720-749`)
**Issue:** The gate's contract (:169-176) is to serialize ratchet mutations for one address and
let retry receipts "ride the same gate". The media awaits are inside that scope, so the HTTP
download (`HandleInboundMediaAsync` :607) and the possibly-blocking `PublishAsync` backpressure
(:631) hold the address's gate for the whole network round-trip. There is no cancellation or
timeout on this path — `OnStanzaReceived` runs `HandleAsync(node, CancellationToken.None)`
(:101) and the download has no linked CTS (unlike uploads, which use a 30 s timeout,
`MediaUploader.cs:108-109`; the HttpClient default ~100 s is the only bound). A slow or hung CDN
stalls every subsequent message from that sender plus its retry receipts, while each stanza
spawns another `Task.Run` handler piling up behind the gate.
**Fix:** keep the gate scope to decrypt/persist only; capture the validated media branch inside,
then download/publish after release:

```csharp
// inside the using: decrypt, persist session state, and validate the media branch only
MediaBranch? pendingMedia = ExtractValidatedMedia(message); // kind + subtype + key pieces
// ... using ends here ...
if (pendingMedia is not null)
{
    await SendAckAsync(pendingMedia.Id, from, cancellationToken).ConfigureAwait(false);
    await HandleInboundMediaAsync(..., pendingMedia, cancellationToken).ConfigureAwait(false);
    return;
}
```

### WR-09: The planned retry/recovery facts are not implemented — the routing assumption (CR-02) and the size check (CR-01) are untested

**File:** `tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaRetryTests.cs:33-44`; `tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaDownloadTests.cs:85-90`; `tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaTracerTests.cs:89-95`
**Issue:** 06-05-PLAN.md:93/110/119 require four loopback facts: (1) a receipt with an `<rmr>`
child and `type="retry"` reaching the media-retry handler with Pattern-5 counters untouched;
(2) one full recovery exchange per id (403 → server-error receipt → fake rmr notification
encrypted with the retry key derived from the cached payload → fresh-path re-download →
`MediaReceived`); (3) no fourth request on the wire + one typed exhaustion signal; (4) error
terminal facts. None exist. What is there:

- `MediaRetryTests.cs:33-44` — despite the name
  `ServerErrorReceiptNodeRoutesMediaRetryBeforeRetryClassification`, it asserts only the
  *outbound node shape*; no dispatcher routing is exercised at all, which is exactly how CR-02
  escaped.
- `MediaDownloadTests.cs:89` and `MediaTracerTests.cs:93` fabricate `Content-Length` equal to
  the plaintext length while serving the larger encrypted body (comment: "allow mismatch
  check"), masking CR-01. On a real wire the header is the body length.
- `MediaRetryCryptoTests.cs` covers only GCM/proto round-trips, `MediaSafetyTests.cs` only
  constants.

These are reliability failures, not style: the suite is green while the production path is
broken.
**Fix:** implement the four planned facts (they will fail against the current code and prove
CR-02, WR-01, WR-02, WR-03), and set the download stubs' `Content-Length` from the served body
length (`blob.Length`) so the sizing logic is exercised with the real-server invariant.

### WR-10: `BuildDownloadUrl` accepts an unvalidated host and leans on the peer-supplied direct path (carried: prior WR-02)

**File:** `src/Iwasug.WhatsApp.Sdk/Media/MediaDownloader.cs:101-112`
**Issue:** Re-verified present. `host` is interpolated into the URL with no validation (currently
not wire-reachable — default constant at :49, internal seam — but a standing SSRF-shaped footgun
the moment a caller passes one), and `directPath` is concatenated without re-validation beyond
the leading-`/` check in `MediaMetadata.cs:52`; a value containing `&` or `#` alters/truncates
the mmg query string (e.g. peer-supplied `/v/1&mms-type=video`).
**Fix:** constrain the host to the media-connect domain and reject query/fragment characters in
the path:

```csharp
if (string.IsNullOrWhiteSpace(host) || !host.EndsWith(".whatsapp.net", StringComparison.OrdinalIgnoreCase))
{
    throw new SdkFormatException("Untrusted media host.");
}
if (directPath.Contains('&') || directPath.Contains('#') || directPath.Contains('?'))
{
    throw new SdkFormatException("Unexpected media direct path shape.");
}
```

(Preserve the existing `&hash=...&mms-type=...&__wa-mms=` suffix shape — it is the pinned wire
convention.)

### WR-11: Image messages always declare `image/jpeg`, even for PNG uploads (carried: prior WR-03)

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageBuilder.cs:211`
**Issue:** Re-verified present. `SendImageAsync`/`SendGroupImageAsync` allow both `image/jpeg`
and `image/png` (Messenger.cs:255/:314 against `MediaMimeAllowList.Images`,
`MediaKind.cs:58`), but `BuildImageMessage` hardcodes `Mimetype = "image/jpeg"`. A PNG sent
through the public API arrives mislabeled on the wire — receivers may render/save it as JPEG —
and `MediaTracerTests.cs:138` pins `image/jpeg` for a jpeg-minded test, so nothing catches the
divergence.
**Fix:** thread the caller's mimetype through and default only when absent:

```csharp
internal static byte[] BuildImageMessage(MediaUploadResult upload, string? caption, int? width = null,
    int? height = null, byte[]? thumbnail = null, string? mimetype = null)
{
    ...
    Mimetype = string.IsNullOrEmpty(mimetype) ? "image/jpeg" : mimetype,
```

Pass `mimetype` at Messenger.cs:265/:322 (the call sites already validated it against the
allow-list; normalize casing before storing).

## Info

### IN-01: Dead null-guard in `MediaDownloader` and a raw `HttpRequestException` escaping the seam

**File:** `src/Iwasug.WhatsApp.Sdk/Media/MediaDownloader.cs:54-93`
**Issue:** The retry loop can only exit via `break` (blob assigned) or by throwing: on the fifth
attempt the `catch (HttpRequestException) when (attempts < 5)` filter (:85) is false, so the raw
`HttpRequestException` propagates — meaning (a) the `blob is null` guard at :90-93 is
unreachable dead code, and (b) callers of the internal seam see a raw transport exception type
on terminal failure instead of the `SdkTransportException` the guard's message implies
(inconsistent with the upload side, which wraps into `SdkTransportException`,
`MediaUploader.cs:142-149`).
**Fix:** drop the dead guard and wrap the terminal failure explicitly:

```csharp
throw new SdkTransportException("Media download failed.");
```

(track the last exception in the loop and attach it as the inner exception).

### IN-02: `MediaRetryTracker` eviction snapshots then mutates — a refreshed key can be evicted (carried: prior IN-02)

**File:** `src/Iwasug.WhatsApp.Sdk/Media/MediaRetryTracker.cs:79-105`
**Issue:** Re-verified present. Both eviction passes materialize keys with `.ToList()` and then
`TryRemove` unconditionally; a concurrent `TryReserve` that refreshes an entry between the
snapshot and the removal can have its key dropped (losing its attempt budget), and the "drop
oldest half" pass at :96-103 can race the same way. Impact is bounded (caps are best-effort,
ban-safety only under-counts), but the mutate-during-enumeration snapshot is a classic
check-then-act shape that future edits can turn sharp.
**Fix:** remove conditionally on the observed value, or re-check freshness immediately before
removal:

```csharp
foreach (string key in stale)
{
    if (_entries.TryGetValue(key, out Entry? e) && e.FirstRequestUtc < cutoff)
    {
        _entries.TryRemove(key, out _);
    }
}
```

---

_Reviewed: 2026-10-15T23:20:13Z_
_Reviewer: the agent (gsd-code-reviewer)_
_Depth: standard_
