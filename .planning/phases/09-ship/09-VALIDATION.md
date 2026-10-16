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

Filled in by the planner wave assignments; Wave 0 gaps tracked below. Initial rough map (from `09-RESEARCH.md` → Validation Architecture):

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| W0 | TBD | 0 | OPS-02 | — | full suite green as a CI gate (fix/quarantine 4 `PairingLifecycleTests`) | unit+integration | `dotnet test Iwasug.WhatsApp.Sdk.sln` | ❌ 4 failures | ⬜ pending |
| W0 | TBD | 0 | OPS-02 | T-09-probe | drift classifier maps status → exit codes (0 ok / 10 drift / 20 infra) | unit (scripted peer) | `dotnet test Iwasug.WhatsApp.Sdk.sln --filter FullyQualifiedName~InteropProbe` | ❌ Wave 0 | ⬜ pending |
| W0 | TBD | 0 | OPS-01 | artifact tampering | pack-verify asserts exactly 2 nupkgs + snupkgs, no sample package, README/LICENSE/icon present | script | `pwsh scripts/verify-pack.ps1 artifacts` | ❌ Wave 0 | ⬜ pending |
| A | TBD | 1 | OPS-01 | — | sample un-packable; packaging switches (icon/LICENSE/snupkg/CI-build) applied | build + pack | `dotnet pack Iwasug.WhatsApp.Sdk.sln -o artifacts` → verify-pack | partial | ⬜ pending |
| A | TBD | 1 | OPS-01 | — | public API frozen; analyzer gate enforced (RS0016/RS0017/RS0041) | build | `dotnet build Iwasug.WhatsApp.Sdk.sln --nologo` | ✅ analyzer wired | ⬜ pending |
| A | TBD | 1 | OPS-01 | — | provenance audit: NOTICE/LICENSE complete, no MPL-2.0 contamination | audit (grep + review) | NOTICE audit checklist / provenance rows | ✅ NOTICE exists | ⬜ pending |
| B | TBD | 1–2 | OPS-02 | supply chain | `ci.yml` runs build + full suite + isolation on push/PR | integration (CI) | `.github/workflows/ci.yml` on push | ❌ Wave 0 | ⬜ pending |
| B | TBD | 2 | OPS-01 | credential theft | `release.yml`: OIDC Trusted Publishing, environment gate, `contents: read` + `id-token: write` publish-job-only | integration (CI) | tag `v*` push / dispatch | ❌ Wave 0 | ⬜ pending |
| B | TBD | 2 | OPS-02 | infra vs drift | `live-interop.yml` scheduled probe; 405 → exit 10 DRIFT fails loudly | integration (live) | `dotnet run --project tools/Iwasug.WhatsApp.Sdk.InteropProbe` | ❌ Wave 0 | ⬜ pending |
| C | TBD | 1–2 | OPS-01 | — | README lead message + absolute links; `docs/` guides; risk ledger honest (incl. cannot-mitigate list) | review + doc checks | human review against UAT checklist | ❌ Wave 0 | ⬜ pending |
| C | TBD | 2 | OPS-01 | trust (repudiation) | reach-first-send UAT checklist executed honestly (CONN-01..03 status kept) | manual UAT (phone) | end-of-phase checklist | ❌ Wave 0 | ⬜ pending |

*Note: W0/A/B/C markers are the research workstreams — the planner replaces them with real task IDs (`09-NN-TT`) once plans exist.*

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
