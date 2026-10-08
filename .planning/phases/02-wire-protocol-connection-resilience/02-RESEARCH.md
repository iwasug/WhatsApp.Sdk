# Phase 2: Wire Protocol & Connection Resilience - Research

**Researched:** 2026-10-08
**Domain:** WhatsApp Web binary wire protocol over WebSocket — Noise XX handshake (WA variant), binary node codec + query/reply router, classified auto-reconnect, typed connection-state events
**Confidence:** MEDIUM (transport constants + disconnect taxonomy confirmed against MIT-licensed Baileys docs this session; WA-variant wire minutiae need a planning-spike confirmation before locking the router API)

## Summary

Phase 2 builds the first live vertical of the SDK: open a WebSocket to WhatsApp, run the Noise XX handshake, exchange correlated query/reply frames through a router, and reconnect on its own with a per-disconnect-code policy — all with no host involvement and safe for many concurrent accounts. The critical research outcome is that the WA Noise variant is fully pinned down from a permissive source: Baileys' MIT-licensed protocol documentation states the Noise protocol name is `Noise_XX_25519_AESGCM_SHA256` (null-padded) with the `WA` + version + dictionary-version header `[87, 65, 6, 3]`, a token-dictionary binary node codec, zlib-optional framing, and a published disconnect-code enum — all readable without touching MPL-2.0 whatsmeow code.

The single most important correction to prior research: STACK.md describes the Noise suite as `Noise_XX_25519_ChaChaPoly_SHA256`. Baileys' docs state the cipher is **AESGCM**, not ChaChaPoly. The planner must treat the STACK.md cipher line as superseded for the Noise transport and confirm `Noise_XX_25519_AESGCM_SHA256` during the planning spike (Baileys `src/Defaults` is MIT — readable and citable). The practical consequence is favorable: AES-GCM is in-box (`AesGcm`), so the only NSec dependency in Phase 2 is X25519 key agreement; no ChaCha20-Poly1305 is needed on the Noise path.

The recommended implementation order is: (1) binary frame pump (ClientWebSocket + PipeReader, length-prefix framing) against a fake in-process server; (2) hand-rolled Noise XX state machine over NSec X25519 + BCL AES-GCM/SHA256/HKDF, proven against cacophony vectors for `XX_25519_AESGCM_SHA256`; (3) node codec (token dictionary ported from Baileys MIT sources with NOTICE attribution — never from whatsmeow) + iq router with message-id correlation and timeouts; (4) reconnect state machine with full-jitter backoff and the per-code policy table; (5) typed state events. Every layer is testable without live WhatsApp via duplex-pipe fakes, golden fixtures, and vector suites; live interop drift detection is explicitly Phase 9's job.

**Primary recommendation:** Build the wire vertical bottom-up (frames → Noise XX/AESGCM → node codec → iq router → reconnect policy → state events) behind transport/codec interfaces so all but one loopback test run against in-memory fakes; port the token dictionary and handshake constants from Baileys (MIT, attribute in NOTICE.md) and confirm the two wire-minutiae unknowns (pre-auth round-trip shape, frame segmentation) in a planning spike before locking the router API.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| WebSocket transport + frame pump | API/Backend (SDK connection layer) | — | Own socket lifecycle per account; host never sees sockets |
| Noise XX handshake (WA variant) | API/Backend (SDK crypto layer) | — | Session keys feed the frame encryptor; no host crypto |
| Binary node codec (encode/decode) | API/Backend (SDK codec) | — | Pure functions over spans; shared immutable token table |
| Query/reply router (iq correlation) | API/Backend (SDK connection layer) | — | Matches replies to awaiting callers by message id |
| Reconnect policy + backoff | API/Backend (SDK connection layer) | — | Fully autonomous; no host code on drop (success criterion 1) |
| Connection-state events | API/Backend (SDK public surface) | — | Typed `connecting`/`open`/`closing`/`logged_out` per account (CONN-05) |
| Unaauthenticated-session wiring to store seam | API/Backend (SDK session layer, minimal) | Database/Storage (host store) | Prove Phase 1 seam minimally (read/write ephemeral keys); full pairing/resume is Phase 3 |

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| CONN-04 | Connection auto-reconnects with backoff and discriminates disconnect reasons (401 terminal, 428/408 retry, 515 restart, 440 replaced, 411 outdated) | Disconnect-code taxonomy table (Baileys MIT enum) + per-code policy mapping; full-jitter exponential backoff (AWS canonical); Phase 1 clamp-after-jitter precedent (D-18) reused for reconnect delays |
| CONN-05 | Host can observe per-account connection state events (`connecting`/`open`/`closing`/`logged_out`) | State-machine design with exactly the 4 public states + internal backoff sub-state; `ILogger` per-account scopes + `CancellationToken` end-to-end per Phase 7-facing conventions |
</phase_requirements>

## Project Constraints (from AGENTS.md / prior decisions)

