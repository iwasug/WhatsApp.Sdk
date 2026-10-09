---
phase: 03-pairing-session-durability
plan: 03-04
subsystem: pairing
tags: [pair-success, hmac, ed25519, protobuf, credential-store, logout, fail-closed]

# Dependency graph
requires:
  - phase: 03-pairing-session-durability (03-01)
    provides: [ICredentialStore seam + CredentialRecord, HandshakePayloadProvider, success-gated Open]
  - phase: 03-pairing-session-durability (03-03)
    provides: [PairingCoordinator with CompletePairSuccessAsync seam, pairing event vocabulary, loopback harness]
provides:
  - PairSuccessVerifier (strict-parse + FixedTimeEquals HMAC + Ed25519 account/device checks, discriminated verdict)
  - adv.proto (hand-written pair-success schema, golden-pinned) + pair-success-container.json fixture
  - Ordered success path (verify → await write → independent read → reply → Success) with FailWrites proof
  - Terminal logout clearing (401/403/406 RemoveAsync before disconnect) + 406 terminal policy arm
affects: [03-05 (link-code success reuses verifier shape + coordinator ordering), 03-06 (restart proof builds on paired rows)]

# Actuals (#2632) — pairs with the plan's `estimate` to calibrate future estimates.
# Same estimateTokens scale (chars/4 over the realized diff), never a harness token count.
actuals:
  tokens: 34806
  tasks: 3
  commits: 3

# Tech tracking
tech-stack:
  added: []
  patterns: [verify-then-persist drain, discriminated fail-closed verdict, raw-field strict protobuf walk, id-correlated success reply]

key-files:
  created: [src/Iwasug.WhatsApp.Sdk/Proto/adv.proto, src/Iwasug.WhatsApp.Sdk/Pairing/PairSuccessVerifier.cs, tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairSuccessVerifierTests.cs, tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ConnectionCredentialTests.cs, tests/Iwasug.WhatsApp.Sdk.Tests/TestData/pair-success-container.json]
  modified: [src/Iwasug.WhatsApp.Sdk/Pairing/PairingCoordinator.cs, src/Iwasug.WhatsApp.Sdk/Pairing/PairingEvents.cs, src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs, src/Iwasug.WhatsApp.Sdk/Connection/ReconnectPolicy.cs, tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairingLifecycleTests.cs, tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairingQrTests.cs, tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ReconnectPolicyTests.cs]

key-decisions:
  - "Account key rides INSIDE the HMAC envelope; only the device key is row-pinned — Verify takes one expected key, not two"
  - "HMAC and signatures cover the exact received details bytes sliced by the strict walk, never a re-serialization"
  - "406 joins the terminal policy arm (it retried before) per A7's logout set — plan-blocked otherwise"
  - "DisconnectReason gains no 406 member (Baileys has none); the unnamed cast flows to LoggedOut"

patterns-established:
  - "Drain pattern: stanza handler enqueues, StartAsync-joined drain task owns the ordered side-effect sequence — no Task.Run, no fire-and-forget"
  - "Strict protobuf walk: hand-rolled tag walk for field-number discipline instead of relying on library unknown-field surfacing"

requirements-completed: [CONN-01, CONN-03]

# Coverage metadata (#1602) — one entry per shipped deliverable. Drives DETERMINISTIC UAT routing in verify-work.
coverage:
  - id: D1
    description: "Pair-success verification fails closed: strict parse, fixed-time HMAC, Ed25519 account + device checks, golden fixture pins adv.proto"
    requirement: "CONN-01"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairSuccessVerifierTests.cs (11 tests)"
        status: pass
    human_judgment: false
  - id: D2
    description: "Success ordering verify → await write → independent read → reply → Success, FailWrites proves no Success and no reply"
    requirement: "CONN-01"
    verification:
      - kind: integration
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairingLifecycleTests.cs (10 tests: 5 Task-2 proofs)"
        status: pass
      - kind: integration
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairingQrTests.cs#Pairing_success_verifies_writes_replies_and_is_terminal_and_one_shot"
        status: pass
    human_judgment: false
  - id: D3
    description: "Terminal logout (401/403/406) clears credentials before disconnect; transients preserve byte-equal; next attempt regenerates"
    requirement: "CONN-03"
    verification:
      - kind: integration
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ConnectionCredentialTests.cs (10 tests)"
        status: pass
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ReconnectPolicyTests.cs#Decide_terminal_codes_return_Terminal_at_any_attempt (406 pin)"
        status: pass
    human_judgment: false
  - id: D4
    description: "Live-server parity: real pair-success accepted, real logout codes clear, repo-local adv field numbers confirmed on wire"
    requirement: "CONN-01"
    verification: []
    human_judgment: true
    rationale: "No live WhatsApp account exists in CI (03-RESEARCH A1/A2); loopback proves the machinery, the server half needs the end-of-phase human proof"

