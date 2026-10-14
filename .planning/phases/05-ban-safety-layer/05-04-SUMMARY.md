---
phase: 05-ban-safety-layer
plan: 04
status: complete
subsystem: ban-safety
tags: [circuit-breaker, exponential-backoff, human-pacing, active-hours, safety-gate, single-emit]
requires:
  - 05-03
provides:
  - CircuitBreaker Closed/Open/HalfOpen state machine on SendOutcome with 30s-to-10min backoff
  - HumanPacing active-hours window plus bounded best-effort presence pause
  - SafetyGate research-order sequence with single-emit AccountRestricted invariant
  - Internal SafetyPolicy seam on WaConnection with NoSafetyDelays loopback pins
affects: [Safety, Messaging, Connection, SafetyEvents]
tech-stack:
  added: []
  patterns:
    - time-based backoff gating (fail-fast refusal, never a waited delay)
    - exactly-once transition signals via breaker-owned idempotency
    - best-effort presence seam with no-op default (Phase 8 upgrade path)
    - set-before-first-use internal policy seam (StoreRegistry precedent)
key-files:
  created:
    - src/Iwasug.WhatsApp.Sdk/Safety/CircuitState.cs
    - src/Iwasug.WhatsApp.Sdk/Safety/CircuitBreaker.cs
    - src/Iwasug.WhatsApp.Sdk/Safety/HumanPacing.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Safety/CircuitBreakerTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Safety/HumanPacingTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SafetyCircuitBreakerTests.cs
  modified:
    - src/Iwasug.WhatsApp.Sdk/Safety/SafetyGate.cs
    - src/Iwasug.WhatsApp.Sdk/Safety/SafetyOptions.cs
    - src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt
    - src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/MessagingLoopbackTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/MessageCorrelationTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/MessagingEventTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/QuotingTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/RecentCachePolicyTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Signal/SenderKeyTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Safety/ColdOutreachBudgetTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Safety/ColdOutreachReplyExemptionTests.cs
key-decisions:
  - "Backoff is time-based gating (NextProbeAtUtc), not a waited delay: ThrowIfOpen refuses fast with CircuitOpen per research Pattern 5. The plan's Task.Delay(backoff, clock, ct) line describes clock-awareness, which the design satisfies via TimeProvider-scheduled probe admission."
  - "CircuitOpen refusals emit nothing: the Open transition owns the single AccountRestricted emission; refusals are steady-state, not transitions."
  - "Inverted pacing bounds clamp (Max := Min) instead of throwing: pacing stays bounded without rejecting host configuration (unlike jitter ratios, which keep their throw)."
  - "Internal WaConnection.SafetyPolicy seam (StoreRegistry set-before-first-use precedent) + NoSafetyDelays loopback pins; the public host-facing configuration story stays with plan 05-05 (SAF-07)."
patterns-established:
  - "Breaker trip: 5 consecutive failures OR 3 consecutive timeouts; Sent resets both streaks and the doubling."
  - "HalfOpen admits exactly one probe; failed probe re-opens with doubled backoff capped at 10min; recovery fires no event."
  - "Active-hours: zoned TimeOfDay check, midnight wrap (start >= end) is overnight, equal bounds is full-day."
  - "Presence pause draws 2000-12000ms via RandomNumberGenerator; throwing presence swallowed, cancellation propagated."
metrics:
  duration: "1 execution pass"
  completed: 2026-10-14
  tasks: 3
  files: 19
actuals:
  tokens: 19000
  tasks: 3
  commits: 3
  plan_head_before: d762aaf2bdcbb0984085bcc65eaee380213a5d27
  plan_head_after: 0f5359051876900c02ccec5026b11a49a19106c6
