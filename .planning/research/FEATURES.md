# Feature Research

**Domain:** Unofficial WhatsApp Web multi-device client SDK for .NET — embedded NuGet library for multi-tenant customer service + broadcast messaging
**Researched:** 2026-10-07
**Confidence:** MEDIUM

> **Confidence notes:** Competitor feature surfaces (whatsmeow, Baileys, whatsapp-web.js) are read off official READMEs/docs and cross-checked across multiple sources → MEDIUM (per source-hierarchy seam, cross-checked websearch = MEDIUM; no official WhatsApp spec is public, so nothing here reaches HIGH). Ban-safety numbers are community/vendor consensus, not official → MEDIUM, treat as heuristics. The claim that *no native .NET protocol implementation exists* is an absence-of-evidence finding from search → MEDIUM. A few flagged items are single-source → LOW and marked inline.

---

## Feature Landscape

### Table Stakes (Users Expect These)

Missing these = the product is broken for its stated audience. Users give no credit for having them and full penalty for lacking them.

#### Connection & Session

| Feature | Why Expected | Complexity | Notes / Dependency |
|---------|--------------|------------|--------------------|
| QR pairing (link account) | Nothing works without it; core requirement | MEDIUM | Needs Noise XX handshake + device registration (protocol core anyway). Competitors expose a QR channel with rotating codes and terminal events: `code`, `success`, `timeout`, `error`, `err-client-outdated` (whatsmeow `GetQRChannel`; Baileys `connection.update.qr`). Emit the same event taxonomy — hosts need to distinguish "still waiting" from "timed out" from "logged out". |
| **Pairing-code login** (link without scanning a QR) | The target deployment is a headless server — an operator cannot point a phone camera at a terminal. Baileys and WhatsAppWeb.NET both offer it; absence is conspicuous | MEDIUM | whatsmeow `PairPhone()`: ~160 s validity, generates companion ephemeral key + wrapped linking code, optional push notification to the phone, client display name. Generate the code immediately after connect to maximize the window. |
| Session persistence + **resume without re-pairing** | Re-pairing tens of accounts after every restart/deploy makes multi-account ops unusable | HIGH | Requires the pluggable store (keys, device identity, signal sessions). whatsmeow: `sqlstore`/in-memory; Baileys: `useMultiFileAuthState` + `creds.update` event that **must** be persisted immediately or the session is lost. |
| Auto-reconnect with exponential backoff + **logged-out discrimination** | Websockets drop constantly; with tens of accounts disconnects are a daily event | HIGH | Must distinguish: transient network drop (retry) vs `401`/server-pushed logout (stop, surface event, require re-pair) vs `err-client-outdated` (protocol drift — WhatsApp updated the web client). Baileys exposes `lastDisconnect.error.output.statusCode`; whatsmeow exposes typed disconnect reasons. |
| Connection state events (`connecting` / `open` / `closing` / `logged_out`) | Host apps must show per-account status in their own dashboard | LOW–MEDIUM | Cheap once the state machine exists; it is the backbone of every other event. |

#### Messaging