# Metrics
duration: 24min
completed: 2026-10-09
status: complete
---

# Phase 03 Plan 04: Pair-Success Verification Summary

**Cryptographic close of the pairing loop: fail-closed HMAC + dual-signature verification over a golden-pinned adv schema, strictly ordered verify-write-read-reply-event persistence, and credential clearing on terminal logout — 395/395 green**

## Performance

- **Duration:** 24min
- **Started:** 2026-10-09T16:50:18Z
- **Completed:** 2026-10-09T17:14:13Z
- **Tasks:** 3
- **Files modified:** 15

## Accomplishments

- Tracer proof green: `PairSuccessVerifier` strict-walks raw container bytes (unknown/duplicate/malformed fail closed), checks `FixedTimeEquals` HMAC with the stored adv secret, then NSec Ed25519 account + device signatures — `Verified(payload)` or `Rejected(reason)`, never null, never an exception for hostile input (CONN-01 trust boundary)
- Golden pin committed: hand-written `adv.proto` (repo-local v1 numbers, Baileys-roles provenance) + `pair-success-container.json` — any field-number edit fails CI, and the fixture rebuilds byte-identically from fixed seeds in-test
- Ordering proof green: coordinator drain runs verify → awaited `PutAsync` → independent read → id-correlated `iq` reply → `Success` as an asserted ordered list; FailWrites proves zero Success + zero replies; forged containers never touch the store; crash-after-write resumes paired without re-pair (CONN-01 + CONN-03)
- Logout clearing green: 401/403/406 each `RemoveAsync` the row (awaited, inside the logged-out guard) before disconnect completes; 408/429/503/515 leave the row byte-equal; double-clear idempotent; next pairing attempt regenerates a fresh identity; `ConnectionState` still exactly four values
- Full suite 395/395 green (`dotnet build -warnaserror` 0 warnings; no new packages; no `PublicAPI.Unshipped.txt` change — all new surface is internal)

## Task Commits

Each task was committed atomically:

1. **Task 1: PairSuccessVerifier — adv.proto parse, HMAC + account/device signature, fail-closed verdict** - `7162eb7` (feat, tracer)
2. **Task 2: Success ordering — verify → await write → independent read → reply → event; FailWrites proves no Success** - `2f8e5d3` (feat)
3. **Task 3: Terminal logout clears credentials — next attempt starts clean** - `9b72cb6` (feat)

**Plan metadata:** ledger base `e848862`, head after `9b72cb6` (measured `rev-list --count` = 3 task commits)

## Files Created/Modified

