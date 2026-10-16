---
phase: "8"
slug: "broadcast-extended-messaging"
status: draft
nyquist_compliant: false
wave_0_complete: false
created: "2026-10-16"
---

# Phase 8 - Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 4.0.1 + AwesomeAssertions 9.6.0 + NSubstitute 6.2.0 |
| **Config file** | `global.json` (MTP mode) |
| **Quick run command** | `dotnet test Iwasug.WhatsApp.Sdk.sln --filter "FullyQualifiedName~Broadcast|Outbox|Presence|Reaction|Edit|Delete"` |
| **Full suite command** | `dotnet test Iwasug.WhatsApp.Sdk.sln` |
| **Estimated runtime** | ~120 seconds |

---

## Sampling Rate

- **After every task commit:** Run the filtered Phase 8 quick command.
- **After every plan wave:** Run the full suite.
- **Before `/gsd-verify-work`:** Full suite must be green and NOTICE/provenance rows audited.
- **Max feedback latency:** 120 seconds.

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------|-------------------|-------------|--------|
| 08-01-01 | 01 | 1 | BCT-01 | T-08-01 / - | unit | `dotnet test --filter FullyQualifiedName~BroadcastSendTests` | ❌ Wave 0 | ⏳ pending |
| 08-01-02 | 01 | 1 | BCT-01 | T-08-01 / - | static audit | `dotnet test --filter FullyQualifiedName~SendPathAuditTests` | extend | ⏳ pending |
| 08-01-03 | 01 | 1 | BCT-01 | T-08-01 / - | unit | `dotnet test --filter FullyQualifiedName~BroadcastCancellationTests` | ❌ Wave 0 | ⏳ pending |
| 08-02-01 | 02 | 1 | EXT-01 | T-08-02 / - | unit | `dotnet test --filter FullyQualifiedName~OutboxStoreTests` | ❌ Wave 0 | ⏳ pending |
| 08-02-02 | 02 | 1 | EXT-01 | T-08-02 / - | unit + loopback | `dotnet test --filter FullyQualifiedName~OutboxPumpTests` | ❌ Wave 0 | ⏳ pending |
| 08-02-03 | 02 | 1 | EXT-01 | T-08-02 / - | integration | `dotnet test --filter FullyQualifiedName~OutboxIsolationTests` | ❌ Wave 0 | ⏳ pending |
| 08-03-01 | 03 | 1 | EXT-02 | T-08-03 / - | unit + loopback | `dotnet test --filter FullyQualifiedName~PresenceTests` | ❌ Wave 0 | ⏳ pending |
| 08-03-02 | 03 | 1 | EXT-02 | T-08-03 / - | unit | `dotnet test --filter FullyQualifiedName~PresencePacingTests` | ❌ Wave 0 | ⏳ pending |
| 08-04-01 | 04 | 1 | EXT-03, EXT-04 | T-08-04 / - | unit + loopback | `dotnet test --filter FullyQualifiedName~ReactionTests` | ❌ Wave 0 | ⏳ pending |
| 08-04-02 | 04 | 1 | EXT-04 | T-08-04 / - | unit + loopback | `dotnet test --filter FullyQualifiedName~EditDeleteTests` | ❌ Wave 0 | ⏳ pending |
| 08-05-01 | 05 | 1 | EXT-03, EXT-04 | T-08-05 / - | integration | `dotnet test --filter FullyQualifiedName~ExtendedMessageEventTests` | ❌ Wave 0 | ⏳ pending |
| 08-06-01 | 02 | 1 | EXT-01 | T-08-02 / - | contract kit | `dotnet test --filter FullyQualifiedName~OutboxStoreContracts` | ❌ Wave 0 | ⏳ pending |

*Status: ⏳ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `IOutboxStore` + factory + in-memory implementation + contract kit
- [ ] `OutboxPump` production seam + options
- [ ] `PresenceManager` upgrading `IPacingPresence` (real nodes)
- [ ] `PresenceState` and inbound dispatcher parse for `<presence>` and peer typing
- [ ] `Message` proto additions for reactions, edits, revoke, and presence-bearing branches with provenance header
- [ ] `MessageBuilder` reaction/edit/revoke helpers (pure functions unit-testable)
- [ ] `BroadcastRecipientOutcome`, `BroadcastOptions`, `OutboxEntry`, `OutboxPayload`, `OutboxState`
- [ ] Public messenger extended APIs + `OutboxPump` wiring in `AccountRuntime` / runtime factory
- [ ] Event types and raise helpers for presence, reaction, edit, and delete
- [ ] In-memory store factory update (`InMemoryStoreFactory`) to serve the new seam
- [ ] `SendPathAuditTests` rows for new APIs + plist baseline
- [ ] `PublicAPI.Unshipped.txt` entries for all new surface

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Voice-of-WhatsApp pacing nuance | EXT-02 | Live device sends peer presence | Pair two accounts, send `composing`/`recording`, verify recipient receives each state with expiry operationally reasonable |
| Protocol field numbers | EXT-03, EXT-04 | Requires clean-room Baileys read before lock | Spike result recorded under `08-*SPIKE*.md`; no implementation commits if absent |
| Real-device delete-for-everyone | EXT-04 | Peer visibility follows WhatsApp quirks | Send and delete for everyone in paired account, verify receipt/edit/revoke |

*If none: "All phase behaviors have automated verification."*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 120s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
path: .planning/phases/08-broadcast-extended-messaging/08-VALIDATION.md