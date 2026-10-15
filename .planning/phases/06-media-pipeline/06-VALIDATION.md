---
phase: "6"
slug: "media-pipeline"
# status lifecycle: draft (seeded by plan-phase) → validated (set by validate-phase §6)
# audit-milestone §5.5 distinguishes NOT-VALIDATED (draft) from PARTIAL (validated + nyquist_compliant: false) (#2117)
status: validated
nyquist_compliant: true
wave_0_complete: true
created: "2026-10-14"
---

# Phase 6 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Seeded from 06-RESEARCH.md Validation Architecture (2026-10-14).

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 4.0.1 + AwesomeAssertions 9.6.0 + NSubstitute 6.2.0 |
| **Config file** | repo-root `global.json` (MTP mode) |
| **Quick run command** | `dotnet test Iwasug.WhatsApp.Sdk.sln` |
| **Full suite command** | `dotnet test Iwasug.WhatsApp.Sdk.sln` |
| **Estimated runtime** | ~120 seconds |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test Iwasug.WhatsApp.Sdk.sln`
- **After every plan wave:** Run `dotnet test Iwasug.WhatsApp.Sdk.sln`
- **Before `/gsd-verify-work`:** Full suite must be green
- **Max feedback latency:** 120 seconds

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 06-01-T1 | 01 | 1 | MED-01 | T-06-01 | media crypto golden fixtures green, tamper fail-closed | unit + golden-fixture | `dotnet test Iwasug.WhatsApp.Sdk.sln --filter MediaCryptoFixtureTests` | ✅ | ✅ green |
| 06-01-T2 | 01 | 1 | MED-01 | — | schema branches stable, text field numbers unchanged | unit | `dotnet test Iwasug.WhatsApp.Sdk.sln --filter MediaSchemaTests` | ✅ | ✅ green |
| 06-02-T1 | 02 | 2 | MED-01 | T-06-04 | upload URL/headers/body exact over stubbed mmg + conn refresh | unit (HttpMessageHandler stub) | `dotnet test Iwasug.WhatsApp.Sdk.sln --filter MediaUploadTests` | ✅ | ✅ green |
| 06-02-T2 | 02 | 2 | MED-01 | T-06-05 | image/doc/voice-note gated sends, type=media + mediatype attrs, ack correlation | unit + loopback | `dotnet test Iwasug.WhatsApp.Sdk.sln --filter MediaSendTests` | ✅ | ✅ green |
| 06-02-T3 | 02 | 2 | SAF-01 | T-06-04 | send-path audit covers 3 new media APIs; mmg/retry egress whitelisted | static audit | `dotnet test Iwasug.WhatsApp.Sdk.sln --filter SendPathAuditTests` | ✅ | ✅ green |
| 06-03-T1 | 03 | 3 | MED-02 | T-06-01 | download→verify→Stream delivery; tamper/oversize/sanitize fail-closed | unit | `dotnet test Iwasug.WhatsApp.Sdk.sln --filter MediaDownloadTests` | ✅ | ✅ green |
| 06-03-T2 | 03 | 3 | MED-02 | T-06-03 | 403/404/410 → server-error receipt → rmr → fresh-path re-download, host-invisible, capped | unit + loopback | `dotnet test Iwasug.WhatsApp.Sdk.sln --filter MediaRetryTests` | ✅ | ✅ green |
| 06-03-T3 | 03 | 3 | MED-02 | — | retry-request GCM loopback, wrong-AAD fails | unit + golden-fixture | `dotnet test Iwasug.WhatsApp.Sdk.sln --filter MediaRetryCryptoTests` | ✅ | ✅ green |
| 06-03-T4 | 03 | 3 | SAF-01 | T-06-06 | gate refusal → zero HTTP + zero ids; caps enforced pre-upload | unit | `dotnet test Iwasug.WhatsApp.Sdk.sln --filter MediaSafetyTests` | ✅ | ✅ green |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [x] `tests/.../TestData/media-crypto-fixtures.json` — expand vectors + encrypt/hash/MAC goldens + tampers
- [x] `tests/.../TestData/media-retry-fixtures.json` — retryKey hex + GCM loopback + notification proto bytes
- [x] `Media/` production seams (MediaCrypto, MediaConn, MediaUploader/MediaDownloader, MediaRetryTracker)
- [x] `message.proto` media-branch extension (hand-written, provenance header, internal_access)
- [x] Stub mmg `HttpMessageHandler` + fake media-conn IQ + fake `<rmr>` notification loopback harness
- [x] `SendPathAuditTests` extension rows for the three new send APIs + mmg/retry whitelist entries
- [x] `SafetyOptions` byte-cap fields + `PublicAPI.Unshipped.txt` entries for new public surface

*Wave 0 is folded into 06-01 (tracer): no standalone wave-0 plan.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Send trio intact on a real account (image/doc/voice-note open on receiver) | MED-01 SC1 | needs live linked account | Link test account, send one of each type, confirm receipt + open intact |
| Expired-URL recovery on a real account (media-retry without host intervention) | MED-02 SC2 | needs aged/expired direct_path | Send media, wait for URL expiry (or force 404), request download, confirm host-invisible re-fetch |

*If live account unavailable: document NOT VERIFIED (03-07 precedent).*

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references
- [x] No watch-mode flags
- [x] Feedback latency < 120s
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** pending

## Validation Audit 2026-10-15

| Metric | Count |
|---|---|
| Gapsfound | 2 |
| Resolved | 2 |
| Escalated | 0 |
