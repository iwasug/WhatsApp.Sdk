---
phase: "4"
slug: "signal-e2ee-messaging"
# status lifecycle: draft (seeded by plan-phase) → validated (set by validate-phase §6)
# audit-milestone §5.5 distinguishes NOT-VALIDATED (draft) from PARTIAL (validated + nyquist_compliant: false) (#2117)
status: draft
nyquist_compliant: false
wave_0_complete: false
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
| TBD | TBD | TBD | MSG-01 | T-4-NN (plan threat_model) | X3DH+Ratchet roundtrip; 1:1 send/receive E2EE; plaintext never appears in wire bytes | unit + golden-fixture | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~SignalCoreTests"` + `--filter "FullyQualifiedName~MessageWireFixtureTests"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | MSG-02 | T-4-NN (plan threat_model) | SKDM distribution, skmsg encrypt/decrypt, phash determinism | unit | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~SenderKeyTests"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | MSG-03 | T-4-NN (plan threat_model) | id format (3EB0/40-char), send→ack correlation, timeout=one outcome, no auto-retry | unit (FakeTimeProvider) | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~MessageCorrelationTests"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | MSG-04 | T-4-NN (plan threat_model) | receipt node shapes, MarkRead per-message keys, inbound receipt parse | unit + wire fixture | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~ReceiptTests"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | MSG-05 | T-4-NN (plan threat_model) | quote build from recent cache; cache write policy per D-20 | unit | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~QuotingTests"` + `--filter "FullyQualifiedName~RecentCachePolicyTests"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | MSG-06 | T-4-NN (plan threat_model) | retry receipt emit (count cap, keys node), retry-resend from cache, no loss on decrypt fail | unit + loopback | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~RetryReceiptTests"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | MSG-07 | T-4-NN (plan threat_model) | typed events, CancellationToken cancels enumeration + in-flight ops | unit | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~MessagingEventTests"` | ❌ W0 | ⬜ pending |
| all plans | 04-NN | 1–N | all | T-4-SC | Public API surface additions tracked | analyzer gate | included in build (`dotnet build ... -warnaserror`) | ✅ `PublicAPI.Unshipped.txt` | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*
*Note: Task ID, Plan, and Wave columns are seeded TBD and finalized during execution (plan/task IDs don't exist at plan time); threat refs must match each declaring plan's `<threat_model>` register; requirement-level test map comes verbatim from 04-RESEARCH.md ## Validation Architecture.*

---

## Wave 0 Requirements

> Wave 0 is created by plan tasks, not a separate wave: each checklist item names the artifact a plan task produces during execution. The Signal byte-layout spike (RESEARCH O8) is a **blocking first task** — its fixture set gates every downstream Signal task.

- [ ] `tests/…/TestData/signal-kats.json` — Signal KAT fixture set from the blocking Wave-0 spike (live-capture / Baileys-cross-checked golden bytes; overrides any [ASSUMED] byte-layout values — Pitfall 1)
- [ ] `message-wire-fixtures.json` — message-node, receipt-node, retry-node golden bytes (source: Baileys encode cross-check or live capture)
- [ ] Loopback peer extensions for `message`/`receipt`/`ack` stanza scripting
- [ ] `RecentMessageEntry` additive extension decision (content + sender fields) — new contract-kit assertions in `StoreContract` tests
- [ ] No framework install required (xunit v3 already wired)

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Real 1:1 E2EE send/receive with a live account (SC 1) | MSG-01 | No WhatsApp account/phone in CI | `human_verify_mode: end-of-phase` — run sample host against a real linked account, confirm delivery + no plaintext on wire (fixture-corroborated) |
| Real group send to a live group (SC 2) | MSG-02 | Requires live group membership | End-of-phase: send to a real group the account belongs to, confirm receipt on peer device |
| Read-receipt round trip + quoted reply rendering (SC 4) | MSG-04, MSG-05 | Cross-app rendering can't be asserted in CI | End-of-phase: mark read by per-message key, reply quoting a message, confirm on phone |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 120s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
