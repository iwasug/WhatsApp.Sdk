---
phase: 03-pairing-session-durability
plan: 03-03
subsystem: pairing
tags: [pairing, qr-code, noise-xx, credential-store, reconnect-policy, binary-node, loopback]

# Dependency graph
requires:
  - phase: 03-pairing-session-durability (03-01)
    provides: [StanzaReceived seam, credential row semantics, HandshakePayloadProvider, loopback harness]
  - phase: 03-pairing-session-durability (03-02)
    provides: [ClientOutdated terminal path, PairingTerminal seam, WaVersionResolver with 405 invalidation]
provides:
  - PairingStatus five-state host event vocabulary + wire-exact QR payload with shared rotation constants
  - PairingCoordinator (single-attempt, rotation-gated, terminal one-shot) exposed as WaConnection.Pairing
  - Registration-branch bootstrap: generate-and-persist identity before the first pairing frame
  - PairingQrTests (15 proofs) + PairingLifecycleTests (5 proofs)
affects: [03-04 (pair-success verify-then-persist through the CompletePairSuccessAsync seam), 03-05 (link-code flow reuses the coordinator event vocabulary), 03-06 (restart proof builds on the persisted row)]

# Actuals (#2632) — pairs with the plan's `estimate` to calibrate future estimates.
# Same estimateTokens scale (chars/4 over the realized diff), never a harness token count.
actuals:
  tokens: 33166
  tasks: 3
  commits: 3

# Tech tracking
tech-stack:
  added: []
  patterns: [rotation-gated pairing attempts, stanza-attr scripted protocol, snapshot-refreshing bootstrap, fake-clock rotation proofs]

key-files:
  created: [src/Iwasug.WhatsApp.Sdk/Pairing/PairingEvents.cs, src/Iwasug.WhatsApp.Sdk/Pairing/PairingQrPayload.cs, src/Iwasug.WhatsApp.Sdk/Pairing/PairingCoordinator.cs, tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairingQrTests.cs, tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairingLifecycleTests.cs]
  modified: [src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs, src/Iwasug.WhatsApp.Sdk/Pairing/HandshakePayloadProvider.cs, src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt, tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ResumeTests.cs]

key-decisions:
  - "Ref rides a stanza attribute, not text content: content-slot raw strings decode as Data bytes (BinaryNodeCodec header), attrs roundtrip losslessly"
  - "Rotation delay gates the next request, not the reply wait — back-to-back ref requests would hammer the server (ban-safety)"
  - "EnsureRegistrationMaterialAsync delegates to LoadCredentialsAsync: one generation path for coordinator-first starts and connect handshakes"
  - "Signed-prekey triple stays on injected defaults until prekey lifecycle lands (row carries no prekey fields)"
  - "Coordinator stays internal; only the event vocabulary is public — external-host exposure arrives with later plans"

patterns-established:
  - "Rotation-gate pattern: nextIssueAt per attempt, recoveries bypass, FakeTimeProvider advances prove the schedule with zero wall-time sleeps"
  - "Scripted-stanza discipline: repo-local iq/notification shapes with codec-safety notes, executable in loopback, confirmed by human proof"
  - "Bootstrap-via-load: Ensure is a snapshot-refreshing load, idempotent on present rows, fail-closed on store errors"

requirements-completed: [CONN-01]

