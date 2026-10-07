# Walking Skeleton — Iwasug.WhatsApp.Sdk (sdk-wa)

**Phase:** 1 — Foundation & Store Seam
**Generated:** 2026-10-07

## Capability Proven End-to-End

> A host-style consumer (the test suite) registers the in-memory store factory, obtains an
> account-scoped store through the async `CreateAsync(AccountId)` factory, writes and reads a
> typed domain record — and the same abstract store-contract test suite passes against both the
> in-memory reference implementation and a second, deliberately-different scratch implementation
> with zero SDK code changes.

## Architectural Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Framework | .NET 10 class library, `net10.0` only, C# 14 defaults | PROJECT.md constraint: .NET 10 LTS (EOL 2028-11-14); single TFM keeps Span-based code simple |
| Data layer | Pluggable five-store seam (`IIdentityStore`, `ISignalSessionStore`, `IPreKeyStore`, `IRecentMessageCacheStore`, `ISafetyStateStore`) + in-memory reference implementation in the main package | D-01 (no monolithic store), D-02 (async factory per account), D-12 (exactly two packages — reference impl lives in main package) |
| Auth | None this phase — no credential or key material exists until Phases 2-4 | D-15: Phase 1 core types are store-driven; typed key placeholders only |
| Deployment target | None (library). Documented local run command: `dotnet test` at repo root | D-14: minimal bootstrap; samples/CI/docs deferred to Phase 9 |
| Test architecture | Published contract-kit package (`Iwasug.WhatsApp.Sdk.Testing`) of abstract xunit.v3 contract classes; consumers subclass to bind implementations | D-10/D-11 (EF Core specification-tests pattern); kit references `xunit.v3.extensibility.core` 4.0.1 only — never `xunit.v3` (Pitfall 3) |
| Directory layout | Root solution `Iwasug.WhatsApp.Sdk.sln` + `src/` (two packable projects) + `tests/` (one executable test project); `Directory.Build.props` + `Directory.Packages.props` centralize TFM/nullable/warnings-as-errors/package pins | D-14; this phase's output becomes the repo convention set |
| Naming | Package IDs and root namespaces: `Iwasug.WhatsApp.Sdk` and `Iwasug.WhatsApp.Sdk.Testing` | D-13 (one-way; user chose the name personally) |
| Account key | `AccountId` — opaque validated `readonly record struct` wrapping a host-chosen string, case-sensitive ordinal | D-25/D-26 (one-way/costly; user-locked) |
| Async convention | `Task`-returning store methods with required `CancellationToken`; `ValueTask<T>` only on factory `CreateAsync` | Planner discretion (RESEARCH Open Question 3): TAP guidance — host-implemented interfaces must not impose single-consumption constraints |

## Stack Touched in Phase 1

- [x] Project scaffold (solution, three projects, central build props, .editorconfig, .gitignore, PublicAPI analyzers)
- [x] Routing — n/a (class library; no UI/routing surface)
- [x] Database — one real read AND one real write (in-memory store round-trip through the factory — no external DB)
- [x] UI — n/a (no UI surface in an SDK)
- [x] Deployment — documented local full-run command: `dotnet test` (solution) proving the whole stack green

## Out of Scope (Deferred to Later Slices)

- Wire protocol: transport, Noise XX handshake, node codec/router (Phase 2)
- Pairing: QR / pairing-code login, lifecycle events (Phase 3)
- Signal E2EE: Double Ratchet, real key material, messaging, receipts (Phase 4)
- Ban-safety policies, safety-gate send path (Phase 5)
- Media pipeline (Phase 6)
- Multi-account `SessionManager` orchestration (Phase 7 — Phase 1's `StoreRegistry` is its foundation, not its replacement)
- Broadcast, outbox, presence, reactions, edit/delete (Phase 8)
- NuGet publishing, samples, docs site, CI, live-interop (Phase 9)
- Any database-backed store implementation (hosts own persistence — PROJECT constraint)
- D-18 retry wrapper's first real caller (protocol loop, Phase 2+) — Phase 1 specifies parameters in XML docs only

## Subsequent Slice Plan

Each later phase adds one vertical slice on top of this skeleton without altering its architectural decisions:

- Phase 2: Wire protocol & connection resilience (transport → Noise → router) writing durable state through this seam
- Phase 3: Pairing & session durability — proves restart-resume through the same store interfaces
- Phase 4: Signal E2EE & messaging — extends domain records additively (ratchet fields, message content)
- Phase 5: Ban-safety layer — persists safety state through `ISafetyStateStore`, SDK-owned trim policy via `IOptions<T>`
- Phase 6: Media pipeline on the gated send path
- Phase 7: Multi-account orchestration (`SessionManager` over `StoreRegistry`, isolation suite in CI)
- Phase 8: Broadcast & extended messaging
- Phase 9: Ship — package the two NuGet artifacts exactly as named here
