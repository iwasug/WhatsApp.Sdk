---
phase: 09-ship
plan: 02
subsystem: infra
tags: [api-freeze, publicapi-analyzers, provenance, notice, release-engineering, ops-01]
requires:
  - phase: 09-ship
    provides: "09-01's packaging truth (two packages via verify-pack.ps1) and suite-green gate - the freeze must land after the last API-affecting change (the PairingLifecycleTests repair was the only wave-1 work able to touch product sources)"
provides:
  - "frozen src/Iwasug.WhatsApp.Sdk/PublicAPI.Shipped.txt: all 1352 SDK API entries moved from Unshipped (ordinal order preserved, byte-for-byte, LF)"
  - "frozen src/Iwasug.WhatsApp.Sdk.Testing/PublicAPI.Shipped.txt: all 181 testing-kit entries moved from Unshipped"
  - "header-only PublicAPI.Unshipped.txt on both packable projects (SDK keeps its leading blank line + #nullable enable convention; Testing keeps #nullable enable)"
  - "analyzer-enforced post-freeze gate: any undeclared surface change fails the build with RS0016/RS0017 under TreatWarningsAsErrors (proven by a seeded negative test)"
  - "NOTICE.md provenance log complete through Phase 9: Phase 7 + Phase 9 audit rows in chronological position; PR checklist walked with evidence"
affects: [09-04, 09-07, 09-08-publish, 09-09-uat-honesty]
actuals:
  tokens: 91906
  tasks: 2
  commits: 2
  plan_head_before: 8398560deef61fbd75b2805af06a2317afe301b7
  plan_head_after: 970de234a75d52efd657ddbdfc85ec23e15811b0
tech-stack:
  added: []
  patterns:
    - "API freeze = mechanical Unshipped->Shipped move proven by SHA-256 payload identity (newline-joined entry blocks must hash equal pre/post) instead of eyeballing 1533 moved lines"
    - "The analyzer gate is proven by a seeded negative test (delete one entry, build must fail RS0016, restore byte-identically) - same fail-closed proof style as 09-01's verify-pack.ps1 seeded negatives"
    - "Provenance audit rows insert at chronological + phase position in NOTICE.md's log (Phase 7 row between Phase 6 and Phase 8), dated to the phase's completion"
key-files:
  created: []
  modified:
    - src/Iwasug.WhatsApp.Sdk/PublicAPI.Shipped.txt
    - src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt
    - src/Iwasug.WhatsApp.Sdk.Testing/PublicAPI.Shipped.txt
    - src/Iwasug.WhatsApp.Sdk.Testing/PublicAPI.Unshipped.txt
    - NOTICE.md
key-decisions:
  - "Freeze written with LF endings: the git index was already i/lf for all four files and the plan says keep LF; the Unshipped working-tree CRLF was core.autocrlf=true checkout conversion, so the plan's 'files are LF' assumption holds against the canonical stored form"
  - "RS0016 gate proven live, not assumed: removing the AccountId.Equals entry from Shipped failed the SDK build ('not part of the declared public API'), then the file was restored and SHA-256-verified byte-identical"
  - "Phase 7 audit row dated 2026-10-15 (Phase 7's completion date in ROADMAP) and placed between the Phase 6 and Phase 8 rows; Phase 9 row dated 2026-10-17 after Phase 8"
  - "PR checklist boxes left unchecked - the plan says the checklist stays as-is unless a claim fails; the walk confirmation lives in this SUMMARY instead"
