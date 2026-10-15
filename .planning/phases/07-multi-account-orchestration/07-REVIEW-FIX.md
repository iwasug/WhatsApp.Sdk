---
phase: 07-multi-account-orchestration
fixed_at: 2026-10-15T22:39:42Z
review_path: .planning/phases/07-multi-account-orchestration/07-REVIEW.md
iteration: 1
findings_in_scope: 21
fixed: 21
skipped: 0
status: all_fixed
---

# Phase 07: Code Review Fix Report

**Fixed at:** 2026-10-15T22:39:42Z
**Source review:** .planning/phases/07-multi-account-orchestration/07-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 21 (2 Critical, 9 Warning, 10 Info; `fix_scope: all`)
- Fixed: 21
- Skipped: 0

**Fix strategy:** each finding was applied to the current code state in an isolated
worktree (one atomic commit per finding), adapted from the reviewer's guidance where the
source had drifted (WR-03/WR-05/WR-06 rewrites moved the cited lines). Cross-cutting
notes:

- **CR-01 also delivered WR-08's `TryGetConnection` half** — the connection accessor and
  the real transmit/auto-connect wiring are one behavioral change; the messenger
  accessor (`TryGetMessenger`) landed separately in WR-08.
- **IN-03's `SessionManager` half was folded into WR-03** — the no-op
  `catch { throw; }` there could not survive the WR-03 rewrite; IN-03's commit covers the
  remaining `DefaultAccountRuntimeFactory` occurrence. A grep at that commit confirmed no
  no-op catch-and-rethrow remains anywhere under `src/.../Accounts/`.
- **IN-10 overlaps CR-02 and existing fix tests** — the reviewer's `CircuitState == Closed`
  assertion was replaced by the breaker-trip test as part of CR-02 (`Manager_Status_Reflects_
  Real_Breaker_State`), the interleaved add/remove proof landed with WR-03, and the
  late-add fused-reader test with WR-04; IN-10's commit added the still-missing
  restart-identity/no-CTS-accumulation test and pinned the recent-failures count.

## Fixed Issues

