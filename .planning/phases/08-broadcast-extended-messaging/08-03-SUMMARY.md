---
phase: 08-broadcast-extended-messaging
plan: 03
subsystem: stores
tags: [outbox, store-registry, contract-tests, ext-01]
provides:
  - "IOutboxStore wired into StoreRegistryOptions, StoreRegistry, and StoreSession"
  - "Abstract OutboxStoreContractTests contract kit"
  - "In-memory and scratch outbox bindings passing the shared contract suite"
affects: [08-broadcast-extended-messaging, ext-01, store-seam]
actuals:
  tokens: 30000
  tasks: 3
  commits: 3
tech-stack:
  added: []
  patterns:
    - "host-first / in-memory-fallback factory registration (D-03/D-24)"
    - "account-scoped shared backing row for D-06 durability (in-memory reference store)"
key-files:
  created:
    - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/OutboxStoreContractTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/InMemoryOutboxStoreContractTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/ScratchOutboxStoreContractTests.cs
  modified:
    - src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs
    - src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistryOptions.cs
    - src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryOutboxStore.cs
    - src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/ScratchStoreFactory.cs
    - src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt
    - src/Iwasug.WhatsApp.Sdk.Testing/PublicAPI.Unshipped.txt
    - tests/Iwasug.WhatsApp.Sdk.Tests/Safety/ColdOutreachBudgetTests.cs
key-decisions:
  - "Fold IOutboxStore into the store seam using the same host-first/fallback discipline as the other capabilities"
  - "Make the in-memory outbox store share a per-account backing row so D-06 independent-instance durability holds"
# coverage: (#1602) optional per-deliverable UAT-routing block
duration: 30min
completed: 2026-10-16
plan_head_before: 8ee57d2e4179661168e5c9c62c0d16f71cc1e771
plan_head_after: fe7b3fb4434035d9133f0e4a166fe36592efde9f
status: complete
requirements: [EXT-01]
---

# Phase 08 Plan 03: Fold IOutboxStore into the Store System Summary

**IOutboxStore is now a first-class pluggable store seam: registered through StoreRegistryOptions, resolved per account by StoreRegistry, exposed via StoreSession.Outbox, and covered by a shared abstract contract kit with in-memory and scratch bindings.**

## Performance
- **Duration:** 30 min
- **Tasks:** 3
- **Files modified:** 12

## Accomplishments
- Added `StoreRegistryOptions.OutboxFactory` and `StoreRegistry` host-first/fallback resolution plus disposal tracking
- Added `StoreSession.Outbox` property
- Created abstract `OutboxStoreContractTests` (11 facts: round-trip, MarkSent, ScheduleRetry, DeadLetter, TryRemove, missing handling, isolation, D-06 durability, parallel)
- Bound contract to `InMemoryStoreFactory` and `ScratchStoreFactory`
- Made in-memory outbox store account-scoped shared state for D-06 durability

## Task Commits
1. **Task 1: StoreRegistry options + factory fields** - `682f71d`
2. **Task 2: Abstract outbox contract kit** - `7be462f`
3. **Task 3: Subclasses for in-memory and scratch stores** - `fe7b3fb`

## Files Created/Modified
- `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs` - Outbox resolution + StoreSession.Outbox
- `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistryOptions.cs` - OutboxFactory
- `src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryOutboxStore.cs` - Shared-row durability
- `src/Iwasug.WhatsApp.Sdk/Testing/StoreContract/OutboxStoreContractTests.cs` - Abstract kit
- `tests/.../StoreContract/InMemoryOutboxStoreContractTests.cs` - Reference binding
- `tests/.../StoreContract/ScratchOutboxStoreContractTests.cs` - Scratch binding

## Decisions & Deviations
None - followed plan; in-memory reference store adjusted to share account-scoped state so D-06 durability holds.

## Next Phase Readiness
Outbox seam ready for pump implementation in 08-04.