| Feature | Why Expected | Complexity | Notes / Dependency |
|---------|--------------|------------|--------------------|
| Send + receive **text, 1:1** | The product | HIGH (protocol core) | Signal E2E: prekey upload, session establishment, double ratchet. Everything else is a payload on top of this path. |
| Send + receive **group messages** | Customer-service requests arrive in groups; a SDK that silently ignores `@g.us` JIDs feels broken | MEDIUM | Group *admin* tooling is explicitly deferred (PROJECT.md out-of-scope), but read + send to groups the account already belongs to is table stakes. Needs group Signal sessions. |
| Stable **message IDs** (generate + correlate) | Hosts must correlate send→ack→receipt and deduplicate on retry | LOW–MEDIUM | `GenerateMessageID()` (random, fromMe bit set). Idempotency must be documented: the SDK should never auto-retry a send without the host's outbox semantics, else double-sends. |
| **Delivery + read receipts**, both directions | CS teams need "was it seen"; broadcast needs delivery proof | MEDIUM | Multi-device caveat: you can only mark **individual message keys** read — you cannot mark a whole chat read (Baileys docs). The SDK must therefore track per-message read state for the host. |
| **Mark-as-read** (outgoing) | Inbound replies should clear unread state on the operator's phone | LOW–MEDIUM | Same per-key constraint as above. |
| **Quoted replies** (reply-to a specific message) | A CS agent replying without quoting the customer's message looks robotic and breaks thread context | MEDIUM | Needs `MessageKey` construction + a recent-message cache so the quoted payload can be re-encrypted if the original is not cached. |
| **Retry receipts / decryption-failure recovery** | Without it any decryption hiccup = a permanently lost inbound message — fatal for CS | MEDIUM | whatsmeow handles retry receipts, re-encryption from `GetMessageForRetry`, and a `PreRetryCallback` hook. Depends on a **recent-sent-message cache** (bounded, per account). |
| Typed event/callback surface for inbound messages | CS apps route on events; polling is not viable | MEDIUM | Baileys: `BaileysEventMap` (typed, plus `ev.process` batching). whatsmeow: typed `events.*` structs + `AddEventHandler`. For .NET: strongly typed records over `Channel<T>`/delegates with `CancellationToken` end-to-end — not string-keyed dictionaries. |
| Media **receive** (download + expiry handling) | Customers send screenshots and PDFs constantly | HIGH | Two-step decrypt + HMAC/SHA256 verify. WhatsApp **deletes old media from its servers** — downloads can 403/404; recovery requires a media-retry receipt that asks another linked device to re-upload (`SendMediaRetryReceipt`, error 2 = "media no longer available on phone"). See dependency graph. |
| Media **send**: image, document, voice note (scoped in PROJECT.md) | Minimum viable attachment support | HIGH | Upload handshake → chunked encrypted upload → typed `MediaType` HKDF (`WhatsApp Image Keys`, `... Document Keys`, `... Audio Keys`) → send with `URL`/`directPath`/`mediaKey`/`fileEncSHA256`/`fileSHA256`/`fileLength`. Voice note = `ptt: true` (blue mic). Wrong field casing or hash = silent failure on the receiving side. |

#### Operations / Multi-account

| Feature | Why Expected | Complexity | Notes / Dependency |
|---------|--------------|------------|--------------------|
| **Many concurrent accounts in one process, isolated** | Core Value: "run many accounts at once without getting them banned" | HIGH (architecture, not protocol) | One client instance per number. Isolation means: per-account store namespace, per-account outbound queue, per-account rate limiter, per-account connection supervisor — and **no shared mutable state that lets one account's stall or crash block the others**. wa-hub's explicit lesson: message queue and readiness checks must be decoupled from lifecycle handlers so a slow handler never wedges the connection. |
| **Pluggable session store interface** (no hard DB dependency) | PROJECT.md requirement; multi-tenant hosts own their database (EF Core, Dapper, files) | MEDIUM | whatsmeow ships in-memory + `sqlstore`; Baileys ships auth-state wrappers. Our interface must cover: device/identity keys, signal sessions, prekeys, recent-message cache, **and ban-safety state** (warm-up progress, send budgets) — otherwise warm-up resets on restart. |
| **Per-account rate limiting with jitter** | v1 requirement; without it accounts die and the product fails its Core Value | MEDIUM | Minimum viable: token bucket per account + randomized inter-send delay. See Differentiators for the full safety layer. |
| Logging abstraction (`ILogger`-compatible, per-account child loggers) | Tens of accounts in one process produce indistinguishable noise otherwise | LOW | Idiomatic .NET: `ILoggerFactory` + account-scoped scopes/activities. |
| NuGet package + docs + CI + tests | Distribution baseline for a .NET OSS library | LOW–MEDIUM | Packaging is trivial; protocol regression testing against a moving target is not (see PITFALLS). |

---

### Differentiators (Competitive Advantage)

