---
phase: 09-ship
plan: 05
subsystem: docs
tags: [readme, risk-ledger, nuget, packaging, honest-scope, ops-01, verify-pack]
requires:
  - phase: 05-ban-safety-layer
    provides: "the original README ban-safety ledger section (Phase 5 — honest scope) relocated here verbatim"
  - phase: 09-ship
    provides: "09-01 packaging truth: scripts/verify-pack.ps1 content gate + README packed into both nupkgs (PackageReadmeFile wiring)"
provides:
  - "docs/risk-ledger.md: full ban-safety ledger (verbatim Phase-5 relocation: what the layer does / does NOT do / media additions / unsafe opt-in + code sample / byte-level note) plus RegistrationDefaults first-registration caveat, CONN-01..03 NOT VERIFIED statuses, and the 'Cannot be mitigated by any SDK setting:' list"
  - "package-ready README.md: truthful phase status (Phase 6 in progress at 4/6 plans, MED-01 pending; pairing real-phone UAT caveat inline), install quickstart, all-absolute links, Compliance pointer to the ledger"
  - "repack gate re-proven: clean-dir dotnet pack + verify-pack.ps1 green (0.1.355; 2 packages + 2 symbol packages)"
affects: [09-06-store-docs, 09-08-publish, 09-09-uat-honesty]
actuals:
  tokens: 3881
  tasks: 2
  commits: 2
  plan_head_before: c997946aee0b9b3817cf2146ea8e5ab5aa500832
  plan_head_after: 42d36e6dca9daff7468fdb0d11ac9d1a46684242
tech-stack:
  added: []
  patterns:
    - "Packed README uses absolute blob/tree URLs only — relative links break on nuget.org (Pitfall 9)"
    - "Every README edit re-runs the clean-dir pack + verify-pack.ps1 gate — it is a shipped package content change"
    - "Honesty expansions quote runtime logs / validation records verbatim in labeled sections — machine-checkable, never softened"
key-files:
  created:
    - docs/risk-ledger.md
  modified:
    - README.md
key-decisions:
  - "Relocation is byte-verbatim: a mechanical diff of the moved block vs the pre-move README shows exactly one hunk — the dangling '(see Compliance below)' cross-reference repointed at the README Compliance section (Rule 1 fix); every disclaimer/code sample is byte-identical"
  - "README status claims bounded by the REQUIREMENTS/ROADMAP truth: media declared Phase 6 in-progress (4/6 plans) with MED-01 pending; pairing carries the CONN real-phone NOT VERIFIED caveat inline"
  - "All README markdown links absolute (blob/tree URLs) and the repack + verify-pack.ps1 re-run after the edit (0.1.355) because the README is packed into both nupkgs"
