# Phase 1: Foundation & Store Seam - Context

**Gathered:** 2026-10-07
**Status:** Ready for planning

<domain>
## Phase Boundary

This phase delivers the persistence seam and the repo it lives in: stable, granular pluggable store interfaces (identity, signal sessions, prekeys, recent-message cache, safety state — each namespaced per account), an in-memory reference store that satisfies every interface, a store-contract test suite that passes against both reference and host implementations, the lean set of core types those stores need (JID, account/device identity, store domain types, error taxonomy), and a minimal `src/` + `tests/` repo bootstrap.

It does NOT deliver: wire protocol, transport, pairing, encryption, messaging, safety policies, multi-account orchestration, samples, docs site, or CI — those belong to Phases 2–9. Requirements in scope: STR-01, STR-02 only.

</domain>

<decisions>
## Implementation Decisions

### Store wiring & composition
- **D-01:** Host registers persistence as five separate interfaces (identity, signal sessions, prekeys, recent-message cache, safety state) — no aggregate facade, no hybrid bundle. — **Reversibility:** costly — public registration surface; hosts wiring one aggregate later would need re-registration migration
- **D-02:** Per-account namespacing is structural: an async factory (`CreateFor`/`CreateAsync(accountId)`) returns store instances scoped to exactly one account. Not shared-instance-with-accountId-param, not keyed DI. — **Reversibility:** costly — factory shape appears in every host integration
- **D-03:** All five stores are optional; the in-memory reference store fills any gap. A host may register only a subset. — **Reversibility:** costly — behavioral contract hosts rely on; making a store mandatory later breaks partial implementations
- **D-04:** Store interfaces expose typed domain objects (signal session, prekey bundle, identity types), not byte blobs. Hosts may inspect/migrate stored data; the SDK owns serialization format and it is not a public contract. — **Reversibility:** costly — host databases will contain SDK-serialized payloads; a format change later needs host data migration

### Durability contract
- **D-05:** Durability classification: identity, signal sessions, prekeys, safety state = must-not-lose; recent-message cache = lossy by definition (reply/quote degrades gracefully when empty). — **Reversibility:** costly — classification drives every store contract test and host implementation
- **D-06:** The SDK awaits every durable write before continuing protocol work (no fire-and-forget on durable stores) — a crash never loses an acknowledged state change. — **Reversibility:** costly — timing is baked into the protocol loop; changing it later alters crash-recovery guarantees
- **D-07:** Store contract guarantees single-key CRUD only. The SDK sequences multi-step state changes itself (e.g., consume prekey → then save session) — no transaction API required, works against any backend. — **Reversibility:** costly — hosts design their implementations against this guarantee
- **D-08:** The recent-message cache is bounded by SDK policy via `IOptions<T>` (max entries/messages per account, optional TTL); the SDK issues trim/delete calls through the store. Host does not own retention. — **Reversibility:** costly — trim primitives become part of the interface hosts implement

### Reference store & test-kit
- **D-09:** In-memory reference store positioning = prototype-capable and honestly documented: usable for prototyping/low-volume; docs state clearly it does not survive restart. Not test-only, no runtime warnings. — **Reversibility:** reversible — docs/positioning change costs a docs edit
- **D-10:** The store-contract test suite ships as a published, host-runnable test-kit package (EF Core test-kit style), not an internal-only suite. — **Reversibility:** costly — second published package; hosts adopting it create a versioning obligation
- **D-11:** In Phase 1 the suite enforces the FULL semantic contract: durability classification (await behavior), per-account isolation, cache trimming calls, concurrency under parallel access — not just CRUD round-trips. Same suite passes against reference and host implementations (success criterion 3). — **Reversibility:** reversible — adding tests later is cheap; the depth decision sets floor, not ceiling

### Package & namespace shape
- **D-12:** v1 ships exactly two NuGet packages: the main SDK and the test kit. No pre-split of wire/signal/media — split later only with a real reason. — **Reversibility:** costly — package graph is hard to change once published
- **D-13:** Package ID and C# root namespace: `Iwasug.WhatsApp.Sdk`; test kit: `Iwasug.WhatsApp.Sdk.Testing`. GitHub repository will be named `Iwasug.WhatsApp.Sdk`. Root namespace = package ID. — **Reversibility:** one-way — NuGet package IDs are effectively permanent once published; renaming orphans every existing install
- **D-14:** Phase 1 repo bootstrap is minimal: `src/` + `tests/` + root solution file only. Samples, docs skeleton, and CI are explicitly deferred to their roadmap phases (samples/docs/CI → Phase 9). — **Reversibility:** reversible — structure can grow later without breaking anything

### Core types scope
- **D-15:** "Core types" = lean and store-driven: JID/address types, account/device identity, store domain types (signal session, prekey bundle, safety state), minimal error taxonomy. Wire vocabulary (node tree, message IDs, presence enums, receipt types) is deferred until the phase that uses it — Phase 2 research shapes it. — **Reversibility:** reversible — later phases add types freely; the lean scope means little to undo
- **D-16:** JID is an immutable parsed value type: construction parses, exposes `User`/`Server`/`Agent`/`Device` components, `ToString()` always reconstructs the canonical string. Not raw strings, not a class hierarchy. — **Reversibility:** one-way — JID is the most-used type across the entire public API; changing it breaks every call site and every host signature
- **D-17:** Public error model = typed exception hierarchy (`SdkException` base + subtypes); async APIs throw. Not Result types, not a hybrid. Store failures follow the same model (see below). — **Reversibility:** costly — hosts write `catch` blocks against these types; changing the model breaks handler code

