---
phase: 02-wire-protocol-connection-resilience
verified: 2026-10-09T00:00:00Z
status: gaps_found
score: 4/6 must-haves verified
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
  - src/Iwasug.WhatsApp.Sdk/Noise/NoiseProtocol.cs
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
  - src/Iwasug.WhatsApp.Sdk/Transport/WebSocketFrameTransport.cs
  - src/Iwasug.WhatsApp.Sdk/Codec/BinaryNode.cs
  - src/Iwasug.WhatsApp.Sdk/Codec/BinaryNodeCodec.cs
  - src/Iwasug.WhatsApp.Sdk/Codec/TokenDictionary.cs
  - src/Iwasug.WhatsApp.Sdk/Proto/handshake.proto
  - src/Iwasug.WhatsApp.Sdk/Errors/SdkException.cs
covered_digest: "v3:sha256:9004ff51289ca0f5aec19627c3f406e2db29591ed541789f125561c8fac2d972"
behavior_unverified: 0
overrides_applied: 0
gaps:
  - truth: "SDK establishes a live session with WhatsApp (WebSocket + Noise XX handshake + query/reply) and reconnects automatically"
    status: failed
    reason: "CR-01 + CR-02 (both independently re-verified against upstream Baileys @0af2386 during this verification): outer proto fields 1/2/3 vs server 2/3/4 so message A never starts a handshake and message B never parses; intro header sent twice with no length prefixes while the server speaks intro-once + 3-byte BE prefix per frame. The client as committed cannot complete a handshake with wss://web.whatsapp.com."
    artifacts:
      - path: "src/Iwasug.WhatsApp.Sdk/Proto/handshake.proto"
        issue: "HandshakeMessage outer fields client_hello=1/server_hello=2/client_finish=3; server schema is 2/3/4 (wire tags 18/26/34)"
      - path: "src/Iwasug.WhatsApp.Sdk/Noise/NoiseHandshake.cs"
        issue: "WriteMessageA re-embeds WaHeader (lines 629-633) on top of the standalone WaHeader frame sent by WaConnection (line 483); no 3-byte length prefix on A/C; ParseMessageB demands a 4-byte header the server never sends inside deframed bytes (line 931)"
      - path: "src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs"
        issue: "WatchAsync/SendRoutedAsync bypass FrameReader entirely (zero production references to FrameReader under src/); raw WS messages feed straight into NoiseTransport.Decrypt"
    missing:
      - "Renumber HandshakeMessage outer fields to 2/3/4 + golden-bytes test pinning outer tags 0x12/0x1A/0x22"
      - "Single Frame/Deframe helper (FrameReader semantics) used by handshake + pump; intro header sent exactly once per connection; accept headerless message B/C"
      - "Server-shaped golden fixture test (header || len || bytes) proving a real-shaped message B is consumed"
  - truth: "Wire bytes decode safely under adversarial fixtures (framing + codec hardening, T-2-06)"
    status: failed
    reason: "CR-03 confirmed mechanically: BinaryNodeCodec.ReadNode recurses per nested child with no depth budget (lines 414-446), and StackOverflowException is uncatchable — ~30 KB of nested LIST_8 frames crashes the process. Companion: LIST_16 pre-allocates up to 65535 entries from two untrusted bytes (line 442). Evil-fixture suite covers lies/truncations/overruns but no nesting case."
    artifacts:
      - path: "src/Iwasug.WhatsApp.Sdk/Codec/BinaryNodeCodec.cs"
        issue: "ReadNode() has no depth parameter/guard; unbounded recursion on adversary-controlled bytes; capacity pre-allocation from untrusted count"
    missing:
      - "Recursion-depth limit (e.g. 64) with rule-naming SdkFormatException + nesting golden/evil test"
      - "Incremental list growth or capped initial capacity for LIST_16"
requirements_verified: [CONN-04, CONN-05]
---

# Phase 02: Wire Protocol & Connection Resilience Verification Report

