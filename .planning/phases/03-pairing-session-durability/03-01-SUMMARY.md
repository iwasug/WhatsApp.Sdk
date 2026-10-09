---
phase: 03-pairing-session-durability
plan: 03-01
subsystem: pairing
tags: [noise-xx, client-payload, protobuf, resume, credential-store, binary-node, contract-kit]

# Dependency graph
requires:
  - phase: 02-wire-protocol-connection-resilience
    provides: [Noise XX handshake + INoiseHandshakePayloads seam, FrameReader pump, QueryRouter skeleton, five store seams, loopback harness]
provides:
  - Sixth store seam (ICredentialStore/CredentialRecord) wired through registry with contract kit
  - ClientPayload registration/login factory + handshake payload provider (message-C seam)
  - BinaryNode pump with tag dispatch + id-attribute correlation (probe-to-Open retired)
  - Success-gated Open + golden payload fixtures + NOTICE provenance rows
affects: [03-02 (failure taxonomy builds on the failure path), 03-03 (pairing coordinator plugs into StanzaReceived), 03-04 (pair-success writes the row the gate reads), 03-06 (kill/restart proof)]

# Actuals (#2632) — pairs with the plan's `estimate` to calibrate future estimates.
# Same estimateTokens scale (chars/4 over the realized diff), never a harness token count.
actuals:
  tokens: 49876
  tasks: 3
  commits: 3

# Tech tracking
tech-stack:
  added: []
  patterns: [credential-row-gated open, node-tag pump dispatch, fixture-pinned proto schemas]

key-files:
  created: [src/Iwasug.WhatsApp.Sdk/Stores/ICredentialStore.cs, src/Iwasug.WhatsApp.Sdk/Domain/CredentialRecord.cs, src/Iwasug.WhatsApp.Sdk/Pairing/ClientPayloadFactory.cs, src/Iwasug.WhatsApp.Sdk/Pairing/HandshakePayloadProvider.cs, src/Iwasug.WhatsApp.Sdk/Proto/client_payload.proto, src/Iwasug.WhatsApp.Sdk/Proto/companion_reg.proto, tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ResumeTests.cs]
  modified: [src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs, src/Iwasug.WhatsApp.Sdk/Connection/QueryRouter.cs, src/Iwasug.WhatsApp.Sdk/Noise/NoiseHandshake.cs, src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs]

key-decisions:
  - "Repo-local v1 protobuf field numbers with honest provenance: Baileys ships compiled WAProto, so live numbers are not observable read-only; roles are Baileys, numbers are ours, fixtures pin them, human proof confirms them (A2)"
  - "WaConnection discovers credentials through the payloads object (internal ICredentialHandshakePayloads), not a new ctor parameter — zero public-surface change to the connection"
  - "Existing loopback tests migrate to seeded credential providers + peer-sent <success> instead of weakening the Open gate for payload-less doubles"
  - "StanzaReceived is an internal WaConnection event (not host-visible) as the 03-03 coordinator plug-in point"

patterns-established:
  - "Open-gate pattern: success stanza + independent credential-row read (D-06), failure never conflated with missing (D-19)"
  - "Node-shape test peers: SendPeerSuccessAsync per file, seeded HandshakePayloadProvider per file (existing duplication convention)"
  - "Fixture-pinned schemas: committed protoc hex + provenance block per payload direction"

requirements-completed: [CONN-03]

# Coverage metadata (#1602) — one entry per shipped deliverable. Drives DETERMINISTIC UAT routing in verify-work.
coverage:
  - id: D1
    description: "Seeded-row resume opens exactly once after <success> with stable handshake static key and login payload in message C"
    requirement: "CONN-03"
    verification:
      - kind: integration
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ResumeTests.cs#Resume_seeded_credentials_login_opens_after_success + Resume_second_connect_sends_login_payload_again"
        status: pass
    human_judgment: false
  - id: D2
    description: "BinaryNode pump (tag dispatch, id correlation) with probe path retired across all loopback suites"
    requirement: "CONN-03"
    verification:
      - kind: integration
        ref: "dotnet test --filter FullyQualifiedName~Connection (76 tests)"
        status: pass
    human_judgment: false
  - id: D3
    description: "Sixth seam host-pluggable: registry resolution, reference impl, nine-fact contract kit on two bindings"
    requirement: "CONN-03"
    verification:
      - kind: integration
        ref: "dotnet test --filter FullyQualifiedName~CredentialStore (18 tests)"
        status: pass
    human_judgment: false
  - id: D4
    description: "Registration and login payload bytes pinned as golden fixtures with structural isolation asserts"
    requirement: "CONN-03"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/ClientPayloadTests.cs (5 tests)"
        status: pass
    human_judgment: false
  - id: D5
    description: "Live-server parity of the repo-local v1 field numbers (registration accepted, login resumes a real account)"
    requirement: "CONN-03"
    verification: []
    human_judgment: true
    rationale: "No live WhatsApp account exists in CI (03-RESEARCH A1/A2); byte parity with the real server needs the end-of-phase human QR/restart proof"

