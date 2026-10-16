---
phase: 09-ship
plan: 01
subsystem: infra
tags: [nuget, packaging, snupkg, sourcelink, msbuild, xunit, pairing, test-repair, release-readiness]
requires:
  - phase: 08-broadcast-extended-messaging
    provides: "feature-complete SDK and test baseline (709 total, 4 known PairingLifecycleTests failures)"
provides:
  - "scripts/verify-pack.ps1 - fail-closed post-pack content gate (exact package set, README/LICENSE/icon/XML, snupkg siblings, clean version shape)"
  - "unpackable PairingSample (IsPackable=false) - a fresh pack emits exactly the two intended packages"
  - "packaging switches: snupkg symbols, SourceLink publish, PackageIcon, LICENSE + icon packing, PackageProjectUrl"
  - "assets/icon.png - 128x128 flat neutral mark (no WhatsApp/Meta trademarks)"
  - "repaired PairingLifecycleTests - full suite 713/713 green, no assertion weakened"
affects: [09-04-release-workflow, 09-07-ci-workflow, 09-08-publish]
actuals:
  tokens: 3320
  tasks: 3
  commits: 3
  plan_head_before: c7eb8be18488c84f195df01dfaba5bcaf69f11e3
  plan_head_after: 50a124f81b0099c4b8ef058f970a904e62752179
tech-stack:
  added: []
  patterns:
    - "Packable-only switches conditioned on IsPackable in Directory.Build.targets; the only .props packaging addition is PackageProjectUrl metadata"
    - "Fail-closed post-pack gate: verify-pack.ps1 is proven against seeded negative cases before landing"
    - "Injectable-randomness seams record defensive copies - the coordinator owns and zeroizes the handed-out buffers"
key-files:
  created:
    - scripts/verify-pack.ps1
    - assets/icon.png
  modified:
    - samples/Iwasug.WhatsApp.Sdk.PairingSample/Iwasug.WhatsApp.Sdk.PairingSample.csproj
    - Directory.Build.targets
    - Directory.Build.props
    - tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairingLifecycleTests.cs
    - src/Iwasug.WhatsApp.Sdk/Pairing/PairingCoordinator.cs
decisions:
  - "The four PairingLifecycleTests failures were a test-recorder aliasing bug (recorded live arrays zeroized on attempt unwind), not an environment cause: repaired by cloning the recorded buffers; no assertion weakened, no quarantine"
  - "Packaging switches live in the IsPackable-conditioned group in Directory.Build.targets; PackageProjectUrl is the only .props addition"
  - "ContinuousIntegrationBuild stays workflow-time (09-04) - a repo-wide default would also rewrite local Debug builds"
  - "Pre-existing MessagingLoopbackTests flake deferred (deferred-items.md + broken-windows ledger), not repaired - out of scope for this plan"
requirements-completed: [OPS-01, OPS-02]
coverage:
  - id: D1
    description: "A clean pack produces exactly the two intended packages plus .snupkg siblings, and verify-pack.ps1 fails closed on any deviation"
    requirement: OPS-01
    verification:
      - kind: integration
        ref: "Remove-Item artifacts; dotnet pack Iwasug.WhatsApp.Sdk.sln --nologo -o artifacts; pwsh -NoProfile -File scripts/verify-pack.ps1 -ArtifactsPath artifacts -> 2 nupkg + 2 snupkg, verify-pack OK (0.1.347); seeded negatives all exit 1: stray PairingSample nupkg, missing README entry, empty dir, missing snupkg, stripped LICENSE"
        status: pass
    human_judgment: false
  - id: D2
    description: "Both nupkgs are content-complete (README.md, LICENSE, icon.png, lib assembly + XML doc, clean ^0.1.N version) and the snupkg siblings carry symbols"
    requirement: OPS-01
    verification:
      - kind: integration
        ref: "scripts/verify-pack.ps1 assertions 5-6 on both packages; entries include README.md, LICENSE, icon.png, lib/net10.0/Iwasug.WhatsApp.Sdk.dll + Iwasug.WhatsApp.Sdk.xml"
        status: pass
    human_judgment: false
  - id: D3
    description: "The four PairingLifecycleTests timeouts are repaired at the root cause with no assertion weakened or deleted, and the full suite is green"
    requirement: OPS-02
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairingLifecycleTests.cs#Companion_finish_from_a_rotated_code_epoch_is_rejected, Companion_finish_with_foreign_bundle_key_fails_closed, Persist_window_suppresses_watchdog_timeout_until_durable_success, Companion_hello_ack_finish_reaches_verified_success (filter run: 21 total, 0 failed)"
        status: pass
      - kind: unit
        ref: "dotnet test Iwasug.WhatsApp.Sdk.sln -> total 713, failed 0, succeeded 713, skipped 0 (run twice on 2026-10-17: 40s, 44s)"
        status: pass
    human_judgment: false
