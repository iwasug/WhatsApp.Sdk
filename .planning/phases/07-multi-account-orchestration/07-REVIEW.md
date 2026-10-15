---
phase: 07-multi-account-orchestration
reviewed: 2026-10-15T21:26:39Z
depth: standard
files_reviewed: 27
files_reviewed_list:
  - Directory.Packages.props
  - src/Iwasug.WhatsApp.Sdk/Accounts/AccountRuntime.cs
  - src/Iwasug.WhatsApp.Sdk/Accounts/AccountStatus.cs
  - src/Iwasug.WhatsApp.Sdk/Accounts/DefaultAccountRuntimeFactory.cs
  - src/Iwasug.WhatsApp.Sdk/Accounts/IAccountRuntimeFactory.cs
  - src/Iwasug.WhatsApp.Sdk/Accounts/IWhatsAppSessionManager.cs
  - src/Iwasug.WhatsApp.Sdk/Accounts/OutboundEnvelope.cs
  - src/Iwasug.WhatsApp.Sdk/Accounts/SessionManager.cs
  - src/Iwasug.WhatsApp.Sdk/Errors/SdkException.cs
  - src/Iwasug.WhatsApp.Sdk/Hosting/AccountDescriptor.cs
  - src/Iwasug.WhatsApp.Sdk/Hosting/AccountEvent.cs
  - src/Iwasug.WhatsApp.Sdk/Hosting/ScopedLogger.cs
  - src/Iwasug.WhatsApp.Sdk/Hosting/ServiceCollectionExtensions.cs
  - src/Iwasug.WhatsApp.Sdk/Hosting/WhatsAppHostedService.cs
  - src/Iwasug.WhatsApp.Sdk/Hosting/WhatsAppSdkOptions.cs
  - src/Iwasug.WhatsApp.Sdk/Iwasug.WhatsApp.Sdk.csproj
  - src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs
  - src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt
  - src/Iwasug.WhatsApp.Sdk/Safety/SafetyGate.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Hosting/AddWhatsAppSdkTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Hosting/HostedServiceLifecycleTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Hosting/ScopedLoggerTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Isolation/NoStaticMutableStateTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Isolation/RateLimitIsolationTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Isolation/TwoAccountRuntimeTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/EventStreamCancellationTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/SessionManager/SessionManagerTests.cs
findings:
  critical: 2
  warning: 9
  info: 10
  total: 21
status: issues_found
---

# Phase 07: Code Review Report

**Reviewed:** 2026-10-15T21:26:39Z
**Depth:** standard
**Files Reviewed:** 27
**Status:** issues_found

## Summary

Phase 07 introduces the multi-account orchestration surface: `SessionManager` (atomic `ConcurrentDictionary` registry + fused event reader), `AccountRuntime`/`DefaultAccountRuntimeFactory` (per-account graph composition), the `AddWhatsAppSdk` DI/options/hosted-service hosting package, `AccountScopedLogger`, and the isolation test suite. The registry's duplicate/replace/remove mechanics, the public API surface (analyzer-green), the store-first fail-clean construction, and the absence of hardcoded secrets, injection sinks, or unsafe deserialization are all sound. No security vulnerabilities were found.

The dominant defect class is **wiring without connection**: the composition root creates pieces that are never driven, and the phase's health/observability surface reports fabricated or constant values. Two of these are release-blocking: (1) the only host-reachable account factory wires a throw-only transport and stub handshake payloads, so accounts created through `AddWhatsAppSdk` can never connect or pair — and nothing ever calls `ConnectAsync` anyway — while the hosted service reports success and the connection monitor retries forever; (2) `AccountStatus.CircuitState` is bound to a second, inert `SafetyGate` that never gates a send, so the public health snapshot reports `Closed` even while the real gate (inside `Messenger`) is refusing sends. Several options (`EventChannelCapacity`) and seams (`AccountScopedLogger`, per-account registry ownership) are likewise declared and then dropped. The isolation suite is structurally good; `RateLimitIsolationTests` even encodes the circuit-state bug as an expected invariant (`CircuitState == Closed`), which is why the phase verification's "verified" claims overreach.