Where this product wins. Aligned with PROJECT.md Core Value: *reliably send and receive across many accounts without getting banned.*

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| **Ban-safety layer as a shipped product feature** (warm-up ramp + budgets + circuit breaker + human-like patterns + telemetry) | whatsmeow and Baileys ship **zero** ban protection — they leave it entirely to the host, and every ban post-mortem traces to that gap. Hosted engines (Whapi.Cloud "warm-up module + Activity Safety Meter", WaSphere "anti-ban controls") charge for it; we ship it in the library. This is the strongest claim in PROJECT.md and it must be visible in the API, not just a doc page. | MEDIUM–HIGH | Composition of several mechanisms — see breakdown below. |
| └ **Account warm-up schedule** | New numbers are highest-risk in their first ~10 days; shipping an enforced ramp makes the safe path the default path | MEDIUM | Community-observed ramp (MEDIUM confidence, vendor/blog consensus — treat as tunable defaults, not law): days 1–3 ≈ 20–50 sends/day to known contacts; days 4–7 ≈ 50–100; days 8–14 ≈ 100–200; then scale on reply-rate trend. State must persist in the store. |
| └ **Per-account send budgets** (hourly + daily + cold-outreach) | Volume alone isn't the trigger — *who* you're sending to matters. A separate budget for **new/never-messaged chats** is the single most cited mitigation | MEDIUM | Community data: <30 msgs/hour is the observed safe ceiling, >60/hr materially raises risk; new-chat initiation ratio moves the needle more than daily totals. Budgets should be **hard-enforced** (reject or queue), not advisory. |
| └ **Circuit breaker + exponential backoff on throttle signals** | Prevents retry storms from escalating a soft limit into an enforcement action | MEDIUM | Trigger on send rejections/timeouts and on "message failed to deliver to new recipient" patterns. Explicitly *not* an infinite retry loop. |
| └ **Human-like pacing**: jittered delays, typing/recording presence before send, active-hours window | Fixed-interval sends are the cheapest behavioral flag to detect | MEDIUM | Community heuristic: 10–45 s randomized gaps, optional typing simulation, concentrate sends in an ~8 h window. Presence APIs (below) are the mechanism. |
| └ **Quality / reply-rate telemetry** | Reply rate and block/report rate are the leading indicators — not send count. Surfacing them lets the host act before enforcement | HIGH (data plumbing) | Requires correlating outbound sends with receipt events over a rolling window. Whapi.Cloud's "Activity Safety Meter" is the market reference. Emit as typed events (`SafetyScoreUpdated`, `BudgetExhausted`, `ThrottleEngaged`) — the host owns presentation. |
| **Multi-account orchestrator with hard isolation** (supervision, per-account lifecycle, hot add/remove) | Every .NET alternative is single-session; hosted engines do multi-session only as a server product. A *library-native* supervisor (tens+ accounts, one process, isolated failure domains) is the actual moat | MEDIUM–HIGH | Table stakes covers "N sessions work"; the differentiator is the ops ergonomics: one `AccountManager` with add/remove/status/restart APIs, per-account health, and no cross-account coupling. |
| **Idiomatic .NET surface** (DI, `IHostedService`/`BackgroundService`, `IOptions<T>`, `System.Threading.Channels` outbox, `CancellationToken` throughout, async streams) | Existing .NET options are mechanical JS ports or browser wrappers. A genuinely .NET-shaped API is why a .NET developer picks this over shelling out to Baileys | MEDIUM | API design cost, not protocol cost. High leverage: this is what the NuGet audience actually evaluates first. |
| **Native protocol — no browser, no sidecar** | whatsapp-web.js / WhatsApp.Client / WhatsAppWeb.NET need Playwright + Chromium (200–500 MB RAM per session, slow start, breaks on web-client updates); WhatsAppNETAPI needs a Node/Baileys process. Tens of accounts on the browser route is not viable | HIGH (up-front) / LOW (run) | Memory per account drops to ~MBs; deployment is one binary. This is the engineering bet the whole project is making. |
| **Durable per-account outbox with pluggable persistence** | Broadcast + CS both need "the send will eventually happen or will visibly fail" — a raw `SendAsync` that throws on disconnect loses messages | MEDIUM | Host supplies the queue storage through the same store abstraction; SDK supplies enqueue → rate-limit → send → backoff → dead-letter semantics. |
| **Quoted replies + reactions as first-class message builders** | Reactions are how CS acknowledges ("👍") without sending another message — cheap perceived responsiveness, low volume | MEDIUM | `BuildReaction`/`DecryptReaction`/remove-reaction exist in whatsmeow; Baileys `messages.reaction`. Reply = table stakes, reaction = v1.x differentiator. |
| **History sync / offline backfill** | A CS agent answering "my order #123 is late" needs prior conversation context; a cold socket that only sees messages from now on is a poor product | HIGH | Baileys `messaging-history.set` (chunked, progress, `isLatest`), `fetchMessageHistory` (≤50 per query). Duplicates table-stakes message handling but at volume — memory/throughput design matters. |
| **Pairing + protocol-lifecycle diagnostics** (typed logout reasons, `client-outdated` detection, structured connection events) | When WhatsApp ships a protocol change, the host's first question is "is this my code or theirs?" — most competing libs answer with a stack trace | MEDIUM | whatsmeow has explicit QR/`events.ClientOutdated` codes; making them first-class in .NET is cheap and builds trust. |
| **Presence control API** (`available`/`unavailable`/`composing`/`recording`/`paused` + subscribe to peer presence) | Dual purpose: CS typing indicators *and* a ban-safety lever | LOW–MEDIUM | Presence updates expire ~10 s (need refresh for sustained "typing"); a desktop client active suppresses phone push notifications — expose a policy so hosts can send `unavailable` and keep the phone notifying. |

#### Ban-safety layer — feature breakdown (the differentiator that must be in v1)

