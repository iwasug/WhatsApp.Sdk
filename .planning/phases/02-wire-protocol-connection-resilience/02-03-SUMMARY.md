---
phase: 02-wire-protocol-connection-resilience
plan: 02-03
subsystem: connection
tags: [noise-xx, aesgcm, cacophony-vectors, nsec, protobuf, zeroization, rehandshake]
requires:
  - phase: 02-02-loopback-tracer-slice
    provides: [NoiseHandshake/NoiseTransport/QueryRouter/WaConnection skeleton surface, loopback peer harness, NSec call shapes]
provides:
  - Spec-faithful Noise_XX_25519_AESGCM_SHA256 state machine proven against committed cacophony vectors
  - Two-party handshake proof over duplex pipe with re-handshake freshness + test-enforced zeroization
  - Provenance-clean handshake.proto with internal-only codegen (no PublicAPI change)
affects: [02-05-reconnect-transport, pairing-phase-3, 02-04-codec-router]

actuals:
  tokens: 26700
  tasks: 2
  commits: 2

tech-stack:
  added: []
  patterns: [spec-core-with-frozen-surface, preview-split-for-early-mint, fingerprint-not-keys-in-asserts, single-impl-both-roles]

key-files:
  created:
    - src/Iwasug.WhatsApp.Sdk/Proto/handshake.proto
    - tests/Iwasug.WhatsApp.Sdk.Tests/TestData/noise-xx-aesgcm-vectors.json
    - tests/Iwasug.WhatsApp.Sdk.Tests/Noise/NoiseVectorTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Noise/NoiseHandshakeTests.cs
  modified:
    - src/Iwasug.WhatsApp.Sdk/Noise/NoiseHandshake.cs
    - src/Iwasug.WhatsApp.Sdk/Noise/NoiseTransport.cs
    - src/Iwasug.WhatsApp.Sdk/Iwasug.WhatsApp.Sdk.csproj
    - tests/Iwasug.WhatsApp.Sdk.Tests/Connection/LoopbackRoundTripTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Iwasug.WhatsApp.Sdk.Tests.csproj

key-decisions:
  - "Initiator transport minted at message B via a pure post-se PreviewSplit (frozen surface forces early mint); convergence with the token-ordered split asserted by test"
  - "Loopback peer drives the internal responder role — raw-NSec peer scripting deleted, one implementation speaks both sides"
  - "Generated proto types stay internal via protoc internal_access (OutputOptions): zero PublicAPI entries, 02-04 owns Unshipped"
  - "Vector/test asserts compare hex strings and SHA-256 fingerprints — raw key bytes can never enter assertion output"
  - "Initiator static is fresh per handshake (maximal forward secrecy now); persistent identity binding is Phase 3 scope"

patterns-established:
  - "Spec-core-with-frozen-surface: raw token byte-strings flow through the crypto core (vector-proven); protobuf framing is a thin carrier layer on top — vectors and envelopes never mix"
  - "Preview-split for early mint: when a frozen API must mint keys before the token order allows, derive them purely from already-known inputs and assert convergence with the real split"
  - "Fingerprint-not-keys in asserts: equality of secrets is proven via SHA-256 fingerprints so failures cannot print key material (V6)"

requirements-completed: [CONN-04]

