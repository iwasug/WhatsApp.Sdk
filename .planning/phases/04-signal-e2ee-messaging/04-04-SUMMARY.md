---
phase: 04-signal-e2ee-messaging
plan: 04-04
type: execute
wave: 4
status: complete
subsystem: messaging
tags: [messaging, receipts, retry, quoting, e2ee, signal, pattern-4, pattern-5, msg-04, msg-05, msg-06]
requires: [04-03-messaging-tracer]
provides: [ReceiptHandler delivery/read/retry emission, MarkReadAsync, ReceiptReceived typed events, DecryptionFailed typed events, Pattern-5 retry exchange with per-(sender,id) cap, same-id cache resend with fresh-session keys rebuild, Messenger.ReplyAsync quoting from the recent cache, MessageReceivedEventArgs.QuotedMessageId]
affects: [04-05-e2ee-uat, phase-5-rate-limiting]
key-files:
  created:
    - src/Iwasug.WhatsApp.Sdk/Messaging/ReceiptHandler.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/ReceiptTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/RetryReceiptTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/QuotingTests.cs
  modified:
    - src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/MessagingEvents.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/MessageBuilder.cs
    - src/Iwasug.WhatsApp.Sdk/Signal/DoubleRatchet.cs
    - src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt
    - tests/Iwasug.WhatsApp.Sdk.Tests/TestData/message-wire-fixtures.json
    - NOTICE.md
decisions:
  - "Delivery receipts are opt-in per chat (SetDeliveryReceipts, off by default); MarkReadAsync sends one wire-exact read receipt per sender with per-message keys (first id in attr, rest as list/item children), idempotent at the wire level"
  - "Retry cap is per (sender, message id): wire receipts carry counts 1-5, the typed DecryptionFailed event never caps; keys block rides retry #2 and the consumed one-time prekey is removed before the node reaches the wire (D-06)"
  - "Retry resend serves ONLY the original 1:1 recipient (T-4-12: requester bare JID must equal the cache entry chat); the recent cache is the ONLY resend source (Pitfall 6); any fault degrades to a logged no-op, never a throw through the receive path"
  - "A retry <keys> block rebuilds a fresh session under the pinned-identity rule (pin = keys identity on first contact, signature verifies under the pin afterwards); malformed or pin-conflicting keys refuse the rebuild"
  - "ReplyAsync reads stanzaId/participant/quoted text/chat from the cache entry only; participant = cached sender whenever present (not group-gated — the wire fixture shows a participant on 1:1 quotes), remoteJid = cached chat always; cache miss / empty reply text / unparseable cached chat reject with SdkFormatException before any node — never a silent downgrade to plain text"
  - "MessageReceivedEventArgs.QuotedMessageId (additive optional param) carries the received message's own wire id so hosts feed it straight back into ReplyAsync"
metrics:
  duration: ~2h46m wall-clock (commit span 20:16→23:02 +0700, 2026-10-11; Task 1 in a prior session, Tasks 2–3 in this one)
  completed: 2026-10-11
commits: 3
plan_head_before: 1f555e21e67808fea3ce88978a4be69a630c563f
plan_head_after: c57c78d3bb9a7fbd87673561730f6a5ca9f42803
actuals:
  tokens: 40765
  tasks: 3
  commits: 3
---

# Phase 04 Plan 04: Receipts, retry receipt exchange, and quoting Summary

