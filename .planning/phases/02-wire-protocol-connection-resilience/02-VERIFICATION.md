---
phase: 02-wire-protocol-connection-resilience
verified: 2026-10-09T12:00:00Z
status: human_needed
score: 6/6 must-haves verified
covered_files:
  - .planning/phases/02-wire-protocol-connection-resilience/02-01-PLAN.md
  - .planning/phases/02-wire-protocol-connection-resilience/02-01-SUMMARY.md
  - .planning/phases/02-wire-protocol-connection-resilience/02-02-PLAN.md
  - .planning/phases/02-wire-protocol-connection-resilience/02-02-SUMMARY.md
  - .planning/phases/02-wire-protocol-connection-resilience/02-03-PLAN.md
  - .planning/phases/02-wire-protocol-connection-resilience/02-03-SUMMARY.md
  - .planning/phases/02-wire-protocol-connection-resilience/02-04-PLAN.md
  - .planning/phases/02-wire-protocol-connection-resilience/02-04-SUMMARY.md
  - .planning/phases/02-wire-protocol-connection-resilience/02-05-PLAN.md
  - .planning/phases/02-wire-protocol-connection-resilience/02-05-SUMMARY.md
  - .planning/phases/02-wire-protocol-connection-resilience/02-06-PLAN.md
  - .planning/phases/02-wire-protocol-connection-resilience/02-06-SUMMARY.md
  - .planning/phases/02-wire-protocol-connection-resilience/02-07-PLAN.md
  - .planning/phases/02-wire-protocol-connection-resilience/02-07-SUMMARY.md
  - .planning/phases/02-wire-protocol-connection-resilience/02-08-PLAN.md
  - .planning/phases/02-wire-protocol-connection-resilience/02-08-SUMMARY.md
  - src/Iwasug.WhatsApp.Sdk/Proto/handshake.proto
  - src/Iwasug.WhatsApp.Sdk/Noise/NoiseHandshake.cs
  - src/Iwasug.WhatsApp.Sdk/Noise/NoiseTransport.cs
  - src/Iwasug.WhatsApp.Sdk/Connection/DisconnectReason.cs
  - src/Iwasug.WhatsApp.Sdk/Connection/ConnectionState.cs
  - src/Iwasug.WhatsApp.Sdk/Connection/ConnectionOptions.cs
  - src/Iwasug.WhatsApp.Sdk/Connection/ReconnectPolicy.cs
  - src/Iwasug.WhatsApp.Sdk/Connection/QueryRouter.cs
  - src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs
  - src/Iwasug.WhatsApp.Sdk/Transport/IFrameTransport.cs
  - src/Iwasug.WhatsApp.Sdk/Transport/FrameReader.cs
  - src/Iwasug.WhatsApp.Sdk/Transport/WaFrame.cs
  - src/Iwasug.WhatsApp.Sdk/Transport/WebSocketFrameTransport.cs
  - src/Iwasug.WhatsApp.Sdk/Codec/BinaryNode.cs
  - src/Iwasug.WhatsApp.Sdk/Codec/BinaryNodeCodec.cs
  - src/Iwasug.WhatsApp.Sdk/Codec/TokenDictionary.cs
  - src/Iwasug.WhatsApp.Sdk/Errors/SdkException.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Noise/HandshakeEnvelopeTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Codec/BinaryNodeDepthTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Noise/NoiseHandshakeTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Connection/LoopbackTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Connection/LoopbackRoundTripTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ConnectionStateTests.cs
  - NOTICE.md
covered_digest: "v2:sha256:7ced93381dbabc23b9c86cb4e44000196b6a9258189bcb43eb657a3f9dbfbc85"
behavior_unverified: 0
overrides_applied: 0
re_verification:
  previous_status: gaps_found
  previous_score: 4/6
  gaps_closed:
    - "SDK establishes a live session (CR-01 envelope field numbers + CR-02 handshake/pump framing + FrameReader bypass) — HandshakeMessage renumbered to server-compatible 2/3/4 with golden-tag tests; single WaFrame helper owns all 3-byte prefixes; intro sent exactly once and validated once at the edge; server-shaped message B (intro || len || bare envelope) proven consumable with agreeing transport keys; FrameReader now the sole inbound pump parser"
    - "Wire bytes decode safely under adversarial fixtures (CR-03 unbounded decoder recursion) — depth budget 64 with rule-naming SdkFormatException; LIST_16 pre-allocation capped at 32 with exact-count growth; four nesting/large-list/truncation regression facts"
  gaps_remaining: []
  regressions: []
requirements_verified: [CONN-04, CONN-05]
---

