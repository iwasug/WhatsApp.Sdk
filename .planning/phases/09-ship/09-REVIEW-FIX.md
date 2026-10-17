---
phase: 09-ship
fixed_at: 2026-10-17T06:13:50.582Z
review_path: .planning/phases/09-ship/09-REVIEW.md
iteration: 1
findings_in_scope: 5
fixed: 5
skipped: 0
status: all_fixed
---

# Phase 9: Code Review Fix Report

**Fixed at:** 2026-10-17T06:13:50.582Z
**Source review:** .planning/phases/09-ship/09-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 5
- Fixed: 5
- Skipped: 0

## Fixed Issues

### CR-01: Host docs reference a non-existent contract-test class name

**Files modified:** `docs/store-implementation.md`, `tests/Iwasug.WhatsApp.Sdk.Tests/README.md`
**Commit:** 64c000b
**Applied fix:** Replaced `IRecipientContactStoreContractTests` with `RecipientContactStoreContractTests` and dropped the parenthetical claim about the I-prefixed name.

### WR-01: README status line contradicts its own media-progress statement

**Files modified:** `README.md`
**Commit:** c4dcc79
**Applied fix:** Reworded status header to "phases 1-5, 7-8; Phase 6 media in progress at 4/6 plans".

### WR-02: Probe option parser accepts whitespace and signs in numeric values

**Files modified:** `tools/Iwasug.WhatsApp.Sdk.InteropProbe/ProbeOptions.cs`
**Commit:** 13126f6
**Applied fix:** Changed `int.TryParse` from `NumberStyles.Integer` to `NumberStyles.None`.

### IN-01: CI test-results upload silently ignores a missing artifact directory

**Files modified:** `.github/workflows/ci.yml`
**Commit:** 617bc9c
**Applied fix:** Changed `if-no-files-found: ignore` to `if-no-files-found: error`.

### IN-02: Live-interop treats an infra (exit 20) outcome as a hard failure

**Files modified:** `.github/workflows/live-interop.yml`
**Commit:** 1e7ea64
**Applied fix:** Changed exit code 20 branch to emit a warning and `exit 0`.

## Skipped Issues

None — all findings were skipped section omitted.

---

_Fixed: 2026-10-17T06:13:50.582Z_
_Fixer: the agent (gsd-code-fixer)_
_Iteration: 1_