requirements-completed: [OPS-01]
coverage:
  - id: D1
    description: "docs/risk-ledger.md carries the relocated Phase-5 ledger verbatim (what the layer does / does NOT do / media additions / unsafe opt-in + code sample / byte-level note) plus the three mandated honesty expansions (RegistrationDefaults caveat quoting the runtime log, CONN-01..03 NOT VERIFIED statuses, cannot-be-mitigated list)"
    requirement: OPS-01
    verification:
      - kind: other
        ref: "Task 1 <automated> verify: all 7 required labels present; 93 lines; README absolute ledger link present; no relative docs link — pass"
      - kind: other
        ref: "git diff --no-index pre-move README block (925e0ed^) vs relocated ledger block: single hunk = dangling cross-reference fix; all disclaimers byte-identical — pass"
      - kind: other
        ref: "CONN-01..03 status texts match 03-VALIDATION.md verbatim; RegistrationDefaults quote equals the composed DefaultAccountRuntimeFactory.cs:206-210 log message — pass"
    human_judgment: false
  - id: D2
    description: "README.md is package-ready for its two audiences: truthful shipped-capabilities status, install quickstart (SDK + Testing kit), preserved unofficial notice / in-memory-store honesty / idempotency contract / provenance sections"
    requirement: OPS-01
    verification:
      - kind: other
        ref: "Task 2 <automated>: relative-link regex zero matches; 'dotnet add package Iwasug.WhatsApp.Sdk' present; 'Unofficial project' present — pass"
    human_judgment: true
    rationale: "Whether the status prose over-claims (claims must be bounded by REQUIREMENTS/ROADMAP truth) is a reading judgment no command asserts; machine checks cover the link/quickstart/notice properties and pack contents only."
  - id: D3
    description: "A fresh clean-dir pack after the README edit still passes scripts/verify-pack.ps1 — the README remains inside both nupkgs"
    requirement: OPS-01
    verification:
      - kind: integration
        ref: "Remove-Item artifacts; dotnet pack Iwasug.WhatsApp.Sdk.sln --nologo -o artifacts; pwsh scripts/verify-pack.ps1 -ArtifactsPath artifacts -> 'verify-pack: OK - 0.1.355; 2 packages + 2 symbol packages verified' — pass"
        status: pass
    human_judgment: false
  - id: D4
    description: "Solution build and full test suite stay green after the docs edits (plan-level verification)"
    requirement: OPS-01
    verification:
      - kind: integration
        ref: "dotnet build Iwasug.WhatsApp.Sdk.sln --nologo -> Build succeeded, 0 Warning(s), 0 Error(s); dotnet test Iwasug.WhatsApp.Sdk.sln -> total 733, failed 0, succeeded 733, skipped 0 (49.6s) — pass"
        status: pass
    human_judgment: false
metrics:
  duration: 8m
  completed: 2026-10-17
status: complete
---

# Phase 9 Plan 05: Risk-Ledger Relocation & Package-Ready README Summary

**Ban-safety ledger relocated byte-verbatim into docs/risk-ledger.md with RegistrationDefaults / CONN-01..03 / cannot-mitigate honesty expansions, and README.md rewritten as a package-ready front page (install quickstart, all-absolute links) behind a green verify-pack repack gate**

## Performance

- **Duration:** ~8 min
- **Started:** 2026-10-17T07:11:50Z
- **Completed:** 2026-10-17T07:19:51Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments

- **docs/risk-ledger.md created** — the complete honest ledger: the Phase-5 README block relocated (what the layer does / what the layer does NOT do / media additions / unsafe opt-in acknowledgment + `SafetyOptions` code sample / byte-level protocol note), followed by the three mandated honesty expansions: (1) the "First-registration material (read before the first pairing)" caveat quoting the `DefaultAccountRuntimeFactory` warning verbatim and pointing hosts at docs/store-implementation.md (09-06); (2) "Live-verification status" carrying the CONN-01..03 NOT VERIFIED statuses from 03-VALIDATION.md verbatim, stating real-phone UAT is pending and no simulated result is a substitute; (3) "Cannot be mitigated by any SDK setting:" listing account-age reputation, contact quality, complaint rates, device reputation, plus the no-guarantee / no-rehabilitation restatement.
- **README.md rewritten for its two audiences** (nuget.org visitor + repo reader): stale "Phases 1–5 complete … arrive in later phases" status replaced by a truthful shipped-capabilities summary (Connection, Pairing & session durability with the NOT VERIFIED caveat inline, Signal E2EE messaging, ban-safety gate, Media Phase 6 in progress 4/6 with MED-01 pending, multi-account, broadcast + extras); an **Install** quickstart added directly after the intro notice (`dotnet add package Iwasug.WhatsApp.Sdk` + the Testing contract kit) with a sentence pointing at the sample and docs/ guides; **every markdown link absolute** (LICENSE, NOTICE.md, docs, sample); the Compliance section's now-dangling "ledger above" pointer replaced with the ledger's absolute URL; unofficial notice, in-memory-store honesty, messaging surface, idempotency contract, and License & provenance preserved.
- **Relocation proven byte-verbatim** — a mechanical `git diff --no-index` of the moved block against the pre-move README block shows exactly one hunk: the cross-reference fix (Rule 1). No disclaimer was softened, dropped, or paraphrased.
- **Repack gate re-proven after the README edit** — clean-dir `dotnet pack` + `scripts/verify-pack.ps1`: `verify-pack: OK - 0.1.355; 2 packages + 2 symbol packages verified`; the README remains inside both nupkgs.
- Plan-level verification: build 0 warnings / 0 errors; full suite **733/733 passed, 0 failed, 0 skipped**.

