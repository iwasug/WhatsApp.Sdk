---
phase: 01-foundation-store-seam
reviewed: 2026-10-08T07:58:10Z
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
  warning: 3
  info: 10
  total: 13
status: issues_found
---

# Phase 1: Code Review Report

**Reviewed:** 2026-10-08T07:58:10Z
**Depth:** standard
**Files Reviewed:** 61
**Status:** issues_found

## Summary

Reviewed all 61 phase-1 files at standard depth: build configuration, the five store
seams, the in-memory reference implementation, `StoreRegistry`/`StoreSession` composition,
the retry policy, the published Testing kit (contract classes, rule helpers, fault
injection, compliance backstop), and the internal test suite.

Verified empirically on the pinned toolchain (SDK 10.0.401 / runtime 10.0.12):
`dotnet build Iwasug.WhatsApp.Sdk.sln` → 0 warnings / 0 errors (PublicAPI analyzer
consistent with both `PublicAPI.*.txt` files), and the documented `dotnet build` +
`dotnet test` flow → **161/161 tests pass, exit 0**.

Overall assessment: this is a disciplined, well-documented phase. The store-seam design is
internally consistent, message discipline (T-01-01/T-01-12) is enforced and tested, the
failure ≠ missing rule is exercised on both faulting and absent-key paths, and the
`StoreRegistry` race handling is correct (verified by tracing the Lazy/closure interaction
across concurrent callers). No security vulnerabilities found: no secrets, no injection
surfaces, control characters are rejected before account ids/JIDs can reach logs, and
exception messages never embed payloads (covered by dedicated tests).

The three warnings below are correctness/robustness gaps that should be fixed before later
phases build on these seams: unvalidated `RetryOptions` (untyped crash path), mutable
`byte[]` aliasing across the store seam in the reference implementation, and the registry's
missing disposed-state guard. Ten info-level items cover packaging, config hygiene, and
fragile-but-correct patterns.

## Warnings

### WR-01: RetryOptions is unvalidated — misconfiguration crashes with the wrong exception type and bogus attempt counts

**File:** `src/Iwasug.WhatsApp.Sdk/Stores/Runtime/DurableWriteRetryPolicy.cs:17-28, 109-124`
**Issue:** `RetryOptions` is public API with unconstrained `init` properties, and
`DurableWriteRetryPolicy` never validates them. Two concrete failure modes:

1. `BaseDelayMs > MaxDelayMs` (e.g. `{ BaseDelayMs = 500, MaxDelayMs = 100 }`):
   `Math.Clamp(raw * (1.0 + jitter), _options.BaseDelayMs, _options.MaxDelayMs)` at line
   124 throws `ArgumentException` ("min greater than max") — but only *after* the first
   store write already failed. The host observes a raw `ArgumentException` instead of the
   D-21-typed `SdkStoreException`, masking the real store failure the policy exists to
   surface. The crash is also nondeterministic in appearance (only when a retry delay is
   computed, i.e. only when the first attempt fails).
2. `MaxAttempts <= 0` (e.g. `{ MaxAttempts = 0 }`): the loop at line 89 never executes, no
   operation is ever invoked, and line 109 throws
   `SdkStoreException("durable store write failed after 0 attempts.", last: null)` — a
   fabricated failure claiming 0 attempts with no inner cause, even though the operation
   would have succeeded.

**Fix:** validate once in the primary constructor so bad config fails fast, before any
store I/O:

```csharp
public DurableWriteRetryPolicy(RetryOptions? options = null, TimeProvider? timeProvider = null)
{
    _options = options ?? new RetryOptions();
    if (_options.MaxAttempts < 1)
        throw new ArgumentOutOfRangeException(nameof(options), "MaxAttempts must be at least 1.");
    if (_options.BaseDelayMs < 0)
        throw new ArgumentOutOfRangeException(nameof(options), "BaseDelayMs must not be negative.");
    if (_options.MaxDelayMs < _options.BaseDelayMs)
        throw new ArgumentOutOfRangeException(nameof(options), "MaxDelayMs must not be below BaseDelayMs.");
    _timeProvider = timeProvider ?? TimeProvider.System;
}
```

### WR-02: In-memory reference store aliases mutable `byte[] SessionState` across the store seam

