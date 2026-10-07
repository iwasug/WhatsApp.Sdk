# Roadmap: WhatsApp SDK for .NET (sdk-wa)

## Overview

The SDK grows from its persistence seam outward: stable granular store interfaces and core types first, then the wire vertical (transport → Noise handshake → router) with classified reconnects, then pairing that proves the store seam by resuming after a process restart. Signal E2EE messaging lands next with the safety-gate seam designed into the send path, the ban-safety policies fill that gate before any live traffic, and media hangs off the now-gated send path. Multi-account orchestration composes everything last (isolation was constrained from Phase 1), broadcast and chat extras build on the safety layer, and the ship phase packages it all as a drift-monitored NuGet release. App-state sync has no v1 requirement, so no standalone phase exists — any minimal app-state work discovered during planning folds into the phase that requires it.

## Phases

**Phase Numbering:**
- Integer phases (1, 2, 3): Planned milestone work
- Decimal phases (2.1, 2.2): Urgent insertions (marked with INSERTED)

- [ ] **Phase 1: Foundation & Store Seam** - Core types, granular pluggable store interfaces, in-memory reference store, repo bootstrap
- [ ] **Phase 2: Wire Protocol & Connection Resilience** - Transport, Noise handshake, node codec/router, classified auto-reconnect
- [ ] **Phase 3: Pairing & Session Durability** - QR + pairing-code login, typed lifecycle events, restart-without-re-pair
- [ ] **Phase 4: Signal E2EE & Messaging** - Double Ratchet, 1:1 + group text, receipts, retry recovery, typed event surface
- [ ] **Phase 5: Ban-Safety Layer** - Mandatory send gate, rate limits, warm-up, cold-outreach budget, circuit breaker, safe-by-default
- [ ] **Phase 6: Media Pipeline** - Send/receive image, document, voice note with expiry and media-retry handling
- [ ] **Phase 7: Multi-Account Orchestration** - SessionManager, hard isolation with CI isolation suite, idiomatic .NET surface
- [ ] **Phase 8: Broadcast & Extended Messaging** - Broadcast fan-out, durable outbox, presence, reactions, edit/delete
- [ ] **Phase 9: Ship** - NuGet package, docs, CI with drift-detecting live-interop job

## Phase Details

### Phase 1: Foundation & Store Seam
**Goal**: Hosts can plug their own persistence into stable, granular store seams from day one.
**Mode:** mvp
**Depends on**: Nothing (first phase)
**Requirements**: STR-01, STR-02
**Success Criteria** (what must be TRUE):
  1. Host can implement its own persistence by plugging granular store interfaces (identity, signal sessions, prekeys, recent-message cache, safety state) — each namespaced per account, no monolithic store.
  2. SDK ships a working in-memory reference store that satisfies every store interface; the test suite runs green against it with no external database dependency.
  3. Swapping the reference store for a host-provided implementation requires zero SDK code changes — the same store-contract test suite passes against both.
**Plans**: 5 plans

Plans:
- [ ] 01-01-PLAN.md — Walking skeleton: repo scaffold + first store seam + first contract class (tracer)
- [ ] 01-02-PLAN.md — Full five-store seam: remaining interfaces, reference impls, second scratch binding
- [ ] 01-03-PLAN.md — Full semantic contract: fault injection, durability-await, parallel isolation rules
- [ ] 01-04-PLAN.md — StoreRegistry composition: fallback precedence, ownership/disposal, D-18 retry, D-21 errors
- [ ] 01-05-PLAN.md — Phase close: host-style second run proof, verification gate, validation re-map

### Phase 2: Wire Protocol & Connection Resilience
**Goal**: Host has a live connection to WhatsApp that reconnects on its own and classifies why it dropped.
**Mode:** mvp
**Depends on**: Phase 1
**Requirements**: CONN-04, CONN-05
**Success Criteria** (what must be TRUE):
  1. SDK establishes a session (WebSocket + Noise XX handshake + correlated query/reply round-trip) and, after any network drop, reconnects automatically with backoff — no host code involved.
  2. Each disconnect reason produces distinct observable behavior: 401 surfaces as terminal logged-out (never retried), 428/408 retry with jittered backoff, 515 prompts immediate restart, 440 surfaces account-replaced, 411 surfaces protocol-outdated.
  3. Host observes typed per-connection state events (`connecting`/`open`/`closing`/`logged_out`) as the connection moves through its lifecycle.
**Plans**: TBD

### Phase 3: Pairing & Session Durability
**Goal**: Host links an account (QR or pairing code) and resumes after a process restart without re-pairing.
**Mode:** mvp
**Depends on**: Phase 2
**Requirements**: CONN-01, CONN-02, CONN-03
**Success Criteria** (what must be TRUE):
  1. Host scans a QR code and observes the typed lifecycle events `code` (auto-rotating before TTL), `success`, `timeout`, `logged_out`, and `client_outdated`.
  2. On a headless server with no camera, host obtains an 8-character pairing code and links the account without scanning anything.
  3. Killing and restarting the process resumes the session from the pluggable store with no re-pairing, and the resumed session stays connected through an injected network drop.
**Plans**: TBD

### Phase 4: Signal E2EE & Messaging
**Goal**: Host sends and receives end-to-end encrypted messages with full ID and receipt correlation.
**Mode:** mvp
**Depends on**: Phase 3
**Requirements**: MSG-01, MSG-02, MSG-03, MSG-04, MSG-05, MSG-06, MSG-07
**Success Criteria** (what must be TRUE):
  1. Host sends and receives E2EE text in 1:1 chats; plaintext never crosses the wire (verified against wire fixtures).
  2. Host sends and receives E2EE text in groups the account belongs to.
  3. Every send returns a stable message ID that correlates send → ack → delivery receipt → read receipt, with documented idempotency and no silent auto-retry.
  4. Host sends and receives delivery/read receipts, marks messages read by per-message key, and replies quoting a specific message via the per-account recent-message cache.
  5. Inbound decryption failures recover via retry receipts with no lost messages, and the host receives typed events (message, receipt, connection, logout) with `CancellationToken` honored end-to-end.