## Task Commits

1. **Task 1: docs/risk-ledger.md — verbatim relocation + honesty expansions** - `925e0ed` (docs)
2. **Task 2: README rewrite — truthful status, quickstart, absolute links, repack** - `42d36e6` (docs)

## Files Created/Modified

- `docs/risk-ledger.md` (created) - the full ban-safety ledger: verbatim Phase-5 block + RegistrationDefaults caveat + CONN-01..03 live-verification status + cannot-be-mitigated list.
- `README.md` (modified) - package-ready front page: install quickstart, truthful status, 5-line ledger summary linking the absolute URL, all links absolute, Compliance pointer fixed.

## Decisions Made

- Relocation is byte-verbatim by mechanical proof; the only touch to moved text is the dangling "(see Compliance below)" cross-reference now pointing at the README's Compliance section — the move itself broke that reference, so fixing it is corrective (Rule 1), not editorial.
- README status claims are bounded by REQUIREMENTS/ROADMAP truth: media is declared "Phase 6 in progress — 4/6 plans" with "live UAT traceability pending (MED-01)", and pairing carries the CONN real-phone NOT VERIFIED caveat inline rather than a bare "shipped" claim.
- Every README markdown link is absolute (blob/tree URLs) because the README is packed into both nupkgs; the repack + verify-pack.ps1 gate was re-run after the edit as a mandatory package-content check.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Dangling cross-reference in the relocated disclaimers**
- **Found during:** Task 1 (relocation)
- **Issue:** The moved "What the layer does NOT do" block ends "(see Compliance below)" — a reference to README content that does not follow the block inside `docs/risk-ledger.md`. Relocating verbatim left a broken pointer in a disclaimer.
- **Fix:** Repointed to "(see the Compliance section in the [README](https://github.com/iwasug/WhatsApp.Sdk/blob/master/README.md))". No disclaimer wording changed — proven by the mechanical block diff (single hunk).
- **Files modified:** docs/risk-ledger.md
- **Verification:** `git diff --no-index` of the relocated block vs the pre-move README block shows exactly this one hunk; all other lines byte-identical.
- **Committed in:** `925e0ed` (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (1 bug).
**Impact on plan:** None on scope — the fix makes the relocated document self-consistent without touching any disclaimer's meaning; the Prohibition "no honesty regression" is machine-verified.

## Issues Encountered

None.

## User Setup Required

None - documentation-only plan; no external service configuration required.

## Known Stubs

None.

## Next Phase Readiness

- `docs/risk-ledger.md` forward-links `docs/store-implementation.md` (absolute URL) — **09-06 must produce that document** for the link to resolve; README's Status/Install sections likewise point at the docs/ guides.
- 09-08 (publish): the packed README is the nuget.org front page and the ledger is the referenced honest scope; the repack gate was re-proven after this edit (0.1.355).
- 09-09 (UAT authoring): honesty items to cover — CONN-01..03 real-phone UAT (NOT VERIFIED), the `RegistrationDefaults` supply path before first registration, and media live UAT traceability (MED-01).

---
*Phase: 09-ship*
*Completed: 2026-10-17*

## Self-Check: PASSED

- docs/risk-ledger.md exists.
- README.md exists.
- .planning/phases/09-ship/09-05-PLAN.md exists.
- Commits 925e0ed, 42d36e6 present in HEAD history.
- Task files committed cleanly (no uncommitted task changes).
