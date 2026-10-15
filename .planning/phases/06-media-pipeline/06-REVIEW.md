---
phase: 06-media-pipeline
reviewed: 2026-10-15T09:00:00Z
depth: standard
files_reviewed: 29
files_reviewed_list:
  - NOTICE.md
  - README.md
  - src/Iwasug.WhatsApp.Sdk/Media/MediaConn.cs
  - src/Iwasug.WhatsApp.Sdk/Media/MediaCrypto.cs
  - src/Iwasug.WhatsApp.Sdk/Media/MediaDownloader.cs
  - src/Iwasug.WhatsApp.Sdk/Media/MediaEvents.cs
  - src/Iwasug.WhatsApp.Sdk/Media/MediaKind.cs
  - src/Iwasug.WhatsApp.Sdk/Media/MediaMetadata.cs
  - src/Iwasug.WhatsApp.Sdk/Media/MediaRetryTracker.cs
  - src/Iwasug.WhatsApp.Sdk/Media/MediaUploader.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/MessageBuilder.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs
  - src/Iwasug.WhatsApp.Sdk/Proto/message.proto
  - src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt
  - src/Iwasug.WhatsApp.Sdk/Safety/SafetyOptions.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Iwasug.WhatsApp.Sdk.Tests.csproj
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaCryptoFixtureTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaDownloadTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaRetryCryptoTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaRetryTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaSafetyTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaSchemaTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaTracerTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/MessagingEventTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SendPathAuditTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/TestData/media-crypto-fixtures.json
  - tests/Iwasug.WhatsApp.Sdk.Tests/TestData/media-retry-fixtures.json
findings:
  critical: 0
  warning: 3
  info: 2
  total: 5
status: issues_found
---

# Phase 06: Code Review Report

**Reviewed:** 2026-10-15T09:00:00Z
**Depth:** standard
**Files Reviewed:** 29
**Status:** issues_found

## Summary

Standard-depth review of the Phase 06 media pipeline diff. Cryptography and receive-side validation are fail-closed and parity-test green; transport/validation code is mostly sound. The findings below are quality and defensive-coding issues, concentrated in download URL/content-length handling, caller mimetype propagation, and documented bring-up gaps. `dotnet build` succeeds with 0 warnings and the Phase 6 filters pass (120 media/safety tests).

### WR-01: Download content-length check rejects only oversized bodies

**Severity:** Warning
**File:** `src/Iwasug.WhatsApp.Sdk/Media/MediaDownloader.cs:51-77`

**Issue:** `DownloadAsync` only throws when the reported `Content-Length` is greater than `fileLength`. A short or truncated response is accepted and pushed into `VerifyAndDecrypt`, where it fails later as a generic hash/MAC error. This costs a full retry budget and obscures the real cause.

**Fix:** Require an exact match when `Content-Length` is present, or reject values below `fileLength` as `SdkFormatException`.

### WR-02: Download URL is built from unsanitized wire-derived path/host data

**Severity:** Warning
**File:** `src/Iwasug.WhatsApp.Sdk/Media/MediaDownloader.cs` (`BuildDownloadUrl`)

**Issue:** `BuildDownloadUrl` interpolates `directPath` and `host` into `https://{host}{directPath}` without rejecting query delimiters, path traversal, or host spoofing. Phase 6 metadata validation only requires `directPath` to start with `/` and never checks `host` because uploads fix the host and receive-side host selection is still future work.

**Fix:** Centralize receive-side host/path validation and reject `?`, `#`, backslashes, `..` segments, and non-allow-listed hosts before constructing the URL.

### WR-03: Image message mimetype is hardcoded to image/jpeg

**Severity:** Warning
**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageBuilder.cs:188-235`

**Issue:** `BuildImageMessage` always emits `Mimetype = "image/jpeg"`, even though `Messenger.SendImageAsync` accepts any image mimetype from `MediaMimeAllowList.Images` (including PNG). The upload result does not retain the caller mimetype, so PNG uploads are serialized with an incorrect mimetype.

**Fix:** Add `Mimetype` to `MediaUploadResult` (or pass it explicitly) and use it in `BuildImageMessage`.

### IN-01: Inbound receive path does not surface media facts

**Severity:** Info
**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs`, `src/Iwasug.WhatsApp.Sdk/Media/MediaEvents.cs`

**Issue:** `MediaReceivedEvent` exists and is vocabulary-tested, but the inbound dispatcher only decodes the `conversation` field and never raises a media event. This is a documented bring-up deferral in Plan 06, not a silent regression; reported here as a follow-up reminder only.

**Fix:** Wire `ImageMessage`/`DocumentMessage`/`AudioMessage` parsing and media-key validation in the receive path before shipping receive-side media.

### IN-02: Media retry tracker eviction is not atomic under concurrency

**Severity:** Info
**File:** `src/Iwasug.WhatsApp.Sdk/Media/MediaRetryTracker.cs:85-125`

**Issue:** `EvictIfNeeded` enumerates and mutates the backing `ConcurrentDictionary` in separate steps while other threads can acquire entries, so the `MaxEntries` bound can be exceeded briefly before another eviction pass. Cap enforcement itself remains correct.

**Fix:** Measure impact; if needed, snapshot/replace under synchronization or accept the opportunistic bound.
