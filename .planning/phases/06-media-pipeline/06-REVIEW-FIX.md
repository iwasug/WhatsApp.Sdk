---
phase: 06-media-pipeline
fixed_at: 2026-10-16T09:30:00Z
review_path: C:/Users/iwasu/iwasug/sdk-wa/.planning/phases/06-media-pipeline/06-REVIEW.md
iteration: 1
findings_in_scope: 15
fixed: 14
skipped: 1
status: partial
---

# Phase 06: Code Review Fix Report

**Fixed at:** 2026-10-16T09:30:00Z
**Source review:** C:/Users/iwasu/iwasug/sdk-wa/.planning/phases/06-media-pipeline/06-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 15
- Fixed: 14
- Skipped: 1

## Fixed Issues

### CR-01: Size check compares response Content-Length to the plaintext fileLength — every legitimate download is rejected

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Media/MediaDownloader.cs`
**Commit:** 2c82ffe
**Applied fix:** Deleted the transport Content-Length vs plaintext comparison; enforced size post-decrypt via plaintext.Length == fileLength, throwing SdkFormatException on mismatch.

### CR-02: `<rmr>` routing gated behind receipt-type parsing — recovery dead for server-error-typed notifications

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs`
**Commit:** eed8e1c
**Applied fix:** Detected `<rmr>` before any receipt-type classification and routed on presence; normal receipt facts are only raised when no rmr child is present.

### WR-01: Pending recovery context consumed before payload validation — malformed notification permanently destroys recovery

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs`
**Commit:** 7816745
**Applied fix:** Claim without deleting; remove the pending context only on verified delivery; keep retryable paths retryable (outer try/catch no longer discards state).

### WR-02: D-66b terminals unimplemented — `<error code>` never parsed, `Result != 1` dropped silently

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs`
**Commit:** 931f577
**Applied fix:** Parsed `<error code>` terminal (code 2 = terminal, others retryable) and non-success result codes; raised MediaDownloadFailed typed failure for both.

### WR-03: Planned public `MediaDownloadFailed` signal does not exist

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs`, `src/Iwasug.WhatsApp.Sdk/Media/MediaEvents.cs`, `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs`, `src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt`
**Commit:** f196ecb
**Applied fix:** Publicized event/args, declared both in PublicAPI.Unshipped.txt, wired exhaustion signal at the dispatch site (cap-exhausted no pending); handler-only raise as documented.

### WR-04: Received media never written to recent-message cache

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs`
**Commit:** 4373cbb
**Applied fix:** Plumbs raw plaintext into HandleInboundMediaAsync/HandleInboundGroupMediaAsync and writes a RecentMessages entry plus purge after the media event raise (mirror of text path).

### WR-05: `_pendingMediaRecoveries` is unbounded

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs`
**Commit:** cf9583d
**Applied fix:** Added SweepPendingRecoveries (cap 4096, 1h horizon, oldest-half drop) invoked on insert.

### WR-06: `SendDocumentAsync` accepts thumbnail and silently discards it

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageBuilder.cs`, `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs`, `src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt`
**Commit:** aeb6046
**Applied fix:** Threaded thumbnail through BuildDocumentMessage with the 64 KiB cap and JpegThumbnail assignment; added thumbnail parameter to SendGroupDocumentAsync and passed it at both call sites; updated PublicAPI.

### WR-07: `MediaReceivedEvent` and EventHandler twin share one Stream instance

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs`
**Commit:** 7ded65a
**Applied fix:** When content is a MemoryStream backed by a byte[] buffer, the stream twin gets its own non-writable MemoryStream view over the same immutable buffer.

### WR-09: Download stub Content-Length set to served body length

**Files modified:** `tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaDownloadTests.cs`, `tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaTracerTests.cs`
**Commit:** 00058b8
**Applied fix:** Set download stubs' Content-Length to the served body length (blob.Length) so the sizing logic is exercised with the real-server invariant. The four planned loopback recovery test facts were not added (test-only follow-up).

### WR-10: `BuildDownloadUrl` accepts unvalidated host and leans on peer-supplied direct path

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Media/MediaDownloader.cs`
**Commit:** 69b34f1
**Applied fix:** Constrained host to `.whatsapp.net` (case-insensitive) and rejected `&`, `#`, `?` in directPath.

### WR-11: Image messages always declare image/jpeg even for PNG uploads

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageBuilder.cs`, `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs`
**Commit:** 2b63d8f
**Applied fix:** Added optional mimetype parameter to BuildImageMessage (default image/jpeg) and threaded caller mimetype at both call sites.

### IN-01: Dead null-guard in MediaDownloader and raw HttpRequestException escaping the seam

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Media/MediaDownloader.cs`
**Commit:** 291cfa8
**Applied fix:** Captured last transport exception and wrapped terminal failure in SdkTransportException; removed now-dead blob-null path separation.

### IN-02: `MediaRetryTracker` eviction snapshots then mutates — refreshed key can be evicted

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Media/MediaRetryTracker.cs`
**Commit:** cbcc193
**Applied fix:** Re-checked entry freshness/value before removal in both eviction passes, so a refreshed concurrent entry keeps its attempt budget.

## Skipped Issues

### WR-08: Media download and host event run inside the per-address SignalSessionGate

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs:176-309` (gate), media branch `:245-267`
**Reason:** Skipped: would require splitting the gate scope so decryption/persistence remain inside the lock while the HTTP download (HandleInboundMediaAsync) and PublishAsync run outside it. That restructure moves SendAck, ack ordering, and the pending-context registration across the using boundary and touches the ratchet-concurrency contract; not applied here (risk of regressions / permanent delivery races) pending a dedicated refereeactor.
**Original issue:** The HTTP download and possibly-blocking PublishAsync backpressure hold the address's gate for the whole network round-trip, stalling subsequent messages from that sender plus retry receipts; no cancellation/timeout beyond the HttpClient default.

---

_Fixed: 2026-10-16T09:30:00Z_
_Fixer: the agent (gsd-code-fixer)_
_Iteration: 1_
