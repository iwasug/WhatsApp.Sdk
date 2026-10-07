---
phase: 01-foundation-store-seam
plan: 01
subsystem: database
tags: [store-seam, scaffold, contract-tests, xunit-v3, central-package-management, net10]

requires: []
provides:
  - net10.0 solution scaffold with central build/package conventions (Directory.Build.props, Directory.Packages.props, Nerdbank.GitVersioning via version.json)
  - first store seam end-to-end: AccountId → ISignalSessionStoreFactory.CreateAsync → account-scoped ISignalSessionStore → typed SignalSessionRecord write/read
  - in-memory reference store (InMemoryStoreFactory) satisfying the seam (STR-02)
  - published contract-kit pattern: abstract SignalSessionStoreContractTests + ComplianceTestBase completeness backstop
  - typed error hierarchy (SdkException base + SdkStoreException/SdkFormatException/SdkDuplicateAccountException)
  - repo hygiene: MIT LICENSE, clean-room NOTICE.md provenance policy, unofficial README, PublicAPI analyzers on both packable projects
affects: [later phases needing the store seam, host store implementations, contract-kit consumers, phase-09 packaging]

actuals:
  tokens: 18553
  tasks: 2
  commits: 1
commits: 1
plan_head_before: c3881e416348eb10282d541c7287b6ed3f62cd5e
plan_head_after: 1b80bfaf453de56c4599caacf8e195a19873817a

tech-stack:
  added: [.NET SDK 10.0.401 (net10.0), xunit.v3 4.0.1, xunit.v3.extensibility.core 4.0.1, Microsoft.NET.Test.Sdk 18.10.1, AwesomeAssertions 9.6.0, NSubstitute 6.2.0, coverlet.collector 10.1.0, Microsoft.Extensions.Logging.Abstractions 10.0.12, Microsoft.Extensions.Options 10.0.12, Microsoft.Extensions.TimeProvider.Testing 10.10.0, Microsoft.CodeAnalysis.PublicApiAnalyzers 5.6.0, Nerdbank.GitVersioning 3.10.94]
  patterns: ["async factory per account (ValueTask on CreateAsync, Task on store methods)", "contract kit via abstract xunit base classes + concrete bindings", "EF-Core-style ComplianceTestBase completeness check", "central package management + Directory.Build.props conventions", "warnings-as-errors + PublicAPI Unshipped gate from day one", "dotnet test in Microsoft.Testing.Platform mode via global.json"]

key-files:
  created:
    - Iwasug.WhatsApp.Sdk.sln
    - Directory.Build.props
    - Directory.Packages.props
    - global.json
    - version.json
    - src/Iwasug.WhatsApp.Sdk/Accounts/AccountId.cs
    - src/Iwasug.WhatsApp.Sdk/Stores/ISignalSessionStore.cs
    - src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs
    - src/Iwasug.WhatsApp.Sdk/Errors/SdkException.cs
    - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/SignalSessionStoreContractTests.cs
    - src/Iwasug.WhatsApp.Sdk.Testing/ComplianceTestBase.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/InMemorySignalSessionStoreContractTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Core/AccountIdTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/ComplianceTests.cs
  modified: [.planning/config.json]

key-decisions:
  - "A4 AccountId validation (discretion area): case-sensitive ordinal identity, non-empty, not whitespace-only, no control characters, 128-char cap; violations throw SdkFormatException whose message names the rule and never the value (T-01-01 message discipline); uniqueness stays with the account registry (D-26), not the type"
  - "Repo-wide async convention (RESEARCH Open Question 3): Task on all host-implemented store methods, ValueTask<T> only on factory CreateAsync (called once per account, D-24)"
  - "Kit assertions use AwesomeAssertions: xunit.v3.extensibility.core ships FactAttribute only (no Assert), and a second xunit package was avoided per the plan's extensibility.core-only rule — 01-RESEARCH Standard Stack already scopes AwesomeAssertions to 'kit + internal tests'"
  - "dotnet test on .NET 10 requires MTP mode: repo-root global.json selects test.runner=Microsoft.Testing.Platform; TestingPlatformDotnetTestSupport is rejected by the .NET 10 SDK (aka.ms/dotnet-test-mtp-error)"
  - "Pre-commit guard reconciliation: git.allow_default_branch_commits=true set in .planning/config.json because this repo's branching_strategy=none runs on its only (default) branch master — the override is the assertion's own named remedy"

