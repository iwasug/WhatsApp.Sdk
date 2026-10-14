---
phase: 05-ban-safety-layer
plan: 06
subsystem: safety
tags: [ban-safety, warmup-ladder, rate-limit, safety-gate, xunit]

# Dependency graph
requires:
  - phase: 05-ban-safety-layer plan 02
    provides: WarmupLadder stage math + persistence (hollow admission) and SafetyGate.CheckWarmupAsync seam
  - phase: 05-ban-safety-layer plan 05
    provides: SafeDefaults gate options and phase-close verification baseline (05-VERIFICATION.md SC2b gap)
provides:
  - Enforced per-UTC-day warm-up stage ceiling end-to-end (ladder admission + gate threading + tests)
  - SAF-03 gap closure: day-0 account refused on 21st send with RateLimit + BudgetExhausted
affects: [06-media, 07-multi-account, 08-broadcast, 05-VERIFICATION-re-run]

# Actuals (#2632) — pairs with the plan's `estimate` to calibrate future estimates.
actuals:
  tokens: 3000
  tasks: 3
  commits: 3

# Tech tracking
tech-stack:
  added: []
  patterns: [per-UTC-day in-process counter under shared lock, stage-threading admission (resolved stage passed as int, zero store writes)]

key-files:
  created:
    - tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SafetyGateWarmupTests.cs
  modified:
    - src/Iwasug.WhatsApp.Sdk/Safety/WarmupLadder.cs
    - src/Iwasug.WhatsApp.Sdk/Safety/SafetyGate.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Safety/WarmupLadderTests.cs

key-decisions:
  - "AdmitAsync takes the already-resolved stage as an int (no second store round-trip per send, pure in-process, unit-testable without a StoreSession)"
  - "Unpaired sessions share the stage-0 ceiling with no bypass branch (no pairing-keyed evasion hole; unpaired sessions cannot reach the gate via any production path)"
  - "Over-ceiling refusal reuses SafetyRule.RateLimit and the existing RateLimit-to-BudgetExhausted path (no new event type)"

patterns-established:
  - "Warm-up accounting stays process-local: the ceiling (not the counter) survives restart via persisted CreatedAt; counters reset on UTC-day rollover"
  - "Refusals stay rule-only per T-05-03: no stage, count, or threshold values in messages (T-05-17 oracle acceptance)"

requirements-completed: [SAF-03]

# Coverage metadata (#1602)
coverage:
  - id: D1
    description: "Day-0 account sending a 21st message within one UTC day is rejected before the inner send (stage-0 ceiling 20 enforced)"
    requirement: "SAF-03"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Safety/WarmupLadderTests.cs#day_zero_ceiling_refuses_21st_send"
        status: pass
      - kind: integration
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SafetyGateWarmupTests.cs#warmup_ceiling_rejects_21st_send_before_inner"
        status: pass
    human_judgment: false
  - id: D2
    description: "Counter resets at the UTC-midnight boundary and a restarted process enforces the same stage-derived ceiling from persisted CreatedAt"
    requirement: "SAF-03"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Safety/WarmupLadderTests.cs#counter_resets_at_utc_midnight"
        status: pass
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Safety/WarmupLadderTests.cs#restart_preserves_stage_derived_ceiling"
        status: pass
    human_judgment: false
  - id: D3
    description: "Over-ceiling rejection surfaces SafetyRuleViolationException(RateLimit) and emits BudgetExhausted exactly once (handler + stream twin) with the inner send never invoked"
    requirement: "SAF-03"
    verification:
      - kind: integration
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SafetyGateWarmupTests.cs#warmup_rejection_emits_budget_exhausted_once"
        status: pass
    human_judgment: false
  - id: D4
    description: "No regressions: full safety suite green and build warning-free"
    requirement: "SAF-03"
    verification:
      - kind: unit
        ref: "dotnet test --filter FullyQualifiedName~Safety|ColdOutreach|RecipientContact|TokenBucket|Warmup|CircuitBreaker|HumanPacing|SendPathAudit (100/100 pass)"
        status: pass
      - kind: other
        ref: "dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror --nologo (0 warnings, 0 errors)"
        status: pass
    human_judgment: false

# Metrics
duration: 1 execution pass
completed: 2026-10-14
status: complete
---

# Phase 05 Plan 06: Warm-Up Ceiling Enforcement Summary

**Per-UTC-day warm-up stage ceiling enforced end-to-end — day-0 accounts refused on the 21st send with RateLimit + BudgetExhausted, closing verification gap SC2b (SAF-03)**

## Performance

- **Duration:** 1 execution pass
- **Started:** 2026-10-14T07:35:00Z
- **Completed:** 2026-10-14T08:10:00Z
- **Tasks:** 3
- **Files modified:** 4

## Accomplishments

- `WarmupLadder.AdmitAsync(int stage, CancellationToken)` enforces sent-today below `CurrentStageCeiling(stage)` from an in-process counter under a shared lock; zero store writes in the admit path
- `WarmupLadder.RecordSendAsync` applies the UTC-day reset then increments the counter exactly once per call; dead `Dictionary` counts, empty try/catch, and unused stage local removed
- `SafetyGate.CheckWarmupAsync` passes the resolved stage into `AdmitAsync` (the `_ = stage;` discard is gone) and throws `SafetyRuleViolationException(RateLimit)` on refusal before the inner send, reusing the existing BudgetExhausted handler-plus-stream emission
- 5 new tests prove day-0 rejection, midnight reset, restart ceiling, gate-level RateLimit refusal with inner never called, and single BudgetExhausted emission; all 6 pre-existing ladder tests pass unmodified

## Task Commits

Each task was committed atomically:

