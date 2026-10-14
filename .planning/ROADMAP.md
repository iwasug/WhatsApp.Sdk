# Roadmap: WhatsApp SDK for .NET (sdk-wa)

## Overview

The SDK grows from its persistence seam outward: stable granular store interfaces and core types first, then the wire vertical (transport → Noise handshake → router) with classified reconnects, then pairing that proves the store seam by resuming after a process restart. Signal E2EE messaging lands next with the safety-gate seam designed into the send path, the ban-safety policies fill that gate before any live traffic, and media hangs off the now-gated send path. Multi-account orchestration composes everything last (isolation was constrained from Phase 1), broadcast and chat extras build on the safety layer, and the ship phase packages it all as a drift-monitored NuGet release. App-state sync has no v1 requirement, so no standalone phase exists — any minimal app-state work discovered during planning folds into the phase that requires it.

## Phases

### Phase Numbering:

- Integer phases (1, 2, 3): Planned milestone work
- Decimal phases (2.1, 2.2): Urgent insertions (marked with INSERTED)

- [x] **Phase 1: Foundation & Store Seam** - Core types, granular pluggable store interfaces, in-memory reference store, repo bootstrap (completed 2026-10-08)
- [x] **Phase 2: Wire Protocol & Connection Resilience** - Transport, Noise XX handshake, node codec/router, classified auto-reconnect (completed 2026-10-10)
- [x] **Phase 3: Pairing & Session Durability** - QR + pairing-code login, typed lifecycle events, restart-without-re-pair (completed 2026-10-10)
- [x] **Phase 4: Signal E2EE & Messaging** - Double Ratchet, 1:1 + group text, receipts, retry recovery, typed event surface (completed 2026-10-14)
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

**Plans**: 5/5 plans complete

Plans:
**Wave 1**
- [x] 01-01-PLAN.md — Walking skeleton: repo scaffold + first store seam + first contract class (tracer)

**Wave 2** *(blocked on Wave 1 completion)*
- [x] 01-02-PLAN.md — Full five-store seam: remaining interfaces, reference impls, second scratch binding

**Wave 3** *(blocked on Wave 2 completion)*
- [x] 01-03-PLAN.md — Full semantic contract: fault injection, durability-await, parallel isolation rules
- [x] 01-04-PLAN.md — StoreRegistry composition: fallback precedence, ownership/disposal, D-18 retry, D-21 errors

**Wave 4** *(blocked on Wave 3 completion)*
- [x] 01-05-PLAN.md — Phase close: host-style second run proof, verification gate, validation re-map

**Wave 5**

### Phase 2: Wire Protocol & Connection Resilience

**Goal**: Host has a live connection to WhatsApp that reconnects on its own and classifies why it dropped.
**Mode:** mvp
**Depends on**: Phase 1
**Requirements**: CONN-04, CONN-05
**Success Criteria** (what must be TRUE):
  1. SDK establishes a session (WebSocket + Noise XX handshake + correlated query/reply round-trip) and, after any network drop, reconnects automatically with backoff — no host code involved.
  2. Each disconnect reason produces distinct observable behavior: 401 surfaces as terminal logged-out (never retried), 428/408 retry with jittered backoff, 515 prompts immediate restart, 440 surfaces account-replaced, 411 surfaces protocol-outdated.
  3. Host observes typed per-connection state events (`connecting`/`open`/`closing`/`logged_out`) as the connection moves through its lifecycle.

**Plans**: 8/8 plans executed (verification: gaps found — see 02-VERIFICATION.md) + 3 gap-closure plans

Plans:
- [x] 02-01-PLAN.md — Spike + locked surface + seams, constants, pure policy table (wave 1)
- [x] 02-02-PLAN.md — Tracer slice: loopback header/handshake/query/drop/reconnect (wave 2)
- [x] 02-03-PLAN.md — Noise XX_AESGCM core: vectors, two-party proof, proto (wave 3)
- [x] 02-04-PLAN.md — Codec + framing + full router semantics (wave 3)
- [x] 02-05-PLAN.md — Reconnect lifecycle, WS transport, loopback proof, close gate (wave 4)

Gap closure (blocked on verification gaps — see 02-VERIFICATION.md):
**Wave 1**
- [x] 02-06-PLAN.md — Envelope renumber 2/3/4 + golden-bytes tags 0x12/0x1A/0x22 (gap_closure)
- [x] 02-08-PLAN.md — Decoder hardening: depth limit 64 + capped LIST_16 + nesting/evil tests (gap_closure)