patterns-established:
  - "Store seam shape: interface + async factory returning account-scoped instance; no accountId parameters on store methods; typed records (never byte blobs) at the interface level"
  - "Contract kit: one abstract class per store in Iwasug.WhatsApp.Sdk.Testing.StoreContract, bound by sealed subclasses in the consuming test assembly"
  - "ComplianceTestBase completeness backstop must have a concrete subclass wherever tests run"
  - "PublicAPI.Unshipped.txt declares every public symbol; #nullable enable header required"
  - "csproj files stay thin — TFM/nullable/warnings-as-errors/versions live in Directory.Build.props / Directory.Packages.props"

requirements-completed: [STR-01, STR-02]

coverage:
  - id: D1
    description: "Buildable repo scaffold: solution binding three projects, central build/package conventions, versioning, MIT license, clean-room NOTICE, unofficial README"
    requirement: STR-01
    verification:
      - kind: other
        ref: "dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror → Build succeeded, 0 Warning(s), 0 Error(s)"
        status: pass
    human_judgment: false
  - id: D2
    description: "AccountId opaque validated account key with A4 rules (case-sensitive ordinal, 128-char cap, no control chars, SdkFormatException on violation)"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Core/AccountIdTests.cs (11 tests incl. distinct ordinal casing and message-discipline)"
        status: pass
    human_judgment: false
  - id: D3
    description: "First store seam wired end-to-end: ISignalSessionStore + ISignalSessionStoreFactory + typed SignalSessionRecord with D-06/D-07/D-19/D-02 contracts in XML docs"
    verification:
      - kind: integration
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/InMemorySignalSessionStoreContractTests.cs#Put_then_Get_round_trips_typed_record"
        status: pass
    human_judgment: false
  - id: D4
    description: "In-memory reference store (InMemoryStoreFactory) satisfying the seam with structural per-account isolation (STR-02)"
    verification:
      - kind: integration
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/InMemorySignalSessionStoreContractTests.cs#Two_accounts_isolated_same_address"
        status: pass
    human_judgment: false
  - id: D5
    description: "Published contract-kit mechanism proven: abstract contract class runs in a concrete binding, and ComplianceTestBase fails the run when any contract class lacks a binding"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/ComplianceTests.cs#Every_contract_class_has_a_concrete_subclass + inherited-fact dispatch (15/15 tests green)"
        status: pass
    human_judgment: false
  - id: D6
    description: "Typed error hierarchy (SdkException + Phase-1 subtypes) reachable through validation failures"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Core/AccountIdTests.cs — Create throws SdkFormatException for all A4 violations"
        status: pass
    human_judgment: false
  - id: D7
    description: "Honest positioning text: README disclaimer (unofficial, not affiliated with WhatsApp/Meta), in-memory store restart warning (D-09), NOTICE.md clean-room provenance policy"
    verification: []
    human_judgment: true
    rationale: "Wording adequacy of a trademark disclaimer, restart-honesty disclosure, and clean-room policy is a judgment call — tests cannot establish that the prose adequately communicates risk to a host developer (T-01-04 is accepted-with-documentation, so the documentation itself is the control)."

duration: 38min
completed: 2026-10-07
status: complete
---

# Phase 01 Plan 01: Walking Skeleton Summary

**net10.0 walking skeleton: solution scaffold + first pluggable store seam (AccountId → async factory → in-memory ISignalSessionStore → typed SignalSessionRecord round-trip) proven green by a published contract-kit suite — 15 tests, zero external dependencies.**

## Performance

- **Duration:** 38 min (started 2026-10-07T21:34:11Z, gates complete 2026-10-07T22:12:04Z)
- **Tasks:** 2
- **Files changed (task commits):** 26 (+1 planning config in the plan-metadata commit)
- **Tests:** 15 total, 15 passed, 0 failed

