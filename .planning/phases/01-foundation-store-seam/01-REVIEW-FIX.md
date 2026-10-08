---
phase: 01
fixed_at: 2026-10-08T18:40:00Z
review_path: C:/Users/iwasu/iwasug/sdk-wa/.planning/phases/01-foundation-store-seam/01-REVIEW.md
iteration: 3
findings_in_scope: 1
fixed: 1
skipped: 0
status: all_fixed
---

# Phase 1: Code Review Fix Report

**Fixed at:** 2026-10-08T18:40:00Z
**Source review:** C:/Users/iwasu/iwasug/sdk-wa/.planning/phases/01-foundation-store-seam/01-REVIEW.md
**Iteration:** 3

**Summary:**
- Findings in scope: 1
- Fixed: 1
- Skipped: 0

## Fixed Issues

### WR-01: StoreRegistry gate closes the registration window but not the force-creation window — a late `lazy.Value` still mints a live session post-dispose

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs`, `src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs`
**Commit:** 637b98e
**Applied fix:**
- `CreateSessionCoreAsync` now re-checks the `_disposed` flag before resolving any store, so a lazy forced after the shutdown drain faults with `ObjectDisposedException` instead of minting a live session against a torn-down fallback (the load-bearing guard per the review).
- `CreateSessionAsync`'s completed-session paths (sync `IsCompletedSuccessfully` branch via a `DisposeAndThrowAsync` helper, and `AwaitCreationAsync`) now re-check `_disposed` after creation completes: a session that raced the drain is disposed (idempotent) and the caller gets `ObjectDisposedException` rather than an already-disposed `StoreSession` as a success value.
- `InMemoryStoreFactory` belt-and-braces: a `_disposed` flag is set in `DisposeAsync` and checked at the top of all five `CreateAsync` implementations, which now throw `ObjectDisposedException` after factory disposal instead of silently serving fresh state; disposal remark updated to document the new contract.
- The `DisposeAsync` drain keeps skipping not-yet-forced lazies (comment updated) because the `CreateSessionCoreAsync` check now deterministically faults any late force.

**Verification (Tier 2):** `dotnet build` 0 warnings / 0 errors (repo is warnings-as-errors); full suite `dotnet test Iwasug.WhatsApp.Sdk.sln` → **167/167 pass**, exit 0. Gates ran in the isolated review-fix worktree (`C:\Users\iwasu\iwasug\sdk-wa\.claude\worktrees\rf-01-9956-...` on branch `gsd-reviewfix/01-...`); the main checkout holds the same sources after the fast-forward but has not been re-tested in place.
**Status:** fixed: requires human verification (concurrency-semantics change — logic reviewed, deterministic race regression test not added in this iteration).

## Skipped Issues

None.

---

_Fixed: 2026-10-08T18:40:00Z_
_Fixer: the agent (gsd-code-fixer)_
_Iteration: 3_
