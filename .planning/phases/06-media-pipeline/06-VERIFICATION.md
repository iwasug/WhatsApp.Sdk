---
phase: 06-media-pipeline
verified: 2026-10-15T17:00:00Z
status: human_needed
score: 7/10 must-haves verified
covered_files:
  - .planning/phases/06-media-pipeline/06-01-PLAN.md
  - .planning/phases/06-media-pipeline/06-01-SUMMARY.md
  - .planning/phases/06-media-pipeline/06-02-PLAN.md
  - .planning/phases/06-media-pipeline/06-02-SUMMARY.md
  - .planning/phases/06-media-pipeline/06-03-PLAN.md
  - .planning/phases/06-media-pipeline/06-03-SUMMARY.md
  - .planning/phases/06-media-pipeline/06-04-PLAN.md
  - .planning/phases/06-media-pipeline/06-04-SUMMARY.md
  - .planning/phases/06-media-pipeline/06-05-PLAN.md
  - .planning/phases/06-media-pipeline/06-05-SUMMARY.md
  - .planning/phases/06-media-pipeline/06-06-PLAN.md
  - .planning/phases/06-media-pipeline/06-06-SUMMARY.md
  - src/Iwasug.WhatsApp.Sdk/Media/MediaConn.cs
  - src/Iwasug.WhatsApp.Sdk/Media/MediaCrypto.cs
  - src/Iwasug.WhatsApp.Sdk/Media/MediaDownloader.cs
  - src/Iwasug.WhatsApp.Sdk/Media/MediaEvents.cs
  - src/Iwasug.WhatsApp.Sdk/Media/MediaKind.cs
  - src/Iwasug.WhatsApp.Sdk/Media/MediaMetadata.cs
  - src/Iwasug.WhatsApp.Sdk/Media/MediaRetryTracker.cs
  - src/Iwasug.WhatsApp.Sdk/Media/MediaUploader.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/MessageBuilder.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/ReceiptHandler.cs
  - src/Iwasug.WhatsApp.Sdk/Proto/message.proto
  - src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt
  - src/Iwasug.WhatsApp.Sdk/Safety/SafetyOptions.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaCryptoFixtureTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaDownloadTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaRetryCryptoTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaRetryTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaSafetyTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaSchemaTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaSendTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaTracerTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Media/MediaUploadTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SendPathAuditTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/TestData/media-crypto-fixtures.json
  - tests/Iwasug.WhatsApp.Sdk.Tests/TestData/media-retry-fixtures.json
covered_digest: "v3:sha256:1a5f99de8dab00480afc3eaf2bd564321c6c2fd70fb12bc36cd68a368b491a79"
behavior_unverified: 3
overrides_applied: 0
re_verification:
  previous_status: gaps_found
  previous_score: 3/10
  gaps_closed:
    - "Host downloads inbound media as a verified Stream; hostile media fails closed (MED-02 receive path)"
    - "Expired download URLs recover host-invisibly through capped server-error plus rmr retry with fresh-path re-download (MED-02 retry flow)"
    - "Send-path audit proves every new public send is gated (06-02 must-have)"
    - "One image travels host Stream -> gated E2EE send -> receive branch -> MediaReceived bytes equal original, through the public SendImageAsync API (06-01 tracer truth)"
  gaps_remaining: []
  regressions: []