requirements-completed: [SAF-05, SAF-06]
coverage:
  - id: D1
    description: "Repeated send failures/timeouts trip an in-process breaker to Open with exponential backoff, raising AccountRestricted once, then HalfOpen probe and close on success"
    requirement: "SAF-05"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Safety/CircuitBreakerTests#open_on_threshold_after_five_consecutive_server_errors + open_on_threshold_after_three_consecutive_timeouts + backoff_doubles_with_each_failed_probe_up_to_cap + half_open_probe_success_closes_breaker_without_event + half_open_probe_failure_reopens_with_longer_backoff"
        status: pass
      - kind: integration
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SafetyCircuitBreakerTests#gate_records_server_errors_then_trips_breaker_once + gate_half_open_probe_success_closes_breaker"
        status: pass
    human_judgment: false
  - id: D2
    description: "Safety event fires exactly once per transition; no duplicate AccountRestricted on repeated failed sends while already open"
    requirement: "SAF-05"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Safety/CircuitBreakerTests#no_duplicate_event_while_open"
        status: pass
      - kind: integration
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SafetyCircuitBreakerTests#gate_records_server_errors_then_trips_breaker_once (6th send refused with zero additional events)"
        status: pass
    human_judgment: false
  - id: D3
    description: "Active-hours window evaluated before inner send; outside-window sends rejected with SafetyRuleViolationException(InactiveHours), midnight wrap and zones handled"
    requirement: "SAF-06"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Safety/HumanPacingTests#outside_window_throws_inactive_hours + inside_window_passes + midnight_wrap_window_handles_overnight + timezone_converts_before_window_check"
        status: pass
      - kind: integration
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SafetyCircuitBreakerTests#gate_active_hours_violation_skips_inner_and_breaker_record + gate_active_hours_checked_before_rate_limits"
        status: pass
    human_judgment: false
  - id: D4
    description: "Human-like pacing applies a bounded presence pause via TimeProvider before send, best-effort and cancellation-aware, with no-op presence fallback"
    requirement: "SAF-06"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Safety/HumanPacingTests#pause_bounds_within_range_over_1000_draws + cancel_pause_propagates_cancellation + presence_throws_swallowed_and_pause_still_applies"
        status: pass
    human_judgment: false
---

# Phase 05 Plan 04: Ban-Safety Circuit Breaker + Human Pacing Summary

**Hand-rolled SendOutcome circuit breaker (5-failure/3-timeout trip, 30s-doubling-to-10min backoff, single-probe HalfOpen) and human-like pacing (zoned active-hours window, 2–12s best-effort presence pause) integrated into SafetyGate in exact research order, with exactly-once AccountRestricted per opening.**

## Accomplishments

- Implemented `CircuitState` (`Closed`/`Open`/`HalfOpen`) and `CircuitBreaker` on an injectable `TimeProvider`: consecutive-failure (5) / consecutive-timeout (3) thresholds, first opening waits 30s with doubling capped at 10min, `ThrowIfOpen` refuses fast with `SafetyRule.CircuitOpen` and advances `Open → HalfOpen` only when the backoff elapsed, `RecordOutcome` returns `true` exactly on fresh `Closed → Open` / failed-probe `HalfOpen → Open` transitions and fires the injected `onOpened` delegate outside the lock.
- Implemented `HumanPacing` with `IPacingPresence` seam defaulting to `NoOpPacingPresence`: `AssertInsideActiveHours` converts the UTC clock into `SafetyOptions.ActiveHoursTimeZone` (UTC when unset) and throws `SafetyRule.InactiveHours` outside `[start, end)` with overnight-wrap and equal-bounds-full-day handling; `ApplyPresencePauseAsync` draws 2000–12000ms via `RandomNumberGenerator`, signals presence best-effort (only `OperationCanceledException` propagates), then waits cancellation-aware on the clock.
- Extended `SafetyOptions` with `PacingPresenceMinMs`/`PacingPresenceMaxMs` (2000/12000 defaults; negatives throw, inversions clamp `Max := Min` in `Validate`) and `ActiveHoursTimeZone` (`null` = UTC); harvested the three properties into `PublicAPI.Unshipped.txt`.
- Finalized `SafetyGate.ExecuteSendAsync` to the research-diagram order — CheckCircuit → CheckActiveHours → AcquireRateLimits → CheckWarmup → CheckColdOutreach → ApplyJitter → ApplyPresencePause → inner → RecordOutcome (warmup record + cold mark + breaker record). Throwing stages skip inner and RecordOutcome; cancellation propagates with no events; `RecordOutcome` feeds every settled outcome including `ServerError`/`TimedOut`.
- Added the internal `WaConnection.SafetyPolicy` seam (mirrors the settable `StoreRegistry` precedent) consumed by `Messenger`, plus `NoSafetyDelays()` loopback pins: pre-existing send-path proofs stay deterministic instead of absorbing the production 2–12s pause into their 15s budgets.
- Proved SAF-05/SAF-06 with 23 new facts (9 breaker + 8 pacing + 6 gate-integration), all green; full Safety suite 72/72 green; full repo suite 589/593 with only the 4 documented pre-existing `PairingLifecycleTests` environment failures.