# Metrics
duration: 55min
completed: 2026-10-09
status: complete
---

# Phase 03 Plan 01: Resume Spine Summary

**Credential-row resume end-to-end: persisted Noise static key, login ClientPayload in message C, Open only after server `<success>` — with the BinaryNode pump pull-forward, the sixth store seam, and byte-pinned payload fixtures**

## Performance

- **Duration:** 55min
- **Started:** 2026-10-09T19:28:31+07:00
- **Completed:** 2026-10-09T20:23:27+07:00
- **Tasks:** 3
- **Files modified:** 31

## Accomplishments

- Tracer proof green: seeded credential row → stable handshake static (server-side asserted) → login payload in message C → `<success>` → exactly one `Open`, never before; second connect re-selects login (CONN-03 mechanism half)
- Pump migrated as one unit: `FrameReader → Decrypt → BinaryNodeCodec.Decode` with tag dispatch (`success`/`failure`/`iq`/`notification`/`stream:error`), id-attribute correlation in `QueryRouter`, probe path deleted
- Sixth seam behaves like the other five: typed `CredentialRecord`, registry resolution with in-memory fallback, `StoreSession.Credentials`, nine-fact contract kit green on InMemory + Scratch (18 tests), compliance floor at six
- Both payload directions byte-pinned as committed fixtures with provenance blocks, structural decode + cross-isolation asserts
- NOTICE.md carries four new provenance rows (whatsmeow `handshake.go` behavioral read; Baileys `validate-connection.ts`, `generics.ts`, `Defaults/index.ts` @ 0af2386)

## Task Commits

Each task was committed atomically:

1. **Task 1: End-to-end resume spine** - `681aaf7` (feat)
2. **Task 2: Sixth seam fully wired** - `d6d0a07` (feat)
3. **Task 3: Payload direction pins** - `6ba811c` (feat)

**Plan metadata:** ledger base `c2ec87d`, head after `6ba811c` (measured `rev-list --count` = 3 task commits)

## Files Created/Modified

- `src/Iwasug.WhatsApp.Sdk/Stores/ICredentialStore.cs` - Sixth seam: interface + async factory, single-row D-02/D-06/D-19 contract
- `src/Iwasug.WhatsApp.Sdk/Domain/CredentialRecord.cs` - Typed row: platform, 4 key/secret buffers, paired identity, registration id
- `src/Iwasug.WhatsApp.Sdk/Proto/client_payload.proto` - Hand-written ClientPayload envelope (registration + login contents)
- `src/Iwasug.WhatsApp.Sdk/Proto/companion_reg.proto` - Hand-written DeviceProps descriptor
- `src/Iwasug.WhatsApp.Sdk/Pairing/ClientPayloadFactory.cs` - Registration/Login serializers over the generated types
- `src/Iwasug.WhatsApp.Sdk/Pairing/HandshakePayloadProvider.cs` - Production `INoiseHandshakePayloads` + internal credential seam + registration defaults
- `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs` - Probe deleted; credential load before handshake; open-gate wait; node pump; internal `StanzaReceived`
- `src/Iwasug.WhatsApp.Sdk/Connection/QueryRouter.cs` - Skeleton envelope replaced by `iq`-node + `id`-attribute correlation
- `src/Iwasug.WhatsApp.Sdk/Noise/NoiseHandshake.cs` - Persisted-static overload + responder-side initiator-static observability
- `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs`, `StoreRegistryOptions.cs` - `CredentialFactory`, `credential` resolution, `StoreSession.Credentials`
- `src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs` - Credential row/store with boundary cloning
- `src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/CredentialStoreContractTests.cs` - Nine-fact kit contract
- `src/Iwasug.WhatsApp.Sdk.Testing/FaultInjection/FaultingStoreFactory.cs` - Credential decorator seam
- `tests/.../Connection/ResumeTests.cs` - Tracer proof (3 tests)
- `tests/.../Connection/LoopbackRoundTripTests.cs`, `ConnectionStateTests.cs`, `LoopbackTests.cs`, `QueryRouterTests.cs` - Migrated to node correlation + success-gated open
- `tests/.../Pairing/ClientPayloadTests.cs`, `TestData/client-payload-*.json` - Golden fixtures (5 tests)
- `tests/.../StoreContract/ScratchStoreFactory.cs`, `*CredentialStoreContractTests.cs` - Second-implementation binding
- `NOTICE.md`, both `PublicAPI.Unshipped.txt` files, both `.csproj` files (proto/content items)