# Phase 02: Wire Protocol & Connection Resilience Verification Report

**Phase Goal:** Host has a live connection to WhatsApp that reconnects on its own and classifies why it dropped.
**Verified:** 2026-10-09
**Status:** human_needed
**Re-verification:** Yes — after gap closure (02-06 envelope renumber, 02-07 framing unification, 02-08 decoder hardening). All prior gap verdicts were re-checked against the current codebase, not carried forward.

## Goal Achievement

### Observable Truths (roadmap success criteria + plan must-haves, deduplicated)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | SDK establishes a session (WebSocket + Noise XX handshake + correlated query/reply round-trip) and, after any network drop, reconnects automatically with backoff — no host code involved | ✓ VERIFIED | Both prior blockers re-checked and closed in code: **(CR-01)** `handshake.proto:33-35` now uses outer fields 2/3/4; five golden-wire facts in `HandshakeEnvelopeTests.cs` pin tags 0x12/0x1A/0x22 and field-3 server-shape parse (green in my run). **(CR-02)** `WaFrame.Frame/Deframe` (new shared helper) owns every 3-byte BE prefix; `WaConnection.cs:484-485` sends the intro exactly once then a framed bare-protobuf A (no re-embedded header — `WriteMessageA` returns `envelope.ToByteArray()`); B consumed intro-first then deframed (`:524-534`) with framing violations wrapped into fail-closed `ProtocolHaltException` (`:537-547`); C framed (`:553`). Server-shaped B proven end-to-end by `Server_shaped_message_b_intro_plus_frame_is_consumed_and_keys_agree` (intro ‖ frame ‖ bare ServerHello envelope → handshake completes → responder-encrypt/initiator-decrypt probe equality). Auto-reconnect: `RealSocket_connect_headers_handshake_roundtrip_then_428_reconnects` (real ClientWebSocket) + 11 LoopbackRoundTrip facts + drop/reconnect cycle facts, all green in my full-suite re-run. |
| 2 | Each disconnect reason produces distinct observable behavior: 401 terminal logged-out (never retried), 428/408 retry with jittered backoff, 515 immediate restart, 440 account-replaced, 411 protocol-outdated | ✓ VERIFIED | `ReconnectPolicy.Decide` table + `ReconnectBackoff` + `WaConnection` close gate; 36 ReconnectPolicyTests (incl. `Decide_401_logged_out_never_retries_even_after_many_attempts`, `Decide_440_replaced_never_retries_at_any_depth`, `Decide_411_outdated_never_retries_at_any_depth`) + per-code ConnectionStateTests (`Drop401_...exactly_once`, `Drop440_stops...`, `Drop411_is_terminal`, `Drops_428_then_515_restart_with_counter_reset`), green in my re-run. Classification is wire-format-independent and the wire format it will observe is now server-shaped (truth 1). |
| 3 | Host observes typed per-connection state events (connecting/open/closing/logged_out) across the lifecycle | ✓ VERIFIED | `Reconnecting_sequence_emits_connecting_open_closing_connecting_open`, `Drop401_after_open_emits_logged_out_exactly_once_with_no_post_terminal_events`, `Concurrent_401_drop_and_dispose_emit_logged_out_at_most_once` — all green in my run; single-writer emit + Interlocked terminal guard in `WaConnection.cs`. |
| 4 | Noise XX core is spec-faithful with strict nonce/key hygiene (cacophony vectors, fresh ephemeral per re-handshake, zeroization) | ✓ VERIFIED | Committed `noise-xx-aesgcm-vectors.json` + 5 NoiseVectorTests + 16 NoiseHandshakeTests (incl. `Rehandshake_after_515_mints_fresh_ephemeral_and_zeroizes_old_keys`, `Cancelled_handshake_zeroizes_inflight_keys`), green in my re-run. Untouched by the gap-closure changes except the envelope/framing surface, which the two-party key-agreement facts re-prove. |
| 5 | Wire bytes decode safely under adversarial fixtures; every query has exactly one observable outcome | ✓ VERIFIED | **CR-03 closed:** `BinaryNodeCodec.cs:63` `MaxNestedDepth = 64`; `ReadNode(int depth = 0)` throws rule-naming `SdkFormatException` above budget (`:415-420`); recursion threads `depth + 1` (`:452`); LIST_16 initial capacity capped `Math.Min(count, 32)` with exact-count validated growth (`:448-453`). Four regression facts in `BinaryNodeDepthTests.cs` (accepts 64, rejects 200 with the exact rule message, 300-child LIST_16 round-trip, truncated 65535-count rejected) green in my run. Router half unchanged and green (10 QueryRouterTests: correlation, timeout, late-discard, closing fail-fast). |
| 6 | Production ClientWebSocket path proven over real sockets (Origin/UA headers, keepalive, close-code mapping) with TLS validation intact; reconnect is single-flight | ✓ VERIFIED | Real-socket loopback fact green in my re-run (headers observed server-side, 428 → autonomous reconnect over the now-framed flow); single-flight facts (`Parallel_queries_during_drop_collapse_to_single_reconnect_attempt`, `ReconnectGate_collapses_concurrent_requests_to_one_attempt`) green; zero TLS-bypass strings under `src/` (my grep). |