**Wave 2** *(blocked on Wave 1 completion)*
- [x] 02-07-PLAN.md — Framing unification: WaFrame helper, intro-once, headerless B/C, FrameReader-wired pump, server-shaped B fixture + NOTICE provenance row (gap_closure, depends on 02-06)

### Phase 3: Pairing & Session Durability

**Goal**: Host links an account (QR or pairing code) and resumes after a process restart without re-pairing.
**Mode:** mvp
**Depends on**: Phase 2
**Requirements**: CONN-01, CONN-02, CONN-03
**Success Criteria** (what must be TRUE):
  1. Host scans a QR code and observes the typed lifecycle events `code` (auto-rotating before TTL), `success`, `timeout`, `logged_out`, and `client_outdated`.
  2. On a headless server with no camera, host obtains an 8-character pairing code and links the account without scanning anything.
  3. Killing and restarting the process resumes the session from the pluggable store with no re-pairing, and the resumed session stays connected through an injected network drop.

**Plans**: 7/7 complete

**Wave 1**
- [x] 03-01-PLAN.md — Resume spine: credential seam + login ClientPayload + BinaryNode pump + success-gated Open

**Wave 2** *(depends on 03-01)*
- [x] 03-02-PLAN.md — ClientOutdated terminal path: 405 relocation, WaVersionResolver, bounded pairing abort

**Wave 3** *(depends on 03-02)*
- [x] 03-03-PLAN.md — Pairing surface: events, wire-exact QR payload + rotation, coordinator on pump seam, registration bootstrap

**Wave 4** *(depends on 03-03)*
- [x] 03-04-PLAN.md — Pair-success verification: adv.proto, fail-closed verifier, ordered persist→reply→event, logout credential clearing

**Wave 5** *(depends on 03-04)*
- [x] 03-05-PLAN.md — Pairing code: 5B→8C generator, link-code crypto KATs, RequestPairingCodeAsync, companion handshake routing

**Wave 6** *(depends on 03-04, 03-05)*
- [x] 03-06-PLAN.md — Kill/restart resume proof + 428 survival + concurrency backstop + PairingSample host

**Wave 7** *(depends on 03-06)*
- [x] 03-07-PLAN.md — End-of-phase real-device UAT, VALIDATION/Nyquist closeout, NOTICE provenance audit

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

**Plans**: 5/5 plans complete

**Wave 1** *(depends on Phase 3)*
- [x] 04-01-PLAN.md — Signal protocol core: byte-layout spike fixtures, X3DH, Double Ratchet, prekey management (tracer)

**Wave 2** *(depends on 04-01)*
- [x] 04-02-PLAN.md — Wire layer: message.proto, MessageBuilder, DeviceDirectory, recent-message cache record (tracer)

**Wave 3** *(depends on 04-02)*
- [x] 04-03-PLAN.md — 1:1 send/receive loop with SendOutcome correlation (tracer)

**Wave 4** *(depends on 04-03)*
- [x] 04-04-PLAN.md — Receipts, retry exchange, quoting/reply metadata (tracer)

**Wave 5** *(depends on 04-04)*
- [x] 04-05-PLAN.md — Group sender keys, typed event stream, validation closeout (tracer)

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

**Plans**: 3/5 plans executed (TBD → see below)

Plans:
**Wave 1**
- [x] 05-01-PLAN.md — Mandatory gate seam on all three public send paths + SafetyOptions + safety event vocabulary + send-path audit (tracer)

**Wave 2** *(depends on 05-01)*
- [x] 05-02-PLAN.md — Token buckets (minute/hour/day) + jitter + persisted warm-up ramp

**Wave 3** *(depends on 05-02)*
- [x] 05-03-PLAN.md — Cold-outreach budget + additive IRecipientContactStore seam + contract kit

**Wave 4** *(depends on 05-03)*
- [x] 05-04-PLAN.md — Circuit breaker + exponential backoff + active-hours/pacing

**Wave 5** *(depends on 05-04)*
- [ ] 05-05-PLAN.md — Safe defaults hardening, risk ledger, provenance audit, phase close

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
| 1. Foundation & Store Seam | 5/5 | Complete    | 2026-10-08 |
| 2. Wire Protocol & Connection Resilience | 8/8+2 | Complete | |
| 3. Pairing & Session Durability | 7/7 | Complete    | 2026-10-10 |
| 4. Signal E2EE & Messaging | 5/5 | Complete    | 2026-10-14 |
| 5. Ban-Safety Layer | 3/5 | In Progress | - |
| 6. Media Pipeline | 0/TBD | Not started | - |
| 7. Multi-Account Orchestration | 0/TBD | Not started | - |
| 8. Broadcast & Extended Messaging | 0/TBD | Not started | - |
| 9. Ship | 0/TBD | Not started | - |
