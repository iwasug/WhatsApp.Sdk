---
phase: 02-wire-protocol-connection-resilience
plan: 02-04
subsystem: wire-protocol
tags: [binary-codec, token-dictionary, framing, query-router, baileys-mit, zlib]

# Dependency graph
requires:
  - phase: 02-02-loopback-tracer-slice
    provides: [QueryRouter skeleton with sender-delegate seam, loopback envelope helpers, MaxFrameBytes rule, single-logging-owner precedent]
provides:
  - MIT-ported token dictionary (236 + 4x256, element-verified against Baileys @0af2386)
  - Span-based BinaryNodeCodec (Encode/Decode/TryDecode, byte-identical round-trips)
  - PipeReader FrameReader (3-byte prefix, zlib flag, pre-allocation cap, partial suspend)
  - Full QueryRouter semantics (timeout, late-discard, closing fail-fast)
affects: [phase-4-messaging, 02-05-reconnect-transport]

# Actuals (#2632) — pairs with the plan's `estimate` to calibrate future estimates.
actuals:
  tokens: 29140
  tasks: 3
  commits: 3

# Tech tracking
tech-stack:
  added: []
  patterns: [flag-free-codec-with-framing-owned-flags, element-verified-mit-port, cap-before-alloc-framing, closing-fail-fast-router]

key-files:
  created:
    - src/Iwasug.WhatsApp.Sdk/Codec/BinaryNodeCodec.cs
    - src/Iwasug.WhatsApp.Sdk/Codec/TokenDictionary.cs
    - src/Iwasug.WhatsApp.Sdk/Transport/FrameReader.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Codec/BinaryNodeRoundTripTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Transport/FrameReaderTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Connection/QueryRouterTests.cs
  modified:
    - src/Iwasug.WhatsApp.Sdk/Codec/BinaryNode.cs
    - src/Iwasug.WhatsApp.Sdk/Connection/QueryRouter.cs
    - src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt
    - NOTICE.md

key-decisions:
  - "Token table verified element-by-element (236 + 4x256 identical) against downloaded Baileys constants.ts @0af2386 — a transcription typo (1430 vs 1432) was caught and fixed pre-commit"
  - "Raw string content coerces to bytes on decode (reference parity) — only token/nibble/hex/JID strings survive as Text"
  - "Compression-flag byte owned by FrameReader; BinaryNodeCodec is flag-free (behavior-equivalent split of reference decodeBinaryNode)"
  - "Skeleton envelope retained through 02-04; node-attr correlation migration deferred to Phase 4 (plan Task 3 owns semantics only)"
  - "Late-discard debug log stays in the WaConnection pump (single logging owner); TryDeliver bool is the discard contract"
  - "BinaryNodeCodec public; FrameReader and TokenDictionary internal — no public-surface growth beyond the codec seam"

patterns-established:
  - "Mitreference port verification: download the upstream source, diff element-by-element with a script, fix before commit — never trust a hand transcription"
  - "Golden pins are hand-computed first (F80156, F8051908550429), runtime must match the hand computation, not the other way round"

requirements-completed: [CONN-04, CONN-05]

# Coverage metadata (#1602)
coverage:
  - id: D1
    description: "Binary nodes round-trip byte-identically through the token-dictionary codec for children/string/bytes content forms"
    requirement: "CONN-04"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Codec/BinaryNodeRoundTripTests.cs (20 tests)"
        status: pass
    human_judgment: false
  - id: D2
    description: "Frame reader parses all complete frames per read, suspends on partials, inflates the zlib flag, and aborts on evil lengths"
    requirement: "CONN-04"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Transport/FrameReaderTests.cs (10 tests)"
        status: pass
    human_judgment: false
  - id: D3
    description: "Every query has exactly one observable outcome: reply, typed timeout, or connection-lost; late replies discarded, closing fails fast"
    requirement: "CONN-05"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Connection/QueryRouterTests.cs (10 tests)"
        status: pass
    human_judgment: false
  - id: D4
    description: "Token bytes from Baileys MIT sources with NOTICE attribution; zero whatsmeow-sourced bytes"
    requirement: "CONN-04"
    verification: []
    human_judgment: true
    rationale: "Provenance correctness is a legal judgment (right source, right commit, no copyleft contamination) that passing tests cannot establish — a human confirms the NOTICE entry and the never-from-whatsmeow guard."