**Score:** 6/6 truths verified (0 present-but-behavior-unverified). All behavior-dependent truths (drop→reconnect transition, exactly-once terminal, single-flight collapse, decoder rejection) are exercised by passing behavioral tests in my own full-suite run — none rests on symbol presence alone.

### Gap Closure Verification (re-verification of the two prior FAILED truths)

| Prior gap | Fix claimed by | Re-checked against code | Verdict |
|-----------|----------------|-------------------------|---------|
| CR-01 envelope fields 1/2/3 vs server 2/3/4 + no golden-tag test | 02-06 | `handshake.proto:33-35` = 2/3/4; provenance header cites Baileys tags 18/26/34 @ `0af2386`; `HandshakeEnvelopeTests.cs` (5 facts) pins 0x12/0x1A/0x22 encode + server-shape decode; commits `94023f8`/`88525af` verified ancestors of HEAD | **CLOSED** |
| CR-02 double intro, missing length prefixes, header-demanding B, FrameReader bypassed in production | 02-07 | `WaFrame.cs` new shared helper; `WaConnection.cs:484` intro sent once per attempt; `NoiseHandshake.WriteMessageA/WriteMessageC` return bare envelopes (no header re-embed — old `ParseMessageB` header demand replaced by edge validation `ValidateIntroHeader`, `NoiseHandshake.cs:918`); B consumed intro-first fail-closed (`WaConnection.cs:518-547`); pump drains through `FrameReader.TryReadFrame` (`:694`) — grep confirms it is now the sole production inbound parser (only other `.Decrypt` callers are `NoiseTransport` internals and the handshake AEAD); `WatchAsync_OLD` gone; server-shaped B tracer fact with key-agreement probe; NOTICE.md row 2026-10-09 logs Baileys framing provenance (noise-handler.ts / socket.ts / WAProto @ `0af2386`) naming the three consumer files — WR-10 also closed; commits `74a9e36`/`8c73812`/`74f778d` verified ancestors | **CLOSED** |
| CR-03 unbounded decoder recursion + LIST_16 pre-allocation from untrusted bytes | 02-08 | Depth budget + rule-naming throw + capped LIST_16 in `BinaryNodeCodec.cs`; `BinaryNodeDepthTests.cs` (4 facts) green in my run; commits `951b930`/`8baa41c`/`12fa837` verified ancestors | **CLOSED** |

### Deferred Items