## Task Commits

Each task was committed atomically:

1. **Task 1: Circuit breaker state machine on SendOutcome with exponential backoff** - `6812139` (feat)
2. **Task 2: Active-hours window + human-like presence pause** - `999b40e` (feat)
3. **Task 3: Integration into SafetyGate sequence + event single-emit invariants** - `0f53590` (feat)

## Files Created/Modified

- `src/Iwasug.WhatsApp.Sdk/Safety/CircuitState.cs` - Breaker position enum (created)
- `src/Iwasug.WhatsApp.Sdk/Safety/CircuitBreaker.cs` - Thresholds, backoff, single-probe discipline, exactly-once signal (created)
- `src/Iwasug.WhatsApp.Sdk/Safety/HumanPacing.cs` - Window check + presence-pause seam with no-op fallback (created)
- `src/Iwasug.WhatsApp.Sdk/Safety/SafetyGate.cs` - CheckCircuit/CheckActiveHours/ApplyPresencePause wiring, research-order sequence, warmup record, silent CircuitOpen refusals, unused store round-trip removed (modified)
- `src/Iwasug.WhatsApp.Sdk/Safety/SafetyOptions.cs` - Pacing bounds + zone + clamp validation (modified)
- `src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt` - Three new SafetyOptions properties harvested from build (modified)
- `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs` - Internal SafetyPolicy seam (modified)
- `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs` - Gate consumes connection SafetyPolicy (modified)
- `tests/Iwasug.WhatsApp.Sdk.Tests/Safety/CircuitBreakerTests.cs` - Threshold/backoff/probe/idempotency proofs (created)
- `tests/Iwasug.WhatsApp.Sdk.Tests/Safety/HumanPacingTests.cs` - Window/pacing/cancel/presence proofs (created)
- `tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SafetyCircuitBreakerTests.cs` - Gate trip/probe/order/cancel proofs (created)
- `tests/.../Messaging/MessagingLoopbackTests.cs` - `NoSafetyDelays()` shared pin (modified)
- `tests/.../Messaging/MessageCorrelationTests.cs` - `safetyOptions` rig param + 3 pins (modified)
- `tests/.../Messaging/MessagingEventTests.cs`, `QuotingTests.cs`, `RecentCachePolicyTests.cs` - One pin each (modified)
- `tests/.../Signal/SenderKeyTests.cs` - Direct-connection pins (modified)
- `tests/.../Safety/ColdOutreachBudgetTests.cs`, `ColdOutreachReplyExemptionTests.cs` - Zero-pacing `NoJitter` helpers (modified)

## Decisions Made

- **Time-based backoff, not waited delay:** the plan's `Task.Delay(backoff, _clock, ct)` line is satisfied via `TimeProvider`-scheduled probe admission (`NextProbeAtUtc`); `ThrowIfOpen` refuses fast per research Pattern 5 ("all application sends throw/refuse immediately"). A waited delay would stall the send path and break fail-fast semantics.
- **Silent CircuitOpen refusals:** the violation path no longer emits for `CircuitOpen` — the Open transition (via `PublishBreakerOpenAsync` with the real `NextProbeAtUtc`, not `MaxValue`) owns the single emission. Keeps must_have truth 2 under repeated blocked sends.
- **Pacing inversion clamps:** unlike jitter ratios (which throw), inverted `PacingPresenceMinMs/MaxMs` collapse to a point. Rationale: pacing bounds are host ergonomics, not safety thresholds; rejecting the send policy over a swapped pair would be hostile.
- **Internal seam, public story deferred:** `WaConnection.SafetyPolicy` is deliberately internal and test-scoped; SAF-07 host configuration (plan 05-05) designs the public API on top of it.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] CircuitOpen refusals double-emitted AccountRestricted**
- **Found during:** Task 3 (gate integration test `gate_records_server_errors_then_trips_breaker_once` showed 2 events)
- **Issue:** The breaker opening emitted once via `PublishBreakerOpenAsync`, then every subsequent `ThrowIfOpen` refusal re-emitted through `PublishViolationAsync` — steady-state refusals are not transitions, violating must_have truth 2.
- **Fix:** `PublishViolationAsync` returns early for `SafetyRule.CircuitOpen` (typed refusal still propagates); removed the now-unreachable emit arms.
- **Files modified:** `src/Iwasug.WhatsApp.Sdk/Safety/SafetyGate.cs`
- **Commit:** 0f53590

