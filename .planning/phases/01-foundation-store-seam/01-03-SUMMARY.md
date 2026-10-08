---
phase: 01-foundation-store-seam
plan: 03
subsystem: testing
tags: [store-seam, contract-kit, fault-injection, decorator, xunit-v3, publicapi]

# Dependency graph
requires:
  - phase: 01-foundation-store-seam
    provides: "Plan 01-02's five kit contract classes, InMemory + Scratch binding families, and plan 01-01's five store/factory interfaces the decorator wraps"
provides:
  - "FaultingStoreFactory — kit-public decorator over all five store factory interfaces with three-mode fault control (None/FailReads/FailWrites) throwing SdkStoreException"
  - "StoreContractRules — kit-public shared helpers: AssertWriteVisibleToIndependentReadAsync (D-06), AssertTwoAccountsIsolatedAsync (D-02), AssertParallelAccessNoCrossReadsAsync keyed + keyless (D-11/T-01-10)"
  - "All five contract classes upgraded with failure-vs-missing, durability-await, and parallel cross-read facts"
  - "FaultingBindingTests — third binding family proving the decorator is seam-correct"
affects: [01-04, phase-2-protocol-loop, host-integration-docs]

# Actuals (#2632) — pairs with the plan's estimate (58000 tokens, 2 tasks) to calibrate future estimates.
actuals:
  tokens: 19166      # chars/4 over the realized diff (76,662 chars, 9 files, +1200/-10)
  tasks: 2
  commits: 2
plan_head_before: d02b3366b21b58ca0de8f38813b23588d21a24d1
plan_head_after: a3535dbfff0b3528c6818cc6e980f71bdb5d089f

# Tech tracking
tech-stack:
  added: []          # in-box only — Microsoft.CodeAnalysis.PublicApiAnalyzers was already configured
  patterns:
    - "PublicAPI harvest loop: build with warnings-as-errors, mechanically extract RS0016 signature strings from build output, append to PublicAPI.Unshipped.txt, rebuild"
    - "Interface-typed decorator binding: multi-interface factories with same-signature CreateAsync need interface-typed variables so the right explicit interface implementation dispatches"
    - "RS0026 discipline: public multi-overload APIs must not repeat optional parameters — make shared trailing params required"

key-files:
  created:
    - src/Iwasug.WhatsApp.Sdk.Testing/FaultInjection/FaultingStoreFactory.cs
    - src/Iwasug.WhatsApp.Sdk.Testing/Rules/StoreContractRules.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/FaultingBindingTests.cs
  modified:
    - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/SignalSessionStoreContractTests.cs
    - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/IdentityStoreContractTests.cs
    - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/PreKeyStoreContractTests.cs
    - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/RecentMessageCacheStoreContractTests.cs
    - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/SafetyStateStoreContractTests.cs
    - src/Iwasug.WhatsApp.Sdk.Testing/PublicAPI.Unshipped.txt

key-decisions:
  - "FaultingStoreFactory is ONE class implementing all five factory interfaces (one ctor taking each factory as a nullable param); the four non-session CreateAsync overloads are explicit interface implementations — callers bind through interface-typed variables (documented in class XML doc)"
  - "AssertParallelAccessNoCrossReadsAsync ships as keyed + keyless overloads with a REQUIRED iterations parameter (RS0026 forbids multiple overloads with optional parameters); callers pass 100"
  - "Decorator disposal deliberately non-cascading — test infrastructure, inner lifecycle belongs to the suite (D-23)"
  - "FaultMode is read at every wrapped operation, so Mode can be toggled after stores exist (fault injects per-call, proven by the FailWrites-then-None facts)"

patterns-established:
  - "Failure-vs-missing fact pair: FailReads_Get_throws_SdkStoreException_never_returns_null + Faulting_factory_Mode_None_absent_key_returns_null — throw and null asserted in distinct contexts per store family (D-19/T-01-09)"
  - "Durability-await fact: AssertWriteVisibleToIndependentReadAsync wraps Put-on-instance-1 / Get-on-instance-2 (D-06 store-side, Pitfall 7)"
  - "Concurrency fact: AssertParallelAccessNoCrossReadsAsync on all five families — keyed for session/identity/prekey/cache, keyless for safety state (D-11: serial-only coverage is not acceptable)"
  - "Cache fault facts pin the PRIMITIVE contract only — SDK-side log-and-continue is Phase 4/5 policy, not part of the store contract (D-20)"

requirements-completed: [STR-01]