## Critical Issues

### CR-01: The only host-reachable account factory wires a throwing transport and stub handshake payloads — composed accounts can never connect or pair

**File:** `src/Iwasug.WhatsApp.Sdk/Accounts/DefaultAccountRuntimeFactory.cs:76-84` (also `:140-144`)

**Issue:** `DefaultAccountRuntimeFactory` is the single composition path reachable from the public hosting surface (`AddWhatsAppSdk` registers it; `IAccountRuntimeFactory` and `AccountRuntime` are `internal`, so a host cannot substitute either — `ServiceCollectionExtensions.cs:61,66-79`). For every account it constructs:

```csharp
connectionInstance = new WaConnection(
    accountId,
    () => throw new NotSupportedException("No transport wired in Wave 1 factory."),
    new EmptyNoiseHandshakePayloads(), ...);
```

Consequences, all reachable through the shipped public API:

1. `ConnectAsync` can never open: the transport factory throws on the first attempt, `CreateTransportSafely` wraps it (`Connection/WaConnection.cs:1537-1556`), and the monitor's generic catch classifies it as a retryable 408 drop (`Connection/WaConnection.cs:596-605`) — so the loop retries the throwing factory forever with backoff, logging a warning+error per attempt, and `ConnectAsync` never completes.
2. `runtime.Connection.Pairing` throws `InvalidOperationException` because `EmptyNoiseHandshakePayloads` is not credential-aware (`Connection/WaConnection.cs:746-751`), so QR/code pairing — the product's entry point — is unreachable.
3. Nothing ever calls `ConnectAsync` on a runtime-managed connection at all: `WhatsAppHostedService.StartAsync` only calls `AddAccountAsync` (`Hosting/WhatsAppHostedService.cs:47-57`), and there is no `ConnectAsync` call site anywhere in `src/`.
4. Nevertheless `StartAsync` succeeds account-by-account (failures are logged and swallowed, deliberately) and `GetStatus` reports `Connecting`, so a host believes the fleet is live while every account is dead.

This stub is not declared anywhere: no phase-07 plan mentions it, `07-01-SUMMARY.md` states "Known Stubs: None", and `07-VERIFICATION.md` marks SC1/SC3 verified ("the hosting surface starts accounts"). The production transport already ships in this repository (`Transport/WebSocketFrameTransport.cs:54-107`, public, takes endpoint + `ConnectionOptions` + logger) and is simply not used here.

