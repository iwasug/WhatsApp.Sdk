---
phase: "9"
slug: "ship"
status: draft
nyquist_compliant: false
wave_0_complete: false
created: "2026-10-17"
---

# Phase 9 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 4.0.1 on Microsoft.Testing.Platform (runner pinned in `global.json`) |
| **Config file** | repo-root `global.json` (SDK 10.0.401 + `"test": { "runner": "Microsoft.Testing.Platform" }`) |
| **Quick run command** | `dotnet test Iwasug.WhatsApp.Sdk.sln --filter FullyQualifiedName~Isolation` |
| **Full suite command** | `dotnet test Iwasug.WhatsApp.Sdk.sln` (observed ~1m24s; NEVER add `--nologo` — IN-10 quirk exits 5 "Zero tests ran") |
| **Estimated runtime** | ~90 seconds |

---

## Sampling Rate

- **After every task commit:** `dotnet build Iwasug.WhatsApp.Sdk.sln --nologo`; for test-touching or shared-code tasks run the full suite.
- **After every plan wave:** Full suite + isolation filter (`--filter FullyQualifiedName~Isolation`).
- **Before `/gsd-verify-work`:** Full suite green (709-test baseline; the 4 `PairingLifecycleTests` failures resolved or explicitly quarantined with tracking) + isolation green + pack-verify green + a dispatched live-probe run producing a classified exit.
- **Max feedback latency:** ~120 seconds.

---

## Per-Task Verification Map

