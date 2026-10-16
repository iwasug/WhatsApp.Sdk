---
phase: 08
plan: 06
subsystem: messaging-extended
tags: [reaction, edit, delete, protocol, proto, messaging, safety]
requires:
  - phase: 08-05
    provides: presence manager, applied-state events
  - phase: 08-04
    provides: durable outbox pump and store plumbing
provides:
  - ReactionMessage (field 46) and ProtocolMessage (field 12) branches in Proto/message.proto with provenance
  - MessageBuilder.BuildReaction, BuildProtocolEdit, BuildProtocolRevoke
  - Messenger.SendReactionAsync, EditOwnMessageAsync, DeleteForEveryoneAsync
  - ReactionReceived, MessageEdited, MessageDeleted inbound events
  - COVERAGE.md declaring extended message scope
affects: [messaging, safety, dispatcher, stores]
decisions:
  - "Reaction/edit/delete ride the existing gated 1:1 send path; no bypass."
  - "Edit and delete validate cache presence and from-me ownership before node assembly."
  - "Empty emoji is a real gated reaction removal, not a no-op."
key-files:
  created:
    - .planning/phases/08-broadcast-extended-messaging/COVERAGE.md
  modified:
    - src/Iwasug.WhatsApp.Sdk/Proto/message.proto
    - src/Iwasug.WhatsApp.Sdk/Messaging/MessageBuilder.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/MessagingEvents.cs
    - src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt
    - tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SendPathAuditTests.cs
  - NOTICE.md
requirements-completed: [EXT-03, EXT-04]
actuals:
  tokens: 72000
  tasks: 4
  commits: 1
  plan_head_before: e9eaf268d8e740c9d40939a754486a8967b016a8
  plan_head_after: 255da108d1a173dc56836e464ee9465e5d2b75f5
metrics:
  duration: executed
  completed: "2026-10-16"
status: complete
---

# Phase 8 Plan 6: Extended messaging reactions, edits, and deletes summary

**Clean-room Baileys spike (commit 0af2386) pinned ReactionMessage=46 and ProtocolMessage=12 before codegen; added outbound SendReactionAsync/EditOwnMessageAsync/DeleteForEveryoneAsync and inbound ReactionReceived/MessageEdited/MessageDeleted events through the gated E2EE path, plus ownership validation and SendPathAudit coverage.**

## Performance

- **Tasks:** 4 completed
- **Files:** 1 created, 8 modified
- **Tests:** SendPathAuditTests (reaction/edit/delete gated IL reference)

## Accomplishments

- Updated Proto/message.proto and NOTICE.md provenance with the pinned Baileys field numbers.
- Added MessageBuilder.BuildReaction, BuildProtocolEdit, BuildProtocolRevoke returning the corresponding Message branches; empty emoji preserved as a removal.
- Implemented the three public APIs with EnsureOwnPrekeysAsync, cache-based ownership validation before the gate, and `_safety.ExecuteSendAsync`.
- Recognized inbound ReactionMessage and ProtocolMessage (edit/revoke) branches in MessageDispatcher with ack-first, typed-event, cache ordering in both 1:1 and group paths.
- Added ReactionReceivedEventArgs, MessageEditedEventArgs, MessageDeletedEventArgs.
- Updated SendPathAuditTests to require the three new APIs reference the safety gate and wrote COVERAGE.md.

## Decisions Made

- Reactions/edit/delete are ordinary gated 1:1 E2EE sends; no independent egress path.
- Edit and delete require the target to be present in the recent cache and owned by this account (from-me) before node assembly, surfacing SdkFormatException otherwise.
- Protocol types use integer constants ProtocolTypeRevoke=0 and ProtocolTypeMessageEdit=14 matching Baileys.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

- Pre-existing PairingLifecycleTests environment failures (4 tests) remain outside this plan's scope.
- PublicAPI.Unshipped.txt was harvested mechanically for the new public symbols (RS0016).

## User Setup Required

None - no external service configuration required.

## Self-Check: PASSED

Verified build (0 errors, 0 warnings) and SendPathAudit tests pass.

## Next Phase Readiness

- Extended messaging surface ready for phase verification.
- 08-07 inbound validation/audit plan lands on top of these event hooks.

## Self-Check: PASSED