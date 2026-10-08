---
phase: 01-foundation-store-seam
reviewed: 2026-10-08T18:00:00Z
depth: standard
files_reviewed: 61
files_reviewed_list:
  - Directory.Build.props
  - Directory.Packages.props
  - global.json
  - Iwasug.WhatsApp.Sdk.sln
  - version.json
  - .editorconfig
  - .gitignore
  - LICENSE
  - NOTICE.md
  - README.md
  - src/Iwasug.WhatsApp.Sdk.Testing/ComplianceTestBase.cs
  - src/Iwasug.WhatsApp.Sdk.Testing/FaultInjection/FaultingStoreFactory.cs
  - src/Iwasug.WhatsApp.Sdk.Testing/Iwasug.WhatsApp.Sdk.Testing.csproj
  - src/Iwasug.WhatsApp.Sdk.Testing/PublicAPI.Shipped.txt
  - src/Iwasug.WhatsApp.Sdk.Testing/PublicAPI.Unshipped.txt
  - src/Iwasug.WhatsApp.Sdk.Testing/Rules/StoreContractRules.cs
  - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/IdentityStoreContractTests.cs
  - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/PreKeyStoreContractTests.cs
  - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/RecentMessageCacheStoreContractTests.cs
  - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/SafetyStateStoreContractTests.cs
  - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/SignalSessionStoreContractTests.cs
  - src/Iwasug.WhatsApp.Sdk/Accounts/AccountId.cs
  - src/Iwasug.WhatsApp.Sdk/Domain/Jid.cs
  - src/Iwasug.WhatsApp.Sdk/Domain/PreKeyRecord.cs
  - src/Iwasug.WhatsApp.Sdk/Domain/SafetyState.cs
  - src/Iwasug.WhatsApp.Sdk/Domain/SignalIdentity.cs
  - src/Iwasug.WhatsApp.Sdk/Errors/SdkException.cs
  - src/Iwasug.WhatsApp.Sdk/Errors/StoreErrorSurface.cs
  - src/Iwasug.WhatsApp.Sdk/Iwasug.WhatsApp.Sdk.csproj
  - src/Iwasug.WhatsApp.Sdk/PublicAPI.Shipped.txt
  - src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt
  - src/Iwasug.WhatsApp.Sdk/Stores/IIdentityStore.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/IPreKeyStore.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/IRecentMessageCacheStore.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/ISafetyStateStore.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/ISignalSessionStore.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/Runtime/DurableWriteRetryPolicy.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistryOptions.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/ComplianceTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Core/AccountIdTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Core/JidTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Iwasug.WhatsApp.Sdk.Tests.csproj
  - tests/Iwasug.WhatsApp.Sdk.Tests/README.md
  - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/FaultingBindingTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/HostStyleSecondRunTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/InMemoryIdentityStoreContractTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/InMemoryPreKeyStoreContractTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/InMemoryRecentMessageCacheStoreContractTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/InMemorySafetyStateStoreContractTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/InMemorySignalSessionStoreContractTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/ScratchIdentityStoreContractTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/ScratchPreKeyStoreContractTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/ScratchRecentMessageCacheStoreContractTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/ScratchSafetyStateStoreContractTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/ScratchSignalSessionStoreContractTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/ScratchStoreFactory.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Stores/DurableWriteRetryPolicyTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreErrorSurfaceTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreRegistryTests.cs
findings:
  critical: 0
  warning: 1
  info: 10
  total: 11
status: issues_found
---

# Phase 1: Code Review Report — Iteration 3 (re-review after WR-01/WR-02 fixes)

**Reviewed:** 2026-10-08T18:00:00Z
**Depth:** standard
**Files Reviewed:** 61
**Status:** issues_found

## Summary

Iteration-3 verification of the two fixes from `01-REVIEW.md` (iteration 2):