### Store failure handling
- **D-18:** Durable store write failure: bounded retry with short backoff, then propagate — protocol work for that account stops; the SDK never acknowledges state that isn't persisted. — **Reversibility:** costly — behavioral contract observable by hosts and assumed by the durability tests
- **D-19:** Strict `failure ≠ missing` on ALL stores: a read failure throws, missing data returns null/None — never conflated. Critical for signal sessions where a DB blip misread as "empty session" would trigger wrong re-establishment. — **Reversibility:** costly — security-sensitive contract; host implementations and tests are built around it
- **D-20:** Recent-message cache write failure = log & continue (`ILogger` warning), no propagate. Retry/propagate applies only to the four durable stores. — **Reversibility:** reversible
- **D-21:** Store failures surface as: exception to the awaiting caller + `ILogger` warning/error from the SDK. No typed store-failure event (yet). — **Reversibility:** costly — exception surface is public API; adding an event later is additive, removing types is breaking

### Store lifecycle & disposal
- **D-22:** The factory is async (`CreateAsync`) — hosts can open connections, initialize, etc. — **Reversibility:** costly — factory signature is public API
- **D-23:** The SDK owns every store instance it creates: it disposes them (`IAsyncDisposable` where available) on account removal and client shutdown. Host never tracks SDK-requested lifecycles. — **Reversibility:** costly — ownership semantics must match host expectations of their own resources
- **D-24:** Store instances are long-lived per account: created once at account add, reused until remove/shutdown. Not per-operation, not per-connection. — **Reversibility:** reversible

### Account key type
- **D-25:** The per-account namespace key is `AccountId`, an opaque value type wrapping a host-chosen string (CRM ID, tenant ID, phone — host's choice). The SDK treats it as opaque; the mapping to WhatsApp identity lives in the identity store, not in the key. — **Reversibility:** one-way — the key type is embedded in every store method signature and in hosts' persisted namespace layout; renaming = host data migration
- **D-26:** `AccountId` is host-provided when adding an account; the SDK validates uniqueness and rejects duplicates. Not SDK-generated GUIDs. — **Reversibility:** costly — additive validation is safe, but switching to SDK-generated IDs later would orphan host-assigned keys

### the agent's Discretion
Left to researcher/planner (user was not asked): concrete retry/backoff parameters and attempt counts for D-18; `AccountId` validation rules (allowed characters, case sensitivity — recommend case-sensitive opaque with a documented uniqueness rule); exact composition of the exception subtype taxonomy; which wire types get deferred from Phase 1's lean set (researcher confirms store seam needs nothing else); solution/project file layout conventions within `src/` + `tests/`; store interface method naming and async patterns (`Task` vs `ValueTask`, cancellation); serialization mechanism for D-04 (implementation detail — hosts never see it).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase definition (scope lock)
- `.planning/ROADMAP.md` § Phase 1 — goal, success criteria (3 criteria), requirements STR-01/STR-02
- `.planning/REQUIREMENTS.md` — STR-01, STR-02 full text + Out of Scope table (enforces: no DB in SDK, no monolithic store)

### Project constraints & decisions
- `.planning/PROJECT.md` — Constraints (pluggable store, ban-safety as v1 req, clean-room/MIT) and Key Decisions table
- `AGENTS.md` — generated project context (project, stack, conventions)

### Prior research (canonical for planner/researcher)
- `.planning/research/SUMMARY.md` — synthesized research summary; start here
- `.planning/research/STACK.md` — locked stack: packages, versions, licenses (Google.Protobuf, NSec, xunit.v3, NSubstitute, AwesomeAssertions, etc.)
- `.planning/research/ARCHITECTURE.md` — proposed architecture and structural patterns
- `.planning/research/PITFALLS.md` — known landmines for this codebase/domain
- `.planning/research/FEATURES.md` — feature inventory backing the roadmap

### Store seam specifics (user-referenced during discussion)
- No external specs or ADRs — requirements fully captured in decisions above. STR-01/STR-02 + D-01..D-26 are the contract.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- None — greenfield repository. Only `.planning/`, `AGENTS.md`, and git history exist. Every file in this phase is new.

### Established Patterns
- None in code yet. Established *decisions* substitute: stack locked in `.planning/research/STACK.md` (net10.0, xunit.v3 + NSubstitute + AwesomeAssertions for tests, NSec/Google.Protobuf for later crypto/wire phases); conventions file is still empty — this phase's structure becomes the pattern others follow.

### Integration Points
- Repo bootstrap itself: root solution + `src/` + `tests/` layout created here; later phases add projects within it. Store interfaces defined here are the integration seam every later phase writes through (Phase 3 proves them with restart-resume; Phase 5 persists safety state through them).

</code_context>

<specifics>
## Specific Ideas

- User chose the package/namespace name personally: `Iwasug.WhatsApp.Sdk` — "nama repo nya sama" (GitHub repo gets the same name).
- Test-kit model explicitly referenced as "EF Core test-kit style" — hosts run conformance proof against their own implementation.
- Reference store must be honest in docs about restart behavior (prototype, not production persistence).

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope.

</deferred>

---

*Phase: 1-Foundation & Store Seam*
*Context gathered: 2026-10-07*
