---
phase: 08
plan: 01
subsystem: messaging-safety-stores
tags: [broadcast, presence, outbox, tracer, safety-gate]
requires: []
provides:
  - SendBroadcastAsync
  - SendPresenceAsync
  - PresenceState
  - PresenceManager
  - IOutboxStore
  - IOutboxStoreFactory
  - InMemoryOutboxStore
key-files:
  modified:
    - src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/MessagingEvents.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs
    - src/Iwasug.WhatsApp.Sdk/Safety/SafetyGate.cs
    - src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SendPathAuditTests.cs
  created:
    - src/Iwasug.WhatsApp.Sdk/Messaging/BroadcastTypes.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/Presence/PresenceState.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/Presence/PresenceManager.cs
    - src/Iwasug.WhatsApp.Sdk/Stores/IOutboxStore.cs
    - src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryOutboxStore.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/BroadcastSendTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/PresenceTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Stores/OutboxStoreTests.cs
decisions:
  - "PresenceManager is account-scoped (instance-owned) and implements IPacingPresence; outgoing pacing upgraded to use it."
  - "Outbox payloads carry descriptors only; media bytes never enter durable records."
  - "Broadcast fan-out stays sequential and per-recipient gated."
metrics:
  duration: "tracer"
  completed: "2026-10-16"
status: complete
estimateTokens: 60000
actuals:
  tokens: 60000
  tasks: 3
  commits: 3
plan_head_before: cc31b44ae27f911f1fccacdcce992b68d73fd3fd
plan_head_after: 1c5d32548b6a68a8615d5131d303b489397b1e2c
---

# Phase 08 Plan 01: Broadcast & Extended Messaging Tracer Summary

**Minimal tracer slice:** one-recipient broadcast fan-out through the safety gate, an in-memory outbox store round-trip, and smoke presence send/receive parse.

## Performance

- **Tasks:** 3 completed (Task 1 tracer, Task 2, Task 3)
- **Files modified:** 17
- **Tests:** BroadcastSendTests, PresenceTests, OutboxStoreTests, SendPathAuditTests green; full suite has only pre-existing pairing flakiness.

## Accomplishments

- Added `Messenger.SendBroadcastAsync` with per-recipient `_safety.ExecuteSendAsync`, lazy `messageFactory`, ordinal JID deduplication, and inter-recipient delay between recipients only.
- Added `SendPresenceAsync` and `PresenceState` (five states), plus a real `PresenceManager: IPacingPresence` wired into both pacing and explicit host sends.
- Added `IOutboxStore` / `IOutboxStoreFactory` primitive seam and `InMemoryOutboxStore` round-trip with `EnqueueAsync`/`GetDueAsync` and state transitions.
- Added inbound `<presence>` / `<chatstate>` best-effort parse in `MessageDispatcher`, raising `PresenceUpdated` without blocking receive; errors swallowed.
- Updated SendPathAudit allow-list with `PresenceManager.SendAsync`.

## Task Commits

1. `a2fddef` - feat(08-01): tracer broadcast presence smoke
2. `17f85b6` - feat(08-01): in-memory outbox store seam round-trip
3. `1c5d325` - feat(08-01): inbound presence parse smoke

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Missing whitelisted egress]** SendPathAuditTests rejected `PresenceManager.SendAsync` as an unlisted `SendNodeAsync` egress site; added it to the allowed set because presence necessarily sends a wire node.

### Known Stubs

None for this tracer slice; protocol field numbers remain [ASSUMED] pending the 08-06 spike.

## Self-Check: PASSED