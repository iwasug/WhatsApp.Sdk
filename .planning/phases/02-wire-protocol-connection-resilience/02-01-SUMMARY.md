---
phase: 02-wire-protocol-connection-resilience
plan: 02-01
subsystem: connection
tags: [noise-protocol, reconnect-policy, transport-seam, waapi, nsec, protobuf]
requires:
  - phase: 01-foundation-store-seam
    provides: [typed exception hierarchy, per-account isolation precedents, clamp-after-jitter retry discipline, PublicAPI tracking]
provides:
  - Spike-confirmed wire constants (AESGCM suite, WA header, 3-byte framing rule, endpoint/Origin)
  - IFrameTransport seam + in-memory loopback fake for socket-free testing
  - Pure ReconnectPolicy.Decide table with 20 green unit tests
  - Connection vocabulary (DisconnectReason incl. 419-terminal, ConnectionState, ConnectionOptions)
affects: [02-02-tracer, 02-03-noise-handshake, 02-04-codec, pairing-phase-3]

actuals:
  tokens: 18700
  tasks: 3
  commits: 2

tech-stack:
  added: [NSec.Cryptography 26.4.0, Google.Protobuf 3.36.2, Grpc.Tools 2.84.0 (PrivateAssets all)]
  patterns: [pure code-to-action policy function, CT-first ValueTask transport seam, pipe-pair loopback fake]

key-files:
  created:
    - src/Iwasug.WhatsApp.Sdk/Noise/NoiseProtocol.cs
    - src/Iwasug.WhatsApp.Sdk/Connection/DisconnectReason.cs
    - src/Iwasug.WhatsApp.Sdk/Connection/ConnectionState.cs
    - src/Iwasug.WhatsApp.Sdk/Connection/ConnectionOptions.cs
    - src/Iwasug.WhatsApp.Sdk/Connection/ReconnectPolicy.cs
    - src/Iwasug.WhatsApp.Sdk/Transport/IFrameTransport.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Transport/LoopbackFrameTransport.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ReconnectPolicyTests.cs
  modified:
    - Directory.Packages.props
    - src/Iwasug.WhatsApp.Sdk/Iwasug.WhatsApp.Sdk.csproj
    - src/Iwasug.WhatsApp.Sdk/Errors/SdkException.cs
    - src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt

key-decisions:
  - "419 maps to Terminal via a new AuthExpired member (no Baileys enum name exists for 419; named for HTTP-419 auth-expired semantics, documented as UNAUTHORIZED_CODES membership)"
  - "411 MultideviceMismatch maps to Terminal (plan action text governs; success-criteria 'stop' means no-retry either way)"
  - "ReconnectDecision is an abstract record with nested sealed singletons (Terminal/RetryAfter/RestartImmediate/SurfaceAndStop); Decide is a static pure function, unknown codes get bounded RetryAfter with the loud-log obligation documented on the caller"
  - "Full-jitter delay in double arithmetic capped before the draw: overflow-proof at any attempt, clamp-after-jitter keeps every delay within [0, cap]"
  - "Loopback fake uses harness-private 4-byte BE length framing (the wire's 3-byte prefix belongs to 02-04 FrameReader); MaxFrameBytes 16 MiB adopted from RESEARCH Pitfall 3"
  - "Endpoint/Origin consts deferred to 02-02 (outside Task 3 file list); no timedOut=408 enum alias (duplicate values break host switch statements — overload noted in remarks)"

patterns-established:
  - "Policy-as-pure-function: Decide takes (statusCode, attempt, options) and returns a record verdict — no I/O, no timers, no logging; attempt counting and unknown-code logging are caller duties"
  - "Transport seam mirrors ISignalSessionStore shape: CT-first, ValueTask, ThrowIfCancellationRequested first line, ConfigureAwait(false), exception-cref XML docs"

requirements-completed: [CONN-04, CONN-05]

coverage:
  - id: D1
    description: "ReconnectPolicy.Decide answers every disconnect code (401/403/411/419 terminal, 515 restart, 440 stop, rest bounded retry)"
    requirement: "CONN-04"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ReconnectPolicyTests.cs (20 tests)"
        status: pass
    human_judgment: false
  - id: D2
    description: "IFrameTransport seam compiles; LoopbackFrameTransport pipe-pair fake in place for 02-02"
    requirement: "CONN-04"
    verification:
      - kind: other
        ref: "dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror → 0 Warning(s), 0 Error(s)"
        status: pass
    human_judgment: true
    rationale: "The fake compiles and satisfies the seam, but no test has pushed a frame through it yet — its first real exercise is the 02-02 tracer round-trip."
  - id: D3
    description: "Spike-confirmed wire constants and connection vocabulary (NoiseProtocol, enums, options, exception subtypes)"
    requirement: "CONN-05"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ReconnectPolicyTests.cs#ConnectionOptions_defaults_match_spike_confirmed_socket_config"
        status: pass
    human_judgment: true
    rationale: "Constant VALUES are spike-sourced (02-SPIKE-NOTES.md F1–F3/F5), not test-proven; byte-level proof arrives with 02-03 cacophony vectors and Phase 9 interop."