coverage:
  - id: D1
    description: "Cacophony XX_25519_AESGCM_SHA256 vectors pass from the committed golden fixture: 3 handshake ciphertexts, 3 payload recoveries, both transcript hashes, split-key mirroring, 3 transport samples"
    requirement: "CONN-04"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Noise/NoiseVectorTests.cs (5 tests)"
        status: pass
    human_judgment: false
  - id: D2
    description: "Two-party handshake over duplex pipe agrees on keys with AEAD both ways; 515-style re-handshake mints a fresh ephemeral while old handshakes and transports are provably zeroized; cancelled handshakes wipe in-flight keys"
    requirement: "CONN-04"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Noise/NoiseHandshakeTests.cs (13 tests)"
        status: pass
    human_judgment: false
  - id: D3
    description: "handshake.proto codegens clean under warnings-as-errors with a Baileys-MIT provenance header and internal-only types"
    requirement: "CONN-04"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Noise/NoiseHandshakeTests.cs#Proto_envelope_carries_baileys_faithful_shapes"
        status: pass
    human_judgment: true
    rationale: "Provenance-cleanliness (no copyleft bytes) is an authorship claim — a reviewer confirms the header and derivation before ship, and NOTICE.md still needs its log row (sibling-owned file, noted for orchestrator merge)."
  - id: D4
    description: "02-02 loopback tracer still green on the new wire (11 facts, no test-logic changes — peer speaks the responder role)"
    requirement: "CONN-04"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Connection/LoopbackRoundTripTests.cs (11 tests)"
        status: pass
    human_judgment: false

duration: 40min
completed: 2026-10-08
status: complete
---

# Phase 02 Plan 03: Noise Core Summary

**Spec-faithful Noise XX over NSec+BCL proven byte-for-byte against committed cacophony vectors, with two-party re-handshake hygiene and a provenance-clean handshake.proto — 18 new crypto facts, 217/217 green**

## Performance

- **Duration:** 40 min
- **Started:** 2026-10-08T13:26:27Z
- **Completed:** 2026-10-08T14:06:33Z
- **Tasks:** 2 (1 crypto core + vectors, 1 two-party proof + proto)
- **Files modified:** 9 (4 created, 5 modified)

## Accomplishments

- `NoiseSymmetricState` implements the Noise rev 33/34 symmetric core (MixHash/MixKey/EncryptAndHash/Split, `0x00*4 || BE64(n)` nonces, BCL-HKDF-as-Noise-HKDF) with initiator + internal responder roles running the exact `arrow-e / arrow-e-ee-s-es / arrow-s-se` token flow
- `NoiseVectorTests` proves the core against the committed `noise-xx-aesgcm-vectors.json` (verbatim `rweather/noise-c` MIT entry): all 3 handshake ciphertexts, all payload recoveries, both transcript hashes, key mirroring, all 3 transport samples, plus a tamper-rejection check
- `NoiseHandshakeTests` proves the live contract over a duplex pipe: matching transcript hashes, AEAD both ways, nonces sequential from 0 per fresh transport, 515-style re-handshake with fresh ephemeral + provably zeroized predecessors, mid-handshake abort zeroization, fail-closed malformed/out-of-order handling
- `Proto/handshake.proto` (hand-written, Baileys-MIT provenance header) codegens internal-only via `internal_access` — the first `.proto` in the repo adds zero public symbols; messages A/B/C carry `header || HandshakeMessage` / bare `HandshakeMessage` with client_hello/server_hello/client_finish envelopes

## Task Commits

Each task was committed atomically:

1. **Task 1: Spec-faithful XX state machine with cacophony vectors and AEAD nonce discipline** - `33a3167` (feat)
2. **Task 2: Two-party handshake proof plus minimal handshake.proto with provenance** - `b85690c` (feat)

**Plan metadata:** summary commit (see below)

## Files Created/Modified

- `src/Iwasug.WhatsApp.Sdk/Noise/NoiseHandshake.cs` - Rewritten: `NoiseSymmetricState` spec core + initiator (unchanged public surface) + internal responder + raw vector seams + preview-split + zeroization proofs
- `src/Iwasug.WhatsApp.Sdk/Noise/NoiseTransport.cs` - Remarks corrected to the spec core (behavior identical) + `IsKeysZeroized` T-2-03 flag
- `src/Iwasug.WhatsApp.Sdk/Proto/handshake.proto` - Minimal hand-written carrier schema with provenance header (Baileys MIT shapes, no whatsmeow bytes)
- `src/Iwasug.WhatsApp.Sdk/Iwasug.WhatsApp.Sdk.csproj` - Protobuf ItemGroup (`GrpcServices=None`, `internal_access`); no versions inline (repo law)
- `tests/Iwasug.WhatsApp.Sdk.Tests/TestData/noise-xx-aesgcm-vectors.json` - Verbatim cacophony XX entry (byte-identical to upstream)
- `tests/Iwasug.WhatsApp.Sdk.Tests/Noise/NoiseVectorTests.cs` - 5 vector facts (fixture pin + handshake + 3 transport theories)
- `tests/Iwasug.WhatsApp.Sdk.Tests/Noise/NoiseHandshakeTests.cs` - 13 two-party facts (completion, re-handshake, cancel, malformed theories, ordering, envelope shapes)
- `tests/Iwasug.WhatsApp.Sdk.Tests/Connection/LoopbackRoundTripTests.cs` - Peer rewritten onto the internal responder (raw-NSec scripting + skeleton KDF deleted)
- `tests/Iwasug.WhatsApp.Sdk.Tests/Iwasug.WhatsApp.Sdk.Tests.csproj` - Fixture `Content` copy to test output