# Coverage metadata (#1602) — one entry per shipped deliverable. Drives DETERMINISTIC UAT routing in verify-work.
coverage:
  - id: D1
    description: "Wire-exact QR payload (prefix + ref/noise/identity/adv/platform join) with 60s/20s/160s rotation policy and opaque-reference public surface"
    requirement: "CONN-01"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairingQrTests.cs (6 Task-1 tests: Build literal, policy constants, opacity reflection, vocabulary, schedule drive, validation)"
        status: pass
    human_judgment: false
  - id: D2
    description: "PairingCoordinator end-to-end over loopback: Code emission with server ref, rotation re-issue, Timeout one-shot, single-attempt rejection, Success passthrough, ClientOutdated/LoggedOut terminal mapping"
    requirement: "CONN-01"
    verification:
      - kind: integration
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairingQrTests.cs (9 Task-2 tracer tests through WaConnection.Pairing)"
        status: pass
    human_judgment: false
  - id: D3
    description: "Pairing-level 405 bounded version refetch (MaxVersionRefetches then ClientOutdated) and 503 survival through reconnect with Code after recovery"
    requirement: "CONN-01"
    verification:
      - kind: integration
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairingQrTests.cs#Pairing_level_405_refetches_version_bounded_then_client_outdated + Pairing_503_survives_reconnect_with_no_terminal_and_code_after_recovery"
        status: pass
    human_judgment: false
  - id: D4
    description: "Registration bootstrap: generate-and-persist before first frame, restart byte-equality, no-regen on seeded rows, fail-closed Put failure, row-valued registration payload"
    requirement: "CONN-01"
    verification:
      - kind: integration
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairingLifecycleTests.cs (5 tests) + tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ResumeTests.cs#Resume_missing_row_generates_persists_and_builds_registration_from_row"
        status: pass
    human_judgment: false
  - id: D5
    description: "Live-server pairing parity: real QR scan links an account, rotation/timeout match a real phone, scripted stanza shapes confirmed"
    requirement: "CONN-01"
    verification: []
    human_judgment: true
    rationale: "No live WhatsApp account exists in CI (03-RESEARCH A1/A2); loopback proves the machinery, the server half needs the end-of-phase human proof"

# Metrics
duration: 2h15m
completed: 2026-10-09
status: complete
---

# Phase 03 Plan 03: Pairing Surface Summary

**QR pairing a host can consume: typed five-state event stream, wire-exact rotating QR payload, coordinator driving attempts over the pump seam, and fresh identity durably persisted before the first pairing frame — 368/368 green**

## Performance

- **Duration:** 2h15m
- **Started:** 2026-10-09T21:21:57+07:00
- **Completed:** 2026-10-09T23:36:00+07:00
- **Tasks:** 3
- **Files modified:** 9

## Accomplishments

- Typed pairing vocabulary ships: `PairingStatus` (Code/Success/Timeout/LoggedOut/ClientOutdated) + `PairingStateChangedEventArgs`, with the wire-exact `https://wa.me/settings/linked_devices#` payload and shared 60s/20s/160s rotation constants (CONN-01 user-visible half)
- Tracer proof green: `WaConnection.Pairing` drives attempts end-to-end through the loopback fake — Code with the server's opaque ref, rotation re-issue on schedule, six-codes-then-Timeout one-shot, single-attempt rejection, unverified Success passthrough, seam-fed ClientOutdated/LoggedOut terminals
- Ban-safety holds under faults: pairing-level 405 invalidates + re-resolves at most `MaxVersionRefetches` (3) times before ClientOutdated; 503-class drops never terminate — the pending request re-issues on the fresh channel with a Code after recovery
- Durability seam delivers: empty store generates noise/signing/identity keys + adv secret with awaited `PutAsync` before any payload exists; restarts reuse the persisted static byte-identically; seeded rows never overwritten; throwing stores abort pairing with zero frames and zero events
- Full suite 368/368 green (`dotnet build -warnaserror` 0 warnings; no new packages; 20 new PublicAPI entries, all Task-1 public vocabulary)

## Task Commits

Each task was committed atomically:

1. **Task 1: Pairing events + wire-exact QR payload with rotation policy** - `c5605a5` (feat)
2. **Task 2: PairingCoordinator on the pump seam** - `d98d776` (feat, tracer)
3. **Task 3: Registration-branch bootstrap** - `f07f996` (feat)

**Plan metadata:** ledger base `f0e0e82`, head after `f07f996` (measured `rev-list --count` = 3 task commits)

## Files Created/Modified