requirements-completed: [OPS-01]
coverage:
  - id: D1
    description: "SDK public surface frozen: all 1352 entries moved to PublicAPI.Shipped.txt, Unshipped header-only (blank line + #nullable enable)"
    requirement: OPS-01
    verification:
      - kind: other
        ref: "dotnet build Iwasug.WhatsApp.Sdk.sln --nologo -> Build succeeded, 0 Warning(s), 0 Error(s) (RS0016 would fire if any of the 1352 symbols were in neither file); payload SHA-256 identical pre/post move (85c8a2ae...cab73)"
        status: pass
    human_judgment: false
  - id: D2
    description: "Testing-kit public surface frozen: all 181 entries moved to PublicAPI.Shipped.txt, Unshipped header-only (#nullable enable)"
    requirement: OPS-01
    verification:
      - kind: other
        ref: "dotnet build Iwasug.WhatsApp.Sdk.sln --nologo -> 0 warnings / 0 errors; payload SHA-256 identical pre/post move (75164368...49140)"
        status: pass
    human_judgment: false
  - id: D3
    description: "Analyzer gate armed: any post-freeze undeclared surface change fails the build until an explicit Unshipped-file edit"
    requirement: OPS-01
    verification:
      - kind: other
        ref: "negative test: removed the '~override ...AccountId.Equals(object obj) -> bool' entry from Shipped -> dotnet build src/Iwasug.WhatsApp.Sdk/Iwasug.WhatsApp.Sdk.csproj failed with 'error RS0016: Symbol ... is not part of the declared public API'; entry restored, payload SHA-256 back to 85c8a2ae...cab73, rebuild clean"
        status: pass
    human_judgment: false
  - id: D4
    description: "NOTICE.md log carries Phase 7 and Phase 9 audit rows in the established row shape and chronological position"
    requirement: OPS-01
    verification:
      - kind: other
        ref: "Select-String -Path NOTICE.md -Pattern 'Phase 7 audit' / 'Phase 9 audit' - both present (lines 66 and 68); row shape matches the Phase 5 model row"
        status: pass
    human_judgment: false
  - id: D5
    description: "PR checklist provenance claims hold for the release (SC3 audit conclusion)"
    verification:
      - kind: other
        ref: "checklist walk: no copyleft license grant in any source file (all MPL-2.0 mentions are behavioral-reference provenance notes); all 6 .proto files carry provenance headers; 24 log rows cite upstream file:lines"
        status: pass
    human_judgment: true
    rationale: "Release-level provenance audit conclusion (SC3) - automated checks confirm absence of copyleft markers and presence of citations, but the 'no MPL-2.0 contamination' sign-off is a verifier/human judgment, not a test assertion"

# Metrics
duration: 7min
completed: 2026-10-17
status: complete
---

# Phase 9 Plan 02: Public API Freeze & Provenance Audit Summary

**Both packable packages now carry a frozen, analyzer-enforced public contract — 1352 SDK + 181 testing-kit entries moved byte-for-byte to PublicAPI.Shipped.txt, Unshipped files header-only, the RS0016 gate proven by a seeded negative test — and NOTICE.md's provenance log is complete through Phase 9.**

## Performance

- **Duration:** 7 min (start 00:49:28Z, end 00:56:12Z)
- **Started:** 2026-10-17T00:49:28Z
- **Completed:** 2026-10-17T00:56:12Z
- **Tasks:** 2
- **Files modified:** 5

## Accomplishments

- SDK surface frozen: all 1352 entries moved from `PublicAPI.Unshipped.txt` to `PublicAPI.Shipped.txt`, ordinal order preserved, payload SHA-256 identical pre/post (`85c8a2ae…cab73`); Unshipped now header-only (blank line + `#nullable enable`).
- Testing-kit surface frozen: all 181 entries moved the same way (payload SHA-256 `75164368…49140`); Unshipped now `#nullable enable` only.
- Freeze gate proven, not assumed: with one entry removed the SDK build fails `error RS0016: … is not part of the declared public API` under `TreatWarningsAsErrors`; the entry was restored and hash-verified. Any post-freeze API drift now requires an explicit Unshipped-file edit visible in review (T-9-04 mitigation).
- Full solution builds clean (0 warnings / 0 errors) and the entire suite is green: 733/733 passed, 0 failed, 0 skipped (46s).
- NOTICE.md: Phase 7 audit row inserted in chronological position (between Phase 6 and Phase 8), Phase 9 audit row added after Phase 8; PR checklist walked against the release state (T-9-05 mitigation, details below).

## Task Commits

1. **Task 1: Freeze the public surface on both packable projects** - `27e9d4a` (chore)
2. **Task 2: NOTICE.md - Phase 7 + Phase 9 audit rows and checklist pass** - `970de23` (docs)

## Files Created/Modified

