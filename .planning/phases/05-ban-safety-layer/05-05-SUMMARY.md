---
phase: 05-ban-safety-layer
plan: 05
status: complete
subsystem: ban-safety
tags: [safety-options, safe-by-default, risk-ledger, provenance, phase-close]
requires:
  - phase: 05-01
    provides: SafetyOptions safe defaults + mandatory gate seam
  - phase: 05-02
    provides: Token buckets + warm-up ladder over SafetyOptions
  - phase: 05-03
    provides: Cold-outreach budget over SafetyOptions
  - phase: 05-04
    provides: Circuit breaker + pacing over SafetyOptions with internal SafetyPolicy seam
provides:
  - SafetyOptions explicit unsafe opt-in with required DeclaredUnsafeAcknowledgment
  - README Ban-safety risk ledger (does / does-not / opt-in banner)
  - NOTICE Phase 5 provenance audit row
  - Phase-close verification gate outcome
affects: [Phase-06-media, Phase-07-multi-account, Phase-08-broadcast]
actuals:
  tokens: 5600
  tasks: 3
  commits: 2
  plan_head_before: bf8f5512954c03c5625ccd3a260ac95928ce7397
  plan_head_after: e6ffa9d84979590f9ddb263ef6cd93456606ac27
tech-stack:
  added: []
  patterns:
    - safe-by-default options with explicit opt-in plus acknowledgment
    - honest risk ledger (does / does-not / banner)
key-files:
  created:
    - tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SafetyOptionsValidationTests.cs
  modified:
    - src/Iwasug.WhatsApp.Sdk/Safety/SafetyOptions.cs
    - src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt
    - README.md
    - NOTICE.md
key-decisions:
  - "Day safe-threshold stays at 1000 (not the plan's literal 300) so SafeDefaults remain valid, per the 05-01 precedent; hour adopts the plan's 600."
  - "Opt-in requires a non-empty DeclaredUnsafeAcknowledgment; existing unsafe test helpers were pinned with explicit acknowledgments."
  - "Full-suite gate closes with the 4 documented pre-existing PairingLifecycleTests environment failures; no new failures."
requirements-completed: [SAF-01, SAF-07]
coverage:
  - id: D1
    description: "Unsafe throughput rejected without opt-in; accepted only with AllowUnsafeThroughput plus explicit acknowledgment"
    requirement: "SAF-07"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SafetyOptionsValidationTests#unsafe_rejected_without_opt_in + unsafe_accepted_with_ack + missing_ack_rejected"
        status: pass
    human_judgment: false
  - id: D2
    description: "Safe defaults accepted and inverted jitter rejected"
    requirement: "SAF-07"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SafetyOptionsValidationTests#defaults_accepted + inverted_jitter_rejected"
        status: pass
    human_judgment: false
  - id: D3
    description: "README ships an honest Ban-safety risk ledger with the unsafe opt-in banner and no overclaim language"
    requirement: "SAF-07"
    verification:
      - kind: other
        ref: "grep -c ban-proof README.md outputs 0; grep AllowUnsafeThroughput README.md matches the banner section"
        status: pass
    human_judgment: false
  - id: D4
    description: "NOTICE provenance audit confirms no new upstream reads and no MPL-2.0 contamination in Phase 5"
    requirement: "SAF-01"
    verification:
      - kind: other
        ref: "NOTICE.md Phase 5 audit row dated 2026-10-14"
        status: pass
    human_judgment: false
  - id: D5
    description: "Phase-close build plus safety suites green; full suite carries only the 4 pre-existing pairing environment failures"
    requirement: "SAF-01"
    verification:
      - kind: unit
        ref: "dotnet build -warnaserror (0 warnings, 0 errors); Safety filter 77 passed; full suite 594/598 with 4 pre-existing PairingLifecycleTests failures"
        status: pass
    human_judgment: false
duration: 1 execution pass
completed: 2026-10-14
---

# Phase 05 Plan 05: Ban-Safety Closeout Summary

**Safe-by-default options with an explicit acknowledged unsafe opt-in, an honest Ban-safety risk ledger, a clean provenance audit, and a green phase-close gate.**

## Performance

- **Duration:** 1 execution pass
- **Tasks:** 3
- **Files modified:** 11 (1 created, 10 touched per diff stat)

## Accomplishments