### CR-01: The host-reachable factory can never connect or pair (throw-only transport, stub payloads, no auto-connect)

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Accounts/DefaultAccountRuntimeFactory.cs`, `src/Iwasug.WhatsApp.Sdk/Accounts/IWhatsAppSessionManager.cs`, `src/Iwasug.WhatsApp.Sdk/Accounts/SessionManager.cs`, `src/Iwasug.WhatsApp.Sdk/Hosting/ServiceCollectionExtensions.cs`, `src/Iwasug.WhatsApp.Sdk/Hosting/WhatsAppHostedService.cs`, `src/Iwasug.WhatsApp.Sdk/Hosting/WhatsAppSdkOptions.cs`, `src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt`, `tests/Iwasug.WhatsApp.Sdk.Tests/Hosting/HostedServiceLifecycleTests.cs`, `tests/Iwasug.WhatsApp.Sdk.Tests/Isolation/TwoAccountRuntimeTests.cs`
**Commit:** 67c0775
**Applied fix:** Default transport is now the real `WebSocketFrameTransport` (with an `Endpoint`/`TransportFactory` override for loopback tests), handshake payloads default to the credential-aware `Pairing.HandshakePayloadProvider` over the account's store session (`HandshakePayloadsFactory` override, `RegistrationDefaults` for the no-credential registration branch), and `WhatsAppHostedService` auto-connects each registered account when `WhatsAppSdkOptions.AutoConnect` (default `true`) is set. `IWhatsAppSessionManager.TryGetConnection` was added so a host can drive pairing itself. New public options/seam members have `PublicAPI.Unshipped.txt` entries; an internal `ResolveTransportFactory` test seam keeps isolation tests off the wire. Both `HostedServiceLifecycleTests` set `AutoConnect = false` (real sockets never opened in unit tests).
**Status:** fixed: requires human verification — the unit suite proves the wiring (options plumbing, accessor, test seams) but the real-socket connect path itself is only observable against a live endpoint.

### CR-02: `AccountStatus.CircuitState` reports a second, inert gate

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Accounts/DefaultAccountRuntimeFactory.cs`, `src/Iwasug.WhatsApp.Sdk/Accounts/SessionManager.cs`, `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs`, `tests/Iwasug.WhatsApp.Sdk.Tests/Isolation/RateLimitIsolationTests.cs`
**Commit:** fd4efb1
**Applied fix:** The factory now adopts the single real gate every send traverses (`Messenger.SafetyGate`, exposed internally as the messenger's `_safety` instance) instead of constructing a second, ungated `SafetyGate`; status/health surfaces therefore reflect the breaker that actually refuses sends. `Manager_Status_Reflects_Real_Breaker_State` replaced the old `CircuitState == Closed` invariant: it trips A's real gate with five consecutive server errors and asserts A reports `Open` while B stays `Closed`.

### WR-01: Status facts were seeded/fabricated instead of driven by events

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Accounts/AccountRuntime.cs`, `src/Iwasug.WhatsApp.Sdk/Accounts/DefaultAccountRuntimeFactory.cs`, `src/Iwasug.WhatsApp.Sdk/Accounts/SessionManager.cs`, `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs`, `tests/Iwasug.WhatsApp.Sdk.Tests/Isolation/RateLimitIsolationTests.cs`
**Commit:** c2c99fa
**Applied fix:** `AccountRuntime` subscribes to the connection's `StateChanged` (drives `LastConnectedAt`/activity), to the messenger's `SendSettled` event (settled-send activity and failures), and to the single real gate's fault events (`BudgetExhausted`/`ThrottleEngaged`/`AccountRestricted` → `RecentFailures`/`LastErrorRuleName`); no construction-time seeds remain, and `Messenger` gained the internal `SendSettled` seam. `Status_Facts_Have_Real_Writers_Not_Fabricated_Seeds` asserts initial nulls/zero and post-trip facts.

### WR-02: `WhatsAppSdkOptions.EventChannelCapacity` was dropped

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Accounts/DefaultAccountRuntimeFactory.cs`, `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs`, `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs`, `tests/Iwasug.WhatsApp.Sdk.Tests/Isolation/TwoAccountRuntimeTests.cs`
**Commit:** f804441
**Applied fix:** The factory validates/records the configured capacity and threads it through `WaConnection.MessagingEventChannelCapacity` into the messenger's event channel, so the host-configured bound is honored per account; isolation tests pin the behavior.

### WR-03: Add/Remove/Restart raced (resurrection, spurious duplicates, add-after-dispose leak)

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Accounts/SessionManager.cs`, `tests/Iwasug.WhatsApp.Sdk.Tests/SessionManager/SessionManagerTests.cs`
**Commit:** 382fc82
**Applied fix:** A `_mutationGate` semaphore serializes all mutations, restart's remove+add runs in one critical section, and `AddAccountCoreAsync` re-checks the disposed flag under the lifecycle lock before registering (add-after-dispose disposes the orphaned runtime and throws `ObjectDisposedException`). Helpers `ThrowIfDisposed`/`DisposeRuntimeQuietlyAsync` de-duplicate the pattern, and `Interleaved_Add_Restart_Remove_No_Resurrection` (two workers × 100 iterations) proves no resurrection and a zero final count. This rewrite subsumed IN-03's `SessionManager` half (see header notes).

### WR-04: The fused reader never woke for account-set changes

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Accounts/SessionManager.cs`, `tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/EventStreamCancellationTests.cs`
**Commit:** 1fb0309
**Applied fix:** A membership signal (lock-guarded `TaskCompletionSource`, swapped-and-completed on every successful add/remove and at the start of the dispose drain) is snapshotted before enumerating accounts and raced against the per-account `MoveNextAsync` tasks; the zero-account branch awaits the signal instead of `Task.Delay(100)`. `FusedReaderSeesAccountAddedAfterItStarted` proves an account added while every existing account is quiet is observed.

### WR-05: `AccountScopedLogger` was never used by the SDK's own logging

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Accounts/DefaultAccountRuntimeFactory.cs`, `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs`, `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs`, `tests/Iwasug.WhatsApp.Sdk.Tests/Isolation/TwoAccountRuntimeTests.cs`
**Commit:** 8f555e3
**Applied fix:** The factory now wraps every account-graph logger (`WaConnection`, `SafetyGate`, `AccountRuntime`) in `AccountScopedLogger<T>`, and `WaConnection` exposes an internal `SafetyLogger` seam consumed by the messenger's safety-gate construction — so emitted entries carry the `AccountId` scope. `RuntimeLoggers_CarryAccountIdScope` captures a real runtime-produced log entry and asserts the scope.

### WR-06: The configuration overload clobbered the host's `IConfiguration`; per-account registries had no owner

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Accounts/AccountRuntime.cs`, `src/Iwasug.WhatsApp.Sdk/Accounts/DefaultAccountRuntimeFactory.cs`, `src/Iwasug.WhatsApp.Sdk/Hosting/ServiceCollectionExtensions.cs`, `tests/Iwasug.WhatsApp.Sdk.Tests/Hosting/AddWhatsAppSdkTests.cs`
**Commit:** 0e32254
**Applied fix:** The overload binds the caller's configuration instance directly (`.Configure(o => configuration.GetSection(sectionName).Bind(o))`) instead of registering it as the container's `IConfiguration`; the factory consumes a container-registered `StoreRegistry` as the host-owned seam (D-03) and otherwise creates one registry per account, with `AccountRuntime.OwnedStoreRegistry` disposed by the runtime (and by the factory's catch path). Three tests cover the clobber, the host-owned registry, and per-account registry disposal.

### WR-07: Dead drain CTS and an unobserved abandoned dispose task

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Hosting/WhatsAppHostedService.cs`
**Commit:** 53b4b09
**Applied fix:** Removed the unused `drainCts` and the unreachable catch; the drain awaits a single bounded `disposeTask.WaitAsync(drainTimeout, cancellationToken)`; the timeout path logs `DrainTimeout` and observes the abandoned task via a helper (`OnlyOnFaulted` continuation logging `DrainFault`), as does the cancellation path.

### WR-08: No public path from the manager to an account's messenger

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Accounts/IWhatsAppSessionManager.cs`, `src/Iwasug.WhatsApp.Sdk/Accounts/SessionManager.cs`, `src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt`, `tests/Iwasug.WhatsApp.Sdk.Tests/SessionManager/SessionManagerTests.cs`
**Commit:** 36b72cb
**Applied fix:** `TryGetMessenger(AccountId, out Messenger?)` added to the interface and implementation, documented as manager-owned (`callers must not dispose`), with the PublicAPI entry (the `TryGetConnection` half of this finding landed with CR-01). `TryGetMessenger_Resolves_Live_Account_And_Is_False_For_Unknown` covers resolution, identity vs. the runtime, and post-removal false.

### WR-09: AOT claim vs. suppressed `IL2026`/`IL3050` on the configuration overload

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Hosting/ServiceCollectionExtensions.cs`
**Commit:** 707338b
**Applied fix:** The `IConfiguration` overload now carries `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]` (pragma removed), so consumers see the warning at their own call site; the XML docs point AOT-published hosts at the delegate overload. Verified against the PublicAPI analyzer source and empirically: attribute annotations are not part of the tracked API text, so no `PublicAPI.Unshipped.txt` change was required (build stayed at 0 warnings with `TreatWarningsAsErrors`).

### IN-01: Stale doc comment on `OutboundEnvelope`

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Accounts/OutboundEnvelope.cs`
**Commit:** c12466e
**Applied fix:** Comment now describes the actual record — account id plus optional rule name and payload — instead of claiming a payload-free placeholder.

### IN-02: `SessionManager._registrations` dead state

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Accounts/SessionManager.cs`
**Commit:** 36dabd0
**Applied fix:** The write-only dictionary and its three touching lines were deleted; a repo-wide grep confirms no remaining references.

### IN-03: Redundant `try { ... } catch { throw; }` wrapper

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Accounts/DefaultAccountRuntimeFactory.cs`
**Commit:** 653e6b2
**Applied fix:** The no-op wrapper around `CreateSessionAsync` was replaced with a direct assignment (the SessionManager half was already gone via WR-03; verified no such pattern remains under `Accounts/`).

### IN-04: `LinkedCts`/`OutboundQueue` unwired scaffolding

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Accounts/AccountRuntime.cs`
**Commit:** 7ca588e
**Applied fix:** Both members are now explicitly doc-marked as not-yet-wired scaffolding (nothing produces/drains the queue; the CTS is never linked to a manager stop token) with pointers to the planned pump/token seam — the review's "mark them explicitly as not yet active" option. Wiring the pump is a behavioral change beyond a fix's scope.

### IN-05: `ScopedLogger<T>` duplicated `AccountScopedLogger<T>`

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Hosting/ScopedLogger.cs`, `src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt`
**Commit:** cb7b8b1
**Applied fix:** The alias type was deleted (with its two unshipped PublicAPI entries); `AccountScopedLogger<T>` — the type production code actually uses — is kept. No consumers referenced the alias.

### IN-06: `Messenger.EventWriter` unused seam

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs`
**Commit:** ec72e4d
**Applied fix:** Removed the uncalled `EventWriter` property; `TryPublishEvent` (which the tests use) remains.

### IN-07: `ReadAccountEventsAsync` double lookup (and undocumented deferred throw)

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Accounts/SessionManager.cs`, `src/Iwasug.WhatsApp.Sdk/Accounts/IWhatsAppSessionManager.cs`
**Commit:** 3db34d0
**Applied fix:** `ContainsKey` + `TryGetValue` collapsed into a single `TryGetValue` (no check-then-get window where a concurrent removal silently ends the stream); the interface docs now state that resolution and `SdkUnknownAccountException` happen at the first `MoveNextAsync` because the method is an async iterator.

### IN-08: `WhatsAppSdkOptions.Validate` named `options` in member errors

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Hosting/WhatsAppSdkOptions.cs`
**Commit:** 55e2e25
**Applied fix:** `ArgumentOutOfRangeException`/`ArgumentNullException` now name the offending member (`StartupJitterMs`, `ShutdownDrainTimeoutMs`, `Accounts`, `AccountId`) instead of the whole options object, so operators can locate the bad row/value.

### IN-09: Delegate overload lacked `ValidateOnStart()`

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Hosting/ServiceCollectionExtensions.cs`, `tests/Iwasug.WhatsApp.Sdk.Tests/Hosting/AddWhatsAppSdkTests.cs`
**Commit:** 3d6efdb
**Applied fix:** `.ValidateOnStart()` added for symmetry with the configuration overload. `Delegate_Overload_Validates_Options_On_Start` resolves the public `IStartupValidator` and asserts invalid options fail at start (the validation delegate's own exception surfaces unwrapped — standard `ValidateOptions<T>` behavior — so the test asserts on the operator-facing message).

### IN-10: Missing plan-mandated lifecycle/health tests

**Files modified:** `tests/Iwasug.WhatsApp.Sdk.Tests/SessionManager/SessionManagerTests.cs`, `tests/Iwasug.WhatsApp.Sdk.Tests/Isolation/RateLimitIsolationTests.cs`
**Commit:** 9679fd9
**Applied fix:** Added `Restart_Rebuilds_The_Graph_And_Retires_The_Old_Token_Source` (fresh runtime identity across repeated restarts, old token cancelled at retirement, fresh token live, exactly one registration — the restart-identity/no-CTS-accumulation proof); and the recent-failures assertion was pinned from "greater than zero" to exactly `1` for the single `AccountRestricted` fact of one breaker opening. The interleaved add/remove proof (WR-03) and the breaker-trip status test (CR-02, replacing the old `CircuitState == Closed` invariant) are the other two plan-mandated pieces and landed with their findings.

## Verification

**Location:** every build and test run happened inside the isolated worktree
`C:/Users/iwasu/iwasug/sdk-wa/.claude/worktrees/rf-07-24612-1792099794` (branch
`gsd-reviewfix/07-24612`, based on `15ec7f0`). A worktree-env result is not reproducible
from the main checkout once the worktree is removed — after the cleanup tail fast-forwards
the branch, the same numbers reproduce from the main checkout at the merge commit.

**Gates per fix:** 3-tier verification — re-read of the changed section, then
`dotnet build Iwasug.WhatsApp.Sdk.sln` (0 warnings / 0 errors under
`TreatWarningsAsErrors`, which is the gate that also validates `PublicAPI.Unshipped.txt`
via the PublicApiAnalyzers), then targeted `dotnet test` filters covering the touched
surface. Comment/doc-only findings (IN-01, IN-04) used re-read + build. No fix was
rolled back; the working tree was clean at report time.

**Final broad pass (after all 21 commits):**
`dotnet test Iwasug.WhatsApp.Sdk.sln --filter "FullyQualifiedName~Isolation|FullyQualifiedName~SessionManager|FullyQualifiedName~EventStream|FullyQualifiedName~AddWhatsAppSdk|FullyQualifiedName~HostedService|FullyQualifiedName~ScopedLogger|FullyQualifiedName~TwoAccountRuntime" --no-build`
→ passed, 36 total / 0 failed.

Per-finding filters also ran clean at their own commits, including the new tests:
`TryGetMessenger_Resolves_Live_Account_And_Is_False_For_Unknown`,
`Restart_Rebuilds_The_Graph_And_Retires_The_Old_Token_Source`,
`Delegate_Overload_Validates_Options_On_Start`, `Interleaved_Add_Restart_Remove_No_Resurrection`,
`FusedReaderSeesAccountAddedAfterItStarted`, `RuntimeLoggers_CarryAccountIdScope`,
`Manager_Status_Reflects_Real_Breaker_State`, `Status_Facts_Have_Real_Writers_Not_Fabricated_Seeds`.

**Known pre-existing failures (not caused by these fixes):** the four
`PairingLifecycleTests` timeouts documented in `07-VERIFICATION.md` were untouched and not
re-run as part of this pass.

---

_Fixed: 2026-10-15T22:39:42Z_
_Fixer: the agent (gsd-code-fixer)_
_Iteration: 1_