**Phase Goal:** Host has a live connection to WhatsApp that reconnects on its own and classifies why it dropped.
**Verified:** 2026-10-09
**Status:** gaps_found
**Re-verification:** No — initial verification (no prior 02-VERIFICATION.md; 02-REVIEW.md + 02-REVIEW-DISPOSITION.md with 24 open findings were weighed as input, not as a prior verdict)

## Goal Achievement

### Observable Truths (roadmap success criteria + plan must-haves, deduplicated)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | SDK establishes a session (WebSocket + Noise XX handshake + correlated query/reply round-trip) and, after any network drop, reconnects automatically with backoff — no host code involved | ✗ FAILED | Loopback mechanics pass (11 LoopbackRoundTrip + 1 real-socket LoopbackTests facts, all green in my re-run), but the client **cannot handshake with the real server**: CR-01 + CR-02 confirmed-gap (see Critical-finding verdicts). "Live connection to WhatsApp" does not hold. |
| 2 | Each disconnect reason produces distinct observable behavior: 401 terminal logged-out (never retried), 428/408 retry with jittered backoff, 515 immediate restart, 440 account-replaced, 411 protocol-outdated | ✓ VERIFIED | `ReconnectPolicy.Decide` table + `ReconnectBackoff` + `WaConnection` close gate, proven by 36 ReconnectPolicyTests + 14 ConnectionStateTests, all green in my full-suite re-run (288/288). Classification logic is wire-format-independent. Live observability of each code depends on gap closure for truth 1. |
| 3 | Host observes typed per-connection state events (connecting/open/closing/logged_out) across the lifecycle | ✓ VERIFIED | Event sequences asserted in every LoopbackRoundTrip fact + ConnectionStateTests (incl. terminal exactly-once, no post-terminal events, Closing payload with code + depth). Single-writer emit + Interlocked terminal guard verified in code (`WaConnection.cs`). |
| 4 | Noise XX core is spec-faithful with strict nonce/key hygiene (cacophony vectors, fresh ephemeral per re-handshake, zeroization) | ✓ VERIFIED | Committed `noise-xx-aesgcm-vectors.json` (verbatim upstream entry) + 5 NoiseVectorTests + 13 NoiseHandshakeTests, all green in my re-run. Nonce shape matches Baileys `generateIV` (review-verified, not disputed). |
| 5 | Wire bytes decode safely under adversarial fixtures; every query has exactly one observable outcome | ✗ FAILED | Router half holds (10 QueryRouterTests: correlation, timeout, late-discard, closing fail-fast — green). Codec/framing half fails: CR-03 unbounded-recursion remote crash (confirmed mechanically); FrameReader proven only by its own tests and bypassed on the production path (CR-02). |
| 6 | Production ClientWebSocket path proven over real sockets (Origin/UA headers, keepalive, close-code mapping) with TLS validation intact; reconnect is single-flight | ✓ VERIFIED | `LoopbackTests` (real `ClientWebSocket` ↔ `HttpListener` fake: headers observed server-side both attempts, 428 → autonomous reconnect) green in my re-run; single-flight + exactly-once interleaving facts green. **Scope note:** the fake speaks the loopback protocol, so this proves socket mechanics, NOT server wire compatibility. No TLS-bypass string under `src/` (my grep: zero hits). |

**Score:** 4/6 truths verified (0 present-but-behavior-unverified)

### Critical-finding verdicts (02-REVIEW.md CR-01/CR-02/CR-03 — each re-verified, none taken on trust)

