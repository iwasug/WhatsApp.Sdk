---
phase: 02-wire-protocol-connection-resilience
plan: 02-02
subsystem: connection
tags: [noise-handshake, query-router, reconnect, loopback-tracer, nsec, aesgcm]
requires:
  - phase: 02-01-seams-constants-policy
    provides: [IFrameTransport seam + LoopbackFrameTransport fake, NoiseProtocol consts, ReconnectPolicy.Decide table, connection vocabulary]
provides:
  - Live WaConnection vertical (header + Noise skeleton handshake + correlated query + open) proven over loopback with zero sockets
  - Autonomous classified reconnect (428 retry with fresh handshake, 401/403/411/419 terminal-logged-out, 440 surface-stop, 515 immediate restart)
  - Skeleton NoiseHandshake/NoiseTransport/QueryRouter with frozen method shapes for 02-03/02-04 hardening
affects: [02-03-noise-core, 02-04-codec-router, 02-05-reconnect-transport, pairing-phase-3]

actuals:
  tokens: 27700
  tasks: 2
  commits: 2

tech-stack:
  added: []
  patterns: [single-logging-owner, payload-agnostic-router, initiator-only-handshake, stillborn-open-rule, linked-CTS-lifecycle, startup-gate]

key-files:
  created:
    - src/Iwasug.WhatsApp.Sdk/Noise/NoiseHandshake.cs
    - src/Iwasug.WhatsApp.Sdk/Noise/NoiseTransport.cs
    - src/Iwasug.WhatsApp.Sdk/Connection/QueryRouter.cs
    - src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Connection/LoopbackRoundTripTests.cs
  modified:
    - src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt
    - tests/Iwasug.WhatsApp.Sdk.Tests/Iwasug.WhatsApp.Sdk.Tests.csproj

key-decisions:
  - "Router stays payload-agnostic (opaque bytes) forever per F4 fallback; 02-04 adds codec above it, never inside it"
  - "WaConnection is the SOLE log site for the vertical; handshake/router never log (message discipline reviewable in one file)"
  - "Host-cancel and dispose-cancel reach the monitor through one linked CTS under a startup gate — Register-forwarding proved unreliable under the test runner"
  - "Stillborn rule: a drop before the open is reported never emits Open; the terminal outcome surfaces to the ConnectAsync caller instead"
  - "Peer-protocol violations (dict mismatch) halt the loop without retry; attempt timeouts recycle as 408 drops"
  - "440 stops in Closing (not LoggedOut); 403/411/419 join 401 as Terminal-to-LoggedOut"

patterns-established:
  - "Linked-CTS lifecycle: monitor observes host+dispose cancellation through CreateLinkedTokenSource under a startup-gate lock (StoreRegistry _lifecycleGate precedent) — no Register callbacks to lose"
  - "Single logging owner: one class logs for a whole vertical; pure collaborators throw typed and stay silent"
  - "Skeleton internals shared with tests: DeriveTransportKeys/BuildFrame/TryParseFrame are internal-static so the loopback peer uses production code, never a duplicated KDF or envelope"

requirements-completed: [CONN-04, CONN-05]

