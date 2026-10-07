# Phase 1: Foundation & Store Seam - Research

**Researched:** 2026-10-07
**Domain:** .NET 10 class-library foundation — pluggable persistence seam (five granular store interfaces, per-account namespacing, in-memory reference store, published store-contract test kit), lean repo bootstrap
**Confidence:** HIGH (stack versions registry-verified this session; contract-kit patterns from official xunit/EF Core docs; store-shape comparisons read from reference-implementation source, behavioral only)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

**Store wiring & composition**
- **D-01:** Host registers persistence as five separate interfaces (identity, signal sessions, prekeys, recent-message cache, safety state) — no aggregate facade, no hybrid bundle. — **Reversibility:** costly — public registration surface; hosts wiring one aggregate later would need re-registration migration
- **D-02:** Per-account namespacing is structural: an async factory (`CreateFor`/`CreateAsync(accountId)`) returns store instances scoped to exactly one account. Not shared-instance-with-accountId-param, not keyed DI. — **Reversibility:** costly — factory shape appears in every host integration
- **D-03:** All five stores are optional; the in-memory reference store fills any gap. A host may register only a subset. — **Reversibility:** costly — behavioral contract hosts rely on; making a store mandatory later breaks partial implementations
- **D-04:** Store interfaces expose typed domain objects (signal session, prekey bundle, identity types), not byte blobs. Hosts may inspect/migrate stored data; the SDK owns serialization format and it is not a public contract. — **Reversibility:** costly — host databases will contain SDK-serialized payloads; a format change later needs host data migration

**Durability contract**
- **D-05:** Durability classification: identity, signal sessions, prekeys, safety state = must-not-lose; recent-message cache = lossy by definition (reply/quote degrades gracefully when empty). — **Reversibility:** costly — classification drives every store contract test and host implementation
- **D-06:** The SDK awaits every durable write before continuing protocol work (no fire-and-forget on durable stores) — a crash never loses an acknowledged state change. — **Reversibility:** costly — timing is baked into the protocol loop; changing it later alters crash-recovery guarantees
- **D-07:** Store contract guarantees single-key CRUD only. The SDK sequences multi-step state changes itself (e.g., consume prekey → then save session) — no transaction API required, works against any backend. — **Reversibility:** costly — hosts design their implementations against this guarantee
- **D-08:** The recent-message cache is bounded by SDK policy via `IOptions<T>` (max entries/messages per account, optional TTL); the SDK issues trim/delete calls through the store. Host does not own retention. — **Reversibility:** costly — trim primitives become part of the interface hosts implement

**Reference store & test-kit**
- **D-09:** In-memory reference store positioning = prototype-capable and honestly documented: usable for prototyping/low-volume; docs state clearly it does not survive restart. Not test-only, no runtime warnings. — **Reversibility:** reversible — docs/positioning change costs a docs edit
- **D-10:** The store-contract test suite ships as a published, host-runnable test-kit package (EF Core test-kit style), not an internal-only suite. — **Reversibility:** costly — second published package; hosts adopting it create a versioning obligation
- **D-11:** In Phase 1 the suite enforces the FULL semantic contract: durability classification (await behavior), per-account isolation, cache trimming calls, concurrency under parallel access — not just CRUD round-trips. Same suite passes against reference and host implementations (success criterion 3). — **Reversibility:** reversible — adding tests later is cheap; the depth decision sets floor, not ceiling

**Package & namespace shape**
- **D-12:** v1 ships exactly two NuGet packages: the main SDK and the test kit. No pre-split of wire/signal/media — split later only with a real reason. — **Reversibility:** costly — package graph is hard to change once published
- **D-13:** Package ID and C# root namespace: `Iwasug.WhatsApp.Sdk`; test kit: `Iwasug.WhatsApp.Sdk.Testing`. GitHub repository will be named `Iwasug.WhatsApp.Sdk`. Root namespace = package ID. — **Reversibility:** one-way — NuGet package IDs are effectively permanent once published; renaming orphans every existing install
- **D-14:** Phase 1 repo bootstrap is minimal: `src/` + `tests/` + root solution file only. Samples, docs skeleton, and CI are explicitly deferred to their roadmap phases (samples/docs/CI → Phase 9). — **Reversibility:** reversible — structure can grow later without breaking anything

**Core types scope**
- **D-15:** "Core types" = lean and store-driven: JID/address types, account/device identity, store domain types (signal session, prekey bundle, safety state), minimal error taxonomy. Wire vocabulary (node tree, message IDs, presence enums, receipt types) is deferred until the phase that uses it — Phase 2 research shapes it. — **Reversibility:** reversible — later phases add types freely; the lean scope means little to undo
- **D-16:** JID is an immutable parsed value type: construction parses, exposes `User`/`Server`/`Agent`/`Device` components, `ToString()` always reconstructs the canonical string. Not raw strings, not a class hierarchy. — **Reversibility:** one-way — JID is the most-used type across the entire public API; changing it breaks every call site and every host signature
- **D-17:** Public error model = typed exception hierarchy (`SdkException` base + subtypes); async APIs throw. Not Result types, not a hybrid. Store failures follow the same model (see below). — **Reversibility:** costly — hosts write `catch` blocks against these types; changing the model breaks handler code

**Store failure handling**
- **D-18:** Durable store write failure: bounded retry with short backoff, then propagate — protocol work for that account stops; the SDK never acknowledges state that isn't persisted. — **Reversibility:** costly — behavioral contract observable by hosts and assumed by the durability tests
- **D-19:** Strict `failure ≠ missing` on ALL stores: a read failure throws, missing data returns null/None — never conflated. Critical for signal sessions where a DB blip misread as "empty session" would trigger wrong re-establishment. — **Reversibility:** costly — security-sensitive contract; host implementations and tests are built around it
- **D-20:** Recent-message cache write failure = log & continue (`ILogger` warning), no propagate. Retry/propagate applies only to the four durable stores. — **Reversibility:** reversible
- **D-21:** Store failures surface as: exception to the awaiting caller + `ILogger` warning/error from the SDK. No typed store-failure event (yet). — **Reversibility:** costly — exception surface is public API; adding an event later is additive, removing types is breaking