**File:** `src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs:136-149, 166-190`
(also `:204-239`; record type at `src/Iwasug.WhatsApp.Sdk/Stores/ISignalSessionStore.cs:83-93`)
**Issue:** `SignalSessionRecord.SessionState` is a mutable `byte[]`, and the in-memory
reference implementation stores and returns the *same instance* on both `PutAsync` and
`GetAsync` — no defensive copy in either direction. Consequences:

- A caller that mutates its `SessionState` buffer *after* `PutAsync` returns silently
  rewrites the persisted record — a subsequent read observes data that was never "written"
  via the API, quietly violating the D-06 store-side durability semantics this store is the
  reference implementation for.
- A caller that mutates the array returned by `GetAsync` corrupts the store for all future
  readers.

This matters because (a) the reference implementation is the behavioral template host
implementations copy, and (b) real backends (EF Core, etc.) will serialize/deserialize and
therefore *not* alias — so observable semantics diverge between the reference store and
production stores, and the contract kit cannot catch it (no rule pins copy semantics).
`tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreRegistryTests.cs:33` even asserts
`retrieved.Should().Be(record)`, which bakes the aliasing in, since record equality over a
`byte[]` field degenerates to reference equality (see IN-05).

Phase 1 has no internal mutator yet, so this is not a live data-corruption bug today — but
Phase 2's protocol loop is exactly the kind of caller that mutates ratchet-state buffers.

**Fix:** copy on the write boundary at minimum (in all five stores' `Put*` for array-bearing
records), and consider copying on `Get*` too:

```csharp
public Task PutAsync(string address, SignalSessionRecord session, CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    records[address] = session with { SessionState = (byte[])session.SessionState.Clone() };
    return Task.CompletedTask;
}
```

Additionally, add a kit contract rule that pins the semantics — e.g. "mutating the bytes
after Put must not change what a subsequent Get observes" — so host implementations are
held to the same behavior (whichever direction is chosen).

### WR-03: StoreRegistry has no disposed-state guard — post-dispose use silently serves fresh sessions, and sessions created mid-disposal leak

**File:** `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs:105-136, 159-210`
**Issue:** `DisposeAsync` sets no disposed flag, and `CreateSessionAsync` never checks for
one. Two related defects:

1. **Use after dispose:** after `DisposeAsync` returns, `CreateSessionAsync` happily
   resolves a brand-new session. Because the fallback factory's `DisposeAsync` only *clears*
   its state (`InMemoryStoreFactory.cs:115-119`) rather than invalidating it, the caller
   gets a fully functional in-memory-backed session against a disposed registry — silent
   misuse instead of the .NET disposal contract (`ObjectDisposedException`).
2. **Mid-disposal creation leak:** `DisposeAsync` enumerates `_sessions` and disposes each
   entry, then disposes the fallback. A `CreateSessionAsync` racing the enumeration inserts
   a new entry after the loop has passed it; that session is never disposed by the shutdown
   cascade (its stores — potentially host-implemented ones holding connections — leak), and
   it continues to be served by the disposed registry.