None. Live-server interop is not deferred here — it is routed to human verification below (external-service integration is inherently human per the verifier contract); Phase 9 OPS-02 will add a drift-detecting live job, but no later phase owns any remaining code gap.

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `Connection/ReconnectPolicy.cs` | Decide table + full-jitter backoff | ✓ VERIFIED | Pure `Decide` + `ReconnectBackoff`, clamp-after-jitter, 36 tests green |
| `Connection/WaConnection.cs` | Lifecycle + close gate + single-flight + server-shaped wire path | ✓ VERIFIED | Gate/single-flight/exactly-once green; intro-once framed A/C, intro-first fail-closed B, FrameReader-wired pump (`:694`), flagged+framed routed sends (`:758`) |
| `Connection/QueryRouter.cs` | Correlation + timeout + late-discard + closing fail-fast | ✓ VERIFIED | 10 tests green; untouched by gap closure (as 02-07 claimed) |
| `Noise/NoiseHandshake.cs` + `NoiseTransport.cs` | Spec XX core + AEAD + hygiene + bare-protobuf envelope | ✓ VERIFIED | Vectors + two-party + zeroization green; writers return bare envelopes; `ValidateIntroHeader` edge validation |
| `Proto/handshake.proto` | Server-compatible envelope (fields 2/3/4) | ✓ VERIFIED | `:33-35` renumbered; clean-room provenance header naming Baileys @ `0af2386` |
| `Codec/BinaryNode.cs` + `BinaryNodeCodec.cs` + `TokenDictionary.cs` | Byte-identical codec + bounded decoder + MIT provenance | ✓ VERIFIED | Round-trips + evil fixtures + 4 depth facts green; depth 64 + LIST_16 cap; NOTICE token rows present |
| `Transport/WaFrame.cs` (new) | Shared 3-byte BE framing helper | ✓ VERIFIED | Cap-enforced Frame/Deframe with rule-naming `SdkTransportException`; used by handshake A/C, B consumption, and routed sends |
| `Transport/FrameReader.cs` | Single inbound pump parser, wired in production | ✓ VERIFIED (was ORPHANED) | Production call site at `WaConnection.cs:694`; sole inbound decrypt path |
| `Transport/WebSocketFrameTransport.cs` | Production socket transport | ✓ VERIFIED | Real-socket loopback green; byte-transparent above the shared framing layer |
| `Connection/DisconnectReason.cs` + `ConnectionState.cs` + `ConnectionOptions.cs` | Vocabulary | ✓ VERIFIED | 401/403/411/419 terminal, 440 stop, 515 restart; exactly Connecting/Open/Closing/LoggedOut |
| `NOTICE.md` | Baileys handshake-framing provenance row | ✓ VERIFIED | Row dated 2026-10-09 (WR-10 closed) |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `WaConnection.ConnectAsync` | `IFrameTransport` | factory + `SendAsync`/`ReceiveAsync` | ✓ WIRED | Real-socket test proves bytes flow both directions |
| `WaConnection` | `WaFrame` (handshake A/C + B + routed sends) | `WaFrame.Frame/Deframe` at `:485`, `:534`, `:553`, `:758` | ✓ WIRED | New link closing CR-02; all length-prefix work in one helper |
| `WaConnection` watch pump | `FrameReader.TryReadFrame` | `:694` | ✓ WIRED (was NOT_WIRed) | Sole production inbound parser; grep confirms zero other production decrypt entry points |
| `WaConnection` drop path | `ReconnectPolicy.Decide` + backoff | `MapDropToOutcome` | ✓ WIRED | Per-code outcomes asserted end-to-end |
| `NoiseHandshake` | `NoiseTransport` | `ReadMessageB` mint + `WriteMessageC` | ✓ WIRED | Two-party + server-shaped-B facts agree on keys both directions |
| `QueryRouter` | `WaConnection` pump | per-attempt router + `NotifyConnectionClosing` | ✓ WIRED | In-flight fails as connection-lost; post-reconnect round-trips green |

### Data-Flow Trace (Level 4)

Server-shaped bytes (`intro ‖ 3-byte-len ‖ bare HandshakeMessage`) → `WebSocketFrameTransport.ReceiveAsync` → intro validated+stripped once (`WaConnection.cs:524-532`) → `WaFrame.Deframe` (`:534`) → `NoiseHandshake.ReadMessageB` → live `NoiseTransport`. Pump: bytes → `FrameReader.TryReadFrame` (flag strip + zlib) → `NoiseTransport.Decrypt` → `QueryRouter.TryParseFrame` → `TryDeliver`. ✓ FLOWING end-to-end, proven by the server-shaped B tracer fact and the loopback round trips. The chain no longer ends in misparse.

### Behavioral Spot-Checks (re-run by this verifier, not quoted from SUMMARYs)

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| Build warnings-as-errors | `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror --nologo` | `Build succeeded. 0 Warning(s) 0 Error(s)` | ✓ PASS |
| Full suite via direct-DLL fallback (`dotnet test` MTP discovery returns zero in this env — exit 5) | `dotnet ./tests/.../Iwasug.WhatsApp.Sdk.Tests.dll` | `Total: 300, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0` | ✓ PASS |
| Gap-closure commits in history | `git merge-base --is-ancestor` × 8 (`94023f8`, `88525af`, `74a9e36`, `8c73812`, `74f778d`, `951b930`, `8baa41c`, `12fa837`) | all 8 ancestors of HEAD | ✓ PASS |
| FrameReader is sole production inbound parser | grep `\.Decrypt\(|TryReadFrame` under `src/` | only `WaConnection.cs:694` (FrameReader) feeds `Noise.Decrypt`; other hits are Noise internals/tests/PublicAPI list | ✓ PASS |
| No TLS bypass under `src/` | grep `ServerCertificate\|ValidationCallback\|DangerousAcceptAny` in Connection/Noise | zero hits | ✓ PASS |
| No key-material logging | grep `Log.*[Kk]ey\|Log.*[Ss]ecret\|Convert.ToHexString` in WaConnection | zero hits | ✓ PASS |
| No debt markers in gap-closure files | grep `TBD\|FIXME\|XXX\|TODO\|HACK\|PLACEHOLDER` across 6 modified/new files | zero hits | ✓ PASS |
| No dead hand-rolled framing | grep `WatchAsync_OLD\|hand-rolled` in `src/` | only a comment asserting its absence | ✓ PASS |