- Hardened `SafetyOptions.Validate`: inverted jitter throws, basic positive-range checks, minute/hour/day safe bands (60/600/1000) gated on `!AllowUnsafeThroughput` with rule-and-threshold messages, and a required non-empty `DeclaredUnsafeAcknowledgment` when opting into unsafe throughput.
- Added `SafetyOptionsValidationTests` (5 facts): defaults accepted, inverted jitter rejected, unsafe thresholds rejected without opt-in, accepted with opt-in plus acknowledgment, missing acknowledgment rejected.
- Shipped the README Ban-safety / risk ledger: what the layer does (mandatory gate, buckets plus jitter, persisted warm-up, cold cap, breaker plus backoff, active-hours plus presence pacing, safe defaults with opt-in), what it does not do (no guarantee against bans, no risk reduction, no rehabilitation, no ToS guarantee, host owns operational realities), and the required unsafe opt-in banner with a compiling `SafetyOptions` example.
- Audited NOTICE.md with a Phase 5 row confirming zero new upstream reads and zero MPL-2.0 contamination; COVERAGE.md already carried the exact required declaration and was verified present.
- Closed the phase gate: warning-free build, 77/77 Safety facts green, full suite 594/598 with only the 4 documented pre-existing `PairingLifecycleTests` environment failures; no new packages; public surface harvested.

## Task Commits

Each task was committed atomically:

1. **Task 1: Harden SafetyOptions validation and explicit unsafe opt-in acknowledgment** - `6607313` (feat)
2. **Task 2: README risk ledger + NOTICE provenance audit + COVERAGE declaration** - `e6ffa9d` (docs)
3. **Task 3: Full verification gate and PublicAPI harvest at phase close** - verification only, no file changes (working tree clean; harvest already complete)

## Files Created/Modified

- `src/Iwasug.WhatsApp.Sdk/Safety/SafetyOptions.cs` - `DeclaredUnsafeAcknowledgment` property, basic-range checks, 60/600/1000 safe bands with acknowledgment-gated opt-in (modified)
- `tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SafetyOptionsValidationTests.cs` - SAF-07 validation proofs with the plan-mandated method-name fragments (created)
- `src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt` - Two `DeclaredUnsafeAcknowledgment` entries harvested from the warnings-as-errors build (modified)
- `README.md` - Phase 5 status plus Ban-safety risk ledger, reworded Compliance note with no overclaim substring (modified)
- `NOTICE.md` - Phase 5 audit row (modified)
- `tests/.../Messaging/MessagingLoopbackTests.cs`, `tests/.../Safety/ColdOutreachBudgetTests.cs`, `ColdOutreachReplyExemptionTests.cs`, `SafetyCircuitBreakerTests.cs`, `SafetyGateTests.cs`, `TokenBucketRateLimitTests.cs` - Existing unsafe test helpers pinned with explicit acknowledgments (modified)
- `.planning/phases/05-ban-safety-layer/COVERAGE.md` - Verified present with the exact required declaration (unmodified)

## Decisions Made

- **Day threshold stays at 1000:** the plan's literal 300 would reject `SafeDefaults` (1000/day). Kept 1000 per the 05-01 precedent ("day band keyed at 1000 instead of the contradictory 300"); hour adopts the plan's 600 (relaxed from the in-tree 300).
- **Acknowledgment is mandatory under opt-in:** `AllowUnsafeThroughput = true` with null/empty/whitespace `DeclaredUnsafeAcknowledgment` throws; all pre-existing unsafe test helpers were updated with explicit test acknowledgments rather than exempted.
- **Phase gate treats the 4 pairing failures as pre-existing:** same 4 `PairingLifecycleTests` environment failures recorded in STATE.md (04-01) and the 05-04 summary; pairing never touches the gate, so they are out of scope for this plan.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing critical] Day threshold kept at 1000 instead of the plan's literal 300**
- **Found during:** Task 1 (validation hardening)
- **Issue:** The plan specifies `MaxMessagesPerDay > 300` throws without opt-in, but `SafeDefaults` ships 1000/day — the literal rule would reject the SDK's own defaults, breaking safe-by-default and every downstream suite.
- **Fix:** Kept the in-tree 1000/day band (05-01 precedent) with a rule-and-threshold message; adopted the plan's 60/min and 600/hr bands exactly.
- **Files modified:** `src/Iwasug.WhatsApp.Sdk/Safety/SafetyOptions.cs`
- **Verification:** `SafetyOptionsValidationTests#defaults_accepted` plus full Safety suite green
- **Committed in:** 6607313 (part of task commit)

