# Store Contract Kit — Test Suite

This project runs the store-contract test kit (`Iwasug.WhatsApp.Sdk.Testing`) against the
SDK's in-memory reference store and a deliberately-different scratch implementation, plus
the composition-layer unit tests (`Stores/`).

## Run the suite

From the repository root:

```bash
dotnet test
```

> Do NOT pass `--nologo` to `dotnet test`: on the pinned SDK (10.0.401) it exits 5
> with "Zero tests ran" and a swallowed error (IN-10). `--nologo` remains fine for
> `dotnet build`. Always rebuild before trusting a test count — a `--no-build` run
> against a stale `bin/Debug` payload under-reports.

Filtered runs use the xunit.v3 MTP dialect (`--filter` is VSTest-only and rejected by this
repo's runner — see the test csproj comment):

```bash
dotnet exec tests/Iwasug.WhatsApp.Sdk.Tests/bin/Debug/net10.0/Iwasug.WhatsApp.Sdk.Tests.dll -method "*Host_style*"
```

## How a host verifies their own store implementation

The kit is the same mechanism EF Core uses for provider conformance: one set of abstract
contract classes, one binding per implementation.

1. Reference the two NuGet packages (once published): `Iwasug.WhatsApp.Sdk` (the SDK — it
   defines the five store interfaces) and `Iwasug.WhatsApp.Sdk.Testing` (the kit).
2. Subclass each kit contract class you want to enforce — `SignalSessionStoreContractTests`,
   `IdentityStoreContractTests`, `PreKeyStoreContractTests`,
   `RecentMessageCacheStoreContractTests`, `SafetyStateStoreContractTests` — and override
   `CreateFactory` to return YOUR factory, e.g.
   `protected override ISignalSessionStoreFactory CreateFactory() => new MyStoreFactory();`.
   xunit runs every inherited `[Fact]` against your implementation.
3. Subclass `ComplianceTestBase` once with an empty concrete type so the completeness
   backstop (every contract class has a binding) runs in your assembly.
4. Run your test project. A failing fact is a contract violation in your store — fix the
   store, never the contract class.

`HostStyleSecondRunTests.cs` in this project is a worked example: it drives all five
capabilities through one factory in a single flow, exactly as a host adoption would.

## Clean-room policy

The SDK and the kit are clean-room implementations — see [NOTICE.md](../../NOTICE.md).
The full store-implementation guide (per-store semantics, failure-vs-missing rules,
indexing advice for real databases) ships with the Phase 9 documentation.