**Store lifecycle & disposal**
- **D-22:** The factory is async (`CreateAsync`) — hosts can open connections, initialize, etc. — **Reversibility:** costly — factory signature is public API
- **D-23:** The SDK owns every store instance it creates: it disposes them (`IAsyncDisposable` where available) on account removal and client shutdown. Host never tracks SDK-requested lifecycles. — **Reversibility:** costly — ownership semantics must match host expectations of their own resources
- **D-24:** Store instances are long-lived per account: created once at account add, reused until remove/shutdown. Not per-operation, not per-connection. — **Reversibility:** reversible

**Account key type**
- **D-25:** The per-account namespace key is `AccountId`, an opaque value type wrapping a host-chosen string (CRM ID, tenant ID, phone — host's choice). The SDK treats it as opaque; the mapping to WhatsApp identity lives in the identity store, not in the key. — **Reversibility:** one-way — the key type is embedded in every store method signature and in hosts' persisted namespace layout; renaming = host data migration
- **D-26:** `AccountId` is host-provided when adding an account; the SDK validates uniqueness and rejects duplicates. Not SDK-generated GUIDs. — **Reversibility:** costly — additive validation is safe, but switching to SDK-generated IDs later would orphan host-assigned keys

### Claude's Discretion
Left to researcher/planner (user was not asked): concrete retry/backoff parameters and attempt counts for D-18; `AccountId` validation rules (allowed characters, case sensitivity — recommend case-sensitive opaque with a documented uniqueness rule); exact composition of the exception subtype taxonomy; which wire types get deferred from Phase 1's lean set (researcher confirms store seam needs nothing else); solution/project file layout conventions within `src/` + `tests/`; store interface method naming and async patterns (`Task` vs `ValueTask`, cancellation); serialization mechanism for D-04 (implementation detail — hosts never see it).

### Deferred Ideas (OUT OF SCOPE)
None — discussion stayed within phase scope.
</user_constraints>

## Summary

Phase 1 is the "store interfaces defined first" move that both reference ecosystems validated: whatsmeow's `store` package defines granular per-concern interfaces (IdentityStore, SessionStore, PreKeyStore, …) that a `Device` aggregates per account, and its sqlstore shows per-account namespacing done structurally (every operation scoped to a device instance, `our_jid` as the leading column of every row). This phase translates that shape into .NET with the decisions the user already locked: five separate interfaces (D-01), an async factory per interface returning account-scoped instances (D-02), typed domain objects rather than byte blobs (D-04 — a deliberate deviation from whatsmeow's `[]byte` sessions), and a full semantic contract (durability await behavior, failure≠missing, isolation, trimming) enforced by a published test kit (D-10/D-11) modeled on EF Core's `Microsoft.EntityFrameworkCore.Specification.Tests` pattern — a regular NuGet package of abstract xunit base classes that hosts subclass.

The main new research findings this session: (1) **the contract-kit packaging mechanics are concrete** — in xunit v3, `[Fact]` methods on abstract base classes run in every concrete subclass, and the kit's *package* must depend on `xunit.v3.extensibility.core` (not `xunit.v3`, which injects MSBuild steps that turn a project into an executable test project — a hard error for a normal class library); hosts reference the kit from their own executable test project. (2) **`Microsoft.Extensions.Options.Abstractions` does not exist on nuget.org** (verified 404 across flatcontainer/registration/search this session) — D-08's `IOptions<T>` comes from `Microsoft.Extensions.Options` 10.0.12. (3) whatsmeow's sqlstore confirms the `failure ≠ missing` discipline (D-19): its `GetSession` maps `sql.ErrNoRows` to `(nil, nil)` while real query errors propagate — exactly the shape D-19 mandates. (4) The EF Core spec-tests precedent validates D-10/D-11 end to end, including the completeness trick (`ComplianceTest` extends `ComplianceTestBase` and fails if a contract class isn't subclassed) worth adopting for the kit.

The MVP Walking Skeleton for this phase is thin and well-defined: solution + two projects (`Iwasug.WhatsApp.Sdk`, `Iwasug.WhatsApp.Sdk.Testing`) + a test project; one real store (identity or sessions) implemented end-to-end in the in-memory reference store, one real read/write flowing through the factory, and the contract-suite pattern proven with a handful of real contract classes passing against the reference implementation. Everything else (full five-store surface, trimming options, retry policy, exception taxonomy breadth) is additive on top of that skeleton and should be planned as separate fine-grained tasks.

**Primary recommendation:** Build `AccountId` + the five async factory-shaped store interfaces with typed domain objects over `Task`-returning methods taking `CancellationToken`; ship `InMemoryStoreFactory` satisfying all five; publish the contract suite as an abstract-class library that depends on `xunit.v3.extensibility.core`; prove the seam with the EF Core compliance-test pattern so "same suite, two implementations" is mechanically true from day one.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Store interfaces (identity, sessions, prekeys, cache, safety) | API/Backend (SDK library surface) | — | Public seam hosts implement; lives in main package, dependency-light |
| Per-account namespacing (AccountId, async factory) | API/Backend (SDK library surface) | — | Structural contract in every signature; host-facing |
| In-memory reference store | API/Backend (SDK library surface) | Database/Storage (in-proc) | SDK-provided reference impl; no external DB |
| Store-contract test kit | Test infrastructure (published package) | — | Separate NuGet package; hosts run it against their impl |
| Core domain types (JID, AccountId, identity, error taxonomy) | API/Backend (SDK library surface) | — | Shared vocabulary; no protocol knowledge (D-15) |
| Durability/await + retry policy (D-06/D-18) | API/Backend (SDK library surface) | — | Protocol-loop timing contract; interfaces document it; enforcement begins when protocol loop exists (Phase 2+) |
| Repo bootstrap (solution, src/, tests/) | Build/packaging | — | D-14 minimal layout |

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| STR-01 | Host can plug its own persistence via granular store interfaces (identity, signal sessions, prekeys, recent-message cache, safety state) namespaced per account | Five-interface shape (D-01) with whatsmeow granular-interface precedent; async factory namespacing (D-02) with `Container.NewDevice`-style scoping precedent; typed domain objects (D-04); Task/CancellationToken conventions from official TAP docs; interface-evolution guidance (DIM judgment rule, FDG "don't add members to shipped interface" → ship full surface day one) |
| STR-02 | SDK ships a working in-memory reference store implementation | In-memory reference impl pattern (ConcurrentDictionary-backed, account-scoped instances from one factory); contract suite proves it — EF Core spec-tests inheritance pattern (official provider docs) + xunit v3 abstract-base-class facts run in subclasses; kit packaging via `xunit.v3.extensibility.core` (official xunit package guide); same suite runs against reference and host impls (success criterion 3) |
</phase_requirements>

