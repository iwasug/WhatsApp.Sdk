---
phase: "2"
slug: "wire-protocol-connection-resilience"
# status lifecycle: draft (seeded by plan-phase) → validated (set by validate-phase §6)
# audit-milestone §5.5 distinguishes NOT-VALIDATED (draft) from PARTIAL (validated + nyquist_compliant: false) (#2117)
status: draft
nyquist_compliant: false
wave_0_complete: false
created: "2026-10-08"
---

# Phase 2 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Seeded from 02-RESEARCH.md ## Validation Architecture (Nyquist gate enabled; security_enforcement true, ASVS L1).

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 4.0.1 + Microsoft.NET.Test.Sdk 18.10.1 (MTP runner via repo global.json) |
| **Config file** | existing `tests/Iwasug.WhatsApp.Sdk.Tests` (extend, don't create new project) |
| **Quick run command** | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~Noise\|FullyQualifiedName~FrameReader\|FullyQualifiedName~ReconnectPolicy"` |
| **Full suite command** | `dotnet test Iwasug.WhatsApp.Sdk.sln` |
| **Estimated runtime** | ~120 seconds |

---

## Sampling Rate

- **After every task commit:** Run quick run command for the touched area + `dotnet build Iwasug.WhatsApp.Sdk.sln --nologo`
- **After every plan wave:** Run `dotnet test Iwasug.WhatsApp.Sdk.sln`
- **Before `/gsd-verify-work`:** Full suite must be green
- **Max feedback latency:** 120 seconds

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 2-seed-01 | TBD | 1 | CONN-04 | T-2-01 | 401 terminal, never retried; no reconnect timer fires | unit (pure policy fn) + integration (fake transport injects 401) | `dotnet test --filter "FullyQualifiedName~ReconnectPolicy"` | ❌ W0 | ⬜ pending |
| 2-seed-02 | TBD | 1 | CONN-04 | T-2-02 | 408/428 jittered retry; delays within [base, cap], decorrelated | unit (FakeTimeProvider, seed sweep) | `dotnet test --filter "FullyQualifiedName~ReconnectPolicy"` | ❌ W0 | ⬜ pending |
| 2-seed-03 | TBD | 1 | CONN-04 | T-2-03 | 515 immediate restart; fresh Noise handshake, old keys zeroized | integration (duplex-pipe fake) | `dotnet test --filter "FullyQualifiedName~Loopback\|FullyQualifiedName~NoiseHandshake"` | ❌ W0 | ⬜ pending |
| 2-seed-04 | TBD | 1 | CONN-04 | T-2-04 | 440/411 surface-and-stop (no retry loop) | unit + integration (fake transport) | `dotnet test --filter "FullyQualifiedName~ReconnectPolicy"` | ❌ W0 | ⬜ pending |
| 2-seed-05 | TBD | 1 | CONN-04 | T-2-05 | Cacophony XX_AESGCM vectors pass; nonces sequential, never reused | unit (golden JSON fixtures) | `dotnet test --filter "FullyQualifiedName~NoiseVector"` | ❌ W0 | ⬜ pending |
| 2-seed-06 | TBD | 1 | CONN-04 | T-2-06 | Frame codec: golden valid / split / coalesced / evil-length rejected | unit (golden fixtures) | `dotnet test --filter "FullyQualifiedName~FrameReader\|FullyQualifiedName~BinaryNode"` | ❌ W0 | ⬜ pending |
| 2-seed-07 | TBD | 1 | CONN-05 | T-2-07 | State sequence connecting→open→closing→connecting→open; terminal logged_out exactly once | integration (fake transport script) | `dotnet test --filter "FullyQualifiedName~ConnectionState"` | ❌ W0 | ⬜ pending |
| 2-seed-08 | TBD | 1 | CONN-05 | T-2-08 | Query/reply correlation + 60s timeout + late-reply discard | unit (router vs fake transport) | `dotnet test --filter "FullyQualifiedName~QueryRouter"` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*
*Note: Task IDs are seeds. The planner replaces them with concrete plan task IDs; requirement + threat-ref + command columns carry over.*

---

## Wave 0 Requirements

- [ ] `tests/.../NoiseVectorTests.cs` + `TestData/noise-xx-aesgcm-vectors.json` — covers CONN-04 crypto correctness
- [ ] `tests/.../FrameReaderTests.cs` + golden frame fixtures (valid + truncated + evil lengths) — covers CONN-04 framing
- [ ] `tests/.../ReconnectPolicyTests.cs` (FakeTimeProvider-based, no sockets) — covers CONN-04 policy table
- [ ] `tests/.../QueryRouterTests.cs` + `ConnectionStateTests.cs` (fake `IFrameTransport` harness) — covers CONN-05 + round-trip
- [ ] `tests/.../LoopbackTests.cs` (single real-WS `HttpListener` fake server) — proves production transport once
- [ ] `src/` production files per RESEARCH Recommended Project Structure
- [ ] `Directory.Packages.props` additions: NSec.Cryptography 26.4.0, Google.Protobuf 3.36.2, Grpc.Tools 2.84.0 (PrivateAssets=all)

*Existing infrastructure (xunit.v3 + test project + `dotnet test` story from Phase 1) covers the runner; Wave 0 adds the fixtures above.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| — | — | — | All phase behaviors have automated verification (fake transports + one loopback test; no live WhatsApp in CI). |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 120s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
