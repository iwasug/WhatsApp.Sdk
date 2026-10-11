---
phase: 04
fixed_at: 2026-10-12T06:05:24+07:00
review_path: .planning/phases/04-signal-e2ee-messaging/04-REVIEW.md
iteration: 1
findings_in_scope: 10
fixed: 8
skipped: 2
status: partial
---

# Phase 4: Code Review Fix Report

**Fixed at:** 2026-10-12T06:05:24+07:00
**Source review:** `.planning/phases/04-signal-e2ee-messaging/04-REVIEW.md`
**Iteration:** 1

**Summary:**
- Findings in scope: 10 (fix_scope=all: 2 critical, 5 warning, 3 info)
- Fixed: 8 (6 with logic changes flagged for human verification, 2 comment/doc-only)
- Skipped: 2

**Where verification ran:** all builds and tests ran inside the isolated worktree
`.claude/worktrees/rf-04-18016-20261012050026` on branch `gsd-reviewfix/04-18016`
(commits were fast-forwarded onto `master` after cleanup). Numbers below are
reproducible from that tree state, not necessarily from a stale main checkout.

## Fixed Issues

### CR-01: Signed-prekey signature is made by a key that no verifier can verify

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs`,
`src/Iwasug.WhatsApp.Sdk/Signal/PreKeyManager.cs`, `src/Iwasug.WhatsApp.Sdk/Signal/X3dh.cs`,
`tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/RetryReceiptTests.cs`
**Commit:** 089f9a2
**Applied fix:** SPK is now signed with `row.IdentityKeyPrivate` instead of the
X25519 `row.SigningKeyPrivate` (signer swap — the signature is now made by the
Ed25519 identity key a verifier actually holds). Parameter docs and the XEdDSA
note corrected: the change is NOT confined to two functions, verification still
fails until real XEdDSA (Ed25519-via-X25519) lands, the false "UAT-validated"
claim was removed, and the Baileys claim was corrected. Loopback test helper doc
notes the round-trip is only valid under the loopback signing convention.
**Verification status:** `fixed: requires human verification` — crypto logic.
**Scope note:** Full XEdDSA was deliberately NOT implemented (STACK.md forbids
hand-rolling new crypto primitives; the wire identity stays X25519 and the
Ed25519 public key is not derivable from it), so the real-WhatsApp verify path
remains non-functional until XEdDSA is planned as its own work item. The
production-path round-trip test suggested by the review was skipped because it
would fail until XEdDSA lands.

### CR-02: Inbound first-contact `pkmsg` never initializes a session

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs`,
`tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/MessagingLoopbackTests.cs`
**Commit:** e807907
**Applied fix:** Dispatcher now calls the new `TryInitializeBobSessionAsync` when
`isPreKey && session is null`: envelope parse fails closed → registration-id
range check → TOFU identity pin (refuse on mismatch, pin if absent) → own-address
bundle check with SPK-id match and private key → required OTK lookup when named →
`X3dh.InitializeBob` + `DoubleRatchet.InitializeBob`. The spent OTK is removed
only after successful inner decrypt (before persist/ack so retries converge), an
unpersisted session is discarded if inner decrypt fails. New loopback test
`first_contact_pkmsg_initializes_the_bob_session_spends_the_prekey_and_decrypts`
asserts ack, event, persisted session, spent prekey 1101, pinned identity, and
follow-up message decryption.
**Verification status:** `fixed: requires human verification` — session-establishment logic.

### WR-01: Retry-keys fallback advertises a one-time prekey whose private key is immediately destroyed

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Messaging/ReceiptHandler.cs`
**Commit:** 9c69eae
**Applied fix:** `TakeOneTimePreKeyAsync` probes upward (`MaxOneTimeProbe=64`) for
an actually-stored key; an empty window mints AND persists (`PutPreKeyAsync`) at
the first observed-free id before advertising, returning the consumed id; ids
exhausted → `(null, 0, -1)` degrade; `BuildKeysBlockAsync` handles the degrade by
sending a keys-less block.
**Verification status:** `fixed: requires human verification` — prekey lifecycle logic.
**Observation for 04-05 (not changed):** the retry path removes a prekey from the
wire advertisement before removal (D-06, pinned by tests), so a retry-advertised
OTK cannot later feed Bob-init. Flagged as a protocol follow-up, out of scope here.

### WR-02: Sender-key state is process-local — no host store seam

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs`
**Commit:** 2a9d1aa
**Applied fix:** Comment-only clarification (the `ISenderKeyStore` seam itself was
deliberately skipped — see Skipped-adjacent note below): both dispatcher comments
now state that sender-key durability is process-local (`SenderKeyRepository` is
in-memory) and that the `ISenderKeyStore` seam is deferred to v2.
**Verification status:** `fixed` (comment-only, no logic change).
**Scope note:** The actual public-API seam was not added — a new public store
interface is a v2 API decision already documented in SenderKeyRepository docs, not
a review-driven hotfix.

