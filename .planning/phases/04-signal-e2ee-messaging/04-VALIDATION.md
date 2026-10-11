---
phase: "4"
slug: "signal-e2ee-messaging"
# status lifecycle: draft (seeded by plan-phase) → validated (set by validate-phase §6)
# audit-milestone §5.5 distinguishes NOT-VALIDATED (draft) from PARTIAL (validated + nyquist_compliant: false) (#2117)
status: draft
nyquist_compliant: true
wave_0_complete: true
created: "2026-10-11"
---

# Phase 4 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Seeded from 04-RESEARCH.md ## Validation Architecture (Nyquist gate enabled; security_enforcement true, ASVS L1).

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 4.0.1 + Microsoft.NET.Test.Sdk 18.10.1 + AwesomeAssertions 9.6.0 + NSubstitute 6.2.0 |
| **Config file** | existing `tests/Iwasug.WhatsApp.Sdk.Tests` (SDK-style csproj + central package management; extend, don't create new project) |
| **Quick run command** | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~<Area>"` |
| **Full suite command** | `dotnet test Iwasug.WhatsApp.Sdk.sln` |
| **Estimated runtime** | ~120 seconds |

---

## Sampling Rate

- **After every task commit:** `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror --nologo` + full suite (repo is fast; full suite is the per-commit gate per research)
- **After every plan wave:** `dotnet test Iwasug.WhatsApp.Sdk.sln` (full) + NOTICE provenance-row audit (mirror 03-07 pattern)
- **Before `/gsd-verify-work`:** Full suite green + live UAT with a real account for SC 1–5 (document NOT VERIFIED if unavailable)
- **Max feedback latency:** 120 seconds

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 04-01-T1 + 04-01-T2 + 04-02-T1 + 04-03-T1 | 04-01, 04-02, 04-03 | 1, 2, 3 | MSG-01 | T-4-01, T-4-02 (04-01), T-4-05 (04-02), T-4-08 (04-03) | X3DH+Ratchet roundtrip; 1:1 send/receive E2EE; plaintext never appears in wire bytes | unit + golden-fixture | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~SignalCoreTests"` + `--filter "FullyQualifiedName~MessageWireFixtureTests"` | ✅ `SignalCoreTests.cs`, `SignalSessionTests.cs`, `MessagingLoopbackTests.cs`, `signal-kats.json`, `message-wire-fixtures.json` (created by 04-01-T1, 04-02-T1) | ✅ green |
| 04-05-T1 | 04-05 | 5 | MSG-02 | T-4-15, T-4-16 (04-05), T-4-11 (04-03 own-device fanout) | SKDM distribution, skmsg encrypt/decrypt, phash determinism | unit | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~SenderKeyTests"` | ✅ `SenderKeyTests.cs` (created by 04-05-T1) | ✅ green |
| 04-03-T2 | 04-03 | 3 | MSG-03 | T-4-10, T-4-09 (04-03) | id format (3EB0/40-char), send→ack correlation, timeout=one outcome, no auto-retry | unit (FakeTimeProvider) | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~MessageCorrelationTests"` | ✅ `MessageCorrelationTests.cs` (created by 04-03-T2) | ✅ green |
| 04-04-T1 | 04-04 | 4 | MSG-04 | T-4-14 (04-04) | receipt node shapes, MarkRead per-message keys, inbound receipt parse | unit + wire fixture | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~ReceiptTests"` | ✅ `ReceiptTests.cs` (created by 04-04-T1) | ✅ green |
| 04-02-T3 + 04-03-T3 + 04-04-T3 | 04-02, 04-03, 04-04 | 2, 3, 4 | MSG-05 | T-4-05, T-4-07 (04-02) | quote build from recent cache; cache write policy per D-20 | unit | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~QuotingTests"` + `--filter "FullyQualifiedName~RecentCachePolicyTests"` | ✅ `QuotingTests.cs`, `RecentCachePolicyTests.cs`, extended `RecentMessageEntry` contract facts (created by 04-04-T3, 04-03-T3, 04-02-T3) | ✅ green |
| 04-04-T2 | 04-04 | 4 | MSG-06 | T-4-12, T-4-13 (04-04), T-4-09 (04-03 no-loss) | retry receipt emit (count cap, keys node), retry-resend from cache, no loss on decrypt fail | unit + loopback | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~RetryReceiptTests"` | ✅ `RetryReceiptTests.cs` (created by 04-04-T2) | ✅ green |
| 04-03-T1 + 04-05-T2 | 04-03, 04-05 | 3, 5 | MSG-07 | T-4-17 (04-05) | typed events, CancellationToken cancels enumeration + in-flight ops | unit | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~MessagingEventTests"` | ✅ `MessagingEventTests.cs` (created by 04-05-T2; EventHandler twin from 04-03-T1) | ✅ green |
| all plans | 04-01 … 04-05 | 1–5 | all | T-4-SC | Public API surface additions tracked | analyzer gate | included in build (`dotnet build ... -warnaserror`) | ✅ `PublicAPI.Unshipped.txt` | ✅ green |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*
- **Run date:** 2026-10-12 (04-05 Task 3 closeout gate)
- **Build:** `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror --nologo` → **0 Warning(s), 0 Error(s)** ✅
- **Suite:** full suite → **523 total, 4 failed, 0 skipped** ⚠️ — the 4 failures are **pre-existing Phase-3 `PairingLifecycleTests` timeouts** documented as Known Pre-existing Failures in 04-04-SUMMARY (not introduced by any phase-4 task); **0 skipped**, and **every MSG-01…MSG-07 test class is green**.
- **Invocation note:** in this execution environment `dotnet test Iwasug.WhatsApp.Sdk.sln` discovers 0 tests (exit 5); executed evidence uses the compiled runner `dotnet exec tests/Iwasug.WhatsApp.Sdk.Tests/bin/Debug/net10.0/Iwasug.WhatsApp.Sdk.Tests.dll` (full suite, or `-class <FQN>` per class). The plan's `dotnet test` verify therefore cannot report exit 0 here — reported, not papered over (03-VALIDATION precedent).
- **Threat refs:** every row's threat refs match its declaring plan's `<threat_model>` register (04-01 owns T-4-01–T-4-04, 04-02 T-4-05–T-4-07, 04-03 T-4-08–T-4-11, 04-04 T-4-12–T-4-14, 04-05 T-4-15–T-4-17) ✅
- **Task ID mappings:** every row names task IDs that exist in its referenced plan files ✅
*Note: Task IDs are concrete plan task references (`04-NN-TM`, plan 04, task M). Threat refs match each declaring plan's `<threat_model>` register; wave columns follow plan frontmatter (`04-01`→wave 1 … `04-05`→wave 5).*

---

## Wave 0 Requirements

> Wave 0 is created by plan tasks, not a separate wave: each checklist item names the artifact a plan task produces during execution. The Signal byte-layout spike (RESEARCH O8) is a **blocking first task** — its fixture set gates every downstream Signal task.

- [x] `tests/…/TestData/signal-kats.json` — Signal KAT fixture set from the blocking Wave-0 spike (**produced by 04-01-T1**; live-capture / Baileys-cross-checked golden bytes — Pitfall 1)
- [x] `message-wire-fixtures.json` — message-node + protobuf golden bytes (**produced by 04-02-T1**) and receipt-node byte forms (**produced by 04-04-T1**), source: Baileys encode cross-check or live capture; the retry-node shape is asserted dynamically by `RetryReceiptTests` (**produced by 04-04-T2**) rather than pinned in the fixture file
- [x] Loopback peer extensions for `message`/`receipt`/`ack` stanza scripting (**produced by 04-03-T1** — the `MessagingLoopbackTests` scripted peer, later extended by 04-04-T1/T2 and 04-05-T1)
- [x] `RecentMessageEntry` additive extension decision (content + sender fields) — new contract-kit assertions in `StoreContract` tests (**produced by 04-02-T3**; executed 28/28 across the concrete bindings)
- [x] No framework install required (xunit v3 already wired — no producing task)

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Real 1:1 E2EE send/receive with a live account (SC 1) | MSG-01 | No WhatsApp account/phone in CI | `human_verify_mode: end-of-phase` — run sample host against a real linked account, confirm delivery + no plaintext on wire (fixture-corroborated) |
| Real group send to a live group (SC 2) | MSG-02 | Requires live group membership | End-of-phase: send to a real group the account belongs to, confirm receipt on peer device |
| Read-receipt round trip + quoted reply rendering (SC 4) | MSG-04, MSG-05 | Cross-app rendering can't be asserted in CI | End-of-phase: mark read by per-message key, reply quoting a message, confirm on phone |

---

## UAT Outcomes (04-05 Task 3)

The sample host's messaging mode (`--send <jid> <text>`, `--reply <jid> <quoted-id> <text>`, `--listen`, `--account <id>`, all with `--store <dir>`) is built and documented in README, and its argument-validation paths were exercised (missing `--store` → error, empty store → "no paired credentials", `--self-test` regression green). Live-device execution:

**UAT-1 (SC 1, 1:1 send/receive against a phone):** NOT VERIFIED — no real WhatsApp account/phone available in execution environment. [Reason: No phone/account in CI — per `human_verify_mode: end-of-phase` config; the automated proof is `MessagingLoopbackTests` + `MessageWireFixtureTests`]

**UAT-2 (SC 2, group send to a real group):** NOT VERIFIED — no live group membership available. [Reason: No phone/account in CI — per `human_verify_mode: end-of-phase` config; the automated proof is `SenderKeyTests` 8/8 on the scripted peer]

**UAT-3 (SC 3, receipt round trip — delivery + read by per-message key):** NOT VERIFIED — cross-device receipt states can't be provoked without a phone. [Reason: No phone/account in CI — per `human_verify_mode: end-of-phase` config; the automated proof is `ReceiptTests` + `MessageCorrelationTests`]

**UAT-4 (SC 4, quoted reply rendering on the phone — also flagged assumption A2):** NOT VERIFIED — quote rendering is a peer-renderer behavior that CI cannot assert. [Reason: No phone/account in CI — per `human_verify_mode: end-of-phase` config; the automated proof is `QuotingTests` (wire-exact `extendedTextMessage` + ContextInfo fixture)]

**UAT-5 (SC 5, retry recovery — fresh linked device, cold store; also flagged assumption A4, sender-stamped `t` accepted by the server):** NOT VERIFIED — provoking a retry exchange requires a second live device under SDK control. [Reason: No phone/account in CI — per `human_verify_mode: end-of-phase` config; the automated proof is `RetryReceiptTests` (emit/cap/keys-node/resend-same-id/wrong-requester/degrade) on the scripted peer]

*All five UAT steps recorded per acceptance criteria. Real-device execution requires a physical WhatsApp phone and account, which is not available in this CI-only environment. The human verification gate (end-of-phase) is the intended path for these steps. No device result is fabricated.*

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references
- [x] No watch-mode flags
- [x] Feedback latency < 120s
- [x] `nyquist_compliant: true` set in frontmatter — every MSG-01…MSG-07 row carries an executed automated verify, and every Manual-Only behavior carries a documented NOT-VERIFIED entry. Known gate caveat, reported not papered over: the full suite carries 4 pre-existing Phase-3 `PairingLifecycleTests` failures and `dotnet test` discovers 0 tests in this environment (see Per-Task Verification Map run-date block); both conditions are pre-existing/environmental, not phase-4 coverage gaps.

**Approval:** pending — closeout evidence recorded by 04-05 Task 3; `status: draft` remains for `/gsd-validate-phase` to adjudicate.
