---
phase: 01-foundation-store-seam
plan: 04
subsystem: database
tags: [store-registry, in-memory-fallback, retry-policy, disposal-cascade, error-surfacing, dotnet]

# Dependency graph
requires:
  - phase: 01-foundation-store-seam (plan 01-02)
    provides: five store seams (I*Store / I*StoreFactory), InMemoryStoreFactory reference implementation, fault-injection test kit
  - phase: 01-foundation-store-seam (plan 01-01)
    provides: SdkException hierarchy (SdkStoreException, SdkDuplicateAccountException, SdkFormatException) and AccountId
provides:
  - StoreRegistry: account-scoped resolution of the five store seams with host-first + in-memory-fallback precedence (D-03) and once-per-account caching (D-24)
  - StoreSession: the resolved five-store view (D-02) with aggregated, idempotent disposal cascade over all SDK-owned instances (D-23)
  - DurableWriteRetryPolicy + RetryOptions: bounded 3-attempt jittered exponential retry with deterministic-time tests (D-18, decision A1)
  - StoreErrorSurface.LogAndWrap (internal): shared D-21 wrap-and-log discipline for host→SDK trust boundaries
  - StoreRegistry.EnsureUnique (internal): D-26 duplicate-account validation for the future account-adding API
  - 19 new unit tests (10 registry, 6 retry policy, 3 error surface); full suite 154 green
affects: [account lifecycle (Phase 7 add/remove APIs), protocol loop (Phase 2+ durable-write routing), cache-writing machinery (Phase 4/5 D-20), store configuration docs]

# Actuals (#2632) — same estimateTokens scale (chars/4 over the realized diff)
actuals:
  tokens: 16000
  tasks: 3
  commits: 3

# Plan commit ledger (#3968)
commits: 3
plan_head_before: 3ded82bb1f07ed9b68dacd8d6f3e209637ef3370
plan_head_after: 460a1ca3eed74627bf97dac62c06cfea63a4e8b3

# Tech tracking
tech-stack:
  added: []  # no new packages — Microsoft.Extensions.TimeProvider.Testing and NSubstitute were already referenced
  patterns: [Lazy-Task once-per-account cache with fault eviction, clamp-after-jitter backoff, wrap-or-passthrough error surfacing, TimeProvider injection for deterministic time]

key-files:
  created:
    - src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs
    - src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistryOptions.cs
    - src/Iwasug.WhatsApp.Sdk/Stores/Runtime/DurableWriteRetryPolicy.cs
    - src/Iwasug.WhatsApp.Sdk/Errors/StoreErrorSurface.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreRegistryTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Stores/DurableWriteRetryPolicyTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreErrorSurfaceTests.cs
  modified:
    - src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs
    - src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt
    - src/Iwasug.WhatsApp.Sdk/Iwasug.WhatsApp.Sdk.csproj

key-decisions:
  - "D-24 cache shape: ConcurrentDictionary<AccountId, Lazy<Task<StoreSession>>> — Lazy memoizes creation so parallel callers observe exactly one creation; a faulted creation is evicted (exact-pair TryRemove) so a later caller retries instead of hitting a poisoned cache"
  - "StoreSession is a resolution RESULT, not a registration surface: internal ctor, only the registry creates it, disposing it self-evicts from the owning registry (D-01/D-24)"
  - "Disposal ownership (D-23): the SDK owns fallback instances AND instances obtained by invoking host factories; StoreSession disposes all five sequentially and aggregates failures so every store gets a dispose attempt (T-01-13)"
  - "Retry backoff (A1): raw = BaseDelay * 2^attemptIndex, ±30% jitter via Random.Shared, THEN clamp to [50, 250]ms — clamp-after-jitter keeps every observed delay in bounds and keeps attempt N's max below attempt N+1's min (growth provable under adversarial jitter)"
  - "D-21 surfacing: StoreErrorSurface.LogAndWrap (internal static) is the single wrap-and-log shape; StoreRegistry.InvokeLoggedAsync delegates to it; exhaustion messages name the operation, never payloads (T-01-12)"
  - "OCE and SdkException are never retried (cancellation is host intent; SDK-typed failures are already shaped)"
  - "Hand-written recording ILogger fake instead of NSubstitute for log assertions — NSubstitute cannot reliably intercept the generic ILogger.Log<TState> call"

