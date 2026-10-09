---
phase: 02-wire-protocol-connection-resilience
plan: 02-07
subsystem: transport
tags: [whatsapp, noise-xx, framing, protobuf, websocket, dotnet, clean-room]

# Dependency graph
requires:
  - phase: 02-wire-protocol-connection-resilience (02-06)
    provides: reconnect machinery with reason discrimination, QueryRouter, FrameReader parser, loopback test harnesses
provides:
  - shared WaFrame 3-byte big-endian framing helper serving both handshake and pump paths
  - bare-protobuf handshake writers/parsers with intro header validated once at the connection edge
  - WaConnection intro-once connect flow, fail-closed server-shaped B consumption, FrameReader-wired watch pump
  - server-shaped message B end-to-end proof with cross-direction key agreement
  - NOTICE.md handshake-envelope provenance row (Baileys @ 0af2386)
affects: [02-08, verify-work, secure-phase]

# Actuals (#2632) — pairs with the plan's `estimate` to calibrate future estimates.
actuals:
  tokens: 9090
  commits: 3
  tasks: 3

# Tech tracking
tech-stack:
  added: []
  patterns: [intro-once framing validated at the edge, one framing helper for handshake and pump, FrameReader as the single inbound pump parser]

key-files:
  created:
    - src/Iwasug.WhatsApp.Sdk/Transport/WaFrame.cs
  modified:
    - src/Iwasug.WhatsApp.Sdk/Noise/NoiseHandshake.cs
    - src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Noise/NoiseHandshakeTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Connection/LoopbackTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Connection/LoopbackRoundTripTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ConnectionStateTests.cs
    - NOTICE.md

key-decisions:
  - "Server-shaped B is consumed intro-first: the 4-byte intro is validated and stripped BEFORE WaFrame.Deframe (server shape is intro || len || bytes); headerless B (loopback shape) deframes directly — the intro prefix can never collide with a real length because a 0x574106-prefixed frame would exceed MaxFrameBytes in practice"
  - "Any framing or intro violation while consuming B wraps into ProtocolHaltException (fail-closed terminal halt) instead of propagating as a retryable transport error — protocol garbage must never spin the reconnect loop"
  - "WatchAsync drains its per-attempt buffer through FrameReader.TryReadFrame (flag strip + zlib inflate owned by the shared parser) before NoiseTransport.Decrypt; the hand-rolled length parse and the dead WatchAsync_OLD were removed so zero production bypass remains"
  - "Loopback fakes call the production NoiseHandshake.ValidateIntroHeader instead of a hardcoded byte equality — the fakes exercise the real edge validation"

patterns-established:
  - "Single framing helper: WaFrame.Frame/Deframe owns every 3-byte length prefix; FrameReader owns every inbound pump frame"
  - "Intro-once: the WA intro is sent exactly once per connection attempt and validated exactly once per received reply at the edge"

requirements-completed: [CONN-04, CONN-05]

# Coverage metadata (#1602) — one entry per shipped deliverable.
coverage:
  - id: D1
    description: "Unified 3-byte big-endian framing helper (WaFrame) with bare-protobuf handshake conversion and intro validated once at the edge"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Noise/NoiseHandshakeTests.cs (16 facts incl. Writer_output_starts_with_the_client_hello_wire_tag_0x12, Intro_header_validation_accepts_the_current_version_and_rejects_mismatches)"
        status: pass
    human_judgment: false
  - id: D2
    description: "WaConnection intro-once connect flow with FrameReader-wired watch pump; loopback reconnect outcomes (per disconnect code) pass over the framed flow"
    requirement: CONN-04
    verification:
      - kind: integration
        ref: "dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter \"FullyQualifiedName~Loopback|FullyQualifiedName~ConnectionState|FullyQualifiedName~QueryRouter\" (36/36 pass, 2026-10-09)"
        status: pass
      - kind: integration
        ref: "dotnet test Iwasug.WhatsApp.Sdk.sln (300/300 pass, 2026-10-09)"
        status: pass
    human_judgment: false
  - id: D3
    description: "Connection-state sequence facts (connecting, open, closing, exactly-once logged_out) preserved unchanged over the corrected framing"
    requirement: CONN-05
    verification:
      - kind: integration
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ConnectionStateTests.cs via Loopback|ConnectionState|QueryRouter filter (36/36 pass, 2026-10-09)"
        status: pass
    human_judgment: false
  - id: D4
    description: "Server-shaped message B (intro || 3-byte length || bare ServerHello envelope) consumed into a live NoiseTransport with agreeing keys, proven by responder-encrypt/initiator-decrypt probe equality"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Noise/NoiseHandshakeTests.cs#Server_shaped_message_b_intro_plus_frame_is_consumed_and_keys_agree (16/16 pass, 2026-10-09)"
        status: pass
    human_judgment: false
  - id: D5
    description: "NOTICE.md provenance row for the Baileys handshake-envelope read (noise-handler.ts, socket.ts, WAProto @ 0af2386) naming the three consumer files"
    verification:
      - kind: other
        ref: "NOTICE.md#Log row 2026-10-09 (present and verified against plan Task 3 wording)"
        status: pass
    human_judgment: false