coverage:
  - id: D1
    description: "FaultingStoreFactory kit-public decorator: five factory interfaces, three-mode fault control, five delegating store wrappers, non-cascading disposal"
    requirement: STR-01
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/FaultingBindingTests.cs"
        status: pass
      - kind: unit
        ref: "command: dotnet build src/Iwasug.WhatsApp.Sdk.Testing -warnaserror (0 warnings, 0 errors)"
        status: pass
    human_judgment: false
  - id: D2
    description: "StoreContractRules shared helpers: durability-await (independent second-instance read), two-account isolation, parallel no-cross-reads (keyed + keyless)"
    requirement: STR-01
    verification:
      - kind: unit
        ref: "src/Iwasug.WhatsApp.Sdk.Testing/Rules/StoreContractRules.cs — exercised by all five contract classes' durability + parallel facts"
        status: pass
    human_judgment: false
  - id: D3
    description: "Five contract classes carry failure-vs-missing (throw vs null), durability-await, and parallel cross-read facts; full suite green across InMemory + Scratch bindings"
    requirement: STR-01
    verification:
      - kind: unit
        ref: "command: dotnet test Iwasug.WhatsApp.Sdk.sln (135 total, 0 failed, Passed!)"
        status: pass
    human_judgment: false
  - id: D4
    description: "Fault-injection facts match the failure/durability filter: 11 tests matching *FailReads* and 14 matching *Faulting* pass"
    requirement: STR-01
    verification:
      - kind: unit
        ref: "command: dotnet exec Iwasug.WhatsApp.Sdk.Tests.dll -method '*FailReads*' / -method '*Faulting*' (11 + 14 passed, exit 0)"
        status: pass
    human_judgment: false

# Metrics
duration: 26min
completed: 2026-10-08
status: complete
---

# Phase 1 Plan 3: Store Contract Fault Injection + Rules Kit Summary

**Kit-public FaultingStoreFactory decorator (None/FailReads/FailWrites → SdkStoreException) plus StoreContractRules helpers, wiring failure≠missing, durability-await, and parallel no-cross-read facts into all five store contract classes**

## Performance

- **Duration:** 26 min
- **Started:** 2026-10-08T05:56:25Z
- **Completed:** 2026-10-08T06:23:11Z
- **Tasks:** 2
- **Files modified:** 9 (+1200/-10 lines)

## Accomplishments

- `FaultingStoreFactory` ships as kit-public API: one sealed class implementing all five store factory interfaces, five delegating store wrappers, `FaultMode` (None/FailReads/FailWrites) consulted at every operation, throwing `SdkStoreException` — hosts get failure≠missing enforcement without writing their own fault plumbing (Pitfall 2 closed)
- `StoreContractRules` gives every family the same rule mechanics: `AssertWriteVisibleToIndependentReadAsync` (D-06 persistence ordering via second-instance visibility), `AssertTwoAccountsIsolatedAsync` (D-02), `AssertParallelAccessNoCrossReadsAsync` keyed + keyless (Task.Run-forced genuine concurrency, ConcurrentBag leak collection, fails listing leaked iterations)
- All five contract classes now carry the D-11 rule families: failure-vs-missing (faulting read THROWS / absent key returns null in distinct assertion contexts), durability-await, and parallel cross-read facts — keyed shape for session/identity/prekey/cache, keyless for safety state
- `FaultingBindingTests` proves the decorator itself is seam-correct (Mode=None CRUD pass-through, FailReads/FailWrites throw, two-account isolation via the shared helper) — third binding family alongside InMemory and Scratch
- Full suite green: 135 tests, 0 failed, 0 skipped across all three binding families

## Task Commits

Each task was committed atomically:

1. **Task 1: FaultingStoreFactory decorator + shared rule helpers** - `bc9bda3` (feat)
2. **Task 2: Upgrade all five contract classes with fault-injection, durability, and concurrency facts** - `a3535db` (feat)

## Files Created/Modified

- `src/Iwasug.WhatsApp.Sdk.Testing/FaultInjection/FaultingStoreFactory.cs` — kit-public fault-injecting decorator + five store wrappers + FaultMode enum
- `src/Iwasug.WhatsApp.Sdk.Testing/Rules/StoreContractRules.cs` — shared D-02/D-06/D-11 rule helpers (kit-public)
- `tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/FaultingBindingTests.cs` — faulting binding family (decorator seam-correctness)
- `src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/{SignalSession,Identity,PreKey,RecentMessageCache,SafetyState}StoreContractTests.cs` — +4 facts each (cache +1 FailWrites fact), XML docs updated
- `src/Iwasug.WhatsApp.Sdk.Testing/PublicAPI.Unshipped.txt` — 71 new entries (50 Task-1 symbols + 21 contract-fact symbols)

## Decisions Made

