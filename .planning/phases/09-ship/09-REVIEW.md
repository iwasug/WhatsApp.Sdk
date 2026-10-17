---
phase: 09-ship
reviewed: 2026-10-17T00:00:00Z
depth: standard
files_reviewed: 24
files_reviewed_list:
  - .github/workflows/ci.yml
  - .github/workflows/live-interop.yml
  - .github/workflows/release.yml
  - Directory.Build.props
  - Directory.Build.targets
  - NOTICE.md
  - README.md
  - assets/icon.png
  - docs/risk-ledger.md
  - docs/store-implementation.md
  - docs/webhook-recipe.md
  - samples/Iwasug.WhatsApp.Sdk.PairingSample/Iwasug.WhatsApp.Sdk.PairingSample.csproj
  - scripts/verify-pack.ps1
  - src/Iwasug.WhatsApp.Sdk.Testing/PublicAPI.Shipped.txt
  - src/Iwasug.WhatsApp.Sdk.Testing/PublicAPI.Unshipped.txt
  - src/Iwasug.WhatsApp.Sdk/Pairing/PairingCoordinator.cs
  - src/Iwasug.WhatsApp.Sdk/PublicAPI.Shipped.txt
  - src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt
  - tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairingLifecycleTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/README.md
  - tests/Iwasug.WhatsApp.Sdk.Tests/Tools/InteropProbeTests.cs
  - tools/Iwasug.WhatsApp.Sdk.InteropProbe/ProbeOptions.cs
  - tools/Iwasug.WhatsApp.Sdk.InteropProbe/Program.cs
  - version.json
findings:
  critical: 1
  warning: 2
  info: 2
  total: 5
status: issues_found
---

# Phase 9: Code Review Report

**Reviewed:** 2026-10-17
**Depth:** standard
**Files Reviewed:** 24
**Status:** issues_found

## Summary

Reviewed the Phase 9 release-engineering surface: CI/test/release workflows, build
props/targets, packaging gate, public-API freeze files, provenance/docs, interop
probe CLI, and the pairing coordinator doc change. The implementation is largely
coherent, but the host-facing documentation misnames a shipped contract-test
class (a compile-breaking error for adopters), and there are two input/validation
and workflow-reporting quality issues worth fixing before ship.

## Critical Issues

### CR-01: Host docs reference a non-existent contract-test class name

**File:** `docs/store-implementation.md:106`, `tests/Iwasug.WhatsApp.Sdk.Tests/README.md:45`
**Issue:** Both documents tell host authors to subclass `IRecipientContactStoreContractTests`,
claiming "the I-prefixed name is the shipped one." The shipped public API
(`src/Iwasug.WhatsApp.Sdk.Testing/PublicAPI.Shipped.txt`) and the source define
the class as `RecipientContactStoreContractTests` (no `I` prefix); only the file
name carries the `I`. A host following the documented name will fail to compile
against the published `Iwasug.WhatsApp.Sdk.Testing` package.
**Fix:** Replace `IRecipientContactStoreContractTests` with
`RecipientContactStoreContractTests` in both documents and drop the parenthetical
claim about the I-prefixed name.

## Warnings

### WR-01: README status line contradicts its own media-progress statement

**File:** `README.md:28`
**Issue:** The status header says "Capabilities shipped so far (phases 1–8; Phase 6
media is in progress at 4/6 plans)", while the bullet list immediately below
marks "Media (Phase 6, in progress — 4/6 plans)". The parenthetical reads as
though Phase 6 is both shipped and in progress, which is confusing for
release-note consumers.
**Fix:** Reword to "Capabilities shipped so far (phases 1–5, 7–8; Phase 6 media
in progress at 4/6 plans):" or move the media caveat into the bullet only.

### WR-02: Probe option parser accepts whitespace and signs in numeric values

**File:** `tools/Iwasug.WhatsApp.Sdk.InteropProbe/ProbeOptions.cs:67`
**Issue:** `int.TryParse(raw, NumberStyles.Integer, ...)` uses `NumberStyles.Integer`,
which permits leading/trailing whitespace and a leading `+`/`-`. Values such as
`" 120 "` or `"+120"` are accepted even though the documented contract is a
plain whole number, and a leading `-` only fails later via the range check
rather than as a parse failure. This weakens the strict argument contract the
workflow relies on for exit code 2.
**Fix:** Parse with `NumberStyles.None` (or validate `raw` matches `^[0-9]+$`
first) so only canonical integer tokens are accepted.

## Info

### IN-01: CI test-results upload silently ignores a missing artifact directory

**File:** `.github/workflows/ci.yml:52`
**Issue:** `if-no-files-found: ignore` means a run with zero `TestResults/**`
files (e.g. a test host crash before emitting results) uploads nothing and the
job can still be marked failed only by the test step; the diagnostic artifact
is lost. For a release-gate workflow this hides the evidence needed to triage
failures.
**Fix:** Change to `if-no-files-found: error` (or `warn`) so a missing results
directory is surfaced instead of swallowed.

### IN-02: Live-interop treats an infra (exit 20) outcome as a hard failure

**File:** `.github/workflows/live-interop.yml:56`
**Issue:** Exit code 20 is documented as an infra/transport outcome that should
never be read as protocol drift, yet the workflow runs `exit 1` for it. This
makes every datacenter-IP throttling event page as a red build, masking real
drift (exit 10) signals.
**Fix:** Consider exiting 0 (or a separate non-failing status) for exit code 20
while keeping exit 1 reserved for the drift (10) and contract-error (default)
cases; alternatively document that infra failures are intentionally alerting.

---

_Reviewed: 2026-10-17_
_Reviewer: the agent (gsd-code-reviewer)_
_Depth: standard_