- `src/Iwasug.WhatsApp.Sdk/Proto/adv.proto` - Hand-written pair-success schema (AdvDetails + PairSuccessContainer, repo-local v1 numbers, provenance header)
- `src/Iwasug.WhatsApp.Sdk/Pairing/PairSuccessVerifier.cs` - Internal fail-closed gate: strict field walk, fixed-time HMAC, NSec Ed25519, discriminated `Verified`/`Rejected`
- `src/Iwasug.WhatsApp.Sdk/Pairing/PairingCoordinator.cs` - Attempt loop + joined verify-then-persist drain; `CompletePairSuccessAsync` fills the 03-03 seam; container extractor + id-correlated ack reply + Ed25519 device-key derivation
- `src/Iwasug.WhatsApp.Sdk/Pairing/PairingEvents.cs` - Success payload doc: verified paired user (was: unverified passthrough)
- `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs` - `LogoutCodes = {401,403,406}` set + awaited `ClearCredentialsAsync` in the terminal branch; Task-2 named-arg ctor update (no behavior)
- `src/Iwasug.WhatsApp.Sdk/Connection/ReconnectPolicy.cs` - 406 joins the terminal arm + doc updates
- `tests/.../Pairing/PairSuccessVerifierTests.cs` - 11 proofs (happy, 3 forgery classes, missing/unknown fields, golden, near-miss, null-guard, provenance)
- `tests/.../Pairing/PairingLifecycleTests.cs` - 5 new ordering proofs + direct-drive harness (recording sender, verify wrapper, phase sink)
- `tests/.../Pairing/PairingQrTests.cs` - Success proof rewritten for the verified shape (container builder, reply correlation, row-update assert)
- `tests/.../Connection/ConnectionCredentialTests.cs` - 10 proofs (3 logout clears, 4 transient preserves, regen, idempotent, state lock)
- `tests/.../Connection/ReconnectPolicyTests.cs` - 406 pinned in the terminal theory
- `tests/.../TestData/pair-success-container.json` - Golden fixture (container hex, public inputs, expected verdict, provenance)
- `src/.../Iwasug.WhatsApp.Sdk.csproj`, `tests/.../Iwasug.WhatsApp.Sdk.Tests.csproj` - adv.proto codegen + fixture content items
- `NOTICE.md` - Pair-success verification-chain provenance row (recorded-research sourcing, no new upstream reads)

## Decisions Made