Phase 1 has no protocol caller, so disposal happens only in tests today — but the registry
is public API and the D-23 cascade contract ("disposes all five when the account's session
is disposed or the registry shuts down") is falsifiable by this race.

**Fix:** set a disposed flag *first* in `DisposeAsync` and honor it in
`CreateSessionAsync`:

```csharp
private int _disposed;

public ValueTask<StoreSession> CreateSessionAsync(AccountId accountId, CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    ThrowIfDefaultAccountId(accountId);
    if (Volatile.Read(ref _disposed) != 0)
        throw new ObjectDisposedException(nameof(StoreRegistry));
    // ... existing GetOrAdd logic
}

public async ValueTask DisposeAsync()
{
    Interlocked.Exchange(ref _disposed, 1);
    // ... existing cascade; optionally re-enumerate once after the loop
    // to drain entries inserted during the snapshot pass.
}
```

## Info

### IN-01: Unnecessary `partial` modifier on AccountId

**File:** `src/Iwasug.WhatsApp.Sdk/Accounts/AccountId.cs:27`
**Issue:** `public readonly partial record struct AccountId` is the only partial
declaration in the repo (verified by grep) — no source generator or second part exists.
**Fix:** drop `partial` (or add a comment naming the future generator that justifies it).

### IN-02: Per-account cache gate (SemaphoreSlim) is never disposed; factory disposal drops state under live views

**File:** `src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs:255, 115-119`
**Issue:** `RecentMessageCacheState.Gate` is `new SemaphoreSlim(1, 1)` — an
`IDisposable` — but neither the store views' `DisposeAsync` nor the factory's
`DisposeAsync` disposes it; the factory just calls `_state.Clear()`. Harmless in practice
(`SemaphoreSlim(1,1)` allocates no wait handle unless `AvailableWaitHandle` is touched), but
it is an asymmetric disposal story for the only disposable object the reference store
creates, and the factory's documented "existing views keep operating on their own
references" semantics means live views continue taking a gate nobody will ever release
them from on teardown.
**Fix:** either dispose gates in `DisposeAsync` (accepting that in-flight views will
fault — arguably the honest teardown), or document why the gate is deliberately left
undisposed.

### IN-03: global.json does not pin the SDK version

**File:** `global.json:1-5`
**Issue:** `global.json` only sets `test.runner` — there is no `sdk.version` block, so
builds float across whatever SDK is installed. The locked-stack decision pins SDK
**10.0.401** (and this machine has it), but a contributor or CI runner with 10.0.5xx gets
different behavior with no signal. This also matters given the `test.runner` semantics in
`global.json` are SDK-version-sensitive.
**Fix:** add the pin with a `rollForward` policy:

```json
{
  "sdk": { "version": "10.0.401", "rollForward": "latestPatch" },
  "test": { "runner": "Microsoft.Testing.Platform" }
}
```

### IN-04: .editorconfig defects — invalid option values, mislabeled comment, duplicated blocks

**File:** `.editorconfig:141-142, 388-389` (plus `56-70` duplicated at `86-133`)
**Issue:** three concrete config defects:
1. Lines 141–142 carry trailing commas — `csharp_new_line_before_members_in_anonymous_types = false,`
   and `csharp_new_line_before_members_in_object_initializers = false,` — making the values
   `false,`, which is not a valid boolean; both options are silently inert.
2. Lines 388–389: the comment says "S2094: Utility classes should not have public
   constructors" but configures `dotnet_diagnostic.S1118.severity = none` — S1118 is the
   correct ID for that rule, so the comment label is wrong (S2094 is separately and
   correctly configured at line 392).
3. Lines 56–70 duplicate blocks that reappear at 93–102/116–117/133 (namespace
   declarations, expression-bodied members, braces, simple using) — redundant surface that
   will drift.
**Fix:** remove the trailing commas, relabel the comment to S1118, and merge the
duplicated sections.

### IN-05: SignalSessionRecord equality over byte[] is reference-based — value-equality trap for consumers

**File:** `src/Iwasug.WhatsApp.Sdk/Stores/ISignalSessionStore.cs:83-93`
**Issue:** `sealed record SignalSessionRecord(string Address, byte[] SessionState)` gets
compiler-generated equality that compares `SessionState` with
`EqualityComparer<byte[]>.Default` — i.e. **reference** equality. Two records with
byte-identical session state are unequal and hash differently. Hosts comparing records for
value equality (a natural expectation for a "typed domain record", D-04) get identity
semantics and will write subtle bugs; nothing in the XML docs warns them.
**Fix:** either document the reference-equality semantics explicitly in the record's XML
docs, or override `Equals`/`GetHashCode` to compare `SessionState` by content
(`SessionState.AsSpan().SequenceEqual(other.SessionState)`).

### IN-06: ComplianceTestBase completeness backstop is silently vacuous under trimming, while the kit declares IsAotCompatible

**File:** `src/Iwasug.WhatsApp.Sdk.Testing/ComplianceTestBase.cs:32-43, 53-60, 91-106`
(kit flag at `src/Iwasug.WhatsApp.Sdk.Testing/Iwasug.WhatsApp.Sdk.Testing.csproj:15`)
**Issue:** the backstop relies on `Assembly.GetTypes()` / `AppDomain` scanning, with
`[UnconditionalSuppressMessage]` silencing the trimming warnings. The suppression's
justification ("runs in a non-trimmed test host") matches the intended usage, but the kit
is simultaneously shipped with `<IsAotCompatible>true</IsAotCompatible>`, which asserts the
opposite posture. If a host ever runs the compliance fact in a trimmed/AOT context,
`GetTypes()` returns fewer types, `GetContractBaseClasses()` can return an empty set, and
`Every_contract_class_has_a_concrete_subclass` **passes vacuously** — exactly the silent
no-coverage failure the backstop exists to prevent.
**Fix:** add a floor assertion (e.g. `GetContractBaseClasses().Count.Should().BeGreaterThanOrEqualTo(5)`,
the five known seams), so an empty/trimmed discovery fails loudly instead of passing.

### IN-07: NuGet packaging gaps — XML doc comments will not ship to consumers

**File:** `src/Iwasug.WhatsApp.Sdk/Iwasug.WhatsApp.Sdk.csproj:8-15`
**Issue:** every public member carries detailed XML documentation, but
`GenerateDocumentationFile` is not set, so the `.xml` file is never produced and IntelliSense
consumers of the NuGet package see none of it. For a published SDK this forfeits a large
part of the documentation investment (and the docs *are* the API contract here — D-19/D-21
semantics live in XML remarks). Also absent before first publish: `RepositoryUrl` /
`PublishRepositoryUrl`, `PackageIcon`, `PackageTags`, `PackageReadmeFile`.
**Fix:** add to `Directory.Build.props` (packable projects only):

```xml
<PropertyGroup>
  <GenerateDocumentationFile>true</GenerateDocumentationFile>
</PropertyGroup>
```

plus repository metadata; note this will turn on CS1591 "missing XML doc" warnings, which
`TreatWarningsAsErrors` then enforces — desirable for a public SDK.

### IN-08: CreateSessionAsync's once-per-account guarantee rests on a razor-thin closure-over-variable pattern

**File:** `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs:110-117`
**Issue:** the correctness of the D-24 "creation runs exactly once" guarantee under a race
depends on a very subtle chain: each racing thread's `GetOrAdd` value-factory creates a
`Lazy` whose inner factory captures the *local variable* `lazy`; the losing thread's `Lazy`
is discarded, and because `GetOrAdd` returns the winner and the code assigns it back to the
same local, the inner factory observes `self == winning lazy` when `.Value` is later
accessed. I traced this and it is **correct today** — but a routine refactor that captures
the value instead of the variable (e.g. `var self = lazy;` inside the lambda, or binding
before GetOrAdd) would silently produce two parallel sessions per account under
concurrency, with no test currently covering the racy interleaving. The comment at lines
110–113 documents intent but not this specific dependency.
**Fix:** make the invariant structural rather than lexical — e.g. have the inner factory
re-read `_sessions[accountId]` instead of closing over `lazy`, or add a regression test
that deterministically exercises the two-thread GetOrAdd race (parallel
`CreateSessionAsync` barrier + `Received(1)` on a host factory).

### IN-09: EnsureUnique is check-then-act and cannot actually enforce uniqueness under concurrency

**File:** `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs:146-156`
**Issue:** `EnsureUnique` does `_sessions.ContainsKey(accountId)` and throws — but the
documented consumer ("the future account-adding API") must then call
`CreateSessionAsync` separately, so between the check and the create another thread can
register the same id: the D-26 guarantee is not enforceable through this shape. This is
acknowledged as Phase 7 scope, but the helper's existence suggests a guarantee it cannot
deliver.
**Fix:** when the account-adding API lands, give it an atomic primitive (e.g.
`TryRegister(accountId, out session)` built on `GetOrAdd` + an added-marker, or
`ConcurrentDictionary.AddOrUpdate` semantics), and document `EnsureUnique` as a
best-effort pre-check only.

### IN-10: `dotnet test --nologo` silently reports "Zero tests ran" on the pinned SDK — CI integration trap

**Files:** `tests/Iwasug.WhatsApp.Sdk.Tests/Iwasug.WhatsApp.Sdk.Tests.csproj:11-16`,
`tests/Iwasug.WhatsApp.Sdk.Tests/README.md:39-47`
**Issue:** verified 2026-10-08 on SDK 10.0.401: plain `dotnet test` (the README's
documented command) passes 161/161, but `dotnet test --nologo` — an extremely common CI
invocation — fails with `Zero tests ran, error: 1` and exit code 5 (reproduced at project
and solution level; unrelated flags/build state ruled out). This is a .NET SDK ↔
Microsoft.Testing.Platform interop defect, not a repo code defect, but the repo's
MTP-mode test setup will collide with it the first time someone decorates the CI
invocation. The swallowed error message ("error: 1" with no text) makes diagnosis
expensive.
**Fix:** document the constraint next to the existing csproj comment (e.g. "do not pass
`--nologo` to `dotnet test` under MTP mode — the run silently no-ops with exit 5") or pin
CI to an invocation known to work; re-test when the SDK servicing train moves.

---

_Reviewed: 2026-10-08T07:58:10Z_
_Reviewer: the agent (gsd-code-reviewer)_
_Depth: standard_