## Decisions Made

- Preview-split for the frozen surface: `ReadMessageB` must mint the transport, but spec keys derive post-`se` (message 3). The initiator knows both `se` inputs after message 2, so it previews the post-`se` chain purely; the running state still advances through `se` at message 3 per token order. The vector test asserts preview == token-ordered split — without this, the two ends would silently derive different keys (caught by vectors, not by review).
- One implementation speaks both roles: the loopback peer dropped its raw-NSec responder script for `NoiseHandshakeResponder`, deleting the KDF duplication the 02-02 summary flagged as tech debt.
- `internal_access` codegen: generated proto types are internal, so this plan adds zero PublicAPI entries and never touches `PublicAPI.Unshipped.txt` (sibling 02-04 owns it).
- Fingerprint-not-keys: secret equality is asserted via SHA-256 fingerprints and hex strings, so even a failing run cannot print key material (V6 discipline extended to test output).
- Fresh static per handshake: Phase 3 owns persistent identity; Phase 2 mints both keys per attempt (forward secrecy maximal, identity binding deferred and documented).

## Deviations from Plan

### Plan-contemplated adjustment (not a rule fix)

**handshake.proto + csproj Protobuf ItemGroup landed in the Task 1 commit (files assigned to Task 2)**
- **Found during:** Task 1 (handshake rewrite consumes the generated envelope types)
- **Issue:** The spec rewrite frames messages as `HandshakeMessage` protobufs, so the schema and its build wiring must exist for Task 1's code to compile — no green commit is possible without them.
- **Fix:** Authored `Proto/handshake.proto` + the csproj `Protobuf` ItemGroup inside the Task 1 commit; Task 2 owns the tests that prove them. No extra files, no scope change.
- **Files modified:** `src/.../Proto/handshake.proto`, `src/.../Iwasug.WhatsApp.Sdk.csproj`
- **Verification:** `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror` → 0 Warning(s), 0 Error(s); generated types confirmed `internal` in build output
- **Committed in:** 33a3167 (Task 1 commit)

### Auto-fixed Issues

**1. [Rule 1 - Bug] Initiator split from the pre-se chaining key while the responder splits post-se**
- **Found during:** Task 1 (vector key-mirror assert failed while all ciphertexts and hashes passed)
- **Issue:** The frozen surface mints the transport at message B, but spec Split derives from the post-`se` chain (message 3). Early split = divergent transport keys between the two ends — handshake completes, every later byte fails.
- **Fix:** `PreviewSplit` — pure post-`se` derivation from already-known inputs at message 2; running state advances through `se` at message 3 per token order; `SplitTransportKeys` convergence asserted by test.
- **Files modified:** `src/.../Noise/NoiseHandshake.cs`
- **Verification:** Key-mirror + convergence + all 3 transport-sample asserts green
- **Committed in:** 33a3167 (Task 1 commit)