| ID | Verdict | Evidence (this verification, not the review's word) |
|----|---------|------------------------------------------------------|
| CR-01 envelope field numbers | **confirmed-gap** | Independently fetched Baileys `@0af2386` `WAProto/index.js` just now: `HandshakeMessage.encode` uses wire tags **18/26/34** (fields 2/3/4) and `decode` switches on cases 2/3/4 — read from raw upstream source. Ours uses 1/2/3 (`handshake.proto:30-34`), whose own header admits "names and numbers below are our own", contradicting the 02-03 "Baileys-faithful shapes" claim. No golden-bytes test pins the outer tags (grep: none). Fatal in both directions. |
| CR-02 handshake/pump framing + FrameReader bypass | **confirmed-gap** | Code-mechanical: `WaConnection.cs:483` sends standalone `WaHeader`, then `WriteMessageA()` re-embeds the same 4 bytes (`NoiseHandshake.cs:629-633`) → intro duplicated; no 3-byte length prefix on A/C; `ParseMessageB` demands a 4-byte header (`:931`); `FrameReader` has **zero production references** under `src/` (only doc-comment mentions). Upstream `encodeFrame` (`src/Utils/noise-handler.ts @0af2386`, fetched just now) confirms intro-once (`sentIntro` flag) + 3-byte BE prefix on every frame, and `socket.ts validateConnection` sends message A as a bare protobuf through `encodeFrame` while decoding the reply directly as `HandshakeMessage` with no header check. One nuance the review overstates slightly: whether a server intro accompanies message B cannot be settled without a live server — but the divergence (double-intro, missing prefixes, header-demanding B, bypassed FrameReader) is settled from code alone. |
| CR-03 unbounded decoder recursion | **confirmed-gap** | Code-mechanical: `BinaryNodeCodec.cs:414-446` — `ReadNode()` takes no depth parameter, file contains no depth guard (grep `depth|MaxDepth`: zero hits); `new List<BinaryNode>(count)` at line 442 allocates from two untrusted bytes. Evil-fixture suite has no nesting case. Uncatchable `StackOverflowException` from adversary-controlled bytes inside the declared T-2-06 boundary. |

### Deferred Items

None. Considered Phase 9 (OPS-02 live-interop drift job): it would *detect* but not *fix* these defects, and the defects are initial incorrectness in Phase 2 files, not drift. No later phase's goal or success criteria covers renumbering the handshake envelope, fixing handshake/pump framing, or bounding decoder recursion. All three gaps belong to Phase 2 gap closure.

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `Connection/ReconnectPolicy.cs` | Decide table + full-jitter backoff | ✓ VERIFIED | Pure `Decide` + internal `ReconnectBackoff`, clamp-after-jitter, 36 tests green |
| `Connection/WaConnection.cs` | Lifecycle + close gate + single-flight | ✓ VERIFIED (mechanics) / wire path FAILED (framing, see gaps) | Gate, single-flight, exactly-once proven; handshake/pump byte path diverges from server |
| `Connection/QueryRouter.cs` | Correlation + timeout + late-discard + closing fail-fast | ✓ VERIFIED | 10 tests green; per-attempt router wired as drop trigger |
| `Noise/NoiseHandshake.cs` + `NoiseTransport.cs` | Spec XX core + AEAD + hygiene | ✓ VERIFIED (crypto) / envelope FAILED (see gaps) | Vectors + two-party + zeroization green; envelope numbers + framing wrong |
| `Proto/handshake.proto` | Server-compatible envelope | ✗ FAILED | Outer fields 1/2/3 vs server 2/3/4 (CR-01) |
| `Codec/BinaryNode.cs` + `BinaryNodeCodec.cs` + `TokenDictionary.cs` | Byte-identical codec, MIT provenance | ✓ VERIFIED (parity) / ✗ FAILED (safety: CR-03) | Round-trips + evil fixtures green; recursion DoS open; NOTICE token rows present |
| `Transport/IFrameTransport.cs` + `FrameReader.cs` | Seam + 3-byte framing parser | ✓ VERIFIED (unit) / ⚠️ ORPHANED in production | FrameReader green in isolation, never called on the live path |
| `Transport/WebSocketFrameTransport.cs` | Production socket transport | ✓ VERIFIED (mechanics, scope-limited) | Real-socket loopback green; byte-transparent by design (which is precisely why server framing breaks above it) |
| `Connection/DisconnectReason.cs` + `ConnectionState.cs` + `ConnectionOptions.cs` | Vocabulary | ✓ VERIFIED | 401/403/411/419 terminal, 440 stop, 515 restart; exactly Connecting/Open/Closing/LoggedOut |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `WaConnection.ConnectAsync` | `IFrameTransport` | factory + `SendAsync`/`ReceiveAsync` | ✓ WIRED | Real-socket test proves the seam carries bytes both directions |
| `WaConnection` drop path | `ReconnectPolicy.Decide` + backoff | `MapDropToOutcome` | ✓ WIRED | Per-code outcomes asserted end-to-end in ConnectionStateTests |
| `NoiseHandshake` | `NoiseTransport` | `ReadMessageB` mint + `WriteMessageC` | ✓ WIRED | Two-party tests agree on keys both directions |
| `FrameReader` | pump handshake/data path | — | ✗ NOT_WIRED | Zero production call sites; the phase's own framing component is dead code on the live path |
| `QueryRouter` | `WaConnection` pump | per-attempt router + `NotifyConnectionClosing` | ✓ WIRED | In-flight fails as connection-lost; no latch leaks (post-reconnect round-trips green) |

### Data-Flow Trace (Level 4)

N/A as UI rendering — applied as wire-byte trace instead: server-shaped bytes → `WebSocketFrameTransport.ReceiveAsync` → `NoiseTransport.Decrypt` directly (FrameReader bypassed, 3-byte prefix un-stripped → strict-nonce failure → spurious 408). Chain ends in misparse, not in delivered data. Status: ✗ DISCONNECTED on the inbound live path; ✓ FLOWING on the loopback path (fake speaks the SDK's own format).

### Behavioral Spot-Checks (re-run by this verifier, not quoted from SUMMARYs)

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| Build warnings-as-errors | `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror --nologo` | `Build succeeded. 0 Warning(s) 0 Error(s)` | ✓ PASS |
| Full suite via direct-DLL fallback (`dotnet test` MTP discovery returns zero in this env) | `dotnet ./tests/.../Iwasug.WhatsApp.Sdk.Tests.dll` | `Total: 288, Errors: 0, Failed: 0, Skipped: 0` | ✓ PASS |
| Upstream envelope numbers | fetch `WAProto/index.js @0af2386`, read `HandshakeMessage.encode/decode` | tags 18/26/34, cases 2/3/4 | ✓ PASS (proves CR-01) |
| Upstream framing rule | fetch `src/Utils/noise-handler.ts @0af2386`, read `encodeFrame` | intro-once + 3-byte BE prefix per frame | ✓ PASS (proves CR-02 rule) |
| No TLS bypass under `src/` | grep `ServerCertificate\|ValidationCallback\|Certificate` in Connection/Noise/Codec/Transport | zero hits | ✓ PASS |
| No stub markers in phase source | grep `TODO\|FIXME\|XXX` in Connection/Noise/Codec/Transport | zero hits | ✓ PASS |
| NOTICE handshake provenance row | grep `handshake\|noise-handler\|socket.ts` in NOTICE.md | zero hits — WR-10 still open | ⚠️ noted (non-blocking, see below) |

Single full-suite run per the once-per-verification rule; no per-truth re-runs. Working tree carries unrelated uncommitted style-pass edits (incl. `STATE.md`, Phase 1 kit files); verification ran against the working tree — build 0W/0E and 288/288 hold with them, and none of the gap findings depend on those hunks.

### Probe Execution

No `scripts/*/tests/probe-*.sh` probes declared; no PLAN/SUMMARY mentions a probe driver. SKIPPED.

### Requirements Coverage

| Requirement | Source Plans | Description | Status | Evidence |
|-------------|--------------|-------------|--------|----------|
| CONN-04 | 02-01, 02-02, 02-03, 02-04, 02-05 | Auto-reconnect with backoff; discriminate disconnect reasons | ⚠️ PARTIAL | Classification + backoff + reconnect mechanics fully proven (50 tests); live-server session blocked by CR-01/CR-02 gaps |
| CONN-05 | 02-01, 02-02, 02-04, 02-05 | Typed per-account state events | ✓ SATISFIED | connecting/open/closing/logged_out asserted across drops, incl. exactly-once terminal |

No orphaned requirement IDs: REQUIREMENTS.md maps exactly CONN-04/CONN-05 to Phase 2 and all five plans claim them.

### Phase 1 Regression Lens (01-VERIFICATION.md)

Full suite 288/288 with zero failures subsumes the Phase 1 contract tests (161 at Phase 1 gate; phase totals grew monotonically 188→199→217→257→288 with no drops). No regressions observed. Uncommitted style-pass edits to Phase 1 kit files are present in the tree but the suite is green with them.

### Prohibitions (all three plan-level must-NOTs)

| Prohibition | Tier | Verdict | Evidence |
|-------------|------|---------|----------|
| MUST NOT log key material / handshake secrets | judgment | honored | 02-REVIEW audited all 19 `WaConnection` log sites (codes/attempts/account/endpoint only); handshake/router log nothing by design; my greps add no counter-evidence |
| MUST NOT auto-retry 401 or 440 | test | honored | `Decide(401/440)` no-retry pinned at depth incl. attempt-100; close-gate tests assert zero reconnect arms; 288/288 green |
| MUST NOT disable TLS cert validation | judgment | honored | Zero bypass strings under `src/` (my grep); loopback uses plaintext `ws://` only |

### Non-blocking observations (02-REVIEW warnings — left `open` for the secure-phase/fix pass, not gaps)

- **WR-01** (serverHello cert chain discarded): real responder-authentication gap, but cert verification belongs to Phase 3 pairing semantics; the handshake API must first *carry* the payload (fix alongside CR-01/CR-02). Not a Phase 2 success-criterion failure on its own.
- **WR-02** (abandoned `WatchAsync` / unobserved ODE): robustness defect worth fixing in gap closure, but no test or inspection here shows dropped reconnects because of it; not blocking the phase goal.
- **WR-10** (NOTICE.md missing handshake-envelope provenance row): confirmed still missing (grep zero hits); process/provenance item, fixable in the gap-closure commit.

### Human / Live-Server Verification Note

Live-server interop **cannot be automated here**: there is no live WhatsApp endpoint, credential, or sanctioned interop harness in this environment, and CI deliberately has no live dependency (live-interop gating is Phase 9 OPS-02 scope). After gap closure, a human (or the Phase 9 job) must prove, against the real `wss://web.whatsapp.com` or a Baileys-`encodeFrame`-shaped fixture reviewed by a human: (a) message A is accepted and message B parses; (b) the pump sustains a session past the handshake without 408-looping. The server-shaped golden-fixture tests required in the gaps above are the automatable down-payment; the final live proof is human territory. No other human verification is required — everything else in this phase is covered by executed tests.

### Gaps Summary

The phase built genuinely strong reconnect machinery (policy table, backoff math, close gate, state events, Noise crypto core — all re-proven green by this verifier) inside a **self-consistent test bubble**: every handshake/codec/router test passes because both ends speak the SDK's own wire format. Three defects pierce the bubble, all in Phase 2 files, none covered by any later phase: **(1)** invented handshake-envelope field numbers (CR-01, upstream-confirmed), **(2)** handshake/pump framing that duplicates the intro, omits length prefixes, demands a header the server never sends, and bypasses the phase's own `FrameReader` (CR-02, code- + upstream-confirmed), **(3)** an unbounded-recursion remote-crash in the decoder (CR-03, code-confirmed). Together (1)+(2) mean the phase goal — a *live* WhatsApp connection — is not achieved; (3) means adversarial wire bytes can kill the host process. Fix shape is small and well-specified in the gaps frontmatter above. Recommend `/gsd-plan-phase --gaps` against this report, then re-verification focused on the two failed truths plus a server-shaped-fixture review.

---

_Verified: 2026-10-09_
_Verifier: the agent (gsd-verifier)_
