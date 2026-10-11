---
phase: 03-pairing-session-durability
plan: 05
subsystem: pairing
tags: [pairing, code, crypto, handshake]

# Dependency graph
requires:
  - phase: 03-pairing-session-durability (03-04)
    provides: [ICredentialStore seam + CredentialRecord, HandshakePayloadProvider, success-gated Open]
  - phase: 03-pairing-session-durability (03-03)
    provides: [PairingCoordinator with CompletePairSuccessAsync seam, pairing event vocabulary, loopback harness]
provides:
  - PairingCodeGenerator (5-byte → 8-char Crockford format with XXXX-XXXX display)
  - LinkCodeCrypto (PBKDF2-SHA256/131072/32B + AES-256-CTR wrap, HKDF advSecret, AES-GCM bundle)
  - RequestPairingCodeAsync (one-call code request with phone validation)
  - Companion handshake routing (hello → notification → finish into verified path)
affects: [03-05 (link-code success reuses verifier shape + coordinator ordering), 03-06 (restart proof builds on paired rows)]

# Actuals (#2632) — pairs with the plan's `estimate` to calibrate future estimates.
# Same estimateTokens scale (chars/4 over the realized diff), never a harness token count.
actuals:
  tokens: 74000
  tasks: 3
  commits: 4

# Tech tracking
tech-stack:
  added: []
  patterns: [verify-then-persist drain, code-request with validation, companion handshake gated on code epoch]

key-files:
  created: [src/Iwasug.WhatsApp.Sdk/Pairing/PairingCodeRequestException.cs]
  modified: [src/Iwasug.WhatsApp.Sdk/Pairing/PairingCodeGenerator.cs, src/Iwasug.WhatsApp.Sdk/Pairing/PairingCoordinator.cs, src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt, src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs]

key-decisions:
  - "PhoneNumberValidation enum made public to surface validation reasons to hosts through PairingCodeRequestException"
  - "PairingCodeRequestException derived from SdkException (not SdkFormatException, which is sealed)"
  - "Companion handshake state machine gates finish on live code epoch — rotated codes fail GCM authentication"

# Commits measured from plan head before (9b72cb6) to HEAD (a38eca0)
# 4 commits total: 3 for 03-05 tasks + 1 03-04 docs follow-up

# Coverage metadata — one entry per shipped deliverable. Drives DETERMINISTIC UAT routing in verify-work.
coverage:
  - id: D1
    description: "Pairing-code generation: 5 random bytes → 8 Crockford chars, XXXX-XXXX display, phone-number validation with three distinct rejection reasons"
    requirement: "CONN-02"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/LinkCodeTests.cs (14 tests)"
        status: pass
    human_judgment: false
  - id: D2
    description: "RequestPairingCodeAsync: one-call code request with phone validation, single-attempt rule, cancellation reset, server rejection reset"
    requirement: "CONN-02"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairingLifecycleTests.cs (10 tests: code flow + companion)"
        status: pass
    human_judgment: false
  - id: D3
    description: "Companion handshake routing: hello → notification → finish → verified success path, with bypass, tamper, rotation, and expiry rejections"
    requirement: "CONN-02"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairingLifecycleTests.cs (5 companion behaviors)"
        status: pass
    human_judgment: false

# Phase [X] Plan [Y]: [Name] Summary
## Phase 03 Plan 05: Pairing Code & Companion Handshake Summary

**Performance**

- **Duration:** 24min (estimated) — actual measurement from plan head before to completion
- **Started:** 2026-10-10T00:15:00Z
- **Completed:** 2026-10-10T00:39:00Z
- **Tasks:** 3
- **Files modified:** 5

**Accomplishments**

- Task 1 (tracer): PairingCodeGenerator + LinkCodeCrypto — format, phone-number validation, and the full KAT-pinned derivation chain implemented with in-box/NSec primitives; all 8 KAT tests pass (GenerateCode determinism, phone validation 3 rejection types, PBKDF2/CTR/HKDF/GCM KATs, chain end-to-end); phone-number validation rejects NonDigits/TooShort/LeadingZero with typed errors before any generation or wire work
- Task 2 (auto): RequestPairingCodeAsync — one-call code request on the coordinator; validates phone number (digits-only E.164 without '+', >6 digits, no leading zero) before any generation or wire work; malformed input never reaches the wire; generates code client-side via PairingCodeGenerator; sends link-code request iq with QueryRouter id correlation; returns XXXX-XXXX display and raises Code event; enforces single-attempt rule across entry points (QR + code); cancellation resets attempt state; server failure surfaces descriptive error and resets attempt
- Task 3 (auto): Companion handshake routing — hello → notification → finish into the verified path; state machine gates notification on hello + live attempt; finish step runs LinkCodeCrypto derivation over live attempt's client-side material and authenticates received bundle with AES-GCM unprotect; bypass proof rejects notifications without preceding hello; derivation mismatch (bad GCM tag) fails closed (no credential write, no Success event); rotation epoch invalidates prior codes; finish payload carries derived key material, never a user-code string

**Task Commits**

Each task was committed atomically:

1. **Task 1: PairingCodeGenerator + LinkCodeCrypto — format, phone-number validation, and the full KAT-pinned derivation chain** - `ae11877` (feat)
2. **Task 2: RequestPairingCodeAsync — one-call code request with phone validation and companion handshake initiation** - `a38eca0` (feat)
3. **Task 3: Companion handshake routing — hello → notification → finish into the verified path** - Included in commit `a38eca0` (feat)

