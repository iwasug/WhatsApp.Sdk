---
phase: "3"
slug: "pairing-session-durability"
# status lifecycle: draft (seeded by plan-phase) → validated (set by validate-phase §6)
# audit-milestone §5.5 distinguishes NOT-VALIDATED (draft) from PARTIAL (validated + nyquist_compliant: false) (#2117)
status: draft
nyquist_compliant: false
wave_0_complete: false
created: "2026-10-09"
---

# Phase 3 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Seeded from 03-RESEARCH.md ## Validation Architecture (Nyquist gate enabled; security_enforcement true, ASVS L1).

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 4.0.1 + Microsoft.NET.Test.Sdk 18.10.1 + AwesomeAssertions 9.6.0 + NSubstitute 6.2.0 |
| **Config file** | existing `tests/Iwasug.WhatsApp.Sdk.Tests` (SDK-style csproj + central package management; extend, don't create new project) |
| **Quick run command** | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~Pairing"` |
| **Full suite command** | `dotnet test Iwasug.WhatsApp.Sdk.sln` |
| **Estimated runtime** | ~120 seconds |

---

## Sampling Rate

- **After every task commit:** `dotnet build Iwasug.WhatsApp.Sdk.sln --nologo` + filtered `dotnet test` (<30s)
- **After every plan wave:** `dotnet test Iwasug.WhatsApp.Sdk.sln` (full)
- **Before `/gsd-verify-work`:** Full suite must be green
- **Max feedback latency:** 120 seconds

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 3-seed-01 | TBD | 1 | CONN-01 | T-3-01 | QR payload format + 60s/20s rotation + ~160s `timeout` | unit (FakeTimeProvider) | `dotnet test --filter "FullyQualifiedName~PairingQr"` | ❌ W0 | ⬜ pending |
| 3-seed-02 | TBD | 1 | CONN-01 | T-3-02 | Typed event sequence `code→success` / `code→timeout` / `→client_outdated(405)` / `→logged_out` on scripted fake server | integration (loopback) | `dotnet test --filter "FullyQualifiedName~PairingLifecycle"` | ❌ W0 | ⬜ pending |
| 3-seed-03 | TBD | 1 | CONN-01 | T-3-03 | `ReconnectPolicy` maps 405 → Terminal (no unknown-code retry loop) | unit | `dotnet test --filter "FullyQualifiedName~ReconnectPolicyTests"` | ✅ exists, needs new case | ⬜ pending |
| 3-seed-04 | TBD | 2 | CONN-02 | T-3-04 | 5B→8C code, alphabet, `XXXX-XXXX`, PBKDF2/CTR/HKDF KATs, hello/finish node shapes | unit + KAT | `dotnet test --filter "FullyQualifiedName~LinkCode"` | ❌ W0 | ⬜ pending |
| 3-seed-05 | TBD | 2 | CONN-02 | T-3-05 | Phone-number validation (digits, >6, no leading 0); secrets never logged | unit | `dotnet test --filter "FullyQualifiedName~LinkCode"` | ❌ W0 | ⬜ pending |
| 3-seed-06 | TBD | 3 | CONN-03 | T-3-06 | pair → dispose → reload from store → login payload → `<success>` → stays up through injected drop + 515 | integration (loopback) | `dotnet test --filter "FullyQualifiedName~Resume"` | ❌ W0 | ⬜ pending |
| 3-seed-07 | TBD | 3 | CONN-03 | T-3-07 | Credential store contract (durability, failure≠missing, idempotent remove) | contract kit | `dotnet test --filter "FullyQualifiedName~CredentialStore"` | ❌ W0 (pattern exists for 5 stores) | ⬜ pending |
| 3-seed-08 | TBD | 3 | all | — | Public API surface additions tracked | analyzer gate | included in build (`dotnet build ... -warnaserror`) | ✅ `PublicAPI.Unshipped.txt` | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*
*Note: Task IDs are seeds. The planner replaces them with concrete plan task IDs; requirement + threat-ref + command columns carry over.*

---

## Wave 0 Requirements

- [ ] `tests/…/Pairing/` — QR payload, rotation timers, lifecycle event sequences, code-gen + crypto KATs (REQ CONN-01/02)
- [ ] `tests/…/Connection/ResumeTests.cs` — restart-without-re-pair + injected-drop survival (CONN-03)
- [ ] `src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/CredentialStoreContractTests.cs` + InMemory/Scratch implementations (completeness backstop `ComplianceTestBase` requires a concrete subclass per contract class)
- [ ] Scripted fake-server pairing peer (extends existing loopback: `pair-device` refs, `pair-success`, `<success>`, `<failure reason=405>`, `link_code_companion_reg` notification)
- [ ] Golden node fixtures: registration/login `ClientPayload` bytes, `companion_hello`/`companion_finish` nodes, `pair-success` container (cross-checked against upstream encodings, **no upstream bytes copied into the repo**)
- [ ] No framework install required (xunit v3 already wired)

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Real QR scan links a live account | CONN-01 | No WhatsApp account/phone in CI | `human_verify_mode: end-of-phase` — run sample host, scan code, observe typed lifecycle events |
| Real 8-char pairing code entry on phone | CONN-02 | No live phone in CI | End-of-phase: request code on headless host, enter on phone, observe `success` |
| Kill/restart process resumes without re-pairing | CONN-03 | Requires live linked session | End-of-phase: link, kill process, restart, confirm resume + survival through injected drop |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 120s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