- **Tech stack (locked):** .NET 10 LTS (`net10.0`), C# 14; in-box `ClientWebSocket` (custom `Origin` header, own reconnect state machine — no third-party WS wrappers); `System.IO.Pipelines` for framing; `Google.Protobuf` 3.36.2 + `Grpc.Tools` 2.84.0; `NSec.Cryptography` 26.4.0 for X25519; BCL for AES-GCM/HMAC/HKDF/SHA-2; `Microsoft.Extensions.Logging.Abstractions` only [VERIFIED: C:/Users/iwasu/iwasug/sdk-wa/Directory.Packages.props:13-32 for the already-pinned set; STACK.md for the transport/crypto decisions]
- **Clean-room / MIT:** no code, `.proto` files, or generated output copied from whatsmeow (MPL-2.0); behavioral reference only. Baileys is MIT-licensed [CITED: https://context7.com/whiskeysockets/baileys (License: MIT)] — its sources may be *read* and permissively-licensed artifacts (constants, token table) ported *with NOTICE.md attribution*; never copy from whatsmeow. NOTICE.md already exists in repo root [VERIFIED: repo root listing shows NOTICE.md]
- **Phase 1 contracts reused, not re-litigated:** typed exception hierarchy (`SdkException` base; messages name rules only, never payload values) [VERIFIED: src/Iwasug.WhatsApp.Sdk/Errors/SdkException.cs:14-23]; `Jid` immutable parsed value type (no wire vocabulary inside it) [VERIFIED: src/Iwasug.WhatsApp.Sdk/Domain/Jid.cs:27-32 scope guard]; five granular stores behind async factories; D-18 clamp-after-jitter retry precedent (raw=Base·2ⁿ, ±30% jitter, clamp [50,250]ms) [CITED: STATE.md Phase 01 decision log]; central package versions in `Directory.Packages.props` [VERIFIED: Directory.Packages.props:9-11]
- **Ban-safety pacing is Phase 5:** Phase 2 needs only reconnect backoff with jitter, not send throttling. Rate-limiting/channels packages stay unused this phase.
- **Two packages only (D-12):** no new NuGet package in Phase 2 — everything lands in `Iwasug.WhatsApp.Sdk`.

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| .NET SDK / runtime | 10.0.401 (local) | `net10.0` target, C# 14 | LTS channel; verified installed this session [VERIFIED: `dotnet --version` → 10.0.401] |
| `System.Net.WebSockets.ClientWebSocket` (in-box) | .NET 10 | WS transport to `wss://web.whatsapp.com/ws/chat` with `Origin: https://web.whatsapp.com` browser-mimicry header, `KeepAliveInterval` pings | Exact header/socket control the protocol needs; every wrapper fights resume semantics [ASSUMED — endpoint + Origin requirement per STACK.md prior research; confirm against Baileys `waWebSocketUrl`/socket config in planning spike] |
| `System.IO.Pipelines` | 10.0.12 (add to central pins) | Length-prefix frame parsing over the WS byte stream via `PipeReader` | Zero-copy parsing of a long-lived binary stream is the canonical Pipelines use case; official docs give the fill/parse loop shape [CITED: https://learn.microsoft.com/en-us/dotnet/standard/io/pipelines] |
| `NSec.Cryptography` | 26.4.0 (add to central pins) | X25519 ephemeral + static key agreement for Noise XX | Only genuine BCL gap on the Noise path; MIT, libsodium-backed, Span-based, secure-memory keys [VERIFIED: nuget.org flatcontainer — 26.4.0 is latest stable, fetched this session] |
| In-box `System.Security.Cryptography` | .NET 10 | `AesGcm` (Noise AESGCM cipher), SHA-256 (Noise hash + `MixHash`), HKDF (`MixKey` KDF), `RandomNumberGenerator` | WA variant uses AESGCM per Baileys docs, so the transport cipher is BCL-covered; no ChaCha20-Poly1305 needed on the Noise path [CITED: Baileys protocol-details page for NOISE_MODE; BCL coverage per STACK.md prior research] |
| Hand-rolled Noise XX state machine | ~400–600 LoC over NSec + BCL | `Noise_XX_25519_AESGCM_SHA256` (WA variant: null-padded protocol name, `WA` header prologue) | No shippable .NET Noise dependency exists (Noise.NET dormant since 2020, native-collision with NSec); WA framing/prologue sit outside any generic library anyway; objectively testable against cacophony vectors [CITED: https://github.com/jchambers/java-noise (cacophony vectors as canonical source); STACK.md for Noise.NET dormancy] |
| `Google.Protobuf` + `Grpc.Tools` | 3.36.2 runtime / 2.84.0 codegen | Handshake-payload protos (minimal set) + any post-handshake protobuf payloads | Byte-exact wire semantics matching other implementations for cross-checking [VERIFIED: nuget.org flatcontainer — 3.36.2 latest stable, 4.0.0-rc1/rc2 prereleases exist and must be avoided; fetched this session] |
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.12 (already pinned) | `ILogger` per-account scopes for connects/drops/reconnects/anomalies | Embeddable-library standard; already in central pins [VERIFIED: Directory.Packages.props:15] |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| `System.IO.Compression` (in-box `ZLibStream`) | .NET 10 | Inflate frames with the compression flag bit set | Frame decode path only — first byte bit `0x02` means zlib [CITED: Baileys protocol-details page, `decompressingIfRequired`] |
| `Microsoft.Extensions.Options` | 10.0.12 (already pinned) | `IOptions<ConnectionOptions>` (timeouts, backoff bounds, keepalive) | Connection tuning surface; safe-default values in code |
| `System.Threading.Channels` (in-box) | .NET 10 | Bounded inbound-dispatch / outbound-send queues per connection | Backpressure between socket pump and router without unbounded growth |
| xunit.v3 + `Microsoft.NET.Test.Sdk` + AwesomeAssertions + NSubstitute | already pinned (4.0.1 / 18.10.1 / 9.6.0 / 6.2.0) | Golden-fixture, vector, fake-server, and policy tests | All Phase 2 tests [VERIFIED: Directory.Packages.props:21-26] |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Hand-rolled Noise XX | Noise.NET 1.0.0 | Dormant (2020), stale native libsodium collides with NSec; read-only reference at most |
| Hand-rolled Noise XX | Torann (managed, cacophony-verified) | Zero-native-deps appeal, but ~370 downloads — not mature enough to underpin session crypto |
| `ClientWebSocket` + PipeReader | .NET 10 `WebSocketStream` | Brand-new API; don't be first on the critical path (revisit post-GA shakedown) |
| Full-jitter backoff | Polly.Core retry | Reconnect needs protocol-aware policy (re-handshake, per-code paths) a generic retry can't express; Polly stays scoped to Phase 6 HTTP media work |
| AES-GCM via NSec | BCL `AesGcm` | BCL is platform-maintained, AOT-safe, zero extra deps — use BCL |

**Installation:**
```bash
dotnet add package NSec.Cryptography --version 26.4.0
dotnet add package Google.Protobuf --version 3.36.2
# build-time only (PrivateAssets=all):
dotnet add package Grpc.Tools --version 2.84.0
# then move versions into Directory.Packages.props (central management is repo law)
```

## Package Legitimacy Audit

| Package | Registry | Age | Downloads | Source Repo | Verdict | Disposition |
|---------|----------|-----|-----------|-------------|---------|-------------|
| NSec.Cryptography 26.4.0 | NuGet | mature (26.x line) | high (locked in STACK.md) | github.com/ektrah/nsec | OK | Approved — 26.4.0 confirmed latest stable via flatcontainer this session [VERIFIED: nuget.org flatcontainer] |
| Google.Protobuf 3.36.2 | NuGet | mature 3.x line | very high | github.com/protocolbuffers/protobuf | OK | Approved — 3.36.2 confirmed latest stable; 4.0.0-rc1/rc2 are prereleases, do NOT adopt [VERIFIED: nuget.org flatcontainer] |
| Grpc.Tools 2.84.0 | NuGet | mature | very high | github.com/grpc/grpc | OK | Approved — build-time only, `PrivateAssets=all`, no runtime dep [ASSUMED version currency — carry STACK.md pin 2.84.0; re-verify at plan time] |

**Packages removed due to [SLOP] verdict:** none
**Packages flagged as suspicious [SUS]:** none
*Note: the `gsd package-legitimacy` seam covers npm/pypi/crates only; NuGet verification was done directly against the first-party flatcontainer API this session (same method as Phase 1 research).*

## Architecture Patterns

### System Architecture Diagram

```
HOST APP (no connection code — only observes events + configures options)
   │  ConnectionOptions (IOptions)        ConnectionState events + ILogger scopes
   ▼                                                         ▲
┌──────────────────────────────────────────────────────────────────────┐
│ WaConnection (per AccountId — NO shared mutable state across accounts)│
│                                                                      │
│  ┌────────────┐   binary WS msgs   ┌──────────────┐  frames ┌───────┐ │
│  │ Transport  │◄──────────────────►│  Frame pump  │◄──────►│ Noise │ │
│  │ (WS socket │   Origin header,   │ (PipeReader,  │  crypt │Xport  │ │
│  │  per-conn) │   30s WS pings,    │  len-prefix,  │◄──────►│(AESGCM│ │
│  └─────▲──────┘   connect timeout  │  zlib flag)  │  keys  │ kDF)  │ │
│        │ drop w/ code              └──────┬───────┘        └───▲───┘ │
│        │                          nodes   │              ┌────────┴──┐ │
│  ┌─────┴──────┐                   ┌───────▼────────┐    │ Noise XX  │ │
│  │ Reconnect  │◄──policy per code─│   iq Router    │    │ handshake │ │
│  │ state mach.│                   │ (id→Task map,  │    │ (e→e,ee,  │ │
│  │ + backoff  │──states──────────►│  60s timeout)  │    │  s,es→s,se│ │
│  └────────────┘  connecting/open/ └────────────────┘    └───────────┘ │
│                   closing/logged_out                                  │
└──────────────────────────────────────────────────────────────────────┘
   │ ephemeral-key read/write (minimal proof of store seam; full resume = Ph3)
   ▼
Phase-1 store seam (IIdentityStore etc. via existing async factories)
```

Primary use case traced: `WaConnection.ConnectAsync` → WS open (20s timeout) → send `WA\x06\x03` header + Noise `→e` → process `←e,ee,s,es` → send `→s,se` → transport keys ready → router round-trips an iq (`id` correlation, 60s timeout) → `open` event. On drop: map code → terminal (401/411/440-surface) vs jittered retry (408/428) vs immediate restart (515) → `connecting` → re-handshake with fresh ephemeral → `open`, host code never involved.

### Recommended Project Structure

```
src/Iwasug.WhatsApp.Sdk/
├── Transport/
│   ├── IFrameTransport.cs        # byte-duplex abstraction (WS impl + in-memory fake)
│   ├── WebSocketFrameTransport.cs# ClientWebSocket + Pipe pump, Origin/UA headers, WS pings
│   └── FrameReader.cs            # 2-byte BE length-prefix + compression-flag framing
├── Noise/
│   ├── NoiseHandshake.cs         # XX state machine: →e / ←e,ee,s,es / →s,se
│   ├── NoiseTransport.cs         # post-handshake AEAD encrypt/decrypt, sequential nonces
│   └── NoiseProtocol.cs          # constants: protocol name, WA header, dict version
├── Codec/
│   ├── BinaryNode.cs             # { Tag, Attrs, Content: children|string|bytes }
│   ├── BinaryNodeCodec.cs        # token-dict encode/decode, nibble/hex packing, JID forms
│   └── TokenDictionary.cs        # ported from Baileys MIT constants + NOTICE attribution
├── Connection/
│   ├── WaConnection.cs           # per-account lifecycle owner (no statics)
│   ├── QueryRouter.cs            # iq id→TaskCompletionSource map + timeouts
│   ├── ReconnectPolicy.cs        # code→action table + full-jitter backoff
│   ├── DisconnectReason.cs       # typed code enum (401/408/411/428/440/515/…)
│   ├── ConnectionState.cs        # connecting/open/closing/logged_out
│   └── ConnectionOptions.cs      # timeouts, backoff bounds, keepalive (IOptions)
└── Proto/
    └── handshake.proto           # MINIMAL handshake-payload schema, provenance-logged
tests/Iwasug.WhatsApp.Sdk.Tests/
├── Transport/FrameReaderTests.cs # golden frame fixtures (valid + truncated + evil lengths)
├── Noise/NoiseVectorTests.cs     # cacophony XX_25519_AESGCM_SHA256 vectors
├── Noise/NoiseHandshakeTests.cs  # two-party handshake over duplex pipe (no socket)
├── Codec/BinaryNodeRoundTripTests.cs
├── Connection/QueryRouterTests.cs# correlation, timeout, late-reply-after-timeout
├── Connection/ReconnectPolicyTests.cs # per-code behavior incl. 440/411 no-retry
└── Connection/LoopbackTests.cs   # ONE real-WS loopback test (HttpListener fake server)
```

### Pattern 1: Transport behind a byte-duplex interface; only one test touches real sockets
**What:** `IFrameTransport` (send frame bytes / receive frame bytes / close code) with two implementations: `WebSocketFrameTransport` (production) and `LoopbackFrameTransport` (two linked `Pipe`s, in-memory). Noise, codec, router, and reconnect logic are all written against the interface.
**When to use:** Everything except the single loopback test.
**Why:** 95% of Phase 2 tests become deterministic, sub-second, and CI-safe with no network; the loopback test proves the real `ClientWebSocket` path once. Mirrors the Baileys AGENTS.md rule "new protocol path → integration test mocking the binary node, not e2e" [CITED: https://github.com/WhiskeySockets/Baileys/blob/master/AGENTS.md].

### Pattern 2: Noise XX as an explicit 3-message state machine, payloads opaque in Phase 2
**What:** `NoiseHandshake` exposes `WriteMessageA() → ReadMessageB() → WriteMessageC()` over the XX token flow `→e / ←e,ee,s,es / →s,se` [CITED: https://noiseprotocol.org/noise.html §7], initialized with protocol name `Noise_XX_25519_AESGCM_SHA256` null-padded to 32 bytes, prologue = `WA` header bytes. Handshake *payloads* (protobuf ClientHello/ClientFinish) are treated as opaque byte blobs supplied by a callback in Phase 2 — their schema semantics belong to Phase 3 pairing.
**When to use:** The handshake and every reconnect re-handshake (fresh ephemeral each attempt — never reuse `e` across handshakes).
**Why:** Keeps Phase 2 shippable without dragging pairing schema design forward, while the crypto core is fully vector-tested.

### Pattern 3: iq router as `ConcurrentDictionary<string, TaskCompletionSource>` with timeout + late-reply discard
**What:** Outbound iq nodes carry a unique `id` attr (monotonic counter per connection, e.g. `msg-id-{n}`); the router registers a TCS per id, matches inbound replies by `id`, enforces `defaultQueryTimeoutMs`-style timeout (60s per Baileys default [CITED: https://baileys.wiki/concepts/socket-config]), and discards replies arriving after timeout. Timeout surfaces as a typed exception, never a silent drop.
**When to use:** Every correlated round-trip (the success-criterion round-trip, later Phase 4 acks/receipts).
**Why:** The 408-lesson from production Baileys operators — messages sent during disconnect windows silently vanish — must not be repeated: every send has exactly one observable outcome (reply / timeout / connection-lost) [CITED: Baileys troubleshooting docs + operator lesson on 408 silent drops].

### Pattern 4: Reconnect policy as a pure code→action function + full-jitter backoff calculator
**What:** `ReconnectPolicy.Decide(code)` is a pure function returning `Terminal | RetryAfter(backoff) | RestartImmediate | SurfaceAndStop`; delay math is full-jitter `sleep = random(0, min(cap, base·2ⁿ))` per the AWS canonical reference [CITED: https://aws.amazon.com/blogs/architecture/exponential-backoff-and-jitter], reusing Phase 1's clamp-after-jitter discipline so every delay stays in provable bounds.
**When to use:** Every abnormal closure; attempt counter resets on `open` and on 515-restart.
**Why:** Pure function = exhaustively unit-testable per-code table without sockets or timers (use `FakeTimeProvider`, already pinned [VERIFIED: Directory.Packages.props:18]).

### Anti-Patterns to Avoid
- **Auto-reconnecting on 401 or 411:** 401 means logged-out (creds dead — reconnect loops forever against a dead session); 411 means client-outdated (needs a deploy, not a retry). Both must be terminal + surfaced.
- **Aggressive retry on 440 (replaced):** another companion took over the session; immediate reconnect starts a kick-war between devices. Surface `account-replaced` and stop — host decides.
- **Reusing Noise ephemeral keys across reconnects:** fresh `e` per handshake, zeroize old keys; reuse destroys forward secrecy.
- **Silent message loss in the disconnect window:** every in-flight query completes as reply/timeout/connection-lost; the outbound queue fails fast on `closing`.
- **Static/shared connection state:** per-`AccountId` instances only; the token dictionary is immutable shared data (safe), everything mutable is per-connection (Phase 7 isolation depends on this from day one).
- **Porting codec/handshake code from whatsmeow:** MPL-2.0 file-level copyleft — read behavior only; port constants solely from Baileys (MIT) with NOTICE attribution.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Binary framing buffers | Manual `byte[]` juggling in `ReceiveAsync` | `System.IO.Pipelines` `PipeReader` with `AdvanceTo(consumed, examined)` | Length-prefix frames split across WS messages; Pipelines handles partial reads + backpressure natively [CITED: MS Learn Pipelines doc] |
| Backoff math | Ad-hoc `Thread.Sleep(base * attempt)` | Full-jitter formula from the AWS reference | Unjittered backoff is the documented thundering-herd loser; full-jitter is the measured best-overall [CITED: AWS Architecture Blog] |
| WS keepalive | Custom ping thread | `ClientWebSocketOptions.KeepAliveInterval` (30s, matching Baileys `keepAliveIntervalMs` default) + server-driven detection | Framework-managed pongs; matches observed WA tolerance [CITED: https://baileys.wiki/concepts/socket-config] |
| Timeout/delay tests | Real `Task.Delay` in tests | `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`, already pinned) | Deterministic backoff/timeout tests with no wall-clock flakiness |
| Binary-node debug view | Custom dump format | `binaryNodeToString`-style XML rendering helper (own implementation of the documented idea) | Baileys documents trace-logging nodes as XML as the debugging standard; same readability, no port [CITED: Baileys protocol-details page] |
| Test vector format | Invented handshake vectors | Cacophony/noise-c vector suite for `XX_25519_AESGCM_SHA256` as golden JSON fixtures | 1,352-pattern×cipher×hash coverage exists; inventing vectors proves nothing [CITED: sgiath/noise-protocol (Snow+Cacophony conformance); java-noise (cacophony vectors as canonical)] |

**Key insight:** Phase 2's risk is wire-shape wrongness, not algorithm novelty. Every defense is borrowed: canonical Noise vectors for crypto, MIT-ported constants for framing, interface-seamed fakes for tests. The only thing hand-rolled is composition (state machine + router + policy), which is exactly the part unit tests can pin.

## Scope Fences (Phase 2 vs 3 vs 4)

| Capability | Phase | Rationale |
|------------|-------|-----------|
| WS + Noise XX + frame pump + router + reconnect + 4 state events | **2 (this phase)** | Success criteria verbatim |
| Handshake-payload *bytes* plumbing (opaque blobs in/out of handshake) | 2 | Proves the handshake runs end-to-end without pairing semantics |
| QR / pairing-code flows, `code`/`success`/`timeout` lifecycle, credential persistence + restart resume | **3, not 2** | CONN-01/02/03; Phase 2 stores only ephemeral handshake keys (minimal seam proof) |
| Handshake-payload *schema semantics* (ClientHello registration, ADV identity, signed device identity) | 3, not 2 | Needs pairing design; Phase 2 treats payloads as opaque |
| Signal sessions, E2EE encrypt/decrypt of message bodies, prekey iq queries | **4, not 2** | MSG-* requirements; Phase 2's round-trip is an unauthenticated iq (e.g. version/attr query), not a Signal message |
| Send throttling, warm-up, circuit breaker | **5, not 2** | Only reconnect backoff lives here |
| Session-blob persistence across process restarts | 3, not 2 | Phase 2 may write ephemeral keys through the store seam to prove it, but resume-without-repair is Phase 3's criterion |
| `logged_out` *from pairing* (e.g. QR timeout → logged_out) | 3, not 2 | Phase 2's `logged_out` fires only on 401-disconnect mapping |

## Disconnect Taxonomy → Policy (CONN-04 core table)

Derived from the Baileys `DisconnectReason` enum [CITED: Baileys source as quoted in https://github.com/WhiskeySockets/Baileys/issues/1923] and Baileys reconnect guidance ("401 = fresh QR needed; anything else safe to retry" [CITED: https://baileys.wiki/advanced/troubleshooting]), refined for our success criteria:

| Code | Baileys name | Meaning | Phase 2 behavior |
|------|--------------|---------|------------------|
| 401 | `loggedOut` | Session logged out / creds dead | **Terminal.** Emit `logged_out`, dispose, never retry (host must re-pair in Phase 3) |
| 408 | `connectionLost` / `timedOut` | Network lost or query timed out (code is overloaded — log both sub-causes distinctly) | **Retry** with full-jitter backoff; fail in-flight queries as connection-lost |
| 428 | `connectionClosed` | Server closed connection (incl. server-side enforcement kicks) | **Retry** with full-jitter backoff [CITED: issue #2249 shows 428 as server-side enforcement shape] |
| 440 | `connectionReplaced` | Another companion replaced this session | **Surface `account-replaced`, stop.** No auto-retry (avoids kick-war); host restarts explicitly |
| 411 | `multideviceMismatch` | Protocol/client version mismatch | **Surface `protocol-outdated`, stop.** Needs deploy with bumped version, not retries |
| 515 | `restartRequired` | Server demands fresh session (also fires right after QR scan in Baileys flow) | **Immediate restart:** fresh socket + fresh Noise handshake, reset backoff counter [CITED: Baileys FAQ "watch for restartRequired and reopen the socket"] |
| 403 | `forbidden` | Forbidden | Terminal + surfaced (no retry) |
| 500 | `badSession` | Bad session | Retry with backoff (bounded); Phase 3 resume may later escalate to re-pair |
| 503 | `unavailableService` | Service unavailable | Retry with longer-base backoff |
| 405 / unknown | — | Unknown / drift signal | Bounded backoff retry + loud log; 405 specifically feeds the Phase 9 drift job, not silent handling |

Timeout defaults to adopt (Baileys socket-config [CITED: https://baileys.wiki/concepts/socket-config]): `connectTimeoutMs` 20s, `defaultQueryTimeoutMs` 60s, `keepAliveIntervalMs` 30s, `retryRequestDelayMs` 250ms base. Backoff bounds (planner discretion, starting point): base 1s, cap 60s, full jitter, unbounded attempts for 408/428 (user-visible `closing` with attempt count in logs), reset on `open`.

## Common Pitfalls

### Pitfall 1: Building against the wrong Noise cipher (ChaChaPoly vs AESGCM)
**What goes wrong:** Handshake completes against vectors but fails against real WA (or vice versa) — every subsequent byte is garbage and the failure looks like a framing bug.
**Why it happens:** STACK.md names ChaChaPoly; Baileys docs name AESGCM for the WA variant.
**How to avoid:** Lock `Noise_XX_25519_AESGCM_SHA256` in the planning spike by reading Baileys `src/Defaults` (MIT); handshake tests run the AESGCM vector set; add a plan task that fails closed if the spike contradicts.
**Warning signs:** Anyone citing STACK.md's cipher line without the Baileys cross-check.

### Pitfall 2: Treating 408 as one cause
**What goes wrong:** Retry tuning fights the wrong enemy — timeout-config vs network-flap need different responses and different logs.
**Why it happens:** Baileys overloads 408 for both `connectionLost` and `timedOut` (same enum value) [CITED: issue #1923 enum quote].
**How to avoid:** Log the sub-cause (last-query outstanding? socket error vs query timeout?) alongside the code; keep the policy identical but the diagnostics distinct.

### Pitfall 3: Length-prefix framing assuming one frame per WS message
**What goes wrong:** Multi-frame coalescing or split frames corrupt the stream; bug appears only under load/latency.
**Why it happens:** Happy-path testing with one small frame per message.
**How to avoid:** `PipeReader` loop that parses *all* complete frames per `ReadAsync` and suspends on partials; golden fixtures include coalesced, split-byte-by-byte, and evil-length (cap max frame, e.g. 16 MiB, abort beyond) cases.

### Pitfall 4: Nonce reuse or wrong nonce direction after re-handshake
**What goes wrong:** Silent decryption failure or, worse, AES-GCM nonce reuse — a catastrophic confidentiality break.
**Why it happens:** Nonce counters stored in the wrong owner (shared across reconnects) or send/recv directions swapped.
**How to avoid:** Nonce pair owned by `NoiseTransport`, constructed fresh per handshake starting at 0, send/recv counters separate fields; `NoiseTransport` is single-owner disposable; never pool or share.

### Pitfall 5: Late replies completing the wrong awaiting query
**What goes wrong:** A reply arriving after timeout completes a *reused* id's new TCS — cross-talk between unrelated queries.
**Why it happens:** Monotonic id counter wraps or ids reused while a late reply is still in flight.
**How to avoid:** Never reuse an id while its reply window is open (64-bit counter, no wrap in practice); router removes TCS on timeout AND discards late arrivals with a debug log.

### Pitfall 6: MPL contamination via "just the proto files"
**What goes wrong:** Copying whatsmeow's `.proto` schemas (or generated C#) triggers MPL-2.0 file-level obligations and breaks the MIT posture — discovered at Phase 9 provenance audit, i.e. maximally expensively.
**Why it happens:** whatsmeow protos are the most complete schema reference and the easiest to copy.
**How to avoid:** Phase 2 authors a *minimal hand-written* `handshake.proto` from Baileys MIT TS types + observed behavior; every `.proto` file carries a provenance header comment; planner adds a "no whatsmeow-sourced bytes" review checkpoint.

### Pitfall 7: `closing`/`logged_out` event ordering races
**What goes wrong:** Host sees `open` after `logged_out`, or duplicate terminal events — host state machines corrupt.
**Why it happens:** Reconnect loop and close path race on the event channel.
**How to avoid:** Single-writer event dispatch (one `Channel`, one pump task); terminal states emitted exactly once via `Interlocked` transition guard; state machine transitions unit-tested including the 515-during-backoff and 401-during-connecting interleavings.

## Code Examples

### Noise XX message flow (pattern reference — implement, don't import)
```
// XX pattern per Noise spec rev 34 [CITED: https://noiseprotocol.org/noise.html]:
//   →e
//   ←e, ee, s, es
//   →s, se
// WA variant [CITED: Baileys protocol-details page]:
NOISE_MODE   = "Noise_XX_25519_AESGCM_SHA256" + 0x00*4   // null-padded
NOISE_HEADER = [0x57, 0x41, 0x06, 0x03]                  // 'W','A', proto 6, dict 3
// Message A on the wire = NOISE_HEADER + ephemeral_pubkey(32)   [ASSUMED framing — confirm in spike]
```

### Binary node shape (implement in C# — shown as TS for fidelity to source)
```typescript
// Source: Baileys src/WABinary/types.ts via protocol-details page [CITED]
export type BinaryNode = {
    tag: string
    attrs: { [key: string]: string }
    content?: BinaryNode[] | string | Uint8Array
}
// C# equivalent: sealed record with Content as OneOf (children | string | byte[]) —
// planner's call; keep it a pure immutable value type, no behavior.
```

### Frame decode entry (logic shape from Baileys decode path)
```csharp
// Logic per Baileys decompressingIfRequired [CITED: protocol-details page]:
//   if ((buffer[0] & 0x02) != 0) buffer = Inflate(buffer[1..]); else buffer = buffer[1..];
// C# sketch for the planner (Phase-2 FrameReader, over PipeReader):
//   - need ≥3 bytes (1 flag + 2 BE length) else suspend  [ASSUMED prefix width — confirm in spike]
//   - if flag&0x02 → ZLibStream inflate payload
//   - enforce MaxFrameBytes cap before allocating; abort connection on violation (V5)
```

### Reconnect decision (pure function — fully unit-testable)
```csharp
// Formula per AWS Architecture Blog [CITED]:
//   sleep = Random(0, min(cap, base * 2^attempt))   // full jitter
// Policy per Disconnect Taxonomy table above (CONN-04):
//   401/403/411 → Terminal(reason)      408/428/500/503 → RetryAfter(FullJitter(attempt))
//   515 → RestartImmediate              440 → SurfaceAndStop    unknown → RetryAfter bounded + loud log
```

### Connection-state events (CONN-05 surface shape)
```csharp
// Public surface (planner locks exact API; events per account, CancellationToken end-to-end):
public enum ConnectionState { Connecting, Open, Closing, LoggedOut }
// Transition rule: Connecting → Open → Closing → (Connecting | LoggedOut);
// LoggedOut terminal for this connection instance; exactly-once emission (Pitfall 7).
// Every transition logs via per-account ILogger scope and carries the DisconnectReason + attempt count.
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `Noise_XX_25519_ChaChaPoly_SHA256` assumed for WA (STACK.md) | `Noise_XX_25519_AESGCM_SHA256` null-padded (Baileys MIT docs) | Corrected this session | BCL `AesGcm` covers transport cipher; NSec needed for X25519 only |
| Manual `ReceiveAsync`-into-`byte[]` loops | `PipeReader` fill/parse loops | .NET Core 2.1+, still current | Partial-frame handling becomes structural, not manual |
| `Thread.Sleep` retry loops | Full-jitter exponential backoff (AWS canonical) | AWS post (long-standing, reaffirmed May 2023) | Measurably lowest server load + completion time |
| e2e-against-live for protocol paths | Fake-transport integration tests; live only in gated drift job | Baileys AGENTS.md convention | Phase 2 stays CI-safe; Phase 9 owns live interop |
| xunit.v2-era vector tests | xunit.v3 + golden JSON fixtures + `FakeTimeProvider` | Locked in Phase 1 | Deterministic crypto/policy tests |

**Deprecated/outdated:**
- Noise.NET as any kind of dependency (dormant, native collision) — reference only, never shipped
- `FluentAssertions`/Moq-era test patterns — AwesomeAssertions + NSubstitute per locked stack
- ChaCha20-Poly1305 on the Noise transport path — not needed once AESGCM is confirmed (BCL ChaCha stays available for Phase 4 media if required)

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | WS endpoint `wss://web.whatsapp.com/ws/chat` + `Origin: https://web.whatsapp.com` browser mimicry | Standard Stack | Connect fails closed (obvious, fast); spike confirms |
| A2 | Message A = `WA` header + raw 32-byte ephemeral pubkey; 2-byte BE length-prefix framing around post-handshake frames | Code Examples | Router/frame tests target wrong bytes; spike against Baileys `src/Socket/noise-handler` + `WAFrame` (MIT) must confirm before locking `FrameReader` |
| A3 | Pre-auth iq round-trip (e.g. version/attr query) exists and suffices for success criterion 1 without pairing | Scope Fences | If no pre-auth query is possible, criterion 1 needs a Phase 3-assisted shape — spike must answer this first |
| A4 | NSec X25519 API shape (`KeyAgreementAlgorithm.X25519`, `Agree`) as sketched in planning | Standard Stack | Compile-time discovery; NSec ships XML docs — resolve in first Noise task, not in planning |
| A5 | Cacophony/noise-c vector JSON for `XX_25519_AESGCM_SHA256` is obtainable and importable as fixtures | Don't Hand-Roll | Fallback: generate vectors from a second implementation (Torann/sgiath) and cross-check — still stronger than self-generated |
| A6 | Backoff starting params (base 1s, cap 60s, unbounded 408/428 attempts) | Disconnect table | Tunable without API break; observe in Phase 9 interop |
| A7 | 440 = stop-no-retry (anti-kick-war) though Baileys samples retry broadly | Disconnect table | If WA tolerates prompt re-takeover, policy can soften later — conservative default is the safe direction |
| A8 | Token dictionary may be ported from Baileys MIT sources with NOTICE attribution | Anti-Patterns | Legal-process judgment; if counsel disagrees, re-derive by observation (slower, same bytes — facts, not expression) |
| A9 | Grpc.Tools 2.84.0 currency carried from STACK.md (not re-fetched) | Package Audit | Re-verify one flatcontainer call at plan time |

## Open Questions

1. **Exact pre-auth round-trip shape (A3)**
   - What we know: handshake completes; Baileys then proceeds to QR/pairing or login depending on creds.
   - What's unclear: which iq (if any) a credential-less session may round-trip to satisfy "correlated query/reply" without building pairing.
   - Recommendation: first plan task is a read-only spike over Baileys MIT `noise-handler` + socket open sequence; lock the router API only after.

2. **Frame segmentation detail (A2)**
   - What we know: length-prefix + compression-flag framing per Baileys decode docs.
   - What's unclear: prefix width/count semantics and multi-segment coalescing rules on the real wire.
   - Recommendation: same spike; golden fixtures committed from spike findings before `FrameReader` implementation.

3. **NSec native-binary story for consumers**
   - What we know: NSec ships per-RID libsodium natives; fine for tests and most hosts.
   - What's unclear: whether any target (unusual AOT/sandbox) needs the BouncyCastle fallback discussed in STACK.md.
   - Recommendation: defer — Phase 2 consumes NSec normally; revisit only on a concrete consumer report (STACK.md already records the fallback).

4. **`Closing` state observability depth**
   - What we know: CONN-05 mandates exactly 4 states.
   - What's unclear: whether hosts need backoff-attempt visibility (pollable property vs event payload).
   - Recommendation: carry attempt count + last reason in the `Closing` event payload and logs; no extra public state without a host request.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | Build/test | ✓ | 10.0.401 [VERIFIED: `dotnet --version` this session] | — |
| nuget.org reachability | NSec + Protobuf restore | ✓ | NSec 26.4.0 / Protobuf 3.36.2 confirmed [VERIFIED: flatcontainer probes this session] | Offline VS packages feed exists but lacks these pkgs — restore requires network |
| Live WhatsApp endpoint | Nothing in Phase 2 | n/a | — | Fake transports + loopback server by design (live interop is Phase 9) |
| NSec native runtime (libsodium) | Noise tests at runtime | ✓ presumed | ships per-RID with the package | Confirm on first `dotnet test` with NSec referenced |

**Missing dependencies with no fallback:** none — Phase 2 is fully CI-safe by construction.
**Missing dependencies with fallback:** none.

## Validation Architecture

> Nyquist gate is enabled (`workflow.nyquist_validation: true` [VERIFIED: .planning/config.json:25]); `security_enforcement: true`, ASVS L1 [VERIFIED: .planning/config.json:49-50].

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 4.0.1 + Microsoft.NET.Test.Sdk 18.10.1 (MTP runner via repo global.json) |
| Config file | existing test project (extend, don't create); new fixtures under `tests/` per structure above |
| Quick run command | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~Noise\|FullyQualifiedName~FrameReader\|FullyQualifiedName~ReconnectPolicy"` |
| Full suite command | `dotnet test Iwasug.WhatsApp.Sdk.sln` (per config `test_command` [VERIFIED: .planning/config.json:54]) |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| CONN-04 | Per-code policy: 401 terminal-never-retried; 408/428 jittered retry; 515 immediate restart; 440/411 surface-and-stop | unit (pure policy fn) + integration (fake transport injects each code) | `dotnet test --filter "FullyQualifiedName~ReconnectPolicy"` | ❌ Wave 0 |
| CONN-04 | Backoff delays always within [base, cap], growth provable, jitter decorrelates | unit (FakeTimeProvider, adversarial-seed sweep) | same filter | ❌ Wave 0 |
| CONN-04 | Reconnect performs fresh Noise handshake (new ephemeral, old keys zeroized) over fake transport after injected drop | integration (duplex-pipe fake) | `dotnet test --filter "FullyQualifiedName~Loopback\|FullyQualifiedName~NoiseHandshake"` | ❌ Wave 0 |
| CONN-04 | Cacophony XX_AESGCM vectors pass (crypto correctness, not self-generated) | unit (golden JSON fixtures) | `dotnet test --filter "FullyQualifiedName~NoiseVector"` | ❌ Wave 0 |
| CONN-04 | Frame codec: golden valid / split / coalesced / evil-length frames | unit (golden fixtures) | `dotnet test --filter "FullyQualifiedName~FrameReader\|FullyQualifiedName~BinaryNode"` | ❌ Wave 0 |
| CONN-05 | State sequence `connecting→open→closing→connecting→open` across a drop; terminal `logged_out` exactly once on 401 | integration (fake transport script) | `dotnet test --filter "FullyQualifiedName~ConnectionState"` | ❌ Wave 0 |
| CONN-05 | Query/reply correlation + 60s timeout + late-reply discard | unit (router against fake transport) | `dotnet test --filter "FullyQualifiedName~QueryRouter"` | ❌ Wave 0 |

### Sampling Rate
- **Per task commit:** quick run command for the touched area + full build (`dotnet build Iwasug.WhatsApp.Sdk.sln --nologo`)
- **Per wave merge:** `dotnet test Iwasug.WhatsApp.Sdk.sln`
- **Phase gate:** full suite green before `/gsd-verify-work`

### Wave 0 Gaps
- [ ] `tests/.../NoiseVectorTests.cs` + `TestData/noise-xx-aesgcm-vectors.json` — covers CONN-04 crypto correctness
- [ ] `tests/.../FrameReaderTests.cs` + golden frame fixtures — covers CONN-04 framing
- [ ] `tests/.../ReconnectPolicyTests.cs` (FakeTimeProvider-based, no sockets) — covers CONN-04 policy table
- [ ] `tests/.../QueryRouterTests.cs` + `ConnectionStateTests.cs` (fake `IFrameTransport` harness) — covers CONN-05 + CONN-04 round-trip
- [ ] `tests/.../LoopbackTests.cs` (single real-WS `HttpListener` fake server) — proves production transport once
- [ ] `src/` production files per Recommended Project Structure above
- [ ] `Directory.Packages.props` additions: NSec.Cryptography 26.4.0, Google.Protobuf 3.36.2, Grpc.Tools 2.84.0 (PrivateAssets)

## Security Domain

### Applicable ASVS Categories (L1)

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | partial | Noise XX mutually authenticates the channel; no passwords/tokens in Phase 2 — handshake payloads opaque |
| V3 Session Management | yes | Fresh ephemeral per handshake; transport keys zeroized on close; no session resumption in Phase 2 (Phase 3) |
| V4 Access Control | no | Single-tenant connection object; no cross-account surface yet (isolation proven structurally, enforced in Phase 7 suite) |
| V5 Input Validation | **yes — critical** | Frame length cap (abort on violation); token-dict bounds checks; unknown attrs preserved-not-executed; every decoder path fuzzed by evil fixtures |
| V6 Cryptography | **yes — critical** | Primitives from BCL/NSec only; sequential AES-GCM nonces owned per-transport; `RandomNumberGenerator` for keys; never log key material (extend Phase 1 message discipline) |
| V9 Communications | yes | WSS only; TLS cert validation never disabled; `Origin`/UA browser mimicry is header content, not a security downgrade |

### Known Threat Patterns for this stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Malicious/fuzzed server frames (length lies, truncated zlib, token overruns) | Tampering / DoS | Length cap + bounds-checked codec + evil fixtures; abort connection, never throw unhandled into host |
| Noise nonce reuse across reconnect | Information disclosure (catastrophic) | Fresh `NoiseTransport` per handshake; counters non-shared, non-persisted |
| Downgrade via dictionary-version mismatch (dict ≠ 3) | Tampering | Pin `DICT_VERSION = 3`, fail closed on mismatch with loud log [CITED: Baileys `NOISE_WA_HEADER = [87,65,6,3]`] |
| Replay of handshake message A | Spoofing | Fresh ephemeral per attempt; server-side challenge semantics owned by WA — client never accepts a replayed transport |
| Key material in logs/dumps | Information disclosure | Extend Phase-1 rule: exception/log messages name operations only; review checkpoint on all new log sites |

## Sources

### Primary (HIGH confidence)
- NuGet v3 flatcontainer API (`api.nuget.org`) — NSec.Cryptography 26.4.0 + Google.Protobuf 3.36.2 latest-stable confirmation, fetched this session
- Local env probes — `dotnet --version` 10.0.401, repo layout, `Directory.Packages.props`, `SdkException.cs`, `Jid.cs`, `.planning/config.json` (all Read this session)
- https://noiseprotocol.org/noise.html — XX pattern `→e / ←e,ee,s,es / →s,se`, protocol-name construction, prologue semantics

### Secondary (MEDIUM confidence)
- Baileys protocol-details doc (MIT project docs, mirrors `src/WABinary` + `src/Defaults`): NOISE_MODE AESGCM, WA header `[87,65,6,3]`, token/dictionary codec, zlib flag, query/iq shapes — via WebFetch this session
- https://baileys.wiki/concepts/socket-config — timeout/keepalive defaults (20s/60s/30s/250ms)
- https://baileys.wiki/advanced/troubleshooting + FAQ — 401-terminal guidance, 515-restart guidance
- https://github.com/WhiskeySockets/Baileys/issues/1923 — `DisconnectReason` enum values (401/408/411/428/440/515/403/500/503)
- https://aws.amazon.com/blogs/architecture/exponential-backoff-and-jitter — full-jitter canonical formulas
- https://github.com/jchambers/java-noise + sgiath/noise-protocol — cacophony vectors as canonical Noise test source
- https://learn.microsoft.com/en-us/dotnet/standard/io/pipelines — PipeReader fill/parse pattern
- https://github.com/WhiskeySockets/Baileys/blob/master/AGENTS.md — mock-node-not-e2e test rule (process guidance, not protocol fact)

### Tertiary (LOW confidence — planner must spike-confirm)
- Endpoint URL + Origin header value (carried from STACK.md prior research, not re-fetched)
- Message-A wire bytes, length-prefix width, pre-auth iq availability (A1–A3)
- NSec exact X25519 call shape (A4); Grpc.Tools 2.84.0 currency (A9)

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — versions registry-verified this session; only Grpc.Tools carried forward
- Architecture: MEDIUM — layering + patterns solid; two wire minutiae gated on the planning spike
- Pitfalls: MEDIUM-HIGH — taxonomy + policy table sourced; 440 stance is a judgment call flagged as such

**Research date:** 2026-10-08
**Valid until:** ~2026-11-08 (stable domain; Baileys-side drift possible — re-check `DICT_VERSION`/header if Phase 3 planning lags)
