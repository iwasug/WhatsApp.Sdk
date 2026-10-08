---
phase: 01-foundation-store-seam
plan: 02
subsystem: stores
tags: [store-seam, contract-testing, in-memory, xunit, mtp]

requires:
  - phase: 01-foundation-store-seam (plan 01-01)
    provides: ISignalSessionStore seam shape, contract-kit mechanism (abstract xunit classes + binding subclasses), ComplianceTestBase completeness check, InMemoryStoreFactory skeleton
provides:
  - Four remaining store interfaces + factories (IIdentityStore, IPreKeyStore, IRecentMessageCacheStore, ISafetyStateStore) — full five-interface seam (D-01)
  - Domain types: Jid (immutable parsed value, D-16), SignalIdentityRecord, PreKeyBundle, PreKeyRecordEntry, SafetyStateRecord, RecentMessageEntry
  - In-memory reference impls for all five stores (STR-02 at impl level)
  - Four new kit contract classes; second deliberately-different ScratchStoreFactory + five scratch bindings proving impl-agnostic swap (D-11)
  - Idempotency + encoding edges enforced in contract tests (last-write-wins, double-Remove false, double-trim bounded)
affects: [01-03 fault-injection contracts, 01-04 errors, 01-05 sync wrapper, Phase 4 Signal crypto, Phase 5 cache policy]

actuals:
  tokens: 22000
  tasks: 3
  commits: 4

tech-stack:
  added: []
  patterns:
    - "Contract kit per store: one abstract class per store with CreateFactory(), zero-SDK-change second binding family"
    - "ScratchStoreFactory: plain Dictionary + single lock as the deliberate contrast to per-account ConcurrentDictionary"
    - "MTP-mode dotnet test: no VSTest flags (--nologo rejected with exit 5, sdk#55309)"

key-files:
  created:
    - src/Iwasug.WhatsApp.Sdk/Domain/Jid.cs
    - src/Iwasug.WhatsApp.Sdk/Stores/IIdentityStore.cs
    - src/Iwasug.WhatsApp.Sdk/Stores/IPreKeyStore.cs
    - src/Iwasug.WhatsApp.Sdk/Stores/IRecentMessageCacheStore.cs
    - src/Iwasug.WhatsApp.Sdk/Stores/ISafetyStateStore.cs
    - src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs
    - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/IdentityStoreContractTests.cs
    - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/PreKeyStoreContractTests.cs
    - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/RecentMessageCacheStoreContractTests.cs
    - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/SafetyStateStoreContractTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/ScratchStoreFactory.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Core/JidTests.cs
  modified:
    - src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt
    - src/Iwasug.WhatsApp.Sdk.Testing/PublicAPI.Unshipped.txt
    - .planning/config.json

key-decisions:
  - "Jid canonical grammar `user[.agent[.device]]@server`, split on LAST @; Device/agent canonicalization for non-canonical forms deferred to Phase 2/4 (Phase 1 ships parse/round-trip/reject)"
  - "IRecentMessageCacheStore gains GetRecentAsync(chatJid, limit) minimal read (RESEARCH Open Question 2) — shapes reference-impl state for Phase 4 quoting"
  - "Safety state is single-row per account: GetAsync/PutAsync/RemoveAsync take no key parameter"
  - "ScratchStoreFactory uses one global lock per instance (not ConcurrentDictionary) — the deliberate contrast proving the kit does not depend on reference-impl internals"

patterns-established:
  - "Kit contract class: protected abstract CreateFactory() + [Fact]s per D-11 rule family; binding subclasses return the factory, kit untouched"
  - "Idempotency assertions authored into kit: double-Put last-write-wins, double-Remove false, double-TrimOldest removes only what remains"

requirements-completed: [STR-01, STR-02]

coverage:
  - id: D1
    description: "All five granular store interfaces exist with async factories; host-style consumers implement any subset (D-01/D-03)"
    requirement: STR-01
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/InMemory*ContractTests.cs#all binding facts"
        status: pass
      - kind: unit
        ref: "command: dotnet build src/Iwasug.WhatsApp.Sdk -warnaserror (0W0E)"
        status: pass
    human_judgment: false
  - id: D2
    description: "In-memory reference store implements all five interfaces; contract suite runs green against it (STR-02)"
    requirement: STR-02
    verification:
      - kind: unit
        ref: "command: dotnet test tests/Iwasug.WhatsApp.Sdk.Tests (89/89 passed)"
        status: pass
    human_judgment: false
  - id: D3
    description: "Same contract classes pass against a second, deliberately-different scratch implementation with zero SDK changes (D-11)"
    requirement: STR-02
    verification:
      - kind: unit
        ref: "command: dotnet test --filter FullyQualifiedName~Scratch (31 passed, 0 failed)"
        status: pass
    human_judgment: false
  - id: D4
    description: "Cache store exposes only trim primitives (TrimOldest/PurgeExpired), no policy parameters; Jid parse/reject/round-trip proven; idempotency edges enforced"
    requirement: STR-02
    verification:
      - kind: unit
        ref: "src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/RecentMessageCacheStoreContractTests.cs#TrimOldest_removes_oldest_entries_observable_via_GetRecent"
        status: pass
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Core/JidTests.cs#parse/round-trip/reject facts"
        status: pass
    human_judgment: false