patterns-established:
  - "Resolve-once cache: ConcurrentDictionary<AccountId, Lazy<Task<StoreSession>>> with exact-pair eviction on fault and on dispose"
  - "Error surfacing: throw StoreErrorSurface.LogAndWrap(ex, logger, operation, account) at host→SDK trust boundaries"
  - "Deterministic time: TimeProvider constructor injection; tests drive FakeTimeProvider.Advance and assert delay bounds behaviorally"
  - "Test dialect (MTP): dotnet exec <testdll> -method \"*Name*\" for filtered runs; pass CancellationToken.None explicitly (xUnit1051); discard NSubstitute Received() ValueTasks with _ = (CS4014)"

requirements-completed: [STR-01]

coverage:
  - id: D1
    description: "StoreRegistry resolves account-scoped stores host-first with in-memory fallback for unregistered capabilities (D-03); two accounts are isolated under the shared fallback factory"
    requirement: STR-01
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreRegistryTests.cs#All_fallback_resolution_roundtrips_writes_and_reads"
        status: pass
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreRegistryTests.cs#Host_factory_wins_when_registered_and_fallback_fills_the_rest"
        status: pass
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreRegistryTests.cs#Subset_registration_mixes_host_identity_with_fallback_everything_else"
        status: pass
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreRegistryTests.cs#All_fallback_sessions_are_isolated_per_account"
        status: pass
    human_judgment: false
  - id: D2
    description: "Once-per-account session caching (D-24): same id returns the same StoreSession, factory invoked exactly once; failed creations evict so retry succeeds"
    requirement: STR-01
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreRegistryTests.cs#Same_account_id_returns_the_cached_session_and_creates_once"
        status: pass
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreRegistryTests.cs#Factory_failure_wraps_into_SdkStoreException_then_a_retry_succeeds"
        status: pass
    human_judgment: false
  - id: D3
    description: "Disposal cascade (D-23/T-01-13): disposing the session or registry disposes every SDK-owned store including host-factory instances; a throwing dispose does not abort the cascade — failures aggregate"
    requirement: STR-01
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreRegistryTests.cs#Disposing_the_session_disposes_host_factory_instances_too"
        status: pass
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreRegistryTests.cs#Disposing_the_registry_disposes_its_cached_sessions"
        status: pass
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreRegistryTests.cs#Dispose_failures_aggregate_without_aborting_the_cascade"
        status: pass
    human_judgment: false
  - id: D4
    description: "DurableWriteRetryPolicy (D-18): bounded 3-attempt loop, retry-then-succeed, exhaustion throws SdkStoreException with last inner preserved, OCE/SdkException never retried, backoff stays in [50,250]ms and grows per attempt (proven via FakeTimeProvider)"
    requirement: STR-01
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Stores/DurableWriteRetryPolicyTests.cs#Succeeds_immediately_when_operation_succeeds"
        status: pass
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Stores/DurableWriteRetryPolicyTests.cs#Retries_twice_failing_then_succeeding_operation"
        status: pass
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Stores/DurableWriteRetryPolicyTests.cs#Exhausts_attempts_and_throws_SdkStoreException_preserving_last_inner"
        status: pass
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Stores/DurableWriteRetryPolicyTests.cs#Does_not_retry_OperationCanceledException"
        status: pass
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Stores/DurableWriteRetryPolicyTests.cs#Does_not_retry_SdkException"
        status: pass
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Stores/DurableWriteRetryPolicyTests.cs#Backoff_delays_stay_within_bounds_and_grow_per_attempt"
        status: pass
    human_judgment: false
  - id: D5
    description: "StoreErrorSurface.LogAndWrap (D-21/T-01-12): wraps non-SDK exceptions preserving inner, passes through already-SDK-typed ones, logs once at Warning naming operation + account id, never payloads"
    requirement: STR-01
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreErrorSurfaceTests.cs#Wraps_non_sdk_exception_preserving_inner_and_logs_operation_without_payload"
        status: pass
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreErrorSurfaceTests.cs#Passes_through_an_already_SdkStoreException_without_double_wrapping"
        status: pass
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreErrorSurfaceTests.cs#Null_logger_skips_logging_but_still_wraps"
        status: pass
    human_judgment: false
  - id: D6
    description: "EnsureUnique (D-26): internal uniqueness validation throws SdkDuplicateAccountException for a known account id and passes for unknown; CreateSessionAsync itself never throws for a known id (returns the cache)"
    requirement: STR-01
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreRegistryTests.cs#EnsureUnique_throws_for_a_known_account_and_passes_for_unknown"
        status: pass
    human_judgment: false