**Fix:**
```csharp
// in DefaultAccountRuntimeFactory.CreateAsync — wire the real seams:
var transportLogger = loggerFactory.CreateLogger<WebSocketFrameTransport>();
connectionInstance = new WaConnection(
    accountId,
    () => new WebSocketFrameTransport(_endpoint, connection, transportLogger), // endpoint from ConnectionOptions/option
    new Pairing.HandshakePayloadProvider(accountId, session.Credentials, registrationDefaults), // credential-aware
    connection, _timeProvider, logger);
// and start the lifecycle once the runtime is registered, routing failures into status,
// e.g. _ = runtime.Connection.ConnectAsync(runtime.LinkedCts.Token);  (observing faults)
```
If this is intentionally deferred, do not let it be silent: throw at `AddAccountAsync`/`StartAsync` time (fail fast, per the repo's fail-loudly discipline), expose a transport/payload seam the host can supply (`IAccountRuntimeFactory` is internal today), and record it as a known stub in the phase summary.

### CR-02: `AccountStatus.CircuitState` is permanently `Closed` — status is wired to a second safety gate that never gates any send

**File:** `src/Iwasug.WhatsApp.Sdk/Accounts/DefaultAccountRuntimeFactory.cs:90-97` (status mapping at `src/Iwasug.WhatsApp.Sdk/Accounts/SessionManager.cs:128-138`)

**Issue:** Each account gets **two** `SafetyGate` instances. The factory's gate is built with a no-op publish callback and is never asked to execute a send:

```csharp
ISafetyGate gate = new SafetyGate(
    _timeProvider, safety, safetyLogger,
    _ => new ValueTask<StoreSession>(session),      // never invoked
    (evt, token) => ValueTask.CompletedTask);       // every event dropped
```

The gate that actually gates traffic is the private one `Messenger` constructs in its constructor from `connection.SafetyPolicy` (`Messaging/Messenger.cs:88-96`), and every send traverses it (`Messenger.cs:212-215, 240-243, 256, 277, 294, 313, 332, 349, 419`). `AccountRuntime.SafetyGate` therefore never records an outcome and its breaker never leaves `Closed`; `SessionManager.GetStatus` maps exactly that gate:

```csharp
if (runtime.SafetyGate is Safety.SafetyGate sg) { circuit = sg.CircuitState switch { ... } }
```

So the public health surface (MUL-01 "per-account health visible at a glance") reports `CircuitState = Closed` forever, even while the real gate is visibly refusing sends with `SafetyRule.CircuitOpen` and emitting `AccountRestricted` events. Any safety fact raised on the runtime gate would be silently dropped (no-op publish). The phase's own test cements the defect as an invariant: `tests/.../Isolation/RateLimitIsolationTests.cs:231-234` asserts `status.CircuitState.Should().Be(SafetyCircuitState.Closed)` for both accounts — an assertion that can never fail given this wiring. The one positive here is that per-account safety *options* do reach the real gate (`connection.SafetyPolicy` is set at `DefaultAccountRuntimeFactory.cs:87`), so isolation is preserved; only the status/observability surface is wrong.

**Fix:**
```csharp
// Messenger.cs — expose the single real gate:
internal ISafetyGate SafetyGate => _safety;

// DefaultAccountRuntimeFactory.cs — stop creating a second, inert gate:
Messenger messenger = connectionInstance.Messaging;
ISafetyGate gate = messenger.SafetyGate;   // one gate per account, the one that gates sends
```
Then add a test that trips the breaker through a real `Messenger` send and asserts `manager.GetStatus(id)!.CircuitState == SafetyCircuitState.Open`.

## Warnings

### WR-01: Four of eight `AccountStatus` fields are fabricated or always-default

**File:** `src/Iwasug.WhatsApp.Sdk/Accounts/SessionManager.cs:139-147` (also `DefaultAccountRuntimeFactory.cs:124`, `Accounts/AccountRuntime.cs:55-57`)

**Issue:** `RecentFailures` is hard-coded to `0` at the construction site, and `LastActivityAt`/`LastErrorRuleName` have no writer anywhere in the codebase (grep shows only the manager reading them), so they are always `null`. `LastConnectedAt` is seeded by the factory at *construction* time:

```csharp
runtime.LastConnectedAt = _timeProvider.GetUtcNow().AddTicks(Interlocked.Increment(ref _seq));
```

i.e. an account that has never opened a socket reports a "last connected at" timestamp (with a per-account tick offset that exists only to make the value move on restart). `ConnectionState` likewise reports `Connecting` (`WaConnection._state` initializer, `Connection/WaConnection.cs:189`) for connections that were never started. `07-04-PLAN.md` explicitly required "recent-failures counting (incremented on surfaced fault only)", and `07-VERIFICATION.md` claims status carries "rule-only last-error name" — neither is implemented.

**Fix:** Delete the fabricated seed; drive `LastConnectedAt`/`LastActivityAt` from `WaConnection.StateChanged` (open transitions and settled sends); increment `RecentFailures` where the breaker records a failing outcome; set `LastErrorRuleName` where a typed rule violation surfaces. Until then, remove the unimplemented members from `AccountStatus` rather than shipping fields that always read zero/null/fake.

### WR-02: `WhatsAppSdkOptions.EventChannelCapacity` is silently ignored

**File:** `src/Iwasug.WhatsApp.Sdk/Accounts/DefaultAccountRuntimeFactory.cs:23,42`

**Issue:** `_eventChannelCapacity` is assigned (`eventChannelCapacity <= 0 ? 128 : eventChannelCapacity`) and then never read. The DI registration passes `value.EventChannelCapacity` into it (`Hosting/ServiceCollectionExtensions.cs:75`), the option is publicly bound and validated (`Hosting/WhatsAppSdkOptions.cs:42-43, 75-78`), but `Messenger` hardcodes its stream capacity as `public const int EventChannelCapacity = 128` (`Messaging/Messenger.cs:46,59-65`). A host configuring a different capacity gets no effect and no warning.

**Fix:** Plumb the capacity into `Messenger` (add a `int eventChannelCapacity` constructor parameter and pass `_eventChannelCapacity` at `DefaultAccountRuntimeFactory.cs:99`), or delete the option from `WhatsAppSdkOptions` until it can be honored.

### WR-03: `SessionManager` lifecycle races: add-after-dispose leaks a live runtime; remove/restart can resurrect an account

**File:** `src/Iwasug.WhatsApp.Sdk/Accounts/SessionManager.cs:34-40, 47-53, 55-80, 105-123`

**Issue:** `_lifecycleGate` is only used to check the disposed flag, then released before any state mutation:

- `AddAccountAsync` checks `_disposed` under the gate (lines 34-37/47-50) but `AddAccountCoreAsync` performs `_accounts.TryAdd` after the awaited factory call (lines 71-75) with **no re-check**. An `AddAccountAsync` that passed the check before `DisposeAsync` snapshotted (lines 254-259) will register its runtime after the drain — the runtime is never disposed, the disposed manager still enumerates it, and the documented "no registration after shutdown" contract (mirrored from `StoreRegistry` WR-01, which *does* re-check the flag before handing anything out — `Stores/StoreRegistry.cs:125-176`) is broken.
- `RestartAccountAsync` is `ContainsKey` → descriptor read → `RemoveAccountAsync` → `AddAccountAsync` (lines 105-123) with no mutual exclusion. Interleaved with a concurrent `RemoveAccountAsync`, the restart's `AddAccountAsync` re-registers the account the remove intended to delete (resurrection); interleaved with a concurrent `AddAccountAsync`, restart throws `SdkDuplicateAccountException` for an account it just removed. `07-01-PLAN.md` required an interleaved add/remove proof ("no resurrection and a final count of zero when all removes win"); that test does not exist.

**Fix:** Adopt the `StoreRegistry` WR-01 discipline: re-check `_disposed` under `_lifecycleGate` after `CreateAsync` and dispose+`throw new ObjectDisposedException` if set (put the `TryAdd` inside the gate); and serialize mutating operations (`AddAccountAsync`/`RemoveAccountAsync`/`RestartAccountAsync`) so restart's remove+add pair is atomic with respect to other mutations. Add the interleaved add/remove test the plan required.

### WR-04: The fused event reader cannot see accounts added after it starts while existing accounts are quiet

**File:** `src/Iwasug.WhatsApp.Sdk/Accounts/SessionManager.cs:165-194`

**Issue:** The loop only re-enumerates `_accounts` to create enumerators for new accounts at the top of each iteration, then parks in `await Task.WhenAny(nextTasks.Values).WaitAsync(cancellationToken)` (line 189). If every already-enumerated account is idle (no pending events), nothing completes, the loop never iterates, and events from an account registered *after* the reader started are not delivered until some pre-existing account emits an event or its stream ends — potentially indefinitely (the `Task.Delay(100)` poll exists only for the zero-account case, lines 179-184). Because each messenger channel is bounded with `FullMode = Wait` (`Messaging/Messenger.cs:59-65`), the starved account's receive pump also blocks once its 128-event buffer fills. This directly undercuts MUL-01's "hot add/remove" promise. `EventStreamCancellationTests` registers the account before starting the reader, so it cannot catch this. A workaround exists (hosts can use `ReadAccountEventsAsync` per account), which is why this is a warning rather than a blocker.

**Fix:** Wake the reader on membership change — race the `WhenAny` against a change signal (`TaskCompletionSource`/`Channel<bool>` flipped by add/remove), or bound the wait with a poll (`Task.WhenAny(whenAny, Task.Delay(pollInterval, cancellationToken))`) and re-enumerate accounts each cycle.

### WR-05: The per-account scoped logger is never wired into the account graph

**File:** `src/Iwasug.WhatsApp.Sdk/Hosting/ScopedLogger.cs:17-63` (consumers: `Accounts/DefaultAccountRuntimeFactory.cs:84, 89-90`)

**Issue:** `AccountScopedLogger<T>`/`ScopedLogger<T>` are referenced only by `ScopedLoggerTests` — no production code constructs them. The account graph logs through plain `CreateLogger<WaConnection>()`, `CreateLogger<AccountRuntime>()`, `CreateLogger<SafetyGate>()`, so no SDK log entry carries the `AccountId` scope. MUL-03 / SC3's "ILogger scopes tagged per account" is thus a library type with unit tests, not an active behavior of the SDK's own logging; with tens of accounts in one process, logs cannot be attributed to accounts.

**Fix:** In `DefaultAccountRuntimeFactory.CreateAsync`, wrap the per-account loggers, e.g. `new AccountScopedLogger<WaConnection>(accountId, loggerFactory)` before passing them to `WaConnection`/`SafetyGate`/`AccountRuntime`, and add an integration test asserting a runtime-produced log entry carries the `AccountId` scope.

### WR-06: DI composition hazards: host `IConfiguration` is clobbered; host `StoreRegistry` is ignored and the per-account registry is never disposed

**File:** `src/Iwasug.WhatsApp.Sdk/Hosting/ServiceCollectionExtensions.cs:46, 66-79` (also `Accounts/DefaultAccountRuntimeFactory.cs:63`)

**Issue:**
- `services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(configuration)` (line 46) appends a registration that wins over a host's own `IConfiguration` registration for every consumer resolving that service (last registration wins in `Microsoft.Extensions.DependencyInjection`). A host that passes a section/alternate configuration silently redirects unrelated components (including other options binding) to that instance. `TryAddSingleton` would satisfy `BindConfiguration`'s need without clobbering.
- `RegisterCore` always passes `registry: null` (line 71), so a container-registered `StoreRegistry` — the documented host persistence seam (D-03) — is never resolved. Each account then constructs its **own** `StoreRegistry` (`DefaultAccountRuntimeFactory.cs:63`) from `StoreFactories` options, and nothing ever disposes it: `AccountRuntime.DisposeAsync` disposes only the session/connection/CTS (`Accounts/AccountRuntime.cs:59-66`), `WaConnection.DisposeAsync` does not touch `StoreRegistry` (`Connection/WaConnection.cs:489-540`), and `SessionManager` disposes only runtimes. `StoreRegistry.DisposeAsync` (`Stores/StoreRegistry.cs:207-249`) is the code path that drains sessions and disposes the fallback factory — it never runs for these per-account registries. (Host store *instances* are still disposed via `StoreSession.DisposeAsync`, so this is an ownership/teardown defect rather than an unbounded leak.)

**Fix:** Use `services.TryAddSingleton<IConfiguration>(configuration)` (or drop the registration and have the config overload capture the section directly). Resolve the registry from the container (`sp.GetService<StoreRegistry>()`) as the factory's registry, falling back to a per-account or shared one; and give `AccountRuntime` explicit ownership of a factory-created registry so it is disposed with the runtime (or create one shared registry in `RegisterCore`).

### WR-07: `WhatsAppHostedService.StopAsync` has a dead drain CTS and abandons the dispose task on timeout

**File:** `src/Iwasug.WhatsApp.Sdk/Hosting/WhatsAppHostedService.cs:75-107`

**Issue:** `drainCts` is created and `CancelAfter(_options.Value.ShutdownDrainTimeoutMs)` is invoked (lines 79-88), but `drainCts.Token` is never used by anything — the actual bound is the separate `Task.Delay(_options.Value.ShutdownDrainTimeoutMs, cancellationToken)` on line 91. The CTS is dead code, and the `catch (ArgumentOutOfRangeException)` fallback (85-88) is unreachable for validated options (both `CancelAfter` overloads reject the same negative values). On timeout, `disposeTask` is abandoned (lines 93-96): its later fault is never observed, and `_manager.DisposeAsync()` keeps running after `StopAsync` returns, racing host teardown. The timeout value is also applied twice, suggesting the author intended the CTS to be the mechanism.

**Fix:** Either remove `drainCts` entirely and keep the single `Task.Delay` bound, or make the CTS meaningful (give `IWhatsAppSessionManager`/`SessionManager` a stop token to cancel). Replace the timeout branch so the abandoned task is observed, e.g. `await disposeTask.WaitAsync(TimeSpan.FromMilliseconds(opts.ShutdownDrainTimeoutMs), cancellationToken)` with the timeout case logging and re-observing faults via a `ContinueWith` that logs.

### WR-08: No public path from `IWhatsAppSessionManager` to an account's messenger/connection

**File:** `src/Iwasug.WhatsApp.Sdk/Accounts/IWhatsAppSessionManager.cs:12-38`

**Issue:** The public manager surface is add/remove/restart/status/events only. The per-account `Messenger` and `WaConnection` live on the `internal` `AccountRuntime`, and `IAccountRuntimeFactory` is `internal` too, so a host that registers accounts through `AddWhatsAppSdk` has no supported way to pair or send on those accounts (it would have to build its own `WaConnection`, which the DI composition does not expose). For a phase whose goal is "host runs tens of concurrent accounts", the manager cannot hand the host the machinery it manages.

**Fix:** Add accessor(s) to the seam, e.g. `bool TryGetMessenger(AccountId accountId, out Messenger messenger)` / `bool TryGetConnection(AccountId accountId, out WaConnection connection)`, backed by `_accounts.TryGetValue`, and document the ownership/disposal contract of the returned objects.

### WR-09: AOT-compatibility claim vs. suppressed `IL2026`/`IL3050` on the configuration overload

**File:** `src/Iwasug.WhatsApp.Sdk/Hosting/ServiceCollectionExtensions.cs:45-51` (project claim: `src/Iwasug.WhatsApp.Sdk/Iwasug.WhatsApp.Sdk.csproj:11`)

**Issue:** The assembly declares `<IsAotCompatible>true</IsAotCompatible>`, yet `AddWhatsAppSdk(IConfiguration, string)` suppresses `IL2026, IL3050` around the reflection-based configuration binding instead of annotating the public method. The suppression hides the analyzer warning from the library build while consumers of this public API get no warning at their call site and can hit runtime failures under native AOT/trimming.

**Fix:** Annotate the overload (and the lambda that calls `BindConfiguration`) with `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`, or drop the `IsAotCompatible` claim / document the overload as AOT-unsafe.

## Info

### IN-01: Stale doc comment on `OutboundEnvelope`

**File:** `src/Iwasug.WhatsApp.Sdk/Accounts/OutboundEnvelope.cs:3` — "payload-free placeholder in Wave 1" contradicts the record's non-nullable-capable `Payload` parameter (line 4). Update the comment (or the type) so the contract matches. (Also noted in `07-VERIFICATION.md` as non-blocking polish.)

### IN-02: `SessionManager._registrations` is dead state

**File:** `src/Iwasug.WhatsApp.Sdk/Accounts/SessionManager.cs:15, 77, 92, 264` — the dictionary is written and removed from but never read anywhere. Remove it, or use it if it was meant to track pending registrations.

### IN-03: Redundant `try { ... } catch { throw; }` wrappers

**File:** `src/Iwasug.WhatsApp.Sdk/Accounts/SessionManager.cs:66-69` and `src/Iwasug.WhatsApp.Sdk/Accounts/DefaultAccountRuntimeFactory.cs:70-73` — both catch-and-rethrow blocks are no-ops. Delete them (the factory's real fail-clean wrapper is the larger try/catch at lines 76-137).

### IN-04: `LinkedCts` and `OutboundQueue` are unwired scaffolding

**File:** `src/Iwasug.WhatsApp.Sdk/Accounts/AccountRuntime.cs:50, 52` — the CTS is created bare (never linked to a manager stop token, never passed to any consumer; only cancel/dispose in `AccountRuntime.DisposeAsync`), and the bounded `OutboundQueue` has no producer or consumer in `src/` (only `TwoAccountRuntimeTests` writes to it). `07-01-PLAN.md` called for "an outbound pump task slot" and "a per-account CancellationTokenSource linked to the manager stop token"; neither exists. Either wire them (pump draining the queue, account token threaded into messenger reads) or mark them explicitly as not yet active.

### IN-05: `ScopedLogger<T>` duplicates `AccountScopedLogger<T>` in the public surface

**File:** `src/Iwasug.WhatsApp.Sdk/Hosting/ScopedLogger.cs:17, 56-63` — two public types with identical constructors and behavior, both locked into `PublicAPI.Unshipped.txt`; also inconsistent sealing (base unsealed, alias sealed). Consider keeping one public type (the plan file-name alias is a reason, not a requirement).

### IN-06: `Messenger.EventWriter` internal seam is unused

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs:481-482` — `EventWriter` has no caller (tests use `TryPublishEvent`, line 475). Drop it or use it in the tests it was added for.

### IN-07: `ReadAccountEventsAsync` throws on first enumeration, not on call, and double-looks-up

**File:** `src/Iwasug.WhatsApp.Sdk/Accounts/SessionManager.cs:240-250` — because this is an async iterator, the `SdkUnknownAccountException` documented on `IWhatsAppSessionManager.ReadAccountEventsAsync` surfaces at the first `MoveNextAsync`, not when the method is called; and `ContainsKey` + `TryGetValue` are two lookups (the second silently swallows a concurrent removal). Document the deferral or use a single `TryGetValue` and throw inside.

### IN-08: `WhatsAppSdkOptions.Validate` reports the wrong parameter name

**File:** `src/Iwasug.WhatsApp.Sdk/Hosting/WhatsAppSdkOptions.cs:60-68, 87, 90` — descriptor failures are thrown as `ArgumentNullException(nameof(options), ...)` / `ArgumentOutOfRangeException(nameof(options), ...)`, naming `options` instead of the offending descriptor/member. This misleads operators diagnosing a bad account row in configuration.

### IN-09: Delegate `AddWhatsAppSdk` overload lacks `ValidateOnStart()`

**File:** `src/Iwasug.WhatsApp.Sdk/Hosting/ServiceCollectionExtensions.cs:26-28` (compare `:47-50`) — the configuration overload validates on start; the delegate overload validates lazily when `IOptions.Value` is first resolved (typically inside `WhatsAppHostedService.StartAsync`), so invalid options surface later and without the standard `OptionsValidationException`-at-start semantics. Add `.ValidateOnStart()` for symmetry.

### IN-10: Plan-mandated lifecycle/health tests are missing, and one existing test encodes CR-02 as an invariant

**File:** `tests/Iwasug.WhatsApp.Sdk.Tests/SessionManager/SessionManagerTests.cs:11-71`, `tests/Iwasug.WhatsApp.Sdk.Tests/Isolation/RateLimitIsolationTests.cs:231-234` — `07-01-PLAN.md` required interleaved add/remove (two threads, 100 iterations, no resurrection, final count zero) and restart-identity/no-CTS-accumulation tests; `07-04-PLAN.md` required recent-failures counting coverage. None exist, which is precisely why WR-01/WR-03 were not caught. Additionally, `Manager_Status_Enumeration_Reflects_Both_Accounts` asserts `CircuitState == Closed`, an assertion that can never fail while CR-02 stands — replace it with a breaker-trip assertion once CR-02 is fixed.

---

_Reviewed: 2026-10-15T21:26:39Z_
_Reviewer: the agent (gsd-code-reviewer)_
_Depth: standard_