metrics:
  duration: 45m
  completed: 2026-10-17
status: complete
---

# Phase 9 Plan 01: Release-Readiness Packaging + Suite Repair Summary

**Both NuGet packages are content-complete (README/LICENSE/icon/XML plus .snupkg symbol siblings at a clean 0.1.N version) behind a fail-closed verify-pack gate, and the four PairingLifecycleTests timeouts are repaired at the root cause for a 713/713 green suite**

## Performance

- **Duration:** 45m
- **Started:** 2026-10-17T05:30:13+07:00 (plan-head commit c7eb8be; execution spawned immediately after)
- **Completed:** 2026-10-17T06:15:00+07:00
- **Tasks:** 3
- **Files modified:** 7

## Accomplishments

- Fresh `dotnet pack Iwasug.WhatsApp.Sdk.sln -o artifacts` now emits exactly 2 nupkg + 2 snupkg (0.1.347) - the PairingSample third-package defect (Pitfall 1) is gone; `IsPackable=false` is the sample's only behavioral change.
- Both packages are content-complete: README.md, LICENSE, icon.png, `lib/net10.0/` assembly + XML doc; snupkg siblings present; versions identical and plain (`^0\.1\.\d+$`, no `-g` suffix).
- `scripts/verify-pack.ps1` grew assertions 5 (per-package content: README/LICENSE/icon/lib assembly + XML) and 6 (snupkg sibling per nupkg, matching IDs and versions); five seeded negative cases all exit 1 (stray nupkg, missing README, empty dir from Task 1; missing snupkg, stripped LICENSE in Task 3).
- The four PairingLifecycleTests timeouts were repaired at the root cause: `RecordingRandomness` retained the live randomness buffers, and `PairingCoordinator` owns and zeroizes salts/IVs/ephemeral seeds on attempt unwind, so the test re-derived the bundle key from a zeroized salt and the companion finish never authenticated (20s waits, then timeouts). The recorder now stores clones; every assertion (including the foreign-key and stale-epoch fail-closed facts) is intact.
- Full suite green twice: 713 total / 0 failed / 0 skipped (40s and 44s runs); `PairingLifecycleTests` filter run 21/21, down from 4 failures at baseline.
- Task 1 ran as the plan's tracer; the tracer feedback gate re-ran the automated verify end-to-end (green) before expansion - no blocking-human gate was present, auto mode inactive (`human_verify_mode=end-of-phase`).

## Task Commits

1. **Task 1: Tracer - exactly two packages, asserted by a pack-verify script** - `be9d911` (feat)
2. **Task 2: Repair the four PairingLifecycleTests failures** - `35bc8a3` (fix)
3. **Task 3: Packaging completeness - icon, LICENSE, snupkg, SourceLink, stronger verify-pack** - `50a124f` (feat)

## Files Created/Modified

- `scripts/verify-pack.ps1` - new fail-closed post-pack content gate (assertions 1-6); the release pipeline runs it immediately before the irreversible push.
- `assets/icon.png` - new 128x128 PNG (slate field, three abstract rounded bars); referenced by `PackageIcon`, packed into both nupkgs; no WhatsApp/Meta marks.
- `samples/Iwasug.WhatsApp.Sdk.PairingSample/Iwasug.WhatsApp.Sdk.PairingSample.csproj` - `IsPackable=false` (the observed third-package fix).
- `Directory.Build.targets` - packable-only switches: `IncludeSymbols`/`SymbolPackageFormat=snupkg`, `PublishRepositoryUrl`, `EmbedUntrackedSources`, `PackageIcon`; LICENSE and icon join README.md as packed repo-root items.
- `Directory.Build.props` - `PackageProjectUrl` next to `RepositoryUrl` (the only .props packaging addition).
- `tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairingLifecycleTests.cs` - `RecordingRandomness.Next` records a clone; class remarks document the aliasing root cause.
- `src/Iwasug.WhatsApp.Sdk/Pairing/PairingCoordinator.cs` - XML doc contract on the `randomBytes` seam param: the coordinator owns the returned buffers and zeroizes them on unwind (doc-only change).