1. **Task 1: WarmupLadder enforcement: AdmitAsync consults counter vs ceiling, RecordSendAsync increments** - `4355108` (feat)
2. **Task 2: SafetyGate threading: CheckWarmupAsync passes stage into admission, rejects over-ceiling before inner send** - `1ed050d` (feat)
3. **Task 3: Warm-up enforcement tests: ladder ceiling, midnight reset, restart ceiling, gate rejection with event proof** - `5b30202` (test)

## Files Created/Modified

- `src/Iwasug.WhatsApp.Sdk/Safety/WarmupLadder.cs` - Replaced dead admits-all admission with stage-ceiling check over a locked per-UTC-day counter; `RecordSendAsync` now increments; `GetCurrentStageAsync`/`CurrentStageCeiling`/`DefaultStages` byte-identical
- `src/Iwasug.WhatsApp.Sdk/Safety/SafetyGate.cs` - `CheckWarmupAsync` threads the resolved stage into `AdmitAsync`; refusal throws RateLimit; execute order and publish paths untouched
- `tests/Iwasug.WhatsApp.Sdk.Tests/Safety/WarmupLadderTests.cs` - 3 new facts (day-0 21st refusal, midnight reset, restart ceiling) reusing the paired-session helper and forward-only FakeTimeProvider clocks
- `tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SafetyGateWarmupTests.cs` - New breaker-harness-mirror gate tests (21st-send RateLimit refusal with inner count pinned at 20; BudgetExhausted handler + stream twin exactly once, zero AccountRestricted)

## Decisions Made

- `AdmitAsync` accepts the already-resolved stage as an `int` (plus cancellation token) and drops the `StoreSession` parameter: the sole production caller (`CheckWarmupAsync`, confirmed by repo-wide search) already resolved the stage via `GetCurrentStageAsync`, so this avoids a second store round-trip per send and keeps admission unit-testable without a store session.
- No unpaired bypass branch: unpaired sessions share the stage-0 ceiling, preserving the observable contract (under-ceiling sends admit, no writes from the admit path) without adding a pairing-keyed evasion hole.
- Refusal reuses `SafetyRule.RateLimit` with the same rule-only message text as the `AcquireRateLimits` refusal, so the existing `ExecuteSendAsync` catch path routes it through `PublishViolationAsync` (BudgetExhausted handler + stream twin) with no new event type and no message-content change (T-05-03 / T-05-17).

## Deviations from Plan

### Process note (not a behavior deviation)

**Tasks 1+2 form one compile unit — single build, separate commits**
- **Found during:** Task 1 (WarmupLadder enforcement)
- **Issue:** The plan assigns `dotnet build` verification to each of Tasks 1 and 2 independently, but Task 1's `AdmitAsync` signature change breaks Task 2's sole call site until Task 2's rewire lands — neither task builds alone.
- **Fix:** Applied both edits, ran one warning-free build proving the seam, then committed per-task file sets separately (`4355108` WarmupLadder only, `1ed050d` SafetyGate only) so history stays atomic per task.
- **Verification:** `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror --nologo` — 0 warnings, 0 errors; `_ = stage;` absent from SafetyGate.cs; `_sentToday++` present under the day-reset block.
- **Impact on plan:** None — all acceptance criteria hold; commit granularity preserved.

---

**Total deviations:** 1 process note (0 auto-fixed bugs/security/blockers)
**Impact on plan:** Plan executed as written; the build/commit sequencing above is the only adaptation.

## Issues Encountered

None — the discarded-stage seam, dead counter code, and RateLimit-to-BudgetExhausted mapping all matched the plan's description exactly; the new tests passed on the first run (11/11 warm-up filter, 100/100 safety suite).

## Known Stubs

None — no placeholder, TODO, or empty-value code introduced; every new test asserts real enforced behavior.

## Threat Flags

None — no new security surface beyond the plan's threat model: the shared private lock mitigates T-05-16 (concurrent Admit vs RecordSend race; residual admit-then-record TOCTOU accepted per plan since per-account sends serialize in the MessageSender semaphore), refusal messages stay rule-only per T-05-03 (T-05-17 accepted), and no new packages were added (T-05-SC gate stays closed).

## User Setup Required

None - no external service configuration required.

## Verification Results

- `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror --nologo` — 0 warnings, 0 errors
- `dotnet test --filter "FullyQualifiedName~Warmup|SafetyGateWarmup"` — 11/11 pass (6 pre-existing + 3 ladder + 2 gate)
- `dotnet test --filter "FullyQualifiedName~Safety|ColdOutreach|RecipientContact|TokenBucket|Warmup|CircuitBreaker|HumanPacing|SendPathAudit"` — 100/100 pass (was 95; +5 new, zero regressions)
- Source assertions: `_ = stage;` absent from SafetyGate.cs; `_sentToday < CurrentStageCeiling(stage)` and `_sentToday++` present in WarmupLadder.cs

## Next Phase Readiness

- SAF-03 is now fully satisfied (persistence + stage math + enforcement); 05-VERIFICATION.md SC2b FAILED truth is closed — re-running `/gsd-verify-work` on Phase 5 should score 5/5, and the README daily-ceiling sentence is now true as written.
- No store-schema change, no ExecuteSendAsync reordering, no README number changes, no new packages — later phases (06/07/08) inherit the enforced gate with no migration.

---
*Phase: 05-ban-safety-layer*
*Completed: 2026-10-14*

## Self-Check: PASSED

- SUMMARY.md, WarmupLadder.cs, SafetyGate.cs, SafetyGateWarmupTests.cs all present on disk.
- Commits 4355108, 1ed050d, 5b30202 all ancestors of HEAD.