## Project Constraints (from AGENTS.md / PROJECT.md)

- **Tech stack:** .NET 10 LTS (SDK 10.0.401, runtime 10.0.12, C# 14), `net10.0` target framework. Do not re-litigate stack choices — NSec, Google.Protobuf, System.IO.Pipelines, Channels, RateLimiting are decided (not needed this phase except Logging.Abstractions/Options).
- **State:** pluggable store interface — the host owns persistence; **no DB in the SDK** (EF Core/Dapper forbidden inside the SDK — What-NOT-Use table).
- **License:** MIT, clean-room — **no code, `.proto` files, or generated output copied from whatsmeow (MPL-2.0)**; behavioral reference only. This phase touches no protocol code, but the provenance/NOTICE policy file should land in repo bootstrap per PITFALLS.md (Pitfall 9: "policy file lands before first protocol commit").
- **Compliance:** unofficial — docs must state not-affiliated with WhatsApp/Meta (Phase 9 deliverable; harmless to seed in README stub now).
- **Package ID / namespace (D-13):** `Iwasug.WhatsApp.Sdk` / `Iwasug.WhatsApp.Sdk.Testing` — one-way decision, use exactly.
- **GSD workflow enforcement:** all repo edits go through GSD flow (planning artifacts in sync).

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| .NET SDK / runtime | 10.0.401 / 10.0.12 | `net10.0` class library | LTS (EOL 2028-11-14); installed on this machine (verified `dotnet --list-sdks`) [VERIFIED: local env probe] |
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.12 | `ILogger` for D-20/D-21 (log & continue, failure logging) | Standard embeddable-library contract; Abstractions only [VERIFIED: nuget.org flatcontainer — versions list shows 10.0.12 latest stable] |
| `Microsoft.Extensions.Options` | 10.0.12 | `IOptions<T>`/`IOptionsMonitor<T>` for D-08 cache-bounds policy | **Not** `Options.Abstractions` — that package ID does not exist on nuget.org (verified 404 this session); `IOptions<T>` ships in `Microsoft.Extensions.Options`. Deps: DI.Abstractions + Primitives (both also 10.0.12) [VERIFIED: nuget.org flatcontainer + registration5-semver1 dependency metadata] |
| `xunit.v3` | 4.0.1 | Test framework (own test projects) | Current generation, MTP runner, net10-compatible [VERIFIED: nuget.org flatcontainer — 4.0.1 latest] |
| `xunit.v3.extensibility.core` | 4.0.1 | Dependency of the **published test kit** (abstract contract classes) | Official rule: non-test projects must reference this, not `xunit.v3`/`xunit.v3.core`, because those inject MSBuild steps making the project an executable test project [VERIFIED: xunit.net/docs/nuget-packages-v3 + nuget.org] |
| `Microsoft.NET.Test.Sdk` | 18.10.1 | Test host for consumer test projects | Current stable pair with xunit.v3 4.0.1 [VERIFIED: nuget.org flatcontainer] |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| `AwesomeAssertions` | 9.6.0 | Fluent assertions in kit + internal tests | Per locked stack (Apache-2.0 fork of FluentAssertions) [VERIFIED: nuget.org flatcontainer] |
| `NSubstitute` | 6.2.0 | Fakes for `ILogger`/options in tests | Per locked stack [VERIFIED: nuget.org flatcontainer] |
| `coverlet.collector` | 10.1.0 | Coverage | Per locked stack [VERIFIED: nuget.org flatcontainer] |
| `Microsoft.CodeAnalysis.PublicApiAnalyzers` | 5.6.0 | `PublicAPI.Unshipped.txt` gate on public surface | Package ships to NuGet eventually; cheap to adopt at bootstrap [VERIFIED: nuget.org flatcontainer — 5.6.0 latest stable; 5.11.0-1 is a preview] |
| `Microsoft.Extensions.TimeProvider.Testing` | 10.10.0 | `FakeTimeProvider` for TTL/trim tests (D-08) | Test-only dependency of the kit/internals tests [VERIFIED: nuget.org flatcontainer — 10.10.0 latest (testing package carries its own version line)] |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `Task<T>` return types on store interfaces | `ValueTask<T>` | ValueTask only "when performance measurements indicate allocation pressure and consumers can handle their usage constraints" (official TAP guidance). A host-implemented interface must not impose single-consumption constraints on every implementer. Choose `Task`. [CITED: learn.microsoft.com TAP + CA2012] |
| Five separate interfaces (D-01) | One aggregate `IStore` / whatsmeow `AllSessionSpecificStores` mega-interface | whatsmeow aggregates via a composite interface for convenience, but its *implementation* granularity stays per-concern; D-01 mandates separate interfaces — the composite pattern is exactly what D-01 forbids on the public surface |
| Async factory per store (D-02) | Shared store instance + accountId parameter (whatsmeow's address-param style is *within* an account) | whatsmeow scopes per device via `Container.GetDevice(jid)` returning device-scoped store objects — structurally the same idea as D-02; D-02 is the .NET-idiomatic form |
| xunit inheritance-based kit | Home-grown reflection test runner | EF Core proves inheritance pattern works at ecosystem scale; hand-rolling a runner is a Don't-Hand-Roll item |

**Installation:**
```bash
# Main SDK project (src/Iwasug.WhatsApp.Sdk/)
dotnet add package Microsoft.Extensions.Logging.Abstractions --version 10.0.12
dotnet add package Microsoft.Extensions.Options --version 10.0.12

# Test kit project (src/Iwasug.WhatsApp.Sdk.Testing/)
dotnet add package xunit.v3.extensibility.core --version 4.0.1
dotnet add package Microsoft.Extensions.TimeProvider.Testing --version 10.10.0   # if TTL tests live in the kit

# Internal test project (tests/Iwasug.WhatsApp.Sdk.Tests/)
dotnet add package xunit.v3 --version 4.0.1
dotnet add package Microsoft.NET.Test.Sdk --version 18.10.1
dotnet add package AwesomeAssertions --version 9.6.0
dotnet add package NSubstitute --version 6.2.0
dotnet add package coverlet.collector --version 10.1.0
```

**Version verification:** every version above was fetched from `api.nuget.org/v3-flatcontainer/*/index.json` this session (2026-10-07). `Microsoft.Extensions.Options.Abstractions` was checked and does **not exist** (BlobNotFound 404 on flatcontainer, 404 on registration, absent from search; 8.0.0/9.0.0 nuspec probes also 404).

## Package Legitimacy Audit

> The gsd seam's `package-legitimacy check` supports npm/pypi/crates only; for this NuGet phase each package was verified directly against the first-party NuGet v3 registry API this session (flatcontainer version lists + registration metadata + search endpoint), which is the strongest available verification for this ecosystem.

| Package | Registry | Age | Downloads | Source Repo | Verdict | Disposition |
|---------|----------|-----|-----------|-------------|---------|-------------|
| Microsoft.Extensions.Logging.Abstractions 10.0.12 | NuGet | Microsoft servicing line | ~1.9B (Diagnostics.Abstractions sibling) | github.com/dotnet/runtime | OK | Approved |
| Microsoft.Extensions.Options 10.0.12 | NuGet | Microsoft servicing line | ~1.9B-class | github.com/dotnet/runtime | OK | Approved |
| xunit.v3 4.0.1 | NuGet | 2025+ current gen | 30.5M+ (v3 family) | github.com/xunit/xunit.net | OK | Approved |
| xunit.v3.extensibility.core 4.0.1 | NuGet | same | 8.8M+ | github.com/xunit/xunit.net | OK | Approved |
| Microsoft.NET.Test.Sdk 18.10.1 | NuGet | Microsoft | very high | github.com/microsoft/vstest | OK | Approved |
| AwesomeAssertions 9.6.0 | NuGet | active fork | established (locked in STACK.md) | github.com/AwesomeAssertions/AwesomeAssertions | OK | Approved |
| NSubstitute 6.2.0 | NuGet | 2025 | very high | github.com/nsubstitute/NSubstitute | OK | Approved |
| coverlet.collector 10.1.0 | NuGet | 2025 | very high | github.com/coverlet-coverage/coverlet | OK | Approved |
| Microsoft.CodeAnalysis.PublicApiAnalyzers 5.6.0 | NuGet | Microsoft | high | github.com/dotnet/roslyn-analyzers | OK | Approved |
| Microsoft.Extensions.TimeProvider.Testing 10.10.0 | NuGet | Microsoft | high | github.com/dotnet/runtime | OK | Approved |

**Packages removed due to [SLOP] verdict:** none
**Packages flagged as suspicious [SUS]:** none
**Registry corrections made this session:** `Microsoft.Extensions.Options.Abstractions` (a commonly-cited-but-wrong ID from training memory) — does not exist on nuget.org; use `Microsoft.Extensions.Options`.

## Architecture Patterns

### System Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────────────┐
│ HOST APPLICATION                                                        │
│  implements:  IIdentityStore / ISignalSessionStore / IPreKeyStore /    │
│               IRecentMessageCacheStore / ISafetyStateStore              │
│  (any subset — D-03)                                                    │
└──────┬──────────────────────────────────────────────────────▲───────────┘
       │ registers factory per store                          │ typed domain
       │ (async CreateAsync(accountId) — D-02/D-22)           │ objects (D-04)
       ▼                                                      │
┌─────────────────────────────────────────────────────────────────────────┐
│ SDK STORE SEAM (this phase's deliverable)                               │
│                                                                         │
│  AccountId (opaque value type, host-chosen string — D-25/D-26)          │
│       │                                                                 │
│       ▼                                                                 │
│  ┌───────────────┐   each missing store falls back to ──┐               │
│  │ StoreRegistry │──────────────────────────────────────┤               │
│  └───┬───────┬───┘                                      │               │
│       │       └──────────────────────┐                   │               │
│       ▼                              ▼                   │               │
│  ┌──────────────────┐    ┌──────────────────────────┐    │               │
│  │ Host-provided    │    │ InMemoryStoreFactory     │───┘               │
│  │ store instances  │    │ (reference impl — D-09)  │                   │
│  │ (long-lived,     │    │ ConcurrentDictionary-    │                   │
│  │  D-23/D-24)      │    │  backed, per-account     │                   │
│  └───────┬──────────┘    └──────────┬───────────────┘                   │
│          │                          │                                   │
│          ▼                          ▼                                   │
│  ┌──────────────────────────────────────────────────┐                   │
│  │ 5 × account-scoped store instances               │                   │
│  │ identity │ sessions │ prekeys │ cache │ safety   │                   │
│  └──────────────────────────────────────────────────┘                   │
│                                                                         │
│  Contract rules baked into signatures:                                  │
│   • Task-returning + CancellationToken (await durable writes — D-06)    │
│   • failure ≠ missing: throw on read failure, null on missing (D-19)    │
│   • single-key CRUD only; SDK sequences multi-step changes (D-07)       │
│   • SDK-owned retry wrapper for durable writes (D-18) — Phase 2+ usage  │
└─────────────────────────────────────────────────────────────────────────┘
       │
       ▼
┌─────────────────────────────────────────────────────────────────────────┐
│ TEST KIT PACKAGE (Iwasug.WhatsApp.Sdk.Testing)                          │
│  abstract contract classes: one per store × one per rule family         │
│  (durability-await, failure≠missing, isolation, trimming, concurrency)  │
│  + ComplianceTestBase completeness check                                │
│  ▲                          ▲                                           │
│  │ subclass w/ reference   │ subclass w/ host impl                   │
│  │ factory (internal       │ factory (host test project)              │
│  │ tests project)          │                                           │
└───┴──────────────────────────┴──────────────────────────────────────────┘
```

Primary use case traced: Host registers factory → SDK calls `CreateAsync(accountId)` → account-scoped store instance returned → SDK reads/writes typed domain objects → contract kit (subclass #1 internal tests, subclass #2 host tests) proves identical behavior against both.

### Recommended Project Structure

```
sdk-wa/                               (repo root; solution file here — D-14)
├── Iwasug.WhatsApp.Sdk.sln
├── LICENSE (MIT)                     (+ NOTICE/provenance policy — Pitfall 9)
├── src/
│   ├── Iwasug.WhatsApp.Sdk/          # main package (net10.0)
│   │   ├── Accounts/
│   │   │   └── AccountId.cs          # opaque value type (D-25)
│   │   ├── Stores/
│   │   │   ├── IIdentityStore.cs     # + factory interface (D-01/D-02)
│   │   │   ├── ISignalSessionStore.cs
│   │   │   ├── IPreKeyStore.cs
│   │   │   ├── IRecentMessageCacheStore.cs   # incl. trim primitives (D-08)
│   │   │   └── ISafetyStateStore.cs
│   │   ├── Stores/InMemory/
│   │   │   └── InMemoryStoreFactory.cs   # + 5 account-scoped impls (STR-02)
│   │   ├── Domain/                   # Jid.cs (D-16), identity/device types,
│   │   │                             # SignalSessionRecord, PreKeyRecord,
│   │   │                             # SafetyState (store domain types, D-04/D-15)
│   │   └── Errors/                   # SdkException + subtypes (D-17)
│   └── Iwasug.WhatsApp.Sdk.Testing/  # test-kit package (net10.0) (D-10)
│       ├── StoreContract/            # abstract contract classes (one per store)
│       ├── Rules/                    # durability/failure-vs-missing/isolation/
│       │                             # trimming/concurrency rule classes
│       └── ComplianceTestBase.cs     # completeness check (EF Core pattern)
└── tests/
    └── Iwasug.WhatsApp.Sdk.Tests/    # executable xunit.v3 project
        ├── StoreContract/            # subclasses binding kit to InMemory factory
        └── Core/                     # AccountId/Jid/error unit tests
```

Rationale: D-12 mandates exactly two packages, so stores and reference impl live in the main package (no `Sdk.Store.InMemory` split despite what project-level ARCHITECTURE.md sketched — that pre-dates D-12; D-13/D-12 govern). The kit is the second package. `AGENTS.md` namespace rule: root namespace = package ID.

### Pattern 1: Async factory returning account-scoped instances (D-02/D-22)
**What:** Each store capability has two interfaces: the store itself and its factory. Hosts implement the factory; the SDK calls `CreateAsync(accountId)` once per account (D-24) and owns/disposes the instance (D-23).
**When to use:** Every store. This is the namespacing mechanism — no accountId parameters inside store methods.
**Example:**
```csharp
// Shape guidance (researcher-recommended naming within planner's discretion)
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

### Pattern 2: failure ≠ missing discipline (D-19)
**What:** Read APIs return `null` (or `false` for existence checks) when the key has no value; any backend error throws. Never swallow-and-return-null.
**When to use:** All five stores; the contract kit enforces it by injecting a faulting backend.
**Example:**
```csharp
// Reference behavior — matches whatsmeow sqlstore's explicit ErrNoRows→(nil,nil)
// handling while genuine query errors propagate [CITED: whatsmeow sqlstore/store.go, behavioral]
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

### Pattern 3: Contract kit via abstract base classes (EF Core specification-tests pattern)
**What:** The kit contains abstract classes with real `[Fact]`/`[Theory]` methods that call abstract members the subclass overrides to supply the store-under-test. xunit runs inherited facts in every concrete subclass. A `ComplianceTestBase`-derived check fails when a contract class is not subclassed anywhere (completeness enforcement).
**When to use:** Exactly this phase's D-10/D-11 requirement — same suite against reference and host impls.
**Example:**
```csharp
// Source: EF Core writing-a-provider docs (pattern) + xunit shared-context docs (mechanics)
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

### Pattern 4: In-memory reference store structure
**What:** One `InMemoryStoreFactory` owning `ConcurrentDictionary<(string capability, AccountId), object>` of per-account state; `CreateAsync` returns light account-scoped view objects; `DisposeAsync` on views releases nothing global (factory disposal clears all). Deterministic, lock-per-account for concurrency tests.
**When to use:** Fills any unregistered store (D-03); ships in the main package (D-12); docs state no-restart-survival (D-09).
**Note:** keep the cache store's trim methods driven by options (D-08) — the store implements primitive trim/delete; the SDK owns policy. `FakeTimeProvider` (Microsoft.Extensions.TimeProvider.Testing) makes TTL behavior testable without real delays.

### Anti-Patterns to Avoid
- **One aggregate `IStore` interface (monolithic):** explicitly forbidden (D-01, success criterion 1). Forces hosts to implement no-op methods for concerns they don't need.
- **accountId parameters inside store methods:** breaks D-02 structural namespacing; the instance *is* the namespace. Also re-opens the Pitfall 8 isolation leak class.
- **Byte-blob store APIs (`byte[]` sessions):** forbidden by D-04; hosts must be able to inspect/migrate data. whatsmeow uses blobs; we deliberately deviate.
- **`ValueTask` on host-implemented interfaces:** imposes single-consumption constraints on every implementer for a seam that's I/O-bound anyway (CITED: TAP docs "prefer Task").
- **Default interface members as a versioning plan:** adding members to shipped interfaces is REQUIRES-JUDGMENT territory even with DIMs (ref structs, other languages); FDG says don't add members to shipped interfaces. Ship the full D-11 surface day one. [CITED: learn.microsoft.com library-change-rules + FDG interface page]
- **Kit package referencing `xunit.v3`:** the injected MSBuild steps would break the kit's class-library build — must reference `xunit.v3.extensibility.core`. [CITED: xunit.net/docs/nuget-packages-v3]
- **Transactions in the store contract (D-07):** no transaction API; multi-step sequencing is the SDK's job. A host-side transaction API would be unusable by the reference impl and untestable by the kit generically.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Contract-test dispatch | Custom reflection-based test runner | xunit abstract base classes + inheritance | Framework-native; analyzers validate; EF Core precedent |
| Test completeness enforcement | Manual checklist | `ComplianceTestBase` pattern (fails if a contract class isn't subclassed) | Mechanically catches new contract classes added later |
| Concurrency-safe in-memory collections | Manual `lock` everywhere | `ConcurrentDictionary` + per-account `SemaphoreSlim` where ordering matters | BCL-maintained; fewer heisenbugs in concurrency contract tests |
| Time-dependent TTL/trim tests | `Thread.Sleep` / real clocks | `TimeProvider` + `FakeTimeProvider` | Deterministic; official .NET 8+ abstraction [CITED: learn.microsoft.com TimeProvider overview] |
| Public API surface tracking | Ad-hoc review | `PublicApiAnalyzers` + `PublicAPI.Unshipped.txt` | Compile-time gate on public-surface changes (publishes to NuGet eventually) |
| Versioning the packages | Manual version strings in csproj | `Nerdbank.GitVersioning` (locked in STACK.md) | Git-height SemVer2 from day one avoids version gaps at first publish |

**Key insight:** The entire phase is *interface design* — the expensive part is thinking, not code. Every place where a library could be reused (test dispatch, time, collections, API tracking) is a solved problem; hand-rolling any of them burns the budget that belongs to contract semantics.

## Runtime State Inventory

Not a rename/refactor/migration phase — but the store seam *creates* runtime state by contract, so the forward-looking inventory is recorded for later phases:

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | None yet (greenfield). Contract established: hosts will persist typed domain records keyed by `AccountId` (D-25 — one-way decision; rename later = host data migration) | None this phase |
| Live service config | None — verified: no external services exist in this phase | None |
| OS-registered state | None — verified: pure class library, no registrations | None |
| Secrets/env vars | None — verified: no secrets in the store seam (keys are *typed domain objects*, not yet real key material; real crypto keys arrive with Phase 4) | None |
| Build artifacts | None yet; NuGet package IDs chosen this phase are effectively permanent (D-13) | Choose exactly `Iwasug.WhatsApp.Sdk` / `Iwasug.WhatsApp.Sdk.Testing` |

## Common Pitfalls

### Pitfall 1: Making `CreateAsync` sync "because in-memory doesn't need await"
**What goes wrong:** The factory ships as `Create(...)`; later a host with a real DB needs to open a connection/pool per store and can't — breaking change (D-22 marks the factory signature public API, costly to change).
**Why it happens:** The reference implementation is synchronous internally, so async looks unnecessary.
**How to avoid:** Factory is `CreateAsync` from day one (D-22); reference impl returns completed tasks naturally (`Task.FromResult` / already-completed awaits — zero allocation).
**Warning signs:** Any interface file with `Task` methods but a factory without them.

### Pitfall 2: Conflating "read failure" with "missing" in the reference store *or* the kit
**What goes wrong:** A fault-injection test class is missing from the kit, or the in-memory store silently returns null on internal errors; hosts learn the wrong behavior; a DB blip later reads as "empty session" → wrong re-establishment (the security scenario D-19 names).
**Why it happens:** Fault injection requires an extra seam in the kit (a wrapper factory that fails on demand) that's easy to defer.
**How to avoid:** The kit must ship a fault-injecting decorator factory as part of the kit's public API — contract classes for failure-vs-missing use it; this is a D-11 "full semantic contract" requirement, not a nice-to-have.
**Warning signs:** Kit has no `Faulting*`/`Failing*` wrapper type; no test asserts a throw distinct from null.

### Pitfall 3: Kit package accidentally becoming an executable test project
**What goes wrong:** Referencing `xunit.v3` (or `xunit.v3.core`) in the kit injects MSBuild steps that make the project an executable test project — broken class-library build, weird packaging artifacts.
**Why it happens:** Muscle memory from test projects.
**How to avoid:** Kit references `xunit.v3.extensibility.core` only; consumers (executable test projects) reference `xunit.v3` + `Microsoft.NET.Test.Sdk` themselves. [CITED: xunit.net/docs/nuget-packages-v3]
**Warning signs:** Kit csproj generates an apphost / has `<OutputType>Exe</OutputType>` after adding packages.

### Pitfall 4: Isolation tested only serially
**What goes wrong:** Two accounts tested sequentially pass; parallel interleaving exposes shared mutable state (the classic Pitfall 8 leak, exactly what MUL-02 later runs in CI).
**Why it happens:** Sequential tests are easier; concurrency assertions need explicit design.
**How to avoid:** D-11 mandates concurrency-under-parallel-access contract tests: two factories/accounts driven concurrently against the same store type, asserting zero cross-reads; run test collections in parallel inside the kit.
**Warning signs:** Any `[Collection]` disabling parallelization in the kit without justification.

### Pitfall 5: Cache-trim policy leaking into the store interface
**What goes wrong:** Retention knobs (counts, TTL) end up as parameters hosts must implement policy for — violating D-08 (SDK owns policy; store only executes trim/delete calls).
**Why it happens:** TTL logic feels store-shaped.
**How to avoid:** Cache store interface exposes only primitives (`Put`, `Get`, `Remove`, `TrimOldest(n)`, maybe `PurgeExpired(cutoff)`) with no policy parameters; `IOptions<>`-bound policy lives in SDK code (which arrives with Phase 4's retry/recent-message machinery — Phase 1 defines the primitives).
**Warning signs:** Interface method named like `TrimAccordingToPolicy(...)` or taking TTL config.

### Pitfall 6: Scope creep — building protocol-adjacent types "while we're here"
**What goes wrong:** Node trees, message IDs, receipt enums creep into Phase 1; they're wrong-shaped without Phase 2 research (D-15 explicitly defers wire vocabulary).
**How to avoid:** Core types = JID, AccountId, identity/device types, store domain types, error taxonomy only. When a type's only consumer is the protocol, it waits.
**Warning signs:** `Node`, `MessageId`, `ReceiptType` appearing in `Domain/`.

### Pitfall 7: Treating D-06 (await durable writes) as untestable until Phase 2+
**What goes wrong:** The await-behavior contract gets skipped from the kit because the SDK's protocol loop doesn't exist; hosts implement fire-and-forget stores that pass the suite — D-11 violated.
**How to avoid:** The *store-side* of D-06 is testable now: contract tests assert that write methods only complete when the value is durably stored (i.e., a subsequent independent read observes it — semantics the host implements). The *SDK-side* enforcement (the protocol loop awaiting) is Phase 2+; document the contract on the interfaces with XML docs now.
**Warning signs:** Any kit test asserting only round-trip equality without persistence-ordering semantics.

## Code Examples

### AccountId — opaque value type (D-25/D-26)
```csharp
public readonly partial record struct AccountId : IComparable<AccountId>
{
    public string Value { get; }          // host-chosen; treated as opaque by the SDK
    private AccountId(string value) => Value = value;

    // Discretion area (validated in plan): case-SENSITIVE, non-empty, no control
    // chars/whitespace-only, reasonable length cap; uniqueness enforced by the
    // SDK's account registry, not by the type (D-26).
    public static AccountId Create(string value) { /* validate + construct */ }

    public override string ToString() => Value;
    public int CompareTo(AccountId other) => string.CompareOrdinal(Value, other.Value);
}
```
Recommendation (discretion): `record struct` gives equality/hashing free and prevents `default(AccountId)` misuse via validation-only construction; case-sensitive ordinal comparison — hosts' backends differ in collation, so the SDK must not fold case.

### JID — immutable parsed value type (D-16)
```csharp
public sealed partial record class Jid   // or readonly record struct — planner's call with A4
{
    public string User { get; }          // phone/LID part
    public string Server { get; }        // e.g. s.whatsapp.net (exact server set arrives with Phase 2)
    public string? Agent { get; }        // device/agent component when present
    public int? Device { get; }

    public static Jid Parse(string value);            // throws SdkFormatException
    public static bool TryParse(string value, out Jid? jid);
    public override string ToString();                // always canonical reconstruction
}
```
D-16 locks: construction parses, components exposed, `ToString()` canonical. The *canonicalization rules* (device suffix rendering, `@lid` handling) deepen in Phase 2/4 — Phase 1 needs the parse/round-trip shape and tests for it.

### Error taxonomy (D-17, composition at planner's discretion)
```csharp
public abstract class SdkException : Exception
{
    protected SdkException(string message, Exception? inner = null) : base(message, inner) { }
}

// Phase 1 minimum set (lean; add per-phase as surfaces appear):
public sealed class SdkStoreException : SdkException { }        // D-21: store failures to awaiting caller
public sealed class SdkFormatException : SdkException { }       // Jid.Parse / AccountId validation
public sealed class SdkDuplicateAccountException : SdkException { }  // D-26 uniqueness
```
The D-18 bounded-retry wrapper (SDK-owned, around durable writes) is behavior specified now in XML docs; its *first real caller* is the protocol loop (Phase 2+). Discretion parameters: e.g. 3 attempts, 50–250 ms jittered backoff — recorded as A1.

### Store-contract kit packaging (csproj shape)
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

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| xunit v2 for shared suites | xunit v3 (MTP runner, dynamic skips, `AssemblyFixture`) | v3 GA 2025 | Kit mechanics differ: `xunit.v3.extensibility.core` for libraries, `Assert.Skip*` runtime skips available for capability-conditional tests |
| `Microsoft.EntityFrameworkCore.Specification.Tests` internal-only | Published regular NuGet packages hosts reference | long-standing, still current | Direct precedent validating D-10's published-test-kit model |
| `IHostApplicationLifetime`-style sync factories | Async factories (`IAsyncDisposable`, `ValueTask` returns) | .NET Core 3+ era, standard by 8/9/10 | D-22 aligns with modern .NET library idioms |
| `DateTime.Now` in retention logic | `TimeProvider` abstraction | .NET 8 | D-08 TTL testable via `FakeTimeProvider` |
| Interface "frozen forever" | DIMs (C# 8) enable additive evolution | C# 8 | Available but FDG still says design full surface up front — D-11 full contract is the right call |

**Deprecated/outdated:**
- `Microsoft.Extensions.Options.Abstractions` as a package reference: does not exist on nuget.org (verified this session) — use `Microsoft.Extensions.Options`.
- FluentAssertions for new OSS (v8+ source-available/Xceed): use AwesomeAssertions (already locked in STACK.md).
- xunit.v2-style `ISelfExecutingXunitTestCase` / runner assumptions don't carry to v3 — kit must be built against v3 semantics from the start.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | D-18 retry parameters ~3 attempts, 50–250 ms jittered backoff (discretion delegated; not user-locked) | Code Examples (error taxonomy) | Contract tests assert retry behavior; values tunable later without API break since wrapper is SDK-internal |
| A2 | Kit TTL tests may live in the kit package itself (TimeProvider.Testing as kit dependency) | Standard Stack (Supporting) | If planner keeps TTL tests internal-only, kit dependency list shrinks — cosmetic |
| A3 | `Jid` as `record class` (vs `readonly record struct`) left to planner; parse/round-trip semantics locked by D-16 either way | Code Examples (JID) | Structural choice; behavioral contract unchanged |
| A4 | `AccountId` validation: case-sensitive, ordinal compare, non-empty, length cap ~128, no control chars (discretion delegated) | Code Examples (AccountId) | Hosts may have shorter column limits; documented rule is additive-safe |
| A5 | PublicApiAnalyzers adopted at bootstrap (not strictly required until Phase 9) | Standard Stack | If skipped now, adopting later is trivial |
| A6 | whatsmeow/Baileys behavioral descriptions (granular interfaces; per-account scoping via device-scoped instances; ErrNoRows→(nil,nil) mapping; SignalKeyStore get/set shape) are accurate as read this session from upstream source/docs pages | Architecture Patterns / Summary | Shape informed our *recommendations* only; our contract is defined by D-01..D-26, not by upstream — low blast radius |

## Open Questions (RESOLVED)

1. **Kit scope split: how many contract classes per store?** — **RESOLVED (plan 01-02 Task 3):** one contract class per store, each calling shared rule helpers (per this section's recommendation). Five kit contract classes; `ComplianceTestBase` enforces completeness reflectively.

2. **Does the cache store need enumeration?** — **RESOLVED (plan 01-02 Task 1):** include the minimal read — `GetRecentAsync(chatJid, limit)` returning newest-first limited entries for that chat (per this section's recommendation; shapes reference-impl ordered state now, used by Phase 4 quoting later).

3. **ValueTask vs Task on the factory's `CreateAsync`** — **RESOLVED (plan 01-01 Task 1, repo-wide convention):** `Task` on all store methods, `ValueTask<T>` only on factory `CreateAsync` (single-consumption safe, called once per account per D-24 — not a hot path).

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | everything | ✓ | 10.0.401 (runtime 10.0.12 present) | — |
| git | commits | ✓ | repo active | — |
| NuGet registry access | package restore | ✓ | verified live this session | — |
| Docker / DB | none this phase | n/a | — | Not required — in-memory store only (STR-02) |

**Missing dependencies with no fallback:** none.
**Missing dependencies with fallback:** none.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 4.0.1 + Microsoft.NET.Test.Sdk 18.10.1 (MTP runner) |
| Config file | none needed at bootstrap; csproj-driven (`tests/Iwasug.WhatsApp.Sdk.Tests/`) |
| Quick run command | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` |
| Full suite command | `dotnet test` (solution root) |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| STR-01 | Five granular interfaces + async factory namespacing exist and compile against a host-style implementation | unit/contract | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` | ❌ Wave 0 |
| STR-01 | Per-account isolation: two accounts, same keys, zero cross-reads (parallel) | contract | same | ❌ Wave 0 |
| STR-01 | failure ≠ missing: injected read failure throws `SdkStoreException`; missing key returns null | contract (fault injection) | same | ❌ Wave 0 |
| STR-01 | Durable write awaited: post-write independent read observes value (persistence ordering semantics) | contract | same | ❌ Wave 0 |
| STR-01 | Cache trim primitives: store executes SDK-issued trim/delete; no policy in interface | contract | same | ❌ Wave 0 |
| STR-01 | Factory disposal cascade (D-23): disposing account/session disposes created stores implementing `IAsyncDisposable` | unit | same | ❌ Wave 0 |
| STR-02 | In-memory reference store satisfies ALL five interfaces; suite green with no external DB | contract (reference binding) | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` | ❌ Wave 0 |
| STR-02 | Same kit passes against a second, deliberately-different scratch implementation (proves "zero SDK changes to swap", success criterion 3) | contract (2nd binding) | same | ❌ Wave 0 |
| D-16/D-25 | Jid parse/round-trip; AccountId validation/uniqueness | unit | same | ❌ Wave 0 |

### Sampling Rate
- **Per task commit:** `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests`
- **Per wave merge:** `dotnet test` (full solution) + `dotnet build` with treat-warnings-as-errors
- **Phase gate:** full suite green before `/gsd-verify-work`

### Wave 0 Gaps
- [ ] `tests/Iwasug.WhatsApp.Sdk.Tests/` project (xunit.v3 + Test.Sdk + AwesomeAssertions + NSubstitute + coverlet.collector) — scaffold in first plan
- [ ] Contract kit abstract classes + `ComplianceTestBase` (kit package itself is the deliverable)
- [ ] Fault-injection decorator factory in kit (D-19 tests)
- [ ] No framework install needed (dotnet 10 present)

## Security Domain

### Applicable ASVS Categories (ASVS L1, per config security_asvs_level)

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | No auth surface this phase (identity *types* only, no credential handling) |
| V3 Session Management | no | No sessions established until Phase 2/3; store seam only |
| V4 Access Control | yes (adapted) | Per-account namespacing IS the access boundary — isolation contract tests (D-11) are the control; failure≠missing prevents auth-adjacent misreads (D-19) |
| V5 Input Validation | yes | `AccountId` validation (D-26), `Jid.Parse` strictness (D-16), no raw-string pass-through into storage keys |
| V6 Cryptography | no | No crypto primitives this phase (typed key *placeholders* only; real material arrives Phase 2/4 — NSec locked in STACK.md) |
| V7 Error Handling/Logging | yes | D-21 (exception + ILogger, no sensitive payloads in messages); D-20 log-and-continue path; no key material in logs (PITFALLS security table) |
| V14 Config | no | No config surface beyond IOptions bounds (D-08) |

### Known Threat Patterns for .NET store-seam phase

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Cross-account data bleed via shared cache/static state | Information Disclosure | Structural namespacing (D-02) + parallel isolation contract tests |
| Store blip misread as empty session → wrong re-establishment | Tampering | failure≠missing (D-19) enforced by fault-injection contract tests |
| Unacked durable state acknowledged to protocol | Repudiation | Await-durable-write contract (D-06) documented + store-side ordering tests |
| Key material leaking through exception messages/logs | Information Disclosure | D-21 typed exceptions with message discipline; XML-doc guidance on interface methods (full logging redaction lands with real keys, Phase 2+) |

## Sources

### Primary (HIGH confidence)
- NuGet v3 registry API (`api.nuget.org/v3-flatcontainer/*/index.json`, `registration5-semver1`) — all package versions + dependency metadata + the Options.Abstractions 404 finding, fetched 2026-10-07
- xunit.net official docs — `/docs/nuget-packages-v3` (extensibility.core vs xunit.v3 rule), `/docs/shared-context` (AssemblyFixture), `/docs/getting-started/v3/whats-new` (dynamic skips, `Assert.Skip*`), analyzers rules xUnit1063/xUnit1004/xUnit1054-1055
- EF Core official docs — `entity-framework/core/providers/writing-a-provider.md` (specification-tests consumption pattern, ComplianceTestBase completeness trick)
- Microsoft Learn — TAP docs + CA2012 (Task vs ValueTask), `implementing-disposeasync.md` (IAsyncDisposable pattern), TimeProvider overview, Native AOT `IsAotCompatible`/`VerifyReferenceAotCompatibility`, C# library-change-rules (interface member additions REQUIRES JUDGMENT), FDG interface design page

### Secondary (MEDIUM confidence)
- whatsmeow `store/store.go` + `store/sqlstore/store.go` + pkg.go.dev + readmex architecture page (GitHub-rendered source read this session) — behavioral reference only (MPL-2.0: no code copied, shape observations logged)
- Baileys `use-multi-file-auth-state.ts`, `Types/Auth.ts`, `auth-utils.ts` + baileys.wiki session-management docs (MIT) — SignalKeyStore get/set shape, creds field checklist

### Tertiary (LOW confidence)
- None — every load-bearing claim was verified against a first-party registry or official docs this session; upstream behavioral reads are labeled MEDIUM and tagged A6.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — every version registry-verified this session; one training-memory error (Options.Abstractions) caught and corrected by the verification protocol
- Architecture: HIGH — contract mechanics from official xunit/EF Core docs; upstream store shapes read from source (behavioral only)
- Pitfalls: MEDIUM-HIGH — kit-packaging and DIM pitfalls from official docs; semantic pitfalls derived from locked decisions D-01..D-26

**Research date:** 2026-10-07
**Valid until:** 2026-11-06 (30 days — stable .NET/OSS domain; re-verify only if xunit v3 or MTP ships a breaking release)