- `src/Iwasug.WhatsApp.Sdk/PublicAPI.Shipped.txt` (modified) - now carries the full 1352-entry SDK surface (`#nullable enable` header + entries, LF, no BOM).
- `src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt` (modified) - header-only: leading blank line + `#nullable enable`.
- `src/Iwasug.WhatsApp.Sdk.Testing/PublicAPI.Shipped.txt` (modified) - now carries the full 181-entry testing-kit surface.
- `src/Iwasug.WhatsApp.Sdk.Testing/PublicAPI.Unshipped.txt` (modified) - header-only: `#nullable enable`.
- `NOTICE.md` (modified) - Phase 7 + Phase 9 audit rows in the log; PR checklist untouched (walk recorded below).

## PR Checklist Walk (SC3)

The plan requires walking NOTICE.md's PR checklist (lines 70-76) against the release state; the checklist itself stays as-is. Confirmation:

1. **"No code, `.proto` files, or generated output copied from an MPL-2.0 (or other copyleft) source." — HOLDS.** No source file carries a copyleft license grant; every MPL-2.0 mention under `src/` is a provenance comment documenting behavioral-reference-only use (BinaryNode, BinaryNodeCodec, TokenDictionary, NoiseProtocol, ClientPayloadFactory, HandshakePayloadProvider, PairingQrPayload, and all six `.proto` headers). The log records every whatsmeow read as "behavioral read-only - no code, schema, or bytes copied".
2. **"Any protocol constant or structure introduced cites its source." — HOLDS.** 24 log rows cite upstream file:lines (`@ 0af2386`, `@ main`, or the Signal spec revision); every `.proto` header names its source file + commit.
3. **"Provenance log updated when upstream source was read during development." — HOLDS.** The log is complete through Phase 9: Phases 1-4 carry detailed read rows; Phases 5-9 carry audit rows (5, 7, 9 = "no new upstream reads"; 6, 8 = the media/extended-message reads), with Phase 7's previously-missing row added here.

## Decisions Made

- Freeze written with **LF** endings: the git index was already `i/lf` for all four PublicAPI files and the plan mandates LF; the Unshipped working-tree CRLF was `core.autocrlf=true` checkout conversion, so the plan's "files are LF" assumption holds against the canonical stored form. No entry content was reformatted; no BOM introduced.
- The RS0016 gate was proven with a **seeded negative test** (delete one entry → build fails → restore byte-identically) rather than inferred from a clean build - the same fail-closed proof style as 09-01's verify-pack negatives.
- Phase 7 audit row **dated 2026-10-15** (Phase 7's ROADMAP completion date) and placed between the Phase 6 and Phase 8 rows; Phase 9 row dated 2026-10-17 after Phase 8. Row shape follows the Phase 5 model row (`_(Phase N audit: no new upstream reads)_ — …`).
- PR checklist boxes **left unchecked** - the plan says the checklist stays as-is unless a claim fails; the walk confirmation lives in this SUMMARY.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

- Working-tree line-ending observation: both Unshipped.txt files were CRLF in the working tree (`core.autocrlf=true` checkout conversion) while the plan's assumption text says "files are LF". The canonical stored form (git index) is LF and the plan mandates LF, so the freeze wrote LF; git confirms `i/lf` for all four files after the freeze. Entry content was copied byte-for-byte (SHA-256 payload identity proves it).
- Known deferred items (WINDOWS.md #17 `MessagingLoopbackTests` line 873, #19 `MessageCorrelationTests` line 354) remain open and untouched; this plan modified only txt/md files. Full suite ran green this pass (733/733).

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- 09-04 (version.json tag refspec + CI workflows) can proceed: the surface it packages is frozen; any workflow-driven API change now fails the build until an explicit Unshipped edit.
- Note: the Phase 9 NOTICE row references `.github/workflows/` per the plan's row text - 09-04 creates that directory; the row describes the phase's full scope.
- 09-07/09-08 publish from this frozen surface; 09-09's UAT reads the now-complete provenance log.

---
*Phase: 09-ship*
*Completed: 2026-10-17*

## Self-Check: PASSED

- All four PublicAPI.{Shipped,Unshipped}.txt files exist (frozen/header-only states verified above).
- NOTICE.md exists with both audit rows (Select-String verify passed).
- .planning/phases/09-ship/09-02-SUMMARY.md exists.
- Commits 27e9d4a and 970de23 present in HEAD history.
- Plan-range commit count measured from the ledger base: 2 (plan_head_before 8398560 → plan_head_after 970de23).
- Task files committed cleanly (no uncommitted task changes; only pre-existing orchestrator/planner artifacts remain untracked).