coverage:
  - id: D1
    description: "Loopback round-trip works end-to-end with zero sockets: WA header + Noise skeleton handshake + correlated iq query/reply + open; injected 428 drop reconnects autonomously with a fresh handshake and opens again; 401 emits logged_out exactly once with no retry"
    requirement: "CONN-04"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Connection/LoopbackRoundTripTests.cs (HappyPath, Injected428Drop, Drop401_after_open)"
        status: pass
    human_judgment: false
  - id: D2
    description: "Slice edges proven: 515-during-connecting restarts with counter reset; 401-during-connecting logs out without open; cancel aborts unwrapped with keys zeroized and no retry; 440 stops without retry or logged_out; dict-mismatch aborts fail-closed; silent-peer queries time out with late replies discarded"
    requirement: "CONN-04"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Connection/LoopbackRoundTripTests.cs (8 edge facts)"
        status: pass
    human_judgment: false
  - id: D3
    description: "Host observes connecting/open/closing/logged_out with code and attempt depth across drops"
    requirement: "CONN-05"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Connection/LoopbackRoundTripTests.cs (state-sequence asserts in every fact)"
        status: pass
    human_judgment: false
  - id: D4
    description: "Skeleton crypto is real (NSec X25519 fresh per handshake, AES-GCM both directions, transcript-bound KDF) but pre-spec: 02-03 replaces the KDF/framing with cacophony-proven MixKey chain and protobuf"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Connection/LoopbackRoundTripTests.cs (fresh-ephemeral inequality, tamper rejection, AEAD round-trip)"
        status: pass
    human_judgment: true
    rationale: "The skeleton KDF, probe traffic, and length-prefix framing are intentionally pre-spec placeholders the tests exercise but cannot bless — 02-03/02-04 own byte-level proof (cacophony vectors, golden fixtures, Phase 9 interop)."

duration: 55min
completed: 2026-10-08
status: complete
---

# Phase 02 Plan 02: Loopback Tracer Slice Summary

**Live reconnecting connection over loopback with zero sockets: WA header, real-X25519 Noise skeleton handshake, correlated query router, and policy-driven reconnect with typed state events — 11 green facts**

## Performance

- **Duration:** 55 min
- **Started:** 2026-10-08T19:18:00+07:00
- **Completed:** 2026-10-08T20:12:30+07:00
- **Tasks:** 2 (1 tracer + 1 edge)
- **Files modified:** 7 (5 created, 2 modified)

## Accomplishments

- `WaConnection` runs the full vertical per attempt — fresh transport from the factory, spike-confirmed WA header, fresh `NoiseHandshake` (fresh NSec X25519 ephemeral, never reused), one opaque liveness-probe query, then `Open` — and owns every drop through `ReconnectPolicy.Decide` with a fresh handshake per reconnect, no host code involved
- `QueryRouter` correlates by id over opaque bytes with `TimeProvider` timeouts, late-reply discard, exact-pair fail-all on drop, and a 64-bit never-reset counter; `NoiseTransport` does strict-order AES-GCM both directions with zeroizing disposal
- 11/11 `LoopbackRoundTrip` facts green (happy path, 428-reconnect with fresh-ephemeral proof, 401-terminal-once, 515/401-during-connecting, cancel-unwrapped, 440-no-retry, dict-mismatch fail-closed, query-timeout with late-discard, handshake/transport disposal discipline); full suite 199/199 green, build 0W/0E
- 30 new public symbols declared in `PublicAPI.Unshipped.txt` (purely additive, ordinal, harvested mechanically from RS0016)

## Task Commits

Each task was committed atomically:

1. **Task 1: End-to-end loopback connection (tracer)** - `8dd3e5f` (feat)
2. **Task 2: Slice edges (drop-during-connecting, cancel)** - `b03b9a4` (feat)

**Plan metadata:** summary commit (see below)

## Files Created/Modified

- `src/Iwasug.WhatsApp.Sdk/Noise/NoiseHandshake.cs` - Initiator skeleton (`WriteMessageA`/`ReadMessageB`/`WriteMessageC`), fresh NSec ephemeral per instance, fail-closed header/dict/length validation, transcript-bound skeleton KDF, `INoiseHandshakePayloads` opaque-blob seam
- `src/Iwasug.WhatsApp.Sdk/Noise/NoiseTransport.cs` - Single-owner AES-GCM transport, 12-byte counter nonces from 0 per direction, strict-order decrypt, zeroizing dispose
- `src/Iwasug.WhatsApp.Sdk/Connection/QueryRouter.cs` - Id-correlation map over opaque bytes, `TimeProvider` timeouts, `TryDeliver`/`FailAllPending`, internal envelope helpers shared with the loopback peer
- `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs` - Single-lifecycle owner: probe-then-open, single-writer states, sole log site, linked-CTS lifecycle under a startup gate, stillborn-open rule
- `tests/Iwasug.WhatsApp.Sdk.Tests/Connection/LoopbackRoundTripTests.cs` - Scripted NSec peer harness + 11 facts
- `src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt` - 30 new declarations, purely additive (30+/0-)
- `tests/Iwasug.WhatsApp.Sdk.Tests/Iwasug.WhatsApp.Sdk.Tests.csproj` - Versionless NSec reference for the peer's real X25519 (02-01 pin reused, no new package)