### WR-03: Wholesale retry-counter clear lets a hostile flood reset the cap

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Messaging/ReceiptHandler.cs`
**Commit:** 92b9536
**Applied fix:** `_retryCounts` is now a `ConcurrentDictionary<string, RetryCounter>`
(record struct Count + LastTouched via `Interlocked.Increment`); the wholesale
`Clear()` was replaced with `EvictOldestRetryCounters()` which evicts the oldest
half by LastTouched, so a flood cannot reset the Pattern-5 cap for legitimate senders.
**Verification status:** `fixed: requires human verification` — eviction/cap logic.

### WR-04: Group retry receipts never answered, justified by a stale comment

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs`
**Commit:** d2ce14d
**Applied fix:** Comment corrected: group resends are refused pending the
GroupDirectory membership check, which is not wired — a live gap since 04-05,
scoped as a follow-up rather than a silent "handled" claim.
**Verification status:** `fixed` (comment-only, no logic change).

### WR-05: Prekey-readiness recompute can overwrite still-live one-time prekeys

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs`
**Commit:** 02b7399
**Applied fix:** `EnsureOwnPrekeysAsync` now uses the new
`FindFreeOneTimeStartAsync`, which bumps past occupied ids (`MaxOneTimeScanHeadroom=1024`)
and only starts a batch where the whole window is free, so reupload no longer
overwrites live one-time prekeys after a retry-path spend.
**Verification status:** `fixed: requires human verification` — prekey allocation logic.

### IN-01: `TryReadBigEndian` returns a constant `true`

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs`
**Commit:** be6839f
**Applied fix:** Now returns false when `bytes is not { Length: > 0 and sizeof(uint) }`.
Both call sites are pre-width-guarded (verified), so behavior at call sites is unchanged.
**Verification status:** `fixed: requires human verification` — small logic change,
guarded by existing call-site width checks.

## Skipped Issues

### IN-02: Retry `<keys>` block wire shape deviates from the real-WA form

**File:** `src/Iwasug.WhatsApp.Sdk/Messaging/ReceiptHandler.cs`
**Reason:** No in-repo 04-05 phase directory exists to hold the suggested UAT
checklist item, and the deviation is already acknowledged in-tree (D-06 documents
the deliberate wire shape). Changing the wire shape now would invalidate pinned
tests without a protocol reference to conform to; tracked as an observation instead.
**Original issue:** Retry `<keys>` block wire shape deviates from the real-WA form.

### IN-03: `DecryptPreKey` replay-dedup fallback reports a less specific failure reason

**File:** `src/Iwasug.WhatsApp.Sdk/Signal/DoubleRatchet.cs`
**Reason:** The suggested guard (`innerRatchet == _aliceBaseKey && _receivingChainKey is not null`)
is unreachable: `_receivingChainKey is not null` implies `_peerRatchetPublic` has
moved off the base key (commit-after-MAC discipline in `DecryptNewChainCore`,
DoubleRatchet.cs:584-713, verified), so the existing 3-clause check already returns
`DuplicatePreKey`. Replays on the same chain already return `OldCounter`, and the
fresh-session MAC path only ever sees tampering where `MacMismatch` is the precise
reason. Adding the guard would be dead code.
**Original issue:** Replay-dedup fallback reports a less specific failure reason.

## Verification

- **Build (Tier 2):** `dotnet build Iwasug.WhatsApp.Sdk.sln --nologo` after every
  finding — final build in the worktree: **0 warnings, 0 errors**.
- **Targeted tests:** Messaging-namespace tests 49/49 green at IN-01; RetryReceiptTests 8/8;
  MessagingLoopbackTests including the new CR-02 test green after each related fix.
- **Final full run (worktree):** 524 total, **4 failed — all
  `PairingLifecycleTests` timeouts (`TaskCanceledException` in
  `WaitForPairingEventAsync`)**. The identical 4/21 failures reproduce on the
  baseline checkout at `84fc70c` (pre-fix), and no file touched by this fix set
  is in the Pairing path — pre-existing, unrelated to these fixes. All
  Messaging/Signal tests pass.
- **Not run:** full test suite between individual fixes (per-fix verification only);
  end-to-end verification deferred to the verifier phase.

## Notes for the reviewer

- Six of eight fixes change logic and are marked `fixed: requires human verification`
  above — Tier-1/Tier-2 verification proves structure and compilation, not semantic
  correctness.
- CR-01 is intentionally partial (signer swap + truthful docs; XEdDSA deferred per
  STACK.md's no-new-crypto-primitives constraint). Real-WhatsApp SPK verification
  remains an open, planned work item.
- WR-01 carries a protocol follow-up observation (retry-path OTK removed before
  wire vs. Bob-init convergence) recorded above for 04-05.

---

_Fixed: 2026-10-12T06:05:24+07:00_
_Fixer: the agent (gsd-code-fixer)_
_Iteration: 1_