duration: 2 sessions (initial run interrupted post-Task-2; closed out via safe-resume)
completed: 2026-10-08
status: complete
---

# Phase 01: Foundation & Store Seam — Plan 02 Summary

**Full five-interface store seam (D-01) with in-memory reference impls, and a mechanically-proven implementation-agnostic contract kit: the same abstract classes run green against two deliberately-different factories with zero SDK changes.**

## Performance

- **Duration:** 2 sessions (prior executor session committed Tasks 1–2, authored Task 3 uncommitted, was interrupted before SUMMARY; this session verified, committed Task 3, and closed out)
- **Completed:** 2026-10-08
- **Tasks:** 3
- **Files modified:** 27 (26 src/tests + config)

## Accomplishments
- Four remaining store interfaces + factories completing the five-seam surface (D-01); typed domain records (SignalIdentityRecord, PreKeyBundle/PreKeyRecordEntry, SafetyStateRecord, RecentMessageEntry) and immutable Jid value type (D-16) with strict parse/reject/round-trip semantics
- InMemoryStoreFactory extended to all five factory interfaces — per-account concurrent maps, ordered cache index, trim primitives executing caller-issued policy only (D-08)
- Four new kit contract classes (identity, prekey, cache, safety) authored against both implementations; ScratchStoreFactory (plain Dictionary + single lock) plus five scratch bindings prove D-11's zero-SDK-change swap — 31 scratch-family tests pass
- Idempotency and encoding edges resolved and enforced: last-write-wins double-Put, second double-Remove returns false, bounded double-TrimOldest, case-sensitive ordinal identity for account/address keys

## Task Commits

Each task was committed atomically:

1. **Task 1: Four remaining store interfaces + domain types + Jid** - `4c51700` (feat)
2. **Task 2: In-memory reference impls for all five stores + Jid unit tests** - `24646e7` (feat)
3. **Task 3: Four new contract classes + second scratch implementation binding** - `38bc4c4` (feat)

**Plan metadata:** `0e9a2f0` (docs: plan revision), `8473a35` (fix: config test_command)

## Files Created/Modified
- `src/Iwasug.WhatsApp.Sdk/Stores/I*.cs` (4) - store seams with XML-doc D-19/D-06/D-07 contracts, primitives-only cache API
- `src/Iwasug.WhatsApp.Sdk/Domain/*.cs` (4) - Jid + typed records, no wire vocabulary (D-15)
- `src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs` - all five factories, per-account state, D-09 docs
- `src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/*ContractTests.cs` (4 new) - kit classes with isolation + idempotency facts
- `tests/.../StoreContract/ScratchStoreFactory.cs` + 5 `Scratch*ContractTests.cs` - second binding family
- `tests/.../Core/JidTests.cs` - parse/round-trip/reject/case-sensitivity coverage
- `PublicAPI.Unshipped.txt` (both projects) - new symbols gated
- `.planning/config.json` - test_command corrected for MTP mode (see Deviations)

## Decisions Made
- Jid canonical grammar `user[.agent[.device]]@server` split on the LAST `@`; non-canonical canonicalization deferred to Phase 2/4 (plan-scoped)
- GetRecentAsync(chatJid, limit) added as the minimal cache read (RESEARCH Open Question 2 recommendation)
- Safety state: single-row semantics (no key parameter) — planner decision restated in interface shape
- ScratchStoreFactory: one global lock per instance — maximally different from the reference impl's per-account concurrency

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] MTP-mode `dotnet test` rejects VSTest-only flags with opaque exit 5**
- **Found during:** verification (config-level test_command)
- **Issue:** `test_command` carried `--nologo`/`--verbosity minimal` (VSTest syntax). In MTP mode (`global.json` test.runner) `dotnet test` forwards them to the test app, which exits `InvalidCommandLine (5)` before discovery — reported only as "Zero tests ran, error: 1" with no reason (dotnet/sdk#55309)
- **Fix:** removed both flags from `.planning/config.json` test_command; documented the MTP flag constraint as an established pattern
- **Files modified:** .planning/config.json
- **Verification:** `dotnet test Iwasug.WhatsApp.Sdk.sln` exits 0, 89/89 passed
- **Committed in:** `8473a35`

---

**Total deviations:** 1 auto-fixed (1 blocking)
**Impact on plan:** Toolchain-level config correction only; no scope change.

## Issues Encountered
- Prior executor session was interrupted after committing Tasks 1–2: Task 3 files existed uncommitted, no SUMMARY. Closed out via the safe-resume gate: verification re-run (full 89/89, Scratch filter 31/31, acceptance greps), Task 3 committed, SUMMARY written. No work duplicated or lost.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Five-interface seam + contract kit complete; plan 01-03 can layer fault-injection contract variants on the same classes
- PublicAPI gates clean under warnings-as-errors; MTP flag discipline noted for all future verify commands (never pass --nologo/--verbosity to MTP dotnet test)

---
*Phase: 01-foundation-store-seam*
*Completed: 2026-10-08*