## Decisions Made

- Router payload-agnostic forever (F4 fallback honored): opaque `byte[]` in/out, ids + timeouts + discard only — 02-04 layers the codec above it without touching the map.
- Single logging owner: only `WaConnection` logs; handshake/router throw typed and stay silent, so key-material discipline is reviewable in one file (every new log site names state/code/attempt/account only).
- Initiator-only handshake in src; the test scripts the responder with raw NSec plus production `DeriveTransportKeys`/`BuildFrame`/`TryParseFrame` (no KDF or envelope duplication).
- Message C is plaintext opaque; the first AEAD traffic is the probe query, proving fresh keys end-to-end in both directions.
- Stillborn rule: probe-success-then-instant-drop and pre-open terminals surface the outcome to the `ConnectAsync` caller with no `Open` event — a connection that died before its open was reported never opens.
- Every connect attempt (including reconnects) ends with the liveness probe before `Open`; the probe's content is ignored (never asserted — a real peer won't echo it).
- 440 stops in `Closing` (no `LoggedOut`, no retry — anti-kick-war); 403/411/419 join 401 as `Terminal` to `LoggedOut`; unknown codes log loudly and take bounded retry (Phase 9 drift signal).
- Connect-attempt timeout recycles as a 408 drop (Pitfall 2: timeouts are connection-lost class); pre-open abort emits `Closing` with null code (never `Connecting` forever, never a false `Open`).
- Wrap-and-log for host-factory failures stays a private helper on `WaConnection` (the plan-implied error surface); extraction to a shared file is deferred to 02-05, which gains the second consumer (`WebSocketFrameTransport`).
- Skeleton wire bytes (header+A/B/C framing, `idLength||id||body` envelope, `wa-probe` body) are loopback-only scaffolding documented as 02-03/02-04-replaced; the method shapes are the frozen surface.

## Deviations from Plan

### Plan-contemplated adjustment (not a rule fix)

**Test project references NSec directly (file outside frontmatter `files_modified`)**
- **Found during:** Task 1 (peer script needs real X25519)
- **Issue:** The scripted peer must perform genuine DH against the client's ephemeral — impossible without NSec in the test project.
- **Fix:** Versionless `PackageReference Include="NSec.Cryptography"` in the test csproj (version from `Directory.Packages.props` central pin — "never inline" honored). No new package: reuses the 02-01 pin, threat T-2-SC satisfied.
- **Files modified:** `tests/Iwasug.WhatsApp.Sdk.Tests/Iwasug.WhatsApp.Sdk.Tests.csproj`
- **Verification:** Restore + build 0W/0E; peer completes real agreement (round-trips decrypt)
- **Committed in:** 8dd3e5f (Task 1 commit)

### Auto-fixed Issues

**1. [Rule 1 - Bug] Channel never uninstalled on the normal drop path (transport leak + stale sends)**
- **Found during:** Task 1 (401 test: late query threw "Transport is closed" instead of "not open")
- **Issue:** `UninstallChannelAsync` ran only on probe-failure paths; terminal/retry drops leaked the transport and left `_channel` non-null, so post-drop sends bypassed the fast "not open" fail.
- **Fix:** Uninstall (fail pendings + dispose transport/noise) immediately after every `ConnectAndWatchAsync` drop return, before `Decide` (idempotent — probe paths already uninstalled).
- **Files modified:** `src/.../Connection/WaConnection.cs`
- **Verification:** Late-query asserts "not open"; reconnect tests show no leaked pairs
- **Committed in:** 8dd3e5f (Task 1 commit)

