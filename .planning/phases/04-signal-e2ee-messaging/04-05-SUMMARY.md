---
phase: 04-signal-e2ee-messaging
plan: 04-05
type: execute
wave: 5
status: complete
subsystem: messaging
tags: [messaging, group-e2ee, sender-key, skdm, events, backpressure, uat, closeout, nyquist, msg-02, msg-07]
requires: [04-04-receipts-retry-quoting]
provides: [sender-key group send path with SKDM pairwise fanout + signed skmsg, GroupDirectory participants-for-fanout IQ, WhatsAppEvent bounded-channel ReadEventsAsync stream, sample-host messaging mode (--send/--reply/--listen/--account), README messaging contract + risk ledger, closed-out 04-VALIDATION with wave_0_complete + nyquist_compliant]
affects: [phase-5-rate-limiting, phase-6-media, phase-8-broadcast-presence, gsd-verify-work]
tech-stack:
  added: []
  patterns: [bounded Channel backpressure (FullMode=Wait, capacity 128), IAsyncEnumerable + EnumeratorCancellation stream, NSec Ed25519 sender-key signatures over 0x33-framed protos]
key-files:
  created:
    - src/Iwasug.WhatsApp.Sdk/Signal/SenderKey.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/GroupDirectory.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Signal/SenderKeyTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/MessagingEventTests.cs
  modified:
    - src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/MessagingEvents.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/MessageBuilder.cs
    - src/Iwasug.WhatsApp.Sdk/Messaging/ReceiptHandler.cs
    - src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt
    - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/MessagingLoopbackTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/RecentCachePolicyTests.cs
    - samples/Iwasug.WhatsApp.Sdk.PairingSample/Program.cs
    - README.md
    - NOTICE.md
    - .planning/phases/04-signal-e2ee-messaging/04-VALIDATION.md
decisions:
  - "Group send = one SKDM per member device (own devices excluded, A3) fanned out pairwise, then ONE signed skmsg per group message with deterministic phash (\"2:\" + first 6 SHA-256 bytes of the sorted participant ad-strings, base64-raw-std); a second send with a live key issues no new SKDM"
  - "Sender-key wire: 0x33 | SenderKeyDistributionMessage/SenderKeyMessage proto | (skmsg only) trailing 64-byte Ed25519 signature; chain KDF HMAC 0x01/0x02; message key = HKDF-64 (\"WhisperGroup\") -> iv[0..16] + AES-256-CBC; record bounds MAX_MESSAGE_KEYS=2000, MAX_FUTURE_GAP=2000, MAX_STATES=5"
  - "ReadEventsAsync exposes exactly four WhatsAppEvent subtypes (MessageReceived/ReceiptReceived/MessageAcked/DecryptionFailed) from the SAME emit sites as the EventHandler twins; one bounded channel (EventChannelCapacity=128, FullMode=Wait) — a slow consumer slows the receive pump, never drops events; connection state stays on WaConnection.StateChanged (CONN-05)"
  - "GroupDirectory resolves participants-for-fanout ONLY (Open Question 7) — group read-side/membership history stays deferred as GRPM-01, named in the README risk ledger"
  - "Sample messaging mode reuses the account already stored in --store <dir> (credentials are one file per account id; a fresh random id would never find them) with --account <id> to pick among several; --send/--reply/--listen all refuse a fresh store (it would attempt registration instead of login)"
  - "04-VALIDATION closes with wave_0_complete + nyquist_compliant true on coverage grounds (every MSG row executed or documented NOT VERIFIED); status stays draft — validate-phase owns it; the 4 pre-existing Phase-3 failures and the dotnet-test-discovers-0 env quirk are reported in the run-date block, not papered over"
metrics:
  duration: ~3h46m wall-clock (plan start 2026-10-11 23:25 -> Task-3 commit 2026-10-12 03:11 +0700; Task 1 + Task 2 + Task 3 in this session)
  completed: 2026-10-12
commits: 4
plan_head_before: d54d40a95b47986887a8321bf69e8e9a92341aa5
plan_head_after: 1e82048946f9469ecb7b4382b5545193f6658c6d
actuals:
  tokens: 56129
  tasks: 3
  commits: 4
---

# Phase 04 Plan 05: Sender-key group E2EE, typed event stream, and phase closeout Summary

Group text travels end-to-end as one signed `skmsg` per message over pairwise SKDM fanout with own-device exclusion and fixture-pinned phash (MSG-02, 8/8 `SenderKeyTests`), the public `ReadEventsAsync` bounded-channel stream ships with end-to-end cancellation, backpressure, and vocabulary parity with the `EventHandler` twins (MSG-07, 5/5 `MessagingEventTests`), and phase 4 closes with the sample-host messaging mode, the README messaging contract + risk ledger, the NOTICE provenance audit, and a Nyquist-complete 04-VALIDATION (SC 1–5 recorded NOT VERIFIED — no live phone in CI).

