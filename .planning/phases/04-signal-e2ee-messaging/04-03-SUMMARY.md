---
phase: 04-signal-e2ee-messaging
plan: 04-03
type: execute
wave: 3
status: complete
subsystem: messaging
tags: [messaging, e2ee, signal, send-path, receive-path, correlation, cache-policy, prekeys]
requires: [04-01-signal-crypto-foundation, 04-02-message-wire-fixtures]
provides: [Messenger send facade, MessageSender per-device fan-out, MessageDispatcher decrypt-persist-ack pipeline, SendOutcome correlation, DeviceSetChanged phash invalidation, prekey-readiness trigger, RecentMessageCacheStore D-20 policy]
affects: [04-04-receipts, phase-5-rate-limiting]
key-files:
  created: []
  modified:
    - src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/MessagingEvents.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/DeviceDirectory.cs
    - src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs
    - src/Iwasug.WhatsApp.Sdk/Signal/PreKeyManager.cs
    - src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt
    - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/MessagingLoopbackTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/MessageCorrelationTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/RecentCachePolicyTests.cs
decisions:
  - "One logical send = one network send; no silent auto-retry — timeout returns the id and drops (host re-call = new send, new id)"
  - "SendOutcome taxonomy: sent/server-error/timed-out/cancelled, correlated by 40-char id; acked send always settles Sent"
  - "phash device-set pinning: ack mismatch invalidates directory cache + raises typed DeviceSetChanged, never fails the acked send"
  - "Prekey-readiness floor = PreKeyManager.ReuploadThreshold (5, whatsmeow prekeys.go provenance); single-flight per connection via _prekeysEnsured latch"
  - "D-20 cache policy: log-and-continue — cache-write failure never breaks the decrypt→persist→ack path (D-06: ack only after durable session writes)"
metrics:
  duration: ~31 min (commit span 18:42→19:12 +0700, 2026-10-11)
  completed: 2026-10-11
commits: 3
plan_head_before: 6b6b7ab805a2b61fddbfd7f3914d9dceef082eb1
plan_head_after: a0e5f6a6eb30cec7735b0ae8ec5b91aca236aa49
actuals:
  tokens: 48979
  tasks: 3
  commits: 3
---

# Phase 04 Plan 03: End-to-end messaging tracer — send/receive proof, correlation matrix, receive-path policy Summary

Real Messenger/MessageSender/MessageDispatcher replace the wave-0 placeholders: one host send reaches a scripted peer as per-device encrypted enc children that decrypt to the exact plaintext, one inbound message yields exactly one ack-after-persist + one typed event + one cache entry, every send settles exactly one SendOutcome, and the receive path degrades safely under cache-write and prekey-shortage faults (D-20/D-06, MSG-01/03/05/07).

## What was built

### Task 1 — End-to-end send/receive proof (commit 1ae2498)

- `Messaging/MessageSender.cs` — real implementation: 40-char id generation (3EB0 + 18 CSPRNG bytes uppercase hex, ordinal comparison), per-device `enc` node fan-out from `DeviceDirectory` resolution, per-account send serialization, pending-map correlation of send→ack.
- `Messaging/MessageDispatcher.cs` — real implementation with the D-06/D-20 ordering contract: decrypt → durable session persist → **then** ack → typed `MessageReceived` event → recent-cache append. A cache-write failure is logged and swallowed (path continues); decrypt failure is surfaced typed with no ack.
- `Messaging/Messenger.cs` — send facade: validation (null/empty text, unparseable Jid → `SdkFormatException` before any frame leaves the process), one call → one send → one outcome.
- `Messaging/DeviceDirectory.cs` — usync-based peer-device resolution wired into the send path (own-device exclusion preserved).
- `WaConnection.Messaging` — pump dispatch routes `message` nodes into the dispatcher; `MessagingEvents.cs` carries the event args.
- `Domain/DeviceDirectory.cs` placeholder deleted (real implementation lives in `Messaging/`); TEMP-DBG diagnostics removed.
- `MessagingLoopbackTests.cs` — 3 facts proving the full loop: send encrypts per device, receive decrypts to exact plaintext, ack only after durable persist.

### Task 2 — SendOutcome correlation matrix (commit 9509cfb)

- `SendOutcome` + typed warning `DeviceSetChangedEventArgs(Chat, MessageId, ExpectedPhash, ReceivedPhash)` in `MessagingEvents.cs`; XML docs pin the idempotency contract: "one logical send = one network send", "the SDK never re-sends a message on its own".
- Pending map tracks `(chat, phash)` per in-flight id; `CompleteFromAck(messageId, error, t, serverId, phash)` — on phash mismatch the acked send still settles `Sent`, and the SDK invalidates the directory cache for that chat (`_directory.Invalidate`) and raises the typed warning instead of failing the ack.
- `MessageCorrelationTests.cs` — 8 facts: id format (40 chars, 3EB0 prefix), timeout shows exactly one network node, **no auto-retry on timeout**, server-error settles with the code, phash mismatch → invalidation + warning, duplicate ack is a no-op, cancellation releases the gate and a subsequent send succeeds, parallel sends correlate correctly.
- `PublicAPI.Unshipped.txt` synced (567 entries) via the harvest loop.