- **WR-02 (regression tests) — verified landed.** Three retry-validation facts
  (`Constructor_throws_ArgumentOutOfRangeException_when_MaxAttempts_is_zero`,
  `..._when_MaxDelay_is_below_BaseDelay`, `..._when_BaseDelay_is_negative`) at
  `tests/Iwasug.WhatsApp.Sdk.Tests/Stores/DurableWriteRetryPolicyTests.cs:113-135`, and
  `CreateSessionAsync_throws_ObjectDisposedException_after_registry_disposal` at
  `tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreRegistryTests.cs:194-204`. Empirical gate
  on the pinned toolchain (SDK 10.0.401): `dotnet test` → **167/167 pass**, exit 0.
  (Note: a `--no-build` run against the stale `bin/Debug` payload reports 163 — rebuild
  before trusting a count; IN-10's stale-binary constraint is worth noting in the test
  README alongside the `--nologo` note.)
- **WR-01 (lifecycle gate) — fix landed but a narrower variant of the same race survives.**
  `_lifecycleGate` correctly serializes "check flag → GetOrAdd" against "set flag →
  snapshot" (`StoreRegistry.cs:126-139, 189-193`), and the original preemption point
  (before `_sessions.GetOrAdd`) is closed. However, a preemption *between* gate release
  (line 139) and the `lazy.Value` force (line 144) still leaks a live session, because
  `DisposeAsync`'s drain skips not-yet-forced entries (`!kvp.Value.IsValueCreated` →
  `continue`, lines 202-205). Details in WR-01 below.

No new security findings: no secrets, no injection surfaces, message discipline intact.

## Narrative Findings (AI reviewer)

### Critical Issues

None.

### Warnings

### WR-01: StoreRegistry gate closes the registration window but not the force-creation window — a late `lazy.Value` still mints a live session post-dispose

**File:** `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs:136-157, 199-226`
**Issue:**
The gate fix claims (comment at lines 121-124) that "either the lazy lands before the
shutdown snapshot (and is drained), or the flag is already set and this throws
(WR-01)." That covers the preemption point *before* `_sessions.GetOrAdd`, but not the
window *after* the gate releases:

1. Thread A enters the gate, `_disposed` is 0, registers lazy `L` at line 136, exits
   the gate, and is preempted before line 144's `creation = lazy.Value`.
2. Thread B runs `DisposeAsync`: sets `_disposed=1`, snapshots (contains `L`,
   `IsValueCreated == false`), drains — line 202's `if (!kvp.Value.IsValueCreated)
   continue;` skips `L` entirely (no dispose, no await), clears the fallback
   factory's state (line 233 → `InMemoryStoreFactory.cs:117`), completes.
3. Thread A resumes and forces `lazy.Value`, which runs `CreateSessionCoreAsync`
   against the fallback. The fallback's `CreateAsync` has no post-dispose failure — it
   serves fresh empty state — so creation **succeeds**: a live `StoreSession` is
   returned to A's caller, never tracked in `_sessions` (evicted at line 201), and
   never disposed. Exactly the "silently serves fresh sessions after disposal" outcome
   the WR-01 comment claims is prevented.

The in-flight variant is less bad but still surprising: if A forced `lazy.Value`
(Task constructed, `IsValueCreated` true) before B's snapshot, B awaits the task
(lines 208-211) and disposes the session — and A's own `await` then returns that same
already-disposed `StoreSession` to its caller as a success value, with no
`ObjectDisposedException`.

**Fix:** close the window inside the creation path, not just at registration. Cheapest
correct shape: make `CreateSessionCoreAsync` honor the flag before resolving any
store, and make the drain force-or-fault not-yet-created entries instead of skipping
them:

```csharp
private async Task<StoreSession> CreateSessionCoreAsync(...)
{
    if (Volatile.Read(ref _disposed) != 0)
        throw new ObjectDisposedException(nameof(StoreRegistry));
    // ... existing resolution
}

