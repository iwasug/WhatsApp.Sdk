---
phase: 08-broadcast-extended-messaging
reviewed: 2026-10-16T22:00:00Z
depth: standard
files_reviewed: 36
files_reviewed_list:
  - src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/MessagingEvents.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs
  - src/Iwasug.WhatsApp.Sdk/Safety/SafetyGate.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SendPathAuditTests.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/BroadcastTypes.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/Presence/PresenceState.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/Presence/PresenceManager.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/IOutboxStore.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryOutboxStore.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/BroadcastSendTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/PresenceTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Stores/OutboxStoreTests.cs
  - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/OutboxStoreContractTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/InMemoryOutboxStoreContractTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/ScratchOutboxStoreContractTests.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistryOptions.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/ScratchStoreFactory.cs
  - src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt
  - src/Iwasug.WhatsApp.Sdk.Testing/PublicAPI.Unshipped.txt
  - tests/Iwasug.WhatsApp.Sdk.Tests/Safety/ColdOutreachBudgetTests.cs
  - src/Iwasug.WhatsApp.Sdk/Accounts/OutboxPump.cs
  - src/Iwasug.WhatsApp.Sdk/Options/OutboxOptions.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/OutboxPumpTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Isolation/OutboxIsolationTests.cs
  - src/Iwasug.WhatsApp.Sdk/Accounts/AccountRuntime.cs
  - src/Iwasug.WhatsApp.Sdk/Accounts/DefaultAccountRuntimeFactory.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Hosting/HostedServiceLifecycleTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/PresencePacingTests.cs
  - src/Iwasug.WhatsApp.Sdk/Safety/HumanPacing.cs
  - src/Iwasug.WhatsApp.Sdk/Proto/message.proto
  - src/Iwasug.WhatsApp.Sdk/Messaging/MessageBuilder.cs
  - NOTICE.md
  - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/ExtendedMessageEventTests.cs
findings:
  critical: 1
  warning: 6
  info: 4
  total: 11
status: issues_found
---

# Phase 08: Code Review Report

**Reviewed:** 2026-10-16
**Depth:** standard
**Files Reviewed:** 36
**Status:** issues_found

## Summary

Phase 08 adds broadcast fan-out, presence, reaction/edit/delete-for-everyone handling, and the durable outbox pump. The core protocol additions are structurally sound and the full suite (minus four unrelated pairing tests) passes. One lifecycle defect in `OutboxPump` can prevent graceful shutdown, several outbox dispatch edge cases will silently dead-letter extended payload kinds, and a few consistency/robustness gaps were found. Critical-level issue count: 1.

## Critical Issues

### CR-01: OutboxPump background loop ignores its own stop/dispose token, so `DisposeAsync` can hang

**File:** `src/Iwasug.WhatsApp.Sdk/Accounts/OutboxPump.cs:397-468`
**Issue:** `StartAsync` launches `Task.Run(() => RunAsync(cancellationToken), CancellationToken.None)` with the caller's token. `RunAsync` loops only on that caller-supplied token. `StopAsync` and `DisposeAsync` cancel `_ownedCts`, but the running loop never observes it because `_ownedCts.Token` is never linked to (or passed instead of) the run token. Consequently a pump started with a token that is never cancelled will never exit, and `DisposeAsync` will `await _runTask` indefinitely when called during shutdown.
**Fix:**
```csharp
private CancellationTokenSource? _runCts;

public Task StartAsync(CancellationToken cancellationToken = default)
{
    if (_runTask is { IsCompleted: false }) return _runTask;
    _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _ownedCts.Token);
    _runTask = Task.Run(() => RunAsync(_runCts.Token), CancellationToken.None);
    return _runTask;
}

public async ValueTask DisposeAsync()
{
    ...
    _ownedCts.Cancel();
    _runCts?.Cancel();
    ...
    if (_runTask is not null)
        try { await _runTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    ...
}
```

## Warnings

### WR-01: `OutboxPump.DefaultSendAsync` rejects every non-text payload kind despite the store accepting them

**File:** `src/Iwasug.WhatsApp.Sdk/Accounts/OutboxPump.cs:129-145`
**Issue:** `DefaultSendAsync` only switches on `Text`/`GroupText`. An enqueued `Reply`, `Reaction`, `Edit`, `Delete`, or `Media` entry reaches `DefaultSendAsync` and throws `SdkFormatException("Unsupported outbox payload kind")`. `ProcessEntryAsync` then treats the throw as a transient failure, retrying until `MaxAttempts` and finally dead-lettering, instead of actually sending. `IsPermanentFailure` even accepts several of these kinds as valid, so the two code paths are inconsistent.
**Fix:** Either implement each kind through the corresponding `Messenger` method (`SendReactionAsync`, `EditOwnMessageAsync`, `DeleteForEveryoneAsync`, `ReplyAsync`, media sends) in `DefaultSendAsync`, or extend `IsPermanentFailure` so unsupported kinds are dead-lettered immediately with a distinct reason instead of being retried.

### WR-02: `OutboxPump.DisposeAsync` blocks synchronously on store disposal

**File:** `src/Iwasug.WhatsApp.Sdk/Accounts/OutboxPump.cs:456-462`
**Issue:** The store is disposed with `_store.DisposeAsync().AsTask().GetAwaiter().GetResult()`. A store whose `DisposeAsync` needs the calling synchronization context can deadlock; the surrounding method is already async and can await directly. The pump also disposes a store it does not own, which can double-dispose the account-owned store held by `StoreSession`.
**Fix:**
```csharp
try { await _store.DisposeAsync().ConfigureAwait(false); }
catch { /* best-effort */ }
```
and only dispose the store when the pump was constructed with an owned instance (or remove store disposal from the pump and let the `StoreSession` remain the single owner).

