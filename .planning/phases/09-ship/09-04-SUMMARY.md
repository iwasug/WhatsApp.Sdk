---
phase: 09-ship
plan: 04
subsystem: release-engineering
tags: [github-actions, nuget, trusted-publishing, ci, nbgv, protocol-drift, oidc]

requires:
  - phase: 09-ship
    provides: "09-01: verify-pack.ps1 pre-push content gate + repaired suite; 09-03: InteropProbe process contract (0/10/20 exit vocabulary, PROBE_RESULT line)"
provides:
  - ".github/workflows/ci.yml - push(master)/PR blocking build + full suite + isolation gate, IN-10-safe"
  - ".github/workflows/release.yml - v* tag/dispatch Trusted Publishing path (verify-before-push, exactly one push step)"
  - ".github/workflows/live-interop.yml - weekly drift probe with classified exit-code triage and the 'probe exit code:' evidence line"
  - "version.json publicReleaseRefSpec accepts ^refs/tags/v.*$ (tagged builds carry no -g sha suffix)"
affects: [09-07, 09-08]

actuals:
  tokens: 1723
  tasks: 3
  commits: 3

plan_head_before: 82b9e113791023297a84388a758e3e4081e3c62c
plan_head_after: 53ef391250605b2468771df7986ee7c5de85e521

tech-stack:
  added: ["GitHub Actions: actions/checkout v7.0.1, actions/setup-dotnet v6.0.0, actions/upload-artifact v7.0.2, NuGet/login v1 (OIDC Trusted Publishing)"]
  patterns:
    - "IN-10 discipline: the no-logo flag never appears on a test invocation"
    - "verify-before-push: verify-pack.ps1 runs between pack and the irreversible push inside the same job"
    - "exit-code triage: 0 accepted / 10 drift / 20 infra, annotated distinctly; never read 20 as drift"

key-files:
  created:
    - .github/workflows/ci.yml
    - .github/workflows/release.yml
    - .github/workflows/live-interop.yml
  modified:
    - version.json

key-decisions:
  - "A4 executed: ^refs/tags/v.*$ joins master/main in publicReleaseRefSpec so a v* tag build stays a public release (no -g sha suffix) - same change set as release.yml by design"
  - "setup-dotnet cache key pinned to Directory.Packages.props (repo has no packages.lock.json; the default key cannot resolve)"
  - "ci.yml carries no result instrumentation (A5: OPS-02 does not require it; the MTP-era syntax differs from the legacy form)"
  - "release.yml has exactly one push step with --skip-duplicate; the sibling snupkg files ride the nupkg push (a second push would double-fire)"
  - "live-interop triage switch proven offline against mock exit codes 0/10/20/2/7 - the workflow's first real runs land at 09-07"
  - "Shared-ID gate: OPS-01/OPS-02 are declared by sibling plans without SUMMARYs, so 0/2 were ready; REQUIREMENTS.md marking defers to the last declaring plan"

patterns-established:
  - "Workflow file-head trust contracts: each workflow states the invariants automation cannot re-derive (IN-10, policy file-name binding, 405-only probe scope, self-hosted-never-publishes)"
  - "Structural acceptance checks as the strongest pre-GitHub verification available offline (no YAML tooling in this environment)"

requirements-completed: [OPS-01, OPS-02]

coverage:
  - id: D1
    description: "version.json accepts v* tags as public-release refs so tag-triggered builds carry the plain 0.1.N version (no -g sha suffix)"
    requirement: OPS-01
    verification:
      - kind: other
        ref: "pwsh: (Get-Content version.json -Raw | ConvertFrom-Json).publicReleaseRefSpec -contains '^refs/tags/v.*$'"
        status: pass
    human_judgment: true
    rationale: "Tag-build version behavior (absence of the -g suffix) is only observable on an actual tag push; the first real validation is the release exercise in 09-07/09-08, not this offline check."
  - id: D2
    description: "ci.yml runs on push/PR with the empirically proven commands: full build, full suite, and the Isolation filter - no test invocation carries the no-logo flag"
    requirement: OPS-02
    verification:
      - kind: other
        ref: "pwsh structural check over .github/workflows/ci.yml (checkout v7.0.1, fetch-depth 0, setup-dotnet global-json-file, cache-dependency-path, upload-artifact v7.0.2, Isolation suite, nologo-on-test guard, no instrumentation syntax)"
        status: pass
    human_judgment: true
    rationale: "A GitHub Actions workflow executes only on GitHub; structural checks prove the required content, while the first real run of this gate is the push carrying this commit (calibration tracked at 09-07)."
  - id: D3
    description: "release.yml publishes only from v* tags/manual dispatch: pack -> verify-pack -> OIDC login -> exactly one --skip-duplicate push, behind the release environment"
    requirement: OPS-01
    verification:
      - kind: other
        ref: "pwsh structural + ordering check over .github/workflows/release.yml (environment/id-token/contents, NuGet/login, verify-pack index < login index < single push step)"
        status: pass
    human_judgment: true
    rationale: "The OIDC exchange, environment approval gate, and policy file-name binding are external facts; their first real validation is the first tag/dispatch run (09-08 release exercise)."
  - id: D4
    description: "live-interop.yml probes weekly + on dispatch, prints 'probe exit code: N', and annotates 10 (drift) vs 20 (infra) distinctly"
    requirement: OPS-02
    verification:
      - kind: other
        ref: "offline extraction of the shipped triage switch executed against mock exit codes 0/10/20/2/7 (0 -> exit 0; 10/20/2/7 -> exit 1 with distinct annotations)"
        status: pass
      - kind: other
        ref: "pwsh structural check over .github/workflows/live-interop.yml (cron, workflow_dispatch, probe project path, --timeout-seconds, 'probe exit code:', PROTOCOL DRIFT, INTEROP INFRA, credential-free)"
        status: pass
    human_judgment: true
    rationale: "The triage switch is proven offline, but the workflow itself (runner egress to the live server) first runs on GitHub; 09-07 log-greps the evidence line from a dispatched run."

