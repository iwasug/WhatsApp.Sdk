---
phase: 08-broadcast-extended-messaging
fixed_at: 2026-10-16T20:01:34Z
review_path: .planning/phases/08-broadcast-extended-messaging/08-REVIEW.md
iteration: 1
findings_in_scope: 11
fixed: 11
skipped: 0
status: all_fixed
---

# Phase 08: Code Review Fix Report

**Fixed at:** 2026-10-16T20:01:34Z
**Source review:** .planning/phases/08-broadcast-extended-messaging/08-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 11 (fix_scope: all — 1 critical, 6 warnings, 4 info)
- Fixed: 11 (10 atomic commits; CR-01 and WR-06 share one commit because they are the same linked-token fix)
- Skipped: 0

## Verification

- **Tier 2 (build) per fix and final:** `dotnet build` on `Iwasug.WhatsApp.Sdk.csproj` (and `Iwasug.WhatsApp.Sdk.Testing.csproj` for IN-04) — **0 warnings, 0 errors**, including the Roslyn PublicApiAnalyzers against both updated `PublicAPI.Unshipped.txt` files and the CS1591 XML-doc gate.
- **Full test suite (run once after all commits, in the isolated worktree):** `dotnet exec Iwasug.WhatsApp.Sdk.Tests.dll` → **Total: 713, Errors: 0, Failed: 4**. The 4 failures are all `Iwasug.WhatsApp.Sdk.Tests.Pairing.PairingLifecycleTests.*` — the same four unrelated pairing failures the source review already documented as pre-existing and out of Phase 08's scope. Every in-scope suite passes: OutboxPumpTests, HostedServiceLifecycleTests, OutboxStoreContractTests (incl. FakeTimeProvider due-ness), BroadcastSendTests, PresenceTests, and the Testing-kit compliance/contract suites.
- **Where verification ran:** inside the isolated review-fix worktree (`.claude/worktrees/rf-08-21716-1792153713`, branch `gsd-reviewfix/08-21716`), not the main checkout. The `dotnet test` MTP runner reports "Zero tests ran / error: 1" in this environment identically in the **main checkout** as well (pre-existing tooling quirk, reproduced at the base commit) — the direct `dotnet exec` run of the built test DLL is the authoritative count above.
- **Tier 1 (re-read)** was performed for every fix before its commit; surrounding code was confirmed intact each time.
- Logic-classified fixes are labeled `fixed: requires human verification` below (cancellation wiring, send routing, disposal ownership, and broadcast error semantics are judgment calls, not mechanical edits).

## Fixed Issues

