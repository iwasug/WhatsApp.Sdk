---
phase: 04-signal-e2ee-messaging
reviewed: 2026-10-12T00:00:00Z
depth: standard
files_reviewed: 40
files_reviewed_list:
  - NOTICE.md
  - README.md
  - samples/Iwasug.WhatsApp.Sdk.PairingSample/Program.cs
  - src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs
  - src/Iwasug.WhatsApp.Sdk/Domain/MessageKeyRecord.cs
  - src/Iwasug.WhatsApp.Sdk/Domain/PreKeyRecord.cs
  - src/Iwasug.WhatsApp.Sdk/Domain/SignalIdentity.cs
  - src/Iwasug.WhatsApp.Sdk/Iwasug.WhatsApp.Sdk.csproj
  - src/Iwasug.WhatsApp.Sdk/Messaging/DeviceDirectory.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/GroupDirectory.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/MessageBuilder.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/MessagingEvents.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/ReceiptHandler.cs
  - src/Iwasug.WhatsApp.Sdk/Proto/message.proto
  - src/Iwasug.WhatsApp.Sdk/Proto/signal.proto
  - src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt
  - src/Iwasug.WhatsApp.Sdk/Signal/DoubleRatchet.cs
  - src/Iwasug.WhatsApp.Sdk/Signal/PreKeyManager.cs
  - src/Iwasug.WhatsApp.Sdk/Signal/SenderKey.cs
  - src/Iwasug.WhatsApp.Sdk/Signal/SignalCipher.cs
  - src/Iwasug.WhatsApp.Sdk/Signal/X3dh.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/IRecentMessageCacheStore.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Iwasug.WhatsApp.Sdk.Tests.csproj
  - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/MessageCorrelationTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/MessageWireFixtureTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/MessagingEventTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/MessagingLoopbackTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/QuotingTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/ReceiptTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/RecentCachePolicyTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/RetryReceiptTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Signal/PreKeyManagerTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Signal/SenderKeyTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Signal/SignalCoreTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Signal/SignalSessionTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/TestData/message-wire-fixtures.json
  - tests/Iwasug.WhatsApp.Sdk.Tests/TestData/signal-kats.json
findings:
  critical: 2
  warning: 5
  info: 3
  total: 10
status: issues_found
---

# Phase 4: Code Review Report

**Reviewed:** 2026-10-12T00:00:00Z
**Depth:** standard
**Files Reviewed:** 40
**Status:** issues_found

## Summary

Phase 4's Signal E2EE and messaging layer was reviewed end-to-end: X3DH/Double Ratchet/SenderKey crypto, the send/receive dispatcher, the retry exchange, receipts, quoting, the group path, the proto schemas, the sample, and the test suite. The overall engineering quality is high — bounds-checked wire parsing, D-06 persist-before-ack ordering, disciplined zeroization, no secret material in log/assertion surfaces, and NOTICE.md's provenance claims check out against the proto headers (hand-written schemas, Baileys MIT reads pinned to commit 0af2386, whatsmeow behavioral-reference-only). The solution builds clean (0 warnings) and the loopback test tracers are genuinely wire-exact.

However, two critical, cross-cutting defects undermine the phase's core promise — reliably establishing Signal sessions with real peers — and both are masked by the test suite's synthetic-peer construction:

1. **CR-01:** The signed-prekey signature is produced by one key (a separate Ed25519 pairing key) and verified under a different key (the X25519 wire identity imported as Ed25519). No first-contact session establishment can succeed in production, in either direction.
2. **CR-02:** The dispatcher never initializes a Bob session from an inbound `pkmsg` — a plan 04-03 requirement — so first-contact inbound messages are permanently undecryptable; `InitializeBob` has zero production callers.

Warnings cover the retry-keys fallback advertising an undecryptable keypair, non-durable sender-key state, a retry-cap reset that a hostile flood can exploit (ban-safety adjacent), the unimplemented group retry-resend with a stale comment, and a prekey-batch overwrite edge. Info items are minor correctness/documentation notes.

## Critical Issues

### CR-01: Signed-prekey signature is made by a key that no verifier in this codebase (or real WhatsApp) can verify — every fresh session establishment fails

**File:** `src/Iwasug.WhatsApp.Sdk/Signal/X3dh.cs:292-349` (verify), `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs:449,455` and `src/Iwasug.WhatsApp.Sdk/Signal/PreKeyManager.cs:168` (sign), `src/Iwasug.WhatsApp.Sdk/Signal/PreKeyManager.cs:526` + `src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs:608,863` (verify-key selection)

**Issue:** Three facts combine into a deterministic first-contact failure in production:

1. **Sign side:** our own signed prekey is signed with `CredentialRecord.SigningKeyPrivate` — an Ed25519 key generated at pairing for device signatures (`Messenger.cs:449` → `PreKeyManager.GenerateOwnMaterial(..., signingPrivate, ...)` → `X3dh.SignWithIdentity` at `PreKeyManager.cs:168`). The identity we advertise in our bundle is the **X25519** `DeriveX25519Public(row.IdentityKeyPrivate)` (`Messenger.cs:455`).
2. **Verify side:** every production verify-key selection uses the **bundle identity** — `PreKeyManager.cs:526` (`bundleSigningPublicDjb ?? bundle.IdentityKeyDjb`) and `MessageSender.cs:608` (`pinnedDjb ?? bundle.IdentityKeyDjb`) — and `X3dh.VerifySignedPreKey` imports those raw bytes as an Ed25519 public key (`X3dh.cs:328`).

So when any peer verifies our SPK signature under our advertised identity (which is exactly what this SDK does to *their* bundles, and what real WhatsApp clients do with XEdDSA), verification fails: the X25519 identity bytes are not the Ed25519 public of `SigningKeyPrivate`. Symmetrically, real WhatsApp peers sign with XEdDSA and our Ed25519-over-X25519-bytes verification fails against them too. **Every fresh first-contact X3DH — SDK↔SDK and SDK↔real-WA, inbound and outbound — dies at "Signal bundle signed pre-key signature is invalid" (`X3dh.cs:135`).**

The documented XEdDSA note (`X3dh.cs:65-73`, `X3dh.cs:106-113`) tracks the missing *primitive* and claims the design approximates XEdDSA by treating "identity and signing key [as] the same Ed25519 pair" (also asserted in `RetryReceiptTests.cs:86-87`). Production violates even that approximation: the signing key is a **third, unrelated** key pair. The note's promise that "only those two functions change" when XEdDSA lands is also inaccurate — the production signing-key selection (`Messenger.cs:449`) would have to change too. Additionally, the comment at `X3dh.cs:318-321` misstates Baileys: Baileys verifies the skey signature with XEdDSA, not by importing the DJB bytes as plain Ed25519, so this path is not "exactly as Baileys' verify path".

Tests mask all of this by (a) seeding Alice/Bob sessions directly (`MessagingLoopbackTests.cs:115-194`) and (b) passing the synthetic peer's **distinct** Ed signing public as `bundleSigningPublicDjb` (`MessagingLoopbackTests.cs:127`, `PreKeyManagerTests.cs:53`) — a value production can never obtain from a real bundle.

**Fix:** Make the SPK signature verifiable under the advertised identity before live UAT:
- Implement XEdDSA (sign and verify over the birational identity conversion) in the `SignWithIdentity`/`VerifySignedPreKey` seam and sign the SPK with the identity-derived key, dropping `SigningKeyPrivate` from the SPK path; or, as an interim self-consistency fix, sign the SPK with an Ed25519 key whose public is derivable from and published alongside `IdentityKeyPrivate` in the bundle.
- Correct the `X3dh.cs:318-321` Baileys claim and the `X3dh.cs:65-73` note ("only two functions change") so the remaining work is not understated.
- Add a test that runs the *production* upload path (`GenerateOwnMaterial` + `UploadOwnKeysAsync`) and then verifies the uploaded bundle through the *production* `EnsureSessionAsync` verify-key selection — that test would currently fail and would pin the fix.

### CR-02: Inbound first-contact `pkmsg` never initializes a session — the receive path can never decrypt a message from a peer that fetched our prekeys

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs:158-163`

**Issue:** When a message stanza arrives for a sender device with no stored session, the dispatcher emits a Pattern-5 retry receipt and returns:

```csharp
DoubleRatchet? session = await repository.LoadUnlockedAsync(address, cancellationToken).ConfigureAwait(false);
if (session is null)
{
    await TryEmitRetryReceiptAsync(row, from, chat, messageId, timestamp, hasSession: false, cancellationToken).ConfigureAwait(false);
    return; // no session for this sender device: ...
}
```

There is **no code path in `src/` that ever calls `X3dh.InitializeBob` or `DoubleRatchet.InitializeBob`** (grep-verified: the only production callers of the Alice factories are `MessageSender.cs:612` and `PreKeyManager.cs:569`; `InitializeBob` is called only from tests). But this is precisely the scenario the whole prekey-upload machinery exists for: a remote peer fetches OUR bundle, X3DHs, and sends `type="pkmsg"`. The envelope is fully parseable (`SignalCipher.DecryptPreKeyEnvelope` exists) and our SPK/one-time privates are in `IPreKeyStore`, yet the dispatcher discards the message.

The retry loop cannot converge: our retry receipt asks the sender to resend, the resend is still a `pkmsg`, we still have no session, and we emit at most 5 retries then go silent. Plan `04-03-PLAN.md:127` explicitly requires "`pkmsg` → X3DH session init" for the dispatcher — the implemented code does not satisfy it. Every inbound message from a never-before-seen device (the normal case for a freshly linked companion) is permanently lost.

**Fix:** In `HandleMessageAsync`, when `isPreKey && session is null`:
1. Parse the prekey envelope (`SignalCipher.DecryptPreKeyEnvelope`), extract the base key, our signed-prekey id, and one-time-prekey id.
2. Load our matching SPK/OTP private material from `IPreKeyStore` (spend/remove the one-time key before wire per D-06), build `BobSession` via `X3dh.InitializeBob`, and `DoubleRatchet.InitializeBob`.
3. Persist through `repository.SaveUnlockedAsync` **before** the ack (same D-06 contract), then continue the normal decrypt → ack → event → cache flow.
(Subject to CR-01: the peer's SPK signature check in this direction is ours-to-them, so `InitializeBob` itself does not verify — but the session is only trustworthy once CR-01 pins identities correctly.)

## Warnings

### WR-01: Retry-keys fallback advertises a one-time prekey whose private key is immediately destroyed

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/ReceiptHandler.cs:335-351`