Both directions of the receipt protocol travel end-to-end (Pattern-4 wire-exact delivery/read receipts with opt-in gating and per-sender grouping, typed inbound receipt events), a failed decrypt recovers through the Pattern-5 retry exchange (emit with per-(sender,id) cap and keys node from #2, same-id resend re-encrypted from the recent cache including fresh-session rebuild, degrade-never-throw), and replies quoting a cached message build the fixture-pinned ContextInfo payload through the unchanged send path (MSG-04/05/06).

## What was built

### Task 1 — Receipts tracer (commit abebc73)

- `Messaging/ReceiptHandler.cs` — delivery opt-in gate (`SetDeliveryReceipts`, process-local, off by default), `BuildReadReceiptNode`/`BuildDeliveryReceiptNode` (Pattern 4 verbatim: first id in the `id` attribute, the rest as `<list><item/>` children, `participant` attr for group senders only, `t` always present), and `EmitRetryReceiptAsync` (Pattern 5: `<retry count id t v>` + `<registration>` 4-byte big-endian own registration id child).
- `Messaging/MessageDispatcher.cs` — after ack + typed event, a successfully decrypted inbound message triggers exactly one opt-in delivery receipt; every inbound `receipt` node parses bounds-checked into the typed `ReceiptReceived` event (kind, ids from attr + list/item children in wire order, chat, optional group participant, timestamp).
- `Messaging/MessagingEvents.cs` — `ReceiptKind` (delivery/read/played/sender/inactive/retry) + `ReceiptReceivedEventArgs`; unknown receipt types are silently ignored (informational only, T-4-14).
- `Messenger.MarkReadAsync` — one read receipt per sender group with the same-sender-only rule; idempotent at the wire level (duplicate call = identical receipt, no dedup machine).
- `tests/.../Messaging/ReceiptTests.cs` — 5 facts; `TestData/message-wire-fixtures.json` gained the pinned `receiptNodes` block (read-single, read-list, delivery, group); `NOTICE.md` gained the `receipt.go` behavioral-reference row.

### Task 2 — Retry receipt exchange (commit 1f44dfc)

- Emit side (`Messaging/ReceiptHandler.cs` + `MessageDispatcher.cs`): a decrypt failure raises `DecryptionFailedEventArgs` (uncapped, rule-only — no plaintext/ciphertext/key bytes) and sends the wire retry receipt while the per-(sender, message id) count stays ≤ 5; retry #2+ carries a `<keys>` node built from the own prekey store (type/identity/key/skey/device-identity, bounds-checked) with the consumed one-time prekey removed from the store BEFORE the node reaches the wire (D-06).
- Respond side (`Messaging/MessageSender.cs`): `ResendForRetryReceiptAsync` re-encrypts the cached `ResendPayload` for the requester and resends with the SAME id/`t`/type, `device_fanout="false"`, `device-identity` child only for pkmsg; T-4-12 wrong-requester refusal; `BuildFreshSessionFromKeysAsync` rebuilds a fresh session from the receipt's keys block under the pinned-identity rule (pin lands TOFU on first contact, signature verifies under the pin); cache miss / malformed keys / pin conflict degrade to a logged no-op — the whole resend never throws through the receive path.
- `Signal/DoubleRatchet.cs` — `LoadUnlockedAsync`/`SaveUnlockedAsync` seams so the resend rides the per-address session gate without deadlocking the receive path (concurrency-edge truth).
- `tests/.../Messaging/RetryReceiptTests.cs` — 8 facts: Pattern-5 exactness with registration, cap at 5 with uncapped typed event, keys-node-from-#2 with fresh prekey consumption, D-06 removal-before-wire ordering, same-id resend preserving id and original `t`, fresh-session rebuild answering pkmsg with session replacement + identity pin, wrong-requester zero frames with pump continuation, cache-miss degrade with pump continuation.

### Task 3 — Quoting (commit c57c78d)

- `Messenger.ReplyAsync(chat, quotedMessageId, text, ct)` — cache lookup by canonical key, `QuotedMessageRef` (stanzaId = quoted id, participant = cached sender when present, quoted text = cached plaintext, remoteJid = cached chat), `MessageBuilder.BuildQuotedText` payload, then the exact shared send path (gate, fanout, one-network-send contract, outcome, D-20 cache write of the quoted bytes as the reply's own resend source).
- `Messaging/MessageSender.cs` — plaintext-bytes seam: `SendAsync` now delegates to `SendPayloadAsync(chat, displayText, plaintext, ct)` so quoted and plain sends share `SendCoreAsync` (validation, gate, id generation, cache write, encrypt, correlate).
- `Messaging/MessagingEvents.cs` + `MessageDispatcher.cs` — `MessageReceivedEventArgs.QuotedMessageId` (additive optional param) carries the received message's wire id; XML doc names `ReplyAsync` as the consumer.
- `tests/.../Messaging/QuotingTests.cs` — 5 facts: `cache_hit` (parsed ContextInfo equals the cache entry AND the decrypted bytes equal plan 04-02's pinned quote fixture hex end-to-end), `group` (participant from cached sender, remoteJid = group chat), `missing_entry` (unknown id / empty text / unparseable cached chat each reject with `SdkFormatException`, zero frames), `plain_vs_quote` (same id format, same node attrs, divergent payload shape), `non_ascii` (multi-byte quoted and reply text round-trip byte-exact, never normalized).

## Test Results

- `ReceiptTests` — 5/5 passed
- `RetryReceiptTests` — 8/8 passed
- `QuotingTests` — 5/5 passed
- Full suite: **506/510 passed** — the only 4 failures are the known pre-existing Phase-3 `PairingLifecycleTests` timeouts (see below), untouched by this plan.
- Build: `0 Warning(s), 0 Error(s)` under `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror --no-incremental`.

## Acceptance Criteria Verification

- Receipts truths (MSG-04): one decrypted inbound message → exactly one opt-in delivery receipt after its ack and event; `MarkReadAsync` sends one wire-exact read receipt per sender (first id in attr, rest as list children) — asserted in ReceiptTests, node bytes pinned by the new `receiptNodes` fixture block.
- Typed receipt events (MSG-04): kind/ids/chat/participant parsed bounds-checked into `ReceiptReceived`; unknown kinds ignored; duplicates re-raise (T-4-14 informational).
- Retry truths (MSG-06): wire-exact Pattern-5 emit (retry + registration children, keys node from #2, cap = 5 per (sender, id)) while `DecryptionFailed` never caps; peer retry answered with same-id resend from the cache (fresh session when keys ride); degrade to no-resend-no-throw past the horizon or under cache faults — asserted in RetryReceiptTests.
- Ordering/concurrency edges (MSG-06): consumed prekey removed before the retry node reaches the wire; resend rides the per-address session gate (unlocked load/save seams) — asserted in the ordering fact and by construction.
- Quoting truths (MSG-05): fixture-pinned ContextInfo payload (stanzaId/participant/quotedMessage/remoteJid from the cache entry) rides the unchanged send path; cache miss / empty text / unparseable cached chat reject with `SdkFormatException` before any node — asserted in QuotingTests (cache_hit byte-pins the 04-02 fixture).
- `MessageReceivedEventArgs` exposes the quoted-message id and its XML doc names `ReplyAsync` as the consumer (grep-verified in `MessagingEvents.cs`).
- NOTICE provenance: `receipt.go` and `retry.go` behavioral-reference rows added; no whatsmeow bytes anywhere.
- Public API: `PublicAPI.Unshipped.txt` harvested (622 entries) — includes `Messenger.MarkReadAsync`, `Messenger.ReplyAsync`, and the `QuotedMessageId` ctor/Deconstruct/get/init rows.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Critical functionality] `DoubleRatchet` unlocked load/save seams not in the plan's files list**
- **Found during:** Task 2 (concurrency-edge truth: retry handling rides the per-address session gate)
- **Issue:** The plan's files list did not include `Signal/DoubleRatchet.cs`, but responding to a retry receipt while the receive path held the session gate required a load/save path that does not re-acquire the per-address gate (deadlock otherwise).
- **Fix:** Added `LoadUnlockedAsync`/`SaveUnlockedAsync` to `DoubleRatchet.SignalSessionRepository`, used only inside an already-held gate.
- **Files modified:** `src/Iwasug.WhatsApp.Sdk/Signal/DoubleRatchet.cs`
- **Commit:** 1f44dfc

**2. [Rule 1 - Test bug] Retry count-keying and registration-bytes expectations in the Task-2 test craft**
- **Found during:** Task 2 verification
- **Issue:** Two failing frames with DIFFERENT ids can never reach count 2 — the cap is keyed per (sender, message id), so the initial test craft (two distinct ids) could never make the keys node ride. Also the own registration id 777 encodes to big-endian `{0x00, 0x00, 0x03, 0x09}`; the test expectation was wrong, the implementation was correct.
- **Fix:** Tests now share one id across the two failing frames; registration expectation corrected to the true 4-byte big-endian form.
- **Files modified:** `tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/RetryReceiptTests.cs`
- **Commit:** 1f44dfc

### Design Decisions (within plan latitude)

- **participant/remoteJid mapping (Task 3):** `participant` = cached sender whenever present (NOT group-gated — the pinned quote fixture shows a `participant` on a 1:1 quote); `remoteJid` = cached chat JID always (plan-literal, overriding the internal `QuotedMessageRef` doc's "null when it equals the reply chat" hint).
- **Keys-block identity in tests (Task 2):** the self-verifiable `<keys>` craft uses the peer's raw Ed25519 public key as the identity (XEdDSA approximation documented in code and test); the fresh-session fact asserts pkmsg + replaced session bytes + identity pin — NOT a peer mirror-decrypt, which the approximation cannot support.
- **`MessageReceivedEventArgs.QuotedMessageId` semantics (Task 3):** per the plan action text ("feed a received message's id straight back into ReplyAsync"), the field is the RECEIVED message's own wire id — its name reads as "the id to quote when replying to this message".
- **Skipped:** the planned `prekeyFactoryOverride` hook on `BuildRigAsync` proved unnecessary (the rig's `PreKeys` instance is already the registered store); the Rig gained `Identity` + `Sessions` members instead for Task-2 assertions.

## Known Stubs

| Stub | File | Reason / Upgrade path |
|------|------|----------------------|
| Device-identity pin of own identity DJB bytes (carried from 04-03) | `src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs` (`BuildResendNode`) | `ponytail:` the pkmsg `device-identity` child pins the own identity DJB form for both first sends and Pattern-5 resends; upgrade to a marshalled Account proto when the adv schema lands (04-05 UAT / live-peer follow-up). |

## Known Pre-existing Failures (out of scope)

The 4 `PairingLifecycleTests` failures predate this plan (Phase-3 scope, ~20s `WaitForPairingEventAsync` timeouts): `Companion_finish_from_a_rotated_code_epoch_is_rejected`, `Companion_finish_with_foreign_bundle_key_fails_closed`, `Persist_window_suppresses_watchdog_timeout_until_durable_success`, `Companion_hello_ack_finish_reaches_verified_success`. Not touched, not fixed here — tracked for the Phase-3 owner.

## Self-Check: PASSED