1. Per-account token-bucket rate limiter (per-minute + per-hour + per-day)
2. Jittered inter-send delay (randomized, not fixed)
3. Warm-up ramp schedule with persisted account-age state
4. Cold-outreach budget (separate cap for never-messaged recipients)
5. Exponential backoff + **circuit breaker** (halt sends for the account, emit event)
6. Human-like pacing hooks (typing/recording presence before send)
7. Telemetry: delivery rate, receipt latency, reply-rate proxy, throttle/engagement events
8. Safe-by-default configuration with an explicit `AllowUnsafeThroughput` opt-in

*(Config values above are consensus heuristics from vendor/community sources — MEDIUM confidence — and must be tunable, documented as "materially lowers risk", never as a guarantee, per PROJECT.md.)*

---

### Anti-Features (Commonly Requested, Often Problematic)

Deliberately NOT building. Includes the alternative to ship instead.

| Feature | Why Requested | Why Problematic | Alternative |
|---------|---------------|-----------------|-------------|
| **Broadcast list sends** (`@broadcast` JIDs) | The word "broadcast" is in the product pitch; users assume WhatsApp broadcast lists work | **The protocol does not support it.** whatsmeow returns `ErrBroadcastListUnsupported` ("not supported on WhatsApp web either"); whatsapp-web.js feature request #1609 was closed `not_planned`. It is not a missing feature, it is an absent capability. Pretending otherwise wastes a phase. | **1:1 fan-out through the rate-limiter** — a `SendBroadcastAsync` that fans out as individual DMs under the account's budget + warm-up schedule. Also safer: DMs are the native path and reply-rate telemetry actually works on them. |
| **Interactive buttons / list menus / product catalogs** | Everyone wants WhatsApp-native quick-reply UI | Restricted to the **official Business API**; via unofficial clients they may not render *and* they are an explicit account-restriction signal (whatsmeow-node FAQ). Competing hosted libs advertise "buttons" via legacy protobuf hacks — a trap. | Plain text with numbered options ("Reply 1 / Reply 2"), or route interactive flows through the official Cloud API when the customer truly needs them. |
| **Voice/video calls** (initiate or accept) | "WhatsApp has calls, SDK should too" | whatsmeow has `call.go` (receive offers, reject) but lists calls as not implemented; real-time media is a multi-week subsystem with **zero** value for CS/broadcast, and it deepens the account's automation footprint. | Receive call offers and **auto-reject with an event** so the host can notify the caller via text. Cheap, useful, no media stack. |
| **Status / Stories writes** | Marketed by some APIs as an engagement channel | whatsmeow marks status sending **experimental** ("may not work for large contact lists"); unreliable and low-value for CS. | Read statuses if trivial; no writes in v1/v2. Defer on evidence. |
| **Auto group creation / mass auto-join / member scraping** | Growth-hack demand | Direct spam signal — a top ban trigger. Also explicitly out of scope (PROJECT.md: heavy group-admin tooling deferred). | Support groups the account is already in. Group management APIs only after core 1:1 is proven. |
| **Account/number registration, OTP or verification bypass** | "Just add a number and go" | ToS-violating at best, likely unlawful; guarantees bans and poisons the project's reputation. | Link existing accounts only (QR/pairing code). Document that operators must bring their own numbers. |
| **"Ban-proof" / "undetectable" claims, or automation-hiding helpers** | Buyers want certainty | Impossible to deliver; PROJECT.md explicitly rules it out. Overclaiming is the reputational landmine of this category. | Ship the layered mitigations and a **documented risk ledger**: what we reduce, what we cannot. Honesty is a differentiator here. |
| **Content-level helpers that reward unsafe patterns** — e.g. `SendBulkAsync(IEnumerable<string> numbers, string identicalBody)` | Convenience for the broadcast use case | The API shape itself teaches the ban-triggering pattern (identical blast, no pacing, no opt-in). The SDK can't control host behavior, but it shouldn't make the dangerous path the easy path. | `SendBroadcastAsync(recipients, contentFactory, options)` that *forces* traversal of the rate limiter + warm-up budget, generates per-recipient content via a factory, and returns a per-recipient outcome report. |
| **HTTP webhook server inside the library** | Hosted engines (Evolution API, WaSphere, WAHA) all ship webhooks, so parity seems expected | PROJECT.md: this is an *embedded library* — "leaves message routing, CRM integration, and webhooks to the host application." Baking in HTTP delivery, retry queues, and HMAC signing drags the package toward being a server and duplicates the host's own infrastructure. | In-process typed events (+ an optional `IEventSink` seam). Document a 10-line ASP.NET webhook-forwarding recipe in the docs instead. |
| **Always-`available` presence by default** | "Look online so customers think we're responsive" | Unnatural sustained presence is a behavioral flag, and an active desktop client **suppresses phone push notifications** for the real user — a footgun for operators using the same number manually. | Default `unavailable`; expose presence as an explicit, rate-limited ban-safety lever. |
| **Silent retry-forever on send failure** | Users hate failed sends | Turns a throttle signal into repeated throttle signals — the fastest route from "limited" to "banned", and it hides real failures from the host. | Bounded retries → backoff → circuit breaker → surface a typed failure event to the host's outbox. |