- **One decorator class, explicit interface implementations:** all five factory interfaces share the `CreateAsync(AccountId, CancellationToken)` signature but differ by return type, so only the session seam is implicit. Callers bind non-session seams through interface-typed variables (`IPreKeyStoreFactory factory = new FaultingStoreFactory(preKey: inner) { Mode = ... }`). Pattern documented in the class XML doc.
- **`iterations` is a required parameter:** RS0026 ("Do not add multiple overloads with optional parameters") forbids the `int iterations = 100` default on both overloads; callers pass 100 explicitly.
- **Fault facts pin the store PRIMITIVE contract:** the cache's FailWrites fact asserts Put throws under fault and succeeds with Mode=None, with the D-20 note that SDK-side log-and-continue is Phase 4/5 policy outside the store contract.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] RS0026: multiple overloads with optional parameters**
- **Found during:** Task 1 (first build of StoreContractRules)
- **Issue:** Both `AssertParallelAccessNoCrossReadsAsync` overloads carried `int iterations = 100` — PublicAPI analyzer error RS0026 (warnings-as-errors repo-wide)
- **Fix:** Removed the default; `iterations` is required on both overloads; XML docs updated to say callers pass 100
- **Files modified:** src/Iwasug.WhatsApp.Sdk.Testing/Rules/StoreContractRules.cs
- **Verification:** Rebuild — RS0026 gone, later full suite green
- **Committed in:** bc9bda3 (Task 1 commit)

**2. [Rule 3 - Blocking] PublicAPI.Unshipped.txt additions (71 entries)**
- **Found during:** Task 1 and Task 2 (builds)
- **Issue:** Plan said "Update kit PublicAPI.Unshipped.txt only if the contract-class signatures changed (they should not — added facts only)" — but every new public symbol (decorator members, rule helpers, and each new `[Fact]` on the public abstract contract classes) is flagged RS0016 under warnings-as-errors
- **Fix:** Mechanically harvested RS0016 signature strings from build output and appended them (50 in Task 1, 21 in Task 2); no hand-written signatures
- **Files modified:** src/Iwasug.WhatsApp.Sdk.Testing/PublicAPI.Unshipped.txt
- **Verification:** Rebuilds clean (0 warnings, 0 errors)
- **Committed in:** bc9bda3, a3535db

**3. [Rule 3 - Blocking] Explicit-interface-impl binding: fault facts initially mis-typed the store**
- **Found during:** Task 2 (first build after adding fault facts)
- **Issue:** `var store = await faulting.CreateAsync(...)` on the decorator resolved to the public session-typed `CreateAsync` for ALL seams (CS7036/CS1061 in identity/prekey/cache/safety facts)
- **Fix:** Interface-typed variables (`IIdentityStoreFactory faulting = ...` etc.) so the correct explicit interface implementation dispatches; binding pattern added to the decorator's class XML doc; cache FailWrites fact keeps a separate decorator reference for Mode mutation
- **Files modified:** 4 contract class files, src/Iwasug.WhatsApp.Sdk.Testing/FaultInjection/FaultingStoreFactory.cs (doc only)
- **Verification:** Testing project builds clean; full suite 135/0 failed
- **Committed in:** a3535db (Task 2 commit)

**4. [Rule 3 - Blocking] Verify command dialect: `--filter` is VSTest-only in this repo**
- **Found during:** Task 2 (verification)
- **Issue:** Plan's `dotnet test tests/... --filter "FullyQualifiedName~FailReads|..."` fails under the repo's MTP/xunit.v3 setup (exit 5; `--filter-fqn` also unsupported)
- **Fix:** Ran the equivalent through the xunit v3 runner: `dotnet exec Iwasug.WhatsApp.Sdk.Tests.dll -method "*FailReads*"` → 11 passed, and `-method "*Faulting*"` → 14 passed (OR-split across two runs; both exit 0, far above the ≥2 acceptance threshold)
- **Verification:** Both filter runs green; full sln run also green (135/0)
- **Committed in:** n/a (verification only, no code change)

---

**Total deviations:** 4 auto-fixed (4 × Rule 3 blocking)
**Impact on plan:** All fixes were required to compile/verify under the repo's warnings-as-errors + PublicAPI + MTP configuration. No scope creep — the delivered semantic contract matches the plan exactly.

## Issues Encountered

- The user's pwsh profile (oh-my-posh) intermittently injected prompt text ("clean — nothing to commit") into captured git-command output; worked around by running git via `cmd /c` for staging/committing. Cosmetic only — no effect on repo state.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- D-11's five rule families now have mechanical kit enforcement (durability-await, failure≠missing, isolation, trimming [01-02], concurrency); plan 01-04 can build on the decorator + helpers
- Three binding families (InMemory, Scratch, Faulting-wrapped) all green — future store work inherits the fault facts automatically via contract-class inheritance
- No blockers

---
*Phase: 01-foundation-store-seam*
*Completed: 2026-10-08*

## Self-Check: PASSED

- Files exist: FaultingStoreFactory.cs, StoreContractRules.cs, FaultingBindingTests.cs, 01-03-SUMMARY.md — all True
- Commits exist: `bc9bda3` (Task 1), `a3535db` (Task 2) — verified via git log; ledger base `d02b336` matches `plan_head_before`
- Measured commits: `git rev-list --count d02b3366..HEAD` = 2 (matches frontmatter)

