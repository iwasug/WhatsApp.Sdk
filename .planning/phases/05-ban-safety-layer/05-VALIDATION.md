---
phase: "05"
slug: "ban-safety-layer"
status: draft
nyquist_compliant: false
wave_0_complete: false
created: "2026-10-14"
---

# Phase 05 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 4.0.1 (Microsoft.NET.Test.Sdk 18.10.1) |
| **Config file** | none designated (tests under `tests/Iwasug.WhatsApp.Sdk.Tests`) |
| **Quick run command** | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~Safety"` |
| **Full suite command** | `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror --nologo && dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` |
| **Clock seam** | `TimeProvider` (`_timeProvider`), already injected across MessageSender/WaConnection |
| **Fakes** | NSubstitute 6.2.0 for stores and gate dependencies |
| **Estimated runtime** | ~120 seconds (full suite) |

---

## Sampling Rate

- **After every task commit:** Run `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror --nologo`
- **After every plan wave:** Run `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests`
- **Before `/gsd-verify-work`:** Full suite must be green
- **Max feedback latency:** 300 seconds (full suite; quick safety filter < 30 s)

---

## Per-Task Verification Map

Seeded from research `05-RESEARCH.md § Validation Architecture`. Planner fills in per PLAN task IDs.

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 05-01-01 | 01 | 1 | SAF-01 | T-05-01 | every public send awaits `_safety.ExecuteSendAsync` before inner send | unit | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "SendPathAuditTests"` | ❌ W0 | ⬜ pending |
| 05-01-02 | 01 | 1 | SAF-01 | T-05-01 | static audit: only whitelisted protocol egress may call `SendNodeAsync` | static | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "SendPathAuditTests"` | ❌ W0 | ⬜ pending |
| 05-02-01 | 02 | 1 | SAF-02 | - | token windows reject at caps 20/min 250/hr 1000/day and acquire under caps | unit | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "TokenBucketRateLimitTests"` | ❌ W0 | ⬜ pending |
| 05-02-02 | 02 | 1 | SAF-02 | - | jitter stays inside configured bounds | unit | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "SafetyJitterTests"` | ❌ W0 | ⬜ pending |
| 05-03-01 | 03 | 1 | SAF-03 | - | account age reads from persisted safety state on restart | contract/integration | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "WarmupPersistenceTests"` | ❌ W0 | ⬜ pending |
| 05-03-02 | 03 | 1 | SAF-03 | - | new account starts first warm-up stage and advances by elapsed age | unit | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "WarmupLadderTests"` | ❌ W0 | ⬜ pending |
| 05-04-01 | 04 | 1 | SAF-04 | - | first contact to a chat counts against cold budget; blocked at cap before inner send | unit | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "ColdOutreachBudgetTests"` | ❌ W0 | ⬜ pending |
| 05-04-02 | 04 | 1 | SAF-04 | - | inbound reply does not consume the protected cold budget | unit | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "ColdOutreachReplyExemptionTests"` | ❌ W0 | ⬜ pending |
| 05-05-01 | 05 | 2 | SAF-05 | - | repeated failures open circuit and apply exponential backoff | unit | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "SafetyCircuitBreakerTests"` | ❌ W0 | ⬜ pending |
| 05-06-01 | 06 | 2 | SAF-06 | - | sends outside the configured active-hours window throw a safety-violation exception | unit | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "HumanPacingTests"` | ❌ W0 | ⬜ pending |
| 05-07-01 | 07 | 2 | SAF-07 | - | lowering protected settings requires `AllowUnsafeThroughput = true` | unit | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "SafetyOptionsValidationTests"` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SafetyGateTests.cs`
- [ ] `tests/Iwasug.WhatsApp.Sdk.Tests/Safety/TokenBucketRateLimitTests.cs`
- [ ] `tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SafetyCircuitBreakerTests.cs`
- [ ] `tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SafetyOptionsValidationTests.cs`
- [ ] `tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SendPathAuditTests.cs`
- [ ] `tests/Iwasug.WhatsApp.Sdk.Tests/Stores/IRecipientContactStoreContractTests.cs` (if recipient ledger seam approved)
- [ ] Gate public-surface guard in `PublicAPI.Unshipped.txt`

*If none: "Existing infrastructure covers all phase requirements."*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Honest risk-positions statement in README + NOTICE audit | SAF-07 | documentation, not a test | review `.planning`/README risk ledger section for "what the layer does and does NOT prevent" |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 300s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
