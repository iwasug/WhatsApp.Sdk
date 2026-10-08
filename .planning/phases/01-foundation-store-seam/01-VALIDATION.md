---
phase: "1"
slug: "foundation-store-seam"
# status lifecycle: draft (seeded by plan-phase) → validated (set by validate-phase §6)
# audit-milestone §5.5 distinguishes NOT-VALIDATED (draft) from PARTIAL (validated + nyquist_compliant: false) (#2117)
status: draft
nyquist_compliant: true
wave_0_complete: true
created: "2026-10-07"
---

# Phase 1 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 4.0.1 + Microsoft.NET.Test.Sdk 18.10.1 (MTP runner) |
| **Config file** | none needed at bootstrap; csproj-driven (`tests/Iwasug.WhatsApp.Sdk.Tests/`) |
| **Quick run command** | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` |
| **Full suite command** | `dotnet test` (solution root) |
| **Estimated runtime** | ~5-15 seconds |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests`
- **After every plan wave:** Run `dotnet test` (full suite) + `dotnet build` with warnings-as-errors
- **Before `/gsd-verify-work`:** Full suite must be green
- **Max feedback latency:** 15 seconds

---

## Per-Task Verification Map

> Re-mapped in 01-05-T2 (2026-10-08): provisional IDs (01-01-01..01-02-03) replaced with the
> actual plan/task IDs. Every row below was executed by a green automated run during this
> phase — final gate: `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror` (0W/0E) and
> `dotnet test` (161 passed / 0 failed, 2026-10-08). Plan 01-01 has exactly two tasks (T1/T2);
> the planner's "01-01-T3" reference was a slip for T2.

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 01-01-T1 | 01 | 1 | STR-01, STR-02 | — | Walking skeleton: AccountId → async factory → account-scoped ISignalSessionStore round-trip (D-02/D-06/D-07) | contract | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` | ✅ | ✅ green |
| 01-01-T2 | 01 | 1 | STR-01 | — | Skeleton sealed: solution builds warnings-as-errors, PublicAPI gate clean, kit completeness backstop runs | build+unit | `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror` | ✅ | ✅ green |
| 01-02-T1 | 02 | 2 | STR-01 | — | Four remaining store interfaces + typed domain records + Jid value type (D-01/D-04/D-16) | unit/contract | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` | ✅ | ✅ green |
| 01-02-T2 | 02 | 2 | STR-02 | — | Reference store satisfies all five interfaces; all five contract classes bound (D-03/D-09) | contract (reference binding) | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` | ✅ | ✅ green |
| 01-02-T3 | 02 | 2 | STR-02 | T-01-05 | SAME kit passes against second scratch implementation with zero SDK changes; two-account isolation (D-02/D-10/D-11) | contract (2nd binding) | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` | ✅ | ✅ green |
| 01-03-T1 | 03 | 3 | STR-01 | T-01-09 | Failure ≠ missing mechanical seam: FaultingStoreFactory (None/FailReads/FailWrites → SdkStoreException) + StoreContractRules helpers (D-19/D-21) | contract (fault injection) | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` | ✅ | ✅ green |
| 01-03-T2 | 03 | 3 | STR-01 | T-01-10 | Failure-vs-missing, durability-await (D-06), and parallel no-cross-reads facts wired into all five contract families (D-11) | contract | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` | ✅ | ✅ green |
| 01-04-T1 | 04 | 3 | STR-01 | T-01-13 | StoreRegistry: host-first + in-memory-fallback precedence, once-per-account cache, disposal cascade (D-03/D-23/D-24) | unit | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` | ✅ | ✅ green |
| 01-04-T2 | 04 | 3 | STR-01 | T-01-12 | D-18 bounded durable-write retry (clamp-after-jitter backoff) + D-21 wrap-and-log error surfacing | unit | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` | ✅ | ✅ green |
| 01-04-T3 | 04 | 3 | STR-01 | — | Registry unit proofs: fallback precedence, subset registration, disposal aggregation, uniqueness (D-24/D-26) | unit | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` | ✅ | ✅ green |
| 01-05-T1 | 05 | 4 | STR-01, STR-02 | T-01-16 | Host-style second run: full kit driven against ScratchStoreFactory in ONE flow incl. fault injection + completeness check; zero reference-impl references (success criterion 3 executed) | contract (host-style 2nd run) | `dotnet exec tests/Iwasug.WhatsApp.Sdk.Tests/bin/Debug/net10.0/Iwasug.WhatsApp.Sdk.Tests.dll -method "*Host_style*"` | ✅ | ✅ green |
| 01-05-T2 | 05 | 4 | STR-01, STR-02 | — | Phase gate: warnings-as-errors build clean + full suite green + kit purity (no apphost in kit bin, no external DB/network deps) | build+unit | `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror` + `dotnet test` | ✅ | ✅ green |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [x] `tests/Iwasug.WhatsApp.Sdk.Tests/` project (xunit.v3 + Microsoft.NET.Test.Sdk + AwesomeAssertions + NSubstitute + coverlet.collector) — scaffolded in plan 01-01
- [x] Contract kit abstract classes + `ComplianceTestBase` (kit is itself a deliverable) — 01-01 (session) + 01-02 (four more)
- [x] Fault-injection decorator factory in kit (D-19 tests) — `FaultingStoreFactory` in 01-03
- [x] No framework install needed (dotnet 10 present)

---

## Manual-Only Verifications

*All phase behaviors have automated verification.*

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify — every task in this phase ran `dotnet test` variants (full suite or MTP-dialect filtered runs); no gap
- [x] Wave 0 covers all MISSING references
- [x] No watch-mode flags
- [x] Feedback latency < 15s — full suite ~7.7s wall clock, filtered runs <1s
- [x] Frontmatter `nyquist_compliant` flag set to true (the only occurrence of that flag)

**Approval:** pending — all gate items green as of 01-05-T2; `validate-phase` §6 sets `status: validated`.
