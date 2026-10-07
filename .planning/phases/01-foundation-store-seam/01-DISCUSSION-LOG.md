# Phase 1: Foundation & Store Seam - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-10-07
**Phase:** 1-Foundation & Store Seam
**Areas discussed:** Store wiring & composition, Durability contract, Reference store & test-kit, Package & namespace shape, Core types scope, Store failure handling, Store lifecycle & disposal, Account key type

---

## Store wiring & composition

| Option | Description | Selected |
|--------|-------------|----------|
| Five separate interfaces | Each store its own DI registration; granular, partial implementation possible | ✓ |
| One aggregate facade | Single interface bundling all five | |
| Hybrid: five + optional bundle | Granular interfaces plus a convenience bundle | |

**User's choice:** Five separate interfaces

---

| Option | Description | Selected |
|--------|-------------|----------|
| Factory → per-account instances | `CreateFor(accountId)` returns account-scoped stores; structural isolation | ✓ |
| Shared instance + accountId param | One store, every method takes account ID | |
| Keyed DI services | SDK resolves stores from keyed DI scopes | |

**User's choice:** Factory → per-account instances

---

| Option | Description | Selected |
|--------|-------------|----------|
| All optional, in-memory default | Host registers any subset; reference store fills gaps | ✓ |
| All five required | Host implements all or nothing | |
| Critical required, rest default | Durable stores required, cache-tier defaults | |

**User's choice:** All optional, in-memory default

---

| Option | Description | Selected |
|--------|-------------|----------|
| Typed domain objects | Protocol-aware types in signatures; SDK owns serialization | ✓ |
| Opaque byte blobs | Hosts store format-agnostic blobs | |
| Typed, serialization hidden | Typed surface, wire format strictly internal | |

**User's choice:** Typed domain objects

---

## Durability contract

| Option | Description | Selected |
|--------|-------------|----------|
| 4 durable, recent-msg cache lossy | Identity/sessions/prekeys/safety-state must-not-lose; cache may lose data | ✓ |
| All five durable | Uniform persistence requirement | |
| Host decides, SDK assumes nothing | SDK classifies nothing | |

**User's choice:** 4 durable, recent-msg cache lossy

---

| Option | Description | Selected |
|--------|-------------|----------|
| Await before proceeding | SDK awaits durable writes; crash never loses acknowledged state | ✓ |
| Fire-and-forget with periodic flush | Best-effort writes, re-sync on crash | |
| Split: critical awaited, rest async | Await durable, fire-and-forget cache-tier | |

**User's choice:** Await before proceeding

---

| Option | Description | Selected |
|--------|-------------|----------|
| Single-key only, SDK sequences | Contract = per-key CRUD; SDK orders multi-step sequences | ✓ |
| Atomic batch required per store | Hosts provide transactional batches | |
| Best-effort, protocol retries fix it | No ordering guarantee | |

**User's choice:** Single-key only, SDK sequences

---

| Option | Description | Selected |
|--------|-------------|----------|
| SDK policy via config | IOptions limits; SDK issues trim calls through the store | ✓ |
| Host owns retention | Store exposes primitives, host decides | |
| No eviction in v1 | Unbounded until host evicts | |

**User's choice:** SDK policy via config

---

## Reference store & test-kit

| Option | Description | Selected |
|--------|-------------|----------|
| Prototype-capable, honestly documented | Usable for prototyping/low-volume; docs state no restart durability | ✓ |
| Test-only | Documented and intended as test-only | |
| Prototype + runtime warning | Warn when default store active | |

**User's choice:** Prototype-capable, honestly documented

---

| Option | Description | Selected |
|--------|-------------|----------|
| Published test-kit package | Hosts run contract suite against their own store (EF test-kit style) | ✓ |
| Internal only + written guide | Suite stays in SDK test project; prose spec in docs | |
| Abstract base test classes | Hosts subclass with their own runner | |

**User's choice:** Published test-kit package

---

| Option | Description | Selected |
|--------|-------------|----------|
| Full semantic contract | Durability, isolation, trimming, concurrency — not just CRUD | ✓ |
| Interface conformance only | Methods exist and round-trip data | |
| Conformance now, semantics later | Depth added as later phases need it | |

**User's choice:** Full semantic contract

---

## Package & namespace shape

| Option | Description | Selected |
|--------|-------------|----------|
| Main SDK + test-kit only | Two packages at v1; split later with real reason | ✓ |
| Pre-split everything now | core/wire/signal/media packages from day one | |
| Single package, fold kit in | Test kit inside main package | |

**User's choice:** Main SDK + test-kit only

---

| Option | Description | Selected |
|--------|-------------|----------|
| Sdk.Wa (matches repo) | `Sdk.Wa` + `Sdk.Wa.Testing` | |
| Whatsapp.Sdk | `Whatsapp.Sdk` + `.Testing` | |
| Other (type it) | Freeform name | ✓ |

