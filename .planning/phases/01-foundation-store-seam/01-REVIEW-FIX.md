---
phase: 01
fixed_at: 2026-10-08T10:44:52Z
review_path: C:/Users/iwasu/iwasug/sdk-wa/.planning/phases/01-foundation-store-seam/01-REVIEW.md
iteration: 1
findings_in_scope: 10
fixed: 10
skipped: 0
status: all_fixed
---

# Phase 1: Code Review Fix Report

**Fixed at:** 2026-10-08T10:44:52Z
**Source review:** C:/Users/iwasu/iwasug/sdk-wa/.planning/phases/01-foundation-store-seam/01-REVIEW.md
**Iteration:** 1 (`--fix --all`: all 10 open Info findings IN-01..IN-10 in scope)

**Summary:**
- Findings in scope: 10
- Fixed: 10
- Skipped: 0

## Fixed Issues

### IN-01: Unnecessary `partial` modifier on AccountId

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Accounts/AccountId.cs`
**Commit:** 5a19cec
**Applied fix:** dropped `partial` — verified it was the only `partial` declaration in the repo (no second part, no generator). No PublicAPI change (modifier is not surface).

### IN-02: Per-account cache gate never disposed on factory teardown

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs`
**Commit:** 5c207f3
**Applied fix:** `DisposeAsync` now disposes every per-account `RecentMessageCacheState.Gate` known at teardown before clearing state, and the disposal remark documents that pre-disposal views must not be used afterwards (the owning `StoreRegistry` disposes sessions first, so no live view observes this in the SDK-owned flow).
**Status:** fixed: requires human verification (teardown behavior change — full suite green, but Tier 1+2 cannot prove no host holds a pre-disposal cache view across factory disposal).

### IN-03: global.json does not pin the SDK version

**Files modified:** `global.json`
**Commit:** 9f22fe3
**Applied fix:** added `"sdk": { "version": "10.0.401", "rollForward": "latestPatch" }`. Verified safe: installed SDK is exactly 10.0.401.

### IN-04: .editorconfig defects (trailing commas, mislabeled rule comment)

**Files modified:** `.editorconfig`
**Commit:** 0f9d85e
**Applied fix:** removed the inert `= false,` trailing commas, relabeled the S2094 comment over the S1118 rule to S1118, and deleted the duplicated blocks (second expression-bodied-members block, duplicate namespace-declaration rule, duplicate code-block/using-directive sections, duplicate method-group/top-level rules, duplicate indent-labels rule). Verified mechanically: zero duplicate keys, zero trailing-comma lines, correct S1118/S2094 labels.