Authoritative map — planner-filled from the executed plan set (`09-NN-TT` = plan `09-NN`, task `TT`). **File Exists** records the planning-time state of each task's deliverable.

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 09-01-T1 | 09-01 | 1 | OPS-01, OPS-02 | T-9-01, T-9-02 | pack output is exactly the two intended packages; sample un-packable | pack + script | `dotnet pack Iwasug.WhatsApp.Sdk.sln --nologo -o artifacts` → `pwsh -NoProfile -File scripts/verify-pack.ps1 -ArtifactsPath artifacts` | ❌ `scripts/verify-pack.ps1` (created here) | ⬜ pending |
| 09-01-T2 | 09-01 | 1 | OPS-01, OPS-02 | T-9-03 | CI can gate on a green suite; no silent skips (fix-first, quarantine only with visible reason) | unit+integration | `dotnet test Iwasug.WhatsApp.Sdk.sln --filter "FullyQualifiedName~PairingLifecycleTests" -x` | ✅ tests exist — 4 failing at baseline | ⬜ pending |
| 09-01-T3 | 09-01 | 1 | OPS-01, OPS-02 | T-9-02 | nupkg contents complete (icon/LICENSE/README/XML/snupkg) and asserted pretag | pack + script | fresh pack → `verify-pack.ps1` (stronger content assertions) | ❌ `assets/icon.png` etc. | ⬜ pending |
| 09-02-T1 | 09-02 | 2 | OPS-01 | T-9-04 | frozen public surface; analyzer gate (RS0016/RS0017/RS0041) blocks drift | build (analyzer) | `dotnet build Iwasug.WhatsApp.Sdk.sln --nologo`; Unshipped files hold no live entries | ✅ files exist (1354/182 lines) | ⬜ pending |
| 09-02-T2 | 09-02 | 2 | OPS-01 | T-9-05 | provenance audit rows for Phase 7 + Phase 9 recorded | doc audit (scripted) | `Select-String NOTICE.md -Pattern 'Phase 7 audit'` / `'Phase 9 audit'` | ✅ NOTICE.md exists | ⬜ pending |
| 09-03-T1 | 09-03 | 1 | OPS-02 | T-9-06 | probe runs on a fresh in-memory store, zero material; classified outcome end-to-end | unit (scripted peer, tracer) | `dotnet test Iwasug.WhatsApp.Sdk.sln --filter "FullyQualifiedName~InteropProbe" -x` | ❌ probe project (created here) | ⬜ pending |
| 09-03-T2 | 09-03 | 1 | OPS-02 | T-9-07 | 405 → exit 10 drift; silence/timeout → exit 20 infra | unit (TDD, scripted peer) | `dotnet test Iwasug.WhatsApp.Sdk.sln --filter "FullyQualifiedName~InteropProbe" -x` | ❌ | ⬜ pending |
| 09-03-T3 | 09-03 | 1 | OPS-02 | T-9-07 | `--timeout-seconds` bounds every wait; `PROBE_RESULT exit=<n>` evidence line | unit | `dotnet test Iwasug.WhatsApp.Sdk.sln --filter "FullyQualifiedName~InteropProbe" -x` | ❌ | ⬜ pending |
| 09-04-T1 | 09-04 | 2 | OPS-01, OPS-02 | T-9-08, T-9-10 | v* tags are public-release refs; ci.yml pins + isolation gate, no `--nologo` on tests | scripted content check | version.json refspec + ci.yml pattern set | ❌ `ci.yml` (version.json ✅) | ⬜ pending |
| 09-04-T2 | 09-04 | 2 | OPS-01, OPS-02 | T-9-09, T-9-10 | publish only on trusted refs; OIDC + environment; exactly one push step | scripted content check | release.yml pattern set incl. verify-pack → login → push order | ❌ | ⬜ pending |
| 09-04-T3 | 09-04 | 2 | OPS-01, OPS-02 | T-9-10 | probe job carries no secrets/OIDC; DRIFT/INFRA annotations loud | scripted content check | live-interop.yml pattern set | ❌ | ⬜ pending |
| 09-05-T1 | 09-05 | 1 | OPS-01 | T-9-11 | cannot-mitigate list + RegistrationDefaults caveat + CONN statuses relocated verbatim | doc checks | risk-ledger token set + README absolute ledger link | ❌ `docs/risk-ledger.md` | ⬜ pending |
| 09-05-T2 | 09-05 | 1 | OPS-01 | T-9-11 | README truthful status + install quickstart; all-absolute links; repack gate | doc checks + pack | README link/quickstart checks → repack + verify-pack | ✅ README.md exists (stale) | ⬜ pending |
| 09-06-T1 | 09-06 | 1 | OPS-01 | T-9-12 | all 8 store interfaces + 8 kit contracts documented accurately | doc checks | store-guide token set (interfaces, contracts, registry, factory) | ❌ `docs/store-implementation.md` | ⬜ pending |
| 09-06-T2 | 09-06 | 1 | OPS-01 | T-9-12 | stale filter claim + "five interfaces" corrected to empirical behavior | doc checks | tests README: stale claims absent, `eight store interfaces` present | ✅ exists (stale) | ⬜ pending |
| 09-06-T3 | 09-06 | 1 | OPS-01 | T-9-13 | webhook recipe: redaction + no-HTTP-server boundary; no key material in events | doc checks | webhook-recipe token set + redaction guidance | ❌ `docs/webhook-recipe.md` | ⬜ pending |
| 09-07-T1 | 09-07 | 3 | OPS-02 | T-9-14 | fast-forward-only push; first remote ci run green for the pushed head | integration (CI) | guarded `git fetch/rev-list/rev-parse` → `gh run list` poll → conclusion success | ❌ remote run | ⬜ pending |
| 09-07-T2 | 09-07 | 3 | OPS-02 | T-9-14 | calibration reads the workflow evidence line, never run-watch exit status | integration (live) | evidence file: calibration section + ≥2 run URLs + classified probe exit | ❌ | ⬜ pending |
| 09-07-T3 | 09-07 | 3 | OPS-02 | T-9-14 | every evidence claim cites an actions/runs/ URL; committed before the publish decision | doc checks | evidence sections + ≥3 run URLs + `git status` clean | ❌ `09-CI-EVIDENCE.md` | ⬜ pending |
| 09-08-T1 | 09-08 | 4 | OPS-01 | T-9-15 | policy binds owner/repo/workflow-file exactly; profile name only, never a key | checkpoint:human-action | — (user console; resume with profile name + policy + visibility election) | n/a (external console) | ⬜ pending |
| 09-08-T2 | 09-08 | 4 | OPS-01 | T-9-15 | one-way publish preceded by a recorded go/no-go decision | checkpoint:decision | — (resume with `publish-now` or `hold: <reason>`) | n/a | ⬜ pending |
| 09-08-T3 | 09-08 | 4 | OPS-01 | T-9-15 | version from the artifact filename; tag push publishes once; both IDs claimed | integration (CI + registry) | packed version → tag exists → release run success → flatcontainer both IDs → publish evidence | ❌ tag v0.1.N | ⬜ pending |
| 09-09-T1 | 09-09 | 5 | OPS-01 | T-9-16 | UAT results bound to evidence or marked NOT VERIFIED; CONN statuses carried | doc checks | 09-UAT.md row tokens (expected/result/source) + `CONN-01` | ❌ `09-UAT.md` | ⬜ pending |
| 09-09-T2 | 09-09 | 5 | OPS-01 | T-9-16 | Phase 6 lag + assumption states surfaced honestly (A2–A5) | doc checks | traceability token set incl. `MED-01`/`06-05`/`06-06` + 09-CI-EVIDENCE cross-ref | ❌ | ⬜ pending |