**2. [Rule 3 - Blocking] Production pacing stalled deterministic send-path tests**
- **Found during:** Task 3 (full-suite triage: 12 Messaging/Signal failures at exact 15s timeouts)
- **Issue:** The mandatory 2–12s presence pause hangs sends on frozen `FakeTimeProvider` clocks and erodes the 15s script budgets of System-clock loopback tests (multi-send tests: phash ×3, group fanout ×2, quote ×2). Latent variant since 05-02 (jitter on fake clocks — prior plans only ever ran Safety-filtered suites, so it went unnoticed).
- **Fix:** Internal `WaConnection.SafetyPolicy` seam (set-before-first-`Messaging`-use, mirroring `StoreRegistry`) consumed by `Messenger`; `NoSafetyDelays()` pin (zero jitter/pacing, generous budgets, full-day window) applied at the 10 affected send proofs; pre-existing Safety `NoJitter` helpers zeroed for pacing. Production defaults untouched.
- **Files modified:** `WaConnection.cs`, `Messenger.cs`, `MessagingLoopbackTests.cs`, `MessageCorrelationTests.cs`, `MessagingEventTests.cs`, `QuotingTests.cs`, `RecentCachePolicyTests.cs`, `SenderKeyTests.cs`, both cold-outreach test files
- **Commit:** 0f53590

**3. [Rule 3 - Blocking] `static` lambda in null-coalescing rejected by the compiler**
- **Found during:** Task 2 (build: CS0106/CS1001 on the default duration draw)
- **Issue:** `_nextMsDraw = nextMsDraw ?? static (minMs, maxMs) => ...` does not parse in this position.
- **Fix:** Parenthesized non-static lambda `((minMs, maxMs) => RandomNumberGenerator.GetInt32(minMs, maxMs + 1))`.
- **Files modified:** `src/Iwasug.WhatsApp.Sdk/Safety/HumanPacing.cs`
- **Commit:** 999b40e

**4. [Rule 1 - Bug] Doc `paramref` on class-level remarks (CS1734 ×5)**
- **Found during:** Task 1 (first `-warnaserror` build)
- **Issue:** `<paramref>` tags referencing ctor parameters from the class doc block.
- **Fix:** Reworded to plain `<c>` code references; ctor keeps proper `<param>` docs.
- **Files modified:** `src/Iwasug.WhatsApp.Sdk/Safety/CircuitBreaker.cs`
- **Commit:** 6812139 (amended before commit)

**5. [Rule 1 - Bug] Tests moved FakeTimeProvider backwards (ArgumentOutOfRangeException)**
- **Found during:** Task 2 (`midnight_wrap` proof)
- **Issue:** `FakeTimeProvider.SetUtcNow` rejects backward sets; the overnight test walked 23:00 → 05:00 same-day.
- **Fix:** Restructured the test to walk forward across midnight (21:00 → 23:00 → next-day 05:00 → 07:00 → 12:00), also covering the pre-window rejection.
- **Files modified:** `tests/Iwasug.WhatsApp.Sdk.Tests/Safety/HumanPacingTests.cs`
- **Commit:** 999b40e

