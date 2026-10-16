<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| OPS-01 | Ship as a NuGet package with docs (README lead message, risk ledger, store-implementation guide, webhook recipe) | Packaging state audited in depth (Section "Standard Stack" + "Code Examples"): what already packs (README redeployed as package readme, XML docs, repository metadata, MIT expression, dual packable projects) and what is missing (sample must be un-packable, PackageIcon, LICENSE in package, snupkg, ContinuousIntegrationBuild). Publish mechanics verified against live nuget.org docs (Trusted Publishing, 1-hour temp key, workflow-file-bound policy). Docs deliverables mapped: README rewrite (it *is* the package readme → absolute URLs), `docs/store-implementation.md` (8 store interfaces + StoreRegistry + contract kit), `docs/webhook-recipe.md` (host-side bridge over `ReadEventsAsync`/`WhatsAppEvent`), risk ledger (already in README — relocate/expand honestly incl. RegistrationDefaults placeholder caveat). |
| OPS-02 | CI with unit/golden-fixture tests, isolation suite, and a gated live-interop job that detects protocol drift (405) | CI does not exist yet (0 workflows — re-verified). Exact commands proven in-repo: `dotnet build ... --nologo`, `dotnet test Iwasug.WhatsApp.Sdk.sln` (never `--nologo` — IN-10 quirk documented in the test csproj, exits 5 "Zero tests ran"), isolation filter `FullyQualifiedName~Isolation` (07-VERIFICATION: 8/8 green). Current action tags verified via GitHub API this session (checkout v7.0.1, setup-dotnet v6.0.0, upload-artifact v7.0.2). Drift probe design grounded in verified SDK behavior: 405 → `DisconnectReason.ClientOutdated` (terminal, `ReconnectPolicy.cs:150`) → `PairingStatus.ClientOutdated`; fresh-registration connect surfaces a `PairingStatus.Code` (QR ref) that proves the server accepted the client version — no phone needed. Four pre-existing test failures must be resolved first (exact names below). |
</phase_requirements>

# Phase 9: Ship - Research

**Researched:** 2026-10-17
**Domain:** NuGet release engineering, GitHub Actions CI/CD, protocol-drift monitoring, OSS provenance & API-surface governance
**Confidence:** HIGH (in-repo + live-registry verification); MEDIUM on external-service behaviors that depend on user account/plan choices

## Summary

Phase 9 completes a release pipeline around a feature-complete SDK. The build half of packaging is already wired (git-height versioning via Nerdbank.GitVersioning, README-as-package-readme, XML docs, repository URL/type, MIT expression, two packable projects); the gaps are precise and few: **the sample project currently packs** (fresh `dotnet pack` emitted `Iwasug.WhatsApp.Sdk.PairingSample.0.1.341.nupkg`), no `PackageIcon`, no LICENSE file inside the package, no `.snupkg`, no `ContinuousIntegrationBuild`, and the public API surface is **not yet frozen** (all 1353 SDK + 182 testing-kit entries sit in `PublicAPI.Unshipped.txt`; `Shipped.txt` contains only `#nullable enable`).

CI has never existed (`0` workflows on the repo — re-verified). A blocking prerequisite: the full suite is **709 total / 705 passed / 4 failed**, all four in `PairingLifecycleTests` with 20-second `TaskCanceledException` timeouts (accepted as "environmental" baseline in prior phase gates). CI cannot gate on a red suite — Wave 0 must fix or explicitly quarantine these with tracking.

