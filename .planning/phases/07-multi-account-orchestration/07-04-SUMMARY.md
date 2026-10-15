---
phase: 07-multi-account-orchestration
plan: 04
status: complete
subsystem: "hosting, accounts"
tags: [multi-account, event-stream, logging, health]
requirements: [MUL-01, MUL-03]
dependency_graph:
  requires: [07-02, 07-03]
  provides: [AccountEvent, AccountScopedLogger, AccountStatus snapshots, fused event stream]
  affects: [IWhatsAppSessionManager, SessionManager]
tech_stack:
  added: []
  patterns: ["per-account fusion via Task.WhenAny", "short-lived BeginScope logging"]
key_files:
  created:
    - src/Iwasug.WhatsApp.Sdk/Hosting/AccountEvent.cs
    - src/Iwasug.WhatsApp.Sdk/Hosting/ScopedLogger.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Hosting/ScopedLoggerTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/EventStreamCancellationTests.cs
  modified:
    - src/Iwasug.WhatsApp.Sdk/Accounts/IWhatsAppSessionManager.cs
    - src/Iwasug.WhatsApp.Sdk/Accounts/SessionManager.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs
    - src/Iwasug.WhatsApp.Sdk/Safety/SafetyGate.cs
    - src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt
decisions:
  - Manager event fusion uses per-account readers combined with Task.WhenAny (no global ordered channel).
  - AccountScopedLogger stamps AccountId via a short-lived BeginScope around each Log call (no ambient AsyncLocal).
  - AccountStatus snapshots remain data-only with rule-only last-error names.
metrics:
  duration: ~1h
  completed: 2026-10-15
  tasks: 3
  files: 9
estimate:
  tokens: 66000
actuals:
  tokens: 66000
  tasks: 3
  commits: 3
commits: 3
plan_head_before: 657ad82848d008b47033a13c7aadc0f71483af8b
plan_head_after: 0d6af3fc59d132d694c64a4121284a3c6d2d8615
---

# Phase 07 Plan 04: Multi-Account Observability Surface Summary

**Tagged manager event fusion, per-account scoped logger, end-to-end cancellation proof, and health snapshots complete the host-visible multi-account surface (MUL-01, MUL-03).**

## Performance

- **Duration:** ~1h
- **Tasks:** 3
- **Files modified:** 9
- **Tests:** EventStreamCancellationTests and ScopedLoggerTests pass (658/662 overall, 4 pre-existing PairingLifecycleTests timeouts unchanged)

## Accomplishments

- Added `AccountEvent` (AccountId + verbatim WhatsAppEvent) and changed `IWhatsAppSessionManager.ReadEventsAsync` to return `IAsyncEnumerable<AccountEvent>`.
- Refactored SessionManager to fuse per-account messenger readers with Task.WhenAny (no global ordered channel).
- Added `AccountScopedLogger<T>`/`ScopedLogger<T>` stamping every entry with `AccountId` via a short-lived BeginScope.
- Updated `AccountStatus` snapshots to carry circuit state from the safety gate.
- Added tests proving tagged delivery, cancellation unwinding, and scoped log tags.

## Task Commits

1. `feat(07-04): tagged manager event fusion with cancellation` (6479319)
2. `feat(07-04): per-account scoped logger surviving async boundaries` (94c161d)
3. `feat(07-04): health status circuit mapping and public API surface` (0d6af3f)

## Deviations from Plan

None - plan executed as written.

## Self-Check: PASSED

- All created files exist.
- All task commits exist on master.
- Build/test filters pass.