### WR-03: `InMemoryOutboxStore` filters due rows against wall-clock time instead of an injected clock

**File:** `src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryOutboxStore.cs:43-65`
**Issue:** `GetDueAsync` uses `DateTimeOffset.UtcNow`, and `ScheduleRetryAsync` stores the pump-supplied `nextAttemptUtc`. The `OutboxPump` drives scheduling from an injected `TimeProvider` (`_clock`). When a host/test supplies a non-system provider (e.g. `FakeTimeProvider`), the store's `DateTimeOffset.UtcNow` does not track it, so retry rows can be permanently due or never due. The same inconsistency affects enqueue ordering timestamps in callers.
**Fix:** Add a `TimeProvider` (default `TimeProvider.System`) to `InMemoryOutboxStore` and to `IOutboxStoreFactory`/`InMemoryStoreFactory`, and use `_clock.GetUtcNow()` in `GetDueAsync` (and, if desired, as the default `EnqueuedUtc`).

### WR-04: `Messenger.SendBroadcastAsync` aborts on the first failure and discards partial results

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs:685-706`
**Issue:** The loop calls `messageFactory(recipient)` and `ExecuteSendAsync` without per-recipient exception isolation. A thrown safety violation, connection error, or factory exception (or an `OperationCanceledException` that is not the host's token) propagates out of the whole method, so callers lose the `BroadcastRecipientOutcome` rows already computed and cannot distinguish which recipients succeeded. Cancellation is the only documented early-exit.
**Fix:** Wrap the per-recipient send in a try/catch that records a failed `SendOutcome` (or a documented exception result) and continues, rethrowing only `OperationCanceledException`. Re-run `cancellationToken.ThrowIfCancellationRequested()` immediately before recording results so host cancellation still stops the loop cleanly.

### WR-05: `Messenger.RaiseMessageReceivedAsync` does not isolate handler exceptions, unlike the new Phase 8 raise methods

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs:754-760`
**Issue:** The new `RaiseReactionReceivedAsync`, `RaiseMessageEditedAsync`, and `RaiseMessageDeletedAsync` wrap `Handler?.Invoke` in try/catch, but `RaiseMessageReceivedAsync` still calls `MessageReceived?.Invoke(this, args)` without a guard, then awaits `PublishAsync`. A throwing host handler aborts the await, so the stream write and any work depending on `RaiseMessageReceivedAsync` completing (the dispatcher's delivery-receipt and cache-write stages run after the await in some paths) are skipped, despite the remarks promising handler exceptions are logged and swallowed.
**Fix:**
```csharp
try { MessageReceived?.Invoke(this, args); }
catch { /* logged by dispatcher contract */ }
```
before the `PublishAsync` await.

### WR-06: `OutboxPump.StartAsync` ignores stop requests that use an unlinked caller token

**File:** `src/Iwasug.WhatsApp.Sdk/Accounts/OutboxPump.cs:396-406`
**Issue:** `_runTask` is created with `Task.Run(() => RunAsync(cancellationToken), CancellationToken.None)`. The pump's `_ownedCts` (used by `StopAsync`) is not linked into `cancellationToken`, so external and internal cancellation are independent. A host that calls `StopAsync()` without cancelling the original token cannot stop the loop; only `StopAsync` + the fix in CR-01 makes the pump controllable.
**Fix:** Addressed together with CR-01 by linked-token creation.

## Info

### IN-01: Unused static `Random _jitter` field is dead code

**File:** `src/Iwasug.WhatsApp.Sdk/Accounts/OutboxPump.cs:19`
**Issue:** `_jitter` is declared but never referenced; jitter is computed with a locally created `Random`. The field also implies (incorrectly) shared locking state.
**Fix:** Remove the field.

### IN-02: Inbound presence state parse mislabels unknown types as `Available`

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs:139-151`
**Issue:** When a `presence` node carries a `type` attribute that is neither `available` nor `unavailable` (or carries none but is actually a chatstate), the handler initializes `state` to `PresenceState.Available` and overrides it only for those two tokens or chatstate children. Hosts therefore receive an availability update for states the SDK does not map.
**Fix:** Initialize to `PresenceState.Unavailable` when no recognizable signal is present, or skip publishing for unmapped types.

### IN-03: `BroadcastOptions` / `SendBroadcastAsync` do not validate a negative inter-recipient delay

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/BroadcastTypes.cs:9-11`, `Messenger.cs:683`
**Issue:** A negative `InterRecipientDelay` is accepted by the record and later passed to `Task.Delay`, which throws `ArgumentOutOfRangeException` at runtime instead of at construction time.
**Fix:** Validate in the `BroadcastOptions` constructor or at the start of `SendBroadcastAsync` (`if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(...)`).

### IN-04: `Testing.StoreContract.FaultingStoreFactory` has no outbox fault-injection seam

**File:** `src/Iwasug.WhatsApp.Sdk.Testing/PublicAPI.Unshipped.txt:113`
**Issue:** The fault-injection decorator supports all eight store capabilities except `IOutboxStore`, even though the outbox store contract suite ships. Hosts writing scratch implementations therefore cannot exercise the "failure ≠ missing" invariant (and the `Mode.None`/fail-reads modes) for their outbox stores. The SDK already exposes the contract base; only the decorator is missing.
**Fix:** Add a `FaultingOutboxStore` wrapper and an `outbox` constructor parameter to `FaultingStoreFactory`, mirroring `FaultingRecipientContactStore`.

---

_Reviewed: 2026-10-16_
_Reviewer: the agent (gsd-code-reviewer)_
_Depth: standard_