# Metrics
duration: 7h 40m wall-clock (includes an inter-session checkpoint pause; execution spanned two sessions)
completed: 2026-10-08
status: complete
---

# Phase 1 Plan 4: Store Composition Layer Summary

**StoreRegistry with host-first + in-memory-fallback resolution and once-per-account caching, aggregated disposal cascade, D-18 bounded durable-write retry with deterministic backoff, and the shared D-21 error-surfacing discipline — 19 new unit tests, full suite 154 green**

## Performance

- **Duration:** 7h 40m wall-clock (2026-10-08T06:29:45Z → 2026-10-08T14:10:04Z; includes an inter-session checkpoint pause mid-Task 1)
- **Started:** 2026-10-08T06:29:45Z
- **Completed:** 2026-10-08T14:10:04Z
- **Tasks:** 3/3
- **Files modified:** 10 (7 created, 3 modified)

## Accomplishments

- `StoreRegistry` composes the five store seams per account: host factories win where registered (D-03), the shared `InMemoryStoreFactory` fills every gap, and resolution is cached once per account via `ConcurrentDictionary<AccountId, Lazy<Task<StoreSession>>>` with fault eviction so failures never poison the cache (D-24)
- `StoreSession` is the resolved five-store view — internal-ctor result, non-null properties, sequential disposal that aggregates failures so every store gets a dispose attempt, idempotent via Interlocked, self-evicting from the owning registry (D-23/T-01-13)
- `DurableWriteRetryPolicy` ships the D-18 contract as production code: 3 bounded attempts, jittered exponential backoff with clamp-after-jitter ordering (every delay in [50, 250]ms, growth provable under adversarial jitter), TimeProvider injection for deterministic tests, exhaustion throwing `SdkStoreException` with the last exception as inner
- `StoreErrorSurface.LogAndWrap` (internal) centralizes the D-21 discipline — wrap-or-passthrough plus one Warning log naming operation and account id, never payloads (T-01-12); `StoreRegistry.InvokeLoggedAsync` already routes through it
- `StoreRegistry.EnsureUnique` delivers the D-26 uniqueness validation now (unit-proven) so the Phase 7 account-adding API consumes it rather than inventing it
- Full suite: **154 passed / 0 failed** (`dotnet test tests/Iwasug.WhatsApp.Sdk.Tests`, exit 0)

## Task Commits

Each task was committed atomically:

1. **Task 1: StoreRegistry — per-account resolution with fallback and precedence** - `2fdc150` (feat)
2. **Task 2: D-18 bounded durable-write retry policy + D-21 error surfacing** - `af3502a` (feat)
3. **Task 3: Registry unit proofs — precedence, caching, disposal cascade, uniqueness** - `460a1ca` (feat)

## Files Created/Modified