### CR-01: OutboxPump background loop ignores its own stop/dispose token, so `DisposeAsync` can hang

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Accounts/OutboxPump.cs`
**Commit:** `45c6559`
**Applied fix:** `StartAsync` now launches the loop with `CancellationTokenSource.CreateLinkedTokenSource(callerToken, _ownedCts.Token)` so `StopAsync`/`DisposeAsync` can always stop it (also resolving WR-06). `DisposeAsync` cancels the linked source directly in addition to the owned CTS and disposes it after the run task awaits. `StopAsync` cancels the current run's linked token while leaving the owned stop token intact so a start-after-stop remains possible.
**Commit status:** `fixed: requires human verification` (lifecycle wiring + restart semantics are semantic decisions)

### WR-06: `OutboxPump.StartAsync` ignores stop requests that use an unlinked caller token

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Accounts/OutboxPump.cs`
**Commit:** `45c6559` (same commit as CR-01 — the review's own note: "Addressed together with CR-01 by linked-token creation")
**Applied fix:** covered by the CR-01 linked-token creation.
**Commit status:** `fixed: requires human verification` (shared with CR-01)

### WR-01: `OutboxPump.DefaultSendAsync` rejects every non-text payload kind despite the store accepting them

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Accounts/OutboxPump.cs`
**Commit:** `b38b66c`
**Applied fix:** Implemented the reviewer's primary option — `DefaultSendAsync` now routes `Reply`/`Reaction`/`Edit`/`Delete` through the corresponding gated Messenger sends (`ReplyAsync`, `SendReactionAsync`, `EditOwnMessageAsync`, `DeleteForEveryoneAsync`). For `Media` (whose rows never carry bytes, T-08-03, so no default send is possible) the default path throws `SdkFormatException`; `ProcessEntryAsync` now catches `SdkFormatException` and dead-letters immediately with a distinct `malformed-payload: …` reason instead of retrying to `MaxAttempts` — the reviewer's alternative applied to the one kind that truly cannot be sent. `IsPermanentFailure` additionally requires `Reply` rows to carry a target, closing the permanent-precheck/send inconsistency the review flagged. Hosts that enqueue media rows register a send delegate; a custom delegate is unaffected because `IsPermanentFailure` still accepts media rows.
**Commit status:** `fixed: requires human verification` (send routing + malformed→permanent semantics)

### WR-02: `OutboxPump.DisposeAsync` blocks synchronously on store disposal

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Accounts/OutboxPump.cs`
**Commit:** `57b6523`
**Applied fix:** Removed store disposal from the pump entirely (the reviewer's second option), so neither the sync-block deadlock nor the double-dispose of the account-owned store (pump drained `session.Outbox` while `AccountRuntime.DisposeAsync` disposes both the pump and the session) can occur. Ownership stays with the creator: StoreSession in the SDK composition, the host for standalone pumps.
**Commit status:** `fixed: requires human verification` (ownership policy change)

### WR-03: `InMemoryOutboxStore` filters due rows against wall-clock time instead of an injected clock

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryOutboxStore.cs`, `src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs`, `src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt`
**Commit:** `187518b`
**Applied fix:** `InMemoryOutboxStore` takes an optional `TimeProvider?` (default `TimeProvider.System`) and `GetDueAsync` filters against `_clock.GetUtcNow()`. `InMemoryStoreFactory` gains an injectable clock (parameterless constructor preserved) threaded into the account-scoped outbox views, so a `FakeTimeProvider` drives store due-ness in step with the pump. The new factory constructor was added to `PublicAPI.Unshipped.txt`.
**Commit status:** `fixed`

### WR-04: `Messenger.SendBroadcastAsync` aborts on the first failure and discards partial results

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs`, `src/Iwasug.WhatsApp.Sdk/Messaging/MessagingEvents.cs`, `src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt`
**Commit:** `5cc4be0`
**Applied fix:** The per-recipient send (messageFactory + gated send) is isolated in a try/catch: a non-cancellation throw is recorded as that recipient's `SendOutcome(string.Empty, SendOutcomeStatus.Failed)` and the fan-out continues; `OperationCanceledException` still propagates (cancellation remains the only documented early-exit) and `ThrowIfCancellationRequested` is re-run immediately before recording so host cancellation stops the loop cleanly. A new public `SendOutcomeStatus.Failed = 3` member documents the locally-failed attempt (no server reply, empty message id) and is registered in `PublicAPI.Unshipped.txt`.
**Commit status:** `fixed: requires human verification` (public enum addition + error-isolation semantics)

### WR-05: `Messenger.RaiseMessageReceivedAsync` does not isolate handler exceptions, unlike the new Phase 8 raise methods

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs`
**Commit:** `c51eee7`
**Applied fix:** Wrapped `MessageReceived?.Invoke(this, args)` in try/catch before the `PublishAsync` await, giving it the same logged-and-swallowed handler guarantee as `RaiseReactionReceivedAsync`/`RaiseMessageEditedAsync`/`RaiseMessageDeletedAsync`, so a throwing host handler can no longer abort the stream write or skip dispatcher work that depends on the raise completing.
**Commit status:** `fixed`

### IN-01: Unused static `Random _jitter` field is dead code

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Accounts/OutboxPump.cs`
**Commit:** `312d1f3`
**Applied fix:** Removed the never-referenced field and the redundant `lock` (Backoff already created a fresh seeded `Random` inside it). The deterministic per-attempt jitter factor is unchanged.
**Commit status:** `fixed`

### IN-02: Inbound presence state parse mislabels unknown types as `Available`

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs`
**Commit:** `6ab0c7f`
**Applied fix:** Implementing the reviewer's "skip publishing for unmapped types" option: the handler publishes only recognized signals — `presence` with `type=available`/`unavailable`, a bare `<presence>` node (wire semantics: available), or a recognized chatstate child (composing/recording/paused). An unmapped `type` attribute or an unrecognized chatstate shape is skipped instead of surfacing a synthetic `Available` update.
**Commit status:** `fixed: requires human verification` (host-visible event semantics)

### IN-03: `BroadcastOptions` / `SendBroadcastAsync` do not validate a negative inter-recipient delay

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs`
**Commit:** `5a0cf65`
**Applied fix:** Implemented the reviewer's boundary-check option: `SendBroadcastAsync` resolves the delay once at entry and throws `ArgumentOutOfRangeException(nameof(options), …)` for negative values before any recipient is contacted, instead of a mid-fan-out `Task.Delay` failure. The record shape (and PublicAPI) is unchanged.
**Commit status:** `fixed`

### IN-04: `Testing.StoreContract.FaultingStoreFactory` has no outbox fault-injection seam

**Files modified:** `src/Iwasug.WhatsApp.Sdk.Testing/FaultInjection/FaultingStoreFactory.cs`, `src/Iwasug.WhatsApp.Sdk.Testing/PublicAPI.Unshipped.txt`
**Commit:** `6fe3da4`
**Applied fix:** Added the reviewer's suggested `FaultingOutboxStore` wrapper (mirroring `FaultingRecipientContactStore`: `GetDueAsync` fails under `FaultMode.FailReads`, enqueue/mark-sent/schedule-retry/dead-letter/remove under `FaultMode.FailWrites`, `Mode.None` passes through) plus the `IOutboxStoreFactory` implementation and an `outbox` constructor parameter on `FaultingStoreFactory`. The Testing `PublicAPI.Unshipped.txt` was updated with the new constructor signature and all nine new public members; the PublicApiAnalyzers accept both files.
**Commit status:** `fixed`

## Skipped Issues

None — all in-scope findings were applied.

---

_Fixed: 2026-10-16T20:01:34Z_
_Fixer: the agent (gsd-code-fixer)_
_Iteration: 1_