## What was built

### Task 1 — Sender-key group E2EE tracer (commit 4e34882)

- `Signal/SenderKey.cs` — sender-key record/state model with the bound constants (MAX_MESSAGE_KEYS/FUTURE_GAP/STATES), chain KDF (HMAC 0x01/0x02), message-key HKDF-64 "WhisperGroup" → AES-256-CBC + iv slice, Ed25519 signing/verification (NSec) over the signed portion, and `phash` construction (`"2:" + base64-raw-std(sha256(sorted participant ad-strings)[..6])`).
- `Messaging/GroupDirectory.cs` — minimal `w:g2` group-metadata IQ (shape pinned from Baileys `groupMetadata`, NOTICE row) resolving participants for fanout only; expired/absent `<group>` result is a typed outcome, never a partial fanout.
- `Messaging/MessageSender.cs` group branch — SKDM pairwise fanout to exactly the member device set (own devices excluded), one `skmsg` enc child with the `phash` attr, shared send gate/correlation (one network send, one outcome); a live key skips re-fanout.
- `Messaging/MessageDispatcher.cs` — inbound `skmsg` decrypts through the stored sender key for (group, participant) and resumes the 1:1 ordering contract (persist → ack with `participant` → event → cache); a missing key or bad signature degrades to the capped Pattern-5 retry with NO ack and NO event (T-4-15).
- `tests/.../Signal/SenderKeyTests.cs` — 8 facts: `skdm_distribution` (fanout set + own-device exclusion), `roundtrip`, `tamper` (flipped signature byte → typed failure), `phash` (determinism + set sensitivity), `iteration` (chain advance, out-of-order within bound), `reuse` (no second fanout), plus fixture pins.

### Task 2 — Typed `WhatsAppEvent` stream (commit 9fc9369)

- `Messaging/MessagingEvents.cs` — `WhatsAppEvent` abstract record + exactly four subtypes (`MessageReceivedEvent`, `ReceiptReceivedEvent`, `MessageAckedEvent`, `DecryptionFailedEvent`), each the stream twin of its `EventHandler` args, raised from the SAME internal emit sites (one vocabulary, two consumption shapes).
- `Messaging/Messenger.cs` — `ReadEventsAsync([EnumeratorCancellation])` over one bounded channel (`EventChannelCapacity = 128`, `FullMode = Wait`): a slow consumer slows the receive pump, never drops an event; cancelling the token closes the stream; internal `BufferedEventCount` test seam.
- `Messaging/MessageDispatcher.cs` / `ReceiptHandler.cs` — emit wiring: dispatch remains fire-and-forget per stanza (`Task.Run`) serialized by the per-address `SignalSessionGate`, so stream emission can never stall the pump; ack facts share the `SendOutcome` completion point (no second ack event).
- `src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt` — the new public surface harvested (RS0016 gate).
- `tests/.../Messaging/MessagingEventTests.cs` — 5 required facts: `ordering` (inbound → receipt → outbound causally scripted through one stream), `cancellation` (OCE mid-enumeration, post-cancel probe observes nothing), `backpressure` (300-message burst with a non-advancing enumerator: `BufferedEventCount == 128`, drain yields 300 distinct ids — zero drops), `in_flight` (send cancelled through the same token releases the gate), `vocabulary` (exactly 4 `WhatsAppEvent` subtypes; each stream event mirrors its EventHandler twin).

### Task 3 — End-of-phase closeout (commits d228e2c + 1e82048)