**Plans**: TBD

### Phase 5: Ban-Safety Layer
**Goal**: Every send traverses a mandatory, safe-by-default gate that materially lowers ban risk.
**Mode:** mvp
**Depends on**: Phase 4
**Requirements**: SAF-01, SAF-02, SAF-03, SAF-04, SAF-05, SAF-06, SAF-07
**Success Criteria** (what must be TRUE):
  1. No public API path can send without traversing the safety gate — a send-path audit test proves zero ungated egress.
  2. Per-account token buckets (minute/hour/day) with randomized inter-send jitter throttle traffic; a warm-up ramp caps new accounts with account-age persisted in the store; a separate hard-enforced budget blocks first contact with never-messaged recipients.
  3. Throttle signals trip a circuit breaker with exponential backoff and emit typed safety events (`BudgetExhausted`, `ThrottleEngaged`, `AccountRestricted`).
  4. Human-like pacing runs before sends — typing/recording presence within an active-hours window.
  5. Defaults are safe; exceeding them requires explicit `AllowUnsafeThroughput` opt-in, and docs ship an honest risk ledger stating what the layer does and does not prevent.
**Plans**: TBD

### Phase 6: Media Pipeline
**Goal**: Host exchanges image, document, and voice-note media through the same gated send path.
**Mode:** mvp
**Depends on**: Phase 5
**Requirements**: MED-01, MED-02
**Success Criteria** (what must be TRUE):
  1. Host sends an image, a document, and a voice note; the receiving side downloads and opens all three intact.
  2. Host receives and downloads inbound media; an expired download URL is detected and recovered through the media-retry flow without host intervention.
  3. Media encrypt/decrypt round-trips match golden hash/MAC fixtures in CI, so byte-level media crypto cannot silently regress.
**Plans**: TBD

### Phase 7: Multi-Account Orchestration
**Goal**: Host runs tens of concurrent accounts in one process with hard isolation.
**Mode:** mvp
**Depends on**: Phase 6
**Requirements**: MUL-01, MUL-02, MUL-03
**Success Criteria** (what must be TRUE):
  1. Host manages many accounts via `SessionManager`: add/remove/status/restart, hot add/remove without a process restart, and per-account health visible at a glance.
  2. Accounts are hard-isolated — per-account store namespace, outbound queue, limiter, zero shared mutable state — proven by an isolation suite (two accounts, concurrent operations, zero cross-reads) that runs in CI.
  3. Host consumes the SDK idiomatically: DI registration, `IOptions<T>` configuration, `IHostedService` lifecycle, async-stream events, `CancellationToken` end-to-end, and `ILogger` scopes tagged per account.
  4. One account hitting its rate limit or circuit breaker never stalls another account's sends.
**Plans**: TBD

### Phase 8: Broadcast & Extended Messaging
**Goal**: Host runs safe broadcast fan-outs and richer chat interactions on top of the gated send path.
**Mode:** mvp
**Depends on**: Phase 7
**Requirements**: BCT-01, EXT-01, EXT-02, EXT-03, EXT-04
**Success Criteria** (what must be TRUE):
  1. `SendBroadcastAsync` fans out as individual 1:1 sends through the safety gate and warm-up budget, returns a per-recipient outcome report, and owns no independent send loop.
  2. Enqueued messages survive restarts and throttling end-to-end — enqueue → rate-limit → send → backoff → dead-letter — with queue storage living behind the store abstraction.
  3. Host sends and observes presence (`available`/`unavailable`/`composing`/`recording`/`paused`) and receives peer presence updates.
  4. Host sends, receives, and removes reactions, edits own messages, and deletes messages for everyone, with peers seeing each change.
**Plans**: TBD

### Phase 9: Ship
**Goal**: Publish a consumable, drift-monitored open-source NuGet package.
**Mode:** mvp
**Depends on**: Phase 8
**Requirements**: OPS-01, OPS-02
**Success Criteria** (what must be TRUE):
  1. Package publishes to NuGet with docs — README lead message, risk ledger, store-implementation guide, webhook recipe — and a new host can install it and reach first send following the docs alone.
  2. CI runs unit/golden-fixture tests plus the isolation suite on every change, and a gated live-interop job detects protocol drift by failing loudly on 405.
  3. The release passes a provenance audit: NOTICE/LICENSE complete, no MPL-2.0 contamination, and the public API surface is frozen by an analyzer gate.
**Plans**: TBD

## Progress

**Execution Order:**
Phases execute in numeric order: 1 → 2 → 3 → 4 → 5 → 6 → 7 → 8 → 9

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 1. Foundation & Store Seam | 0/5 | Not started | - |
| 2. Wire Protocol & Connection Resilience | 0/TBD | Not started | - |
| 3. Pairing & Session Durability | 0/TBD | Not started | - |
| 4. Signal E2EE & Messaging | 0/TBD | Not started | - |
| 5. Ban-Safety Layer | 0/TBD | Not started | - |
| 6. Media Pipeline | 0/TBD | Not started | - |
| 7. Multi-Account Orchestration | 0/TBD | Not started | - |
| 8. Broadcast & Extended Messaging | 0/TBD | Not started | - |
| 9. Ship | 0/TBD | Not started | - |
