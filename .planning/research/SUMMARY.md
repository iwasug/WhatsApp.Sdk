# Project Research Summary

**Project:** sdk-wa — WhatsApp Web multi-device client SDK for .NET (NuGet library)
**Domain:** Unofficial protocol reimplementation — E2EE messaging client library, multi-account, ban-safety, open source
**Researched:** 2026-10-07
**Confidence:** MEDIUM-HIGH (Stack HIGH; Architecture findings read from primary source = HIGH; Features/Pitfalls MEDIUM — no official WhatsApp spec exists, community consensus only)

## Executive Summary

This is a greenfield, clean-room reimplementation of the WhatsApp Web multi-device protocol as an embeddable .NET 10 NuGet library. Experts in this domain (whatsmeow in Go, Baileys in TypeScript) all converge on the same shape: a strictly layered pipe — raw WebSocket transport → Noise XX handshake → binary-XML node codec → tag-correlated router → per-session crypto services (Signal double ratchet, media, app-state) → typed event bus — with persistence behind granular pluggable store interfaces that the host owns. No native-protocol .NET implementation exists today; every current .NET option drives a headless browser (200–500 MB/session) or shells out to a Node sidecar. That gap, plus an in-library ban-safety layer that no open-source competitor ships, is the entire product thesis.

The recommended approach: **net10.0-only**, in-box `ClientWebSocket` + `System.IO.Pipelines` for transport, `Google.Protobuf` + `Grpc.Tools` for wire schemas (byte-parity with whatsmeow/Baileys is your debugging lifeline), `NSec.Cryptography` for the X25519/Ed25519 that .NET lacks in-box, and **hand-rolled protocol composition** (Noise XX state machine, Signal X3DH + Double Ratchet) over audited primitives — no dormant crypto library dependencies. Hand-rolling composition is validated: whatsmeow itself does it, cacophony reference vectors make Noise objectively testable, and no mature .NET Signal library exists.

The dominant risks, in order: (1) **protocol drift** — WhatsApp ages out pinned version numbers and silently kills pairing (status 405), so version must resolve at connect time with an upstream-watch feed; (2) **crypto byte-fidelity and ratchet persistence** — a ratchet advanced-but-not-persisted across a disconnect permanently desyncs the session and forces re-pair, and incomplete Noise cert-chain validation opens a MITM that poisons cached identity keys; (3) **ban-safety bolted on late** — the safety layer must be a mandatory gate decorator on the single send chokepoint designed in *before* the first send method exists, or every guarantee is advisory; (4) **MPL-2.0 contamination** from whatsmeow — clean-room policy and a provenance/NOTICE doc must land in repo bootstrap, before the first protocol commit. All four have documented prevention phases below.

## Key Findings

### Recommended Stack
*(full detail in [STACK.md](STACK.md) — all versions verified against first-party NuGet/dotnet release APIs on 2026-10-07)*

