# Phase 1: Foundation & Store Seam - Pattern Map

**Mapped:** 2026-10-07
**Files analyzed:** 24 (all new — greenfield)
**Analogs found:** 24 / 24 — all **external/reference-based** (no codebase analogs exist)

## GREENFIELD STATEMENT

This repository contains **zero source code**. Verified this session: `git ls-files` returns 35 files, all under `.planning/` plus `AGENTS.md`. No `.cs`, `.csproj`, `.sln`, or build props exist anywhere in the working tree. There are no in-repo analogs to copy from — every pattern below is anchored to a named external reference (official docs pattern, framework mechanism, or researcher-authored shape from `01-RESEARCH.md`).

**Tracked-source gate (#3645):** satisfied vacuously — no analog paths are claimed from the repo, so no gitignored-mirror risk exists. The `New File` column lists *planned* paths (to be created by the executor); the `Closest Analog` column names external reference sources only.

This phase's output **becomes** the repo's convention set: the shapes established here are what every later phase copies. Conventions worth locking deliberately are listed in "Shared Patterns".

## File Classification

Match-quality scale (greenfield adaptation): **direct** = an official ecosystem precedent exists for exactly this file shape; **shape** = pattern derived from a behavioral reference (no code copied — clean-room); **convention** = standard .NET project/config boilerplate.

| New File | Role | Data Flow | Closest Analog (external reference) | Match |
|----------|------|-----------|-------------------------------------|-------|
| `Iwasug.WhatsApp.Sdk.sln` | config | — | `dotnet new sln` standard layout (MS docs solution-file conventions) | convention |
| `LICENSE` (MIT) | config | — | Standard MIT text; STACK.md license table | convention |
| `NOTICE.md` / provenance policy | config | — | PITFALLS.md Pitfall 9 ("policy file lands before first protocol commit"); clean-room rules in STACK.md | direct |
| `README.md` stub | config | — | STACK.md trademark rule: "unofficial, not affiliated with WhatsApp/Meta" seeded now | convention |
| `src/Iwasug.WhatsApp.Sdk/Iwasug.WhatsApp.Sdk.csproj` | config | — | RESEARCH.md "Store-contract kit packaging (csproj shape)" + `IsAotCompatible`/PublicApiAnalyzers setup (Microsoft Learn) | convention |
| `src/.../Accounts/AccountId.cs` | model (value type) | transform (parse/validate) | RESEARCH.md Code Examples §AccountId (D-25/D-26); `readonly record struct` equality semantics (C# guide) | direct |
| `src/.../Domain/Jid.cs` | model (value type) | transform (parse/round-trip) | RESEARCH.md Code Examples §JID (D-16); `IParsable<T>`-style Parse/TryParse shape | direct |
| `src/.../Domain/*` (SignalSessionRecord, PreKeyRecord, SafetyState, identity/device types) | model | transform (typed records) | RESEARCH.md Pattern 1 typed-record signature; D-04 (typed objects, not byte blobs) | shape |
| `src/.../Errors/SdkException.cs` + 3 subtypes | utility (error model) | event-driven (throw surface) | RESEARCH.md Code Examples §Error taxonomy (D-17/D-21); .NET exception-design guidelines (FDG) | direct |
| `src/.../Stores/IIdentityStore.cs` (+ factory) | store (interface seam) | CRUD | RESEARCH.md Pattern 1 (async factory, D-02/D-22); whatsmeow granular-interface shape (behavioral only) | shape |
| `src/.../Stores/ISignalSessionStore.cs` (+ factory) | store (interface seam) | CRUD | RESEARCH.md Pattern 1 example code (exact researcher-authored C# shape) | direct |
| `src/.../Stores/IPreKeyStore.cs` (+ factory) | store (interface seam) | CRUD | RESEARCH.md Pattern 1 (same factory/CRUD shape); whatsmeow PreKeyStore behavioral shape | shape |
| `src/.../Stores/IRecentMessageCacheStore.cs` (+ factory, trim primitives) | store (interface seam) | CRUD + batch (trim/purge) | RESEARCH.md Pitfall 5 avoidance (primitives-only trim, D-08); `IOptions<T>` policy pattern (Microsoft.Extensions.Options 10.0.12) | shape |
| `src/.../Stores/ISafetyStateStore.cs` (+ factory) | store (interface seam) | CRUD | RESEARCH.md Pattern 1 (same shape); D-05 durability class | shape |
| `src/.../Stores/InMemory/InMemoryStoreFactory.cs` (+ 5 account-scoped impls) | store (reference impl) | CRUD (concurrent, in-proc) | RESEARCH.md Pattern 4 (ConcurrentDictionary-backed, per-account views, lock-per-account); BCL `ConcurrentDictionary`/`SemaphoreSlim` idioms | direct |
| `src/Iwasug.WhatsApp.Sdk.Testing/Iwasug.WhatsApp.Sdk.Testing.csproj` | config | — | RESEARCH.md csproj shape (exact XML); xunit.net `/docs/nuget-packages-v3` rule (extensibility.core, Pitfall 3) | direct |
| `src/...Testing/StoreContract/*.cs` (5 abstract contract classes) | test (infrastructure) | CRUD-verification | RESEARCH.md Pattern 3 (EF Core Specification-Tests pattern + xunit v3 abstract-base mechanics) — exact excerpt below | direct |
| `src/...Testing/Rules/*.cs` (durability-await, failure-vs-missing, isolation, trimming, concurrency helpers) | test (infrastructure) | CRUD + fault-injection + concurrency | RESEARCH.md Patterns 2 (failure≠missing) & 4 (FakeTimeProvider for TTL); Pitfall 4/7 avoidance | direct |
| `src/...Testing/FaultInjection/*` (faulting decorator factory) | test (utility) | event-driven (fault injection) | RESEARCH.md Pitfall 2 avoidance — "kit must ship a fault-injecting decorator factory as part of the kit's public API" | shape |
| `src/...Testing/ComplianceTestBase.cs` | test (utility) | batch (completeness check) | EF Core `ComplianceTestBase` completeness trick (writing-a-provider docs, cited in RESEARCH.md) | direct |
| `tests/Iwasug.WhatsApp.Sdk.Tests/Iwasug.WhatsApp.Sdk.Tests.csproj` | config | — | RESEARCH.md Installation block (xunit.v3 + Test.Sdk + AwesomeAssertions + NSubstitute + coverlet) | convention |
| `tests/.../StoreContract/*.cs` (reference binding + second scratch impl binding) | test | CRUD-verification | RESEARCH.md Pattern 3 subclass example (both internal bindings); success criterion 3 | direct |
| `tests/.../Core/*.cs` (AccountId/Jid/error unit tests) | test | transform-verification | Standard xunit.v3 unit-test layout; D-16/D-25 test map rows in 01-VALIDATION.md | convention |
| `Directory.Build.props` + `.editorconfig` (optional, planner's discretion) | config | — | Standard .NET repo central-build-management convention (STACK.md "Conventions not yet established — this phase populates them") | convention |

## Pattern Assignments

### `src/.../Stores/ISignalSessionStore.cs` + all five store interfaces (store, CRUD)

**Analog:** RESEARCH.md Pattern 1 (researcher-authored C# shape; whatsmeow `store/store.go` granular-interface *behavior* only — MPL-2.0, no code copied)

**Interface + factory shape** (RESEARCH.md lines 270-287 — copy this shape for all five stores):
```csharp
public interface ISignalSessionStoreFactory
{
    ValueTask<ISignalSessionStore> CreateAsync(AccountId accountId, CancellationToken cancellationToken = default);
}

public interface ISignalSessionStore : IAsyncDisposable
{
    // D-19: null when missing; throw on read failure. Typed record (D-04).
    Task<SignalSessionRecord?> GetAsync(string address, CancellationToken cancellationToken);

    // D-06: SDK awaits every durable write before continuing protocol work.
    Task PutAsync(string address, SignalSessionRecord session, CancellationToken cancellationToken);

    Task<bool> RemoveAsync(string address, CancellationToken cancellationToken);
}
```

**Contract rules to encode in every interface (XML docs + signatures):**
- `Task`-returning on store methods (TAP guidance — `ValueTask` imposes single-consumption on every implementer; RESEARCH.md Anti-Patterns). `ValueTask<T>` is acceptable on the factory's `CreateAsync` only (called once per account, D-24 — RESEARCH.md Open Question 3; planner picks one convention repo-wide).
- No `accountId` parameters inside store methods — the instance *is* the namespace (D-02; Anti-Patterns list).
- No `byte[]` blob APIs — typed domain objects only (D-04; Anti-Patterns list).
- Single-key CRUD only — no transaction API (D-07; Anti-Patterns list).
- Ship the full surface day one — no default-interface-member versioning plan (FDG; Anti-Patterns list).
- Factory is async `CreateAsync` from day one even though the reference impl is sync (Pitfall 1; reference returns `Task.FromResult`).

**Cache-store deviation** (D-08, Pitfall 5): `IRecentMessageCacheStore` adds primitives only — `TrimOldest(int)`, optional `PurgeExpired(DateTimeOffset cutoff)` — with **no policy parameters**; bounds live in SDK-owned `IOptions<T>` policy (package: `Microsoft.Extensions.Options` 10.0.12 — `Options.Abstractions` does not exist on nuget.org, verified). Warning sign of violation: any method named like `TrimAccordingToPolicy(...)`.

---

### `src/.../Stores/InMemory/InMemoryStoreFactory.cs` (store, CRUD in-proc)

**Analog:** RESEARCH.md Pattern 4 + BCL concurrent-collections idioms

**Shape** (RESEARCH.md Pattern 4 verbatim intent): one `InMemoryStoreFactory` owning `ConcurrentDictionary<(string capability, AccountId), object>` of per-account state; `CreateAsync` returns light account-scoped view objects; view `DisposeAsync` releases nothing global (factory disposal clears all). Deterministic lock-per-account (`SemaphoreSlim`) where ordering matters. Don't-hand-roll table: `ConcurrentDictionary` + per-account `SemaphoreSlim` over manual `lock` everywhere.

Key behaviors the kit will prove against it: missing key returns null (not throw), factory is async (returns completed tasks naturally), two accounts driven concurrently see zero cross-reads.

---

### `src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/*.cs` (test infra, CRUD-verification)

**Analog:** RESEARCH.md Pattern 3 = EF Core Specification-Tests consumption pattern + xunit v3 abstract-base-class mechanics

**Kit class shape** (RESEARCH.md lines 313-331 — the mechanical core of D-10/D-11):
```csharp
public abstract class SignalSessionStoreContractTests
{
    protected abstract ISignalSessionStoreFactory CreateFactory();   // subclass binds impl

    [Fact]
    public async Task Put_then_Get_round_trips_typed_record() { /* … */ }

    [Fact]
    public async Task Get_missing_address_returns_null_not_throw() { /* … */ }  // D-19

    [Fact]
    public async Task Two_accounts_isolated_same_address() { /* … */ }          // D-02
}

public sealed class InMemorySignalSessionStoreContractTests : SignalSessionStoreContractTests
{
    protected override ISignalSessionStoreFactory CreateFactory() => new InMemoryStoreFactory();
}
// Host side: public sealed class PostgresSignalSessionStoreContractTests : SignalSessionStoreContractTests { … }
```

Mechanics that make this work: xunit v3 runs `[Fact]` methods on abstract base classes in every concrete subclass — that is the entire dispatch mechanism; no custom runner (Don't-Hand-Roll: contract-test dispatch).

Granularity (RESEARCH.md Open Question 1, recommendation): **one abstract contract class per store** (5 classes), each calling shared rule helpers from `Rules/` — fewer classes for hosts to subclass; completeness still enforced via `ComplianceTestBase`.

Rule families each contract class must cover (D-11 full semantic contract): durability-await semantics, failure≠missing (fault injection), per-account isolation (parallel), cache trimming calls, concurrency under parallel access.

---

### `src/...Testing/ComplianceTestBase.cs` (test utility)

**Analog:** EF Core `ComplianceTestBase` completeness trick (official writing-a-provider docs)

**Shape:** a concrete test class that reflects over the kit's contract base classes and fails when any one of them is not subclassed somewhere in the running assembly set — mechanically catches contract classes added later without a binding. Keep it in the kit so host suites get the check for free.

---

### `src/...Testing/FaultInjection/*` (test utility, fault injection)

**Analog:** RESEARCH.md Pitfall 2 avoidance (D-19 enforcement)

**Shape:** a kit-public decorator factory that wraps any `I*StoreFactory` and fails on demand (throw-on-read / throw-on-write, configurable). The failure≠missing contract classes use it to assert: injected read failure **throws** `SdkStoreException` (never mapped to null) while a genuinely missing key returns null.

**failure≠missing reference behavior** (RESEARCH.md lines 293-305):
```csharp
try
{
    record = await store.GetAsync(address, ct);   // null = missing (fine)
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    // read failure — NEVER mapped to null; D-19 + D-21 (throw + ILogger)
    throw;
}
```
Upstream behavioral precedent: whatsmeow sqlstore maps `sql.ErrNoRows` → `(nil, nil)` while real query errors propagate — same discipline (behavioral observation only, no code).

---

### `src/.../Accounts/AccountId.cs` (model, transform)

**Analog:** RESEARCH.md Code Examples §AccountId (D-25/D-26)

**Shape** (RESEARCH.md lines 418-431):
```csharp
public readonly partial record struct AccountId : IComparable<AccountId>
{
    public string Value { get; }          // host-chosen; treated as opaque by the SDK
    private AccountId(string value) => Value = value;

    // Discretion area (A4): case-SENSITIVE, non-empty, no control chars/whitespace-only,
    // length cap ~128; uniqueness enforced by the SDK's account registry, not the type (D-26).
    public static AccountId Create(string value) { /* validate + construct */ }

    public override string ToString() => Value;
    public int CompareTo(AccountId other) => string.CompareOrdinal(Value, other.Value);
}
```
Rationale (researcher-recommended): `record struct` gives equality/hashing free; validation-only construction prevents `default(AccountId)` misuse; ordinal compare because hosts' backends differ in collation.

---

### `src/.../Domain/Jid.cs` (model, transform)

**Analog:** RESEARCH.md Code Examples §JID (D-16)

**Shape** (RESEARCH.md lines 437-448):
```csharp
public sealed partial record class Jid   // or readonly record struct — planner's call (A3)
{
    public string User { get; }          // phone/LID part
    public string Server { get; }        // exact server set arrives with Phase 2
    public string? Agent { get; }        // device/agent component when present
    public int? Device { get; }

    public static Jid Parse(string value);            // throws SdkFormatException
    public static bool TryParse(string value, out Jid? jid);
    public override string ToString();                // always canonical reconstruction
}
```
Phase 1 delivers parse/round-trip + tests; canonicalization rules (device suffix rendering, `@lid`) deepen in Phase 2/4. Scope guard (Pitfall 6): no `Node`, `MessageId`, `ReceiptType` in `Domain/` — wire vocabulary waits for Phase 2 (D-15).

---

### `src/.../Errors/SdkException.cs` (utility, throw surface)

**Analog:** RESEARCH.md Code Examples §Error taxonomy (D-17/D-21)

**Shape** (RESEARCH.md lines 452-462 — Phase 1 minimum set):
```csharp
public abstract class SdkException : Exception
{
    protected SdkException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class SdkStoreException : SdkException { }        // D-21: store failures to awaiting caller
public sealed class SdkFormatException : SdkException { }       // Jid.Parse / AccountId validation
public sealed class SdkDuplicateAccountException : SdkException { }  // D-26 uniqueness
```
Message discipline (security table, V7): no sensitive payloads in exception messages; D-18's bounded-retry wrapper (e.g. 3 attempts, 50-250 ms jittered backoff — A1) is *specified in XML docs now*, first real caller is the Phase 2+ protocol loop. Failure surfacing (D-21): exception to awaiting caller + `ILogger` warning/error; no typed store-failure event yet. Cache write failure (D-20): log & continue, never propagate.

---

### Repo bootstrap csproj/sln files (config)

**Analog:** RESEARCH.md "Store-contract kit packaging (csproj shape)" — exact XML for the kit project:
```xml
<!-- src/Iwasug.WhatsApp.Sdk.Testing/Iwasug.WhatsApp.Sdk.Testing.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>Iwasug.WhatsApp.Sdk.Testing</RootNamespace>
    <IsPackable>true</IsPackable>
    <!-- NOT an executable test project: no xunit.v3 / Test.Sdk here -->
    <PackageReference Include="Iwasug.WhatsApp.Sdk" Version="..." />
    <PackageReference Include="xunit.v3.extensibility.core" Version="4.0.1" />
    <IsAotCompatible>true</IsAotCompatible>
  </PropertyGroup>
</Project>
```

Hard packaging rules:
- Kit references **`xunit.v3.extensibility.core` 4.0.1 only** — referencing `xunit.v3`/`xunit.v3.core` injects MSBuild steps that turn the project into an executable test project (Pitfall 3; warning sign: apphost / `<OutputType>Exe</OutputType>` appears).
- Consumers (the `tests/` project) reference `xunit.v3` 4.0.1 + `Microsoft.NET.Test.Sdk` 18.10.1 + AwesomeAssertions 9.6.0 + NSubstitute 6.2.0 + coverlet.collector 10.1.0 themselves.
- Main SDK references `Microsoft.Extensions.Logging.Abstractions` 10.0.12 + `Microsoft.Extensions.Options` 10.0.12 (NOT `Options.Abstractions` — package does not exist, 404-verified).
- PublicApiAnalyzers 5.6.0 at bootstrap (A5): add `PublicAPI.Shipped.txt` (empty) + `PublicAPI.Unshipped.txt` to both packable projects.
- Layout per D-14: root solution + `src/` + `tests/` only. D-12: exactly two packages — reference store lives in the main package (no `Sdk.Store.InMemory` split; that pre-dates D-12 in project-level ARCHITECTURE.md — D-12/D-13 govern). Root namespace = package ID (D-13, one-way).
- `Directory.Build.props` (optional, discretion): central TFM `net10.0`, LangVersion, nullable-enable, warnings-as-errors, package pins — keeps the two csproj files thin and gives later phases one place for conventions.

---

### `tests/.../StoreContract/*.cs` (test bindings)

**Analog:** RESEARCH.md Pattern 3 subclass example (shown above) — internal bindings

**Shape:** two concrete binding families in the internal test project:
1. Reference binding — one sealed subclass per contract base class, `CreateFactory()` returns `new InMemoryStoreFactory()` (proves STR-02).
2. Second scratch binding — a deliberately-different minimal implementation (e.g. non-concurrent dictionary wrapper) with its own subclasses — proves success criterion 3 ("same suite, two implementations, zero SDK changes") mechanically true from day one.

Command contract (01-VALIDATION.md): every task verified by `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests`; wave gates run `dotnet test` + warnings-as-errors build.

---

### `NOTICE.md` / provenance policy (config)

**Analog:** PITFALLS.md Pitfall 9 ("the *policy file* should land in repo bootstrap, before protocol coding starts")

**Shape:** clean-room policy text: no verbatim copying from whatsmeow (MPL-2.0), no mechanical translation, no header-stripped files; when upstream code must be read, record file+line as provenance; PR checklist item — "any protocol constant or structure introduced: cite source". Phase 1 touches no protocol code, but the policy lands here per the pitfall's phase mapping.

## Shared Patterns

These are the repo's **founding conventions** — later phases copy them from this phase's code, so they should be applied uniformly across all files this phase creates.

### Async signature convention
**Source:** RESEARCH.md Pattern 1 + TAP guidance (CA2012)
**Apply to:** all five store interfaces, factories, and the in-memory reference impl
```csharp
Task<SignalSessionRecord?> GetAsync(string address, CancellationToken cancellationToken);
```
`Task` on host-implemented store methods; `CancellationToken` parameter on every async method; `ValueTask<T>` only on factory `CreateAsync` if the planner adopts that option (Open Question 3 — either is defensible, pick one repo-wide and record it).

### Durability contract encoded in docs + tests
**Source:** RESEARCH.md Pattern 2, Pitfall 7 avoidance
**Apply to:** all store interfaces (XML docs) + kit durability rule class
The store-side of D-06 is testable now: writes complete only when an independent subsequent read observes the value. SDK-side enforcement (protocol loop awaiting) is Phase 2+ — document on interfaces with XML docs now; do not defer the kit tests.

### Namespace = package ID
**Source:** D-13 (one-way)
**Apply to:** every file
`Iwasug.WhatsApp.Sdk` and `Iwasug.WhatsApp.Sdk.Testing` root namespaces; folder layout in the csproj mirrors `Accounts/`, `Stores/`, `Stores/InMemory/`, `Domain/`, `Errors/` under the main package, `StoreContract/`, `Rules/`, `FaultInjection/` under the kit.

### Test-time determinism
**Source:** RESEARCH.md Don't-Hand-Roll table
**Apply to:** kit TTL/trim rule classes
`TimeProvider` + `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing` 10.10.0) — never `Thread.Sleep`/real clocks in trim/TTL tests.

### Public-surface tracking
**Source:** RESEARCH.md Don't-Hand-Roll table (A5)
**Apply to:** both packable projects
PublicApiAnalyzers + `PublicAPI.Unshipped.txt` from day one — compile-time gate on the store interfaces, which are the most versioning-sensitive surface in the SDK.

### Concurrency-safety primitives
**Source:** RESEARCH.md Pattern 4 + Don't-Hand-Roll table
**Apply to:** in-memory reference store
`ConcurrentDictionary` for shared maps, `SemaphoreSlim` per account where write ordering matters; no global mutable statics (Pitfall 8 hard rule — no static/singleton holding account data).

## No Analog Found

**None in the traditional sense — and none needed.** Every planned file has a named external reference pattern (see File Classification). The honest caveat: all "analogs" are references to *patterns and mechanics*, not copyable code:

- EF Core Specification-Tests / xunit v3 mechanics — official docs describe the pattern; no code copied.
- whatsmeow / Baileys store shapes — behavioral observations only (MPL-2.0 / clean-room: zero code, schemas, or generated output copied; see NOTICE.md assignment).
- The C# excerpt shapes in RESEARCH.md are researcher-authored for this project and are the closest thing to in-repo analogs that exists — the planner should treat them as the canonical starting shapes and refine within the discretion areas (factory Task/ValueTask choice, AccountId validation rules A4, retry parameters A1, contract-class granularity, cache read enumeration Open Question 2).

## Metadata

**Analog search scope:** full repo tree (Glob `**/*.{cs,csproj,sln,slnx,md,json,props,targets,yml,yaml}`; `git ls-files` audit) — confirmed greenfield; then `.planning/research/{STACK,ARCHITECTURE,PITFALLS}.md` and `.planning/phases/01-foundation-store-seam/{01-CONTEXT,01-RESEARCH,01-VALIDATION}.md` as the reference-pattern corpus.

**Files scanned:** 35 tracked files (all planning docs / AGENTS.md), 0 source files.
**Pattern extraction date:** 2026-10-07