- `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs` - StoreRegistry (resolution + cache + disposal + EnsureUnique) and StoreSession (resolved view + cascade) in one file
- `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistryOptions.cs` - Host registration surface: five nullable per-capability factory properties (D-03)
- `src/Iwasug.WhatsApp.Sdk/Stores/Runtime/DurableWriteRetryPolicy.cs` - D-18 bounded retry policy + RetryOptions (A1 defaults)
- `src/Iwasug.WhatsApp.Sdk/Errors/StoreErrorSurface.cs` - Internal D-21 wrap-and-log helper
- `src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemory/InMemoryStoreFactory.cs` - (path normalized: `Stores/InMemory/InMemoryStoreFactory.cs`) gained `IAsyncDisposable.DisposeAsync` so the fallback factory joins the D-23 cascade [Rule 3 deviation]
- `src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt` - Entries for StoreRegistry, StoreRegistryOptions, StoreSession, InMemoryStoreFactory.DisposeAsync, RetryOptions, DurableWriteRetryPolicy
- `src/Iwasug.WhatsApp.Sdk/Iwasug.WhatsApp.Sdk.csproj` - `InternalsVisibleTo` for the tests assembly [Rule 3 pull-forward from Task 3]
- `tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreRegistryTests.cs` - 10 facts: all-fallback (roundtrip + isolation), host-first, subset-mix, same-id-cached, EnsureUnique, session/registry disposal cascades, aggregate dispose failures, factory-failure wrap + fault-eviction retry
- `tests/Iwasug.WhatsApp.Sdk.Tests/Stores/DurableWriteRetryPolicyTests.cs` - 6 facts incl. the FakeTimeProvider backoff-bounds/growth proof and payload-discipline assertions
- `tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreErrorSurfaceTests.cs` - 3 facts via a hand-written recording ILogger fake [extra file beyond plan's list]

## Decisions Made

- **Cache shape:** `Lazy<Task<StoreSession>>` values in a `ConcurrentDictionary` — the Lazy memoizes its factory so exactly one creation survives parallel races; the session's eviction callback removes exactly its own KeyValuePair so a concurrently re-created entry is never dropped
- **Creation-failure handling:** host/fallback factory failures are logged once and wrapped into `SdkStoreException` (inner preserved; OCE passes through unwrapped); a failed creation evicts itself so the next caller retries with fresh stores
- **Rollback on partial creation:** stores created before a later capability fails are SDK-owned (D-23) and are disposed best-effort before the failure propagates — the original creation failure is what the caller sees
- **Backoff ordering:** clamp-AFTER-jitter (planner A1) — keeps the deterministic-time test formula-faithful under any jitter sequence and bounds every observed delay to [BaseDelayMs, MaxDelayMs]
- **Provenance:** StoreSession privately tracks Fallback vs HostFactory origin per store for diagnostics; disposal treats both paths identically as SDK-owned
- **Retry policy has no Phase 1 caller by design:** the plan ships the wrapper as production code with unit proofs and declares the durable-write routing contract on the consumer side (StoreRegistry/StoreSession XML docs); the Phase 2+ protocol loop is the first real caller

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] InMemoryStoreFactory now implements IAsyncDisposable**
- **Found during:** Task 1 (build: CS8121 — the `_fallback is IAsyncDisposable` pattern could never match)
- **Issue:** The registry's disposal cascade disposes the fallback factory, but the factory implemented no disposal — plan 01-02's in-code comment anticipated exactly this wiring ("factory-level disposal clearing all account state is wired in plan 01-04")
- **Fix:** Added `IAsyncDisposable` to the class + `DisposeAsync()` that clears all account state (views keep operating on their own references)
- **Files modified:** src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs, PublicAPI.Unshipped.txt
- **Verification:** build 0W/0E; registry disposal tests pass
- **Committed in:** 2fdc150 (Task 1 commit)

**2. [Rule 3 - Blocking] InternalsVisibleTo added in Task 2 (plan placed it in Task 3)**
- **Found during:** Task 2
- **Issue:** Task 2's action mandates unit-testing the internal `StoreErrorSurface`, but the `InternalsVisibleTo` item was only listed under Task 3's acceptance criteria — the Task 2 tests cannot compile without it
- **Fix:** Added `<InternalsVisibleTo Include="Iwasug.WhatsApp.Sdk.Tests" />` to the SDK csproj during Task 2
- **Files modified:** src/Iwasug.WhatsApp.Sdk/Iwasug.WhatsApp.Sdk.csproj
- **Verification:** StoreErrorSurfaceTests compile and pass
- **Committed in:** af3502a (Task 2 commit)

**3. [Rule 3 - Plan gap] StoreErrorSurfaceTests.cs added as an extra file**
- **Found during:** Task 2
- **Issue:** The plan mandates "Unit-test it" for LogAndWrap but its `<files>` list contains no test file for that unit
- **Fix:** Created `tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreErrorSurfaceTests.cs` with 3 facts (wrap+log without payload, passthrough, null-logger)
- **Files modified:** tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreErrorSurfaceTests.cs
- **Verification:** 3/3 pass; full suite green
- **Committed in:** af3502a (Task 2 commit)