**2. [Rule 1 - Bug] Post-split guard rejected the legitimate message-3 operations**
- **Found during:** Task 1 (first full-exchange run)
- **Issue:** A `ThrowIfSplit` guard on every symmetric op blocked message 3's encrypt/mix after the early split. The guard modeled noise-c's call order, not ours.
- **Fix:** Split stays one-shot; the other ops remain valid until Dispose (the `_step` machine already prevents out-of-order misuse).
- **Files modified:** `src/.../Noise/NoiseHandshake.cs`
- **Verification:** Full exchange green
- **Committed in:** 33a3167 (Task 1 commit)

**3. [Rule 1 - Bug] Initiator transcript hash captured at split (mid-handshake) instead of after message 3**
- **Found during:** Task 1 (hash assert failed after ciphertexts passed)
- **Issue:** Split-time capture froze the initiator hash before message 3's transcript contributions; noise-c reads the hash after the full handshake on both sides.
- **Fix:** `CurrentHash` live read on the symmetric state; both handshake classes expose the final hash gated on exchange completion.
- **Files modified:** `src/.../Noise/NoiseHandshake.cs`
- **Verification:** Both hashes equal the vector `handshake_hash`
- **Committed in:** 33a3167 (Task 1 commit)

**4. [Rule 1 - Bug] Test compared uppercase `ToHexString` output against the lowercase fixture**
- **Found during:** Task 1 (first vector run — bytes identical, case differed)
- **Issue:** `Convert.ToHexString` returns uppercase; the fixture is lowercase.
- **Fix:** `Hex()` helper normalizes to lowercase for all wire comparisons.
- **Files modified:** `tests/.../Noise/NoiseVectorTests.cs`
- **Verification:** All 5 vector facts green
- **Committed in:** 33a3167 (Task 1 commit)

**5. [Rule 1 - Bug] xunit.v3 `Assert.Throws` requires exact exception type (tamper assert)**
- **Found during:** Task 1 (transport theories — samples already matched)
- **Issue:** BCL throws derived `AuthenticationTagMismatchException`, not base `CryptographicException`.
- **Fix:** Assert the precise BCL type (also documents runtime behavior).
- **Files modified:** `tests/.../Noise/NoiseVectorTests.cs`
- **Verification:** All 3 transport theories green
- **Committed in:** 33a3167 (Task 1 commit)

**6. [Rule 3 - Blocker] `using var _` discard declarations collided with tuple-deconstruction discards**
- **Found during:** Task 2 (first test build — CS0841 at all three deconstruction sites)
- **Issue:** A later `using var _` turns same-scope deconstruction `_` uses into variable references.
- **Fix:** Dropped the using-discard declarations; transports dispose explicitly at test ends.
- **Files modified:** `tests/.../Noise/NoiseHandshakeTests.cs`
- **Verification:** Test build 0W/0E
- **Committed in:** b85690c (Task 2 commit)

**7. [Rule 1 - Bug] Out-of-order fact called `WriteMessageA` once under `Throws`, then expected `ObjectDisposedException` as `InvalidOperationException`**
- **Found during:** Task 2 (test authoring review before first run)
- **Issue:** First `WriteMessageA` succeeds (no throw); post-Dispose calls throw `ObjectDisposedException`, not `InvalidOperationException`.
- **Fix:** Discard the successful first call; assert the precise post-dispose type.
- **Files modified:** `tests/.../Noise/NoiseHandshakeTests.cs`
- **Verification:** All 13 handshake facts green
- **Committed in:** b85690c (Task 2 commit)

---

**Total deviations:** 1 adjustment + 7 auto-fixed (6 Rule 1 correctness/test bugs, 1 Rule 3 test blocker)
**Impact on plan:** All changes stay inside the plan's file list and prohibitions (no key logging, no 401/440 retry touched, no TLS touched, no PublicAPI entries). The preview-split is a structural consequence of the frozen surface meeting the spec token order — method shapes downstream plans consume are unchanged.

## Issues Encountered