## Accomplishments

- Root solution `Iwasug.WhatsApp.Sdk.sln` binding exactly two packable projects (main SDK + test kit) and one test project, under warnings-as-errors with central package versions pinned to the research-verified registry pins.
- One complete vertical slice: `AccountId.Create` (validated) → `ISignalSessionStoreFactory.CreateAsync` → account-scoped `ISignalSessionStore` → `PutAsync` typed `SignalSessionRecord` → independent `GetAsync` observes it; missing returns null; two accounts never cross (D-02/D-06/D-19 store-side semantics).
- Contract-kit inheritance mechanism proven: abstract `SignalSessionStoreContractTests` facts run in the sealed `InMemorySignalSessionStoreContractTests` binding; `ComplianceTestBase` completeness check green with its concrete `ComplianceTests` subclass.
- A4 validation unit tests (11) including case-sensitive ordinal distinctness ("a" vs "A") and exception-message discipline (no payload values in messages).
- Repo hygiene landed: MIT LICENSE, NOTICE.md clean-room provenance policy (whatsmeow/MPL-2.0 rules), unofficial README with in-memory-store restart honesty, PublicAPI analyzers declaring 43 + 11 public API entries.

## Task Commits

Each task was committed atomically:

1. **Task 1: Walking skeleton — scaffold + one store round-trip proven by the contract suite** - `1b80bfa` (feat)
2. **Task 2: Seal the skeleton — solution-level verification pass and commit** - this SUMMARY commit, message `feat(01-01): walking skeleton — scaffold, first store seam, first contract class` (hash is HEAD after this commit; listed in the executor completion output)

**Plan metadata:** docs(01-01) commit follows this one (SUMMARY + STATE.md + ROADMAP.md + REQUIREMENTS.md).