# Metrics
duration: 35min
completed: 2026-10-09
status: complete
plan_head_before: 817150d9089365d3063109ca735558b62b0b365d
plan_head_after: 74f778d04122cd0a2f99653f604907ea75a06db4
---

# Phase 2 Plan 07: Intro-Once Framing Unification Summary

**Shared WaFrame 3-byte framing behind handshake and pump, intro-once connection flow with intro-first fail-closed B consumption, FrameReader wired into the production watch loop, and a proven server-shaped message B with agreeing transport keys (300/300 suite green)**

## Performance

- **Duration:** 35 min (continuation execution run; resumed partial work from an interrupted earlier run was verified and completed in this run)
- **Started:** 2026-10-09T10:38:00+07:00
- **Completed:** 2026-10-09T11:13:00+07:00
- **Tasks:** 3 of 3
- **Files modified:** 8

## Accomplishments

- One helper (`WaFrame.Frame`/`Deframe`, MaxFrameBytes-capped, rule-naming `SdkTransportException`) now owns every 3-byte big-endian length prefix; the handshake speaks bare `HandshakeMessage` protobuf with the WA intro sent exactly once per attempt and validated exactly once at the edge (`ValidateIntroHeader`, tags 0x12/0x1A/0x22 wire-shape facts green).
- `WaConnection` consumes server-shaped B as `intro || len || bytes`: intro validated/stripped before deframing, framing violations wrapped into `ProtocolHaltException` (fail-closed — protocol garbage halts instead of spinning the retry loop); the watch pump drains its per-attempt buffer through `FrameReader.TryReadFrame` before `NoiseTransport.Decrypt`, with the hand-rolled length parse and dead `WatchAsync_OLD` removed; routed sends prepend the `0x00` flag and wrap with `WaFrame.Frame`.
- New tracer fact proves a real-shaped reply end to end: intro validated once, remainder deframed, message B consumed, handshake completed, and a responder-encrypt → initiator-decrypt probe asserts byte equality (agreeing keys).
- NOTICE.md gained the Baileys handshake provenance row (`noise-handler.ts` intro-once + 3-byte prefix, `socket.ts` bare-protobuf A, `WAProto/index.js` tags 18/26/34, all @ `0af2386`) pointing at the three consumer files — closing the T-2-16/WR-10 gap.
- Zero diff in `QueryRouter.cs`; build 0W/0E; full solution suite **300/300 pass** (297 baseline + 3 new facts).

## Task Commits

Each task was committed atomically:

1. **Task 1: Shared frame helper + bare-protobuf handshake** - `74a9e36` (feat)
2. **Task 2: Intro-once connection flow plus FrameReader-wired pump** - `8c73812` (feat)
3. **Task 3: End-to-end server-shaped message B + provenance row (tracer)** - `74f778d` (test)

Measured from ledger `817150d` (plan_head_before) to `74f778d` (plan_head_after): **3 commits**.

## Files Created/Modified

