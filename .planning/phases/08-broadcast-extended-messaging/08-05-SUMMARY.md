---
phase: 08
plan: 05
subsystem: messaging-presence
tags: [presence, pacing, events, messaging]
requires:
  - phase: 08-04
    provides: outbox pump, durable store plumbing
provides:
  - Real PresenceManager implementing IPacingPresence
  - Outbound SendPresenceAsync with account/chat targeting rules
  - Inbound peer presence parsing to PresenceUpdated event
  - PresenceApplied and PresencePacing tests
affects: [messaging, safety, accounts]
decisions:
  - "PresenceManager is account-scoped and retains no static state (Pitfall 6)."
  - "SendComposingAsync best-effort swallows node failures except cancellation."
  - "Inbound presence parse failures are swallowed and never block receive."
key-files:
  created:
    - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/PresencePacingTests.cs
  modified:
    - src/Iwasug.WhatsApp.Sdk/Messaging/Presence/PresenceManager.cs
    - src/Iwasug.WhatsApp.Sdk/Safety/HumanPacing.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/MessagingEvents.cs
requirements-completed: [EXT-02]
actuals:
  tokens: 3700
  tasks: 3
  commits: 1
  plan_head_before: a28a182b417826df2e637bd810ad2edf290a2a25
  plan_head_after: f3c7dd89e11a12bca1b182421aae13d6b0ef1f84
metrics:
  duration: executed
  completed: "2026-10-16"
status: complete
---

# Phase 8 Plan 5: Presence Summary

**Real account-scoped presence manager replacing the no-op pacing seam with outbound SendPresenceAsync, inbound peer presence events, and best-effort pacing proofs.**

## Performance

- **Tasks:** 3 completed
- **Files:** 1 created, 5 modified
- **Tests:** `PresenceTests`, `PresencePacingTests`, `SendPathAuditTests`

## Accomplishments

- Upgraded `IPacingPresence` from `NoOpPacingPresence` to `PresenceManager`, emitting composing signals for safety pacing while never failing the send on node faults.
- Added `Messenger.SendPresenceAsync` covering all five `PresenceState` values through the safety gate with applied-state events.
- Parsed inbound `<presence>` and `<chatstate>` nodes in `MessageDispatcher`, raising typed `PresenceUpdatedEventArgs` without blocking receive.
- Added `PresencePacingTests` proving best-effort presence swallows node failures while cancellation continues to propagate.

## Decisions Made

- Presence manager remains instance-owned with no static state to preserve per-account isolation.
- Explicit host presence calls fail closed; only HumanPacing seam calls use best-effort semantics.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

- Pre-existing pairing tests had environment/timing failures unrelated to this plan; omitted from this plan's scope.

## User Setup Required

None - no external service configuration required.

## Self-Check: PASSED

## Next Phase Readiness:

- Presence API ready for broadcast and extended messaging phases.
- Inbound peer typing and availability surfaces are available through Messenger events.

## Self-Check: PASSED