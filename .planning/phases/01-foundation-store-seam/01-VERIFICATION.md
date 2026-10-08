---
phase: 01-foundation-store-seam
verified: 2026-10-08T15:00:00Z
status: passed
score: 3/3 must-haves verified
covered_files:
  - .planning/phases/01-foundation-store-seam/01-01-PLAN.md
  - .planning/phases/01-foundation-store-seam/01-01-SUMMARY.md
  - .planning/phases/01-foundation-store-seam/01-02-PLAN.md
  - .planning/phases/01-foundation-store-seam/01-02-SUMMARY.md
  - .planning/phases/01-foundation-store-seam/01-03-PLAN.md
  - .planning/phases/01-foundation-store-seam/01-03-SUMMARY.md
  - .planning/phases/01-foundation-store-seam/01-04-PLAN.md
  - .planning/phases/01-foundation-store-seam/01-04-SUMMARY.md
  - .planning/phases/01-foundation-store-seam/01-05-PLAN.md
  - .planning/phases/01-foundation-store-seam/01-05-SUMMARY.md
  - .planning/phases/01-foundation-store-seam/01-CONTEXT.md
  - .planning/phases/01-foundation-store-seam/01-VALIDATION.md
  - src/Iwasug.WhatsApp.Sdk.Testing/ComplianceTestBase.cs
  - src/Iwasug.WhatsApp.Sdk.Testing/FaultInjection/FaultingStoreFactory.cs
  - src/Iwasug.WhatsApp.Sdk.Testing/Rules/StoreContractRules.cs
  - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/IdentityStoreContractTests.cs
  - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/PreKeyStoreContractTests.cs
  - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/RecentMessageCacheStoreContractTests.cs
  - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/SafetyStateStoreContractTests.cs
  - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/SignalSessionStoreContractTests.cs
  - src/Iwasug.WhatsApp.Sdk/Accounts/AccountId.cs
  - src/Iwasug.WhatsApp.Sdk/Domain/Jid.cs
  - src/Iwasug.WhatsApp.Sdk/Domain/PreKeyRecord.cs
  - src/Iwasug.WhatsApp.Sdk/Domain/SafetyState.cs
  - src/Iwasug.WhatsApp.Sdk/Domain/SignalIdentity.cs
  - src/Iwasug.WhatsApp.Sdk/Errors/SdkException.cs
  - src/Iwasug.WhatsApp.Sdk/Errors/StoreErrorSurface.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/IIdentityStore.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/IPreKeyStore.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/IRecentMessageCacheStore.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/ISafetyStateStore.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/ISignalSessionStore.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/Runtime/DurableWriteRetryPolicy.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistryOptions.cs
covered_digest: "v2:sha256:c471ea5e459c7fabe9b88b5a4274fe3df2a41e05529fac44b8bc527cce24d1dc"
behavior_unverified: 0
overrides_applied: 0
decision_coverage: {honored: 26, total: 26, not_honored: []}
requirements_verified: [STR-01, STR-02]
---

# Phase 1: Foundation & Store Seam Verification Report

**Phase Goal:** Hosts can plug their own persistence into stable, granular store seams from day one.
**Verified:** 2026-10-08T15:00:00Z
**Status:** passed
**Mode note:** ROADMAP marks this phase `**Mode:** mvp`, but the goal string is NOT in user-story format (`As a …, I want to …, so that …`) — `user-story.validate` returns `valid: false` (missing all three slots). The MVP-mode UAT framing therefore does not apply; standard goal-backward verification against the ROADMAP success criteria was run instead. Surfacing the discrepancy per the MVP verifier guard; consider reformatting the goal via `/gsd-mvp-phase` if MVP UAT framing is wanted for later phases.

## Goal Achievement