*Note: the former W0/A/B/C research-workstream markers are replaced by real task IDs. Wave 0 deliverables map to: `scripts/verify-pack.ps1` → 09-01-T1/T3; suite-green gate → 09-01-T2; probe + classifier tests → 09-03-T1..T3; the three workflows + version.json → 09-04-T1..T3; docs set → 09-05/09-06; user actions → 09-08-T1.*

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] Root-cause fix (preferred) or explicitly tracked quarantine of the 4 `PairingLifecycleTests` failures — CI cannot gate on a red suite.
- [ ] `tools/Iwasug.WhatsApp.Sdk.InteropProbe/` — console probe (public API only; PairingSample construction pattern) + status→exit classifier unit tests with a scripted peer.
- [ ] `scripts/verify-pack.ps1` — asserts 2 nupkgs + snupkgs, no sample package, README/LICENSE/icon inside.
- [ ] `.github/workflows/ci.yml`, `.github/workflows/release.yml`, `.github/workflows/live-interop.yml`.
- [ ] `assets/icon.png` (128×128), `docs/store-implementation.md`, `docs/webhook-recipe.md`, `docs/risk-ledger.md`, README rewrite.
- [ ] `version.json` `publicReleaseRefSpec` tag decision + edit (only if tag-triggered publish).
- [ ] User actions (checkpoints, not CI): nuget.org account + Trusted Publishing policy; `NUGET_USER` repo secret; repo-visibility decision.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Reach first send following the docs alone | OPS-01 | Requires a real phone + WhatsApp account; outside CI | Install the packed nupkg into a scratch host; follow README + docs verbatim through pairing and one send; record deviations in the UAT checklist |
| Trusted Publishing policy + first publish claims both IDs | OPS-01 | nuget.org account action | Create policy (Repository Owner `iwasug`, Repository `WhatsApp.Sdk`, Workflow File `release.yml`), run release, confirm both package IDs claimed |
| Repo goes public / environment gate activates | OPS-01 | GitHub plan + visibility settings are user-owned | After first publish, confirm required-reviewer gate on the `release` environment once the repo is public |
| Live probe calibration against hosted runners | OPS-02 | Hosted-runner IP acceptance unverifiable from CI | Dispatch `live-interop.yml` 2–3 times; if all runs exit 20, document and switch to self-hosted fallback |

*If none: "All phase behaviors have automated verification." — not the case here (release ceremony + UAT are human).*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 120s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
