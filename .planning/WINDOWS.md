---
schema_version: 1
open_count: 7
waived_count: 0
fixed_count: 0
total_count: 7
last_updated: 2026-10-08T07:13:22.129Z
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
  }
]
````