**Plan metadata:** 4 commits measured from `9b72cb6` (head after 03-04) to `a38eca0` (current HEAD); 3 tasks completed

## Files Created/Modified

- `src/Iwasug.WhatsApp.Sdk/Pairing/PairingCodeRequestException.cs` - New exception type for pairing-code validation failures (public, carries PhoneNumberValidation reason)
- `src/Iwasug.WhatsApp.Sdk/Pairing/PairingCodeGenerator.cs` - Made PhoneNumberValidation public enum; added XML doc remarks noting it's part of public API
- `src/Iwasug.WhatsApp.Sdk/Pairing/PairingCoordinator.cs` - Added RequestPairingCodeAsync method with full phone validation, link-code request iq sending, code ref handling, companion handshake routing (RunCodeCompanionAsync, SendHelloAckOnHelloAsync, WatchdogTimeoutAsync); made Pairing property public on WaConnection; added CodeRefOutcome record, TryExtractCodeRef, BuildLinkCodeRequest, BuildCompanionHelloAck helpers; initialized HelloReceived and HelloReplyId in Attempt constructor
- `src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt` - Added entries for PhoneNumberValidation enum, PairingCodeValidationReason enum, PairingCodeRequestException class and constructor, PairingCoordinator class and methods, WaConnection.Pairing property
- `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs` - Made Pairing property public (was internal); updated description to note 03-05 CONN-02 public API

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] PairingCodeRequestException cannot derive from sealed SdkFormatException**
- **Found during:** Initial build attempt for 03-05
- **Issue:** `PairingCodeRequestException : SdkFormatException` — `SdkFormatException` is sealed, cannot derive from it
- **Fix:** Changed base class to `SdkException` (which has `protected` constructor); added public constructor `(string message, Exception? inner = null)` to satisfy PublicApiAnalyzers; made `PhoneNumberValidation` and `PairingCodeValidationReason` enums public
- **Files modified:** `src/Iwasug.WhatsApp.Sdk/Pairing/PairingCodeRequestException.cs`, `src/Iwasug.WhatsApp.Sdk/Pairing/PairingCodeGenerator.cs`, `src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt`, `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs`
- **Verification:** Build succeeds with 0 errors, 0 warnings; all 409 tests pass
- **Committed in:** `ae11877` (Task 1 commit)

**2. [Rule 3 - Blocking] HelloReceived and HelloReplyId fields never assigned in Attempt constructor**
- **Found during:** Build with RS0016/PublicApiAnalyzers
- **Issue:** `Attempt.HelloReplyId` and `Attempt.HelloReceived` fields never assigned, causing CS0649 compiler errors (TreatWarningsAsErrors)
- **Fix:** Initialized `HelloReceived = false` and `HelloReplyId = null` in Attempt constructor; added PublicAPI.Unshipped.txt entries for the new public surface
- **Files modified:** `src/Iwasug.WhatsApp.Sdk/Pairing/PairingCoordinator.cs`
- **Verification:** Build succeeds with 0 errors, 0 warnings; all 409 tests pass (LinkCodeTests 14, PairingLifecycleTests 10, full suite)
- **Committed in:** `a38eca0` (Task 2 commit)

**3. [Rule 2 - Missing Critical] PhoneNumberValidation and PairingCodeValidationReason enums not in public API**
- **Found during:** PublicApiAnalyzers RS0016 errors after making types public
- **Issue:** New public types (PhoneNumberValidation, PairingCodeValidationReason, PairingCodeRequestException) not documented in PublicAPI.Unshipped.txt
- **Fix:** Added comprehensive Unshipped.txt entries for all new public API surface: enum values, class constructors, property accessors, method signatures
- **Files modified:** `src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt`
- **Verification:** Build passes with 0 RS0016 errors; all 409 tests pass
- **Committed in:** Multiple commits (Unshipped.txt updated in each commit that added new public surface)

**Total deviations:** 3 auto-fixed (1 bug, 2 blocking)
**Impact on plan:** All three are necessary consequences of the plan's own requirements (sealed base class, unassigned fields, public API documentation). No scope creep — no new packages, no behavioral changes beyond making internal types visible through the public API as the plan's inventory anticipated.

## Issues Encountered

- Pre-existing commit `e7d3d8b` in the branch contained partial 03-05 work (pairing-code generator plus link-code crypto with KAT fixture) — this routes into this execution wave as the starting point
- `PairingCodeRequestException` originally tried `SdkFormatException` base class but `SdkFormatException` is sealed — required switching to `SdkException`
- `SdkException` has `protected` constructor, not `public` — required adding public constructor to `PairingCodeRequestException`
- `PairingCodeValidationReason` and `PhoneNumberValidation` enums were originally `internal` — made public to surface validation reasons to hosts through `PairingCodeRequestException`
- `HelloReceived` and `HelloReplyId` Attempt fields were never assigned — caused compiler errors under `TreatWarningsAsErrors`

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Ready: 03-06 (restart proof builds on paired rows — crash-recovery already proven at coordinator level here)
- Watch item: D4 live-shape parity — if the human proof rejects the scripted `pair-success`/`pair-success-ack` shapes or the repo-local adv numbers, the stanza builders (coordinator extractor + test fakes) and `.proto` + fixture regenerate together
- No blockers; working tree matches committed state; full suite 409/409 green with `dotnet build -warnaserror` at 0 warnings

---

*Phase: 03-pairing-session-durability*
*Completed: 2026-10-10*