**Issue:** `TakeOneTimePreKeyAsync` probes `bundle.OneTimePreKeyId + cursor`; when the slot is empty it mints a fresh X25519 pair, **zeroizes the private** (`line 349`), and returns the public with `ConsumedPreKeyId = -1`. That public is advertised in the retry receipt's `<keys><key id=…>` block. Any peer that X3DHs against it (DH4) produces a session whose receiving half we can never compute — the private no longer exists. The advertised id also names a key the store never held, so a future `InitializeBob` (CR-02's fix) would look it up and find nothing. The cursor starts at 0 per process and only moves forward, so after a re-upload restarts ids at the same checkpoint, *every* subsequent retry hits the mint-and-discard fallback.

**Fix:** Never advertise a key you cannot later decrypt with. Either take the next **actually stored** key (iterate ids from the checkpoint until a stored entry is found), or persist the minted pair into `IPreKeyStore` (with its id) before advertising it and mark it consumed after the wire per D-06.

### WR-02: Sender-key state is process-local — group E2EE material and replay counters are not durable and have no host store seam

**File:** `src/Iwasug.WhatsApp.Sdk/Signal/SenderKey.cs:795` (`SenderKeyRepository._records`), `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs:72`

**Issue:** `SenderKeyRepository` is a `ConcurrentDictionary<string, byte[]>` created fresh per `Messenger`. Unlike 1:1 sessions (which ride `ISignalSessionStore` through `SignalSessionRepository`), sender keys have **no store-registry seam** — inbound SKDMs, the outbound sender key, and cached skipped message keys die with the process. Consequences: after a restart, group messages are undecryptable until each sender redistributes; the outbound sender key rotates silently (peers must wait for a new SKDM fanout); and the skipped-key replay window resets, weakening replay protection. This contradicts the pluggable-store constraint (hosts own persistence) and the D-06 comments in `MessageDispatcher.HandleGroupMessageAsync` ("the sender key persists BEFORE the ack (D-06)") and `SenderKeyRepository.Save` ("Persists the record's serialization"), which read as durable but are process-local.

**Fix:** Add an `ISenderKeyStore` (get/put by address) to `StoreRegistryOptions`, route `TryLoad`/`Save` through it with the same D-06 awaited-write discipline, and keep the in-memory dictionary only as the documented D-03 fallback.

### WR-03: Wholesale retry-counter clear lets a hostile flood reset the Pattern-5 cap for legitimate senders

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/ReceiptHandler.cs:199-202`

**Issue:**

```csharp
if (_retryCounts.Count > MaxRetryCountKeys)
{
    _retryCounts.Clear(); // bounded map: clearing relaxes the cap, never tightens it
}
```

A peer that floods >4096 distinct failing `(sender, id)` pairs forces a clear, resetting the counter for a legitimate `(sender, id)` that had already emitted its 5 capped retries. After each clear the same message can draw another 5 retry receipts — each carrying a `<registration>` child and (from count 2) a `<keys>` block that **spends a real one-time prekey** (line 215-225). The doc comment's "never over-sends … beyond the cap in a contiguous run" concedes the relaxation; combined with prekey spend per retry, this is a ban-safety (v1 requirement, T-4-13) and prekey-exhaustion lever for any hostile sender.

**Fix:** Replace the wholesale clear with bounded eviction that preserves recent/hot entries — e.g., drop the lowest N keys by last-update (per-entry timestamp), or shrink the map to half by removing arbitrary entries while leaving recently-touched counters intact. Cap total wire retries per (sender, id) across clears with a persisted or time-windowed counter if ban-safety requires a hard bound.

### WR-04: Group retry receipts are silently never answered, justified by a stale comment that 04-05 invalidated

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs:507-515`