- Two foreign `var`↔explicit hunks from the parallel style wave sat in `NoiseTransport.cs`; reverted to committed state before staging so the Task 1 commit contains only plan work (orchestrator reconciles the style pass at merge).
- `NoiseProtocol.cs` shows a phantom worktree modification (status M, empty diff — mode/line-ending artifact from a parallel wave); left untouched.
- Noise spec AESGCM nonce endianness was settled empirically: `0x00*4 || BE64(n)` matches all vector ciphertexts on first full run — no Baileys-vs-spec divergence at handshake scope.
- BCL `HKDF.DeriveKey(SHA256, ikm, 64, salt: ck, info: [])` reproduces Noise HKDF exactly (verified by message-2 ciphertext match, which gates on MixKey correctness).

## Known Stubs

None — every new symbol is fully implemented. Scanned all created/modified plan files for stub patterns (`TODO`/`FIXME`/placeholder/empty literals): no matches. The per-handshake initiator static is working code with a documented Phase-3 persistence story, not a stub.

## Threat Flags

None beyond the plan's register — all five dispositions verified in-code:
- T-2-03 (nonce/key lifecycle): fresh transport per handshake from 0, separate counters, single-owner disposal, re-handshake freshness + zeroization test-enforced (`Rehandshake_after_515…`, `IsKeysZeroized`).
- T-2-05 (cipher correctness): committed vectors gate every build; wrong-cipher code cannot pass message-2.
- T-2-10 (key material in logs): every exception/log/test message names rules only; asserts use hex of public data + key fingerprints. New-log-site review: this plan adds zero log sites (handshake/router stay silent; sole log owner remains `WaConnection`).
- T-2-12 (message-A replay): fresh ephemeral per attempt asserted (`secondMessageA != firstMessageA`); re-handshake always re-runs the full XX exchange.
- T-2-SC (codegen): pinned Grpc.Tools 2.84.0 build-time-only, output build-local under `obj/`, schema hand-written with provenance header, no whatsmeow-sourced bytes.
- New surface introduced: NONE externally — all new types are internal; no endpoints, auth paths, file access (beyond the test-only fixture read), or schema changes at trust boundaries.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Ready for 02-05 (reconnect lifecycle + WS transport): consumes the frozen `WriteMessageA/ReadMessageB/WriteMessageC` surface, per-attempt fresh handshake discipline, and the `IsKeyMaterialZeroized` abort story for cancel paths. `ConnectionOptions.KeepAliveIntervalMs` remains unwired (02-05 scope, unchanged).
- Ready for Phase 3 (pairing): consumes the opaque-payload handshake API (`INoiseHandshakePayloads` supplies ClientHello/ClientFinish semantics), the `NoiseHandshakeResponder` test-harness shape (pairing flows extend the duplex pattern), the `handshake.proto` codegen pattern (`internal_access` + provenance header rule), and the named gap — static-identity key persistence (`PreviewSplit` takes injected keys already, so a persisted static slots into the internal seam without surface change).
- Ready for 02-04 (codec + router): untouched files respected — no changes to `BinaryNode*`, `TokenDictionary`, `FrameReader`, `QueryRouter`, `PublicAPI.Unshipped.txt`, or `NOTICE.md`. Note for orchestrator merge: `NOTICE.md` still wants one provenance-log row for the Baileys read (socket.ts validateConnection + noise-handler.ts @ 0af2386 → handshake envelope shapes); the full record lives in the `.proto` header.
- Watch item: `HandshakeHash` is only available after message C (initiator) / message C processing (responder) and throws `InvalidOperationException` before that — intentional, asserted nowhere outside vector/two-party tests.

---
*Phase: 02-wire-protocol-connection-resilience*
*Completed: 2026-10-08*

## Self-Check: PASSED

- All 4 created + 5 modified plan files exist on disk (verified by successful build inputs compiling)
- Commits exist: `33a3167` (Task 1) and `b85690c` (Task 2) both in `git log`
- `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror` → 0 Warning(s), 0 Error(s)
- `dotnet test Iwasug.WhatsApp.Sdk.sln` → 217/217 passed (199 pre-existing + 5 vector + 13 handshake)
- No sibling plan 02-04 files touched: `git diff HEAD~2..HEAD --name-only` shows only the 9 plan files
- No STATE.md / ROADMAP.md modifications (orchestrator-owned writes)