**2. [Rule 3 - Blocking] Existing unsafe test helpers rejected by the new acknowledgment requirement**
- **Found during:** Task 1 (first build after validation hardening)
- **Issue:** Six pre-existing `AllowUnsafeThroughput = true` helpers/options (loopback pins, cold-outreach, breaker, gate, token-bucket) carried no acknowledgment and would now throw at `Validate`.
- **Fix:** Pinned each with an explicit test acknowledgment string; no production defaults changed.
- **Files modified:** `MessagingLoopbackTests.cs`, `ColdOutreachBudgetTests.cs`, `ColdOutreachReplyExemptionTests.cs`, `SafetyCircuitBreakerTests.cs`, `SafetyGateTests.cs`, `TokenBucketRateLimitTests.cs`
- **Verification:** Safety suite 77/77 green, full Messaging/Signal loopback suites green
- **Committed in:** 6607313 (part of task commit)

**3. [Rule 1 - Bug] README carried the forbidden overclaim substring**
- **Found during:** Task 2 (risk-ledger rewrite)
- **Issue:** The Compliance note contained the literal substring the plan's verify forbids, which would fail the `grep -c` gate.
- **Fix:** Reworded to guarantee-against-restrictions language with zero occurrences of either forbidden substring; expanded the ledger to the full does / does-not / banner shape.
- **Files modified:** `README.md`
- **Verification:** `ban-proof` count 0, `undetectable` count 0, `AllowUnsafeThroughput` banner present
- **Committed in:** e6ffa9d (part of task commit)

---

**Total deviations:** 3 auto-fixed (1 missing-critical, 1 blocking, 1 bug)
**Impact on plan:** All required for a correct, self-consistent, gate-passing delivery. No scope creep — no new packages (T-05-SC holds).

## Issues Encountered

- Full-suite run surfaces 4 `PairingLifecycleTests` failures (task-canceled waits in `WaitForPairingEventAsync`). Proven pre-existing via STATE.md (04-01 decision) and the 05-04 summary (589/593 with the same 4); not re-proven via stash this pass since the pairing path never touches the gate and the failing set is byte-identical to the documented one. Left untouched per scope boundary.

## Known Stubs

None - no stubs introduced. (`HumanPacing.NoOpPacingPresence` remains the intentional Phase-5 seam for Phase 8, per 05-04.)

## Threat Flags

None — no new security-relevant surface beyond the plan's threat model. The new `DeclaredUnsafeAcknowledgment` property is validated host configuration (T-05-13 mitigated); the ledger carries the explicit does-not list with no overclaim language (T-05-14 mitigated); public surface was harvested under warnings-as-errors (T-05-15 mitigated); no new packages (T-05-SC holds).

## User Setup Required

None - no external service configuration required. No new packages.

## Verification

- `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror --nologo`: 0 warnings, 0 errors.
- `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~SafetyOptionsValidationTests"`: 5 passed (`Passed!`).
- `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~Safety"`: 77 passed, 0 failed.
- `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` (full): 594/598 — the 4 failures are the documented pre-existing `PairingLifecycleTests` environment failures.
- `grep -c "No external API integration" COVERAGE.md`: 1 (declaration present).
- `grep -c "ban-proof" README.md`: 0; `undetectable`: 0.
- `dotnet list package`: no new packages.
- PublicAPI: `DeclaredUnsafeAcknowledgment` get/init present; 111 safety-surface lines total.

## Next Phase Readiness

- Phase 5 closes with SAF-01..SAF-07 observably satisfied: mandatory gate (05-01), buckets plus warm-up (05-02), cold-outreach ledger (05-03), breaker plus pacing (05-04), safe-by-default configuration plus ledger plus audit plus gate (05-05).
- No blockers. Note for Phase 6: new outbound asset paths must route through `SafetyGate.ExecuteSendAsync` or a documented media passthrough (research Pitfall 1); `WaConnection.SafetyPolicy` remains the internal set-before-first-use seam.

---
*Phase: 05-ban-safety-layer*
*Completed: 2026-10-14*

## Self-Check: PASSED

All created files exist, both task commits are ancestors of HEAD, the build is
warning/error free, and the Safety suites plus doc gates are green.