# Metrics
duration: ~50 min (continuation session)
completed: 2026-10-08
status: complete
---

# Phase 02 Plan 04: Wire Codec, Framing & Router Summary

**MIT-ported token-dictionary codec with byte-identical round-trips, cap-guarded PipeReader framing with zlib, and a timeout/late-discard/closing-fail-fast query router — 40 new tests, full suite 257/257 green**

## Performance

- **Duration:** ~50 min (continuation session: Task 1 commit + Tasks 2-3 + verification)
- **Started:** 2026-10-08T20:50:00Z (approx)
- **Completed:** 2026-10-08T21:41:32Z
- **Tasks:** 3 (1 approved decision + 2 implementation)
- **Files modified:** 10 (7 created, 3 modified)

## Accomplishments

- `TokenDictionary` ports the Baileys MIT token table (236 single + 4x256 double bytes) with a per-file provenance header, then proves the transcription with an element-by-element diff against the downloaded upstream source at commit `0af2386` — one typo caught pre-commit
- `BinaryNodeCodec` encodes/decodes every content form byte-identically (token, double-byte token, nibble/hex packing, JID pair / AD-JID / FB-JID / interop forms, raw strings) with hand-computed golden pins (`F80156`, `F8051908550429`) and rule-naming `SdkFormatException` on every evil input
- `FrameReader` parses all complete frames per read over the spike-confirmed 3-byte prefix, suspends on partials, inflates the `0x02` zlib flag via in-box `ZLibStream` bounded by the same cap, and enforces `MaxFrameBytes` before allocating (proven by a 17 MiB zlib-bomb abort)
- `QueryRouter` grown to full semantics: `TimeProvider` timeout stays typed `SdkConnectionException`, TCS evicted on timeout with late arrivals discarded (`TryDeliver` false — the pump's existing debug log is the record), 64-bit ids never reused, and `NotifyConnectionClosing` fails in-flight as connection-lost while rejecting new sends fail-fast
- Full suite 257/257 green (217 pre-existing incl. the 02-02 loopback facts and 02-03 noise facts, plus this plan's 40 new), build `0W/0E`, zero package changes (T-2-SC), zero new log sites (message discipline)

## Task Commits

Each task was committed atomically:

1. **Task 1: Lock the one-way BinaryNode public shape** - `cca93d4` (feat: sealed record, factories, XML debug view + 17 PublicAPI lines; approved Option A)
2. **Task 2: Node model, MIT token table, codec, frame reader** - `db8cab2` (feat: 7 files, 30 tests green, NOTICE entry, 4 PublicAPI lines)
3. **Task 3: Full router semantics** - `04f8f82` (feat: closing fail-fast + 10 router tests green)

**Plan metadata:** summary commit (see below)

## Files Created/Modified

- `src/Iwasug.WhatsApp.Sdk/Codec/BinaryNodeCodec.cs` - Public stateless codec: `Encode`/`Decode`/`TryDecode` over spans, wire tags, list framing, packed strings, JID forms, cursor decoder with check-before-allocate discipline
- `src/Iwasug.WhatsApp.Sdk/Codec/TokenDictionary.cs` - Internal immutable token table (Baileys MIT port, provenance header) + ordinal lookup map + bounds-checked getters
- `src/Iwasug.WhatsApp.Sdk/Transport/FrameReader.cs` - Internal static parser: `TryReadFrame` pure step + `ReadAllAsync` fill/parse loop, flag/inflate handling, cap-before-alloc, bomb bound
- `src/Iwasug.WhatsApp.Sdk/Codec/BinaryNode.cs` - Task 1 approved shape (committed first, untouched after)
- `src/Iwasug.WhatsApp.Sdk/Connection/QueryRouter.cs` - Added `_closing` flag, closing fail-fast guard, `NotifyConnectionClosing`; envelope-retention remark update
- `tests/.../Codec/BinaryNodeRoundTripTests.cs` - 20 tests: goldens, all string encodings, JID forms, normalization, TryDecode, truncation sweep, 6 evil cases, table pins
- `tests/.../Transport/FrameReaderTests.cs` - 10 tests: golden, coalesced, byte-by-byte split, partial suspends, max-prefix parks, truncation-on-close, empty payload, inflate, corrupt envelope, zlib bomb
- `tests/.../Connection/QueryRouterTests.cs` - 10 tests: correlation, unique ids, fake-time timeout, late-discard + id freshness, closing fail-fast (new + in-flight), sender-fault eviction, cancellation, dispose, options guard
- `src/.../PublicAPI.Unshipped.txt` - 21 additive lines total (17 Task 1 + 4 codec), zero deletions
- `NOTICE.md` - Two provenance log rows (token table port + codec logic-shape reference)

## Decisions Made

- Token-table transcription verified mechanically, not by eye: downloaded `constants.ts` (18,784 bytes, size-matched to the registry listing) and diffed all 1,260 entries with a script — the single mismatch (`1430` vs `1432` in double dict 2) was fixed before any commit.
- Raw string content coerces to bytes on decode (reference parity, documented in codec remarks): `FromText("message","hello world!")` decodes to `Data`, not `Text`. Only token/nibble/hex/JID strings survive as `Text`. Phase 4 must place message text accordingly.
- Compression-flag byte owned by `FrameReader`; codec sees flag-free node bytes (behavior-equivalent split of reference `decodeBinaryNode`, which takes the flagged buffer).
- Skeleton `idLength||id||body` envelope retained: plan Task 3 tasks semantics only, and the envelope's consumers (loopback peer, pump, 11 green tracer facts) move together in Phase 4 with real node traffic.
- Late-discard debug log stays in the `WaConnection` pump (lines 611-614 already log it) — giving the router a logger would break the 02-02 single-owner decision; the `TryDeliver` bool is the discard contract the tests pin.
- `BinaryNodeCodec` public (Phase 4 builds on it); `FrameReader`/`TokenDictionary` internal (transport-internal detail + shared table) — 4 new public declarations, nothing more.
- The 3-byte width caps encodable lengths at `0xFFFFFF` (cap − 1), so no length prefix can violate `MaxFrameBytes`: the prefix-cap check stays as defense-in-depth while the abort path is proven by the zlib-bomb test. Cap value itself untouched (02-01 locked).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Token transcription typo caught by the verification script**
- **Found during:** Task 2 (table verification, pre-commit)
- **Issue:** Double dict 2 entry written as `1430`, upstream has `1432` (both `1430` and `1432` exist in that dict at different indices)
- **Fix:** One-line correction; re-ran the element diff → `TABLES IDENTICAL` (236 + 4x256)
- **Files modified:** `src/.../Codec/TokenDictionary.cs`
- **Verification:** check-tokens.py exit 0; table-pin tests green
- **Committed in:** `db8cab2` (Task 2 commit)

**2. [Rule 1 - Bug] Three test expectations contradicted reference behavior (tests wrong, code right)**
- **Found during:** Task 2 (first red run: 25/30)
- **Issue:** (a) Raw-text round-trip expected `Text` back — reference encodes raw strings as `BINARY_8`, which decodes as bytes. (b) Duplicate attr keys expected rejection — reference resolves last-wins (and the codec remarks already documented it). (c) `BINARY_32` giant length expected a "range" message — simplified `ReadInt32` to a single remainder check so it reports truncation.
- **Fix:** Rewrote the three tests to pin reference behavior (coercion test, last-wins test, truncation message) and simplified `ReadInt32` (dropped the dead range branch — the remainder never exceeds `int.MaxValue`, so beyond-range is already beyond-remainder)
- **Files modified:** `BinaryNodeRoundTripTests.cs`, `BinaryNodeCodec.cs` (remarks + `ReadInt32`)
- **Verification:** 30/30 green
- **Committed in:** `db8cab2` (Task 2 commit)

**3. [Rule 1 - Bug] Frame cap tests assumed a violable prefix (tests wrong, wire right)**
- **Found during:** Task 2 (first red run)
- **Issue:** Tests expected `0xFFFFFF` and cap+1 prefixes to throw — but the 3-byte width maxes at cap − 1, so the prefix-cap path is unreachable by construction
- **Fix:** Replaced the three tests with one honest test (max prefix parks without allocating) plus the bomb test as the live cap proof; documented the width-vs-cap fact in the test and this summary
- **Files modified:** `FrameReaderTests.cs`
- **Verification:** 30/30 green; bomb test aborts at ~17 MiB inflated
- **Committed in:** `db8cab2` (Task 2 commit)

**4. [Rule 3 - Blocker] Test-only compile fixes (first build)**
- **Found during:** Tasks 2-3 (build + test compile)
- **Issue:** Collection expressions don't target `ReadOnlyMemory<byte>` (11 sites); `Task<T>.Should()` has no `ThrowAsync` (4 sites); static `ReadAllAsync` called via instance syntax; `void` test method containing `await`; 4 missing `PublicAPI.Unshipped.txt` declarations (RS0016)
- **Fix:** `Payload(params byte[])` helper; `Func<Task>` observer wrappers; `FrameReader.ReadAllAsync` static calls; `async Task` signature; 4 harvested RS0016 lines appended in ordinal position (purely additive)
- **Files modified:** `QueryRouterTests.cs`, `FrameReaderTests.cs`, `PublicAPI.Unshipped.txt`
- **Verification:** Build `0W/0E`; suites green
- **Committed in:** `db8cab2` / `04f8f82` (respective task commits)

---

**Total deviations:** 4 auto-fixed (3 Rule 1, 1 Rule 3)
**Impact on plan:** No scope creep. All prohibitions honored: no log sites added (no key-material risk), no 401/440/419 retry path touched, no TLS code touched, zero package changes, no sibling 02-03 files touched, no STATE.md/ROADMAP.md modifications (per continuation instructions — orchestrator owns those).

## Issues Encountered

- `Func<bool> read = () => FrameReader.TryReadFrame(ref buffer, out _)` pattern abandoned with the cap-test rewrite (ref-struct capture in lambdas was never needed once suspension replaced throwing).
- Parallel-wave style edits are churning nearby files (e.g. `var`↔explicit swaps); both task commits stage only plan files, and one `Edit` needed a retry after the file shifted underneath.

## Known Stubs

None — every new symbol is fully implemented. The envelope-retention and raw-string coercion items are documented reference-parity behaviors, not stubs.

## Threat Flags

None beyond the plan's register — all four mitigations land as tasked: T-2-06 (cap pre-alloc + bomb bound + bounds checks + evil fixtures + rule-naming throws), T-2-08 (monotonic ids + timeout eviction + late discard + closing fail-fast + typed timeout), T-2-13 (Baileys-only port + provenance header + NOTICE rows + never-from-whatsmeow guard), T-2-SC (zero package changes verified — no csproj/props diff in any plan commit).

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Ready for 02-05 (reconnect lifecycle + WS transport): consumes `FrameReader.ReadAllAsync` (pump shape reference), `NotifyConnectionClosing` (the drop trigger — currently uncalled; 02-05 wires it), `MaxFrameBytes` + 3-byte rule.
- Ready for Phase 4 (messaging): consumes `BinaryNode` + `BinaryNodeCodec` (message/receipt nodes ride it); note the raw-string→bytes coercion when placing message text; envelope→node-attr correlation migration rides with real node traffic.
- Watch item: `check-tokens.py` / `dbg-tokens.py` live in the temp dir (not committed) — re-download `constants.ts` @ `0af2386` if the table ever needs re-verification.
- Pre-existing uncommitted worktree changes from parallel waves were left untouched; orchestrator reconciles at merge.

---
*Phase: 02-wire-protocol-connection-resilience*
*Completed: 2026-10-08*

## Self-Check: PASSED

- All 6 created files exist on disk (Codec x2, Transport x1, tests x3) — verified via build inputs compiling
- Commits exist: `cca93d4` (Task 1), `db8cab2` (Task 2), `04f8f82` (Task 3) all in `git log`
- `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror` → 0 Warning(s), 0 Error(s)
- `dotnet test --filter FullyQualifiedName~BinaryNode|FullyQualifiedName~FrameReader` → 30/30 passed; `--filter FullyQualifiedName~QueryRouter` → 10/10 passed; full suite → 257/257 passed
- No src/tests file invented outside plan `files_modified` (temp verification scripts uncommitted by design)
- No STATE.md / ROADMAP.md modifications (per continuation instructions)