---

## Feature Dependencies

```
[Noise XX handshake + protobuf framing]                 ← protocol foundation, everything sits on this
        │
        ├──requires──> [QR pairing / pairing-code login]
        │                     └──requires──> [Pluggable store]  (persist identity keys)
        │
        ├──requires──> [Signal sessions: prekeys + double ratchet]
        │                     └──requires──> [Pairing]  (device keys exist first)
        │
        ├──requires──> [Send/receive text 1:1]  ──requires──> [Signal sessions]
        │                     │
        │                     ├──requires──> [Recent-message cache] ──> [Quoted replies]
        │                     │                                 └──> [Retry receipts / decryption recovery]
        │                     ├──requires──> [MessageKey builders] ──> [Reactions] [Edit] [Delete-for-everyone]
        │                     └──requires──> [Group Signal sessions] ──> [Group send/receive] ──> [Group management]
        │
        ├──requires──> [Media upload handshake + HKDF] ──> [Media send]
        ├──requires──> [Media download + verify]       ──> [Media receive] ──> [Media-retry receipt flow]
        │
        ├──requires──> [Receipts (send + receive)]     ──> [Mark-as-read]
        │                     └──requires──> [Per-message read-state tracking]  (no whole-chat read in multi-device)
        │
        ├──requires──> [App-state sync]  ──requires──> [Pluggable store]  (mutations are dangerous: malformed patch = logged out of ALL devices)
        │                     └──requires──> [History sync / backfill]
        │
        └──requires──> [Connection state machine] ──> [Reconnect/backoff/logout discrimination]
                                  │
[Multi-account orchestrator] ──requires──> [Pluggable store] + [Connection state machine] + [Per-account outbound queue]
        │
        └──requires──> [Ban-safety layer] ──requires──> [Send-path interception (outbox)]
                                  │        ├────requires──> [Store: warm-up state + budgets]
                                  │        ├────requires──> [Receipt events]  (quality/reply-rate telemetry)
                                  │        └────requires──> [Presence API]   (human-like pacing)
                                  │
[Broadcast fan-out] ──requires──> [Ban-safety layer] + [Multi-account orchestrator]   ← MUST be last

[Interactive buttons / product catalogs] ──conflicts──> [Ban safety]   (official-API-only; restriction signal)
[Broadcast list sends]  ──impossible──>  not supported by the web protocol
[Voice/video calls]     ──conflicts──>  [Native-protocol simplicity] + [Ban safety]
```

### Dependency Notes

- **Everything requires the protocol core first.** There is no shortcut around Noise handshake + protobuf framing + Signal sessions; every messaging feature is a payload riding that path. Phase order must front-load it.
- **Quoted replies and retry receipts both require the recent-message cache.** Build the cache once — it serves two table-stakes features. The cache must be bounded and **per account** (leaking it across accounts is both a memory bug and a privacy bug).
- **Multi-account orchestrator requires the pluggable store**, not the reverse. The store interface must be designed *before* the orchestrator, and must namespace by account from day one — retrofitting isolation into a shared store is the classic multi-tenant rewrite.
- **Ban-safety layer requires the send path to be interceptable.** If v1 exposes a raw `SendMessage()` that bypasses the limiter, the safety layer becomes advisory and every guarantee is void. Decide the outbox/interception seam *before* writing the first send method — this is the highest-leverage API decision in the project.
- **Telemetry requires receipt events.** The quality/reply-rate signal cannot exist before receipts are wired; sequencing this into v1 means receipts land early.
- **Broadcast fan-out is a consumer of the safety layer, never a peer of it.** It must not have its own send loop.
- **App-state sync is deceptively dangerous.** Malformed `chatModify`/app-state patches can log the account out of *all* devices (documented Baileys warning). Any feature touching app state (pin/mute/archive, history sync) needs validation + careful scope, which raises its effective complexity from MEDIUM to HIGH.
- **Conflict: interactive buttons vs ban safety.** Do not schedule them together; do not schedule them at all.
- **History sync requires app-state sync but not vice versa** — history can be deferred to v1.x without touching the foundation.