- **Sample messaging mode** (`samples/.../Program.cs`): `--send <jid> <text>` (a `…@g.us` JID routes to `SendGroupMessageAsync`, everything else to `SendMessageAsync`), `--reply <jid> <quoted-id> <text>`, `--listen` (streams `ReadEventsAsync` until Ctrl+C — after a send this keeps the receipt round trip observable), `--account <id>` (multi-account store dirs); messaging flags require `--store <dir>` with paired credentials, refuse `--self-test`/`--code` combinations, and refuse a fresh store before connecting (registration-instead-of-login guard). Argument-validation paths smoke-tested; `--self-test` regression green.
- **README** — messaging section: send/receive/group/receipts/quotes/events, the idempotency contract (one logical send = one network send; `TimedOut` carries the id and drops; retry receipts resend the SAME id only inside the cache horizon — the documented no-resend outcome past it; cancellation is not an outcome), the A3 own-device-exclusion note, the honest risk ledger (signed-prekey rotation not yet periodic; group read-side deferred GRPM-01; presence/`inactive` Phase 8; pacing = Phase 5), and the sample messaging-mode arguments.
- **NOTICE** — audit pass: every 04-RESEARCH Sources read now has a provenance row (10/10 greps ≥1: `retry.go`, `receipt.go`, `WAProto.proto`, `user.go`, `message.go`, `send.go`, `prekeys.go`, `groupMetadata`, `signal-utils`, `generics.ts`).
- **04-VALIDATION** — MSG-01…MSG-07 rows mapped to executing task IDs with threat refs matching each declaring plan's register (04-01 → T-4-01–04 … 04-05 → T-4-15–17), File-Exists/Status columns green with executed evidence, Wave 0 checklist closed with producing tasks, UAT-1…UAT-5 (+ flagged assumptions A2/A4) recorded NOT VERIFIED with reasons, sign-off noting the gate caveats; frontmatter `wave_0_complete: true`, `nyquist_compliant: true`, `status: draft` untouched.
- **Flake fix (Rule 1):** `RecentCachePolicyTests` read cache state the instant `ack:` hit the peer log, but the write + horizon purge run strictly AFTER the ack (D-06: persist → ack → event → cache) — `no_resend_after_horizon…` lost that race under full-suite load. New `WaitForCacheStateAsync` polls with the established `WaitForAsync` TimeoutException shape; the fault-path event count now waits via `WaitForCountAsync`.

## Test Results

- `SenderKeyTests` — 8/8 passed (04-05 Task 1)
- `MessagingEventTests` — 5/5 passed (04-05 Task 2, 0.9s)
- `RecentCachePolicyTests` — 6/6 passed after the race fix (04-05 Task 3)
- Full suite (compiled runner): **523 total, 4 failed, 0 skipped, 82.5s** — the only 4 failures are the known pre-existing Phase-3 `PairingLifecycleTests` timeouts (see below), untouched by this plan.
- Build: `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror --nologo` → **0 Warning(s), 0 Error(s)** (re-run after Task-3 sample changes).
- Sample smoke: `--send` without `--store` → typed error; `--listen` on an empty store → "no paired credentials"; no-args usage lists the messaging flags; `--self-test` → SELF-TEST OK.

## Acceptance Criteria Verification

- **MSG-02 (group E2EE):** SKDM fanout to exactly the member device set with own devices excluded, one signed `skmsg`, deterministic fixture-pinned phash, tamper → typed failure with no event/ack, chain iteration + reuse-no-refanout — asserted in `SenderKeyTests` (8/8); `grep -c "groupMetadata" NOTICE.md` ≥ 1.
- **MSG-07 (typed events):** `ordering`, `cancellation`, `backpressure`, `in_flight`, `vocabulary` test names present and passing; bounded channel never drops an event (300/300 drained at capacity); cancellation kills enumeration AND in-flight sends through one token.
- **NOTICE audit:** `retry.go` 1, `receipt.go` 1, `WAProto.proto` 2 (plan floor ≥1 each), plus `user.go` 1 and `message.go` 1 added this task (plan-required rows were missing — Rule 2 below).
- **README:** messaging section contains the idempotency contract, the cache-horizon no-resend outcome, the A3 own-device-exclusion note, the risk ledger naming the signed-prekey rotation gap and the deferred group read-side (GRPM-01), and the sample messaging-mode arguments.
- **Sample + UAT:** messaging mode built and documented; UAT-1…UAT-5 (+A2/A4) recorded in 04-VALIDATION as NOT VERIFIED with reasons — no device result fabricated.
- **VALIDATION flip:** `grep -c "MSG-0"` → 15 (≥7); every row carries an executed automated verify or a documented manual entry; `wave_0_complete: true`, `nyquist_compliant: true`.
- **Full-suite gate (reported, not papered over):** plan verify expects `dotnet test …` exit 0 — see deviation 3 for why that cannot hold here; executed evidence is the compiled-runner suite at the known 4-failure baseline with 0 skips.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Test bug] Post-ack cache-state race in `RecentCachePolicyTests`**
- **Found during:** Task 3 automated gate (full-suite run showed `no_resend_after_horizon_purged_entry_has_no_resend_source` FAIL against the 4-failure baseline)
- **Issue:** The tests waited for `ack:…` in the peer log and then read the recent cache — but the cache write and horizon purge run strictly after the ack (D-06 ordering), so under full-suite load the assertion observed pre-purge state. Three sites shared the same latent race (`no_resend…`, `retention_trim…`, `cache_write_success…`) plus one event-count read.
- **Fix:** Added `WaitForCacheStateAsync` (poll + established `WaitForAsync` TimeoutException shape) at all three store-state sites; the fault-path event count now waits through `WaitForCountAsync` before asserting exactness. `RecentCachePolicyTests` 6/6, full suite back to the 4-failure baseline.
- **Files modified:** `tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/RecentCachePolicyTests.cs`
- **Commit:** d228e2c

