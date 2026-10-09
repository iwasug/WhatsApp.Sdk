---
phase: 01-foundation-store-seam
fixed_at: 2026-10-08T17:00:00Z
review_path: C:/Users/iwasu/iwasug/sdk-wa/.planning/phases/01-foundation-store-seam/01-REVIEW.md
iteration: 2
findings_in_scope: 2
fixed: 2
skipped: 0
status: all_fixed
---

# Phase 01: Code Review Fix Report

**Fixed at:** 2026-10-08T17:00:00Z
**Source review:** C:/Users/iwasu/iwasug/sdk-wa/.planning/phases/01-foundation-store-seam/01-REVIEW.md
**Iteration:** 2

**Summary:**
- Findings in scope: 2
- Fixed: 2
- Skipped: 0

## Fixed Issues

### WR-01: StoreRegistry disposed-guard still has a check-then-act window

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs`
**Commit:** c7d4bdb
**Applied fix:** Introduced `_lifecycleGate` lock. `CreateSessionAsync` now performs the disposed-check and `_sessions.GetOrAdd` registration inside one critical section; `DisposeAsync` sets `_disposed` and snapshots `_sessions` under the same gate, then drains the snapshot (single pass — the two-pass drain is no longer needed once registration is gated). A preempted registration now either lands before the snapshot (and is drained) or throws `ObjectDisposedException`. Fixed — logic change, requires human verification of the race semantics.

### WR-02: Iteration-1 fixes WR-01 and WR-03 landed with no regression tests

**Files modified:** `tests/Iwasug.WhatsApp.Sdk.Tests/Stores/DurableWriteRetryPolicyTests.cs`, `tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreRegistryTests.cs`
**Commit:** dc8ef3f
**Applied fix:** Added three retry-validation tests (`MaxAttempts = 0`, `BaseDelayMs` negative, `MaxDelayMs < BaseDelayMs` → `ArgumentOutOfRangeException`) and one disposed-registry test (`CreateSessionAsync` after `DisposeAsync` → `ObjectDisposedException`).

## Verification

- `dotnet build Iwasug.WhatsApp.Sdk.sln` → 0 warnings / 0 errors (run inside the isolated worktree `.claude/worktrees/rf-01-23640-*`).
- `dotnet test Iwasug.WhatsApp.Sdk.sln` → **167/167 pass** (163 prior + 4 new), exit 0, run inside the isolated worktree.

---

_Fixed: 2026-10-08T17:00:00Z_
_Fixer: the agent (gsd-code-fixer)_
_Iteration: 2_