---

## MVP Definition

### Launch With (v1)

Minimum to validate Core Value: *link many accounts, message reliably, keep them alive.*

- [ ] **QR + pairing-code login** with typed lifecycle events (`code`/`success`/`timeout`/`logged_out`/`client_outdated`) — nothing else is testable without it
- [ ] **Session persistence + resume without re-pairing** through the pluggable store — multi-account ops dies without it
- [ ] **Reconnect with backoff + logged-out discrimination** — tens of accounts means disconnects are constant
- [ ] **Send/receive text 1:1 and group** end-to-end encrypted — the product
- [ ] **Quoted replies + stable message IDs + dedup guidance** — CS thread context; cheap, high leverage
- [ ] **Delivery/read receipts in + out, mark-as-read** — CS and broadcast both need proof of delivery
- [ ] **Retry receipts / decryption-failure recovery** with per-account recent-message cache — otherwise silent message loss
- [ ] **Media send + receive: image, document, voice note** (PROJECT.md scope) incl. download-expiry handling
- [ ] **Typed event surface** (inbound message, receipt, connection, logout, safety) with `CancellationToken` flow
- [ ] **Multi-account orchestrator** — tens+ concurrent, isolated queues/state/failure domains
- [ ] **Pluggable store interface** covering identity, signal sessions, message cache, *and safety state*
- [ ] **Ban-safety v1**: per-account token bucket + jittered delays + exponential backoff + circuit breaker + warm-up ramp + human-like pacing hooks + safe defaults
- [ ] **NuGet package, docs, CI, tests**

### Add After Validation (v1.x)