**2. [Rule 1 - Bug] Probe/watch race stalled on silent peers**
- **Found during:** Task 1 (reasoning + timeout-test design)
- **Issue:** When the watch won the `WhenAny` race, `await probe` could hang until the full query timeout even though the drop was already known.
- **Fix:** Uninstall the channel first (fails the probe fast via `FailAllPending`), then observe the probe, then return the drop code.
- **Files modified:** `src/.../Connection/WaConnection.cs`
- **Verification:** Timeout fact completes in ~1s against an 800ms-silent peer
- **Committed in:** 8dd3e5f (Task 1 commit)

**3. [Rule 1 - Bug] Host-cancel forwarding via `CancellationToken.Register` never fired under the test runner**
- **Found during:** Task 2 (cancel fact: `Created==2` — cancel recycled as a 408 retry at the 5s attempt timeout)
- **Issue:** `ConnectAsync` forwarded host-cancel to the monitor CTS via `Register(() => _monitorCts.Cancel())`. Proven broken under the MTP runner by tripwire + reflection + trace: a static flag set inside the callback stayed false and the monitor CTS stayed uncancelled 500ms after cancel, while test-side callbacks on the same source fired and `SynchronizationContext.Current` was null throughout. Root cause of the Register anomaly not conclusively identified.
- **Fix:** No `Register` anywhere in the lifecycle — the monitor observes host-cancel and dispose-cancel through one `CancellationTokenSource.CreateLinkedTokenSource(hostCt, _monitorCts.Token)` created and published under a `_startupGate` lock (StoreRegistry `_lifecycleGate` precedent); all monitor-loop reads use the passed token parameter. Linking is synchronous and context-free.
- **Files modified:** `src/.../Connection/WaConnection.cs`
- **Verification:** Cancel fact passes deterministically (abort in ms, `Created==1`); full suite 199/199 green
- **Committed in:** b03b9a4 (Task 2 commit)

**4. [Rule 3 - Blocker] Test-side corrections (peer script and assertions)**
- **Found during:** Tasks 1–2 (first red runs)
- **Issue:** Four test-only mistakes: peer read the standalone WA-header frame as message A; echo budgets didn't account for the internal probe query (probe consumes one echo); white-box transport test looped a single instance back into itself (direction keys differ by design — needs a mirrored pair); pre-open abort assertion raced the monitor unwind.
- **Fix:** Peer consumes and asserts the header frame first; drop-injecting peers serve 2 queries (probe + explicit) before closing; transport fact uses a mirrored initiator/responder pair; cancel fact waits for the abort `Closing` event instead of asserting immediate state.
- **Files modified:** `tests/.../Connection/LoopbackRoundTripTests.cs` (test-only; production untouched except the two real bugs above it exposed)
- **Verification:** 11/11 green, deterministic across reruns
- **Committed in:** 8dd3e5f (header/budget), b03b9a4 (mirror/event-wait)

**5. [Rule 1 - Bug] `PublicAPI.Unshipped.txt` merge keeps the pre-existing order quirk**
- **Found during:** Task 1 (RS0016 harvest)
- **Issue:** Mechanical ordinal re-sort moved the pre-existing `RetryOptions.RetryOptions()` ctor line (committed before its type line), producing a spurious deletion.
- **Fix:** Restored that one line to its committed position; final diff purely additive (30+/0-), harvested verbatim from RS0016 output.
- **Files modified:** `src/.../PublicAPI.Unshipped.txt`
- **Verification:** Build 0W/0E; `git diff` shows zero deletions
- **Committed in:** 8dd3e5f (Task 1 commit)

---