duration: ~25min (Task 3 continuation; full plan spans 2026-10-08 sessions)
completed: 2026-10-08
status: complete
---

# Phase 02 Plan 01: Wire Seams, Constants & Policy Table Summary

**Spike-locked wire constants, CT-first transport seam, and a pure unit-proven reconnect table (401/419 never retry) behind human-approved surface**

## Performance

- **Duration:** ~25 min (Task 3 continuation; Tasks 1–2 completed in prior sessions 2026-10-08)
- **Started:** 2026-10-08 (plan overall)
- **Completed:** 2026-10-08
- **Tasks:** 3 (1 spike + 1 approved decision checkpoint + 1 implementation)
- **Files modified:** 12 (Task 3); 1 planning note (Task 1)

## Accomplishments

- 02-SPIKE-NOTES.md records 19 CONFIRMED/CONTRADICTED verdicts over Baileys MIT sources (AESGCM cipher, protobuf Message-A, 3-byte prefix, no pre-auth iq, endpoint/Origin, NSec shape, pin currency) with fail-closed fallbacks — spike values govern on contradiction
- `ReconnectPolicy.Decide` pure table unit-proven: 401/403/411/419 → Terminal (no retry arm at any attempt), 515 → RestartImmediate, 440 → SurfaceAndStop, 408/428/500/503 + unknown → bounded full-jitter RetryAfter (20/20 tests green)
- `IFrameTransport` seam (CT-first, ValueTask, close-code protocol) compiles against Phase 1 conventions with a tests-only pipe-pair loopback fake (instance pipes, no statics) ready for the 02-02 tracer
- Central pins NSec.Cryptography 26.4.0 / Google.Protobuf 3.36.2 / Grpc.Tools 2.84.0 (PrivateAssets all); sealed `SdkTransportException` + `SdkConnectionException`; 93 PublicAPI.Unshipped.txt declarations; full suite 188/188 green

## Task Commits

Each task was committed atomically:

1. **Task 1: Read-only Baileys spike** - `62d03b8` (docs: record spike findings; prior session — not redone)
2. **Task 2: Lock one-way public surface** - checkpoint:decision, approved as proposed with the 419 → Terminal amendment (no code commit by design)
3. **Task 3: Seams, constants, policy table** - `004d7fb` (feat)

**Plan metadata:** this summary + commit (see below)

## Files Created/Modified

- `src/Iwasug.WhatsApp.Sdk/Noise/NoiseProtocol.cs` - Internal wire consts (AESGCM name, 32-byte pad rule, WA header `[57 41 06 03]`, DictVersion 3, 16 MiB cap) with spike citations + fail-closed remarks
- `src/Iwasug.WhatsApp.Sdk/Connection/DisconnectReason.cs` - 10-member code enum (values ARE the wire contract) incl. new `AuthExpired = 419` terminal + 408-overload note
- `src/Iwasug.WhatsApp.Sdk/Connection/ConnectionState.cs` - Exactly Connecting/Open/Closing/LoggedOut with locked transition rule in remarks (T-2-07)
- `src/Iwasug.WhatsApp.Sdk/Connection/ConnectionOptions.cs` - Spike defaults (20000/60000/30000/1000/60000) + internal ValidateOptions fail-fast guard
- `src/Iwasug.WhatsApp.Sdk/Connection/ReconnectPolicy.cs` - `ReconnectDecision` record hierarchy + static pure `Decide` with full-jitter/clamp-after-jitter delay math
- `src/Iwasug.WhatsApp.Sdk/Transport/IFrameTransport.cs` - Byte-duplex seam (Send/Receive-null-on-close/CloseStatusCode/CloseAsync) + close protocol remarks
- `tests/.../Transport/LoopbackFrameTransport.cs` - Pipe-pair fake, harness-private 4-byte BE framing, shared close codes, full CT/disposal discipline
- `tests/.../Connection/ReconnectPolicyTests.cs` - 20 tests: terminal-never-retried (incl. 401 at attempt 100, 419 amendment), stop/restart, retry bounds, 0–30 attempt cap sweep, negative-attempt + invalid-options guards, defaults pin test
- `Directory.Packages.props` - Three central pins with provenance comment
- `src/.../Iwasug.WhatsApp.Sdk.csproj` - Versionless references (NSec, Protobuf, Grpc.Tools PrivateAssets=all) so restore materializes the pins
- `src/.../Errors/SdkException.cs` - Two sealed subtypes, operation-naming message discipline
- `src/.../PublicAPI.Unshipped.txt` - 93 declarations harvested mechanically from RS0016 output, pure-addition diff (93+/0-)

## Decisions Made

