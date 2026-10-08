---
phase: 02-wire-protocol-connection-resilience
plan: 02-08
subsystem: codec
tags: [csharp, dotnet-10, binary-node, decoder, denial-of-service]

# Dependency graph
requires:
  - phase: 02-wire-protocol-connection-resilience
    provides: BinaryNode codec and adversarial wire-format tests
provides:
  - Bounded BinaryNode decoder recursion with a 64-level nesting budget
  - Capped initial LIST_16 child-list allocation with exact-count decoding
  - Four executable depth, capacity, and truncation regression facts
  - Full warnings-as-errors build and 292-test suite re-proof
affects: [wire-protocol, connection-resilience, codec, fuzzing]

# Actuals (#2632)
actuals:
  tokens: 1187
  tasks: 3
  commits: 3

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Decoder recursion carries an internal depth budget while public Decode callers remain unchanged."
    - "Untrusted LIST_16 counts use a small initial capacity and grow only as validated children are read."

key-files:
  created:
    - tests/Iwasug.WhatsApp.Sdk.Tests/Codec/BinaryNodeDepthTests.cs
  modified:
    - src/Iwasug.WhatsApp.Sdk/Codec/BinaryNodeCodec.cs

key-decisions:
  - "Keep MaxNestedDepth internal and default ReadNode depth to zero so no public API or caller migration is required."
  - "Cap only LIST_16 initial capacity at 32; preserve LIST_8 allocation and the exact declared child read loop."

patterns-established:
  - "Malformed adversarial wire input is rejected through rule-naming SdkFormatException messages."

requirements-completed: [CONN-04]

coverage:
  - id: D1
    description: "BinaryNode decoder rejects adversarially deep nesting and caps LIST_16 pre-allocation."
    requirement: CONN-04
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Codec/BinaryNodeDepthTests.cs#BinaryNodeDepthTests"
        status: pass
    human_judgment: false
  - id: D2
    description: "Hardened decoder preserves the existing solution behavior across the complete suite."
    verification:
      - kind: other
        ref: "dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror --nologo"
        status: pass
      - kind: unit
        ref: "dotnet test Iwasug.WhatsApp.Sdk.sln — total 292, failed 0, skipped 0"
        status: pass
    human_judgment: false

# Metrics
commits: 3
plan_head_before: ff951fb0b87cff83b87aa94564f6c5feb6fd54b5
plan_head_after: 12fa837617ac4994717759dc53abb49ce61c7fdf
duration: 2min
completed: 2026-10-09
status: complete
---

# Phase 02 Plan 02-08 Summary

**BinaryNode decoding now bounds recursive nesting and untrusted LIST_16 allocation, with four adversarial regression facts and a green 292-test full-suite proof.**

## Performance

- **Duration:** 2 min
- **Started:** 2026-10-09T05:57:20Z
- **Completed:** 2026-10-09T05:59:47Z
- **Tasks:** 3
- **Files modified:** 2

## Accomplishments

- Added an internal maximum nesting depth of 64 to the cursor decoder; deeper adversarial input throws the exact rule-naming `SdkFormatException` required by the plan.
- Changed LIST_16 child lists to begin with at most 32 capacity while retaining the declared-count read loop and unchanged LIST_8 behavior.
- Added facts for accepted depth 64, rejected depth 200, exact 300-child LIST_16 round-trip, and truncated 65535-child LIST_16 rejection.
- Re-ran the warnings-as-errors solution build and complete suite: 292 passed, 0 failed, 0 skipped.

## Task Commits

Each task was committed atomically:

1. **Task 1: Depth budget plus capped LIST_16 growth in the decoder** - `951b930` (fix)
2. **Task 2: End-to-end nesting golden and evil decode — one bound proved** - `8baa41c` (test)
3. **Task 3: Full-suite re-proof of the hardened phase** - `12fa837` (test)

## Files Created/Modified

- `src/Iwasug.WhatsApp.Sdk/Codec/BinaryNodeCodec.cs` - Internal depth guard and capped LIST_16 initial capacity.
- `tests/Iwasug.WhatsApp.Sdk.Tests/Codec/BinaryNodeDepthTests.cs` - Four nesting, large-list, and truncation facts.

## Decisions Made

- Preserved the public codec surface by using a defaulted internal `ReadNode` depth parameter and keeping `Decode` unchanged.
- Applied the capacity cap only to LIST_16, as specified; LIST_8 and other decode paths remain unchanged.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None. The assigned worktree initially pointed at an older baseline without the phase-two plan artifacts; it was fast-forwarded to the current master baseline before execution, without modifying the primary checkout.

## Known Stubs

None found in the created or modified files.

## Next Phase Readiness

The CONN-04 decoder hardening gap is closed in this worktree. The full phase suite remains green at 292 tests, including the prior 288-test baseline plus four new facts. No shared `STATE.md` or `ROADMAP.md` changes were made, per orchestrator instruction.

## Self-Check: PASSED

- `src/Iwasug.WhatsApp.Sdk/Codec/BinaryNodeCodec.cs` exists.
- `tests/Iwasug.WhatsApp.Sdk.Tests/Codec/BinaryNodeDepthTests.cs` exists.
- Commits `951b930`, `8baa41c`, and `12fa837` are ancestors of `HEAD`.
- Plan ledger measures 3 commits from `ff951fb` through `12fa837`.
- Final worktree is clean.

---
*Phase: 02-wire-protocol-connection-resilience*
*Completed: 2026-10-09*
