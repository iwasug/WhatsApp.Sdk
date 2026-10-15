---
phase: 07-multi-account-orchestration
plan: 01
subsystem: multi-account
tags: [multi-account, session-manager, accounts, tracer]
requires:
  - phase: 01-foundation-store-seam
    provides: StoreRegistry, StoreSession, atomic lifecycle discipline
  - phase: 04-signal-e2ee-messaging
    provides: WaConnection, Messenger
  - phase: 05-ban-safety
    provides: ISafetyGate, SafetyOptions
provides:
  - IWhatsAppSessionManager
  - SessionManager
  - AccountRuntime
  - IAccountRuntimeFactory / DefaultAccountRuntimeFactory
  - AccountStatus
  - SdkUnknownAccountException
  - OutboundEnvelope
affects: [07-02, 07-03, 07-04]

actuals:
  tokens: 38576
  tasks: 3
  commits: 1
  plan_head_before: 2c9656c28cccdebf72568433aa7e73ec2a25ae09
  plan_head_after: a7012a1a3f007a132d162ab97e6cf16f90dce70c

tech-stack:
  added:
    - Microsoft.Extensions.DependencyInjection.Abstractions 10.0.12
    - Microsoft.Extensions.Hosting.Abstractions 10.0.12
    - Microsoft.Extensions.Options.ConfigurationExtensions 10.0.12
  patterns:
    - atomic ConcurrentDictionary registration with TryAdd (loser disposes)
    - snapshot-then-evict-then-dispose removal
    - store-first construction in DefaultAccountRuntimeFactory

key-files:
  created:
    - src/Iwasug.WhatsApp.Sdk/Accounts/IWhatsAppSessionManager.cs
    - src/Iwasug.WhatsApp.Sdk/Accounts/SessionManager.cs
    - src/Iwasug.WhatsApp.Sdk/Accounts/AccountRuntime.cs
    - src/Iwasug.WhatsApp.Sdk/Accounts/IAccountRuntimeFactory.cs
    - src/Iwasug.WhatsApp.Sdk/Accounts/DefaultAccountRuntimeFactory.cs
    - src/Iwasug.WhatsApp.Sdk/Accounts/AccountStatus.cs
    - src/Iwasug.WhatsApp.Sdk/Accounts/OutboundEnvelope.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/SessionManager/SessionManagerTests.cs
  modified:
    - Directory.Packages.props
    - src/Iwasug.WhatsApp.Sdk/Iwasug.WhatsApp.Sdk.csproj
    - src/Iwasug.WhatsApp.Sdk/Errors/SdkException.cs
    - src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt

key-decisions:
  - "Composition types remain internal; only IWhatsAppSessionManager, AccountStatus, SafetyCircuitState, and SdkUnknownAccountException are public."
  - "RestartAccountAsync is implemented as remove-then-re-add against a fresh runtime."
  - "DefaultAccountRuntimeFactory accepts optional host registry/logger factory/TimeProvider and falls back to in-memory StoreRegistry."

patterns-established:
  - "Runtime registration mirrors StoreRegistry WR-01 snapshot discipline."
  - "Store session is resolved before WaConnection and Messenger construction."

requirements-completed: [MUL-01, MUL-02]

duration: N/A
completed: 2026-10-15
status: complete
---

# Phase 7 Plan 01: Atomic Account Registry Summary

**Atomic multi-account SessionManager with per-account runtime composition, restart, and snapshot disposal**

## Performance

- **Tasks:** 3
- **Files modified:** 12
- **Tests:** 5 added (SessionManagerTests), all passing

## Accomplishments

- Added IWhatsAppSessionManager and SessionManager over ConcurrentDictionary<AccountRuntime> with duplicate/unknown handling.
- AccountRuntime owns store session, connection, messenger, safety gate, outbound channel, cancellation.
- DefaultAccountRuntimeFactory composes per-account graphs store-first with fail-clean disposal.
- Restart rebuilds one account without touching siblings.

## Task Commits

1. **Tasks 1-3: multi-account session manager tracer** - `a7012a1` (feat)

**Plan metadata:** pending final commit (docs)

## Files Created/Modified

- `src/Iwasug.WhatsApp.Sdk/Accounts/*` - account lifecycle and runtime composition.
- `src/Iwasug.WhatsApp.Sdk/Errors/SdkException.cs` - added `SdkUnknownAccountException`.
- `Directory.Packages.props`, `src/.../Iwasug.WhatsApp.Sdk.csproj` - three 10.0.12 ME abstractions.

## Deviations from Plan

None - plan executed as written (composition types internalized to satisfy analyzer; no functional change).

## Issues Encountered
- Public API analyzer required unshipped declarations; resolved by auto-populating Unshipped list.
- Full test suite retains 4 pre-existing PairingLifecycleTests environmental timeouts (baseline).

## Known Stubs
None.

## Next Phase Readiness

- Ready for 07-02 isolation suite and 07-03 hosting surface.

---
*Phase: 07-multi-account-orchestration*
*Completed: 2026-10-15*