Publishing should use **nuget.org Trusted Publishing** (keyless OIDC): `NuGet/login@v1` exchanges a GitHub OIDC token for a **1-hour** temp API key; the policy binds **Repository Owner + Repository + Workflow File name** (`release.yml`, file name only) — the workflow filename becomes part of the trust contract. Two plan-critical realities: the repo is **private**, and on GitHub Free/Pro/Team plans **required reviewers and environment secrets only work on public repositories** — so either the repo goes public (also required by the project's open-source goal) or publication is gated by branch protection/CODEOWNERS instead. Both nuget.org IDs (`Iwasug.WhatsApp.Sdk`, `Iwasug.WhatsApp.Sdk.Testing`) are currently **unclaimed** (flatcontainer 404) — first publish claims ownership.

**Primary recommendation:** structure the phase as three workstreams with one publish path: (A) Packaging + provenance (unpackable sample, icon/LICENSE/snupkg/CI-build switches, PublicAPI freeze, NOTICE audit row for Phase 7, versioning/trigger decision), (B) CI (`ci.yml` on push/PR with the empirically proven commands; `release.yml` tag-triggered via Trusted Publishing behind a `release` environment; `live-interop.yml` scheduled + dispatch with an exit-code-triaged drift probe), (C) Docs (README lead message + absolute links, `docs/store-implementation.md`, `docs/webhook-recipe.md`, risk ledger, end-of-phase human UAT checklist mirroring the Phase 3–7 pattern). Publish from a **tag** (`v0.1.*`) — but that requires adding a tag regex to `version.json`'s `publicReleaseRefSpec`, otherwise the package version gains a `-g<sha>` suffix. Reuse the existing `PairingSample` public-API pattern verbatim for the probe; no new third-party packages are required (one optional: `coverlet.MTP` if a coverage artifact is wanted — `coverlet.collector` is inert under the MTP test runner).

## Architectural Responsibility Map

This phase is release engineering, so tiers are build/CI/registry surfaces rather than browser/API tiers.

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Version derivation (0.1.<git-height>) | Build (NBGV in MSBuild) | CI checkout (`fetch-depth: 0`) | NBGV computes from git history; CI must not shallow-clone |
| Package composition (readme, docs XML, icon, license, symbols) | Build (packable csprojs + Directory.Build.targets) | — | All packaging metadata is MSBuild; verified current state by fresh pack |
| Test gate (suite + isolation + golden fixtures) | CI (GitHub Actions `ci.yml`) | Build (xunit.v3/MTP runner) | Fixtures run inside the suite; isolation is a filtered second pass for visibility |
| Publish authentication | External service (nuget.org Trusted Publishing) | CI (OIDC token issuance) | Keyless exchange; no long-lived key exists anywhere |
| Release approval gate | GitHub repo settings (environment w/ required reviewers) | CI (`environment: release` on job) | Approval is repo configuration, not workflow logic — and public-repo-only on Free/Pro/Team |
| Protocol-drift monitoring | CI (scheduled `live-interop.yml` exercising the SDK as a black box) | SDK itself (`WaVersionResolver` 405 handling) | Drift = external fact; probe classifies and fails loudly (exit codes) |
| Public API freeze | Build (PublicApiAnalyzers + warnings-as-errors) | Repo files (`PublicAPI.Shipped.txt`) | Gate is compiler-level; freeze = moving entries files |
| Provenance audit (NOTICE/LICENSE/MPL) | Repo docs (`NOTICE.md`, `LICENSE`) | Build (license expression → nuspec) | Human audit + evidence log; the package metadata is the machine-readable part |
| Docs surface | Repo files (README = package readme; `docs/` guides) | — | README ships inside the nupkg → its links must be absolute; long guides live outside |

## Standard Stack

### No new packages are required for this phase

Everything CI/packaging needs is already in the repo's pinned stack (research 01–08, `Directory.Packages.props`), or is an in-box SDK switch. This is deliberate: fewer moving parts for a provenance-audited release.

| Component | Version | Purpose | Why This / Already Verified |
|-----------|---------|---------|-----------------------------|
| .NET SDK | 10.0.401 (`global.json`, rollForward latestPatch) | Build/test/pack | [VERIFIED: global.json:1-9 read this session; `dotnet --version` = 10.0.401 in a pack run this session] |
| Nerdbank.GitVersioning | 3.10.94 (pinned) | Git-height SemVer2 package versions | [VERIFIED: Directory.Packages.props:50 + Directory.Build.props:29] |
| Microsoft.CodeAnalysis.PublicApiAnalyzers | 5.6.0 (pinned) | Public-surface gate (RS0016/RS0017/RS0041) | [VERIFIED: Directory.Packages.props:34; wired as AdditionalFiles in both packable csprojs] |
| xunit.v3 + MTP | 4.0.1, runner pinned in global.json (`"test": { "runner": "Microsoft.Testing.Platform" }`) | Test execution | [VERIFIED: global.json:6-8; 709 tests observed] |
| SourceLink | bundled in .NET SDK 8+ | Source-linked symbols for GitHub | [CITED: learn.microsoft.com/dotnet/standard/library-guidance/sourcelink] |
| coverlet.collector | 10.1.0 (already referenced) | **Inert under MTP** — see Pitfall 6 | [VERIFIED: tests csproj:43; CITED: coverlet README — "coverlet.collector and coverlet.msbuild cannot be used with the Microsoft Testing Platform (MTP)"] |
| actions/checkout | v7.0.1 | CI checkout (needs `fetch-depth: 0`) | [VERIFIED: GitHub API releases/latest this session] |
| actions/setup-dotnet | v6.0.0 | SDK install from global.json (+ optional NuGet cache) | [VERIFIED: GitHub API releases/latest this session] |
| actions/upload-artifact | v7.0.2 | Test/probe logs & package artifacts | [VERIFIED: GitHub API releases/latest this session] |
| NuGet/login | v1 | OIDC → temp NuGet API key | [VERIFIED: NuGet/login repo exists (github.com/NuGet/login, pushed 2026-10-13) + documented usage `NuGet/login@v1` on the live Trusted Publishing doc] |
| `coverlet.MTP` (**optional**, only if a coverage artifact is wanted) | 10.1.0 | MTP-native coverage (`dotnet test --coverlet`) | [VERIFIED: nuget.org search — 1,030,291 downloads, owner daveMueller, project github.com/coverlet-coverage/coverlet] |

**Installation:** none — this phase adds zero `PackageReference`s to the shipped SDK. `coverlet.MTP` (optional) would be a test-project-only private asset, same family/owner as the already-present `coverlet.collector`.

**Version verification:** performed this session — action tags via `gh api repos/<owner>/<repo>/releases/latest`; package IDs via `api.nuget.org` flatcontainer (both project IDs → HTTP 404 = unclaimed); `coverlet.MTP` via `azuresearch-usnc.nuget.org` query endpoint (v10.1.0).

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Trusted Publishing (OIDC) | Long-lived NuGet API key in repo secret | Works offline/instantly and is plan-independent, but leaves a never-expiring push credential in GitHub and requires rotation; Trusted Publishing is the documented modern path [CITED: learn.microsoft.com/nuget/nuget-org/trusted-publishing] |
| Tag-triggered publish (`v0.1.*`) | Publish on every master push | Master is already inside `publicReleaseRefSpec` (no `version.json` change), but every merge publishes; tags give an immutable release record and a natural environment-gate point |
| `coverlet.MTP` (if coverage wanted) | `--collect:"XPlat Code Coverage"` (VSTest syntax) | **Broken under MTP** — data collectors are not used; do not copy VSTest coverage commands into CI [CITED: coverlet docs] |
| GitHub-hosted runner for the live probe | Self-hosted runner | Hosted runners come from datacenter IP ranges; WhatsApp may throttle/block them (unverifiable from here — flag). Self-hosted (home/residential IP) is the escape hatch if hosted runs prove systematically blocked |
| A dedicated probe console project | Extend `PairingSample` with a probe mode | Sample is packed-content-adjacent (un-packable target) and user-facing; a tiny `tools/` probe keeps CI concerns out of the docs-facing sample. Extending the sample is acceptable if the planner prefers one artifact to maintain |

## Package Legitimacy Audit

No new runtime/build packages are introduced by this phase. The only candidate addition is the optional coverage extension.

| Package | Registry | Age / Activity | Downloads | Source Repo | Verdict | Disposition |
|---------|----------|----------------|-----------|-------------|---------|-------------|
| `coverlet.MTP` (optional) | NuGet | Current line 10.1.0, actively maintained | 1,030,291 total | github.com/coverlet-coverage/coverlet (org verified; owner `daveMueller`) | OK | Approved **only if** planner includes a coverage artifact. The legitimacy seam's `package-legitimacy check` does not support the NuGet ecosystem; verification done via nuget.org search API + official coverlet docs instead. |

**Packages removed due to SLOP verdict:** none
**Packages flagged as suspicious [SUS]:** none
**Not packages (context):** GitHub Actions are pinned by tag to first-party/vendor repos (`actions/*` = GitHub; `NuGet/login` = nuget.org's own action under the `NuGet` org). Tag pinning is the repo-friendly choice; SHA pinning is stronger supply-chain hygiene if the planner wants it.

## Architecture Patterns

### System Architecture Diagram

Three independent pipelines share one build definition surface. Data flow:

```
 [push / pull_request]                      [tag v0.1.* / dispatch]                 [schedule Mon 06:00 / dispatch]
        │                                          │                                          │
        ▼                                          ▼                                          ▼
 .github/workflows/ci.yml               .github/workflows/release.yml             .github/workflows/live-interop.yml
        │                                          │                                          │
 checkout fetch-depth:0 → setup-dotnet  checkout fetch-depth:0 → setup-dotnet     checkout fetch-depth:0 → setup-dotnet
        │                                          │                                          │
 build (--nologo)                       build -c Release (ContinuousIntegrationBuild)      dotnet run tools/InteropProbe
        │                                          │                                          │
        ├─ test: unit + golden fixtures  ───────►  test -c Release (same suite)              ├─ Noise connect (fresh registration)
        ├─ test: Isolation filter                  │                                          ├─ waiting for PairingStatus.Code [?]
        │                                          pack -o artifacts ──► verify-pack script   │        └─ ClientOutdated(405)? ──► exit 10 DRIFT
        └─ upload logs/artifacts                   │      (README/LICENSE/icon/2×nupkg+snupkg, │        └─ transport/handshake fail ─► exit 20 INFRA
                                                   │       NO sample nupkg)
                                                   │                                          └─ exit 0 = server accepted client version
                                                   ▼
                                          environment: release  ──► (approval, when repo public)
                                                   │
                                                   ▼
                                          NuGet/login@v1 (OIDC → temp key, ≤1h lifetime)
                                                   │
                                                   ▼
                                          dotnet nuget push artifacts/*.nupkg --skip-duplicate
                                                   │   (sibling .snupkg auto-pushed)
                                                   ▼
                                          nuget.org  (first publish CLAIMS both package IDs)
```

### Recommended Project Structure

```
.github/workflows/
├── ci.yml               # push/PR: build + full suite + isolation filter (blocking)
├── release.yml          # tag v0.1.* / dispatch: pack → verify → approve → OIDC push
└── live-interop.yml     # schedule + dispatch: drift probe with exit-code triage
tools/
└── Iwasug.WhatsApp.Sdk.InteropProbe/   # tiny console probe (public API only)
docs/
├── store-implementation.md   # 8 store interfaces + StoreRegistry + contract-kit usage
├── webhook-recipe.md         # host-side bridge: WhatsAppEvent → your webhook
└── risk-ledger.md            # relocated/expanded ban-safety ledger + cannot-mitigate list
assets/
└── icon.png             # 128×128 PNG for PackageIcon
scripts/
└── verify-pack.ps1      # asserts package contents post-pack (fails CI on drift)
```

### Pattern 1: Public-surface freeze (NOTICE-verifiable, compiler-enforced)
**What:** Move every entry from `PublicAPI.Unshipped.txt` → `PublicAPI.Shipped.txt` on both packable projects; keep `#nullable enable` as line 1 in both files. After the freeze, any public API change requires a deliberate Unshipped edit — with `TreatWarningsAsErrors=true` (already on) the build fails otherwise.
**When to use:** In this phase, after docs/API wording is final but before the first publish.
**Evidence:** `PublicAPI.Shipped.txt` currently contains exactly `#nullable enable` in both projects; Unshipped holds 1353 (SDK) + 182 (testing kit) lines [VERIFIED: read + counted this session].

### Pattern 2: Trusted Publishing release job (verbatim doc pattern, adapted)
**What:** `permissions: id-token: write` on the publish job; `NuGet/login@v1` with `user: ${{ secrets.NUGET_USER }}` (nuget.org **profile name**, not email); push with `${{ steps.login.outputs.NUGET_API_KEY }}`. Request the key immediately before push (1-hour validity; one token → one key).
**When to use:** The `release.yml` publish job only. Never on `pull_request`.
**Policy side (user action):** nuget.org → Trusted Publishing → policy = Repository Owner `iwasug`, Repository `WhatsApp.Sdk`, Workflow File `release.yml` (**file name only**), optional Environment `release`. Note for private repos: policy starts "pending full activation" (7-day window, restartable) and becomes permanent after the first successful publish. [VERIFIED: fetched learn.microsoft.com/nuget/nuget-org/trusted-publishing this session — quotes: temp keys "valid for **1 hour**"; "Enter the **file name only** (`build.yml`)—do not include the `.github/workflows/` path"]

### Pattern 3: Exit-code-triaged drift probe
**What:** The probe connects with a fresh registration (no credentials) and watches `PairingStatus` on `connection.Pairing`. First `PairingStatus.Code` (server-issued QR ref) = *server accepted our client version* → exit 0, no phone needed. `PairingStatus.ClientOutdated` at any point → exit 10 (DRIFT — fail loudly). Transport/DNS/handshake/timeout failures → exit 20 (INFRA). The workflow job fails on both 10 and 20 but annotates which (so a blocked datacenter IP is never mistaken for protocol drift).
**Grounded in verified code:** 405 maps to `DisconnectReason.ClientOutdated = 405` [VERIFIED: PublicAPI.Unshipped.txt:88 verbatim: `Iwasug.WhatsApp.Sdk.Connection.DisconnectReason.ClientOutdated = 405 -> Iwasug.WhatsApp.Sdk.Connection.DisconnectReason`] and is terminal [VERIFIED: ReconnectPolicy.cs:150 verbatim: `401 or 403 or 406 or 411 or 419 or (int)DisconnectReason.ClientOutdated => ReconnectDecision.Terminal.Instance,`]. The version the probe exercises is the live-resolved one: `WaVersionResolver` fetches `https://web.whatsapp.com/sw.js` with a ~6h cache, falls back to the bundled pin `[2, 3000, 1043857760]`, and a 405 clears the cache [VERIFIED: WaVersionResolver.cs:100-110 remarks, read this session].
**Honest scope:** this detects the *client-version-rejected* class of drift (405). It is a smoke detector, not a protocol conformance suite — say so in the workflow comment and docs.

### Anti-Patterns to Avoid
- **Copying VSTest habits into MTP CI:** `--collect:"XPlat Code Coverage"`, runsettings files, or `coverlet.collector` usage silently produce nothing (or fail) under the MTP runner. Use `coverlet.MTP --coverlet` or skip coverage.
- **Shallow checkout in any job:** NBGV silently computes a wrong height without `fetch-depth: 0` — the published version becomes untrustworthy.
- **Passing `--nologo` to `dotnet test`:** documented repo quirk (IN-10) — exits 5 with "Zero tests ran" on SDK 10.0.401. The test csproj carries a comment: "always invoke `dotnet test` WITHOUT that flag" [VERIFIED: tests csproj:17-21].
- **Publishing in the same job that runs fork/PR code:** publish only from trusted refs, ideally behind an environment, with `permissions` scoped to `contents: read` + (publish job only) `id-token: write`.
- **Leaving the sample packable:** packaging a sample as a NuGet package (observed!) invites an accidental publish and pollutes the "exactly two packable projects" invariant (D-12: "the solution contains exactly two packable projects" — test csproj comment line 5-6).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Version strings | Manual version bumps in csproj | NBGV (already wired; `version.json`) | Height-based, signed, reproducible; a manual bump already exists nowhere in this repo |
| OIDC token exchange for nuget.org | Custom curl flow + jq (that's the *GitLab* pattern) | `NuGet/login@v1` on GitHub | First-party action; handles the exchange and outputs `NUGET_API_KEY` |
| Symbols package tooling | Custom `.symbols.nupkg` assembly | `IncludeSymbols=true` + `SymbolPackageFormat=snupkg` (SDK switches) | SDK-native; snupkg is the modern format; sibling auto-push on `dotnet nuget push` |
| Public API diffing | Reflection-based surface dumper script | PublicApiAnalyzers (already wired) | Compile-time errors beat post-hoc diffs; zero runtime cost; PR-reviewable text files |
| Package content verification | Trusting `dotnet pack` output | A tiny `verify-pack.ps1` asserting expected entries + absence of the sample package | Cheap, deterministic gate right before the irreversible push |
| Drift detection framework | A generic uptime monitor | The SDK's own connect path + `PairingStatus` taxonomy | The SDK *is* the client under test; the taxonomy (Code/Success/Timeout/LoggedOut/ClientOutdated) is already public API |

**Key insight:** this phase is integration, not invention — nearly every capability already exists in the repo or in first-party tooling; the work is wiring, gating, and honest documentation.

## Common Pitfalls

### Pitfall 1: The sample project packs today
**What goes wrong:** `dotnet pack Iwasug.WhatsApp.Sdk.sln` emits **three** packages — including `Iwasug.WhatsApp.Sdk.PairingSample.0.1.341.nupkg` (21,281 B), with a warning: "The package Iwasug.WhatsApp.Sdk.PairingSample.0.1.341 is missing a readme."
**Why it happens:** `IsPackable` is not set false on the sample (its csproj has only `OutputType=Exe` + AssemblyName) [VERIFIED: sample csproj:1-16 read this session; fresh pack this session reproduced the third nupkg].
**How to avoid:** `<IsPackable>false</IsPackable>` in the sample csproj (and assert in the release workflow that exactly the 2 expected packages were produced — the verify-pack script).
**Warning signs:** three `Successfully created package` lines in pack output; a warning about a missing readme for a project you never meant to ship.

### Pitfall 2: Four pre-existing suite failures block a green CI gate
**What goes wrong:** CI gates on the suite; the suite is 709 total / **4 failed** / 705 succeeded. All four are `Iwasug.WhatsApp.Sdk.Tests.Pairing.PairingLifecycleTests`, each dying with a 20s `TaskCanceledException` in `WaitForPairingEventAsync`:
`Companion_finish_from_a_rotated_code_epoch_is_rejected`, `Companion_finish_with_foreign_bundle_key_fails_closed`, `Persist_window_suppresses_watchdog_timeout_until_durable_success`, `Companion_hello_ack_finish_reaches_verified_success` [VERIFIED: fulltest.log lines 2-32, `total: 709 / failed: 4 / succeeded: 705`, this session].
**Why it happens:** Labeled "environmental timeouts" and accepted as a baseline in Phase 4–8 gates; root cause never established.
**How to avoid:** Wave 0 task: root-cause and fix (preferred) or quarantine with an explicit tracking issue — never let CI land red. Linux CI behavior is unknown (may differ from Windows) — verify on the runner as part of the task.
**Warning signs:** `dotnet test` exit code ≠ 0 regardless of environment.

### Pitfall 3: NBGV + shallow checkout = wrong published version
**What goes wrong:** Without `fetch-depth: 0`, git height is wrong and the package version (e.g. `0.1.341` from a local full-history pack) diverges silently.
**How to avoid:** `fetch-depth: 0` in every job that builds or packs. [CITED: dotnet/NBGV repo docs — full history required for version height]

### Pitfall 4: Tag-triggered publish gets a `-g<sha>` suffix
**What goes wrong:** `version.json` only treats `master`/`main` refs as public releases [VERIFIED: version.json:4 verbatim: `"publicReleaseRefSpec": ["^refs/heads/master$", "^refs/heads/main$"],`]. Building on a `v0.1.x` tag produces a non-public version (suffix), which is not what you want on nuget.org (or, worse, publishes the suffix and burns the version number).
**How to avoid:** either trigger the release from `master` (already in spec), or add a tag regex (e.g. `"^refs/tags/v.*$"`) to `publicReleaseRefSpec` in the same PR that adds `release.yml`. Decide the trigger **before** writing the workflow.

### Pitfall 5: Trusted Publishing policy/workflow coupling
**What goes wrong:** The nuget.org policy binds the **workflow file name** (`release.yml`). Renaming the file breaks publishing with an opaque error. Additionally: temp keys live **1 hour** — logging in early and pushing late fails; one OIDC token yields exactly one key (a retry needs a fresh `NuGet/login` step).
**How to avoid:** treat the filename as part of the release contract; `NuGet/login` step immediately precedes push. For private repos, publish within the 7-day "pending full activation" window or restart it. [VERIFIED: fetched Trusted Publishing doc this session]

### Pitfall 6: Coverage commands from the VSTest era don't work under MTP
**What goes wrong:** CI steps like `dotnet test --collect:"XPlat Code Coverage"` produce no coverage (data collectors are skipped under MTP), and `coverlet.collector` (already referenced in the test project) does nothing there.
**How to avoid:** either skip coverage entirely (OPS-02 doesn't require it) or add `coverlet.MTP` and run `dotnet test --coverlet` (default output `coverage.json`; `--coverlet-output-format cobertura` available). [CITED: coverlet README + MTP integration doc — "coverlet.collector and coverlet.msbuild cannot be used with the Microsoft Testing Platform (MTP)"]

### Pitfall 7: Environment gating is public-repo-only on Free/Pro/Team
**What goes wrong:** Configuring a `release` environment with required reviewers fails silently in intent: on GitHub Free, Pro, and Team plans "required reviewers are only available for public repositories" (likewise wait timers, deployment branch rules, environment secrets).
**How to avoid:** make the repo public (it also needs to be for the open-source goal and SourceLink UX), or gate via branch protection + CODEOWNERS; keep the `environment:` reference in `release.yml` anyway so the gate activates the moment the repo becomes public. [VERIFIED: fetched docs.github.com/en/actions/reference/deployments-and-environments this session — quote above]

### Pitfall 8: setup-dotnet cache with no lock files
**What goes wrong:** `cache: true` with no `packages.lock.json` anywhere (this repo has none) fails to resolve a cache key.
**How to avoid:** set `cache-dependency-path` explicitly (e.g. `Directory.Packages.props` + the csproj glob), or skip caching. [CITED: actions/setup-dotnet README — default dependency path is `**/packages.lock.json`]

### Pitfall 9: README is the package readme — two audiences, one file
**What goes wrong:** The README is packed into the nupkg (`PackageReadmeFile` wired in Directory.Build.targets:23; exercised in the packed nupkg). Relative links/images break on nuget.org; a long guide-style README bloats the first-publish experience; and the current status section is stale ("Phases 1–5 complete … Media, multi-account orchestration, broadcast, and full presence arrive in later phases" — Phases 6–8 are done).
**How to avoid:** rewrite with tight lead message + absolute URLs; push deep material to `docs/` and link absolutely. Any README edit changes shipped package content — re-pack and re-verify.

### Pitfall 10: Live probe can fail for non-drift reasons
**What goes wrong:** GitHub-hosted runners egress from datacenter IP ranges; WhatsApp may block/throttle them — a connection refused/timeout is *not* protocol drift but would look like a red job.
**How to avoid:** exit-code taxonomy (10 = drift, 20 = infra), clear failure annotations, and a documented self-hosted fallback. Treat first runs as calibration.

### Pitfall 11: `dotnet nuget push` pushes the sibling snupkg automatically
**What goes wrong:** Pushing `artifacts/*.nupkg` also pushes each sibling `.snupkg`; separate steps or double pushes cause duplicate/skip errors.
**How to avoid:** one push step, `--skip-duplicate` for idempotent re-runs, verify the artifacts directory contains exactly the expected files.

### Pitfall 12: Registration material gap undercuts "reach first send from docs alone"
**What goes wrong:** First-time pairing/registration requires real signed-prekey material in `RegistrationDefaults`; the factory's default path logs "First-time pairing/registration will be rejected by the server until real signed-prekey material is supplied" [VERIFIED: DefaultAccountRuntimeFactory.cs:206-210 verbatim]. A new host following docs alone can get stuck between install and first send.
**How to avoid:** the store-implementation/quickstart docs must state exactly what `RegistrationDefaults` needs and why; add an end-of-phase human UAT checklist (mirroring Phases 3–7 pattern) for the reach-first-send claim; keep CONN-01..03's NOT VERIFIED status honest in docs.

## Code Examples

### CI workflow skeleton (verified commands + verified action tags)
```yaml
# .github/workflows/ci.yml
name: ci
on:
  push: { branches: [ master ] }
  pull_request:
jobs:
  build-test:
    runs-on: ubuntu-latest
    permissions: { contents: read }
    steps:
      - uses: actions/checkout@v7.0.1
        with: { fetch-depth: 0 }              # NBGV needs full history
      - uses: actions/setup-dotnet@v6.0.0
        with:
          global-json-file: global.json       # pins SDK 10.0.401
          cache: true
          cache-dependency-path: Directory.Packages.props   # no packages.lock.json in repo
      - run: dotnet build Iwasug.WhatsApp.Sdk.sln --nologo
      - run: dotnet test Iwasug.WhatsApp.Sdk.sln            # NEVER --nologo (IN-10 quirk)
      - name: Isolation suite
        run: dotnet test Iwasug.WhatsApp.Sdk.sln --filter FullyQualifiedName~Isolation
```
Filter syntax is empirically proven in this repo: `Filter FullyQualifiedName~Isolation: 8/8 passed` [VERIFIED: 07-VERIFICATION.md:71; the longer form in 07-VERIFICATION.md:128 ran 17 passed with the Hosting union].

### Release workflow skeleton (Trusted Publishing)
```yaml
# .github/workflows/release.yml
name: release
on:
  push: { tags: [ 'v*' ] }        # requires "^refs/tags/v.*$" added to version.json publicReleaseRefSpec
  workflow_dispatch:
jobs:
  publish:
    runs-on: ubuntu-latest
    environment: release          # activates required-reviewers gate when repo is public
    permissions:
      contents: read
      id-token: write             # OIDC issuance for NuGet/login
    steps:
      - uses: actions/checkout@v7.0.1
        with: { fetch-depth: 0 }
      - uses: actions/setup-dotnet@v6.0.0
        with: { global-json-file: global.json }
      - run: dotnet build Iwasug.WhatsApp.Sdk.sln -c Release --nologo -p:ContinuousIntegrationBuild=true
      - run: dotnet test Iwasug.WhatsApp.Sdk.sln -c Release
      - run: dotnet pack Iwasug.WhatsApp.Sdk.sln -c Release --nologo -o artifacts -p:ContinuousIntegrationBuild=true
      - name: Verify package contents
        shell: pwsh
        run: ./scripts/verify-pack.ps1 artifacts   # asserts 2 nupkgs + snupkgs; no sample package; README/LICENSE/icon present
      - uses: NuGet/login@v1
        id: login
        with:
          user: ${{ secrets.NUGET_USER }}   # nuget.org profile name, NOT email
      - run: >-
          dotnet nuget push artifacts/*.nupkg
          --api-key ${{ steps.login.outputs.NUGET_API_KEY }}
          --source https://api.nuget.org/v3/index.json
          --skip-duplicate
```
[Source: learn.microsoft.com/nuget/nuget-org/trusted-publishing — pattern adapted with this repo's commands]

### Packaging switches to add (build-tier completeness)
```xml
<!-- sample csproj: stop packing the sample -->
<PropertyGroup>
  <IsPackable>false</IsPackable>
</PropertyGroup>

<!-- both packable projects (or Directory.Build.targets under IsPackable condition): -->
<PropertyGroup>
  <IncludeSymbols>true</IncludeSymbols>
  <SymbolPackageFormat>snupkg</SymbolPackageFormat>
  <PublishRepositoryUrl>true</PublishRepositoryUrl>
  <EmbedUntrackedSources>true</EmbedUntrackedSources>
  <PackageIcon>icon.png</PackageIcon>
  <PackageProjectUrl>https://github.com/iwasug/WhatsApp.Sdk</PackageProjectUrl>
</PropertyGroup>
<ItemGroup>
  <None Include="../../assets/icon.png" Pack="true" PackagePath="\" />
  <None Include="../../LICENSE" Pack="true" PackagePath="\" />
</ItemGroup>
```

### Drift-probe core (public API only — pattern copied from PairingSample)
```csharp
// tools/Iwasug.WhatsApp.Sdk.InteropProbe/Program.cs  (sketch)
// Construction mirrors samples/Iwasug.WhatsApp.Sdk.PairingSample/Program.cs:212-232
// (same HandshakePayloadProvider + RegistrationDefaults + WaConnection + WebSocketFrameTransport shape;
//  exact AccountId/store creation copied from the sample).
var connection = new WaConnection(
    accId,
    () => new WebSocketFrameTransport(new Uri("wss://web.whatsapp.com/ws/chat")),
    payloads);
var pairing = connection.Pairing;
var terminal = new TaskCompletionSource<PairingStatus>();

pairing.StateChanged += (_, e) =>
{
    switch (e.Status)
    {
        case PairingStatus.Code:          // server issued a QR ref ⇒ our client version was accepted
            Console.WriteLine($"PROBE OK: server issued pairing ref ({e.Payload?.Length ?? 0} chars)");
            terminal.TrySetResult(e.Status);
            break;
        case PairingStatus.ClientOutdated: // DisconnectReason.ClientOutdated == 405 ⇒ drift
            terminal.TrySetResult(PairingStatus.ClientOutdated);
            break;
        case PairingStatus.Success or PairingStatus.Timeout or PairingStatus.LoggedOut:
            terminal.TrySetResult(e.Status);
            break;
    }
};

// connect (same await pattern as the sample), wait with timeout:
//   PairingStatus.Code            → exit 0  (server accepted client version)
//   PairingStatus.ClientOutdated  → exit 10 (PROTOCOL DRIFT — 405)
//   anything else / exception     → exit 20 (INFRA: network, DNS, handshake transport)
```
[Grounded in PairingSample Program.cs:212-269 read this session; `PairingStatus.ClientOutdated = 4` and `DisconnectReason.ClientOutdated = 405` are public API entries — PublicAPI.Unshipped.txt:88,748]

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Long-lived NuGet API key in CI secret | Trusted Publishing (OIDC → 1-hour temp key) | GA 2025 | No stored push credential; policy binds repo + workflow file |
| VSTest + `--collect:"XPlat Code Coverage"` | MTP + `coverlet.MTP --coverlet` | .NET 10 SDK defaults to MTP mode (xunit.v3) | VSTest-style collectors inert; coverage steps must be MTP-aware |
| `symbols.nupkg` | `.snupkg` via `IncludeSymbols`/`SymbolPackageFormat` | NuGet 5.x era | Sibling auto-push; smaller payload |
| Manual version bumps | Git-height versioning (NBGV) | already adopted here | CI must full-clone; tags need `publicReleaseRefSpec` entries |
| Manual API docs/diff | PublicApiAnalyzers text files | already adopted here | Freeze = file move; warnings-as-errors is the gate |

**Deprecated/outdated in this repo right now:** README status section (says Phases 1–5, claims media/multi-account "arrive in later phases" though Phases 6–8 shipped); `coverlet.collector` reference (inert under MTP); `PublicAPI.Unshipped.txt`-only state (pre-freeze).

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | GitHub account is on the Free plan (drives whether required-reviewers/env-secrets work privately) | Pitfall 7 / Open Q2 | If Enterprise, private-repo environment gating works without going public; if Free/Pro/Team, must go public or use branch protection |
| A2 | WhatsApp accepts a fresh-registration Noise connect from GitHub-hosted runner IPs | Drill probe design / Pitfall 10 | Probe always exits 20 → useless drift signal until a self-hosted runner is introduced |
| A3 | The 4 failing pairing tests behave the same on Linux CI as on this Windows box | Pitfall 2 | If they fail identically on CI, quarantine decision must land in Wave 0 (cannot be deferred) |
| A4 | Publish trigger preference: tag `v0.1.*` (with `version.json` change) | Pitfall 4 / Open Q1 | If master-push is preferred, no version.json change; if tag without the spec change, version gets `-g` suffix |
| A5 | Planner will accept skipping coverage in CI (OPS-02 doesn't require it) | Pitfall 6 | If coverage is wanted, add `coverlet.MTP` 10.1.0 (audit OK) + `dotnet test --coverlet`; still no VSTest syntax |

## Open Questions

1. **Publish trigger + `version.json`**
   - What we know: master/main are public-release refs today; tags are not.
   - What's unclear: user's preferred release ceremony (merge-to-master auto-publish vs explicit `v0.1.N` tag).
   - Recommendation: tag-triggered; add `"^refs/tags/v.*$"` to `publicReleaseRefSpec` in the same PR as `release.yml`.

2. **Make the repo public?**
   - What we know: repo is private; project goal says open source; Free/Pro/Team environments need public; SourceLink UX and NuGet page assume public.
   - What's unclear: timing/plan (user action, outside the repo).
   - Recommendation: flag as an explicit user action in the phase plan (before or right after first publish); design the workflow so it works either way.

3. **Fix vs quarantine the 4 failing tests**
   - What we know: 20s timeouts in `WaitForPairingEventAsync`, stable across runs, accepted as baseline in earlier phase gates.
   - What's unclear: root cause (test watchdog wiring vs product-side timer) and Linux behavior.
   - Recommendation: Wave 0 root-cause fix; if genuinely environmental, quarantine with a tracking issue and an explicit CI-visible note — never a silent skip (the isolation plan explicitly forbids skip/ignore attributes as a pattern).

4. **Live-probe runner + schedule economics**
   - What we know: probe is phone-free and cheap (~1 min); tariff is connection + waiting for QR ref.
   - What's unclear: hosted-runner IP acceptance; desired schedule.
   - Recommendation: weekly `schedule` + `workflow_dispatch`; triage exit codes; calibrate with 2–3 manual runs before enabling the schedule; self-hosted fallback documented.

5. **Traceability lag before "Ship"**
   - What we know: STATE.md says "Phase 08 complete, ready to verify Phase 6"; REQUIREMENTS.md shows MED-01 Pending; Phase 6 verification pending.
   - What's unclear: whether the milestone requires reconciling those before Phase 9 closes.
   - Recommendation: carry as a milestone-close checklist item for the planner (not a Phase 9 blocker, but a release-honesty item).

6. **Docs IA**: README ships in the package → keep it a tight lead message + absolute links; long-form guides live in `docs/` (not packaged). Confirm that "risk ledger" lives in `docs/risk-ledger.md` with a README summary link (nuget.org renders the README only).

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | build/test/pack (all) | ✓ | 10.0.401 | — |
| git | NBGV version height | ✓ | 2.56.0.windows.2 | — |
| GitHub CLI (gh) | release/CI setup steps, repo ops | ✓ | 2.102.0, authed as `iwasug` | Manual web UI |
| Node.js | not required (no JS tooling) | ✓ | v26.10.0 | — |
| nuget.exe | not required (`dotnet nuget` covers push) | ✗ | — | `dotnet nuget push` |
| GitHub repo (iwasug/WhatsApp.Sdk) | everything | ✓ but **private**, 0 workflows | — | — |
| nuget.org account + Trusted Publishing policy | first publish | ✗ (user action) | — | Long-lived API key secret (discouraged) |
| `NUGET_USER` repo secret | Trusted Publishing login step | ✗ (user action) | — | — |
| LICENSE at repo root | package license file | ✓ | — | — |
| `assets/icon.png` | PackageIcon | ✗ | — | Ship without icon (not fatal, but a release-readiness item) |
| `docs/` directory | OPS-01 guides | ✗ | — | — |
| Phone (a real WhatsApp account) | end-of-phase human UAT for reach-first-send | ✗ (human, outside CI) | — | Document honestly; mark UAT pending |

**Missing dependencies with no fallback:**
- nuget.org account + Trusted Publishing policy + `NUGET_USER` secret — first publish is impossible without the user performing this; the phase plan must include an explicit `checkpoint:human-verify`/user-action task.
- Repo visibility decision — gates environment-based approval and the open-source goal.

**Missing dependencies with fallback:**
- Probe runner acceptance: self-hosted runner fallback if hosted IPs are blocked.
- Coverage (optional): skip, or `coverlet.MTP`.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 4.0.1 on Microsoft.Testing.Platform (runner pinned in `global.json` `"test": { "runner": "Microsoft.Testing.Platform" }`) |
| Config file | `global.json` (SDK + runner); no pytest/jest-style config; fixtures as `TestData/*.json` copied to output |
| Quick run command | `dotnet test Iwasug.WhatsApp.Sdk.sln --filter FullyQualifiedName~Isolation` |
| Full suite command | `dotnet test Iwasug.WhatsApp.Sdk.sln` (observed runtime ~1m24s; **never** add `--nologo`) |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| OPS-01 | nupkg contains README + LICENSE + icon; no sample package; snupkg siblings present; version = `0.1.<height>` | structural (pack-verify script) | `dotnet pack ... -o artifacts; pwsh scripts/verify-pack.ps1 artifacts` | ❌ Wave 0 (`scripts/verify-pack.ps1`) |
| OPS-01 | Reach first send following docs alone | manual UAT (needs phone) | end-of-phase checklist; mirrors 03–07 UAT pattern; CONN-01..03 stay NOT VERIFIED until then | ❌ Wave 0 (checklist doc) |
| OPS-01 | Public API frozen; docs link integrity (absolute URLs) | build gate + review | `dotnet build Iwasug.WhatsApp.Sdk.sln --nologo` (RS0016/RS0017 with warnings-as-errors) | ✅ analyzer wired; freeze files pending |
| OPS-02 | Unit + golden-fixture suite green on every change | integration (CI) | `dotnet test Iwasug.WhatsApp.Sdk.sln` (709 tests; the 4 failures must be resolved first) | ✅ suite exists; ❌ 4 failures |
| OPS-02 | Isolation suite runs on every change | integration (CI) | `dotnet test ... --filter FullyQualifiedName~Isolation` (8/8 green per 07-VERIFICATION) | ✅ exists |
| OPS-02 | Drift probe fails loudly on 405; distinguishes infra | integration (live) + unit (classifier) | `dotnet run --project tools/.../InteropProbe` (exit 10 drift / 20 infra / 0 ok); unit-test the status→exit mapping with a scripted peer | ❌ Wave 0 (probe project + classifier unit tests) |

### Sampling Rate
- **Per task commit:** `dotnet build Iwasug.WhatsApp.Sdk.sln --nologo` (fast) and, for test-touching tasks, the full suite.
- **Per wave merge:** full suite + isolation filter.
- **Phase gate:** full suite green + isolation green + a dispatched live-probe run producing a classified result + pack-verify green before `/gsd-verify-work`.

### Wave 0 Gaps
- [ ] Fix or quarantine the 4 `PairingLifecycleTests` failures (exact names in Pitfall 2) — blocks every CI gate.
- [ ] `tools/Iwasug.WhatsApp.Sdk.InteropProbe/` — console probe + status→exit classifier unit tests (scripted peer, no network).
- [ ] `scripts/verify-pack.ps1` — package content assertions (2 nupkgs + snupkgs, no sample, README/LICENSE/icon).
- [ ] `.github/workflows/ci.yml`, `release.yml`, `live-interop.yml`.
- [ ] `assets/icon.png`, `docs/store-implementation.md`, `docs/webhook-recipe.md`, `docs/risk-ledger.md`, README rewrite, UAT checklist.
- [ ] `version.json` tag refspec decision + edit (if tag-triggered).
- [ ] User actions (document as checkpoints): nuget.org account + Trusted Publishing policy; `NUGET_USER` secret; repo visibility.

## Security Domain

### Applicable ASVS Categories (L1)
| ASVS Category | Applies | Standard Control |
|---------------|---------|------------------|
| V2 Authentication | yes (release auth) | NuGet Trusted Publishing OIDC; no stored push credentials; `NuGet/login@v1` |
| V3 Session Management | no | n/a (no new sessions; WhatsApp session machinery shipped earlier) |
| V4 Access Control | yes | Least-privilege workflow `permissions` (`contents: read` default; `id-token: write` only on the publish job); environment gate on publish; publish never on `pull_request` |
| V5 Input Validation | no (new) | No new input surfaces; probe parses only server frames via existing SDK paths |
| V6 Cryptography | no new crypto | Do not hand-roll signing/token exchange; OIDC handled by first-party action; snupkg needs no signing |
| V14 Configuration | yes | Pinned action tags, explicit `cache-dependency-path`, `ContinuousIntegrationBuild=true`, package content verification before push |

### Known Threat Patterns for this release stack
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Credential theft (long-lived NuGet key) | Information Disclosure / Spoofing | OIDC exchange only; key lifetime ≤1h; no `NUGET_API_KEY` secret exists |
| Malicious workflow from fork/PR triggering publish | Elevation of Privilege | Publish triggers limited to tags/dispatch on trusted refs; `permissions` scoped; environment approval |
| Supply-chain via mutable action refs | Tampering | Pin to verified tags (v7.0.1 / v6.0.0 / v7.0.2 / v1); SHA pinning optional hardening |
| Dependency/cache poisoning | Tampering | Deterministic `cache-dependency-path`; central package pins (existing `Directory.Packages.props`) |
| Artifact tampering between build and push | Tampering | Build → verify → approve → push within one job graph; `ContinuousIntegrationBuild=true`; verify-pack script |
| Accidental publish of unintended package | (Integrity) | Sample un-packable + verify-pack asserts exactly the expected package set; `--skip-duplicate` |
| Version/naming confusion (typosquat ID claim) | Spoofing | First publish claims both IDs; owner unique on nuget.org; docs state official ID |
| Self-hosted runner (if used for probe) | Elevation of Privilege | Never run publish on self-hosted; probe job stays `contents: read` without secrets [CITED: GitHub docs — env secrets on self-hosted not container-isolated] |
| Docs overpromising safety (ban-safety) | Repudiation (trust) | Keep the honest risk ledger incl. cannot-mitigate list and RegistrationDefaults limits; CONN UAT honesty |

## Sources

### Primary (verified this session, 2026-10-17)
- In-repo reads (verbatim quotes in this document): `version.json`, `Directory.Build.props/.targets`, `Directory.Packages.props`, `src/Iwasug.WhatsApp.Sdk/Iwasug.WhatsApp.Sdk.csproj`, `src/Iwasug.WhatsApp.Sdk.Testing/*.csproj`, `tests/.../*.csproj`, sample csproj + `Program.cs`, `PublicAPI.*.txt` (read + counted), `ReconnectPolicy.cs`, `WaVersionResolver.cs`, `DefaultAccountRuntimeFactory.cs`, `fulltest.log`, `07-VERIFICATION.md`, `README.md`, `NOTICE.md`, `STATE.md`, `REQUIREMENTS.md`, `ROADMAP.md`, `.planning/config.json`.
- Fresh `dotnet pack` (this session): three nupkgs incl. the sample defect; SDK nupkg 481,735 B; version `0.1.341`.
- `gh api` (this session): repo private/master/MIT; `actions/*` workflow count = 0; action latest tags (checkout v7.0.1, setup-dotnet v6.0.0, upload-artifact v7.0.2); `NuGet/login` repo exists.
- `api.nuget.org` flatcontainer (this session): both project IDs → 404 (unclaimed).
- `azuresearch-usnc.nuget.org` (this session): `coverlet.MTP` 10.1.0, 1,030,291 downloads.
- learn.microsoft.com/nuget/nuget-org/trusted-publishing (fetched this session; quotes in text).
- docs.github.com/en/actions/reference/deployments-and-environments (fetched this session; quotes in text).

### Secondary (official docs, prior research in this phase's session)
- Nerdbank.GitVersioning docs (fetch-depth requirement, publicReleaseRefSpec semantics) — MEDIUM.
- PublicApiAnalyzers rule docs RS0016/RS0017/RS0041 — MEDIUM.
- SourceLink guidance + snupkg docs (bundled in .NET 8+ SDK; sibling snupkg push) — MEDIUM.
- coverlet MTP integration documentation (coverlet.collector inert under MTP) — MEDIUM.
- actions/setup-dotnet README (cache-dependency-path requirement) — MEDIUM.

### Tertiary
- Datacenter-IP behavior of WhatsApp Web toward GitHub-hosted runners — unverifiable here; risk, not fact.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new packages; all pins read from the repo this session; action tags and IDs re-verified live.
- Packaging/release mechanics: HIGH for in-repo facts (pack output reproduced); HIGH for nuget.org/GitHub doc behaviors (fetched); MEDIUM for click-through UX of the nuget.org policy UI.
- Architecture (CI/probe): HIGH for command/filter correctness (empirical evidence in repo); MEDIUM for hosted-runner acceptance (A2).
- Pitfalls: HIGH — each one is either reproduced this session (sample pack, failing tests, stale README) or quoted from first-party docs.

**Research date:** 2026-10-17
**Valid until:** ~2026-11-16 (30 days) for repo-local facts; re-check action tags and the Trusted Publishing page right before implementing `release.yml` (fast-moving surface).