- **Account key rides INSIDE the HMAC envelope; only the device key is row-pinned.** `Verify` takes one expected key (device public, derived from the row's signing private) rather than the plan's plural "expected identity keys": the claimed account key is HMAC-authenticated in-container and the account signature is checked under it, while the device signature is checked under the row-pinned key. A forged account signature still rejects (proven by test 3). The phone's long-term account key has no row field to pin against (CredentialRecord is contract-frozen), so in-container HMAC binding is the honest v1 story.
- **HMAC and signatures cover the exact received details bytes.** The strict walk slices the field-1 payload out of the raw container; crypto covers those bytes, never a re-serialization — non-canonical encodings fail closed at the HMAC (the safe direction) instead of being normalized into acceptance.
- **406 joins the terminal policy arm.** It previously fell into bounded retry; the plan's logout set (401/403/406 per A7) requires terminal handling or Test 3 cannot pass. One-line table change + doc updates + theory pin.
- **DisconnectReason gains no 406 member.** Baileys (the enum's provenance) names no 406; the unnamed `(DisconnectReason)406` cast flows through the existing terminal seam and the coordinator maps every non-405 terminal to LoggedOut. No vocabulary invented.
- **Coordinator success path uses a joined drain task, not inline async.** `OnStanzaReceived` stays synchronous (enqueue + signal); `StartAsync` joins the attempt loop and the drain with `Task.WhenAll`, surfacing drain store-faults via `ExceptionDispatchInfo` with original stacks. No `Task.Run` invocations anywhere on the path.
- **Ordering tests drive the coordinator directly (no loopback) except crash-recovery.** The coordinator's injected seams (sender, verify delegate, store) make the five-phase list fully deterministic; crash-recovery alone composes a real second connection to prove the login resume branch.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] 406 was retry-class; extended the terminal policy arm**
- **Found during:** Task 3 (the plan's logout set presumes 406 terminates; `ReconnectPolicy.Decide` sent it to bounded retry)
- **Issue:** A 406 stream failure would reconnect-loop instead of clearing credentials and surfacing LoggedOut — Test 3 unimplementable as specified
- **Fix:** `406 => Terminal` in the switch + doc enumerations updated; `[InlineData(406)]` pinned in the terminal theory (per A7's "confirm the set during implementation" note)
- **Files modified:** `src/.../Connection/ReconnectPolicy.cs`, `tests/.../Connection/ReconnectPolicyTests.cs`
- **Verification:** `ReconnectPolicyTests` green; `ConnectionCredentialTests` 406 case green
- **Committed in:** `9b72cb6` (part of task commit)

**2. [Rule 1 - Bug] Rewrote the 03-03 unverified-passthrough success test (file not in plan list)**
- **Found during:** Task 2 (the old test's `user`-attr notification shape is now ignored by extraction — no container, no Success)
- **Issue:** `Pairing_success_is_terminal_and_one_shot_with_unverified_passthrough` asserted the exact boundary this plan removes; left alone it times out waiting for Success
- **Fix:** Rebuilt as `Pairing_success_verifies_writes_replies_and_is_terminal_and_one_shot` with a row-keyed signed container, reply-correlation asserts, and a row-update assert; one-shot assertions kept
- **Files modified:** `tests/.../Pairing/PairingQrTests.cs`
- **Verification:** `PairingQrTests` 15/15 green
- **Committed in:** `2f8e5d3` (part of task commit)

**3. [Rule 3 - Blocking] Named-arg update to WaConnection's coordinator construction**
- **Found during:** Task 2 (new optional `pairSuccessVerifier` ctor parameter breaks positional call)
- **Issue:** `CreatePairingCoordinator` passed positionally; the inserted parameter shifted `TimeProvider`/resolver positions
- **Fix:** Named arguments (`pairSuccessVerifier: null, timeProvider:, resolver:, logger:`) — zero behavior change
- **Files modified:** `src/.../Connection/WaConnection.cs`
- **Verification:** Build green; all connection suites green
- **Committed in:** `2f8e5d3` (part of task commit)

---

**Total deviations:** 3 auto-fixed (1 bug, 2 blocking)
**Impact on plan:** All three are necessary consequences of the plan's own requirements (logout set, verified-success boundary, verify seam). No scope creep — no new packages, no public-surface additions, `ConnectionState.cs` verifiably untouched.

## Issues Encountered

- **Positional-record ctor collision (caught immediately):** explicit ctors on the `Verified`/`Rejected` positional records duplicate the synthesized ones (CS0111) — removed, documented via `<param>` on the record.
- **NSec `PublicKey` is not disposable (caught immediately):** `using PublicKey` is CS1674 — import/verify without `using` (only `Key` holds native resources); noted in code.
- **`InvalidProtocolBufferException` needs `Google.Protobuf` using (caught immediately)** — added.
- **My own FailWrites assertion bug (caught pre-commit):** passed the `because` string as an expected element to `Equal` — fixed to `Equal(["Verify"], because)`.
- **`Task.Run` grep criterion matches the pre-existing `Task RunAttemptAsync` declaration:** literal `Task.Run(` invocations are 0 (verified with escaped regex); the single regex match is the 03-03 method name, present before this plan. Intent (no fire-and-forget) holds — the drain is `Task.WhenAll`-joined.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Ready: 03-05 (link-code success reuses the verifier shape + ordered drain; the `pairSuccessVerifier` delegate seam lets code-mode tests observe verification), 03-06 (restart proof builds on paired rows — crash-recovery already proven at coordinator level here)
- Watch item: D4 live-shape parity — if the human proof rejects the scripted `pair-success`/`pair-success-ack` shapes or the repo-local adv numbers, the stanza builders (coordinator extractor + test fakes) and `.proto` + fixture regenerate together
- No blockers; working tree matches committed state; full suite 395/395 green with `dotnet build -warnaserror` at 0 warnings

---
*Phase: 03-pairing-session-durability*
*Completed: 2026-10-09*

## Self-Check: PASSED

- All 5 key files verified present on disk; all 3 task commits (`7162eb7`, `2f8e5d3`, `9b72cb6`) verified in `git log`
- `ConnectionState.cs` absent from all three commits (four-value lock honored)
- No stub patterns (`TODO|FIXME|placeholder|coming soon|not available|NotSupportedException`) in any created/modified source file
- No private key material in logs/exceptions/events (verifier has zero logging; coordinator logs account id + reason codes only; tests assert on verdicts/public hex only)
- `Task.Run(` literal invocations in `PairingCoordinator.cs` = 0; `RemoveAsync` ≥ 1 and `LogoutCodes` = 2 hits in `WaConnection.cs`; `FixedTimeEquals` = 2 hits in verifier; `Rejected` = 11 hits in verifier tests; `FailWrites` = 4 hits in lifecycle tests