## Decisions Made

- **Repo-local v1 field numbers, honestly documented.** Baileys ships compiled WAProto (no `.proto` sources), so live wire numbers are not observable read-only. Field *roles* are Baileys (`validate-connection.ts` read this task); field *numbers* are declaration-order v1 assignments stated in the `.proto` headers and fixture provenance. The fixtures make any drift fail CI; live parity is D5 human proof (A2). Fabricating Baileys provenance for invented numbers would have been worse.
- **Credential discovery through the payloads object.** `WaConnection` pattern-matches its existing `INoiseHandshakePayloads` for the internal `ICredentialHandshakePayloads` (load snapshot → static key; independent re-read for the gate). No public ctor change, no second composition root, bare doubles keep working (they just never open without a row — the prohibition holds uniformly).
- **Strict gate, migrated tests.** The gate withholds `Open` for payload-less doubles rather than special-casing them; all `ConnectAsync`-driving loopback tests now seed rows and send `<success>`. Echo-loop counts dropped by one where the probe used to consume a query.
- **`GetClientHello` stays an empty opaque blob.** The handshake authenticates it without parsing; registration versioning (what hello will eventually carry) lands with later pairing work. Valid per the seam contract, documented in code.
- **Failure mapping keeps numerics observable.** `<failure reason>` returns the raw code into the existing policy table; unknown codes (incl. 405) ride the bounded-retry arm until plan 03-02 adds `ClientOutdated`.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] NoiseHandshake persisted-static overload + responder observability**
- **Found during:** Task 1 (resume spine — "handshake uses the persisted Noise static key" is unimplementable otherwise)
- **Issue:** `NoiseHandshake` mints a fresh static per instance with no injection seam, and the responder discards message-C contents, so neither stable identity nor server-side assertions were possible
- **Fix:** Internal `(payloads, staticPrivateKey)` ctor (imports + zeroizes); responder stashes the decrypted initiator static behind an internal getter (zeroized on dispose, covered by `IsKeyMaterialZeroized`)
- **Files modified:** `src/Iwasug.WhatsApp.Sdk/Noise/NoiseHandshake.cs`
- **Verification:** `ResumeTests` asserts the server-observed static equals the seeded key; full suite green
- **Committed in:** `681aaf7` (part of task commit)

**2. [Rule 3 - Blocking] InMemory credential row/store landed in Task 1, not Task 2**
- **Found during:** Task 1 (the tracer proof needs a runnable store; Task 2 owns only registry/kit)
- **Issue:** No `ICredentialStore` implementation existed for `ResumeTests` or the migrated loopback suites
- **Fix:** `CredentialRow` + `InMemoryCredentialStore` (boundary cloning for key buffers) + `CredentialCapability`, mirroring `SafetyStateRow`; Task 2 kept registry/contract-kit/binding scope and only touched the file's count docs
- **Files modified:** `src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs`
- **Verification:** `ResumeTests` 3/3; `CredentialStore` 18/18 across both bindings
- **Committed in:** `681aaf7` (part of task commit)

