---
phase: 02-wire-protocol-connection-resilience
plan: 02-06
subsystem: wire-protocol
tags: [protobuf, noise-handshake, golden-bytes, baileys-mit]

# Dependency graph
requires:
  - phase: 02-wire-protocol-connection-resilience
    provides: [minimal hand-written Noise handshake protobuf schema]
provides:
  - Server-compatible HandshakeMessage outer field numbers 2/3/4
  - Five golden-byte and parser facts pinning handshake envelope tags
  - Baileys WAProto provenance for the corrected outer wire tags
affects: [phase-3-pairing, noise-handshake, wire-protocol]

# Actuals (#2632)
actuals:
  tokens: 1465
  tasks: 2
  commits: 2

# Tech tracking
tech-stack:
  added: []
  patterns: [protobuf-envelope-wire-tag-golden-tests, in-file-mit-provenance]

key-files:
  created:
    - tests/Iwasug.WhatsApp.Sdk.Tests/Noise/HandshakeEnvelopeTests.cs
  modified:
    - src/Iwasug.WhatsApp.Sdk/Proto/handshake.proto

key-decisions:
  - "Use outer protobuf fields 2, 3, and 4 because the Baileys WAProto encoding emits tags 18, 26, and 34 for clientHello, serverHello, and clientFinish."
  - "Keep inner handshake message fields unchanged and treat payloads as synthetic opaque bytes; pairing semantics remain in Phase 3."

patterns-established:
  - "Pin protocol envelope tags with fixed synthetic bytes and parser-shape assertions so future schema renumbering fails in tests."
  - "Record clean-room wire-schema provenance in the hand-written .proto header without copying upstream generated output."

requirements-completed: [CONN-04]

coverage:
  - id: D1
    description: "HandshakeMessage uses server-compatible outer field numbers 2/3/4, producing wire tags 0x12/0x1A/0x22."
    requirement: CONN-04
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Noise/HandshakeEnvelopeTests.cs#HandshakeEnvelopeTests"
        status: pass
    human_judgment: false
  - id: D2
    description: "A server-shaped field-three envelope parses into ServerHello with its synthetic ephemeral payload while the other envelope members remain absent."
    requirement: CONN-04
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Noise/HandshakeEnvelopeTests.cs#ServerHello_field_3_golden_bytes_parse_to_server_shape"
        status: pass
      - kind: other
        ref: "dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror --nologo"
        status: pass
    human_judgment: false

# Metrics
commits: 2
plan_head_before: ff951fb0b87cff83b87aa94564f6c5feb6fd54b5
plan_head_after: 88525af76a7912e94ad1a0c4980cb8b5f25a5718
duration: 8min
completed: 2026-10-09
status: complete
---

# Phase 02 Plan 06 Summary

**Corrected the Noise handshake protobuf envelope to server-compatible fields 2/3/4 and added five synthetic golden-wire facts that pin tags 0x12, 0x1A, and 0x22.**

## Performance

- **Duration:** 8 min
- **Started:** 2026-10-09T06:15:00Z
- **Completed:** 2026-10-09T06:23:00Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments

- Renumbered `HandshakeMessage.client_hello`, `server_hello`, and `client_finish` to protobuf fields 2, 3, and 4, matching the Baileys WAProto wire tags 18, 26, and 34.
- Updated the schema provenance header to cite the MIT-licensed Baileys source and explicitly record that no whatsmeow bytes were involved.
- Added five xunit facts covering all three outer tags, field-three server-shaped parsing, and client-hello round-trip isolation using fixed synthetic bytes only.
- Rebuilt with warnings as errors and ran the complete solution suite: 293 passed, 0 failed, 0 skipped.

## Task Commits

Each task was committed atomically:

1. **Task 1: Renumber HandshakeMessage outer fields to 2/3/4 with provenance citation** - `94023f8` (fix)
2. **Task 2: End-to-end envelope golden bytes — one envelope per direction** - `88525af` (test)

**Plan metadata:** this SUMMARY is committed separately by the orchestrator's final metadata commit.

## Files Created/Modified

- `src/Iwasug.WhatsApp.Sdk/Proto/handshake.proto` - Corrected outer field assignments and clean-room wire provenance.
- `tests/Iwasug.WhatsApp.Sdk.Tests/Noise/HandshakeEnvelopeTests.cs` - Five golden-byte and parser-shape facts for the handshake envelope.

## Decisions Made

- Followed the server-compatible Baileys WAProto outer field numbering while leaving the inner payload schema unchanged.
- Used fixed synthetic ephemeral and payload bytes, ensuring tests prove wire shape without handling real key material or logging secrets.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None. The isolated worktree began from an older phase baseline without the requested gap-closure plan, so it was fast-forwarded to the current `master` baseline before implementation. No other worktree was modified.

## Known Stubs

None found in the created or modified files.

## Threat Flags

None. The plan corrected an existing handshake schema and added tests; it introduced no new trust boundary, endpoint, authentication path, file-access pattern, or schema surface beyond the planned envelope change.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

The handshake envelope now places client, server, and finish messages at the expected outer wire tags. Phase 3 can consume the corrected generated `HandshakeMessage` parser for pairing payload semantics. The full solution remains green at 293 tests.

## Self-Check: PASSED

- `src/Iwasug.WhatsApp.Sdk/Proto/handshake.proto` exists.
- `tests/Iwasug.WhatsApp.Sdk.Tests/Noise/HandshakeEnvelopeTests.cs` exists.
- Commits `94023f8` and `88525af` are ancestors of `HEAD`.
- Plan ledger measures 2 commits from `ff951fb0b87cff83b87aa94564f6c5feb6fd54b5` through `88525af76a7912e94ad1a0c4980cb8b5f25a5718`.
- Warnings-as-errors build passed with 0 warnings and 0 errors.
- Full solution test suite passed with 293 succeeded, 0 failed, and 0 skipped.
- Final worktree is clean before the metadata commit.

---
*Phase: 02-wire-protocol-connection-resilience*
*Completed: 2026-10-09*