### Task 3 — D-20 cache policy + prekey-readiness trigger (commit a0e5f6a)

- `RecentCachePolicyTests.cs` — 6 policy facts: log-and-continue on cache-write failure (write observable via cache override), trim horizon enforcement, **no resend after horizon**, prekey-low triggers exactly one upload, prekey-healthy triggers zero uploads.
- `Messenger.EnsureOwnPrekeysAsync` — probes own checkpoint bundle (`{PairedUser}:{Device}@s.whatsapp.net`) by counting consecutive fetchable one-time prekey ids; floor = `PreKeyManager.ReuploadThreshold` (5 — whatsmeow `prekeys.go` provenance comment in code). Below floor → one 50-key `GenerateOwnMaterial` + `UploadOwnKeysAsync`; gate + `_prekeysEnsured` latch = single flight per connection; failures unlatch and surface typed; new ids never reuse old ones (spkId+1, one-time start = first+remaining+1).

## Test Results

- `MessagingLoopbackTests` — 3/3 passed
- `MessageCorrelationTests` — 8/8 passed
- `RecentCachePolicyTests` — 6/6 passed
- Full suite: **487/491 passed** — the only 4 failures are the known pre-existing Phase-3 `PairingLifecycleTests` timeout failures (see below), untouched by this plan.
- Build: `0 Warning(s), 0 Error(s)` under `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror`.

## Acceptance Criteria Verification

- Loopback truths (MSG-01): per-device `enc` children decrypt to exact plaintext; one inbound message → exactly one ack (after durable persist) + one typed event + one cache entry — asserted in the 3 loopback facts.
- Correlation truths (MSG-03): id is 3EB0 + 18 CSPRNG bytes uppercase hex (40 chars, ordinal compare); every send settles exactly one outcome; the timeout proof asserts the scripted server saw exactly one node; grep confirms "one logical send = one network send" and "never re-sends" in the `SendOutcome`/`Messenger`/`MessageSender` XML docs.
- Receive ordering (D-06/D-20): session state persisted before the ack leaves; cache-write failure logged and the path continues — asserted in loopback + policy facts.
- Prekey floor constant provenance: `PreKeyManager.ReuploadThreshold = 5` with the whatsmeow `prekeys.go` comment, referenced by `Messenger.EnsureOwnPrekeysAsync` (grep-verified).
- Empty-input edge: null/empty text or unparseable Jid rejects with `SdkFormatException` before any frame leaves the process.
- Concurrency edge: per-account semaphore serializes sends; a cancelled queued send releases the gate and a subsequent send succeeds (correlation fact).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] DeviceDirectory.IsPhoneDevice misclassified bare JIDs**
- **Found during:** Task 1
- **Issue:** Bare-JID (no device suffix) peer addresses were not recognized as phone devices, breaking per-device key resolution for the common 1:1 case.
- **Fix:** Treat a bare JID as the phone device (default device 0) in the resolver.
- **Files modified:** `src/Iwasug.WhatsApp.Sdk/Messaging/DeviceDirectory.cs`
- **Commit:** 1ae2498

**2. [Rule 1 - Bug] decrypt_failure test constructed a Frankenstein session**
- **Found during:** Task 1 verification
- **Issue:** The decrypt-failure test seeded half the session from one pair and half from another, producing a "failure" that did not exercise the dispatcher's real failure path. Root cause was test-side; the SDK ratchet/dispatcher were correct.
- **Fix:** Mirrored the canonical pair seeding (SDK owns SPK/ED/OT keys; single `sdkBundle` handed to the peer).
- **Files modified:** `tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/MessagingLoopbackTests.cs`
- **Commit:** 1ae2498

**3. [Rule 3 - Blocking] Task-1 send test needed a healthy own prekey batch**
- **Found during:** Task 3 (trigger landed before the Task-1 test was authored against it)
- **Issue:** The Task-1 send test asserted `UploadQueries == 0`, which the new prekey-readiness trigger would violate (it would legitimately upload).
- **Fix:** Send test now seeds a healthy own batch (`SeedOwnPrekeysAsync(preKeys, count: 6, firstId: 200)`) so zero uploads remains the correct expectation.
- **Files modified:** `tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/MessagingLoopbackTests.cs`
- **Commit:** a0e5f6a

## Known Stubs

| Stub | File | Reason / Upgrade path |
|------|------|----------------------|
| Device-identity pin of own identity DJB bytes | `src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs` | `ponytail:` own-identity key bytes are pinned for the identity-check input instead of full DJB marshalling; upgrade when live-peer identity verification (UAT A4 follow-up) requires the exact wire form. |

## Known Pre-existing Failures (out of scope)

The 4 `PairingLifecycleTests` failures predate this plan (Phase-3 scope, ~20s `WaitForPairingEventAsync` timeouts): `Companion_finish_from_a_rotated_code_epoch_is_rejected`, `Companion_finish_with_foreign_bundle_key_fails_closed`, `Persist_window_suppresses_watchdog_timeout_until_durable_success`, `Companion_hello_ack_finish_reaches_verified_success`. Not touched, not fixed here — tracked for the Phase-3 owner.

## Self-Check: PASSED