**Issue:** `ResendForRetryReceiptAsync` refuses any requester whose bare JID does not equal `entry.ChatJid` exactly — correct as a T-4-12 disclosure guard for 1:1 — but the comment states group chats refuse because "no group sends exist yet". 04-05 added `SendGroupMessageAsync` (sender-key fanout, exercised by the sample's `@g.us` path), so that premise is false. The practical effect: a group member who failed to decrypt an `skmsg` (e.g., missing SKDM) sends a retry receipt and is silently ignored — the documented degrade, but now a live functional gap rather than a future-proofing note, and the stale comment will mislead the next maintainer into thinking the gap is pre-group.

**Fix:** Update the comment to state the actual rule (group resends require verifying the requester is a current group member via `GroupDirectory`, which is not wired here) and record group retry-resend as an explicit scoped follow-up rather than implying it cannot arise yet.

### WR-05: Prekey-readiness recompute can overwrite still-live one-time prekeys after a retry-path spend

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs:442-452, 474-494` interacting with `src/Iwasug.WhatsApp.Sdk/Messaging/ReceiptHandler.cs:225`

**Issue:** The retry path consumes a one-time prekey via `TakeOneTimePreKeyAsync`'s cursor (first spend = the checkpoint id) and removes it from the store (`ReceiptHandler.cs:225`). The readiness trigger counts remaining keys by **probing contiguously from the checkpoint id** (`CountRemainingOneTimeKeysAsync`, `Messenger.cs:484`) — the spent checkpoint id now probes null, so `remaining = 0` even though ids checkpoint+1…checkpoint+N are still stored and may already be advertised to peers via served bundles. The next upload starts the new batch at `(bundle?.OneTimePreKeyId ?? 0) + remaining + 1` = checkpoint+1 (`Messenger.cs:451`) and `PutPreKeyAsync` **overwrites** those live stored keys with fresh pairs. Any peer holding a previously served bundle naming an overwritten id will X3DH against a public whose private we just destroyed — their prekey message becomes undecryptable (once CR-02 lands).

**Fix:** Derive the next batch start from the store's actual highest stored id (add a primitive or query) rather than `checkpoint + contiguous-count + 1`, or re-key the batch strictly above any previously issued id. At minimum, never overwrite an existing id — bump until free.

## Info

### IN-01: `TryReadBigEndian` returns a constant `true`

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs:830-839`

**Issue:** The method always returns true; the bool contract is dead (callers at `MessageSender.cs:661-662` combine it with length checks that already guarantee safety). A signature promising failure that never fails invites future callers to rely on it incorrectly.

**Fix:** Make it `void` (or return false when `bytes.Length > 4` so the contract is meaningful).

### IN-02: Retry `<keys>` block wire shape deviates from the real-WA form (acknowledged in-tree)

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/ReceiptHandler.cs:276, 300-301, 322`

**Issue:** The `identity` child carries the raw X25519 public and `device-identity` carries only the DJB-encoded identity, with an in-code note ("ponytail: … upgrade to a marshalled Account proto when the adv schema lands"). On the real wire, `device-identity` is a marshalled account/signature payload, and the identity semantics tie into the XEdDSA identity (CR-01). Fine for loopback, a known fidelity gap for live UAT — worth tracking explicitly so it isn't lost when CR-01's work lands.

**Fix:** Track the Account-proto `device-identity` marshalling as a prerequisite item on the 04-05 live-device UAT checklist, cross-referenced from CR-01.

### IN-03: `DecryptPreKey` replay-dedup fallback reports a less specific failure reason

**File:** `src/Iwasug.WhatsApp.Sdk/Signal/DoubleRatchet.cs:414-420`

**Issue:** While `_peerRatchetPublic == _aliceBaseKey` (no DH-ratchet step yet), the third clause lets any inner ratchet key through to `DecryptRegular`, where a replay fails at the MAC instead of returning `DuplicatePreKey`. Security holds (the replay cannot decrypt), but the typed failure surfaced to the retry machinery and `DecryptionFailed` event is less precise, which weakens diagnosis of replay-vs-tamper in the field.

**Fix:** When the inner ratchet equals the base key and the counter was already consumed, return `Failed(DuplicatePreKey)` directly instead of relying on the MAC path.

---

_Reviewed: 2026-10-12T00:00:00Z_
_Reviewer: the agent (gsd-code-reviewer)_
_Depth: standard_