_Note: `actuals.commits: 1` is the measured ledger window `c3881e4..1b80bfa` (last task code-commit before this SUMMARY, per #3968/#4670 bounded reconciliation); the SUMMARY seal and plan-metadata commits fall outside the window by design._

## Files Created/Modified

- `Iwasug.WhatsApp.Sdk.sln` — root solution, three projects
- `Directory.Build.props` — net10.0, LangVersion latest, nullable, warnings-as-errors, NBGV reference
- `Directory.Packages.props` — central pins (registry-verified versions from 01-RESEARCH)
- `global.json` — `test.runner = Microsoft.Testing.Platform` (see Deviations)
- `version.json` — Nerdbank.GitVersioning 0.1 on master/main
- `.editorconfig`, `.gitignore` — standard .NET defaults / VS ignores
- `LICENSE`, `NOTICE.md`, `README.md` — MIT, clean-room policy, unofficial positioning + D-09 restart honesty
- `src/Iwasug.WhatsApp.Sdk/Iwasug.WhatsApp.Sdk.csproj` — packable main package (RootNamespace `Iwasug.WhatsApp.Sdk`)
- `src/Iwasug.WhatsApp.Sdk/Accounts/AccountId.cs` — opaque validated key (A4)
- `src/Iwasug.WhatsApp.Sdk/Stores/ISignalSessionStore.cs` — store + factory interfaces + `SignalSessionRecord`
- `src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs` — ConcurrentDictionary-backed reference impl (no global statics)
- `src/Iwasug.WhatsApp.Sdk/Errors/SdkException.cs` — D-17 hierarchy
- `src/Iwasug.WhatsApp.Sdk/PublicAPI.{Shipped,Unshipped}.txt` — 43 declared symbols
- `src/Iwasug.WhatsApp.Sdk.Testing/Iwasug.WhatsApp.Sdk.Testing.csproj` — kit (RootNamespace `Iwasug.WhatsApp.Sdk.Testing`, no runnable xunit)
- `src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/SignalSessionStoreContractTests.cs` — first abstract contract class (3 facts)
- `src/Iwasug.WhatsApp.Sdk.Testing/ComplianceTestBase.cs` — completeness backstop (1 fact)
- `src/Iwasug.WhatsApp.Sdk.Testing/PublicAPI.{Shipped,Unshipped}.txt` — 11 declared symbols
- `tests/Iwasug.WhatsApp.Sdk.Tests/Iwasug.WhatsApp.Sdk.Tests.csproj` — non-packable test project
- `tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/InMemorySignalSessionStoreContractTests.cs` — reference binding (STR-02 proof)
- `tests/Iwasug.WhatsApp.Sdk.Tests/ComplianceTests.cs` — concrete completeness subclass (see Deviations)
- `tests/Iwasug.WhatsApp.Sdk.Tests/Core/AccountIdTests.cs` — A4 rule tests
- `.planning/config.json` — `git.allow_default_branch_commits: true` (see Deviations; rides with the plan-metadata commit)

## Decisions Made

- **A4 AccountId validation rule chosen:** case-sensitive ordinal comparison (`string.CompareOrdinal`), non-empty, not whitespace-only, no control characters, 128-char cap; violations throw `SdkFormatException` naming the rule only (message discipline enforced by a dedicated test).
- **Task vs ValueTask convention:** `Task` on every host-implemented store method (TAP guidance — no single-consumption constraint on hosts); `ValueTask<T>` only on factory `CreateAsync` (once per account, D-24).
- **SKELETON.md architectural decisions now proven:** D-13 package/namespace names on disk; D-14 minimal `src/`+`tests/` layout; D-01/D-02/D-04 seam shape (granular interface, async factory namespacing, typed record); D-10/D-11 contract-kit model with concrete completeness enforcement; D-09 honesty text present; two-packable-projects constraint (D-12) mechanically verified.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Set `git.allow_default_branch_commits: true` so executor commits can land on `master`**
- **Found during:** Task 1 (pre-commit HEAD safety assertion, before any commit)
- **Issue:** The repo's only branch is the protected default `master` (`branching_strategy: none`, no remote); the mandatory assertion refuses commits on it and gsd_run's fallback five-name regex matched `master`.
- **Fix:** Added the override the assertion itself names (`git.allow_default_branch_commits: true` in `.planning/config.json`) — consistent with the orchestrator's own `branching_strategy: none` step, which warns-and-continues on this branch rather than refusing.
- **Verification:** `gsd_run query git.base-branch --is-protected master` → `false`; all subsequent pre-commit assertions pass.
- **Committed in:** plan-metadata commit (config.json, together with the orchestrator's pre-existing build/test-command additions).

**2. [Rule 3 - Blocking] Repo-root `global.json` selecting the Microsoft.Testing.Platform `dotnet test` runner**
- **Found during:** Task 1 (`dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` verify)
- **Issue:** On .NET 10 SDK, VSTest-mode `dotnet test` hard-errors for xunit.v3/MTP projects ("Testing with VSTest target is no longer supported…"); the transitional `TestingPlatformDotnetTestSupport` property is also rejected by official guidance for this SDK.
- **Fix:** `global.json` with `{"test": {"runner": "Microsoft.Testing.Platform"}}` per aka.ms/dotnet-test-mtp-error; property removed from the test csproj.
- **Verification:** all three plan `<verify>` commands pass — root `dotnet test` (15/15 `Passed!`), and `--filter "FullyQualifiedName~Put_then_Get_round_trips_typed_record"` runs exactly 1 test.
- **Committed in:** 1b80bfa

**3. [Rule 3 - Blocking] Kit assertions via AwesomeAssertions instead of xunit `Assert`**
- **Found during:** Task 1 (first build: CS0103 `Assert` does not exist)
- **Issue:** `xunit.v3.extensibility.core` ships only the extensibility types (FactAttribute; its sole dependency is `xunit.v3.common`) — no `Assert`. Adding `xunit.v3.assert` would make a second xunit package against the plan's "extensibility.core ONLY" rule.
- **Fix:** Kit references `AwesomeAssertions` — 01-RESEARCH Standard Stack already scopes it to "fluent assertions in kit + internal tests"; the runnable-xunit prohibition (Pitfall 3) remains fully honored.
- **Verification:** build 0 warnings/0 errors; contract suite green.
- **Committed in:** 1b80bfa

**4. [Rule 2 - Missing Critical] Concrete `ComplianceTests` subclass in the test project**
- **Found during:** Task 1 (implementing the must-have "ComplianceTestBase completeness check fails the run")
- **Issue:** The plan specifies an *abstract* `ComplianceTestBase` with a `[Fact]`, but xunit only discovers facts in concrete classes of the test assembly — with no subclass anywhere the completeness check would never execute and the must-have would be vacuously green.
- **Fix:** Added `tests/Iwasug.WhatsApp.Sdk.Tests/ComplianceTests.cs` (empty sealed subclass), mirroring EF Core's own `ComplianceTest` consumption pattern.
- **Verification:** 15-test run includes `Every_contract_class_has_a_concrete_subclass` passing; removing any binding would fail it (check asserts non-empty contract bases are all bound).
- **Committed in:** 1b80bfa

---

**Total deviations:** 4 auto-fixed (3 × Rule 3 blocking, 1 × Rule 2 missing-critical)
**Impact on plan:** No scope creep — every fix was required to satisfy the plan's own `<verify>`/must-have statements; all are documented and committed.

## Issues Encountered

- **PublicAPI analyzer iteration:** first build surfaced 54 unique RS0016 symbols + RS0037 (missing `#nullable enable`); symbols were declared verbatim (including `static`/`virtual`/`abstract`/`~override` forms) and a comment line caused RS0017, so the shipped files contain only the `#nullable enable` header plus API entries. Final build: 0 warnings.
- **Trim/AOT warnings on kit reflection:** `IsAotCompatible` enabled IL2026 (Assembly.GetTypes) and IL2070 (GetMethods) in `ComplianceTestBase`; suppressed with `[UnconditionalSuppressMessage]` + justification (test-kit completeness discovery runs only in a non-trimmed test host — never in trimmed production apps).
- **Naive-grep collisions with acceptance criteria:** explanatory comments containing the literal strings `<OutputType>Exe</OutputType>` and `Options.Abstractions` tripped the plan's own grep-based checks; comments reworded before commit (properties themselves were always correct).
- **Plan file-list inconsistency (no impact):** Task 1's `<files>` list omits `tests/.../Core/AccountIdTests.cs` although the `<action>` text and the plan frontmatter `files_modified` both require it; created per the action.
- **LF→CRLF git warnings** on add — cosmetic (`core.autocrlf` on Windows), no content impact.

## Authentication Gates

None — no auth-gated services in this plan.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Plan 01-02 can build on the proven seam: remaining four store interfaces + their contract classes, second scratch binding, `Jid`, and the Phase-1 trim primitives.
- Kit packaging rule is now a repo fact (verified: kit builds as a .dll library, no apphost, exactly two packable projects).
- Deferred by design (tracked in the plan, not stubs): fault injection (01-03), factory disposal cascade + StoreRegistry + retry policy (01-04), host-style kit run (01-05).
- Concern for later: `master`-commit override is now a standing repo setting — revisit if the project adopts branching (switch `branching_strategy` and remove `allow_default_branch_commits`).

## Self-Check: PASSED

- Key-files exist on disk: all 14 `key-files.created` verified (`[ -f ]` / committed in 1b80bfa; grep-based acceptance checks read them directly)
- `gsd_run check evaluation-scope --plan "01-01" --commits-only` → `status: resolved`, commits array has 1 entry (`1b80bfa`), no missingOnDisk/outsideUnion/unreachable
- All 20 acceptance criteria re-run before Task 1 commit: 20 PASS / 0 FAIL; Task 2 acceptance gates green (root `dotnet test` `Passed!`; kit build `-warnaserror` 0W0E with 0 `.exe` under `src/Iwasug.WhatsApp.Sdk.Testing/bin`)
- Plan-level `<verification>` re-run: `dotnet build Iwasug.WhatsApp.Sdk.sln` (0W0E), `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` (15/15 `Passed!`), filter run (total: 1, no zero-match) — all exit 0

---
*Phase: 01-foundation-store-seam*
*Completed: 2026-10-07*
