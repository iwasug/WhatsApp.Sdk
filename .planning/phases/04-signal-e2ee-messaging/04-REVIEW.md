---
phase: 04-signal-e2ee-messaging
reviewed: 2026-10-12T10:00:00Z
depth: standard
files_reviewed: 8
files_reviewed_list:
  - src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/ReceiptHandler.cs
  - src/Iwasug.WhatsApp.Sdk/Signal/PreKeyManager.cs
  - src/Iwasug.WhatsApp.Sdk/Signal/X3dh.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/MessagingLoopbackTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/RetryReceiptTests.cs
findings:
  critical: 3
  warning: 3
  info: 5
  total: 11
status: issues_found
---

# Phase 4: Code Review Report (incremental re-review)

**Reviewed:** 2026-10-12T10:00:00Z
**Depth:** standard
**Files Reviewed:** 8
**Status:** issues_found

## Summary

This is the consolidated current-state re-review of the eight files changed since the prior review's base commit `84fc70c` (the CR-01/CR-02/WR-01..WR-05/IN-01 fix pass, commits `089f9a2`..`2a9d1aa`). Prior findings were checked against the fixed code: CR-02 (Bob-init from inbound `pkmsg`), WR-03 (bounded retry-counter eviction), and IN-01 (`TryReadBigEndian`) are genuinely fixed; WR-01, WR-05, and the WR-02/WR-04 comment fixes landed as described. IN-03 was verified-rebutted with sound reasoning (commit-after-MAC discipline makes the suggested guard dead code) and is not re-carried.

However, adversarial re-review of the fixed code surfaces three critical issues, two of them brand-new defects — one introduced by the WR-01 fix's chosen ordering, one a previously-missed concurrency hole — and one prior critical that the disposition marked "fixed" but whose functional gap remains open by the fixer's own scope note:

1. **CR-01 (re-carry, still open):** the signer swap does not change the verification math. Production first-contact signature verification still imports X25519 wire-identity bytes as Ed25519 (`X3dh.cs:339`), so SDK↔SDK and SDK↔real-WA first-contact X3DH still deterministically fails. The fix was preparatory (correct signer chosen, docs now truthful), not remedial.
2. **CR-06 (new):** the send path's load → encrypt → persist is not atomic under the per-address session gate — only per-call gated `LoadAsync`/`SaveAsync` (`MessageSender.cs:853,891`), while the dispatcher holds the gate across its full operation. Concurrent bidirectional traffic on one session can lose ratchet state in either direction.
3. **CR-07 (new):** the MSG-06 retry-with-keys exchange can never converge. The keys block advertises a one-time prekey that is then removed from the store *before the wire* (`ReceiptHandler.cs:234-241`), so the peer's rebuilt prekey message names a key the Bob path can no longer find; the stale-session leg is equally dead because a `pkmsg` with a new base key hard-fails `UnexpectedPreKey` with no rebuild path.