- [ ] Reactions, edit, delete-for-everyone — message builders on the existing send path (trigger: first CS users ask for acknowledgement UX)
- [ ] Presence subscribe (peer typing/online) + full presence policy — beyond the pacing hooks (trigger: CS "agent is typing" UX demand)
- [ ] Media: video, stickers, thumbnails/link previews — incremental on the media subsystem (trigger: attachment mix data from real usage)
- [ ] History sync / offline backfill — (trigger: CS users need prior-conversation context; HIGH complexity, don't front-load)
- [ ] Group read-side + basic management (participants, invite link, group info) — (trigger: group use proves out; PROJECT.md defers heavy group tooling)
- [ ] Quality/reply-rate telemetry + safety-score events — (trigger: accounts start hitting real volume; depends on receipt correlation being stable)
- [ ] Polls (`BuildPollCreation`/`BuildPollVote`/`DecryptPollVote`) — (trigger: user demand; MEDIUM complexity, self-contained)
- [ ] Newsletter/Channel read + post — (trigger: broadcast users migrate toward channels)
- [ ] Programmatic call-reject + event — (trigger: operators complain about call interruptions)

### Future Consideration (v2+)

- [ ] Communities (linked groups, community announcements)
- [ ] Disappearing-messages timers, blocklist sync, privacy settings, profile management
- [ ] Status/Stories — experimental upstream, low CS value
- [ ] WhatsApp Business labels — only if a real CS integration demands it
- [ ] Interactive buttons/lists — **stay anti-feature** unless the official API path is chosen deliberately

---

## Feature Prioritization Matrix

| Feature | User Value | Implementation Cost | Priority |
|---------|------------|---------------------|----------|
| Protocol core (Noise + Signal + framing) | HIGH | HIGH | **P1** |
| QR + pairing-code login, session resume | HIGH | MEDIUM | **P1** |
| Reconnect/backoff/logout discrimination | HIGH | MEDIUM | **P1** |
| Text send/receive 1:1 + group | HIGH | HIGH | **P1** |
| Typed event surface | HIGH | MEDIUM | **P1** |
| Pluggable store (incl. safety state) | HIGH | MEDIUM | **P1** |
| Multi-account orchestrator with isolation | HIGH | HIGH | **P1** |
| Ban-safety v1 (limiter, jitter, backoff, breaker, warm-up) | HIGH | MEDIUM | **P1** |
| Receipts + mark-as-read + retry receipts | HIGH | MEDIUM | **P1** |
| Media send/receive (image, document, voice) | HIGH | HIGH | **P1** |
| Quoted replies + message IDs | HIGH | MEDIUM | **P1** |
| NuGet + docs + CI + tests | HIGH | MEDIUM | **P1** |
| Reactions / edit / delete | MEDIUM | LOW–MEDIUM | P2 |
| Outbox with durable retry + dead-letter | HIGH | MEDIUM | **P2** |
| Presence subscribe + full presence policy | MEDIUM | LOW–MEDIUM | P2 |
| History sync / backfill | MEDIUM | HIGH | P2 |
| Quality/reply-rate telemetry | HIGH | HIGH | P2 |
| Group read-side + basic management | MEDIUM | MEDIUM | P2 |
| Media: video, stickers, link previews | MEDIUM | MEDIUM | P2 |
| Polls | LOW–MEDIUM | MEDIUM | P3 |
| Newsletters/Channels | LOW–MEDIUM | MEDIUM–HIGH | P3 |
| Call-reject handling | LOW | LOW | P3 |
| Communities | MEDIUM | HIGH | P3 |
| Disappearing messages / blocklist / privacy / profile | LOW | MEDIUM | P3 |
| Status writes | LOW | MEDIUM | P3 (anti-adjacent) |
| Broadcast **list** sends | — | — | **Won't do** (unsupported) |
| Interactive buttons/lists/catalogs | — | — | **Won't do** (anti-feature) |
| Voice/video call implementation | — | — | **Won't do** (anti-feature) |
| Account registration / OTP bypass | — | — | **Won't do** (anti-feature) |

**Priority key:**
- **P1:** Must have for launch — directly serves PROJECT.md Active requirements
- **P2:** Should have, add when the trigger above appears
- **P3:** Nice to have, future consideration
- **Won't do:** Documented anti-features — protected against scope creep

---

## Competitor Feature Analysis

| Feature | whatsmeow (Go, MPL-2.0) | Baileys (TS, MIT) | whatsapp-web.js (JS) | .NET options today | Our Approach |
|---------|------------------------|-------------------|----------------------|--------------------|--------------|
| **Protocol transport** | Native WebSocket + Noise (direct) | Native WebSocket + Noise (direct) | Puppeteer/Chromium browser automation | All browser-automation (`WhatsApp.Client`, `WhatsAppWeb.NET` via Playwright) or **Baileys sidecar** (`WhatsAppNETAPI`) | **Native protocol in .NET** — the gap in the market |
| **Pairing** | QR channel + `PairPhone` pairing code + passkey events | QR + pairing code | QR only (session persisted to disk) | QR; `WhatsAppWeb.NET` also has pairing code | QR **+** pairing code (server-first) |
| **Multi-account in one process** | Yes by convention (1 `Client`/number + `sqlstore`) — no orchestrator | Yes by convention — no orchestrator | One client per session; multi-tenant is the host's job (see wa-hub) | `WhatsAppNETAPI` claims "one or multiple WA numbers" (sidecar) | **First-class orchestrator**: supervision, isolation, per-account queues + safety |
| **Ban-safety** | None (community guidance only) | None | None | None | **Layered safety in-library** (the differentiator) |
| **Rate limiting** | None built-in | None | None (host config: typing delays only) | None | Per-account token bucket + budgets + breaker, safe by default |
| **Media** | Upload/Download/DownloadAny/Thumbnail + media-retry receipts | `downloadContentFromMessage`, `updateMediaMessage` re-upload | Send/receive common types | Send/receive images, video, docs, stickers | v1: image/document/voice + expiry + retry flow; video/stickers v1.x |
| **Text/group send/receive** | ✅ full | ✅ full | ✅ full | ✅ | ✅ (P1) |
| **Group management** | Full (create, participants, invites, photo, communities) | Full | Partial | Full (per READMEs) | Read + send v1; management deferred (PROJECT.md) |
| **Receipts / mark-read** | ✅ send + receive, retry receipts | ✅ per-message keys only (no whole-chat read) | ✅ | ✅ | ✅ per-message, with explicit read-state tracking |
| **Reactions / replies / edit / delete** | ✅ builders + decryptors | ✅ | ✅ reply/forward/delete; edit limited | ✅ reply/forward/delete/star | Reply P1; reaction/edit/delete P2 |
| **Polls** | ✅ build/vote/decrypt | ✅ | ✅ | ✅ (WaSphere claims) | P3 |
| **Presence / typing** | ✅ send + receive | ✅ send + subscribe | ✅ | ✅ status/presence | P1 for pacing hooks; subscribe P2 |
| **History sync / backfill** | App-state + message history | ✅ `messaging-history.set`, `fetchMessageHistory` | Limited | Limited | P2 (HIGH cost) |
| **Newsletters/Channels** | ✅ create/upload/follow (read-leaning) | ✅ | ❌ | Partial | P3 |
| **Status/Stories** | ✅ experimental | Partial | ❌ | Partial | Won't do for now |
| **Broadcast lists** | ❌ `ErrBroadcastListUnsupported` | ❌ (JID exists, unsupported) | ❌ (issue #1609 closed) | ❌ | ❌ — document; ship 1:1 fan-out instead |
| **Interactive buttons/lists** | ❌ | ❌ | ❌ (official-API-only) | WaSphere advertises "buttons" | ❌ **anti-feature** |
| **Calls** | Receive/reject only, listed as not implemented | Receive/reject (`call` event) | ❌ | ❌ | Reject + event only |
| **Store/persistence** | in-memory + `sqlstore` (PG/MySQL/SQLite) | pluggable auth-state | LocalAuth on disk | session persistence on disk | **Pluggable interface, host owns DB** |
| **Event model** | Typed Go structs + handler registration | Typed emitter + `ev.process` batching | Callbacks | .NET events | Typed records + channels + `CancellationToken` |
| **Webhooks** | ❌ (in-process events) | ❌ (in-process events) | ❌ | ❌ | ❌ in-process only — host forwards (per PROJECT.md) |

**Headline read of the competitive field:**

1. **No native-protocol .NET implementation exists** (MEDIUM confidence — search-based absence claim). Every current .NET option either drives a browser (200–500 MB/session, breaks on web-client updates) or shells out to a Node/Baileys process. That is precisely the gap this project fills, and it should be the lead message in docs/README.
2. **Nobody ships ban-safety in-library.** Hosted SaaS products monetize it (warm-up modules, safety meters, regional proxy isolation); open-source protocol libraries ship none. Making safety a visible, default-on API surface is the most defensible differentiator.
3. **Multi-session is everywhere as a concept, nowhere as a library feature.** Engine products (WAHA, Evolution API, WaSphere) implement it at the HTTP-server layer with per-session keys, proxies, webhooks, and sliding-window limiters — a good architecture checklist for our in-library orchestrator, minus the HTTP parts.
4. **Broadcast-list and interactive-button requests recur across all projects and are consistently refused** — confirming they belong in the anti-feature list rather than the roadmap.

---

## Sources

- **whatsmeow README + pkg.go.dev godoc** (tulir/whatsmeow, go.mau.fi/whatsmeow) — feature list, `pair-code.go`, `qrchan.go`, error surface (`ErrBroadcastListUnsupported`, media errors) — https://github.com/tulir/whatsmeow — *MEDIUM (official library docs, cross-checked across pkg.go.dev mirrors)*
- **Baileys README + BaileysEventMap** (WhiskeySockets/Baileys, baileys.wiki, npm) — event taxonomy, `chatModify` logout warning, per-key mark-read, presence semantics, message-action surface — https://baileys.wiki/concepts/events.md — *MEDIUM*
- **whatsapp-web.js** — issue #1609 (broadcast closed `not_planned`), Puppeteer architecture costs — https://github.com/wwebjs/whatsapp-web.js — *MEDIUM*
- **whatsmeow-node comparison + FAQ** (nicastelo) — whatsmeow/Baileys/whatsapp-web.js/Cloud API table, interactive-buttons-are-official-API-only, capability list (100/126 methods wrapped) — https://github.com/nicastelo/whatsmeow-node/blob/main/docs-site/docs/comparison.md — *MEDIUM*
- **.NET NuGet landscape**: `WhatsApp.Client` (Playwright port of wajs), `WhatsAppWeb.NET` (.NET 10 + Playwright, pairing code, labels), `WhatsAppNETAPI` (Baileys engine sidecar) — nuget.org + GitHub — *MEDIUM*
- **Ban-safety consensus**: Whapi.Cloud "How to Avoid a WhatsApp Ban 2026", wasenderapi guide, Lead Notifi checklist, openwa rate-limit docs, Medium "Why WhatsApp Bans Accounts" — warm-up ramps, <30/hr ceilings, reply-rate-vs-volume, block/report thresholds — *MEDIUM (multiple agreeing but commercial-blog sources; heuristics, not official policy). Single-source claim "WhatsApp tracks 48h-no-reply over rolling 30 days" = **LOW**, excluded from recommendations.*
- **Multi-tenant architecture**: WaSphere feature matrix (per-session proxy, sliding-window limiter, HMAC webhooks, anti-ban controls), wa-hub (queue/lifecycle isolation, LocalAuth hygiene), Evolution API (instances, webhooks, queues), WAHA sessions docs (reachout timelock / message-capping introspection) — *MEDIUM*
- **whatsmeow-node media docs** — upload→send two-step, field-casing failure mode, `downloadAny` — *MEDIUM*

---
*Feature research for: WhatsApp Web multi-device client SDK for .NET (sdk-wa)*
*Researched: 2026-10-07*
