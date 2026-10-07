---
phase: "1"
slug: "foundation-store-seam"
# status lifecycle: draft (seeded by plan-phase) → validated (set by validate-phase §6)
# audit-milestone §5.5 distinguishes NOT-VALIDATED (draft) from PARTIAL (validated + nyquist_compliant: false) (#2117)
status: draft
nyquist_compliant: false
wave_0_complete: false
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

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 01-01-01 | 01 | 1 | STR-01 | — | N/A | unit/contract | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` | ❌ W0 | ⬜ pending |
| 01-01-02 | 01 | 1 | STR-01 | — | Per-account isolation: zero cross-reads | contract | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` | ❌ W0 | ⬜ pending |
| 01-01-03 | 01 | 1 | STR-01 | — | failure ≠ missing: injected failure throws SdkStoreException | contract (fault injection) | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` | ❌ W0 | ⬜ pending |
| 01-01-04 | 01 | 1 | STR-01 | — | Durable write awaited: post-write read observes value | contract | `dotnet test tests/Iwasu.WhatsApp.Sdk.Tests` | ❌ W0 | ⬜ pending |
| 01-01-05 | 01 | 1 | STR-01 | — | Cache trim primitives via SDK-issued commands | contract | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` | ❌ W0 | ⬜ pending |
| 01-01-06 | 01 | 1 | STR-01 | — | Disposal cascade for IAsyncDisposable stores | unit | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` | ❌ W0 | ⬜ pending |
| 01-02-01 | 02 | 1 | STR-02 | — | Reference store satisfies all five interfaces | contract (reference binding) | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` | ❌ W0 | ⬜ pending |
| 01-02-02 | 02 | 1 | STR-02 | — | Same kit passes against second scratch implementation | contract (2nd binding) | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` | ❌ W0 | ⬜ pending |
| 01-02-03 | 02 | 1 | D-16/D-25 | — | Jid parse/round-trip; AccountId validation | unit | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

*Task IDs above are provisional mappings of the RESEARCH.md Phase Requirements → Test Map; the planner should re-map them to actual plan/task IDs when writing PLAN.md files.*

---

## Wave 0 Requirements

- [ ] `tests/Iwasug.WhatsApp.Sdk.Tests/` project (xunit.v3 + Microsoft.NET.Test.Sdk + AwesomeAssertions + NSubstitute + coverlet.collector) — scaffold in first plan
- [ ] Contract kit abstract classes + `ComplianceTestBase` (kit is itself a deliverable)
- [ ] Fault-injection decorator factory in kit (D-19 tests)
- [ ] No framework install needed (dotnet 10 present)

---

## Manual-Only Verifications

*All phase behaviors have automated verification.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 15s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