duration: 7 min
completed: 2026-10-17
status: complete
---

# Phase 09 Plan 04: Release workflows and the versioning trigger Summary

**IN-10-safe CI gate, Trusted Publishing release path with verify-before-push, and a weekly exit-code-triaged drift probe - plus the v* tag refspec that makes tagged builds public releases**

## Performance

- **Duration:** 7 min
- **Started:** 2026-10-17T01:02:06Z
- **Completed:** 2026-10-17T01:09:13Z
- **Tasks:** 3
- **Files modified:** 4

## Accomplishments

- `version.json` treats `^refs/tags/v.*$` as a public-release ref, so a tag-triggered publish carries the plain `0.1.N` version instead of a `-g<sha>` suffix (Pitfall 4/A4)
- `ci.yml` is the blocking push(master)/PR gate running the exact empirically proven commands: full build (`--nologo`), the bare full suite, and the `FullyQualifiedName~Isolation` filter - no test invocation carries the no-logo flag (IN-10) and no result instrumentation appears (A5)
- `release.yml` publishes only from trusted refs (`v*` tags + manual dispatch) behind `environment: release` with `contents: read` + `id-token: write` on the publish job only; `verify-pack.ps1` gates between pack and exactly one `--skip-duplicate` push, with `NuGet/login` immediately preceding it
- `live-interop.yml` runs the probe weekly (Mon 06:00 UTC) and on dispatch as a credential-free job, emits the machine-greppable `probe exit code: N` line, and annotates PROTOCOL DRIFT (405) vs INTEROP INFRA distinctly; a self-hosted fallback is documented as never-publishes
- All three workflows structurally verified (every required element present, forbidden patterns absent); the live-interop triage switch additionally executed offline against mock exit codes 0/10/20/2/7

## Task Commits

Each task was committed atomically:

1. **Task 1: version.json tag refspec + ci.yml** - `3674940` (feat)
2. **Task 2: release.yml Trusted Publishing workflow** - `181ed28` (feat)
3. **Task 3: live-interop.yml drift probe workflow** - `53ef391` (feat)

## Files Created/Modified

- `.github/workflows/ci.yml` - push/PR blocking gate: build, full suite, isolation filter, failure-only artifact upload
- `.github/workflows/release.yml` - tag/dispatch publish: environment gate, OIDC login, verify-before-push, single push step
- `.github/workflows/live-interop.yml` - schedule/dispatch drift probe with exit-code triage and the evidence line
- `version.json` - `^refs/tags/v.*$` added to `publicReleaseRefSpec`

## Decisions Made

- A4 executed: the tag refspec lands in the same change set as `release.yml` (a tag build without it would gain a `-g<sha>` suffix and burn the version)
- Cache key pinned to `Directory.Packages.props` because the repo has no `packages.lock.json` - the setup-dotnet default key cannot resolve (Pitfall 8)
- Result instrumentation deliberately absent from `ci.yml` (A5: OPS-02 does not require it)
- One push step only in `release.yml`, with `--skip-duplicate` for idempotent re-runs (Pitfall 11: snupkg siblings ride along automatically)
- Workflow file-head comments carry the trust contracts: IN-10 is build-only, `release.yml`'s file name is bound into the nuget.org policy, the probe is a 405-only smoke detector, and the self-hosted fallback runner must never publish
- Shared-ID gate: both requirements stayed `blocked` (0/2 ready) because sibling plans also declare OPS-01/OPS-02 without SUMMARYs - REQUIREMENTS.md marking is deferred to the last declaring plan's close-out

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None. The plan's structural verification was the strongest offline check available (no YAML tooling in this environment, per the plan's own assumption); the live-interop triage switch was additionally proven by executing the shipped `switch` block against mock exit codes, and the full suite + isolation filter were re-run green (733 passed / 0 failed; isolation 13 passed / 0 failed).

## User Setup Required

None - no external service configuration required by this plan. (First publish still needs the user's nuget.org Trusted Publishing policy + `NUGET_USER` secret; those actions are captured in later Phase 9 plans, not here.)

## Next Phase Readiness

- 09-07 can dispatch `live-interop.yml` and log-grep the `probe exit code:` evidence line; the other two workflows first exercise on the next push and the first `v*` tag
- The repo is private today, so `environment: release` is inert until it goes public (A1), captured for the 09-08 visibility election
- No blockers introduced; OPS-01/OPS-02 completion marking defers to the shared-ID gate

---
*Phase: 09-ship*
*Completed: 2026-10-17*

## Self-Check: PASSED

- Files verified on disk: version.json, .github/workflows/ci.yml, .github/workflows/release.yml, .github/workflows/live-interop.yml, 09-04-SUMMARY.md
- Commits verified as ancestors of HEAD: 3674940, 181ed28, 53ef391
- Evaluation scope resolved with 3 commits (`check evaluation-scope --plan 09-04 --commits-only`)