Single full-suite run per the once-per-verification rule. Working tree carries uncommitted unrelated style-pass edits; build 0W/0E and 300/300 hold against the working tree, and none of the verdicts depend on those hunks.

### Probe Execution

No `scripts/*/tests/probe-*.sh` probes declared; no PLAN/SUMMARY mentions a probe driver. SKIPPED.

### Requirements Coverage

| Requirement | Source Plans | Description | Status | Evidence |
|-------------|--------------|-------------|--------|----------|
| CONN-04 | 02-01…02-05 + 02-06, 02-08 | Auto-reconnect with backoff; discriminate disconnect reasons | ✓ SATISFIED | Classification + backoff + reconnect mechanics green (50+ tests); handshake/framing now server-shaped (envelope 2/3/4, intro-once, prefixed frames, bounded decoder) — the prior PARTIAL verdict's blocker is closed |
| CONN-05 | 02-01, 02-02, 02-04, 02-05 + 02-07 | Typed per-account state events | ✓ SATISFIED | connecting/open/closing/logged_out asserted across drops, incl. exactly-once terminal, over the corrected framing |

No orphaned requirement IDs: REQUIREMENTS.md maps exactly CONN-04/CONN-05 to Phase 2; all eight plans claim them (02-06→CONN-04, 02-07→CONN-04/05, 02-08→CONN-04).

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| — | — | none | — | Zero debt markers, stubs, empty implementations, or bypass strings in phase-modified files |

### Prohibitions (all plan-level must-NOTs, re-verified)

| Prohibition | Tier | Verdict | Evidence |
|-------------|------|---------|----------|
| MUST NOT log key material / handshake secrets | judgment | honored | No key/secret logging at any WaConnection site (my grep); handshake/router log nothing by design |
| MUST NOT auto-retry 401 or 440 | test | honored (wired enforcement present) | `Decide(401/440)` no-retry pinned at depth incl. attempt-100; close-gate tests assert zero reconnect arms; 300/300 green |
| MUST NOT disable TLS cert validation | judgment | honored | Zero bypass strings under `src/` (my grep); loopback uses plaintext `ws://` only |

### Human Verification Required

The single remaining item is inherently non-automatable in this environment (no live WhatsApp endpoint or credentials; CI deliberately has no live dependency — live gating is Phase 9 OPS-02 scope). Everything else in this phase is covered by executed tests, including the server-shaped wire-format down-payment this item's prior iteration demanded.

#### 1. Live-server handshake and session proof

**Test:** Run a host application using the SDK against the real `wss://web.whatsapp.com` (with a linked-account pairing flow, or the Phase 3 harness once available): connect, complete the Noise handshake, sustain a session with at least one correlated query/reply, then induce a network drop (e.g. airplane-mode the socket) and observe recovery.
**Expected:** Message A is accepted and message B parses (envelope tags 18/26/34, intro ‖ length-prefixed frame); the pump sustains the session without a 408-retry loop; after the induced drop the SDK reconnects autonomously with jittered backoff and emits the typed state sequence `connecting → open → closing → connecting → open`.
**Why human:** Live-server interop and real-network behavior cannot be exercised programmatically here. The automatable down-payment — a server-shaped (upstream-observed) message B consumed to agreeing keys, pinned envelope tags, and real-socket reconnect mechanics — is now proven by executed tests; only the actual server's response remains human-territory. (Phase 3's QR-pairing work will also exercise this path in development.)

### Gaps Summary

**No gaps.** Both prior FAILED truths are closed against the current codebase, each with code-mechanical re-inspection plus green behavioral tests in this verifier's own full-suite run (300/300, 0 warnings): the handshake envelope now carries server-compatible field numbers pinned by golden-wire tests; one framing helper owns every length prefix; the intro is sent once and validated once at the edge; a real-shaped message B is provably consumable with agreeing transport keys; `FrameReader` is wired as the sole production inbound parser; and the decoder rejects adversarial nesting with a bounded budget instead of crashing the process. All 6 truths verify, both requirements are satisfied, and all prohibitions hold. The phase moves to `human_needed` solely for the live-server interop proof that no test in this environment can provide — the natural checkpoint before Phase 3 builds pairing on top.

---

_Verified: 2026-10-09_
_Verifier: the agent (gsd-verifier)_