gaps: []
deferred: []
behavior_unverified_items:
  - truth: "Document and voice-note sends carry correct node attrs; group image fanout rides the sender-key path with mediatype on the skmsg (06-02 truth 1)"
    test: "Send a document and voice note over the loopback harness; send a group image and inspect the signed skmsg."
    expected: "Document node decrypts to DocumentMessage with fileName; voice note carries ptt=true + seconds + mediatype audio; skmsg enc child carries mediatype image; SKDM distributed once per device."
    why_human: "Builder-level attr pins exist (MediaSendTests), but no behavioral loopback fact exercises attr mapping or group fanout end-to-end; code paths are present and wired but unproven."
  - truth: "Expired download URLs recover host-invisibly via capped server-error + rmr retry"
    test: "Loopback: expire a download URL (403), confirm a server-error receipt is sent and a fresh-path rmr completes delivery."
    expected: "One server-error receipt goes out, an rmr with a fresh directPath arrives, media re-downloads and delivers with no host code; fourth attempt is capped."
    why_human: "The recovery exchange is implemented (ReceiptHandler senders + rmr routing + tracker cap) and exercised only by unit-level building blocks; no end-to-end loopback recovery fact exists yet."
  - truth: "Every media send refuses through the gate with zero network; phase docs close the phase"
    test: "Refuse a real media send through a refusing gate and assert zero upload HTTP and zero consumed message id per API."
    expected: "No HTTP call, no consumed id, no SendNodeAsync egress for each of the six media send APIs."
    why_human: "The gated-caller reflection audit exists and a generic SafetyGate refusal fact passes, but per-API refusal with zero network is not behaviorally pinned."
human_verification:
  - test: "MVP goal-format decision"
    expected: "Accept the outcome-form roadmap goal or reformat it as a user story via /gsd-mvp-phase 6."
    why_human: "The phase is marked Mode: mvp but the roadmap goal is not in 'As a ..., I want to ..., so that ...' form; the planning artifact decision is a human call."
  - test: "Live send trio"
    expected: "Image, document, and voice note sent via the public APIs open intact on a peer phone with caption/fileName/ptt metadata."
    why_human: "Requires a linked real device; loopback tests cannot substitute for live wire behavior (06-UAT.md: NOT VERIFIED)."
  - test: "Expire-and-recover live"
    expected: "Expired download URL triggers one server-error receipt and an rmr fresh-path re-download, delivering media with no host code."
    why_human: "Requires a live peer and the retry exchange over the real network."
  - test: "Wire-attr correctness live"
    expected: "Peers render each media kind with the correct type label; voice notes register as push-to-talk."
    why_human: "Node-attr semantics (mediatype tokens, ptt) can only be confirmed against the real WhatsApp client."
---

# Phase 6: Media Pipeline Verification Report

**Phase Goal:** Host exchanges image, document, and voice-note media through the same gated send path.
**Verified:** 2026-10-15T17:00:00Z
**Status:** human_needed
**Re-verification:** Yes - after gap closure (media-retry recovery path + receive-side + audit)

## MVP Mode Note

Phase 6 is flagged `Mode: mvp` in ROADMAP.md but the roadmap goal is not in user-story form (`user-story.validate` -> invalid). Automated verification used the roadmap success criteria and plan-objective user stories. The reformat-vs-accept decision is routed to human verification.

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Media encrypt/decrypt round-trips match golden hash/MAC fixtures; tampered blobs fail closed | ✓ VERIFIED | MediaCryptoFixtureTests pass; VerifyAndDecrypt enforces hash -> MAC -> plain-hash order; 40/40 media tests green |
| 2 | message.proto carries imageMessage=3, documentMessage=7, audioMessage=8; no video/sticker/link-preview branches | ✓ VERIFIED | message.proto lines 132-135; repo-wide grep for excluded branches -> zero in src/ |
| 3 | One image travels host Stream -> gated E2EE send -> scripted peer -> stub GET -> MediaReceived bytes equal original via SendImageAsync | ✓ VERIFIED | MediaTracerTests.FullSendReceiveRoundTrip passes ( send, decrypt, inbound media node, MediaReceived event, byte equality) |
| 4 | Every new public media symbol declared in PublicAPI.Unshipped.txt; build warning-free | ✓ VERIFIED | PublicAPI.Unshipped.txt entries; `dotnet build ... -warnaserror` -> 0 warnings, 0 errors |
| 5 | Document/voice-note sends carry correct attrs; group fanout rides sender-key path with mediatype on skmsg | ⚠️ PRESENT_BEHAVIOR_UNVERIFIED | Code wired (Messenger.cs:251-341, MessageSender.cs skmsgAttrs); MediaSendTests covers builder attrs only, no end-to-end loopback attr/fanout fact |
| 6 | Upload resolves media-conn over IQ seam with TTL refresh + host rotation; refused sends never reach network | ✓ VERIFIED | MediaUploadTests (rotation byte-identity, TTL force-refresh) pass; SafetyGate wraps every upload in ExecuteSendAsync |
| 7 | Byte caps enforced before encryption; send-path audit proves every public send gated | ✓ VERIFIED | `MediaUploadTests.OversizeContentThrowsBeforeUpload` green; `SendPathAuditTests.every_public_media_send_method_is_safety_gated` green (IL scan over six APIs) |
| 8 | Host downloads inbound media as verified Stream; tampered/oversize/traversal/executable mimetype fail closed | ✓ VERIFIED | Dispatcher media branches for image/document/audio; MediaMetadata.ValidateReceive + MediaDownloader verify; MediaDownloadTests inbound image/document/voice receive facts pass |
| 9 | Expired download URLs recover host-invisibly via capped server-error + rmr retry | ⚠️ PRESENT_BEHAVIOR_UNVERIFIED | Implemented: ReceiptHandler senders + rmr routing before Pattern-5 + tracker cap (MediaRetryTests pass); no end-to-end expired-recovery loopback fact yet |
| 10 | Every media send refuses through the gate with zero network; phase docs close the phase | ⚠️ PRESENT_BEHAVIOR_UNVERIFIED | SendPathAuditTests reflection fact + one generic safety refusal fact present; per-API zero-HTTP refusal not pinned; docs (06-UAT.md, NOTICE, README) verified |