// DisposeAsync drain, replacing the IsValueCreated skip:
foreach (var kvp in snapshot)
{
    Evict(kvp.Key, kvp.Value);
    if (!kvp.Value.IsValueCreated)
    {
        // Mark the lazy abandoned so a late force faults instead of creating:
        try { _ = kvp.Value.Value; } catch { /* creation failed; nothing to dispose */ }
        // ... or simply: rely on the CreateSessionCoreAsync disposed-check above to
        // fault the late force, and let the caller observe it.
        continue;
    }
    // ... existing await-and-dispose
}
```

With the `CreateSessionCoreAsync` check alone, step 3 above faults the late creation
and the caller gets `ObjectDisposedException` wrapped by the existing evict-and-rethrow
path (lines 146-150 / 403-409) — no live session can escape. Belt-and-braces: also
have `InMemoryStoreFactory.DisposeAsync` set an invalidated flag so any later
`CreateAsync` throws `ObjectDisposedException` (host factories are outside the SDK's
control, so the registry-side check is the load-bearing one).

### Info

The iteration-2 info items remain unaddressed (disposition ledger still `open`);
carried forward verbatim — re-listed compactly:

### IN-01: Unnecessary `partial` modifier on AccountId

**File:** `src/Iwasug.WhatsApp.Sdk/Accounts/AccountId.cs:27`
**Issue:** `public readonly partial record struct AccountId` is the only `partial`
declaration in the repo; no second part or generator exists. **Fix:** drop `partial`.

### IN-02: Per-account cache gate never disposed on factory teardown

**File:** `src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs:261, 115-119`
**Issue:** `RecentMessageCacheState.Gate` (`SemaphoreSlim`) is never disposed;
factory disposal clears state under live views. Harmless for `SemaphoreSlim(1,1)`
today, but asymmetric. **Fix:** dispose gates or document the deliberate choice.

### IN-03: global.json does not pin the SDK version

**File:** `global.json:1-5`
**Issue:** only `test.runner` is set; SDK version floats. **Fix:** add
`"sdk": { "version": "10.0.401", "rollForward": "latestPatch" }`.

### IN-04: .editorconfig defects (trailing commas, mislabeled rule comment)

**File:** `.editorconfig:141-142, 388-389, 56-70`
**Issue:** `= false,` values (inert), S1118 rule configured under an S2094
comment, duplicated blocks. **Fix:** remove commas, relabel comment, merge
duplicates.

### IN-05: SignalSessionRecord equality over byte[] is reference-based

**File:** `src/Iwasug.WhatsApp.Sdk/Stores/ISignalSessionStore.cs:93`
**Issue:** record equality on `SessionState` is reference equality; consumers
comparing for value equality get identity semantics. **Fix:** document, or implement
content equality.

### IN-06: ComplianceTestBase completeness backstop is vacuous under trimming

**File:** `src/Iwasug.WhatsApp.Sdk.Testing/ComplianceTestBase.cs:32-43, 62-74`
**Issue:** `Assembly`/`AppDomain` scanning can silently return empty in a trimmed
host, passing `Every_contract_class_has_a_concrete_subclass` vacuously.
**Fix:** add a floor count assertion (≥ 5 base classes).

### IN-07: NuGet packaging gaps — XML docs won't ship

**File:** `src/Iwasug.WhatsApp.Sdk/Iwasug.WhatsApp.Sdk.csproj:8-15`
**Issue:** `GenerateDocumentationFile` unset, no `RepositoryUrl`/`PackageIcon`/
`PackageTags`/`PackageReadmeFile`. **Fix:** set the flag in `Directory.Build.props`
for packable projects, plus repository metadata.

### IN-08: Once-per-account guarantee rests on closure-over-variable pattern

**File:** `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs:131-138`
**Issue:** correctness of the `lazy` self-capture is lexical; a routine refactor
could produce two sessions per account under a race. **Fix:** re-read
`_sessions[accountId]` inside the inner factory, or add a deterministic race
regression test.

### IN-09: EnsureUnique is check-then-act

**File:** `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs:168-178`
**Issue:** uniqueness enforced non-atomically; documented as Phase 7 scope.
**Fix:** provide an atomic register primitive when the account-adding API lands.

### IN-10: `dotnet test --nologo` reports "Zero tests ran" on pinned SDK

**Files:** `tests/Iwasug.WhatsApp.Sdk.Tests/Iwasug.WhatsApp.Sdk.Tests.csproj:11-16`, `tests/Iwasug.WhatsApp.Sdk.Tests/README.md:39-47`
**Issue:** verified 2026-10-08 on SDK 10.0.401 — a `--nologo` invocation fails
with exit 5 and a swallowed error. README does not document the constraint.
**Fix:** note the constraint in the csproj comment and README; re-test on SDK
servicing updates.

---

_Reviewed: 2026-10-08T18:00:00Z_
_Reviewer: the agent (gsd-code-reviewer)_
_Depth: standard_
