# Architecture Research

**Domain:** WhatsApp Web multi-device client SDK (protocol client library, .NET)
**Researched:** 2026-10-07
**Confidence:** MEDIUM (cross-checked against primary source code of whatsmeow, Baileys, whatsapp-web.js; component-level findings read directly from upstream repos are effectively HIGH — see [Confidence Notes](#confidence-notes))

## Standard Architecture

### System Overview

Every mature WhatsApp Web MD protocol client (whatsmeow, Baileys) converges on the same
layered structure. Layers are strictly downward-only: upper layers call lower ones, never
the reverse (lower layers emit raw data upward through handlers/callbacks).

```
┌──────────────────────────────────────────────────────────────────────────┐
│  Host Application (.NET app — message routing, CRM, webhooks)            │
│   subscribes: events / callbacks          invokes: SendMessage, Pair...   │
└───────────────────────────────▲──────────────────────────┬───────────────┘
                                │ typed events              │ API calls
┌───────────────────────────────┴──────────────────────────▼───────────────┐
│  MULTI-ACCOUNT LAYER                                                     │
│  ┌─────────────────────┐  ┌──────────────────────────────────────────┐   │
│  │ SessionManager      │  │ SafetyLayer (per-account): rate limiter, │   │
│  │ Map<accountId,      │  │ warm-up curve, jitter, circuit breaker,  │   │
│  │ SessionHandle>      │  │ I/O-ratio guard, reconnect throttle      │   │
│  └──────────┬──────────┘  └───────────────▲──────────────────────────┘   │
│             │ owns 1..n                   │ gates every outbound send    │
├─────────────┴─────────────────────────────┴──────────────────────────────┤
│  SESSION LAYER (one instance per linked account — fully isolated)        │
│  ┌────────────────┐ ┌─────────────────┐ ┌────────────────────────────┐   │
│  │ EventBus       │ │ Pairing (QR /   │ │ Protocol client (state     │   │
│  │ (dispatcher,   │ │ pairing-code,   │ │ machine: node handlers,    │   │
│  │  backpressure) │ │ device link)    │ │ response waiters, retries) │   │
│  └───────▲────────┘ └────────▲────────┘ └─────────────▲──────────────┘   │
│          │                   │                        │                  │
│  ┌───────┴───────────────────┴────────────────────────┴──────────────┐   │
│  │ SERVICE LAYER per session:                                        │   │
│  │  SignalSessionManager (per-device ratchet, sender keys, prekeys)  │   │
│  │  MediaPipeline (mediaConn cache, encrypt/upload/download/decrypt) │   │
│  │  AppStateSync (patch fetch/apply, LTHash cursors)                 │   │
│  │  DeviceDiscovery (usync) / Receipts / HistorySync                 │   │
│  └──────────────────────────────┬────────────────────────────────────┘   │
├─────────────────────────────────┴────────────────────────────────────────┤
│  PROTOCOL LAYER                                                          │
│  ┌──────────────────────┐   ┌────────────────────────────────────────┐   │
│  │ Node codec            │   │ Router: tag → handler dispatch,       │   │
│  │ (binary XML encode/  │   │ query() = send + await response tag   │   │
│  │  decode, tokens)      │   │                                        │   │
│  │ + protobuf payloads   │   └────────────────────────────────────────┘   │
│  └──────────▲───────────┘                                               │
│  ┌──────────┴────────────────────────────────────────────────────────┐   │
│  │ TRANSPORT LAYER: FrameSocket (raw WS frames)                      │   │
│  │   └─ NoiseSocket (Noise_XX_25519_AESGCM_SHA256 handshake,        │   │
│  │       per-frame AEAD seal/open, read/write counters)              │   │
│  └──────────────────────────────▲─────────────────────────────────────┤
├─────────────────────────────────┴────────────────────────────────────────┤
│  PERSISTENCE (pluggable — host implements; no DB dependency in SDK)      │
│  ┌────────────┐ ┌────────────┐ ┌───────────┐ ┌───────────┐ ┌──────────┐ │
│  │ DeviceStore│ │Identity/   │ │ PreKey /  │ │ AppState  │ │ Safety   │ │
│  │ (keys, JID)│ │ Session    │ │ SenderKey │ │ versions  │ │ state    │ │
│  └────────────┘ └────────────┘ └───────────┘ └───────────┘ └──────────┘ │
└──────────────────────────────────────────────────────────────────────────┘
```

### Component Responsibilities

| Component | Responsibility | Talks to | Typical Implementation (per reference ecosystem) |
|-----------|----------------|----------|--------------------------------------------------|
| **FrameSocket** | Raw WebSocket lifecycle; read/write length-prefixed frames; keepalive ping | NoiseSocket | whatsmeow `socket.FrameSocket`; Baileys `WebSocketClient`. .NET: `ClientWebSocket` + `System.IO.Pipelines` or `Channel<byte[]>` |
| **NoiseSocket** | Noise XX handshake; per-frame AEAD encrypt/decrypt with monotonic counters (separate read/write keys) | FrameSocket (below), Router (above) | whatsmeow `socket/noisesocket.go`, `handshake.go`; Baileys `makeNoiseHandler`. Emits **plaintext frames upward** — it never interprets content |
| **Node codec** | Serialize/deserialize binary XML nodes (tag/attrs/content) with WhatsApp token dictionaries, packed nibble/hex strings, JID encodings | Router, all services | whatsmeow `binary/encoder.go|decoder.go`; Baileys `BinaryNode` encode/decode |
| **Router / query engine** | Inbound dispatch by node tag (`message`, `receipt`, `iq`, `notification`, `stream:error`…) and request/response correlation (generate tag → `TaskCompletionSource` → await reply or timeout) | NoiseSocket (raw frames in), handlers (out) | whatsmeow `nodeHandlers` map + `responseWaiters`; Baileys `query()` + `CB:`/`TAG:` emitter events |
| **Protocol client (state machine)** | Owns connection lifecycle, login/registration payload, retry accounting, reconnect policy, dispatches typed events | Everything in session | whatsmeow `Client` (client.go); Baileys `makeSocket` |
| **Pairing** | QR channel (`ref,noiseKeyPub,identityKeyPub,advSecret`), pair-success verification (HMAC/account signature/device signature), optional 8-char pairing-code flow; writes device identity to store | Router, Store | whatsmeow `pair.go`, `qrchan.go`; Baileys `CB:...pair-device` handler + `requestPairingCode` |
| **SignalSessionManager** | Per-device X3DH/double-ratchet sessions (`pkmsg`/`msg`), sender keys for groups (`skmsg`), prekey generation/upload, identity trust changes | Store (sessions/prekeys/identities), Node codec (payloads) | whatsmeow `send.go`/`message.go` on `go.mau.fi/libsignal`; Baileys `makeLibSignalRepository` + transactional key store |
| **MediaPipeline** | mediaConn IQ + TTL cache; media key → HKDF → AES-CBC + HMAC-10; HTTP upload/download against media hosts; returns `url/direct_path/hashes` for the message protobuf | Router (mediaConn IQ), HTTP (upload), send path (attach) | whatsmeow `upload.go`, `download.go`, `mediaconn.go` |
| **AppStateSync** | Fetch/apply encrypted patch collections with LTHash summation integrity, per-collection `version+hash` cursors, 409 conflict retry, full-sync recovery | Router (IQ), Store (appstate versions/keys) | whatsmeow `appstate/` + `appstate.go` |
| **EventBus / dispatcher** | Map protocol events → typed .NET events/callbacks; per-session and global subscriptions; backpressure; exception isolation per subscriber | Protocol client (in), host (out) | whatsmeow `dispatchEvent` + `eventHandlers`; Baileys `ev` emitter with `makeEventBuffer` batching |
| **SafetyLayer** | Every outbound send passes gates in order: pause/circuit-breaker → rate caps → warm-up daily ceiling → per-recipient cooldown → jitter/typing mimicry → send; records outcomes (success/failure/inbound) into health score | Send path (wraps it), Store (safety state), EventBus (risk signals) | Not in whatsmeow/Baileys core — ecosystem puts it in middleware (`baileys-antiban`, `whatsmeow-antiban`, WAPI anti-ban layer). **Our v1 builds it in** |
| **SessionManager (multi-account)** | `Map<accountId, SessionHandle>`; create/load/destroy sessions; per-session reconnect state machine with classified disconnect reasons (fatal / recoverable / rate-limited); tags all events with accountId; aggregates health | N × Session, SafetyLayer, Store | wa-multi-session (`Map<string, Session>` + per-session retry/backoff), WaSP, Telegram account orchestrators |
| **Store abstraction** | Granular interfaces the host implements: Device (noise/identity keys, JID), Identity, Session, PreKey, SenderKey, AppStateSyncKey, AppState, LID map, safety state | Consumed by Signal, Pairing, AppState, Safety | whatsmeow `store/store.go` interfaces + `sqlstore` reference impl; Baileys `AuthenticationState` (creds + keys) with `makeCacheableSignalKeyStore` |

### Key Boundary Rules (opinionated)

1. **Transport never interprets content.** NoiseSocket emits plaintext bytes; only the codec
   turns them into nodes. This keeps the crypto layer testable in isolation and swappable.
2. **The Router is the only component that fans in.** Every inbound byte path goes through
   one dispatch point — no component subscribes to the socket directly. This is what makes
   inbound instrumentation (dedup, receipts, safety "inbound" counters) single-sourced.
3. **The SafetyLayer is a decorator on the send path, not a hook inside it.** It sits above
   `SendMessage` as a gate (like WAPI's `IRateLimiter.acquire` sequence or whatsmeow-antiban's
   `BeforeSend`/`AfterSend` gate), so the protocol core stays ban-policy-free and policies
   are unit-testable without a socket.
4. **Store interfaces are the pluggability seam and must be defined first.** Everything
   persistent (pairing identity, ratchet sessions, app-state cursors, warm-up day counters)
   flows through them; the host owns the DB.
5. **One session = one isolated object graph.** No static/shared mutable crypto or socket
   state. The multi-account layer composes N independent sessions; it never reaches inside them.

## Recommended Project Structure

```
src/
├── SdkWa.Core/                  # no protocol knowledge — types & seams
│   ├── Jid.cs, WaMessage.cs     # domain types, message envelopes
│   ├── Events/                  # typed event records (MessageReceived, ConnectionState, ...)
│   ├── Stores/                  # IDeviceStore, ISessionStore, IPreKeyStore, IAppStateStore...
│   └── Safety/                  # ISafetyGate, RateLimitOptions, WarmupCurve (interfaces)
├── SdkWa.Protocol/              # wire layer, zero persistence deps
│   ├── Transport/               # FrameSocket, NoiseSocket, Noise handshake
│   ├── Binary/                  # Node model, encoder/decoder, token tables
│   ├── Proto/                   # generated protobuf (waE2E, waCommon, handshake)
│   └── Router/                  # tag dispatch, query()/response waiters
├── SdkWa.Client/                # the single-session protocol client
│   ├── Client.cs                # state machine: connect, login, reconnect
│   ├── Pairing/                 # QR + pairing-code flows
│   ├── Signal/                  # session manager, sender keys, prekeys
│   ├── Media/                   # mediaConn, upload/download, crypto
│   ├── AppState/                # patch fetch/apply, LTHash
│   └── Events/                  # event bus implementation
├── SdkWa.Safety/                # gate implementation (decorator over send)
│   ├── SafetyGate.cs            # ordered gates: pause → caps → warmup → cooldown → jitter
│   ├── WarmupState.cs, HealthScore.cs, CircuitBreaker.cs
├── SdkWa/                       # public facade package (what NuGet consumers import)
│   ├── SessionManager.cs        # multi-account: create/load/destroy, event fan-out
│   └── SessionHandle.cs         # per-account API surface
└── SdkWa.Store.InMemory/        # reference store impl (tests + docs + getting started)
```

### Structure Rationale

- **Core first:** store interfaces + events live in a dependency-free package so hosts can
  implement persistence and compile against events without pulling the protocol layer.
- **Protocol vs Client split:** `SdkWa.Protocol` (codec/transport) has no notion of accounts
  or persistence — it can be exhaustively tested against recorded frames. `SdkWa.Client`
  composes it with the store into a working session.
- **Safety as its own package:** it wraps the client but contains no protocol code; policies
  are pure functions over counters + `TimeProvider`, trivially unit-tested.
- **Facade package last:** `SdkWa.SessionManager` is the only type most consumers touch;
  keeping it thin keeps the public surface small and the internals refactorable.

## Architectural Patterns

### Pattern 1: Layered pipe with upward-only callbacks (the reference-client pattern)

**What:** Strict layering — Transport → Codec → Router → Services → Session API — where each
layer only calls down and receives data up via a handler delegate.
**When to use:** Always, for this domain. It is the shape both whatsmeow and Baileys converged on.
**Trade-offs:** Deep call stacks for one message; but each layer is independently testable and
the wire format can change without touching E2EE logic.

```csharp
// transport emits plaintext; it knows nothing about nodes
noiseSocket.OnFrame = async plaintext => {
    var node = NodeDecoder.Decode(plaintext);   // codec layer
    await router.DispatchAsync(node, ct);       // router layer
};
```

### Pattern 2: Tag-correlated request/response (`query()`)

**What:** Every outbound IQ/message carries a generated tag; a dictionary maps
`tag → TaskCompletionSource<Node>` so callers await replies with timeout.
**When to use:** All synchronous protocol interactions (login result, usync, mediaConn, app-state fetch).
**Trade-offs:** Requires cleanup of orphaned waiters on disconnect (leak source) — clear the
map on close and fail all pending TCSs with a typed `ConnectionLostException`.

```csharp
Task<Node> QueryAsync(Node request, TimeSpan timeout, CancellationToken ct) {
    request.Attrs["id"] = NextTag();
    var tcs = new TaskCompletionSource<Node>(TaskCreationOptions.RunContinuationsAsynchronously);
    _waiters[request.Attrs["id"]] = tcs;
    await SendAsync(request, ct);
    // timeout + finally: _waiters.TryRemove — whatsmeow responseWaiters / Baileys waitForMessage
}
```

### Pattern 3: Per-session single-writer + channel-based inbound pipeline

**What:** One serialized write loop per session (`Channel<OutboundFrame>`) so frame counters
and WebSocket writes are single-writer by construction; inbound flows through a bounded
`Channel<Node>` before dispatch (backpressure, and a natural interception point for receipts
and safety counters).
**When to use:** Always in .NET — replaces whatsmeow's mutexes with a simpler concurrency story.
**Trade-offs:** Bounded channels can drop/stall under load; size them and define overflow policy
(drop-oldest for chat states, block for messages).

```csharp
var outbound = Channel.CreateBounded<OutboundFrame>(new BoundedChannelOptions(256)
    { SingleReader = true, SingleWriter = false });
// single writer task: encode → noiseSeal → socket.Send — counters are safe without locks
```

### Pattern 4: Gate decorator for the safety layer

**What:** Wrap `ISendClient` in an ordered chain of gates (pause → rate caps → warm-up →
cooldown → jitter → inner send), each recording outcomes.
**When to use:** Always for ban-safety — the ecosystem implementations (baileys-antiban's
`wrapSocket`, whatsmeow-antiban's `BeforeSend/AfterSend` gate, WAPI's anti-ban flow) all
converge on "middleware in front of send".
**Trade-offs:** Adds latency to the send path (jitter is intentional); gates must share one
health-score state — keep it in the safety store, not in closures.

```csharp
public async Task<SendResult> SendAsync(SendRequest r, CancellationToken ct) {
    await _pause.ThrowIfPausedAsync(ct);            // circuit breaker / ban pause
    await _rateLimiter.AcquireAsync(r.Account, ct); // sliding-window caps
    await _warmup.AdmitAsync(r.Account, ct);        // daily ceiling ramp
    await _jitter.DelayAsync(r, ct);                // human-like pacing + typing presence
    var res = await _inner.SendAsync(r, ct);        // protocol client
    _health.Record(r.Account, res);                 // success/failure → risk score
    return res;
}
```

### Pattern 5: Pluggable store as granular interfaces (not one big interface)

**What:** 8–12 small interfaces (`ISessionStore`, `IPreKeyStore`, …) aggregated in a
`DeviceState` object, exactly as whatsmeow's `store.Device` does.
**When to use:** Always — a single monolithic `IStore` forces hosts to implement methods for
features they don't use (app-state, LID mapping), which kills adoption.
**Trade-offs:** More types to maintain; mitigate with an in-memory reference implementation
hosts copy from. Note: ratchet session read-modify-write **must be atomic per session address** —
document that requirement on the interface (Baileys wraps its key store in a transaction
capability for exactly this reason).

## Data Flow

### Inbound event path (server → host)

```
WhatsApp server
  → WebSocket frame
  → FrameSocket.ReadAsync                      (raw bytes)
  → NoiseSocket: AEAD open (read key + counter) → plaintext frame
  → NodeDecoder.Decode → Node {tag, attrs, content}
  → Router.Dispatch:
      ├─ tag matches pending query id? → complete TaskCompletionSource (IQ/usync/mediaConn replies)
      └─ tag = "message" → handleEncryptedMessage
            ├─ parse MessageInfo (chat, sender, timestamp, encType)
            ├─ encType pkmsg/msg → SignalSessionManager.DecryptAsync (loads session via ISessionStore,
            │        ratchets, persists new session state)      ← store write
            ├─ encType skmsg      → SenderKey decrypt (group)
            ├─ protobuf decode waE2E.Message
            ├─ ACK: send receipt node back (outbound, also passes SafetyLayer? no — receipts are
            │        protocol traffic, gated only by connection state)
            └─ EventBus.Publish(MessageReceived{accountId, ...})
                 ├─ per-session subscribers (host callback, exception-isolated)
                 └─ global subscribers (SessionManager fan-out, safety inbound counters)
Host application handles the event (routing, CRM, reply)
```

Dedup happens **before** decrypt (whatsmeow `bufferedDecrypt` caches ciphertext hashes) — the
same frame must never ratchet a session twice.

### Outbound send path (host → server)

```
Host → SessionHandle.SendMessageAsync(to, payload)
  → SafetyLayer gate chain (per account):
      pause? → rate caps? → warm-up ceiling? → per-recipient cooldown? → jitter/typing delay
  → DeviceDiscovery (usync IQ via query(); cached per JID, invalidated on participant-hash error)
  → for each target device JID:
      SignalSessionManager: load session (ISessionStore) or fetch prekey bundle + build →
      encrypt → EncryptedNode participant           ← store write (ratcheted state)
  → wrap in message node, encode (NodeEncoder)
  → outbound Channel → single writer: NoiseSocket.Seal (write key + counter) → FrameSocket.Send
  → await response waiter / server ack (timeout + retry w/ exponential backoff, max attempts)
  → SafetyLayer.Record(account, success/failure) → health score update
  → EventBus.Publish(MessageStatus{sent/acked/failed})
```

Media variant inserts before encryption: `MediaPipeline.UploadAsync` (mediaConn IQ → AES-CBC+HMAC
→ HTTP POST → `url/direct_path/hashes`) and puts the result into the protobuf being encrypted.

### State management

| State | Owner | Survives restart via |
|-------|-------|----------------------|
| Device identity (noise/identity keys, JID) | Session | `IDeviceStore` (host) |
| Signal sessions / prekeys / identities | SignalSessionManager | `ISessionStore`/`IPreKeyStore`/`IIdentityStore` |
| App-state version+hash cursors | AppStateSync | `IAppStateStore` |
| Connection state + backoff attempt # | Session state machine | `IDeviceStore` (recommended — avoids re-pair storms; `wa-multi-session` persists status) |
| Warm-up day, health score, pauses | SafetyLayer | `ISafetyStateStore` |
| mediaConn token, usync device lists | caches | in-memory (TTL) — never persist |

## Suggested Build Order

Dependency graph (arrows = "requires"); this directly implies phase ordering:

```
(1) Core types + Store interfaces + event records
      │
      ▼
(2) Node codec (encode/decode) + protobuf schemas ────────┐ parallel-safe with (3)
      ▼                                                   │
(3) Transport: FrameSocket + Noise handshake/frames ◄─────┘
      ▼
(4) Router: tag dispatch + query()/waiters + connection state machine
      ├──────────────┬───────────────────┐
      ▼              ▼                   ▼
(5) Pairing     (6) Signal layer    (7) EventBus skeleton
 QR + persist    sessions/prekeys    (typed events, backpressure)
 resume/reconnect sender keys
      │              │                   │
      └──────┬───────┘                   │
             ▼                           │
      (8) Inbound pipeline ◄─────────────┘
       decrypt → dedup → receipts → publish
             │
             ▼
      (9) Outbound send path
       usync discovery → per-device encrypt → await ack
             │
      ┌──────┴───────┐
      ▼              ▼
(10) Safety gate  (11) Media pipeline
 (decorator on 9)  (needs 4 for mediaConn, 9 to attach)
      │              │
      └──────┬───────┘
             ▼
      (12) AppState sync   ← independent of 9–11; can slip later
             │
             ▼
      (13) Multi-account SessionManager
       (composes N × (1–12), event fan-out, lifecycle)
```

**Build-order rationale (for roadmap phase structuring):**

1. **(1) Store interfaces before anything** — every later component consumes them; changing
   them later ripples through Signal, pairing, app-state, and safety. Cheapest decision to
   make first, most expensive to change last.
2. **(2)(3)(4) form the "wire vertical"** — first demonstrable milestone: connect, complete
   Noise handshake, send an IQ and get a reply (proves codec + transport + router together).
   Nothing above works without this; no reason to parallelize anything else before it lands.
3. **(5) Pairing depends only on the wire vertical + store** → yields the second milestone:
   link a device via QR and **reconnect without re-pairing** (validates persistence seam).
4. **(6) Signal is independent of pairing** — pure crypto over store interfaces; can be
   developed in parallel with (5) and unit-tested offline (this is the standard libsignal
   session-test harness pattern). It blocks (8)/(9), so it must start before messaging.
5. **(8)(9) are the product moment** — E2E text send/receive. (9) must expose the seam the
   **Safety gate (10)** decorates: land the gate *interface* with (9), even if policies fill in
   later — retrofitting a gate around a shipped send path breaks every caller.
6. **(11) Media and (12) App-state are leaf features** — both hang off the wire vertical and
   the send path respectively; neither blocks the other. Media before app-state for this
   product (messaging is core value; contacts/pins/mutes are quality-of-life). App-state sync
   carries the LTHash failure-mode risk (known upstream issue #858) — schedule it when there is
   slack, and scope v1 to read-mostly sync.
7. **(13) Multi-account SessionManager last** — by construction it adds nothing but
   composition *if* every per-session component already honors the isolation rules (no statics,
   per-session cancellation tokens, per-session stores). Building it earlier would force
   rework of anything that leaks global state. Do, however, keep the `SessionManager`
   *interface* sketched in (1) so the public API shape is reviewable from day one.

**Parallelization windows:** (6) ∥ (5); (10) ∥ (11); (12) ∥ (10+11); docs/CI/packaging ∥ everything.

## Scaling Considerations

| Scale | Architecture Adjustments |
|-------|--------------------------|
| 1 account | Full stack as drawn; SessionManager is a thin wrapper (still use it — one code path) |
| ~10s of accounts | One process fits comfortably (reference: direct-protocol sessions ≈ 50 MB each vs browser-based ≈ 400–500 MB — the whole reason to build a protocol client). Watch: total socket count, per-session timers, store connection pooling |
| ~100s of accounts | Bound the inbound channels globally; consider sharding SessionManager across processes (sessions are independent by design, so this is deployment-only, no code change); host store becomes bottleneck first (connection pool, write amplification from ratchet state) |

### Scaling Priorities

1. **First bottleneck: host persistence.** Every decrypt/send writes ratcheted session state;
   a naive EF Core save per message will choke. Mitigation: batch session writes, document
   expected write rate on the store interfaces.
2. **Second bottleneck: timers.** Keepalive + backoff + jitter + warm-up produce O(accounts)
   timers — use a single shared timer wheel / `PeriodicTimer` fan-out, not one `Task.Delay` loop
   per concern per account.

## Anti-Patterns

### Anti-Pattern 1: Browser-automation core (whatsapp-web.js shape)

**What people do:** Wrap headless Chromium and drive web.whatsapp.com internals.
**Why it's wrong:** ~400–500 MB RAM + seconds of cold start per account makes "tens of concurrent
accounts" (a core requirement) impractical; adds Puppeteer/Chromium operational surface.
**Do this instead:** Direct protocol client (whatsmeow/Baileys shape) — ≈50 MB/session,
sub-second start. Reference this comparison whenever scope pressure suggests shortcuts.

### Anti-Pattern 2: Safety logic embedded inside the send function

**What people do:** `if (count > limit) return;` sprinkled through `SendMessageAsync`.
**Why it's wrong:** Uncontrollable, untestable, silently grows; every protocol feature (media,
receipts, retries) needs the same gates and will miss them.
**Do this instead:** Gate decorator (Pattern 4) in front of one `ISendClient` choke point —
the ecosystem's mature antiban packages all landed here independently.

### Anti-Pattern 3: Direct event subscription on the socket / static session state

**What people do:** Host subscribes to raw frames; or static `ConcurrentDictionary` for caches.
**Why it's wrong:** Breaks multi-account isolation (cross-talk between accounts is a data-leak
class bug) and makes reconnect logic race-prone.
**Do this instead:** Single Router dispatch point → typed EventBus with accountId; per-session
object graph; caches keyed inside the session.

### Anti-Pattern 4: Persisting transport caches; failing to dedup before decrypt

**What people do:** Store mediaConn/ousync results in the pluggable store; decrypt on every
receipt of a frame.
**Why it's wrong:** mediaConn tokens TTL-expire (stale = failed sends); double-decrypt double-ratchets
a session and permanently corrupts it (the ratchet is one-way).
**Do this instead:** caches in-memory only; ciphertext-hash dedup buffer ahead of Signal decrypt.

### Anti-Pattern 5: One big `IStore` interface

**What people do:** Single persistence interface with 40 members.
**Why it's wrong:** Hosts must implement app-state, LID, privacy-token methods to send a text —
kills the "pluggable store" adoption story.
**Do this instead:** Granular interfaces aggregated per device (whatsmeow `store.Device` pattern),
with an in-memory reference implementation to copy.

## Integration Points

### External Services

| Service | Integration Pattern | Notes |
|---------|---------------------|-------|
| `wss://web.whatsapp.com/ws/chat` | Noise-over-WebSocket, long-lived | keepalive ping ~20–30s; classify disconnect reasons (logged-out vs network) before auto-reconnect |
| WhatsApp IQ endpoints (`w:sync:app:state`, `encrypt`, `usync`, `w:m`) | query() over the same socket | server throttles IQ budget — rate-limit app-state skip loops upstream had to sleep 300 ms/iteration for this reason |
| Media hosts (`mms`/rupload) | HTTPS POST/GET outside the socket, auth token from mediaConn IQ | Origin/Referer headers checked; token tied to file hash |

### Internal Boundaries

| Boundary | Communication | Notes |
|----------|---------------|-------|
| SessionManager ↔ Session | owns + invokes API + receives events | never reaches inside a session's internals |
| SafetyLayer ↔ Send path | decorator (`ISendClient` wrapper) | protocol core must stay policy-free |
| Protocol client ↔ Store | interface calls (async) | atomic read-modify-write per session address |
| Protocol client ↔ Host | EventBus (typed records) + returned `Task`s | exception isolation per subscriber; bounded channels for backpressure |
| Transport ↔ Codec | plaintext `byte[]` frames | only boundary where data is content-agnostic |

## Confidence Notes

- Component structure, data flows, and layering: **HIGH** — read directly from primary sources
  (whatsmeow, Baileys, whatsapp-web.js source and DeepWiki source-line-cited docs); seam tier for
  cross-checked web findings is MEDIUM (`classify-confidence --verified`).
- Multi-account manager specifics: **MEDIUM** — patterns derived from smaller wrapper libraries
  (wa-multi-session, WaSP) rather than a battle-tested reference at "tens+ accounts in one .NET
  process"; no reference implementation in .NET exists for this protocol.
- Safety layer placement: **HIGH** on the decorator consensus (three independent ecosystems
  converged), but policy thresholds (warm-up curves, I/O ratios) are empirical, not architectural.

## Sources

- whatsmeow source — github.com/tulir/whatsmeow (client.go, socket/, binary/, pair.go, send.go, message.go, appstate/, upload.go, store/store.go) — primary, HIGH
- Salman Ansari, "Reading whatsmeow source code" (hisalman.in, 2026-03-18) — walkthrough confirming layer read-order — MEDIUM
- Baileys — DeepWiki "Core Architecture" / "Socket Layer Hierarchy" / "Cryptography and Security" (source-line cited) + github.com/WhiskeySockets/Baileys src/Socket/socket.ts — primary-assisted, HIGH
- whatsapp-web.js — DeepWiki "Advanced Topics" + github.com/pedroslopez/whatsapp-web.js Client.js — primary-assisted, HIGH
- Baileys vs whatsapp-web.js comparison (checkleaked.cc blog, 2026-06) — resource figures per session — MEDIUM
- whatsmeow issues #858/#518/#651 + PR #1171 — app-state LTHash failure modes — HIGH (primary)
- github.com/zakirkun/wapi docs/architecture/anti-ban-flow.md — safety gate sequence reference — MEDIUM
- github.com/TA-rathnayaka/whatsmeow-antiban, kobie3717/baileys-antiban — gate-chain antiban designs — MEDIUM
- github.com/mimamch/wa-multi-session (DeepWiki) — multi-session Map<sessionId, Session> pattern — MEDIUM

---
*Architecture research for: WhatsApp Web multi-device client SDK for .NET*
*Researched: 2026-10-07*