.NET 10 LTS (`net10.0`, SDK 10.0.401, runtime 10.0.12, C# 14, EOL 2028-11-14), MIT-licensed, deliberately minimal dependency tree — every dependency verified permissive for OSS publication.

**Core technologies:**
- **In-box `ClientWebSocket`** — WA needs exact header control (`Origin: https://web.whatsapp.com`), custom ping policy, protocol-driven resume; every third-party WS wrapper fights this. HIGH
- **`System.IO.Pipelines` 10.0.12** — zero-copy parsing of length-prefixed binary frames over a long-lived stream. HIGH
- **`Google.Protobuf` 3.36.2 + `Grpc.Tools` 2.84.0** — official protoc = byte-exact parity with reference implementations; do not adopt the 4.0.0-rc runtime. HIGH
- **`NSec.Cryptography` 26.4.0** — X25519/Ed25519 are *not* in-box on net10.0; NSec brings audited libsodium, secure memory, typed keys. HIGH
- **Hand-rolled Noise XX + Signal protocol** over NSec + BCL (`ChaCha20Poly1305`, `HKDF`, AES-CBC all in-box) — no mature library exists for either in .NET; Noise.NET is dormant and its native libsodium collides with NSec's. MEDIUM-HIGH (validate in a Noise spike)
- **`Microsoft.Extensions.Logging.Abstractions` 10.0.12 only** — `ILogger` injection, never a concrete logging framework.
- Supporting: `System.Threading.RateLimiting` (token buckets), `System.Threading.Channels` (bounded queues/backoff), `Polly.Core` 8.8.0 (HTTP only — *never* WS reconnect, which needs protocol-aware resume).

**Hard "do NOT use":** any whatsmeow code/schema (MPL-2.0 file-level copyleft — source `.proto` from Baileys/MIT and keep per-file provenance), `protobuf-net` (loses byte parity), WebSocket wrappers/SignalR/gRPC, FluentAssertions 8.x (source-available), Moq, any DB inside the SDK, hand-rolled crypto *primitives*.

### Expected Features
*(full detail in [FEATURES.md](FEATURES.md))*

**Must have (P1, table stakes):**
- QR **and pairing-code login** (headless servers can't scan QRs) with typed lifecycle events (`code/success/timeout/logged_out/client_outdated`)
- Session persistence + **resume without re-pairing**; reconnect with backoff + **logged-out discrimination** (401 terminal vs 428/408 retry vs 515 restart vs 440 replaced vs 411 outdated)
- Send/receive **text 1:1 + group**, E2EE via Signal; stable message IDs; quoted replies
- Receipts both directions + mark-as-read (per-message keys only — no whole-chat read exists in MD); retry receipts with per-account recent-message cache
- Media send/receive: image, document, voice note (incl. download-expiry + media-retry flow)
- **Pluggable store** (granular interfaces — identity, sessions, prekeys, app-state, *and ban-safety state*) — no DB dependency
- **Multi-account orchestrator** — tens of accounts, one process, hard isolation
- **Ban-safety v1** — token bucket + jitter + warm-up ramp + separate cold-outreach budget + circuit breaker + human-like pacing + safe-by-default (`AllowUnsafeThroughput` opt-in)
- Typed event surface with `CancellationToken` end-to-end; NuGet + docs + CI + tests

**Should have (differentiators):** in-library ban-safety as a *visible API* (the moat — competitors ship none), library-native multi-account supervisor, idiomatic .NET surface (DI/`IHostedService`/`IOptions<T>`/async streams), durable outbox, native-protocol memory footprint (~50 MB vs ~500 MB/session), presence control API, protocol-lifecycle diagnostics.

**Anti-features (Won't do — protect against scope creep):** broadcast *list* sends (**protocol doesn't support it** — ship 1:1 fan-out through the rate limiter instead), interactive buttons/lists (official-API-only + restriction signal), voice/video calls (auto-reject + event only), account registration/OTP bypass, "ban-proof" claims, HTTP webhook server inside the library, `SendBulkAsync`-shaped APIs that teach the ban-triggering pattern.

**Defer (v2+):** history sync (HIGH cost), reactions/edit/delete (P2), quality/reply-rate telemetry (P2, needs receipts stable first), group management, polls, newsletters, communities, status writes.

### Architecture Approach
*(full detail in [ARCHITECTURE.md](ARCHITECTURE.md))* — the reference-client layered pipe with five opinionated boundary rules: transport never interprets content; the Router is the single fan-in point; the SafetyLayer is a **decorator gate in front of `ISendClient`**, not hooks inside send; store interfaces are the pluggability seam and must be defined *first*; one session = one isolated object graph (no statics, no shared mutable state).

**Major components:** `FrameSocket`/`NoiseSocket` (transport) → Node codec + Router (`query()` tag-correlated `TaskCompletionSource` waiters) → per-session services (SignalSessionManager, MediaPipeline, AppStateSync, EventBus) → `SafetyLayer` gate chain → `SessionManager` multi-account facade. Proposed packages: `SdkWa.Core` (types/seams, dependency-free) → `SdkWa.Protocol` → `SdkWa.Client` → `SdkWa.Safety` → `SdkWa` (facade) → `SdkWa.Store.InMemory` (reference impl).

**Key patterns:** layered pipe with upward-only callbacks; tag-correlated `query()` with waiter cleanup on disconnect; per-session single-writer outbound `Channel` (replaces whatsmeow's mutexes) + bounded inbound channel; gate decorator (pause → caps → warm-up → cooldown → jitter → send → health record); granular store interfaces (8–12 small ones, atomic read-modify-write per session address documented).

### Critical Pitfalls
*(11 pitfalls in [PITFALLS.md](PITFALLS.md) — top 5)*

1. **Hard-pinned protocol version → 405, pairing dies silently weeks post-release** — resolve live version from `web.whatsapp.com` at connect (cache ~6h + bundled fallback); clear cache on 405 instead of blind retry; watch upstream issue trackers as drift feed.
2. **Ratchet advanced but not persisted across disconnect → permanent desync → forced re-pair** — persist-after-encrypt with a *cancellation-insensitive* store write (`CancellationToken.None`); `PutSessions` is the most safety-critical call in the SDK. Chaos-test: kill network mid-send.
3. **App-state LTHash mismatch treated as fatal → server unlinks the device (re-pair doesn't fix)** — verification failure must be non-fatal: log, emit `AppStateSyncError`, skip patch, full re-sync. Schedule app-state late, scope v1 read-mostly.
4. **Undifferentiated reconnect logic** — disconnect-reason taxonomy with per-reason policy (401 terminal, 428/408 backoff+jitter, 515 prompt reconnect, 440 surface, 411 drift); generation-tag every pending IQ; error state after 2nd consecutive failure.
5. **Ban-safety bolted on late / bypassable** — enforce at an SDK chokepoint every send must pass (never a helper the host may skip); persist warm-up in the store; hard cold-outreach budget; honest "materially lowers risk, never ban-proof" docs.

Also critical: Noise cert-chain validation must verify signatures *and* validity windows (merge-blocking); dedup ciphertext hashes **before** decrypt (double-decrypt corrupts the one-way ratchet); full-AD-JID keying + no-static-state for multi-account isolation; MPL clean-room policy before first protocol commit.

## Implications for Roadmap

Based on the dependency graphs in ARCHITECTURE.md (build order 1–13) and FEATURES.md, plus pitfall-to-phase mapping:

### Phase 1: Foundation — Core Types, Store Interfaces, Repo Bootstrap
**Rationale:** Cheapest decision first, most expensive last — every later component consumes store interfaces; isolation and safety-state persistence are *constraints on early interfaces*, not retrofits (Pitfall 8). Also lands the MPL clean-room policy + NOTICE/provenance doc + PR checklist *before any protocol code* (Pitfall 9).
**Delivers:** `SdkWa.Core` package (Jid/message types, typed event records, 8–12 granular store interfaces incl. `ISafetyStateStore`, sketched `SessionManager` interface), in-memory reference store, solution/CI/test harness, license-provenance policy files.
**Addresses:** pluggable store (P1), typed event surface (P1), NuGet/CI/tests groundwork, isolation test-suite skeleton.
**Avoids:** Pitfall 8 (isolation as early constraint), Pitfall 9 (contamination), anti-pattern "one big IStore".

### Phase 2: Wire Vertical — Codec + Transport + Router
**Rationale:** First demonstrable milestone: connect, complete Noise handshake, send an IQ, get a reply. Nothing above works without it; ARCHITECTURE says (2)(3)(4) form one vertical with no reason to parallelize anything else first.
**Delivers:** `SdkWa.Protocol` — Node codec + protobuf schemas (provenance-tracked, sourced from MIT/Baileys-derived observations), `FrameSocket`, `NoiseSocket` (hand-rolled XX over NSec), Router with `query()`/waiters, connection state machine with **disconnect-reason taxonomy** and **runtime version resolution**.
**Uses:** ClientWebSocket, System.IO.Pipelines, Google.Protobuf + Grpc.Tools, NSec.
**Avoids:** Pitfall 1 (version drift — resolution built into connect flow), Pitfall 5 (cert validation = merge-blocking acceptance criterion), Pitfall 4 (reconnect taxonomy), waiter-leak pattern.
**Research flag: NEEDS `--research-phase`** — Noise XX WA variant specifics (prologue, 3-byte framing, XEdDSA cert chain) + byte-fixture strategy.

### Phase 3: Pairing & Session Durability
**Rationale:** Depends only on the wire vertical + store → yields milestone 2: link via QR, restart the process, reconnect **without re-pairing** (validates the persistence seam end-to-end).
**Delivers:** `SdkWa.Client` pairing (QR channel with rotation + 8-char pairing-code flow for headless servers), pair-success verification, persisted-at-each-step identity, resume/reconnect with logged-out discrimination, typed lifecycle events.
**Addresses:** QR + pairing-code login, session persistence/resume, reconnect/backoff/logout discrimination (all P1).
**Avoids:** Pitfall 1 warning signs (QR-regen on TTL, live interop test in gated CI), "looks done but isn't" trap (restart-without-re-pair as acceptance criterion).
**Research flag: NEEDS research** — pairing-code flow (`PairPhone`) is single-reference (whatsmeow); verify payload shape against observed wire.

### Phase 4: Signal E2EE + Inbound/Outbound Messaging
**Rationale:** Signal is pure crypto over store interfaces — parallelizable with Phase 3, unit-testable offline — but blocks messaging, the product moment. (8)(9) in the build graph.
**Delivers:** X3DH + Double Ratchet (clean-room), prekey management, sender keys for groups, **dedup-before-decrypt**, inbound pipeline (decrypt → dedup → receipts → publish), outbound path (usync discovery → per-device encrypt → ack), PN/LID identity mapping, recent-message cache, **the `ISendClient` seam with the safety-gate interface already in place**, 1:1 + group text, quoted replies, receipts, retry receipts.
**Addresses:** send/receive 1:1+group, message IDs, receipts, mark-as-read, retry recovery, quoted replies (all P1).
**Avoids:** Pitfall 2 (cancellation-insensitive persist-after-encrypt — designed in, not audited later), Pitfall 6 (MKSKIPPED bounds, out-of-order property tests, retry round-trip), Pitfall 11 (full-JID normalization, both `@s.whatsapp.net` and `@lid` fixtures), anti-pattern 4 (dedup before decrypt).
**Research flag: NEEDS `--research-phase`** — largest unknown: clean-room Signal in .NET with no reference lib; needs spec-derived implementation guidance + whatsmeow behavioral cross-check plan.

### Phase 5: Ban-Safety Layer
**Rationale:** Pitfall 7 says it must be a *prerequisite gate for any live-traffic phase*, and its chokepoint belongs to the send API built in Phase 4 (gate interface already landed). Now fill in policies. ARCHITECTURE position (10) ∥ (11).
**Delivers:** `SdkWa.Safety` — ordered gate chain (pause → caps → warm-up ceiling → cold-outreach cooldown → jitter/typing), persisted warm-up ladder + budgets, circuit breaker + I/O-ratio guard, reconnect throttle, safe-by-default config, safety events (`BudgetExhausted`, `ThrottleEngaged`, `AccountRestricted`).
**Addresses:** ban-safety v1 (P1, the core differentiator), presence pacing hooks.
**Avoids:** Pitfall 7 in full — send-path audit (no ungated egress), warm-up keyed to persisted first-link time, honest risk ledger docs.
**Standard patterns** — decorator + token bucket are well documented; thresholds are consensus heuristics (MEDIUM) and must be tunable defaults, not law.

### Phase 6: Media Pipeline
**Rationale:** Leaf feature hanging off the wire vertical + send path; (11) ∥ safety. PROJECT.md scope: image/document/voice.
**Delivers:** mediaConn IQ + TTL cache, per-type HKDF info strings, AES-CBC+HMAC encrypt/decrypt, chunked upload with token refresh + `file_offset` resume, download + expiry → media-retry receipt, golden hash/MAC fixture tests.
**Addresses:** media send/receive (P1).
**Avoids:** Pitfall 10 (sidecar only needed if video — explicitly out of v1; all four scoped types golden-tested; refresh-once-on-401/403).
**Research flag: research recommended** — media crypto details are MEDIUM across several implementation blogs; golden fixtures are the real safety net.

### Phase 7: Multi-Account Orchestrator
**Rationale:** ARCHITECTURE position (13) **last by construction** — it adds nothing but composition *if* every prior component honors isolation (no statics, per-session tokens/stores). Building it earlier forces rework of anything leaking global state. But the *interface* was sketched in Phase 1.
**Delivers:** `SdkWa` facade — `SessionManager` (create/load/destroy, per-session supervisor, classified disconnect handling, accountId-tagged event fan-out), per-account outbound queues + rate limiters, hot add/remove, health aggregation, thundering-herd-jittered reconnect.
**Addresses:** multi-account orchestrator with isolation (P1), logging scopes, durable outbox (P2 can slot here).
**Avoids:** Pitfall 8 (isolation test suite — two fake accounts, concurrent ops, zero cross-reads — runs in CI), anti-pattern 3 (single Router → typed bus with accountId).
**Research flag: NEEDS research** — MEDIUM confidence: no battle-tested tens-of-accounts-in-one-.NET-process reference exists for this protocol.

### Phase 8: App-State Sync (scoped, read-mostly)
**Rationale:** (12) is independent of send/media and can slip; schedule when there's slack because it carries the LTHash fatal-error risk and malformed patches can log the account out of *all* devices. v1 scope: read-mostly, skip-and-resync error policy.
**Delivers:** patch fetch/apply with LTHash cursors, **non-fatal verification-failure path** + `AppStateSyncError` event, snapshot/full-sync recovery, atomic per-collection hash persistence, ack-before-process.
**Addresses:** what little of P1 touches app state (push name / presence prerequisites); unblocks history sync (P2) later.
**Avoids:** Pitfall 3 — explicitly designated "survivable error" in design docs; inject-bad-patch test as acceptance criterion.
**Research flag: NEEDS `--research-phase`** — LTHash algorithm + recovery flow; known upstream issues #858/#651 are the reference.

### Phase 9: Ship — NuGet, Docs, Diagnostics
**Rationale:** Packaging is trivial; protocol regression testing against a moving target is not. Standing drift-monitoring item lives here permanently.
**Delivers:** NuGet package (Nerdbank.GitVersioning, SourceLink, PublicApiAnalyzers gate), docs (README lead message: "no native-protocol .NET implementation exists"; risk ledger; store-implementation guide; 10-line webhook recipe), gated/nightly live-interop CI job (fails on 405), provenance audit, "looks done but isn't" checklist as release gate.
**Addresses:** NuGet + docs + CI + tests (P1).
**Avoids:** Pitfall 1 (interop job), Pitfall 9 (pre-release provenance audit — cost explodes post-release).

### Phase Ordering Rationale
- **Store interfaces → wire vertical → pairing → Signal → messaging** mirrors both reference implementations' build graphs and FEATURES' dependency map: nothing bypasses the protocol core, and the persistence seam must be proven (restart-without-re-pair) before anything scales on top of it.
- **Safety gate interface lands with the send path (Phase 4), policies in Phase 5 before any live traffic** — retrofitting a gate around a shipped `SendMessage` breaks every caller and voids every ban-safety guarantee (research calls this "the highest-leverage API decision in the project").
- **Multi-account last** (composition-only if isolation was enforced all along), **app-state late** (highest known fatal-error risk, lowest core value), **broadcast fan-out last of all** (consumer of the safety layer, never a peer — P2, and it must have no send loop of its own).
- **Parallelization windows:** Signal ∥ Pairing; Safety ∥ Media; App-state ∥ (Safety+Media); docs/CI ∥ everything.
- **Anti-features are never scheduled** — broadcast lists, buttons, calls, registration stay in the Won't-do table as scope-creep armor.

### Research Flags

**Needs `--research-phase` during planning:**
- **Phase 2 (Wire Vertical):** Noise XX WA variant is the single most byte-sensitive subsystem — needs dedicated research on handshake vectors, cert chain, framing.
- **Phase 4 (Signal + Messaging):** clean-room Double Ratchet in .NET with zero reference libraries; PN/LID dual-identity semantics; needs implementation-ready spec guidance.
- **Phase 7 (Multi-Account):** weakest evidence base (MEDIUM) — no .NET reference at target scale; needs research on supervision/orchestration patterns.
- **Phase 8 (App-State):** LTHash algorithm + survivable-error recovery — niche, sparse, high-consequence.

**Standard patterns (skip research-phase):**
- **Phase 1 (Foundation):** interfaces, events, repo bootstrap — well-trodden .NET OSS setup.
- **Phase 5 (Ban-Safety):** decorator + token bucket documented across three ecosystems; thresholds are tunable config, not research.
- **Phase 9 (Ship):** standard NuGet/CI packaging (live-interop job is execution, not research).

**Conditional:** Phase 3 (pairing-code flow — moderately documented, may need light research), Phase 6 (media crypto — fixture-driven, research optional).

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| Stack | **HIGH** | All versions/licenses verified 2026-10-07 against first-party NuGet registry + dotnet/core release JSON + GitHub API. MEDIUM only on the "X25519 not in-box" absence claim and the hand-rolled Noise design decision (validate in spike). |
| Features | **MEDIUM** | Read off competitor READMEs/docs, cross-checked across sources; no official WhatsApp spec exists so nothing reaches HIGH. Ban-safety numbers are commercial-blog consensus — heuristics, tunable, never guarantees. |
| Architecture | **MEDIUM-HIGH** | Component/layer/flow findings read directly from whatsmeow/Baileys source = effectively HIGH; multi-account manager pattern is MEDIUM (smaller wrappers only, no battle-tested reference at scale); safety-decorator placement HIGH (3 independent ecosystems converged). |
| Pitfalls | **MEDIUM-HIGH** | Cross-checked across multiple upstream issue trackers + primary specs (Signal DR spec, Meta engineering blog, MPL text = HIGH); individual issue-based findings are MEDIUM, single-source ones capped LOW and marked. |

**Overall confidence:** **MEDIUM-HIGH** — strong enough to write a roadmap without further research; the flagged phases need per-phase research during planning.

### Gaps to Address

- **Live protocol wire truth:** everything derives from *other implementations'* behavior, not an official spec. Mitigation: provenance-tracked schemas, gated nightly live-interop CI from Phase 2 onward, upstream issue-tracker watch as drift early-warning.
- **Ban-safety thresholds:** warm-up ladders / <30-msgs/hr ceilings are commercial-blog consensus (MEDIUM), single-source claims excluded. Handle as tunable safe defaults + documented risk ledger, never as guarantees; revisit with real telemetry (P2 quality/reply-rate events).
- **Multi-account at scale:** no reference implementation of tens of native-protocol accounts in one .NET process exists. Handle via Phase 1 isolation test suite + Phase 7 research flag + process-sharding escape hatch (sessions are independent by design, so it's deployment-only).
- **Hand-rolled Noise/Signal effort estimates:** MEDIUM — treat Phase 2/4 sizing as soft; a Noise spike at the start of Phase 2 de-risks the estimate.
- **X25519 absence from .NET 10:** corroborated (docs 404 + absent from release notes) but not authoritatively stated — verify in Phase 2 setup; BouncyCastle fallback documented if a zero-native-dep requirement appears.
- **Baileys.NET 1.9.1** (.NET 10 port, low adoption, unclear provenance): note only — do not use as a foundation or copy from it (possible contamination vector).

## Sources

### Primary (HIGH confidence)
- NuGet v3 registry API + `dotnet/core` `10.0/releases.json` + Microsoft Learn API docs — all stack versions, licenses, .NET 10 LTS/EOL status
- whatsmeow source (tulir/whatsmeow, MPL-2.0, read-only behavioral reference) — client/socket/binary/pair/send/appstate/store
- Baileys source + DeepWiki source-line-cited docs — architecture, event map, noise handler shape
- Signal Double Ratchet specification; USENIX WOOT'25 "Prekey Pogo"; Meta Engineering "How WhatsApp enables multi-device"
- MPL-2.0 license text + Mozilla FAQ — file-level copyleft rules
- GitHub API (Metalnem/noise dormancy, Polly BSD-3 license, whatsmeow go.mod)

### Secondary (MEDIUM confidence)
- whatsmeow / Baileys / whatsapp-web.js issue trackers — pitfalls 1–11 evidence base
- whatsmeow-node comparison docs; wa-hub, WaSphere, Evolution API, WAHA — competitor feature surface + multi-tenant checklists
- Ban-safety consensus (Whapi.Cloud, wasenderapi, OpenWA, WAPI, whatsmeow-antiban, baileys-antiban) — warm-up ladders, gate-chain designs
- .NET NuGet landscape (WhatsApp.Client, WhatsAppWeb.NET, WhatsAppNETAPI) — confirm absence of native protocol impl
- whatsapp-media-crypto / whatsapp-rust — media HKDF/sidecar/token details

### Tertiary (LOW confidence — validate during execution)
- sigalor/whatsapp-web-reveng — structural framing notes, dated
- Single-source "48h-no-reply rolling 30 days" tracking claim — excluded from recommendations
- "No native .NET protocol implementation exists" — search-based absence claim; keep a light watch

---
*Research completed: 2026-10-07*
*Ready for roadmap: yes*