- `src/Iwasug.WhatsApp.Sdk/Transport/WaFrame.cs` - NEW: shared 3-byte big-endian Frame/Deframe helper (cap-enforced, no flag processing — handshake bodies are flag-free)
- `src/Iwasug.WhatsApp.Sdk/Noise/NoiseHandshake.cs` - bare-protobuf writers/parsers (header prepend removed), new `ValidateIntroHeader`, wire-shape docs updated
- `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs` - intro-once send, framed A/C, intro-first fail-closed B receive, FrameReader-wired buffered watch pump, flagged framed routed sends, `WatchAsync_OLD` deleted
- `tests/Iwasug.WhatsApp.Sdk.Tests/Noise/NoiseHandshakeTests.cs` - writer tag 0x12 fact, intro validation accept/reject fact, server-shaped B end-to-end fact with probe equality
- `tests/Iwasug.WhatsApp.Sdk.Tests/Connection/LoopbackTests.cs` - fake validates intro via production `ValidateIntroHeader`; framed flow both ends
- `tests/Iwasug.WhatsApp.Sdk.Tests/Connection/LoopbackRoundTripTests.cs` - same production intro validation on the peer handshake path
- `tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ConnectionStateTests.cs` - same production intro validation (see deviation #2 — not in plan files list)
- `NOTICE.md` - provenance log row dated 2026-10-09 for the Baileys handshake-framing read

## Decisions Made

- **Intro-first B consumption:** the received B message is checked for the 4-byte intro before deframing (server shape is `intro || len || bytes`); a deframe-first order would read the intro's `57 41 06` as a length prefix and throw. Headerless B (loopback shape) is accepted unchanged, matching the plan's "accept headerless B/C" objective.
- **Fail-closed protocol halt:** `SdkTransportException` from `Deframe`-of-B wraps into `ProtocolHaltException` (Rule 2). Without it, framing violations surfaced as a generic exception and hit the reconnect-retry path — protocol garbage would recycle forever instead of halting.
- **Single parser on the pump:** `FrameReader.TryReadFrame` now owns flag-strip/zlib handling for every inbound pump byte; the previous inline parse duplicated that logic and bypassed the compression path.
- **Production validation in fakes:** all three loopback fakes replaced a hardcoded intro byte-equality with `NoiseHandshake.ValidateIntroHeader`, so the edge validation runs on every loopback build.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Received-B consumed in the wrong order (deframe-before-intro)**
- **Found during:** Task 2 (Intro-once connection flow)
- **Issue:** The resumed partial implementation deframed message B *before* the intro check; on a real server-shaped B (`intro || len || bytes`) the intro bytes would be read as a length prefix, throwing `SdkTransportException` on every real handshake.
- **Fix:** Intro validated/stripped first, then `WaFrame.Deframe` on the remainder; framing violations now wrap into `ProtocolHaltException` (fail-closed).
- **Files modified:** `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs`
- **Verification:** Loopback|ConnectionState|QueryRouter filter 36/36 pass; full suite 300/300 pass.
- **Committed in:** `8c73812` (Task 2 commit)

**2. [Rule 2 - Missing Critical] Third loopback fake outside the plan's file list**
- **Found during:** Task 2 (plan `<files>` listed only `WaConnection.cs`, `LoopbackTests.cs`, `LoopbackRoundTripTests.cs`)
- **Issue:** `ConnectionStateTests.cs` carries the same hardcoded intro assertion; leaving it unconverted would keep a divergent assertion on a shared protocol edge and risk future suite drift.
- **Fix:** Converted to production `NoiseHandshake.ValidateIntroHeader` alongside the two planned fakes; file added to the Task 2 commit as a documented out-of-plan file.
- **Files modified:** `tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ConnectionStateTests.cs`
- **Verification:** same 36/36 run (the file's suite is in the verify filter).
- **Committed in:** `8c73812` (Task 2 commit)

**3. [Rule 1 - Bug] Dead `WatchAsync_OLD` left behind by the interrupted run**
- **Found during:** Task 2
- **Issue:** The resumed working tree contained both the new buffered pump and an obsolete `WatchAsync_OLD` method (dead code, zero callers) — an intermediate artifact of the interrupted session.
- **Fix:** Deleted as part of the pump rewire so the plan's "zero production bypass" key-link is structurally true.
- **Files modified:** `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs`
- **Committed in:** `8c73812` (Task 2 commit)

### Resumed-Work Note

This plan executed as a **continuation**: an interrupted earlier run had already left partial, uncommitted implementation in the tree (intro send, framed A/C sends, `0x00`-flagged framed routed sends, loopback framed flow, Task 1 handshake conversion + test facts). Per continuation protocol this work was **not reverted** — it was audited against the plan scope, corrected where wrong (deviation #1), completed, and committed inside Tasks 1–2.

---

**Total deviations:** 3 auto-fixed (2× Rule 1, 1× Rule 2)
**Impact on plan:** All deviations serve plan correctness (byte-compatibility with the real server, plan key-links). No scope creep; zero public-API and zero `QueryRouter.cs` changes.

## Issues Encountered

- **Windows Smart App Control intermittently blocked freshly built assemblies** (`FileLoadException ... Application Control policy has blocked this file (0x800711C7)`, Code Integrity events 3033/3077/3118, `MadeCloudCall=false` on the denied hash). Affected Task 1 and Task 3 verification attempts. Resolution was environment-only, no repo changes: rebuilds producing a fresh assembly hash cleared the verdict, and one run used `-p:Deterministic=false` so the incremental build could not regenerate a negatively-cached hash. Final full-suite verification passed under default flags (300/300). **Carry-forward caveat for future runs:** if `dotnet test` is suddenly denied with `0x800711C7`, rebuild the affected project for a fresh hash or pass `-p:Deterministic=false` — do not treat it as a code failure, and do not disable SAC without a human decision.
- Minor: `git status --cached` is not a valid flag (used `git diff --cached --name-only`); the default `bash` on this host is WSL and rejects `C:/` paths (pin guard runs via Git bash explicitly).

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Gap 2 (CR-02 framing truth 1) is closed: one helper owns all length-prefix work, the pump deframes through `FrameReader` on the production path, and a real-shaped message B is proven consumable with agreeing keys — `verify-work` should find the framing truths green.
- `QueryRouter.cs` is untouched, so router-dependent plans (02-08+) proceed without reconciliation.
- Environment caveat for the next executor on this machine: the SAC block behavior above (see Issues Encountered).

---
*Phase: 02-wire-protocol-connection-resilience*
*Completed: 2026-10-09*

## Self-Check: PASSED

- SUMMARY.md present at `.planning/phases/02-wire-protocol-connection-resilience/02-07-SUMMARY.md`
- Commits found in history: `74a9e36`, `8c73812`, `74f778d`, `54e484c`
- Key artifacts found: `src/Iwasug.WhatsApp.Sdk/Transport/WaFrame.cs`, `NOTICE.md`