Warnings cover the still-open group retry-resend gap (WR-04 re-carry), EventHandler exceptions violating the documented "swallowed" contract, and the backpressure contract implemented as unbounded task accumulation. Info items are crypto-hygiene, comment-truthfulness, and rotation/edge notes. Test files are consistent with their documented loopback conventions; the synthetic-signing loophole that masks CR-01 is now disclosed in helper docs but the missing production-path round-trip test (the CR-01 fix's own skipped item) remains the reason the suite cannot see CR-01, CR-06, or CR-07.

## Critical Issues

### CR-01: Signed-prekey first-contact verification still cannot succeed in production, in either direction (re-carry; disposition "fixed" is partial)

**File:** `src/Iwasug.WhatsApp.Sdk/Signal/X3dh.cs:325-360` (verify), `src/Iwasug.WhatsApp.Sdk/Signal/PreKeyManager.cs:526` + `src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs:611` (verify-key selection), `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs:461,467` + `src/Iwasug.WhatsApp.Sdk/Signal/PreKeyManager.cs:168` (sign + advertised identity)

**Issue:** The fix pass (commit `089f9a2`) correctly swapped the SPK signer to `row.IdentityKeyPrivate` and made the XEdDSA/Baileys doc claims truthful. But the defect the prior review actually reported — "no first-contact session establishment can succeed in production, in either direction" — is behaviorally unchanged:

- **Sign:** `SignWithIdentity` imports `row.IdentityKeyPrivate` as Ed25519 (`PreKeyManager.cs:168` → `X3dh.cs:377`), producing signature under Ed25519_pub(seed). The bundle advertises `DeriveX25519Public(row.IdentityKeyPrivate)` (`Messenger.cs:467`) — a *different* byte string.
- **Verify:** every production verify-key selection is the wire identity (X25519 bytes) — `bundleSigningPublicDjb ?? bundle.IdentityKeyDjb` at `PreKeyManager.cs:526`, `pinnedDjb ?? bundle.IdentityKeyDjb` at `MessageSender.cs:611` — and `VerifySignedPreKey` imports those bytes as an Ed25519 public (`X3dh.cs:339`). Verifying an Ed25519-seed signature under the X25519 encoding of the same seed fails deterministically.

So `EnsureSessionAsync` for any real peer bundle still throws `SdkFormatException("Signal bundle signed pre-key signature is invalid")` from `InitializeAlice` (`X3dh.cs:140-143`) on every first-contact send, and the inbound Bob path is unaffected only because `InitializeBob` verifies nothing (sender authentication is deferred to the inner MAC). SDK↔SDK is equally dead: both sides verify each other's X25519 identity as Ed25519. The in-code note (`X3dh.cs:65-78`) and the fix report's scope note admit this ("first-contact signature verification still fails SDK↔SDK and SDK↔real-WA until XEdDSA lands"; "the production-path round-trip test... would fail until XEdDSA lands"), so the disposition "fixed" overstates the state: the fix is a necessary *preparatory* step for XEdDSA, not a remediation. Two secondary defects ride along: an `InitializeAlice` signature failure escapes `EnsureSessionAsync` as an undocumented `SdkFormatException` from `SendMessageAsync` (the XML doc lists only malformed-text/JID/format causes), and the TOFU identity pin plus the peer-bundle checkpoint are persisted (`PreKeyManager.cs:535-564`) *before* the verification that is about to fail, leaving durable debris on every failed attempt.

**Fix:**
- Track XEdDSA (sign/verify over the birational identity conversion) as its own planned work item with a hard gate on the 04-05 live-device UAT; when it lands, move the verify-key selection at `PreKeyManager.cs:526`/`MessageSender.cs:611` and the signer selection at `Messenger.cs:461` together (the corrected note already says so).
- Add the skipped production-path round-trip test now (upload via `GenerateOwnMaterial`+`UploadOwnKeysAsync`, then `EnsureSessionAsync`/`TryInitializeBobSessionAsync` with production verify-key selection), marked `Skip` with the CR-01 reason, so the gap is pinned by a failing-by-design test instead of invisible.
- Map the signature-invalid outcome to a typed `EnsureSessionOutcome` (e.g., `FetchFailed`/a new `VerificationFailed`) instead of an unhandled `SdkFormatException`, and move the identity-pin/bundle writes after successful verification (or roll them back on failure).

### CR-06: Send-path ratchet mutation is not atomic under the session gate — concurrent send and receive on one session can lose ratchet state

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs:171-193` (fanout loop), `src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs:849-892` (`EnsureSessionAsync`/`PersistSessionAsync`); cf. `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs:149-156` and `src/Iwasug.WhatsApp.Sdk/Signal/DoubleRatchet.cs:1191-1231`

**Issue:** The per-address `SignalSessionGate` is the ONLY mutual exclusion for ratchet mutations, and the dispatcher honors the "full load → decrypt → persist holds the gate" contract (`MessageDispatcher.cs:156-263`). The send path does not. For each fanout device it runs:

```
EnsureSessionAsync → repository.LoadAsync(address)      // gate acquired AND RELEASED inside the call
ratchet.Encrypt(plaintext)                              // in-memory mutation, no gate held
PersistSessionAsync → repository.SaveAsync(address, …)  // gate acquired AND RELEASED inside the call
```

`LoadAsync`/`SaveAsync` gate only their own single store call (`DoubleRatchet.cs:1194,1229`). Nothing serializes the window between them against the dispatcher, which holds the gate across the same session's load → decrypt → persist. When we send to device D while D's message is being decrypted (any active bidirectional conversation), the two in-memory `DoubleRatchet` instances are deserialized siblings of the same persisted state, and the last `SaveAsync` wins:

- Dispatcher persists receive-chain advance / skipped keys; the send's later `SaveAsync` overwrites with the stale send-side state → the receive counter regresses; subsequent inbound messages stage as skipped keys waiting for a counter that will never re-arrive — the session wedges for receiving.
- Or the send's sending-chain advance is overwritten by the dispatcher's save → `_ns` regresses → we re-use counters the peer already consumed → the peer's decrypt returns `OldCounter` and drops our messages.

The repository doc's own invariant ("a persist failure can never leave a caller holding state newer than what a restart would reload", `DoubleRatchet.cs:1156-1158`) is violated by exactly this lost update, and the dispatcher comment that is supposed to document the contract is factually wrong: `MessageDispatcher.cs:152-153` says "The repository's locked variants are used under the held gate" — the dispatcher uses the *Unlocked* variants; a maintainer "correcting" the code to match that comment would deadlock the non-reentrant gate. No test exercises concurrent bidirectional traffic on one session address, which is why the loopback suite never sees it. (`PreKeyManager.EnsureSessionAsync` has the same shape — its `sessions.LoadAsync`/`sessions.SaveAsync` at lines 509/581 are separately gated with a long network fetch in between.)

**Fix:** Mirror the retry-resend path's pattern (`MessageSender.cs:522-558`, which holds the gate correctly) in the send path: acquire `SignalSessionGate.AcquireAsync(address)` around the full ensure → encrypt → persist per device and use the `Unlocked` repository variants inside; give `PreKeyManager.EnsureSessionAsync` an unlocked-sequence entry point (or run it under the caller-held gate). Delete/correct the `MessageDispatcher.cs:152-153` comment ("Unlocked variants are used under the held gate; the send and retry paths must hold the same gate across their full operation"). Add a loopback test that sends to device D while concurrently decrypting an inbound message from D and asserts both ratchet directions still advance (both sides decrypt after the interleaving).

### CR-07: The MSG-06 retry-with-keys exchange can never converge — the advertised one-time prekey is removed from the store before the wire, and stale sessions never rebuild

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/ReceiptHandler.cs:225-241` (advertise-then-remove), `src/Iwasug.WhatsApp.Sdk/Messaging/ReceiptHandler.cs:390-436` (take/mint returns a "consumed" id); cf. `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs:309-325` and `src/Iwasug.WhatsApp.Sdk/Signal/DoubleRatchet.cs:390-392`

**Issue:** The WR-01 fix made the keys block advertise a *stored-or-minted-and-persisted* key (good), but `EmitRetryReceiptAsync` then removes exactly that key from the store **before** sending the receipt:

```csharp
(keys, consumedPreKeyId) = await BuildKeysBlockAsync(stores.PreKeys, row, cancellationToken);
BinaryNode node = BuildRetryReceiptNode(..., keys);
if (consumedPreKeyId >= 0)
{
    await stores.PreKeys.RemovePreKeyAsync(consumedPreKeyId, cancellationToken); // advertised key is now gone
}
await _owner.Connection.SendNodeAsync(node, cancellationToken);
```

The keys block's whole purpose is to name a fresh one-time prekey for the peer to X3DH against. The peer's `ResendForRetryReceiptAsync` rebuilds a fresh Alice session from it (`MessageSender.cs:525-541` → `BuildFreshSessionFromKeysAsync`) and resends a `pkmsg` naming that prekey id. Our Bob path requires the named one-time key to be *in the store* (`MessageDispatcher.cs:316-322` — `entry?.PrivateKey is not { Length: KeyLength }` → refuse). It never is: it was removed one stanza earlier. The exchange therefore cycles deterministically — retry #1 advertises K (removed), peer rebuilds against K, pkmsg naming K is refused, retry #2 advertises K′ (removed), … up to the Pattern-5 cap, then silence. The fix report flagged this ("a retry-advertised OTK cannot later feed Bob-init") as an out-of-scope observation, but its impact is not a protocol nicety: it renders the documented MSG-06 recovery mechanism — the phase's headline resilience promise — permanently non-functional in the one scenario it exists for (we have no session or a broken session with the sender). The mint path compounds the waste: `TakeOneTimePreKeyAsync` persists a minted pair and the caller immediately deletes it, so the mint is pure churn.

The stale-session leg is equally dead and needs its own half of the fix: when a live-but-stale session receives the peer's rebuilt `pkmsg` (fresh base key), `DecryptPreKey` hard-fails with `UnexpectedPreKey` (`DoubleRatchet.cs:390-392`), and `HandleMessageAsync` only attempts Bob-init when `session is null` (`MessageDispatcher.cs:160`). Real Signal clients replace the session when a prekey message carries a new base key; this SDK has no such path, so even fixing the removal ordering would not make the has-session recovery converge. The loopback suite cannot see either leg: `fresh_session_retry_keys_rebuilds_the_session_and_answers_with_pkmsg` tests only the SDK as *responder* (Alice rebuild) against a synthetic keys block and never closes the loop back through the SDK's Bob path.

**Fix:**
- Stop removing the advertised one-time prekey at emission. Leave it in the store and let the Bob path spend it (`MessageDispatcher.cs:201-206` already removes `spentOneTimeId` after a successful first-contact decrypt — that IS the protocol-correct spend point). Each emission already advertises a distinct key via the `_oneTimeCursor`; serialize `TakeOneTimePreKeyAsync` (e.g., a small lock around take+persist) so two concurrent group-path emissions can never advertise the same id.
- Treat an inbound `pkmsg` whose base key differs from the live session's base key as a session rebuild: under the gate, discard the stale session and run the `TryInitializeBobSessionAsync` flow (the envelope already carries everything needed), persisting the replacement before the ack.
- Add a full-loop test: SDK-side decrypt failure → retry receipt with keys → scripted peer rebuilds via `BuildFreshSessionFromKeysAsync` → `pkmsg` naming the advertised id → SDK Bob-init spends it and decrypts; plus the stale-session variant asserting session replacement.

## Warnings

### WR-04: Group retry receipts are still silently never answered (re-carry; comment fixed, functional gap open)

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs:507-513`

**Issue:** The stale comment was corrected as advertised, and the code now honestly documents the rule: `ResendForRetryReceiptAsync` refuses any requester whose bare JID does not equal the cache entry's chat, so a group member who failed to decrypt an `skmsg` sends a retry receipt that is dropped with no resend and no signal beyond the informational receipt event. Since 04-05, group sends are real traffic, so this is a live functional gap in the documented degrade path — the "scoped follow-up" the comment names does not yet exist as tracked work anywhere in-tree.

**Fix:** Track "group retry-resend with a current-membership check via `GroupDirectory` before serving the cache" as an explicit backlog item (cross-referenced from the comment), or implement it: on a retry for a group-chat cache entry, verify the requester is a current participant, then re-encrypt the cached plaintext through the sender-key path for that member device.

### WR-06: EventHandler handler exceptions are NOT swallowed — the documented handler contract is false, with concrete data-path consequences

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs:27-30` (contract), `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs:329,340,349` (raw `Invoke`), `src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs:446-453` (`DeviceSetChanged` before completion)

**Issue:** The class remarks promise "handler exceptions are logged and swallowed" (and "logged", though this SDK has no logger on any of these paths). The emit points invoke handlers directly with no try/catch. A throwing host handler therefore:

- in `RaiseMessageReceivedAsync`: skips the recent-cache write (D-20) and the event-stream publication for that message, and skips the opt-in delivery receipt — the ack already went out, so the host silently loses quotable/resendable state for a delivered message;
- in `RaiseReceiptReceivedAsync`: prevents `TryResendForRetryReceiptAsync` from ever running, so a peer's Pattern-5 retry goes unanswered;
- in `CompleteFromAck` via `RaiseDeviceSetChanged` (`MessageSender.cs:446-453`): runs BEFORE `pending.Completion.TrySetResult(outcome)`, so a throwing `DeviceSetChanged` subscriber converts a successful server ack into a send timeout — and per the documented idempotency contract the host will re-call with a NEW id, producing a duplicate message.

**Fix:** Wrap each `EventHandler` invocation in try/catch (swallow per the documented contract, or add the `ILogger` seam from the stack and actually log); in `CompleteFromAck`, settle `pending.Completion` before raising `DeviceSetChanged` regardless. Then the doc claim becomes true.

### WR-07: The documented backpressure contract is implemented as unbounded per-stanza task accumulation, not a slowed pump

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs:77-88`, `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs:41-45,119-123,373-383`

**Issue:** `OnStanzaReceived` fires a fresh `Task.Run` per stanza and returns immediately, so the connection's read loop is never throttled by anything. When a `ReadEventsAsync` consumer stalls, `PublishAsync`'s `WriteAsync` blocks the 129th dispatcher task — which for messages blocks *inside the held session gate*, freezing that sender's ratchet processing — while every subsequent inbound stanza keeps spawning new unbounded tasks that queue behind the channel or the gate. The docs' claim "the pump is SLOWED, never dropped" (`Messenger.cs:42-43,120-121`) is not what happens: buffered events are indeed never dropped, but in-flight dispatcher work grows without bound under a slow or stalled consumer, and the 04-05 backpressure fact only proves the channel fills to 128 — it never asserts the spawn rate is bounded. One host bug (a handler draining the stream while doing slow I/O) turns into unbounded memory growth plus frozen receive processing for all senders.

**Fix:** Bound the dispatcher's in-flight work — e.g., a `SemaphoreSlim` of small capacity around `HandleAsync` in `OnStanzaReceived` (awaited inside the `Task.Run` so the read loop still returns quickly but spawn pressure queues bounded), or replace per-stanza `Task.Run` with a single sequential processing loop per connection. Update the contract text to describe the mechanism that actually ships.

## Info

### IN-02: Retry `<keys>` block wire shape deviates from the real-WA form (re-carry, previously skipped)

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/ReceiptHandler.cs:351-375`

**Issue:** The `identity` child carries the raw X25519 public and `device-identity` carries only the DJB-encoded identity (the in-code "ponytail" note). On the real wire, `device-identity` is a marshalled account/signature payload whose semantics tie into the XEdDSA identity (CR-01). Fine for loopback; a known fidelity gap for live UAT.

**Fix:** Carry this as a named prerequisite on the 04-05 live-device UAT checklist alongside the CR-01 XEdDSA item so it is not lost when that work lands.

### IN-04: The DH4 shared secret is never zeroized in either X3DH initializer

**File:** `src/Iwasug.WhatsApp.Sdk/Signal/X3dh.cs:162-164` (Alice), `src/Iwasug.WhatsApp.Sdk/Signal/X3dh.cs:258-260` (Bob)

**Issue:** `Concat(dh1, dh2, dh3, Agree(...))` passes the fourth DH output straight into the concatenation; unlike dh1–dh3 (each zeroized in a dedicated `finally`), the DH4 secret is never wiped and survives until GC. The class's own discipline claim — "every DH output and derived buffer the session does not return is zeroized" (`X3dh.cs:82-84`) — is violated on exactly the one-time-prekey path.

**Fix:** Capture the fourth agreement into a named local and zeroize it in the same `finally` chain (or restructure so `Concat` receives already-owned segments that are wiped immediately after use).

### IN-05: The first-contact Bob path writes a durable identity pin before the inner decrypt, contradicting its own "changes nothing durable" contract

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs:327-331` (pin write) vs `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs:275-277` (comment)

**Issue:** `TryInitializeBobSessionAsync` persists the TOFU identity pin before `InitializeBob`/`Decrypt` run; if the inner decrypt then fails, the session is (deliberately) discarded unpersisted, but the pin REMAINS — so the doc claim "an undecryptable envelope changes nothing durable" is false. Practical impact is limited (the `from` attribute is server-authenticated and the transport is Noise-encrypted, so only the genuine device can pre-pin), but a parseable envelope with a corrupted inner message can pin an identity that later blocks the peer's genuine post-reinstall identity ("sender identity changed under an existing pin") until manual reset.

**Fix:** Move the pin write after the successful inner decrypt (still inside the gate, before persist/ack — the session-persist ordering already guarantees durability), or amend the comment to state that the pin is the one durable change a failed envelope can leave.

### IN-06: Signed-prekey rotation drops in-flight first-contact pkmsgs naming the previous SPK id

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs:306-313` (own-bundle SPK-id match), `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs:462` (id advance)

**Issue:** The own-address bundle checkpoint holds exactly one signed prekey; `EnsureOwnPrekeysAsync` advances `SignedPreKeyId` by one per re-upload. A peer that fetched our bundle (SPK id N) and sends its first-contact `pkmsg` after we rotate to N+1 is refused (`ownBundle.SignedPreKeyId != (int)content.SignedPreKeyId` → null → retry) — and because the retry-with-keys exchange is broken (CR-07), the message is lost rather than recovered. Real implementations keep a short signed-prekey history for exactly this in-flight window.

**Fix:** Retain a small bounded history (id → signed-prekey private) in `IPreKeyStore` and have the Bob path match the envelope's named id against the history, not just the live checkpoint. Coordinate with CR-07's fix, which restores the retry convergence this currently depends on.

### IN-07: `FindFreeOneTimeStartAsync` can return an occupied window on headroom exhaustion, contradicting its "never a silent overwrite" doc

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs:508-555`

**Issue:** WR-05's fix is correct for every realistic layout, but when the scan exits via `start >= scanLimit` (≈1024 bumps — more than 1024 occupied slots in the probe region), the returned `start` is `blockedAt + 1` of the last probe *without a window check*; the subsequent `PutPreKeyAsync` batch then overwrites whatever occupies that window. The doc comment's claim "exceeding it surfaces as the send path's typed id-range failure, never as a silent overwrite" (`Messenger.cs:510-513`) holds only for the id-exhaustion exit (`MaxPreKeyId`), not the headroom exit.

**Fix:** On headroom exhaustion, throw the typed `SdkFormatException` (fail loudly) instead of returning an unchecked `start`, making the code match the doc; keep the id-exhaustion exit as-is.

---

_Reviewed: 2026-10-12T10:00:00Z_
_Reviewer: the agent (gsd-code-reviewer)_
_Depth: standard_
