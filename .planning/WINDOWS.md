---
schema_version: 1
open_count: 16
waived_count: 1
fixed_count: 0
total_count: 17
last_updated: 2026-10-16T23:09:52.888Z
---

# Broken Windows Ledger

> Cross-phase defect register. With `workflow.windows_enforce` enabled, `/gsd-ship` blocks while `open_count > 0`.
> Waive with `gsd-tools windows waive <id> "<reason>"` (reason required).
> Mark fixed with `gsd-tools windows fixed <id>`.

| id | phase | kind | file | line | description | status | reason | recorded_at | resolved_at |
|----|-------|------|------|------|-------------|--------|--------|-------------|-------------|
| 1 | 01 | deviation | .planning/config.json | 15 | git.allow_default_branch_commits=true override set by executor so commits can land on this repo's only (default) branch master under branching_strategy=none — revisit if branching is adopted | open |  | 2026-10-07T22:19:44.344Z |  |
| 2 | 01 | deviation | global.json | 2 | Repo-root global.json selects test.runner=Microsoft.Testing.Platform because .NET 10 SDK rejects VSTest-mode dotnet test for xunit.v3 MTP projects (plan verify commands required it) | open |  | 2026-10-07T22:19:45.138Z |  |
| 3 | 01 | deviation | src/Iwasug.WhatsApp.Sdk.Testing/Iwasug.WhatsApp.Sdk.Testing.csproj | 28 | Kit asserts via AwesomeAssertions because xunit.v3.extensibility.core ships no Assert and a second xunit package was avoided per the extensibility.core-only rule | open |  | 2026-10-07T22:19:45.966Z |  |
| 4 | 01 | deviation | tests/Iwasug.WhatsApp.Sdk.Tests/ComplianceTests.cs | 9 | Concrete ComplianceTests subclass added (plan listed only abstract ComplianceTestBase) so the completeness [Fact] actually executes in the test assembly | open |  | 2026-10-07T22:19:46.814Z |  |
| 5 | 01 | deviation | src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs |  | 01-04 [Rule 3]: InMemoryStoreFactory gained IAsyncDisposable.DisposeAsync so the fallback factory joins the registry's D-23 disposal cascade | open |  | 2026-10-08T07:13:20.905Z |  |
| 6 | 01 | deviation | src/Iwasug.WhatsApp.Sdk/Iwasug.WhatsApp.Sdk.csproj |  | 01-04 [Rule 3]: InternalsVisibleTo for the tests assembly added in Task 2 (plan listed it under Task 3) because Task 2's StoreErrorSurface tests need internal access | open |  | 2026-10-08T07:13:21.548Z |  |
| 7 | 01 | deviation | tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreErrorSurfaceTests.cs |  | 01-04 [plan gap]: extra test file beyond the plan's <files> list — the plan mandated unit-testing LogAndWrap without naming a file for it | open |  | 2026-10-08T07:13:22.129Z |  |
| 8 | 02 | deviation | tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ConnectionStateTests.cs |  | ConnectionStateTests.cs committed with 02-07 Task 2 though not in the plan files list (Rule 2: third loopback fake needed production intro validation) | open |  | 2026-10-09T04:13:39.383Z |  |
| 9 | 02 | deviation | src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs |  | 02-07 verify runs needed fresh-hash rebuilds or -p:Deterministic=false to clear Windows SAC 0x800711C7 blocks on newly built DLLs (environment-only, no repo change) | open |  | 2026-10-09T04:13:40.232Z |  |
| 10 | 04 | stub | src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs |  | Device-identity pin of own identity DJB bytes (ponytail) - upgrade when live-peer identity verification requires exact wire form | open |  | 2026-10-11T12:17:56.048Z |  |
| 11 | 4 | deviation | src/Iwasug.WhatsApp.Sdk/Signal/DoubleRatchet.cs |  | DoubleRatchet.LoadUnlockedAsync/SaveUnlockedAsync added outside the plan files list (Rule 2: per-address session gate concurrency edge for retry resend) | open |  | 2026-10-11T16:06:32.896Z |  |
| 12 | 4 | stub | src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs |  | Device-identity child pins own identity DJB bytes (carried from 04-03 ponytail) in BuildResendNode; upgrade to marshalled Account proto at 04-05 UAT | open |  | 2026-10-11T16:06:33.556Z |  |
| 13 | 4 | stub | src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs |  | verify-response-probe | waived | Not a defect: accidental executor probe entry (verify-response-probe) created while checking CLI output shape during 04-04 execution — waiving to keep the ledger clean | 2026-10-11T16:07:25.912Z | 2026-10-11T16:08:47.331Z |
| 14 | 04 | unrun-verify | .planning/phases/04-signal-e2ee-messaging/04-VALIDATION.md |  | Plan 04-05 Task 3 verify 'dotnet test Iwasug.WhatsApp.Sdk.sln' cannot exit 0 in this environment (discovers 0 tests, exit 5); executed evidence uses dotnet exec of the compiled test assembly - documented in 04-VALIDATION run-date block | open |  | 2026-10-11T20:12:53.260Z |  |
| 15 | 04 | unrun-verify | .planning/phases/04-signal-e2ee-messaging/04-VALIDATION.md |  | SC 1-5 live-device UAT (1:1/group send, receipt round trip, quoted reply rendering, retry recovery) + assumptions A2/A4 recorded NOT VERIFIED - no live phone in CI; sample messaging mode built for the end-of-phase human gate | open |  | 2026-10-11T20:12:53.861Z |  |
| 16 | 09 | deviation | src/Iwasug.WhatsApp.Sdk/Pairing/PairingCoordinator.cs |  | 09-01 [Rule 2]: Ownership/zeroization contract documented on the randomBytes pairing seam (file not in the plan's Task 2 files list) - the invariant the repaired PairingLifecycleTests depend on | open |  | 2026-10-16T23:09:51.697Z |  |
| 17 | 09 | deviation | tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/MessagingLoopbackTests.cs | 873 | 09-01 (pre-existing, deferred per scope boundary): decrypt_failure_no_ack_no_event_and_pump_continues is flaky - NRE at line 873 when the test continuation outruns the peer-task assignment after ConnectAsync; isolated + rerun runs green; fix direction (TaskCompletionSource handoff) in phases/09-ship/deferred-items.md | open |  | 2026-10-16T23:09:52.888Z |  |

````json
[
  {
    "id": 1,
    "kind": "deviation",
    "phase": "01",
    "file": ".planning/config.json",
    "line": 15,
    "description": "git.allow_default_branch_commits=true override set by executor so commits can land on this repo's only (default) branch master under branching_strategy=none — revisit if branching is adopted",
    "status": "open",
    "reason": "",
    "recorded_at": "2026-10-07T22:19:44.344Z",
    "resolved_at": null,
    "milestone": null
  },
  {
    "id": 2,
    "kind": "deviation",
    "phase": "01",
    "file": "global.json",
    "line": 2,
    "description": "Repo-root global.json selects test.runner=Microsoft.Testing.Platform because .NET 10 SDK rejects VSTest-mode dotnet test for xunit.v3 MTP projects (plan verify commands required it)",
    "status": "open",
    "reason": "",
    "recorded_at": "2026-10-07T22:19:45.138Z",
    "resolved_at": null,
    "milestone": null
  },
  {
    "id": 3,
    "kind": "deviation",
    "phase": "01",
    "file": "src/Iwasug.WhatsApp.Sdk.Testing/Iwasug.WhatsApp.Sdk.Testing.csproj",
    "line": 28,
    "description": "Kit asserts via AwesomeAssertions because xunit.v3.extensibility.core ships no Assert and a second xunit package was avoided per the extensibility.core-only rule",
    "status": "open",
    "reason": "",
    "recorded_at": "2026-10-07T22:19:45.966Z",
    "resolved_at": null,
    "milestone": null
  },
  {
    "id": 4,
    "kind": "deviation",
    "phase": "01",
    "file": "tests/Iwasug.WhatsApp.Sdk.Tests/ComplianceTests.cs",
    "line": 9,
    "description": "Concrete ComplianceTests subclass added (plan listed only abstract ComplianceTestBase) so the completeness [Fact] actually executes in the test assembly",
    "status": "open",
    "reason": "",
    "recorded_at": "2026-10-07T22:19:46.814Z",
    "resolved_at": null,
    "milestone": null
  },
  {
    "id": 5,
    "kind": "deviation",
    "phase": "01",
    "file": "src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs",
    "line": null,
    "description": "01-04 [Rule 3]: InMemoryStoreFactory gained IAsyncDisposable.DisposeAsync so the fallback factory joins the registry's D-23 disposal cascade",
    "status": "open",
    "reason": "",
    "recorded_at": "2026-10-08T07:13:20.905Z",
    "resolved_at": null,
    "milestone": null
  },
  {
    "id": 6,
    "kind": "deviation",
    "phase": "01",
    "file": "src/Iwasug.WhatsApp.Sdk/Iwasug.WhatsApp.Sdk.csproj",
    "line": null,
    "description": "01-04 [Rule 3]: InternalsVisibleTo for the tests assembly added in Task 2 (plan listed it under Task 3) because Task 2's StoreErrorSurface tests need internal access",
    "status": "open",
    "reason": "",
    "recorded_at": "2026-10-08T07:13:21.548Z",
    "resolved_at": null,
    "milestone": null
  },
  {
    "id": 7,
    "kind": "deviation",
    "phase": "01",
    "file": "tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreErrorSurfaceTests.cs",
    "line": null,
    "description": "01-04 [plan gap]: extra test file beyond the plan's <files> list — the plan mandated unit-testing LogAndWrap without naming a file for it",
    "status": "open",
    "reason": "",
    "recorded_at": "2026-10-08T07:13:22.129Z",
    "resolved_at": null,
    "milestone": null
  },
  {
    "id": 8,
    "kind": "deviation",
    "phase": "02",
    "file": "tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ConnectionStateTests.cs",
    "line": null,
    "description": "ConnectionStateTests.cs committed with 02-07 Task 2 though not in the plan files list (Rule 2: third loopback fake needed production intro validation)",
    "status": "open",
    "reason": "",
    "recorded_at": "2026-10-09T04:13:39.383Z",
    "resolved_at": null,
    "milestone": null
  },
  {
    "id": 9,
    "kind": "deviation",
    "phase": "02",
    "file": "src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs",
    "line": null,
    "description": "02-07 verify runs needed fresh-hash rebuilds or -p:Deterministic=false to clear Windows SAC 0x800711C7 blocks on newly built DLLs (environment-only, no repo change)",
    "status": "open",
    "reason": "",
    "recorded_at": "2026-10-09T04:13:40.232Z",
    "resolved_at": null,
    "milestone": null
  },
  {
    "id": 10,
    "kind": "stub",
    "phase": "04",
    "file": "src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs",
    "line": null,
    "description": "Device-identity pin of own identity DJB bytes (ponytail) - upgrade when live-peer identity verification requires exact wire form",
    "status": "open",
    "reason": "",
    "recorded_at": "2026-10-11T12:17:56.048Z",
    "resolved_at": null,
    "milestone": null
  },
  {
    "id": 11,
    "kind": "deviation",
    "phase": "4",
    "file": "src/Iwasug.WhatsApp.Sdk/Signal/DoubleRatchet.cs",
    "line": null,
    "description": "DoubleRatchet.LoadUnlockedAsync/SaveUnlockedAsync added outside the plan files list (Rule 2: per-address session gate concurrency edge for retry resend)",
    "status": "open",
    "reason": "",
    "recorded_at": "2026-10-11T16:06:32.896Z",
    "resolved_at": null,
    "milestone": null
  },
  {
    "id": 12,
    "kind": "stub",
    "phase": "4",
    "file": "src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs",
    "line": null,
    "description": "Device-identity child pins own identity DJB bytes (carried from 04-03 ponytail) in BuildResendNode; upgrade to marshalled Account proto at 04-05 UAT",
    "status": "open",
    "reason": "",
    "recorded_at": "2026-10-11T16:06:33.556Z",
    "resolved_at": null,
    "milestone": null
  },
  {
    "id": 13,
    "kind": "stub",
    "phase": "4",
    "file": "src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs",
    "line": null,
    "description": "verify-response-probe",
    "status": "waived",
    "reason": "Not a defect: accidental executor probe entry (verify-response-probe) created while checking CLI output shape during 04-04 execution — waiving to keep the ledger clean",
    "recorded_at": "2026-10-11T16:07:25.912Z",
    "resolved_at": "2026-10-11T16:08:47.331Z",
    "milestone": null
  },
  {
    "id": 14,
    "kind": "unrun-verify",
    "phase": "04",
    "file": ".planning/phases/04-signal-e2ee-messaging/04-VALIDATION.md",
    "line": null,
    "description": "Plan 04-05 Task 3 verify 'dotnet test Iwasug.WhatsApp.Sdk.sln' cannot exit 0 in this environment (discovers 0 tests, exit 5); executed evidence uses dotnet exec of the compiled test assembly - documented in 04-VALIDATION run-date block",
    "status": "open",
    "reason": "",
    "recorded_at": "2026-10-11T20:12:53.260Z",
    "resolved_at": null,
    "milestone": null
  },
  {
    "id": 15,
    "kind": "unrun-verify",
    "phase": "04",
    "file": ".planning/phases/04-signal-e2ee-messaging/04-VALIDATION.md",
    "line": null,
    "description": "SC 1-5 live-device UAT (1:1/group send, receipt round trip, quoted reply rendering, retry recovery) + assumptions A2/A4 recorded NOT VERIFIED - no live phone in CI; sample messaging mode built for the end-of-phase human gate",
    "status": "open",
    "reason": "",
    "recorded_at": "2026-10-11T20:12:53.861Z",
    "resolved_at": null,
    "milestone": null
  },
  {
    "id": 16,
    "kind": "deviation",
    "phase": "09",
    "file": "src/Iwasug.WhatsApp.Sdk/Pairing/PairingCoordinator.cs",
    "line": null,
    "description": "09-01 [Rule 2]: Ownership/zeroization contract documented on the randomBytes pairing seam (file not in the plan's Task 2 files list) - the invariant the repaired PairingLifecycleTests depend on",
    "status": "open",
    "reason": "",
    "recorded_at": "2026-10-16T23:09:51.697Z",
    "resolved_at": null,
    "milestone": null
  },
  {
    "id": 17,
    "kind": "deviation",
    "phase": "09",
    "file": "tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/MessagingLoopbackTests.cs",
    "line": 873,
    "description": "09-01 (pre-existing, deferred per scope boundary): decrypt_failure_no_ack_no_event_and_pump_continues is flaky - NRE at line 873 when the test continuation outruns the peer-task assignment after ConnectAsync; isolated + rerun runs green; fix direction (TaskCompletionSource handoff) in phases/09-ship/deferred-items.md",
    "status": "open",
    "reason": "",
    "recorded_at": "2026-10-16T23:09:52.888Z",
    "resolved_at": null,
    "milestone": null
  }
]
````