**6. [Rule 1 - Bug] Order-proof test admitted the half-open probe instead of refusing**
- **Found during:** Task 3 (`gate_circuit_open_checked_before_active_hours` observed `InactiveHours`)
- **Issue:** Moving the clock 8h past the trip elapsed the 30s backoff, so the probe was rightly admitted and failed at the hours check — the test proved the wrong thing.
- **Fix:** Seconds-precision window (12:00:00–12:00:20, trip at :10, assert at :25): outside the window but inside the backoff, so the circuit refusal provably wins.
- **Files modified:** `tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SafetyCircuitBreakerTests.cs`
- **Commit:** 0f53590

---

**Omitted from plan (documented, not built):** `SafetyHealthRecord.cs` and the separate `SafetyCircuitBreakerTests.cs`-vs-`CircuitBreakerTests.cs` split — the record carries no behavior any task specifies (creating it would be dead surface), and the split is honored as unit-vs-gate files instead. `SafetyHealthRecord` remains available for 05-05 if the health story needs it.

**Total deviations:** 6 auto-fixed (3 bug, 2 blocking, 1 bug/test-design)
**Impact on plan:** All required for a green, regression-free delivery. No scope creep — no new packages (T-05-SC holds).

## Issues Encountered

- `FakeTimeProvider` never moves backwards: both the midnight-wrap unit proof and the circuit-before-hours order proof had to be designed around forward-only time (see deviations 5–6).
- Full-suite timing flakiness: two consecutive full runs surfaced slightly different failing sets (12 vs 10) before the pins — the 2–12s random pacing draw inside 15s script budgets. After pinning, one full run is 589/593 with only the pre-existing Pairing set failing.

## Known Stubs

| File | Line | Description |
|------|------|-------------|
| `src/Iwasug.WhatsApp.Sdk/Safety/HumanPacing.cs` (`NoOpPacingPresence`) | class | Intentional Phase-5 seam: presence signals are no-ops until Phase 8 wires real composing/recording nodes (05-RESEARCH Open Question 2). The pause still applies; only the wire signal is skipped. Tracked for Phase 8, not a ship blocker. |

## Threat Flags

None — no new security-relevant surface beyond the plan's threat model. `CircuitBreaker`/`HumanPacing`/`SafetyPolicy` are internal; the three new `SafetyOptions` properties are host configuration with validated bounds; no new packages (T-05-SC holds); breaker scheduling rides the injected `TimeProvider` (T-05-10) and the hours check is a mandatory early stage (T-05-12).

## User Setup Required

None - no external service configuration required. No new packages.

## Verification

- `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror --nologo`: 0 warnings, 0 errors.
- `dotnet test --filter "FullyQualifiedName~CircuitBreakerTests"`: 9 passed (`open_on_threshold`, `backoff_doubles`, `half_open_probe` ×3, `no_duplicate_event`, reset, cancellation).
- `dotnet test --filter "FullyQualifiedName~HumanPacingTests"`: 8 passed (`outside_window`, `inside_window`, `midnight_wrap`, `pause_bounds`, `cancel_pause`, `presence_throws_swallowed`, + zone/clamp).
- `dotnet test --filter "FullyQualifiedName~Safety"`: 72 passed, 0 failed.
- `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` (full): 589/593 — the 4 failures are the documented pre-existing `PairingLifecycleTests` environment failures (STATE.md 04-01 decision; pairing path never touches the gate).

## Next Phase Readiness

- SAF-05 and SAF-06 observably true. Remaining: warm-up admission (`WarmupLadder.AdmitAsync` still admits-all — 05-02/05-03 note), SAF-07 safe-by-default host configuration + public `SafetyPolicy` story + risk ledger (plan 05-05), and the Phase 8 presence-node upgrade behind `IPacingPresence`.
- No blockers. Note for 05-05: `WaConnection.SafetyPolicy` is the internal seam to promote; `SafetyHealthRecord.cs` (listed in this plan's files but behavior-free) is still unclaimed if the health story wants it.

## Self-Check: PASSED

Created files exist; commits 6812139, 999b40e, 0f53590 are ancestors of HEAD; build and all scoped suites green.

---
*Phase: 05-ban-safety-layer*
*Completed: 2026-10-14*