- `src/Iwasug.WhatsApp.Sdk/Pairing/PairingEvents.cs` - Public five-state vocabulary + EventArgs with host-guidance docs (render/re-pair/upgrade)
- `src/Iwasug.WhatsApp.Sdk/Pairing/PairingQrPayload.cs` - Public wire-exact `Build` + shared rotation constants + pure schedule over injected-clock instants
- `src/Iwasug.WhatsApp.Sdk/Pairing/PairingCoordinator.cs` - Internal attempt state machine: rotation gate, single-attempt rule, terminal one-shot, bounded 405 refetch, 503 survival, `CompletePairSuccessAsync` seam (throws until 03-04)
- `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs` - `Pairing` property (lazy, wired, singleton), `SendPairingNodeAsync` send path, `ChannelReady` event + raise site
- `src/Iwasug.WhatsApp.Sdk/Pairing/HandshakePayloadProvider.cs` - Real no-row branch (generate+persist), row-based registration snapshot, `EnsureRegistrationMaterialAsync` via snapshot-refreshing load, `Credentials` accessor
- `src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt` - 20 harvested entries (Task-1 public surface; Task-2/3 surface is internal, nothing to track)
- `tests/.../Pairing/PairingQrTests.cs` - 15 proofs (6 unit + 9 loopback tracer)
- `tests/.../Pairing/PairingLifecycleTests.cs` - 5 bootstrap proofs (ordering, crash-window, no-regen, fail-closed, row values)
- `tests/.../Connection/ResumeTests.cs` - Missing-row test rewritten for the generation contract (was: null-static defaults path)

## Decisions Made

- **Ref rides a stanza attribute, not text content.** Content-slot raw strings decode as `Data` bytes (BinaryNodeCodec header: only token/nibble/hex/JID strings survive as `Text`), while attr values roundtrip losslessly — found by bisecting a silent reply loss. Documented in the coordinator remarks and test header.
- **Rotation delay gates the next request, not the reply wait.** The first shape re-issued immediately after each reply, so an instant peer spun codes unboundedly (server-spam class). Now `NextIssueAt` per attempt; 405/drop recoveries bypass it to re-issue promptly.
- **`EnsureRegistrationMaterialAsync` delegates to `LoadCredentialsAsync`.** One generation path serves coordinator-first starts and connect handshakes; the snapshot is row-based either way; the ignored static copy is zeroized at the call site.
- **Signed-prekey triple stays on injected defaults.** The credential row carries no prekey fields and `CredentialRecord` is contract-frozen — generation covers noise/signing/identity/adv only; prekey lifecycle is future work (asserted as boundary in lifecycle test 5).
- **Coordinator stays internal; only the event vocabulary is public.** `WaConnection.Pairing` is internal (IVT-tested); external-host exposure arrives with later plans — no public-surface addition beyond Task 1.
- **Task 3 needed no WaConnection change** (plan listed it): the provider seam carries the whole branch. Left untouched deliberately.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Scripted ref moved from text child to attribute**
- **Found during:** Task 2 (all 9 coordinator tests hung — reply sent by fake, never matched)
- **Issue:** `FromText("ref", "REF-1")` encodes as raw UTF8 bytes, which decode as `Data`, not `Text` — `TryExtractRef` read `.Text` (null) and ignored every reply. Silent by pump design (decode-ok, no drop, no log).
- **Fix:** Ref travels as `<pair-device ref="…"/>` attr; extractor reads the attr; codec rationale documented in coordinator remarks + test header. Bisected via trace-queue probe (send proven, pump proven, extraction replicated in-test).
- **Files modified:** `src/.../Pairing/PairingCoordinator.cs`, `tests/.../Pairing/PairingQrTests.cs`
- **Verification:** Filtered run 15/15 green
- **Committed in:** `d98d776` (part of task commit)

**2. [Rule 2 - Missing critical] Rotation gate restructured around next-request timing**
- **Found during:** Task 2 (rotation test saw 6 codes where 3 were expected)
- **Issue:** The reply wait doubled as the rotation timer, so an instant peer reply skipped gating entirely — unbounded ref requests against the server (ban-safety violation, Pitfall 1 class).
- **Fix:** `Attempt.NextIssueAt` gates the next send; `Retry(ReissuePromptly)` distinguishes recovery (bypass) from spent waits (re-gate); non-positive-delay guard before arming fake-clock timers.
- **Files modified:** `src/.../Pairing/PairingCoordinator.cs`
- **Verification:** Rotation/timeout counts exact (3 codes on 60+20, 6-then-Timeout on full window); 405/503 tests unaffected
- **Committed in:** `d98d776` (part of task commit)

