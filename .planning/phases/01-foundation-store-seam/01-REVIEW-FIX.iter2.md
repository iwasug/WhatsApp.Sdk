---
phase: 01-foundation-store-seam
fixed_at: 2026-10-08T09:45:23Z
review_path: C:/Users/iwasu/iwasug/sdk-wa/.planning/phases/01-foundation-store-seam/01-REVIEW.md
iteration: 1
findings_in_scope: 3
fixed: 3
skipped: 0
status: all_fixed
---

# Phase 1: Code Review Fix Report

**Fixed at:** 2026-10-08T09:45:23Z
**Source review:** C:/Users/iwasu/iwasug/sdk-wa/.planning/phases/01-foundation-store-seam/01-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 3
- Fixed: 3
- Skipped: 0

## Fixed Issues

### WR-01: RetryOptions is unvalidated — misconfiguration crashes with the wrong exception type and bogus attempt counts

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Stores/Runtime/DurableWriteRetryPolicy.cs`
**Commit:** 74b0c79
**Applied fix:** Added a `ValidateOptions` gate on the primary-constructor field initializer: `MaxAttempts < 1`, `BaseDelayMs < 0`, and `MaxDelayMs < BaseDelayMs` now throw `ArgumentOutOfRangeException(nameof(options), ...)` before any store I/O runs (adapted to a static helper because the class uses a C# primary constructor without a body).

### WR-02: In-memory reference store aliases mutable `byte[] SessionState` across the store seam

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs`, `src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/SignalSessionStoreContractTests.cs`, `src/Iwasug.WhatsApp.Sdk.Testing/PublicAPI.Unshipped.txt`, `tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/ScratchStoreFactory.cs`, `tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreRegistryTests.cs`
**Commit:** f987974
**Applied fix:** `InMemorySignalSessionStore.PutAsync` now stores `session with { SessionState = (byte[])session.SessionState.Clone() }` and `GetAsync` returns a freshly-cloned array, so neither the caller's post-write mutation nor a later mutation of a fetched buffer can rewrite store state. Only `SignalSessionRecord` carries a mutable array across the seam (the other four records are value-shaped), so the copy is applied at that one boundary. Added a kit contract fact `Mutating_record_bytes_after_Put_does_not_change_subsequent_Get` (registered in `PublicAPI.Unshipped.txt`) that pins both directions, and applied the same cloning in `ScratchStoreFactory` so the scratch binding passes the new rule. The three `retrieved.Should().Be(record)` assertions in `StoreRegistryTests` that baked in the aliasing were switched to `BeEquivalentTo` (content equality).

### WR-03: StoreRegistry has no disposed-state guard — post-dispose use silently serves fresh sessions, and sessions created mid-disposal leak

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs`
**Commit:** e0d0113
**Applied fix:** Added `private int _disposed;`. `CreateSessionAsync` now throws `ObjectDisposedException` when the flag is set (checked after the default-account-id validation). `DisposeAsync` sets the flag via `Interlocked.Exchange` first, then runs the existing evict-and-dispose cascade over `_sessions`, then does a second drain pass over `_sessions` to dispose any session a racing `CreateSessionAsync` inserted mid-disposal before observing the flag. Sessions in both passes are disposed exactly as before (fault-tolerant per-session try/catch, failures aggregated).

## Verification

- Tier 2 build gate: `dotnet build Iwasug.WhatsApp.Sdk.sln` → 0 warnings / 0 errors after each fix (PublicAPI analyzer consistent with the updated `PublicAPI.Unshipped.txt`).
- Filtered test runs (xunit v3 MTP dialect): `-method "*SignalSession*"` → 16/16 pass (includes the two new `Mutating_record_bytes_after_Put_does_not_change_subsequent_Get` bindings — InMemory + Scratch); `-method "*StoreRegistry*"` → 10/10 pass.
- Full suite after all fixes: `dotnet exec tests/Iwasug.WhatsApp.Sdk.Tests/bin/Debug/net10.0/Iwasug.WhatsApp.Sdk.Tests.dll` → **163/163 pass** (161 baseline + 2 new contract fact bindings), exit 0.
- Gates ran in the isolated worktree `C:/Users/iwasu/iwasug/sdk-wa/.claude/worktrees/rf-01-14080-20261008163606` (branch `gsd-reviewfix/01-14080`); the worktree has its own NuGet/obj outputs but shares the global package cache, so results reproduce in the main checkout after the fast-forward.

---

_Fixed: 2026-10-08T09:45:23Z_
_Fixer: the agent (gsd-code-fixer)_
_Iteration: 1_