**4. [Rule 3 - Blocking] Test-runner dialect translation**
- **Found during:** Tasks 2-3 verification
- **Issue:** The plan's `dotnet test --filter "FullyQualifiedName~..."` commands use VSTest-only flags; the repo's xunit.v3 MTP setup rejects them outright (documented in the test csproj and the 01-03 precedent)
- **Fix:** Used the established dialect: `dotnet exec tests/Iwasug.WhatsApp.Sdk.Tests/bin/Debug/net10.0/Iwasug.WhatsApp.Sdk.Tests.dll -method "*DurableWriteRetryPolicy*"` (and `*StoreErrorSurface*`, `*StoreRegistry*`); full suite via `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests`
- **Verification:** filtered runs report 9 and 10 tests respectively, zero failed; full suite 154 passed
- **Committed in:** n/a (execution technique, no file change)

**5. [Rule 2 - Missing mechanism] Recording ILogger fake instead of NSubstitute**
- **Found during:** Task 2 test authoring
- **Issue:** The plan specifies "fake ILogger (NSubstitute)", but NSubstitute cannot reliably intercept the generic `ILogger.Log<TState>` call (the framework's strongly-typed state parameter cannot be expressed as an Arg.Any matcher)
- **Fix:** Hand-written `RecordingLogger` fake capturing rendered entries; assertions target rendered message content
- **Files modified:** tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreErrorSurfaceTests.cs
- **Verification:** log-content assertions (operation + account present, payload absent) pass
- **Committed in:** af3502a (Task 2 commit)

---

**Total deviations:** 5 auto-fixed (3 blocking/plan-gap, 1 missing critical mechanism, 1 technique translation)
**Impact on plan:** All deviations were enabling fixes for plan-mandated behavior; no scope creep. The public API grew by exactly the planned surface plus `InMemoryStoreFactory.DisposeAsync()` (the D-23 cascade the plan itself directs).

## Issues Encountered

- First Task 1 build failed with CS8121/CS8978 (null-conditional method groups `expr?.CreateAsync` cannot convert to delegates) — resolved by hoisting each host factory into a local with an explicit null-check + lambda; the local snapshot also pins the host registration for the whole resolution
- PublicAPI exact strings for the generic `ExecuteAsync<T>` were harvested from RS0016/RS0017 build output (unconstrained `T` is unannotated: `Task<T>!`)
- xUnit analyzers (errors under warnings-as-errors) shaped the test code: xUnit1051 requires an explicit `CancellationToken.None` on optional-CT calls, xUnit1031 forbids `GetAwaiter().GetResult()`, and CS4014 requires discarding NSubstitute `Received()` results of ValueTask-returning members with `_ =`

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- The composition layer is complete and proven: account lifecycle work (Phase 7) can consume `CreateSessionAsync` for adds and `EnsureUnique` for duplicate rejection
- The durable-write routing contract is declared in XML docs (`StoreRegistry`/`StoreSession` → `DurableWriteRetryPolicy`); the Phase 2+ protocol loop must route durable-capability writes through the policy and the recent-message cache through D-20 log-and-continue (cache machinery, Phase 4/5)
- `DurableWriteRetryPolicy` is production code with unit proofs but intentionally has no protocol caller in Phase 1 (plan-documented; not a stub)
- Full suite is green at 154 tests; the store test kit (01-03) plus these tests give Phase 2 a stable persistence base

---
*Phase: 01-foundation-store-seam*
*Completed: 2026-10-08*

## Self-Check: PASSED

- All 7 created source/test files exist on disk; SUMMARY exists at `.planning/phases/01-foundation-store-seam/01-04-SUMMARY.md`
- All 3 task commits verified in history: `2fdc150`, `af3502a`, `460a1ca`
- Commit count measured from the plan ledger (`3ded82bb1f07ed9b68dacd8d6f3e209637ef3370..HEAD`) = 3, matching frontmatter `commits: 3`
- Full suite at write time: 154 passed / 0 failed (exit 0)