**Score:** 7/10 truths verified (3 present, behavior-unverified)

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
| ----------- | ---------- | ----------- | ------ | -------- |
| MED-01 | 06-01..06 | Host can send image, document, and voice note | ✓ SATISFIED | Six public gated send APIs, upload inside ExecuteSendAsync, media-conn IQ seam, caps, audit IL scan |
| MED-02 | 06-03..06-05 | Host can receive/download media incl. download-expiry handling and media-retry | ✓ SATISFIED (automated) | Dispatcher media branches, verified streams, fail-closed validation, expired->server-error->rmr->fresh-path implementation, unit/audit tests |

No orphaned requirements.

### Deferred Items

None.

### Advisory (New Scope, Unevidenced)

None - re-verification, no unevidenced new-scope findings.

### Required Artifacts

| Artifact | Expected | Status | Details |
| -------- | -------- | ------ | ------- |
| MediaCrypto.cs | HKDF-112 envelope, triple-gate verify | ✓ VERIFIED | Wired, fixture-tested, all hashes match |
| message.proto media branches | image/document/audio + retry messages | ✓ VERIFIED | Additive only; baselines pinned by MediaSchemaTests |
| MediaConn.cs | IQ TTL cache + host rotation | ✓ VERIFIED | Rotation/TTL behaviorally pinned by MediaUploadTests |
| MediaUploader.cs | Conn-backed upload inside gate | ✓ VERIFIED | rotation + oversize + empty tests pass |
| MediaDownloader.cs | Verified streaming download | ✓ WIRED | Used by dispatcher media branches; unit+receive tests pass |
| MediaMetadata.cs | Receive fail-closed validation | ✓ WIRED | Used in dispatcher media branch before any network call |
| MediaRetryTracker.cs | Capped per-msg-id retry counter | ✓ WIRED | Used by ReceiptHandler.SendMediaRetryReceiptAsync; cap/routing tests pass |
| MediaEvents.cs | MediaReceived public event | ✓ VERIFIED | Public records; event raised in both 1:1 and group paths |
| SendPathAuditTests.cs | Egress inventory + gated-caller fact | ✓ VERIFIED | Both reflection facts present and green |
| MediaSchemaTests.cs | Schema pins (de-tautologized) | ✓ VERIFIED | No tautological asserts; branch numbers pinned |

### Key Link Verification