**User's choice:** `Iwasug.WhatsApp.Sdk` (free text) — test kit `Iwasug.WhatsApp.Sdk.Testing`, GitHub repo same name
**Notes:** User asked "Iwasug.WhatsApp.Sdk gitu bagus ga? terus nama repo nya sama" — confirmed good (unique NuGet prefix, namespace = package ID convention), repo renamed to match when pushed.

---

| Option | Description | Selected |
|--------|-------------|----------|
| Minimal: src + tests | src/ + tests/ + solution file; samples/docs/CI to their phases | ✓ |
| src + tests + samples skeleton | Placeholder samples project from day one | |
| Full: termasuk CI dasar | Basic CI workflow in Phase 1 instead of Phase 9 | |

**User's choice:** Minimal: src + tests

---

## Core types scope

| Option | Description | Selected |
|--------|-------------|----------|
| Lean: store-driven saja | JID, account/device identity, store domain types, minimal errors; wire types deferred | ✓ |
| Comprehensive: full wire vocabulary | All protocol types now | |
| Minimal saja | Only what compiler needs | |

**User's choice:** Lean: store-driven only

---

| Option | Description | Selected |
|--------|-------------|----------|
| Immutable value type, parsed | Parse at construction; components exposed; ToString reconstructs | ✓ |
| Raw string + helpers | Strings everywhere, extension helpers | |
| Class hierarchy per variant | Jid base with UserJid/GroupJid subclasses | |

**User's choice:** Immutable value type, parsed

---

| Option | Description | Selected |
|--------|-------------|----------|
| Typed exception hierarchy | SdkException base + subtypes; async APIs throw | ✓ |
| Result types, exceptions rare | Result/Error objects for expected failures | |
| Hybrid: exceptions + result | Exceptions for single ops, Result for batches | |

**User's choice:** Typed exception hierarchy

---

## Store failure handling

| Option | Description | Selected |
|--------|-------------|----------|
| Bounded retry, lalu propagate | Short-backoff retries then exception; protocol halts for account | ✓ |
| Fail fast langsung | Immediate propagation, no retry | |
| Log & continue | Swallow and proceed (breaks durability promise) | |

**User's choice:** Bounded retry, then propagate

---

| Option | Description | Selected |
|--------|-------------|----------|
| Strict: failure ≠ missing | Throw vs null always distinguished, all stores | ✓ |
| Failure = missing | Read failure treated as absent data | |
| Strict durable, lenient cache | Strict for four durable stores, lenient for cache | |

**User's choice:** Strict: failure ≠ missing (all stores)

---

| Option | Description | Selected |
|--------|-------------|----------|
| Log & continue | Cache write failure → ILogger warning, protocol proceeds | ✓ |
| Sama dengan durable | Retry + propagate like durable stores | |
| Retry singkat, lalu log | Brief retry, then log without propagating | |

**User's choice:** Log & continue

---

| Option | Description | Selected |
|--------|-------------|----------|
| Exception + ILogger | Exception to awaiting caller + SDK logs | ✓ |
| Exception + logger + typed event | Also a typed store-failure event | |
| Exception saja | No logging channel | |

**User's choice:** Exception + ILogger

---

## Store lifecycle & disposal

| Option | Description | Selected |
|--------|-------------|----------|
| Async factory | `CreateAsync(accountId)` | ✓ |
| Sync factory | Blocking factory | |
| Sync create + init hook | Factory + separate InitializeAsync | |

**User's choice:** Async factory

---

| Option | Description | Selected |
|--------|-------------|----------|
| SDK dispose | SDK owns instances; disposes on remove/shutdown | ✓ |
| Host dispose | Host tracks and disposes | |
| Dispose hanya default store | Only SDK-created defaults disposed | |

**User's choice:** SDK dispose

---

| Option | Description | Selected |
|--------|-------------|----------|
| Long-lived per account | Created once at add, lives until remove/shutdown | ✓ |
| Per-operation | New instance per access | |
| Per-connection | Recreated on reconnect | |

**User's choice:** Long-lived per account

---

## Account key type

| Option | Description | Selected |
|--------|-------------|----------|
| AccountId value type | Opaque host-chosen string, SDK treats as namespace key | ✓ |
| Phone number string | Normalized phone as key | |
| Owner JID as key | Protocol-native owner JID | |

**User's choice:** AccountId value type

---

| Option | Description | Selected |
|--------|-------------|----------|
| Host-provided, SDK validates | Host picks ID at add; SDK rejects duplicates | ✓ |
| SDK-generated GUID | SDK assigns, host maps | |
| Either, host optional | Both paths supported | |

**User's choice:** Host-provided, SDK validates

---

## the agent's Discretion

No explicit "you decide" answers. Left to researcher/planner by default: retry/backoff parameters, AccountId validation rules, exception subtype taxonomy, deferred wire-type list, solution layout conventions, Task/ValueTask and cancellation patterns, serialization mechanism.

## Deferred Ideas

None — discussion stayed within phase scope.