- 419 → Terminal via new `AuthExpired = 419` member: no Baileys enum name exists (only UNAUTHORIZED_CODES membership per O1); name reflects HTTP-419 auth-expired semantics. Without this, 419 fell into "unknown → bounded retry" — a reconnect loop against dead credentials (T-2-01 class).
- 411 → Terminal per Task 3 action text (success-criteria "440/411 stop" means no-retry either way; Terminal vs SurfaceAndStop differ only in host signal: re-pair/redeploy vs explicit restart).
- `Decide` stays static-pure with no logger: the unknown-code "loud log" is a documented caller obligation for the 02-02 loop (purity is what makes the table exhaustively testable).
- No `timedOut = 408` enum alias: duplicate values break host `switch` statements (duplicate case labels); the overload is documented in remarks + policy docs instead.
- Endpoint/Origin consts deferred to 02-02: Task 3's file list has no home for them; noted as 02-02 input (spike F5 values: `wss://web.whatsapp.com/ws/chat`, `Origin: https://web.whatsapp.com`).

## Deviations from Plan

### Plan-contemplated adjustment (not a rule fix)

**SDK csproj carries versionless package references (file outside frontmatter `files_modified`)**
- **Found during:** Task 3 (package pins)
- **Issue:** Plan action says "Add central pins … to Directory.Packages.props — never inline in csproj — then restore". Pins alone restore nothing; without a versionless reference the `dotnet restore` step and T-2-SC ("restores only these three pinned versions") are vacuous.
- **Fix:** Added versionless `PackageReference` lines (NSec, Protobuf, Grpc.Tools with PrivateAssets=all) to `Iwasug.WhatsApp.Sdk.csproj` — "never inline" honored (no versions in csproj). No src/tests file *invented* outside the list.
- **Files modified:** `src/Iwasug.WhatsApp.Sdk/Iwasug.WhatsApp.Sdk.csproj`
- **Verification:** `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror` 0W/0E; full suite 188/188 green
- **Committed in:** 004d7fb (Task 3 commit)

### Auto-fixed Issues

**1. [Rule 1 - Bug] Missing `using System.Buffers` + invalid `paramref` in new code**
- **Found during:** Task 3 (first solution build)
- **Issue:** `ReadOnlySequence<>` unresolved in LoopbackFrameTransport (ImplicitUsings excludes System.Buffers); CS1734 `paramref="attempt"` on the `ReconnectPolicy` class remarks (no such parameter at class scope)
- **Fix:** Added the using; reworded to `<c>attempt</c>`
- **Files modified:** `tests/.../Transport/LoopbackFrameTransport.cs`, `src/.../Connection/ReconnectPolicy.cs`
- **Verification:** Rebuild 0W/0E
- **Committed in:** 004d7fb (Task 3 commit)

---

**Total deviations:** 1 adjustment + 1 auto-fixed (Rule 1, compiler-flagged in new code)
**Impact on plan:** No scope creep. All prohibitions honored: no key-material logging (no log sites added), no 401/440/419 retry path (table + tests), no TLS validation touched.

## Issues Encountered

- PublicAPI.Unshipped.txt merge churn: an initial case-insensitive file sort repositioned 9 pre-existing lines; detected via `git diff` (9 deletions) and repaired with a true ordinal sort plus manual restoration — final diff is purely additive (93+/0-). No trace left in the commit.
- Pre-existing uncommitted working-tree changes from parallel waves (Phase 1 style pass) were left untouched; Task 3 staged its 12 files individually per protocol.

## Known Stubs

None — every new symbol is fully implemented. `LoopbackFrameTransport` is a complete fake (not a stub); its first behavioral exercise is the 02-02 tracer (coverage D2).

## Threat Flags

None — no new network endpoints, auth paths, file access, or schema changes. The policy table itself is the T-2-01 mitigation (401/419 Terminal proven by test); T-2-07 transition rule is locked in `ConnectionState` remarks; T-2-SC pins verified + PrivateAssets=all.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Ready for 02-02 (tracer): consumes `IFrameTransport` + loopback pair + `ReconnectPolicy.Decide`; needs endpoint/Origin consts + `WebSocketFrameTransport` (its scope, using spike F5 values).
- 02-03 consumes: `NoiseProtocol` consts + NSec X25519 call shape (spike F6 type names recorded).
- 02-04 consumes: 3-byte BE framing rule (F3) + `MaxFrameBytes` cap; token dictionary bytes (NOT in this plan — 02-04 with NOTICE attribution).
- 02-05 completes backoff math/integration proofs (401-over-fake zero-reconnect, 515-during-backoff, 401-during-connecting interleavings per seeds 2-seed-01/2-seed-07).

---
*Phase: 02-wire-protocol-connection-resilience*
*Completed: 2026-10-08*

## Self-Check: PASSED

- All 8 created files exist on disk (Noise ×1, Connection ×4, Transport ×1, tests ×2) — verified via build inputs compiling
- Commits exist: `62d03b8` (Task 1) and `004d7fb` (Task 3) both in `git log`
- `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror` → 0 Warning(s), 0 Error(s)
- `dotnet test --filter FullyQualifiedName~ReconnectPolicy` → 20/20 passed; full suite 188/188 passed
- No src/tests file invented outside plan `files_modified` (csproj reference documented above as plan-contemplated)