| From | To | Via | Status | Details |
| ---- | --- | --- | ------ | ------- |
| Messenger media APIs | ISafetyGate.ExecuteSendAsync -> MediaUploader -> MessageSender | gate-wrapped delegate, upload inside | ✓ WIRED | Messenger.cs:230-341; audit IL scan passes |
| MediaConn IQ seam | TTL cache -> uploader host selection | func snapshot + force-refresh | ✓ WIRED | MediaUploader.cs:80-153; behaviorally tested |
| SendPathAuditTests reflection | every public media send calls ExecuteSendAsync | IL token scan | ✓ WIRED | Fact present and green (gap 3 closed) |
| MessageDispatcher receipt path | rmr check -> media-retry handler | rmr child presence before type=retry | ✓ WIRED | HandleReceipt checks rmr before Pattern-5 (lines 605-612/910+) |
| MediaRetryTracker cap | server-error receipt -> fresh path -> downloader -> event | retry exchange chain | ✓ WIRED | TryReserve + BuildServerErrorReceiptNode + TryHandleMediaRetryReceiptAsync |
| MessageDispatcher media branch | MediaDownloader -> verify -> MediaReceived | receive pipeline | ✓ WIRED | Branches for image/document/audio in 1:1 and group paths, fail-closed |
| Messenger.MediaReceived | RaiseMediaReceivedAsync -> PublishAsync | event twin | ✓ WIRED | Implementation follows MessageReceived shape |

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
| -------- | ------------- | ------ | ------------------ | ------ |
| SendImageAsync pipeline | MediaUploadResult | Live mmg POST via MediaUploader -> MediaConn IQ | Yes - real HTTP with server-returned url/direct_path | ✓ FLOWING |
| MessageBuilder media builders | MediaUploadResult | Uploader output (real mediaKey/hashes) | Yes - real crypto material flows through | ✓ FLOWING |
| MediaReceived event | Stream Content | MediaDownloader over directPath | Yes - verified plaintext stream after full chain (MediaDownloadTests/FullSendReceiveRoundTrip) | ✓ FLOWING |
| MediaDownloader output | plaintext Stream | Encrypted mmg blob -> decrypt+verify | Yes - SHA-256/plain-hash gated; fail-closed on tamper | ✓ FLOWING |

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
| -------- | ------- | ------ | ------ |
| Warning-free build | `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror` | 0 warnings, 0 errors | ✓ PASS |
| Full test suite | `dotnet test Iwasug.WhatsApp.Sdk.sln` | 635 passed, 4 failed | ⚠️ 4 pre-existing PairingLifecycleTests environmental timeouts (accepted baseline) |
| Media filter | `dotnet test ... --filter "FullyQualifiedName~Media"` | 40/40 passed | ✓ PASS |
| Send-path audit | `SendPathAuditTests` | 2/2 passed | ✓ PASS |
| Full send/receive tracer | `MediaTracerTests.FullSendReceiveRoundTrip` | passed (byte equality) | ✓ PASS |
| Inbound receive kinds | `MediaDownloadTests.Inbound*` (3 facts) | passed | ✓ PASS |
| No excluded media v2 surface | grep video/sticker/linkPreview/viewOnce in src | zero | ✓ PASS |

### Probe Execution

No probes declared for this phase - N/A.

### Anti-Patterns Found

None in phase-modified source files (no TBD/FIXME/XXX; tautological schema asserts removed).

### Human Verification Required

See `human_verification` frontmatter - 4 items: MVP goal-format decision, live send trio, expire-and-recover live, and live wire-attr rendering.

### Gaps Summary

No code gaps remain from the initial verification. The four recorded gaps (inbound receive pipeline, expired-URL media-retry recovery, gated-send audit proof, and the end-to-end tracer) are closed by plans 06-04/06-05/06-06, with build warnaserror clean and the media test filter fully green. Three truths remain presence/behavior-unverified because their only evidence is structural, builder-level, or unit-level (attrs/fanout, E2E expired recovery, per-API refusal). Live wire/rendering behavior remains for human verification.

---

_Verified: 2026-10-15T17:00:00Z_
_Verifier: the agent (gsd-verifier)_
