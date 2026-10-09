---
phase: 01-foundation-store-seam
reviewed: 2026-10-08T10:35:00Z
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
  warning: 2
  info: 10
  total: 12
status: issues_found
---

# Phase 1: Code Review Report — Iteration 2 (re-review after WR-01/02/03 fixes)

**Reviewed:** 2026-10-08T10:35:00Z
**Depth:** standard
**Files Reviewed:** 61
**Status:** issues_found

## Summary

Re-reviewed all 61 phase-1 files at standard depth after iteration-1 fixes. Verified
the fixes landed and their mechanics:

- **WR-01 (RetryOptions validation):** `DurableWriteRetryPolicy.cs:71` now runs
  `ValidateOptions` on the primary-constructor field initializer, so `MaxAttempts < 1`,
  `BaseDelayMs < 0`, and `MaxDelayMs < BaseDelayMs` throw `ArgumentOutOfRangeException`
  before any store I/O. Fail-fast shape is correct (field initializers run in
  declaration order; the throw surfaces from the constructor).
- **WR-02 (byte[] aliasing):** `InMemoryStoreFactory.cs:141-153` clones on both the
  read and write boundary, and `ScratchStoreFactory.cs:89-101` mirrors it. The new
  contract fact `Mutating_record_bytes_after_Put_does_not_change_subsequent_Get`
  (`SignalSessionStoreContractTests.cs:54`) pins both directions and is registered in
  `PublicAPI.Unshipped.txt`. `StoreRegistryTests` assertions switched to content
  equality (`BeEquivalentTo`).
- **WR-03 (disposed-state guard):** `StoreRegistry.cs:79, 114-115, 172` adds the
  `_disposed` flag; `DisposeAsync` sets it first and drains `_sessions` twice. See
  WR-01 below for the residual race this does **not** close.

Empirical gates on the pinned toolchain (SDK 10.0.401 / runtime 10.0.12):
`dotnet build` → 0 warnings / 0 errors; full suite → **163/163 pass**, exit 0.

No security findings: no secrets, no injection surfaces, message discipline intact.

## Warnings

### WR-01: StoreRegistry disposed-guard still has a check-then-act window — a preempted CreateSessionAsync can register a live session after DisposeAsync completes

**File:** `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs:110-143, 166-254`
**Issue:** The WR-03 fix narrows but does not close the mid-disposal leak. The
sequence is reachable:

1. Thread A enters `CreateSessionAsync`, passes `Volatile.Read(ref _disposed)` at
   line 114 while the flag is still 0, and is preempted before line 122's
   `_sessions.GetOrAdd`.
2. Thread B runs `DisposeAsync` to completion: sets the flag, drains `_sessions`
   twice (both passes find nothing — A hasn't inserted yet), disposes the fallback
   factory.
3. Thread A resumes, inserts a new `Lazy` into `_sessions`, and its creation runs.
   The in-memory fallback's `DisposeAsync` only clears state rather than
   invalidating it (`InMemoryStoreFactory.cs:115-119`), so the new session resolves
   successfully — post-dispose, untracked, and never disposed. Exactly the
   "silently serves fresh sessions after disposal" outcome the fix's comment at
   lines 76-78 claims is prevented.

The two-pass drain only catches insertions that land *between* the passes; it
cannot catch an insertion that lands after the second pass. Note also that
`CreateSessionAsync`'s disposed-check and registration are not atomic, so there is
no ordering-based guarantee.

**Fix:** make registration and the shutdown drain mutually exclusive. Either hold a
lock across "check flag → GetOrAdd → return lazy handle" in `CreateSessionAsync`
and across "set flag → drain" in `DisposeAsync`, or use a second flag check after
the `GetOrAdd` plus a disposal loop that re-drains until an enumeration finds
nothing (a quiescence loop). The lock is simplest and the contention profile is a
once-per-account call path:

```csharp
private readonly object _lifecycleGate = new();

public ValueTask<StoreSession> CreateSessionAsync(AccountId accountId, CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    ThrowIfDefaultAccountId(accountId);
    lock (_lifecycleGate)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(StoreRegistry));
        // ... existing GetOrAdd / lazy handling
    }
    // ... existing await/evict logic outside the gate
}

public async ValueTask DisposeAsync()
{
    lock (_lifecycleGate)
    {
        Volatile.Write(ref _disposed, 1);
        // snapshot _sessions under the same gate so no new entry can be added
    }
    // ... drain the snapshot freely, then dispose the fallback
    // (no second drain pass needed once registration is gated)
}
```

### WR-02: Iteration-1 fixes WR-01 and WR-03 landed with no regression tests — only WR-02 added a pinning fact

**Files:** `tests/Iwasug.WhatsApp.Sdk.Tests/Stores/DurableWriteRetryPolicyTests.cs` (no validation tests), `tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreRegistryTests.cs` (no disposed-guard test)
**Issue:** WR-01's `ValidateOptions` gate (`DurableWriteRetryPolicy.cs:121-131`) and
WR-03's `_disposed` flag + drain (`StoreRegistry.cs:79, 114-115, 172`) are now
public-facing behavioral guarantees, but no test asserts them. `git grep
ArgumentOutOfRangeException` and `git grep ObjectDisposedException` over `tests/`
return nothing. A later refactor that drops the validation or the flag would keep
the suite green, silently reverting the fixes. WR-02's fix, by contrast, added the
`Mutating_record_bytes_after_Put_...` contract fact — the same discipline should
apply to the other two.
**Fix:** add two small tests — `new DurableWriteRetryPolicy(new RetryOptions { MaxAttempts = 0 })`
throws `ArgumentOutOfRangeException` (and one case for inverted delays), and
`await registry.DisposeAsync(); await registry.CreateSessionAsync(...)` throws
`ObjectDisposedException`.

## Info

The iteration-1 info items remain unaddressed (disposition ledger still `open`);
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
comparing for value equality get identity semantics. New WR-02 cloning makes this
*more* visible (two `GetAsync` calls for the same record are now never reference-
equal). **Fix:** document, or implement content equality.

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

**File:** `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs:117-135`
**Issue:** correctness of the `lazy` self-capture is lexical; a routine refactor
could produce two sessions per account under a race. **Fix:** re-read
`_sessions[accountId]` inside the inner factory, or add a deterministic race
regression test.

### IN-09: EnsureUnique is check-then-act

**File:** `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs:153-163`
**Issue:** uniqueness enforced non-atomically; documented as Phase 7 scope.
**Fix:** provide an atomic register primitive when the account-adding API lands.

### IN-10: `dotnet test --nologo` reports "Zero tests ran" on pinned SDK

**Files:** `tests/Iwasug.WhatsApp.Sdk.Tests/Iwasug.WhatsApp.Sdk.Tests.csproj:11-16`, `tests/Iwasug.WhatsApp.Sdk.Tests/README.md:39-47`
**Issue:** verified 2026-10-08 on SDK 10.0.401 — a `--nologo` invocation fails
with exit 5 and a swallowed error. README does not document the constraint.
**Fix:** note the constraint in the csproj comment and README; re-test on SDK
servicing updates.

---

_Reviewed: 2026-10-08T10:35:00Z_
_Reviewer: the agent (gsd-code-reviewer)_
_Depth: standard_