### Observable Truths (from ROADMAP.md Success Criteria)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Host can implement its own persistence by plugging granular store interfaces (identity, signal sessions, prekeys, recent-message cache, safety state) — each namespaced per account, no monolithic store | ✓ VERIFIED | Five separate interface files exist (`Stores/ISignalSessionStore.cs`, `IIdentityStore.cs`, `IPreKeyStore.cs`, `IRecentMessageCacheStore.cs`, `ISafetyStateStore.cs`), each with its own `I*StoreFactory` exposing `ValueTask<T> CreateAsync(AccountId, CancellationToken)`; store methods carry no `accountId` parameter (instance is the namespace, D-02). No aggregate facade interface exists anywhere. `StoreRegistryOptions` declares exactly five nullable per-capability factory properties, so a host registers any subset. Kit contract classes are subclassed by a second, structurally different implementation (`ScratchStoreFactory` — plain `Dictionary` + single `lock`, no `ConcurrentDictionary`) and the full kit passes against it. |
| 2 | SDK ships a working in-memory reference store that satisfies every store interface; the test suite runs green against it with no external database dependency | ✓ VERIFIED | `InMemoryStoreFactory` declares all five factory interfaces (`ISignalSessionStoreFactory, IIdentityStoreFactory, IPreKeyStoreFactory, IRecentMessageCacheStoreFactory, ISafetyStateStoreFactory`) and implements per-account views for each; the five `InMemory*ContractTests` bindings run the shared kit against it. `dotnet test Iwasug.WhatsApp.Sdk.sln` → `Passed! total: 161, failed: 0`; no DB/network packages referenced by any project (xunit.v3, MTP, NSubstitute, AwesomeAssertions, TimeProvider.Testing only). |
| 3 | Swapping the reference store for a host-provided implementation requires zero SDK code changes — the same store-contract test suite passes against both | ✓ VERIFIED | `tests/.../StoreContract/ScratchStoreFactory.cs` provides a deliberately different implementation; five `Scratch*ContractTests` binding subclasses run the SAME abstract kit classes against it, plus `HostStyleSecondRunTests.cs` drives the entire kit (all five capabilities + `FaultingStoreFactory` FailReads/None pair + completeness check) against `ScratchStoreFactory` with zero references to `InMemoryStoreFactory` in that file (grep count 0). Kit source has no compile-time dependency on the reference impl. Full suite (both binding families + Faulting family) green: 161/161. |

**Score:** 3/3 truths verified (0 present-but-behavior-unverified)