### IN-05: SignalSessionRecord equality over byte[] is reference-based

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Stores/ISignalSessionStore.cs`
**Commits:** 5dea025, e50a788 (follow-up: plain code text for the `SequenceEqual` reference — the `cref` form was CS0419-ambiguous once shipped docs started validating links)
**Applied fix:** documented on the record that `SessionState` compares by reference and content comparison is the caller's job when value equality is meant.

### IN-06: ComplianceTestBase completeness backstop is vacuous under trimming

**Files modified:** `src/Iwasug.WhatsApp.Sdk.Testing/ComplianceTestBase.cs`
**Commit:** d7b96cb
**Applied fix:** `Every_contract_class_has_a_concrete_subclass` now asserts the discovered base-class count is ≥ 5 (the five kit contract classes) before checking bindings, so an empty discovery fails loudly instead of passing vacuously. The floor comment says to raise it when a sixth contract class is added.

### IN-07: NuGet packaging gaps — XML docs won't ship

**Files modified:** `Directory.Build.props`, `Directory.Build.targets` (new), `src/Iwasug.WhatsApp.Sdk.Testing/Iwasug.WhatsApp.Sdk.Testing.csproj`, `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs`, `src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/*.cs` (5 files), `src/Iwasug.WhatsApp.Sdk.Testing/Rules/StoreContractRules.cs`
**Commits:** c3e23d1, fb6e3ee (follow-up with the doc repairs below)
**Applied fix:**
- Shared repository metadata (`RepositoryUrl` → `https://github.com/iwasug/WhatsApp.Sdk` from the git remote, `RepositoryType`, `PackageTags`) in `Directory.Build.props`.
- New `Directory.Build.targets`: packable-conditioned (`IsPackable == true`) `GenerateDocumentationFile`, explicit `DocumentationFile` → `$(OutputPath)$(AssemblyName).xml`, and `PackageReadmeFile` + repo-root README packing. The condition lives in targets (not props) because `IsPackable` is unset at props-evaluation time; `DocumentationFile` is set explicitly because the SDK's derivation in `BeforeCommon.targets` evaluates before this file is imported (a late `GenerateDocumentationFile` alone emits nothing — verified), and it points at the output dir because pack collects the XML there (obj/-pathed file yields NU5026 — verified).
- The internal test project (`IsPackable=false`) is untouched by doc generation, so its undocumented test methods cannot trip the warnings-as-errors gate.
- Enabling shipped docs surfaced genuine doc bugs, fixed in the follow-up: 5 missing `<param>` tags on the internal `StoreSession` constructor (CS1573), 6 ambiguous `cref`s (CS0419 — overload-ambiguous helper references reduced to type links, `Task.WhenAll` pinned to the `Task[]` overload), and CS1591 relaxed to `NoWarn` for the Testing kit only (justified in the csproj: ~100 inherited `[Fact]` rule methods carry their spec in their names, EF Core style; class/hook-level docs ship; all other doc warnings still fail the build).
- `PackageIcon` deliberately deferred: it needs a designed image asset (nuget.org rejects SVG), flagged for the Phase 9 packaging pass.
- Verified: both nupkgs (`Iwasug.WhatsApp.Sdk`, `Iwasug.WhatsApp.Sdk.Testing`) contain `lib/net10.0/*.xml` + `README.md`.

### IN-08: Once-per-account guarantee rests on closure-over-variable pattern

**Files modified:** `tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreRegistryTests.cs`
**Commit:** 7db57c9
**Applied fix:** added `Parallel_create_calls_for_one_account_create_exactly_once` — 32 parallel `CreateSessionAsync` calls for one account must all observe the same session instance with exactly one factory invocation. A refactor that breaks the lexical Lazy self-capture produces a second session here instead of silently passing.

### IN-09: EnsureUnique is check-then-act

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs`
**Commit:** e23c82d
**Applied fix:** documented the contract on `EnsureUnique`: validation helper, not an atomic reservation — the Phase 7 account-adding API must perform the add atomically (`GetOrAdd`-style insertion whose loser throws `SdkDuplicateAccountException`) and treat this method as a fast pre-check only. No behavior change (the atomic primitive lands with the API that needs it).

### IN-10: `dotnet test --nologo` reports "Zero tests ran" on pinned SDK

**Files modified:** `tests/Iwasug.WhatsApp.Sdk.Tests/Iwasug.WhatsApp.Sdk.Tests.csproj`, `tests/Iwasug.WhatsApp.Sdk.Tests/README.md`
**Commit:** 2f937fc (includes an amend: first version put `--` inside an XML comment, which is illegal — MSB4025 — reworded to avoid double hyphens)
**Applied fix:** csproj comment and test README now record the constraint (invoke `dotnet test` without the nologo flag on SDK 10.0.401; the flag stays fine for `dotnet build`), plus the rebuild-before-counting note and the servicing-update re-test reminder.

## WR-01 residual — verified intact, not re-fixed

Per the run brief, the iteration-3 WR-01 fix (commit 637b98e) was verified, not touched: `CreateSessionCoreAsync` disposed-check, the completed-session re-checks (`IsCompletedSuccessfully` branch via `DisposeAndThrowAsync`, `AwaitCreationAsync`), the drain's skip-not-yet-forced comment, and `InMemoryStoreFactory.ThrowIfDisposed` on all five `CreateAsync` paths are all present. The WR-01 regression test (`CreateSessionAsync_throws_ObjectDisposedException_after_registry_disposal`) passes in the suite below. IN-07/IN-09 edits to `StoreRegistry.cs` are doc-comment-only; no logic touched.

## Verification

- `dotnet build Iwasug.WhatsApp.Sdk.sln --nologo` → **0 warnings / 0 errors** (repo is warnings-as-errors).
- `dotnet test Iwasug.WhatsApp.Sdk.sln` (no `--nologo`, per IN-10) → **168/168 pass** (167 pre-existing + the new IN-08 race test), exit 0.
- `dotnet pack` on both packable projects → nupkgs contain `lib/net10.0/*.xml` + `README.md`.
- Gates ran in the isolated review-fix worktree (branch `gsd-reviewfix/01-11108`, fast-forwarded to `master` on cleanup); the main checkout holds the same sources after the fast-forward but has not been re-tested in place.

## Skipped Issues

None.

---

_Fixed: 2026-10-08T10:44:52Z_
_Fixer: the agent (gsd-code-fixer)_
_Iteration: 1_