**2. [Rule 2 - Missing critical] NOTICE provenance rows for whatsmeow `user.go` and `message.go`**
- **Found during:** Task 3 NOTICE audit (`grep -c` for each 04-RESEARCH Sources read)
- **Issue:** The plan requires a NOTICE row for every upstream read the phase recorded — `user.go` (USync device-list IQ shape, Primary) and `message.go` (inbound `enc` v=2 skmsg/msg/pkmsg dispatch, Secondary) had none.
- **Fix:** Added both rows in the established format naming their destination files (`Messaging/DeviceDirectory.cs`, `Messaging/MessageDispatcher.cs` + `ReceiptHandler.cs`).
- **Files modified:** `NOTICE.md`
- **Commit:** 1e82048

**3. [Rule 3 - Documented] Full-suite gate cannot report `dotnet test` exit 0**
- **Found during:** Task 3 automated gate
- **Issue:** Two independent conditions: (a) `dotnet test Iwasug.WhatsApp.Sdk.sln` discovers 0 tests in this environment (exit 5 — MTP runner quirk documented since Phase 1, WINDOWS id 2); (b) the suite carries 4 pre-existing Phase-3 `PairingLifecycleTests` timeout failures (04-04-SUMMARY precedent). Neither is introduced by this plan.
- **Fix:** Executed the gate through the compiled runner (`dotnet exec …Tests.dll`) — build 0/0 under `-warnaserror`, suite 523/4/0-skipped with every MSG class green — and recorded both conditions in the 04-VALIDATION run-date block, the sign-off, and WINDOWS ledger id 14 (unrun-verify). Not papered over (03-VALIDATION precedent).
- **Files modified:** `.planning/phases/04-signal-e2ee-messaging/04-VALIDATION.md`
- **Commit:** 1e82048

### Design Decisions (within plan latitude)

- **Sample messaging flags:** the plan names `--send <jid> <text>` and `--listen`; `--reply <jid> <quoted-id> <text>` (needed to exercise SC 4 quoted replies) and `--account <id>` (needed because pairing mints a fresh account id per run while credentials are one file per id) were added alongside. Group-send is served by routing `…@g.us` JIDs in `--send` to `SendGroupMessageAsync` — public API only.
- **Messaging-mode dispatch precedence:** messaging flags run before the `--store`-alone persistence test so a previously paired store is never wiped by the store round-trip cycle.
- **UAT honesty:** no live phone exists in this environment; SC 1–5 and assumptions A2/A4 are recorded NOT VERIFIED with reasons and the human end-of-phase gate named — never fabricated.

## Known Stubs

| Stub | File | Reason / Upgrade path |
|------|------|----------------------|
| Device-identity pin of own identity DJB bytes (carried from 04-03/04-04) | `src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs` (`BuildResendNode`) | `ponytail:` the pkmsg `device-identity` child pins the own identity DJB form; upgrade to a marshalled Account proto when the adv schema lands (WINDOWS ids 10/12). Group resend rides the same node builder. |
| GroupDirectory participants-for-fanout only | `src/Iwasug.WhatsApp.Sdk/Messaging/GroupDirectory.cs` | Intentional deferred scope, not a stub: GRPM-01 group read-side is explicitly out of this phase and named in the README risk ledger. |
| Sample messaging mode not live-exercised | `samples/Iwasug.WhatsApp.Sdk.PairingSample/Program.cs` | Argument validation smoke-tested; live send/receive awaits the end-of-phase human gate (WINDOWS id 15, NOT VERIFIED in 04-VALIDATION). |

## Known Pre-existing Failures (out of scope)

The 4 `PairingLifecycleTests` failures predate this plan (Phase-3 scope, ~20s `WaitForPairingEventAsync` timeouts): `Companion_finish_from_a_rotated_code_epoch_is_rejected`, `Companion_finish_with_foreign_bundle_key_fails_closed`, `Persist_window_suppresses_watchdog_timeout_until_durable_success`, `Companion_hello_ack_finish_reaches_verified_success`. Not touched, not fixed here — tracked for the Phase-3 owner.

## Threat Flags

None — Task 3 adds no network endpoints, auth paths, or schema changes (CLI flags on the sample host only); Tasks 1–2 landed under the declared T-4-15–T-4-17 register with their mitigations asserted in `SenderKeyTests`/`MessagingEventTests`.

## Self-Check: PASSED