**Total deviations:** 1 adjustment + 5 auto-fixed (4 Rule 1 production/test bugs, 1 Rule 3 test blocker)
**Impact on plan:** All changes stay inside the plan's file list and prohibitions (no key logging, no 401/440 retry, no TLS touched). The linked-CTS lifecycle is a structural improvement within the approved surface — method shapes downstream plans consume are unchanged.

## Issues Encountered

- xUnit1030 forbids `ConfigureAwait(false)` in `[Fact]` methods (repo's xunit analyzers) — test methods use plain `await` (existing-test precedent); only private helpers keep `ConfigureAwait(false)`.
- NSec exact API verified by reflecting over the restored package DLL (`Key.Create` needs an `in` parameter struct, `Agree` needs `AllowPlaintextExport` on the shared-secret params, `PublicKey` is not disposable) — no network or docs lookup needed.
- Parallel Phase-1 style-pass waves are editing the same worktree concurrently (e.g. `var`↔explicit swaps in `QueryRouter.cs`, `NoiseHandshake.cs`); those hunks were left uncommitted — both task commits stage only plan files. The orchestrator merge reconciles.
- The `CancellationToken.Register` anomaly (deviation 3) consumed the bulk of Task 2: tripwire flag, reflection reads of the private CTS and monitor task, per-callback firing checks, and a BCL-minimal repro (which passed — the mechanism is sound in isolation). The production fix removes the pattern instead of explaining the runner interaction.

## Known Stubs

None — every new symbol is fully implemented. The skeleton KDF, probe traffic, and envelope framing are working code with documented 02-03/02-04 replacements (not stubs); `LoopbackFrameTransport` remains the tests-only fake whose first behavioral exercise was this plan (02-01 coverage D2 now satisfied).

## Threat Flags

None — no new network endpoints, auth paths, file access, or schema changes. Peer bytes are bounds-checked and fail closed (T-2-09 fact asserts abort + rule-only log); handshake secrets never enter messages or logs (verified by reading every new log site); first NSec runtime load succeeded (no silent crypto fallback, T-2-SC).

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Ready for 02-03 (Noise core): consumes `NoiseHandshake` method shapes + `DeriveTransportKeys` (replaced by spec MixKey chain + cacophony vectors), `NoiseProtocol` consts, NSec call shapes proven here; handshake `.proto` schema lands there.
- Ready for 02-04 (codec + router): consumes `QueryRouter.SendQueryAsync` (pairing-code iq rides it per downstream contract), internal `TryDeliver`/`BuildFrame`/`TryParseFrame` (envelope replaced by node-attr correlation), `MaxFrameBytes` + 3-byte framing rule.
- Ready for 02-05 (reconnect lifecycle + WS transport): consumes `WaConnection` lifecycle (transport factory becomes `WebSocketFrameTransport`), `ConnectionOptions.KeepAliveIntervalMs` (shipped but unwired — keepalive loop is 02-05 scope), 515-during-backoff structurally precluded (no socket held during backoff).
- Watch item: the `Register` anomaly means no host-cancel path anywhere in this codebase should rely on `CancellationToken.Register` forwarding until the runner interaction is understood — prefer linking (this plan's pattern).
- Pre-existing uncommitted worktree changes from parallel waves were left untouched (style pass + STATE.md); orchestrator reconciles at merge.

---
*Phase: 02-wire-protocol-connection-resilience*
*Completed: 2026-10-08*

## Self-Check: PASSED

- All 5 created files + 2 modified files + SUMMARY exist on disk (verified via Test-Path this session)
- Commits exist: `8dd3e5f` (Task 1) and `b03b9a4` (Task 2) both in `git log`
- `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror` → 0 Warning(s), 0 Error(s)
- `dotnet test Iwasug.WhatsApp.Sdk.sln` → 199/199 passed (188 pre-existing + 11 new)
- No src/tests file invented outside plan `files_modified` (test-csproj NSec ref documented as plan-contemplated adjustment)