**3. [Rule 1 - Bug] Migrated ConnectionStateTests + LoopbackTests (not in plan files list)**
- **Found during:** Task 1 (pump migration breaks every `ConnectAsync`-driving suite, not just the listed ones)
- **Issue:** Both files drive open-via-probe with payload-less doubles; after the migration `ConnectAsync` would hang to timeout (20+ tests)
- **Fix:** Same mechanical migration as the listed files: seeded `HandshakePayloadProvider`, peer-sent `<success>`, echo counts adjusted, `StaticPayloads` removed where fully replaced
- **Files modified:** `tests/.../Connection/ConnectionStateTests.cs`, `tests/.../Connection/LoopbackTests.cs`
- **Verification:** `Connection` filter 76/76; full suite 329/329
- **Committed in:** `681aaf7` (part of task commit)

**4. [Rule 2 - Missing critical] Compliance floor raised five → six**
- **Found during:** Task 2 (the backstop's own comment instructs raising the floor when a sixth contract class is added)
- **Issue:** `ComplianceTestBase` floor assert + comment still said five; a silently-undiscovered sixth class would pass vacuously
- **Fix:** Floor `BeGreaterThanOrEqualTo(6)` + comment lists credential
- **Files modified:** `src/Iwasug.WhatsApp.Sdk.Testing/ComplianceTestBase.cs`
- **Verification:** Full suite green incl. `Every_contract_class_has_a_concrete_subclass`
- **Committed in:** `d6d0a07` (part of task commit)

**5. [Rule 3 - Blocking] Testing-kit PublicAPI entries (plan listed only the SDK file)**
- **Found during:** Task 2 (RS0016 fires on the Testing assembly too)
- **Issue:** `CredentialStoreContractTests`, `FaultingCredentialStore`, and the widened `FaultingStoreFactory` ctor have no declared entries
- **Fix:** 17 harvested entries added; ctor line replaced with the six-parameter shape
- **Files modified:** `src/Iwasug.WhatsApp.Sdk.Testing/PublicAPI.Unshipped.txt`
- **Verification:** `dotnet build -warnaserror` 0 warnings, 0 errors
- **Committed in:** `d6d0a07` (part of task commit)

---

**Total deviations:** 5 auto-fixed (3 blocking, 1 bug, 1 missing-critical)
**Impact on plan:** All five are necessary consequences of the tracer structure (stable-identity wiring, runnable seam, suite-wide pump migration, backstop contract). No scope creep — no new packages, no public-surface additions beyond the planned seam types.

## Issues Encountered

- **ResumeTests peer tail hung on infinite delay (caught pre-commit):** the first version held the peer with `Task.Delay(Timeout.Infinite)` and `await peerTask` only returned when the test CTS fired — test 1 passed in ~20s by accident (CTS expiry mid-await), test 2 failed with `OperationCanceledException` on its second connect (cancelled token). Fixed by holding the peer on `ReceiveAsync` until client-dispose EOF instead; both tests now pass in ~2s. Lesson recorded: never gate a peer task's lifetime on the test timeout.
- **Wrong-edit rollback (caught immediately):** one `WaConnection` edit accidentally dropped the message-A send line; re-added in the next edit and verified by build + handshake tests.
- **Dropped comment line (caught immediately):** an `InMemoryStoreFactory` doc edit removed a continuation line; restored and verified by read-back.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Ready: 03-02 (failure taxonomy extends `MapFailureCode`'s numeric path with `ClientOutdated = 405`), 03-03 (coordinator plugs into internal `StanzaReceived`; registration inputs replace `RegistrationDefaults`), 03-04 (pair-success writes the row the Open gate re-reads — same gate, no architectural change), 03-06 (kill/restart + drop survival complete the CONN-03 proof; mechanism half proven here)
- Watch item: D5 live-number parity (D5) — if the human proof rejects a payload, numbers change in exactly two places (`.proto` + fixtures regenerate together)
- No blockers; working tree clean; full suite 329/329 green with `dotnet build -warnaserror` at 0 warnings

## Self-Check: PASSED

- All 10 key files verified present on disk; all 3 task commits (`681aaf7`, `d6d0a07`, `6ba811c`) verified in `git log --all`
- No stub patterns (`TODO|FIXME|placeholder|coming soon|not available`) in any created/modified source file
- No private key material in logs/exceptions/events (provider/factory never log; messages name rules only; tests compare public hex)
- Comment-filtered `wa-probe` count in `WaConnection.cs` = 0; `waits for real node traffic` absent from `QueryRouter.cs`

---
*Phase: 03-pairing-session-durability*
*Completed: 2026-10-09*