All behavior-dependent invariants asserted by the plan must-haves have dedicated tests that ran green in the single full-suite run: parallel isolation (`AssertParallelAccessNoCrossReadsAsync` present in all five contract classes — 2 refs each), durability-await (`AssertWriteVisibleToIndependentReadAsync` in all five), failure≠missing fault facts (`FaultingStoreFactory` referenced in all five), D-23 disposal cascade (`StoreRegistryTests#Disposing_the_session_disposes_host_factory_instances_too`, `#Disposing_the_registry_disposes_its_cached_sessions`, `#Dispose_failures_aggregate_without_aborting_the_cascade`), and D-18 retry exhaustion (`DurableWriteRetryPolicyTests#Exhausts_attempts_and_throws_SdkStoreException_preserving_last_inner`).

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/Iwasug.WhatsApp.Sdk/Stores/I*Store.cs` (5 files) | Granular store seams + factories | ✓ VERIFIED | Exist, substantive XML-doc'd interfaces; wired into kit + reference impl |
| `src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs` | Reference impl for all five | ✓ VERIFIED | 450 lines, all five factory interfaces, per-account concurrent state, D-09 honesty docs, no static mutables |
| `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs` + `StoreRegistryOptions.cs` | Host-first resolution + in-memory fallback, per-account caching, disposal cascade | ✓ VERIFIED | `InMemoryStoreFactory _fallback`, `ConcurrentDictionary` cache, `IAsyncDisposable` cascade, `EnsureUnique` present |
| `src/Iwasug.WhatsApp.Sdk/Stores/Runtime/DurableWriteRetryPolicy.cs` | Bounded 3-attempt jittered retry | ✓ VERIFIED | `MaxAttempts = 3`, `TimeProvider` ctor injection, clamp-after-jitter, `SdkStoreException` on exhaustion |
| `src/Iwasug.WhatsApp.Sdk/Errors/StoreErrorSurface.cs` | Internal D-21 wrap-and-log | ✓ VERIFIED | `internal static`, logs operation name (no payload interpolation) |
| `src/Iwasug.WhatsApp.Sdk.Testing/FaultInjection/FaultingStoreFactory.cs` | Kit-public fault decorator | ✓ VERIFIED | All five factory interfaces, `FaultMode` None/FailReads/FailWrites, throws `SdkStoreException` |
| `src/Iwasug.WhatsApp.Sdk.Testing/Rules/StoreContractRules.cs` | Shared rule helpers | ✓ VERIFIED | `AssertTwoAccountsIsolatedAsync`, `AssertWriteVisibleToIndependentReadAsync`, `AssertParallelAccessNoCrossReadsAsync` (keyed + keyless) |
| `src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/*ContractTests.cs` (5) | Contract classes with full D-11 rule families | ✓ VERIFIED | Each class: 1 durability fact, 2 parallel refs, 3–4 Faulting refs |
| `tests/.../StoreContract/ScratchStoreFactory.cs` + 5 scratch bindings | Second impl proving swap | ✓ VERIFIED | `lock`-guarded plain Dictionary, no `ConcurrentDictionary`; scratch filter previously green and included in the 161 |
| `tests/.../StoreContract/HostStyleSecondRunTests.cs` | Executed formal proof of SC3 | ✓ VERIFIED | 7 `Host_style_` facts covering all five capabilities + fault pair + completeness; zero `InMemoryStoreFactory` refs |
| `src/Iwasug.WhatsApp.Sdk.Testing/Iwasug.WhatsApp.Sdk.Testing.csproj` | Kit as pure library | ✓ VERIFIED | Only `xunit.v3.extensibility.core`; no runnable xunit; no `OutputType Exe`; kit bin has no `.exe` apphost |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `InMemorySignalSessionStoreContractTests` | `SignalSessionStoreContractTests` | sealed subclass overriding `CreateFactory()` | ✓ WIRED | Pattern `: SignalSessionStoreContractTests` + `new InMemoryStoreFactory()` |
| `Scratch*ContractTests` (5) | same kit classes | same binding shape | ✓ WIRED | All five scratch bindings present and green in suite |
| `InMemoryStoreFactory` | all five `I*StoreFactory` interfaces | explicit/implicit interface impls | ✓ WIRED | Class declaration lists all five; CreateAsync variants return typed views |
| Contract classes | `FaultingStoreFactory` | wrap `CreateFactory()` output in fault facts | ✓ WIRED | Present in all five files |
| Contract classes | `StoreContractRules` | durability + parallel facts call helpers | ✓ WIRED | refs in all five files |
| `StoreRegistry` | `InMemoryStoreFactory` | fallback field | ✓ WIRED | `private readonly InMemoryStoreFactory _fallback` |
| `HostStyleSecondRunTests` | `ComplianceTestBase` completeness check | scratch-context completeness fact | ✓ WIRED | `Host_style_completeness_check_passes_in_scratch_context` |

### Data-Flow Trace (Level 4)

N/A — Phase 1 produces no UI/rendering surface. Store records (`SignalSessionRecord`, `RecentMessageEntry`, etc.) flow from store contract tests through the factory → account-scoped view → assertions; verified behaviorally by the green round-trip/isolation/trim facts.

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| Full solution builds warnings-as-errors | `dotnet build Iwasug.WhatsApp.Sdk.sln --nologo` | `Build succeeded. 0 Warning(s) 0 Error(s)` | ✓ PASS |
| Full suite green, no external deps | `dotnet test Iwasug.WhatsApp.Sdk.sln` | `Passed! total: 161, failed: 0, succeeded: 161` | ✓ PASS |
| Cache interface primitives-only (no policy params) | grep `maxEntries|ttl` in `IRecentMessageCacheStore.cs` | zero hits; `TrimOldestAsync`/`PurgeExpiredAsync` present | ✓ PASS |
| Kit stays a class library (no apphost) | kit csproj + bin output | only `xunit.v3.extensibility.core` ref, no Exe output | ✓ PASS |
| No wire vocabulary in Domain/ | grep `Node|MessageId|ReceiptType` in `Domain/*.cs` | only a doc scope-guard mention in `Jid.cs` | ✓ PASS |
| Decision coverage | `check.decision-coverage-verify` | 26/26 honored | ✓ PASS |

Per the once-per-verification rule, the single full `dotnet test` run above is the suite-level behavioral evidence; no per-truth re-runs were performed. MTP dialect note: `--filter` is VSTest-only in this repo, so filtered spot-checks used grep/presence plus the single suite run.

### Probe Execution

No `scripts/*/tests/probe-*.sh` probes declared; phase PLAN/SUMMARY mention no probe drivers. SKIPPED.

### Requirements Coverage

| Requirement | Source Plans | Description | Status | Evidence |
|-------------|--------------|-------------|--------|----------|
| STR-01 | 01-01, 01-02, 01-03, 01-04, 01-05 | Host can plug its own persistence via granular store interfaces, namespaced per account | ✓ SATISFIED | Five interfaces + factories present; `StoreRegistryOptions` subset registration; ScratchStoreFactory/HostStyleSecondRun prove host-side implementation with zero SDK changes; kit enforces the semantic contract |
| STR-02 | 01-01, 01-02, 01-05 | SDK ships a working in-memory reference store | ✓ SATISFIED | `InMemoryStoreFactory` implements all five seams; InMemory contract bindings green; suite green with no external DB |

No orphaned requirement IDs: REQUIREMENTS.md maps exactly STR-01/STR-02 to Phase 1 and every plan claims both.

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| `src/.../Domain/SignalIdentity.cs` | 8 | "Phase-1 placeholder fields" in XML doc | ℹ️ Info | Intentional per plan (real key material arrives Phase 4); not a stub — the type is wired and exercised |
| `src/.../Domain/Jid.cs` | 27 | word "node trees" in doc | ℹ️ Info | Scope-guard documentation only, not a declared type |

No `TBD`/`FIXME`/`XXX` markers in any phase source file. No disabled tests (no `skip`/`Ignore` patterns found in kit or test files during scan). D-09 honesty text present in `InMemoryStoreFactory` XML docs ("does NOT survive process restart") — the judgment-tier prohibition on production-persistence framing reads as honored; flagged for human review per the prohibition routing.

### Test Quality Audit

Requirement-linked test files (`StoreContract/*`, `Stores/*`, `Core/*`) all actively run in the 161-test suite — no disabled tests, no circular expected-value generation (fixtures are constructed inline from typed records, not captured from the system under test), assertion level at value/behavioral tier (put→get equality, ordering, isolation, fault-vs-null distinction) rather than existence-only. Verdict: sufficient.

### Human Verification Required

N/A — Infrastructure/foundation phase with no user-facing elements; all acceptance criteria are verifiable programmatically. One judgment-tier prohibition (D-09 honesty wording) is flagged for human review: README states the project is unofficial/not affiliated and the in-memory store's restart limitation, and `InMemoryStoreFactory` XML docs repeat it — wording adequacy is a human call, not a programmatic one.

### Gaps Summary

None. All three ROADMAP success criteria verified against the codebase with a green build (0 warnings, warnings-as-errors) and a green 161-test suite. STR-01 and STR-02 both satisfied with mechanical swap proof (Scratch + HostStyleSecondRun). Phase goal achieved.

---

_Verified: 2026-10-08T15:00:00Z_
_Verifier: the agent (gsd-verifier)_