**3. [Rule 3 - Blocking] `Resume_missing_row` test rewritten for the generation contract**
- **Found during:** Task 3 (only red test in the pre-existing suite after the no-row branch went real)
- **Issue:** The 03-01 test asserted null-static + defaults-based registration — the exact premise Task 3 replaces (generate+persist+row-based).
- **Fix:** Rewrote as `Resume_missing_row_generates_persists_and_builds_registration_from_row` (persisted static, independent-read durability, row-valued eIdent/regid, skey-defaults boundary kept).
- **Files modified:** `tests/.../Connection/ResumeTests.cs`
- **Verification:** Full suite 368/368
- **Committed in:** `f07f996` (part of task commit)

---

**Total deviations:** 3 auto-fixed (1 bug, 1 missing-critical, 1 blocking)
**Impact on plan:** All three are necessary consequences of honest wiring (codec truthfulness, ban-safe pacing, contract migration). No scope creep — no new packages, no public-surface additions beyond the planned vocabulary.

## Issues Encountered

- **Silent reply loss (90+ min bisect, pre-commit):** the fake's reply arrived at the pump and even at a test-side stanza subscriber, but the coordinator ignored it — no error anywhere (null logger + pump `continue` discipline). Bisected with a trace-queue scratch probe (`ScratchProbeTests.cs`, deleted after): send ✓ → pump ✓ → in-test extraction replication showed `refText` empty → codec content-slot behavior identified. Lesson recorded: scripted stanza shapes must respect the content-vs-attr codec asymmetry; replicate-then-assert is the fastest bisect for silent pump drops.
- **Stale-match test predicates (caught pre-commit):** rotation "third code" wait matched the first code (`!= second` only). Fixed with exclusion of all previously seen payloads.
- **FakeTimeProvider overdue-timer care:** rotation/timeout advances stay incremental (60/20 steps) so no delay is ever armed past-due; coordinator guards non-positive delays. Same pattern 03-02 relies on.
- **Analyzer friction (caught immediately):** xUnit1030 (no `ConfigureAwait(false)` in test methods — extracted `WaitForConnectionAsync` helper), `BeEquivalentTo` params overload miscount (replaced with `HaveCount` + `Contain`), test-namespace `Connection` collision (added SDK using), RS0016 harvest (20 entries).

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Ready: 03-04 (pair-success verification + credential update through the `CompletePairSuccessAsync` seam; `Success` is currently an unverified passthrough — the seam throws `NotSupportedException` until then), 03-05 (link-code flow reuses the event vocabulary + coordinator retry shape), 03-06 (kill/restart proof builds on the persisted-row mechanism proven here at provider level)
- Watch item: D5 live-shape parity — if the human proof rejects the scripted `pair-device`/`pair-success` shapes, the stanza builders change in exactly two places (coordinator extractors + test fakes move together)
- No blockers; full suite 368/368 green with `dotnet build -warnaserror` at 0 warnings

## Known Stubs

| File | Line | Description |
|------|------|-------------|
| `src/Iwasug.WhatsApp.Sdk/Pairing/PairingCoordinator.cs` | `CompletePairSuccessAsync` | Intentional seam (plan-directed): throws `NotSupportedException` until plan 03-04 implements verify-then-persist. Never called in this plan; `Success` is a documented unverified passthrough. Resolves in 03-04. |

## Threat Flags

| Flag | File | Description |
|------|------|-------------|
| threat_flag: deferred-verification | `src/Iwasug.WhatsApp.Sdk/Pairing/PairingCoordinator.cs` (`OnStanzaReceived` Success arm) | Pair-success payload passes to the host UNVERIFIED in this plan (no HMAC/signature check, no credential write) — staged deferral to plan 03-04 per the plan's action list, documented in code remarks + test boundary note. No credential mutation occurs on this path today. |

---
*Phase: 03-pairing-session-durability*
*Completed: 2026-10-09*

## Self-Check: PASSED

- All 5 key files verified present on disk; all 3 task commits (`c5605a5`, `d98d776`, `f07f996`) verified in `git log --all`
- No stub patterns in created/modified source except the plan-directed `CompletePairSuccessAsync` seam (tracked above, resolves in 03-04)
- No private key material in logs/exceptions/events (provider/coordinator never log material; messages name rules only; tests compare public hex/row-equality)
- `Task.Delay(` with literal ≥ 1000 in PairingQrTests = 0 (injected clock only); no `catch` in the registration persist path (try/finally zeroing only, never swallowing)