## Decisions Made

- Repaired, not quarantined: the four failures had been attributed to environment (04-01/05-05 notes); this plan proved a deterministic harness cause and fixed it with zero assertion changes.
- Packaging switches stay in the `IsPackable`-conditioned group in `Directory.Build.targets`; `PackageProjectUrl` is the only `.props` addition.
- `ContinuousIntegrationBuild` deliberately remains workflow-time (`-p:` in 09-04) - a repo-wide default would also rewrite local Debug builds.
- The pre-existing `MessagingLoopbackTests` flake was documented and deferred rather than repaired (see below) - staying inside the plan's scope boundary.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing Critical] Ownership/zeroization contract documented on the pairing randomness seam**
- **Found during:** Task 2 (repairing PairingLifecycleTests)
- **Issue:** The root cause was invisible in the code: `PairingCoordinator` takes ownership of the buffers returned by the injectable randomness seam and zeroizes salts/IVs/seeds when an attempt unwinds, but nothing on the seam said so - exactly the aliasing trap the test fell into.
- **Fix:** Extended the XML doc on the internal `randomBytes` param with the ownership/zeroization contract (doc-only; no behavior change).
- **Files modified:** `src/Iwasug.WhatsApp.Sdk/Pairing/PairingCoordinator.cs` (not in the plan's Task 2 files list)
- **Verification:** Suite green (713/713); the doc matches the `CryptographicOperations.ZeroMemory` unwind path.
- **Committed in:** `35bc8a3` (part of the Task 2 commit)

---

**Total deviations:** 1 auto-fixed (Rule 2, doc-only)
**Impact on plan:** None on scope or behavior; the note prevents the exact class of test bug from recurring.

## Issues Encountered

- One full-suite run surfaced a pre-existing flaky `System.NullReferenceException` in `MessagingLoopbackTests.decrypt_failure_no_ack_no_event_and_pump_continues` (line 873: `peer` still null because the test continuation outran the peer task's assignment after `ConnectAsync`). Isolated run and two subsequent full-suite runs are green; the file was last touched in 05-05 and the failure is unrelated to this plan's changes. Logged to `deferred-items.md` with a fix direction (`TaskCompletionSource` handoff) and to the broken-windows ledger; not repaired here (out of scope).
- `dotnet test --filter` works under the repo's MTP runner (the README's "VSTest-only" statement is stale - plan 09-06's job).
- Version moved 0.1.346 -> 0.1.347 between Task 1 and Task 3 runs (git-height versioning reacting to the new commits) - expected, and both runs stayed inside the clean `^0\.1\.\d+$` shape.

## User Setup Required

None - no external service configuration required.

## Known Stubs

None.

## Next Phase Readiness

- 09-04 (release workflow) can gate the irreversible `dotnet nuget push` with `scripts/verify-pack.ps1`; the two-package guarantee needs nothing else.
- 09-07 (ci.yml) can gate on the now-green suite; flag the deferred `MessagingLoopbackTests` flake (`deferred-items.md`) as a possible intermittent CI red until fixed.
- Assumption A3 remainder: the four repaired pairing tests pass on Windows/.NET 10.0.12; their first Linux CI run (09-07) is the real cross-platform check.

---
*Phase: 09-ship*
*Completed: 2026-10-17*

## Self-Check: PASSED

- scripts/verify-pack.ps1 exists.
- assets/icon.png exists (128x128 PNG).
- .planning/phases/09-ship/09-01-SUMMARY.md exists.
- .planning/phases/09-ship/deferred-items.md exists.
- Commits be9d911, 35bc8a3, 50a124f all present in HEAD history.
