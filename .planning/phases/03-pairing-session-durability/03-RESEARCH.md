# Phase 3: Pairing & Session Durability - Research

**Researched:** 2026-10-09
**Domain:** WhatsApp Web multi-device pairing (QR + link-code), credential persistence, restart resume
**Confidence:** MEDIUM-HIGH (protocol behavior cross-checked across two independent official implementations — Baileys MIT + whatsmeow MPL behavioral reads; in-repo seam facts verified by file reads this session; live-server interop not yet observed, see Open Questions)

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| CONN-01 | Host can link an account via QR code and observe typed lifecycle events (`code`/`success`/`timeout`/`logged_out`/`client_outdated`) | QR payload construction + ref rotation (60s/20s, ~160s window), `pair-device`/`pair-success` stanza handling, `<success>`/`<failure reason=405>` mapping, `DisconnectReason` 405 gap (Pitfall 1), event-vocabulary mapping table in Architecture Patterns |
| CONN-02 | Host can link an account via pairing code without scanning a QR (headless servers) | `link_code_companion_reg` two-stage flow (`companion_hello` → notification → `companion_finish`), 5-byte → 8-char Crockford base32 code, PBKDF2-SHA256/131072/32B + AES-256-CTR wrap, advSecret HKDF, key-bundle AES-GCM wrap, phone-number validation rules |
| CONN-03 | Session state persists through the pluggable store; a restarted process resumes without re-pairing | Sixth store seam (`ICredentialStore`) modeled on the single-row `ISafetyStateStore` precedent; login `ClientPayload` inside Noise message C; `<success>` acceptance path; persist-before-continue durability (D-06); post-restart reconnect through injected drop + 515 handling |
</phase_requirements>

## Project Constraints (from AGENTS.md)

Actionable directives the planner must honor (all `[VERIFIED: AGENTS.md]`, injected as project instructions this session):

1. **Stack is locked:** .NET 10 LTS (`net10.0` only), C# 14, current SDK **10.0.401** / runtime **10.0.12**.
2. **Clean-room / no MPL-2.0 copying:** "No verbatim copying of code, `.proto` files, schemas, or generated output from whatsmeow (or any MPL-2.0 / copyleft-licensed source)" and "Behavioral reference only … the resulting C# must be independently written" `[VERIFIED: NOTICE.md:13-21]`. Baileys is MIT and may inform shapes with NOTICE attribution. **Phase 3 reads `pair.go`, `pair-code.go`, `qrchan.go`, `handshake.go`, `connectionevents.go` as behavioral reference only — never copy.**
3. **Provenance log is mandatory:** every upstream file read for a protocol detail must get a NOTICE.md log row `[VERIFIED: NOTICE.md:29-43]`. Phase 3 reads ≥6 new upstream files → ≥6 new rows.
4. **Pluggable store is locked:** no EF Core/Dapper/DB inside the SDK; host owns persistence. Phase 3's credential store must be a *seam*, not a file/db implementation.
5. **Ban safety is a v1 requirement, not future work** — pairing must not introduce high-frequency reconnect loops (405-stale-retry, QR re-request spam).
6. **Compliance:** usage must respect WhatsApp ToS; the SDK documents the risks it mitigates and the risks it cannot (relevant to what `success`/`timeout` promise to hosts).
7. **GSD workflow enforcement:** work starts through a GSD command (`/gsd-execute-phase` for phase work); no direct repo edits outside a workflow.
8. **Tests / build commands are fixed:** `dotnet build Iwasug.WhatsApp.Sdk.sln --nologo`, `dotnet test Iwasug.WhatsApp.Sdk.sln` `[VERIFIED: .planning/config.json:53-54]`.

## Summary

Phase 2 delivered framing, the Noise XX handshake, a payload-agnostic query router, the reconnect policy table, and five store seams — but the *open* flow is deliberately a skeleton. `WaConnection` injects `INoiseHandshakePayloads` whose doc says "The payloads' schema semantics belong to Phase 3 pairing (ClientHello registration, ClientFinish login)" `[VERIFIED: src/Iwasug.WhatsApp.Sdk/Noise/NoiseHandshake.cs:11-13]`, and after the handshake it sends a synthetic probe (`private static readonly byte[] ProbeBody = "wa-probe"u8.ToArray();` `[VERIFIED: src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs:103]`) and, on its reply, calls that "Open" `[VERIFIED: WaConnection.cs:661-694]`. Phase 3 replaces all of that with the real protocol: a `ClientPayload` inside Noise message C, server stanzas `<success>` / `<failure>` / `<iq>` / `<notification>` dispatched as BinaryNodes, pair-success verification, and durable credential persistence.

The three facts that shape the whole plan: **(1)** registration vs login is *one* protobuf (`ClientPayload`) whose contents decide the flow — the same `GetClientFinish()` seam carries both `[CITED: raw.githubusercontent.com/tulir/whatsmeow/main/handshake.go]`; **(2)** the pump must move from Phase 2's skeleton correlation envelope (`idLength(u8) || id(ASCII) || body`) to BinaryNode dispatch + id-attribute correlation — `QueryRouter`'s own docs defer that to Phase 4 `[VERIFIED: src/Iwasug.WhatsApp.Sdk/Connection/QueryRouter.cs:22-25]`, but live pairing *is* the "real node traffic" that triggers it, so this is a planned pull-forward/deviation for the planner to decide explicitly; **(3)** no credential store exists — `StoreRegistryOptions` exposes exactly five factory seams `[VERIFIED: src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistryOptions.cs:27-51]` and a repo-wide grep for `ClientPayload|noiseKey|advSecret|registrationId|signedPreKey` in `src/` returned **no matches**, so Phase 3 adds a sixth seam.

Two hard gaps must be closed or CONN-01 cannot hold: **405 (`client_outdated`) is absent from `DisconnectReason`** — `ReconnectPolicy.Decide`'s default arm sends unknown codes to a bounded retry `[VERIFIED: src/Iwasug.WhatsApp.Sdk/Connection/ReconnectPolicy.cs:144-150]`, so a stale client would reconnect-loop instead of surfacing `client_outdated`; and **runtime WA-version resolution** is assigned to this phase by the project's own pitfall research: "Phase to address: Pairing/Noise transport phase (runtime version resolution as part of connect flow)" `[VERIFIED: .planning/research/PITFALLS.md:28]`.

**Primary recommendation:** implement pairing as a dedicated `PairingCoordinator` (own state machine + typed event surface, separate from `ConnectionState`), feed it through the existing `INoiseHandshakePayloads` seam, add a sixth single-row `ICredentialStore` modeled on `ISafetyStateStore`, pull BinaryNode dispatch forward as an explicit Phase 3 deviation, and add `DisconnectReason.ClientOutdated = 405 → Terminal`. Zero new runtime NuGet packages; everything else is in-box BCL.

## Architectural Responsibility Map

*Standard web tiers (Browser / SSR / API / CDN / Database) map onto this library as: **SDK connection core** (protocol logic), **Host application** (display/input), **Pluggable store tier** (persistence). No browser, SSR, or CDN tier exists in this project.*

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| QR payload construction (`wa.me/…#ref,noise,identity,adv,platform`) + 60s/20s ref rotation | SDK connection core (Pairing) | — | Wire-protocol knowledge; host must not invent the format |
| QR rendering (image/terminal) | **Host application** | SDK samples/docs (optional QRCoder) | Stack rule: SDK emits the raw payload string only; hosts decide display |
| Pairing-code generation + link-code crypto (PBKDF2/CTR/HKDF/GCM) | SDK connection core (Pairing) | — | Constant-heavy protocol composition; must be single-sourced and KAT-tested |
| Phone-number input, code display (`XXXX-XXXX`) | **Host application** | — | Headless hosts own UX; SDK validates and returns the formatted code |
| `link_code_companion_reg` hello/finish exchange + notification handoff | SDK connection core | Query dispatcher | Requires iq correlation + notification routing (BinaryNode dispatch) |
| Pair-success verification (HMAC + account/device signatures) | SDK connection core | — | Authenticity gate; failure must fail closed before any credential write |
| Credential persistence | **Pluggable store tier** (`ICredentialStore`) | SDK awaits writes | Host owns durability; SDK enforces persist-before-continue (D-06) |
| Restart resume (login `ClientPayload` in message C) | SDK connection core | Pluggable store tier | Same handshake seam as pairing; store supplies the row |
| Typed lifecycle events (`code`/`success`/`timeout`/`logged_out`/`client_outdated`) | SDK connection core (Pairing events) | Host subscribes | Vocabulary belongs next to the pairing state machine, not in `ConnectionState` |
| 405/401 classification + reconnect verdicts | SDK connection core (`ReconnectPolicy`) | — | Existing single code-to-action table; never a second policy |
| Runtime WA version resolution (`sw.js` `client_revision`) | SDK connection core (connect flow) | — | PITFALLS assigns it to this phase; 405 clears the cache |
| Session durability across process restart | **Pluggable store tier** | Host deployment | Only the host can survive a process kill; SDK documents the contract |

## Standard Stack

### Core (all already pinned or in-box — **no new runtime package required**)

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `Google.Protobuf` + `Grpc.Tools` | **3.36.2** / **2.84.0** `[VERIFIED: Directory.Packages.props:41-42]` | Encode/decode `ClientPayload`, `ADVSignedDeviceIdentity(HMAC)`, `DeviceProps`, `HandshakeMessage` | Same reason as Phase 2: byte-parity with protoc-based implementations is the debugging lifeline; schemas are **hand-written** with provenance (see `handshake.proto` precedent `[VERIFIED: src/Iwasug.WhatsApp.Sdk/Proto/handshake.proto:3-17]`) |
| `NSec.Cryptography` | **26.4.0** `[VERIFIED: Directory.Packages.props:40]` | X25519 DH for link-code ephemeral + identity shared secrets; Ed25519/Ed25519 verify for ADV signatures | Only gap in .NET's crypto box; libsodium-audited, typed-key API |
| In-box `Rfc2898DeriveBytes.Pbkdf2` (SHA-256) | .NET 10 | Link-code key: PBKDF2-SHA256, **131072** iterations, 32-byte output `[CITED: raw.githubusercontent.com/tulir/whatsmeow/main/pair-code.go]` | Never hand-roll PBKDF2; static span-based overload is in-box |
| In-box `System.Security.Cryptography.HKDF` | .NET 10 | `adv_secret` and key-bundle info-string derivations | In-box since .NET 8; exact info strings quoted in Code Examples |
| In-box `HMACSHA256` | .NET 10 | Pair-success HMAC verification over `details` with `advSecretKey` | Authenticity gate — platform implementation only |
| In-box `AesGcm` | .NET 10 | Key-bundle wrap (salt32 ‖ nonce12 ‖ ciphertext) | Authenticated encryption; never compose your own AEAD |
| In-box `Aes` (ECB block) + **repo-composed AES-256-CTR** | .NET 10 | Link-code ephemeral wrap needs CTR — **.NET has no `CipherMode.Ctr`**: net-10.0 `CipherMode` fields are `CBC \| 1`, `ECB \| 2`, `OFB \| 3`, `CFB \| 4`, `CTS \| 5` `[VERIFIED: learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.ciphermode (net-10.0 Fields table)]` | Compose CTR over the in-box block cipher (16-byte counter, big-endian), covered by known-answer tests; **do not** add a crypto package just for CTR |
| In-box `System.IO.Compression.ZLibStream` | .NET 10 | Already used by `FrameReader` (`private const int CompressedFlag = 0x02;` `[VERIFIED: src/Iwasug.WhatsApp.Sdk/Transport/FrameReader.cs:33]`) | Post-handshake compression flag is **already handled** — Phase 3 does not touch it |
| `Microsoft.Extensions.Logging.Abstractions` | **10.0.12** `[VERIFIED: Directory.Packages.props:15]` | `ILogger` only; pairing events logged with **redaction** | Host-owned logging pipeline |

### Supporting

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| `QRCoder` | **1.8.0** `[VERIFIED: api.nuget.org/v3-flatcontainer/qrcoder/index.json — latest version 1.8.0]` | Render QR **in a console sample / docs only** | Only if Phase 3 adds a sample project for the human QR-scan proof; never referenced by `src/Iwasug.WhatsApp.Sdk` |
| `Microsoft.Extensions.TimeProvider.Testing` | **10.10.0** `[VERIFIED: Directory.Packages.props:18]` | FakeTimeProvider drives 60s/20s QR rotation and pairing timeouts deterministically | Test-only; already in the test stack |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Repo-composed AES-CTR over in-box `Aes` | `BouncyCastle.Cryptography` (has CTR) | Adds a large managed dependency for one mode — rejected; composition is ~40 lines + KATs |
| Hand-written `.proto` schemas | `protobuf-net` attribute contracts | Loses byte-parity with protoc encodings — already rejected in Phase 2 (`[VERIFIED: AGENTS.md` What NOT to Use table]) |
| SDK emits raw QR string | SDK renders QR itself (`QRCoder` in core) | Violates "dependency-free core / host decides display" — QRCoder stays samples-only |
| New `PairingState` public enum | Reuse `ConnectionState` (add states) | Breaks the locked 4-state vocabulary and the "scope guard" doc rule `[VERIFIED: src/Iwasug.WhatsApp.Sdk/Connection/ConnectionState.cs:16-21]` |
| Separate pairing backoff logic | Existing `ReconnectPolicy.Decide` | Two policy tables drift; add the 405 arm to the one table |

**Installation (only if the sample project is approved):**
```bash
dotnet add samples/PairingSample package QRCoder --version 1.8.0   # samples/docs only
```
Core library: **no new PackageReference.**

**Version verification performed this session:** `api.nuget.org/v3-flatcontainer/qrcoder/index.json` → latest `1.8.0` (published 2026-04-04 per registration catalog, MIT, first published 2016-05-16 → ~10 yrs old) `[VERIFIED: api.nuget.org/v3/registration5-semver1/qrcoder/index.json]`; all other pins re-read from `Directory.Packages.props` this session.

## Package Legitimacy Audit

> The `gsd-tools query package-legitimacy check` seam supports **npm | pypi | crates only** — running it for NuGet returns: `Error: Usage: gsd-tools package-legitimacy check --ecosystem <npm|pypi|crates> <pkg1> ...` (executed this session). NuGet packages were therefore verified directly against the first-party registry + GitHub API.

| Package | Registry | Age | Downloads | Source Repo | Verdict | Disposition |
|---------|----------|-----|-----------|-------------|---------|-------------|
| `QRCoder` 1.8.0 (optional, samples only) | NuGet | ~10 yrs (first publish 2016-05-16) | registration stats endpoint deprecated — not retrievable | github.com/Shane32/QRCoder (MIT, 5.2k★, pushed 2026-09-11) `[VERIFIED: api.github.com/search/repositories?q=QRCoder]` | **OK** | Approved — samples/docs only, never a core dependency |
| `Google.Protobuf` 3.36.2, `Grpc.Tools` 2.84.0, `NSec.Cryptography` 26.4.0 | NuGet | multi-year | high | upstream repos | **OK** (already audited in `02-RESEARCH` Package Legitimacy Audit) | Carry forward, no change |
| Microsoft.Extensions.* / System.* 10.0.12 | NuGet | runtime-aligned | very high | github.com/dotnet | **OK** | Carry forward |

**Packages removed due to [SLOP] verdict:** none
**Packages flagged as suspicious [SUS]:** none

*No package in this phase was discovered via WebSearch or training data; QRCoder's name comes from the project's own locked stack (`AGENTS.md`), and its current version/age/repo were re-verified against the registry + GitHub API this session.*

## Architecture Patterns

### System Architecture Diagram

```
                          HOST APPLICATION (tier: display/input only)
    ┌──────────────────────────────────────────────────────────────────────────┐
    │  render QR string │ show 8-char code │ collect E.164 phone │ subscribe   │
    └───────────▲────────────────▲───────────────────────▲─────────────┬──────┘
                │ code payload   │ code XXXX-XXXX        │             │ typed events
┌───────────────┴────────────────┴───────────────────────┴─────────────▼────────┐
│  PAIRING COORDINATOR (new: Pairing/)  — owns the pairing state machine        │
│   • QR mode: build payload, rotate refs on 60s/20s timer, re-issue on expiry   │
│   • Code mode: generate 5B→8C code, wrap ephemeral, hello→notification→finish │
│   • verify pair-success (HMAC + sigs) BEFORE any credential write             │
│   • emits code|success|timeout|logged_out|client_outdated                      │
└───────────────┬───────────────────────────────┬───────────────────────────────┘
                │ INoiseHandshakePayloads       │ persist-before-continue
                │  GetClientHello()/            ▼
                │  GetClientFinish()   ┌─────────────────────────┐
┌───────────────▼──────────────────┐   │ PLUGGABLE STORE (tier)  │
│ WaConnection (existing)          │   │  + ICredentialStore ◄───┼─ 6th seam
│  handshake → message C carries   │   │    (single row, D-06)   │   (5 existing:
│  ClientPayload (register/login)  │   └─────────────────────────┘   signal/identity/
└───────────────┬──────────────────┘                                  prekey/recent/
                │ encrypted frames                                     safety state)
                ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│  PUMP / DISPATCH (changed): FrameReader → NoiseTransport.Decrypt →           │
│   BinaryNode decode → tag dispatch: <success> <failure> <iq> <notification>  │
│   <stream:error>  ·  iq replies correlated by node id attribute             │
│   (pull-forward from the Phase 2 skeleton envelope — see Open Question 1)    │
└───────────────┬──────────────────────────────────────────────────────────────┘
                ▼
        wss://web.whatsapp.com/ws/chat   (version resolved at runtime from
                                          web.whatsapp.com/sw.js, 6h cache,
                                          cleared on 405)
```

### Recommended Project Structure

```
src/Iwasug.WhatsApp.Sdk/
├── Pairing/
│   ├── PairingCoordinator.cs      # state machine + typed event surface (new public API)
│   ├── PairingEvents.cs           # PairingCodeEventArgs / PairingStatus (code|success|timeout|logged_out|client_outdated)
│   ├── PairingQrPayload.cs        # 'https://wa.me/settings/linked_devices#…' builder + ref rotation
│   ├── PairingCodeGenerator.cs    # 5 random bytes → 8-char Crockford base32, XXXX-XXXX display
│   ├── LinkCodeCrypto.cs          # PBKDF2/CTR(over in-box AES)/HKDF/AES-GCM composition
│   ├── ClientPayloadFactory.cs    # registration vs login ClientPayload (protobuf)
│   ├── PairSuccessVerifier.cs     # HMAC + account/device signature verification, fail-closed
│   └── WaVersionResolver.cs       # sw.js client_revision, ~6h cache, 405 invalidation
├── Proto/
│   ├── client_payload.proto       # hand-written, Baileys field-role provenance (never whatsmeow)
│   ├── adv.proto                  # ADVSignedDeviceIdentity / ADVSignedDeviceIdentityHMAC / ADVDeviceIdentity
│   └── companion_reg.proto        # DeviceProps + pairing props (as needed)
├── Stores/
│   ├── ICredentialStore.cs        # + ICredentialStoreFactory (6th seam; single-row shape)
│   └── InMemory/…                 # reference impl
└── Connection/
    ├── WaConnection.cs            # Open = <success>; probe flow removed
    ├── ReconnectPolicy.cs         # + ClientOutdated 405 → Terminal
    ├── DisconnectReason.cs        # + ClientOutdated = 405
    └── (pump) node tag dispatch + id-attribute correlation
src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/
└── CredentialStoreContractTests.cs   # host-facing contract kit (completeness backstop)
tests/Iwasug.WhatsApp.Sdk.Tests/
├── Pairing/…                      # QR payload, rotation timers, code gen, crypto KATs
└── Connection/…                   # scripted fake-server pairing + restart-resume proofs
```

### Pattern 1: Credential-carrying payload provider (implement the existing seam)
**What:** `INoiseHandshakePayloads` already exists; Phase 3 writes the implementation that serializes `ClientPayload` (registration when no stored credentials, login when present) into message C.
**When to use:** every connection attempt — pairing *and* resume.
**Example source:** [CITED: raw.githubusercontent.com/tulir/whatsmeow/main/handshake.go] — `ClientFinish: &waWa6.HandshakeMessage_ClientFinish{ Static: encryptedPubkey, Payload: encryptedClientFinishPayload }`; registration vs login node selection [CITED: raw.githubusercontent.com/WhiskeySockets/Baileys/0af2386/src/Utils/validate-connection.ts] (`generateRegistrationNode` vs `generateLoginNode`).

### Pattern 2: Pairing state machine with a *separate* typed event surface
**What:** pairing emits its own 5-value vocabulary; `ConnectionState` keeps its locked 4 values.
**When to use:** always — the scope-guard style already used by `Jid`/`DisconnectReason`.
**Event mapping (CONN-01):**

| Host event | Wire trigger | Reference behavior |
|------------|--------------|--------------------|
| `code` | `<iq><pair-device><ref/>…` → payload string; first TTL 60s, later 20s | `timeout := 20 * time.Second` / `if len(codes) == 6 { timeout = 60 * time.Second }` `[CITED: …/whatsmeow/main/qrchan.go]` |
| `timeout` | refs exhausted (≈60 + 5×20 = **160s**) or server disconnects before pairing | `QRChannelTimeout = QRChannelItem{Event: "timeout"}` `[CITED: …/qrchan.go]` |
| `success` | `<iq>` **`pair-success`** verified + persisted | `QRChannelSuccess … {Event: "success"}` `[CITED: …/qrchan.go]` |
| `client_outdated` | `<failure reason=405>` | `else if reason == events.ConnectFailureClientOutdated { … dispatchEvent(&events.ClientOutdated{}) }` `[CITED: …/connectionevents.go]` |
| `logged_out` | `<failure reason=401/403/…>` during pairing | whatsmeow has **no** `logged_out` QR event — ours derives it from the logged-out connect-failure set (see Assumptions A4) |

### Pattern 3: Persist-before-continue, verify-then-write
**What:** verify pair-success **first**, then write credentials and *await* the write before sending the confirmation iq — mirroring `ISafetyStateStore`'s contract: "the write completes only when an independent subsequent read observes the value; the SDK awaits it before continuing" `[VERIFIED: src/Iwasug.WhatsApp.Sdk/Stores/ISafetyStateStore.cs:20-23]`.
**When to use:** every credential mutation (pair-success, LID update, registration-id advance, advSecret rotation).
**Why:** a crash between "server considers us paired" and "store has the row" is exactly the re-pair failure mode; PITFALLS: "Persist at each pair step; treat 515 as 'reconnect immediately with persisted state'" `[VERIFIED: .planning/research/PITFALLS.md:288]`.

### Pattern 4: Runtime version resolution with 405 invalidation
**What:** resolve the WA Web version at connect time, cache ~6h, bundled pin as fallback, and clear the cache on 405.
**Source:** "Resolve the live version at connect time from `web.whatsapp.com` (the source of truth), cache ~6h, with a fallback to a bundled pin" and "On 405/connection-failure, clear the cached version so the next attempt re-resolves instead of retrying a known-rejected value" `[VERIFIED: .planning/research/PITFALLS.md:18-19]`. Mechanism: fetch `https://web.whatsapp.com/sw.js`, match `/\\?"client_revision\\?":\s*(\d+)/`, return `[2, 3000, +clientRevision]` with bundled fallback `const baileysVersion = [2, 3000, 1043857760]` `[CITED: …/Baileys/0af2386/src/Utils/generics.ts]`.

### Anti-Patterns to Avoid
- **Treating the QR string as a durable credential:** only a verified `pair-success` creates a session; rotate on expiry, never cache a code as "logged in" `[VERIFIED: .planning/research/PITFALLS.md:287]`.
- **Keeping the `wa-probe` open definition:** "Open" must mean `<success>` was received and credentials are durable — otherwise `ConnectionState.Open` lies to hosts and to `02`'s reconnect loop.
- **Adding pairing states to `ConnectionState`:** breaks the locked 4-state transition rule (`Connecting → Open → Closing → (Connecting | LoggedOut)`) `[VERIFIED: ConnectionState.cs:10-14]`.
- **A second backoff/retry table for pairing:** add verdicts to `ReconnectPolicy.Decide` only.
- **Silently retrying 405:** unknown-code default is `RetryAfter` — a stale client loops forever (ban-safety + operator blindness).
- **Copying `pair.go`/`pair-code.go` structures:** MPL-2.0 — write independently, cite behavior `[VERIFIED: NOTICE.md:13-21]`.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| AES-256-CTR for the link-code ephemeral wrap | a new block cipher / a crypto package dependency | compose CTR over in-box `Aes` (ECB block encrypt + 16-byte BE counter) + known-answer tests | `CipherMode.Ctr` doesn't exist in .NET `[VERIFIED: learn.microsoft.com … ciphermode]`; the *composition* is small and testable, the *primitive* is not |
| PBKDF2 / HKDF / HMAC / AES-GCM | custom KDFs, homemade MAC | `Rfc2898DeriveBytes.Pbkdf2`, `HKDF`, `HMACSHA256`, `AesGcm` | Constant-time, audited, AOT-safe; crypto bugs are silent and catastrophic |
| X25519 DH + Ed25519 verify for adv/key-bundle secrets | manual curve math | `NSec.Cryptography` (already pinned) | Same — and `SharedSecret` needs secure-memory disposal |
| `ClientPayload` / ADV protobuf encoding | hand-written varint encoding | `Google.Protobuf` + hand-written `.proto` with provenance rows | Byte-parity with protoc-based peers is the debug lifeline |
| QR rendering | a QR encoder in the core | host rendering, or `QRCoder` in samples only | SDK contract: emit the payload string; keep the core dependency-free |
| Retry/backoff during pairing | a pairing-specific backoff loop | `ReconnectPolicy.Decide` + `ConnectionOptions` bounds | One policy table; `Polly` remains media/HTTP-only |
| Durable credential writes | ad-hoc retry loops in pairing code | `DurableWriteRetryPolicy` + store contract (D-06) | Store durability semantics already exist and are contract-tested |
| Lifecycle event dispatch | parallel event buses | existing single-writer `Emit`/`StateChanged` pattern in `WaConnection` | "single-writer dispatch + Interlocked guard" is already the house rule `[VERIFIED: ConnectionState.cs:12-14]` |

**Key insight:** pairing is *protocol composition over audited primitives* — the exact split the project already draws ("hand-roll protocol composition, never primitives"). The only genuinely hand-built crypto is AES-CTR mode composition, which has a crisp known-answer test strategy (RFC 3686 vectors) and no in-box alternative.

## Common Pitfalls

### Pitfall 1: 405 falls into the "unknown code → bounded retry" bucket
**What goes wrong:** stale-client rejections (`<failure reason=405>`) reconnect-loop instead of surfacing `client_outdated`; the host never learns pairing is broken, and the loop hammers the server (ban-safety violation).
**Why it happens:** `ReconnectPolicy.Decide` returns Terminal only for `401 or 403 or 411 or 419` and sends everything else to `RetryAfter` `[VERIFIED: ReconnectPolicy.cs:144-150]`; `DisconnectReason` has no 405 member `[VERIFIED: DisconnectReason.cs:32-94]`.
**How to avoid:** add `ClientOutdated = 405` + `405 => ReconnectDecision.Terminal.Instance`, emit `client_outdated`, clear the cached WA version, and unit-test the switch arm next to the existing `ReconnectPolicyTests`.
**Warning signs:** repeated 405s in logs; "QR never appears but WebSocket connects" `[VERIFIED: PITFALLS.md:24]`.

### Pitfall 2: Correlation still speaks the Phase 2 skeleton while pairing speaks BinaryNode
**What goes wrong:** `pair-device`/`pair-success`/`notification`/`<success>` stanzas arrive as BinaryNodes with no `q-{n}` id envelope → they're dropped or misparsed; live pairing can never complete even though loopback tests pass.
**Why it happens:** `QueryRouter` was scoped to defer node-id correlation: "migrating correlation onto binary-node id attributes waits for real node traffic in Phase 4" `[VERIFIED: QueryRouter.cs:22-25]` — Phase 3 *is* that traffic.
**How to avoid:** make the pull-forward an explicit plan item (deviation), migrate pump + loopback fake peer **together** (as the doc requires), keep the router payload-agnostic, and gate on golden node fixtures both directions.
**Warning signs:** fake-server pairing tests pass while any live attempt hangs after message C.

### Pitfall 3: `Open` is declared before `<success>` (or credentials aren't durable yet)
**What goes wrong:** hosts observe `Open`, then the process dies before the credential row is durable → restart re-pairs (CONN-03 fails), or the server sends 515 right after scan and the client treats it as a failure instead of "reconnect with persisted state".
**Why it happens:** today's open flow calls `Emit(ConnectionState.Open …)` on the probe reply `[VERIFIED: WaConnection.cs:693-694]`; nothing ties `Open` to `<success>` or to a durable store write.
**How to avoid:** redefine first-open as "received `<success>` **and** credential write observed by a subsequent read" (D-06), treat 515 as immediate re-handshake with persisted state `[VERIFIED: DisconnectReason.cs:88-94]`, and test the kill/restart sequence in CI with the fake server.
**Warning signs:** `Open` emitted while `registered == false`; restart tests that re-enter the QR path.

### Pitfall 4: Link-code crypto constants drift (silent phone-side rejection)
**What goes wrong:** wrong alphabet (`I/L/O/U` present), wrong iteration count, wrong byte layout, or ASCII-vs-UTF8 mismatch → the phone never completes linking and the SDK sees only a `timeout` with no diagnostic.
**Why it happens:** the constants live in one upstream line each: `base32.NewEncoding("123456789ABCDEFGHJKLMNPQRSTVWXYZ")`, `pbkdf2.Key([]byte(encodedLinkingCode), salt, 2<<16, 32, sha256.New)`, 80-byte wrap = `salt[0:32] ‖ iv[32:48] ‖ ct[48:80]` `[CITED: …/whatsmeow/main/pair-code.go]`.
**How to avoid:** table-driven KATs for alphabet/encode/PBKDF2/CTR-wrap and node-shape assertions for `companion_hello`/`companion_finish` children; log (redacted) stage transitions so a silent rejection is attributable.
**Warning signs:** `timeout` in code mode with no `link_code_pairing_ref` response.

### Pitfall 5: Credential/secret leakage through logs and events
**What goes wrong:** QR payload (contains noise + identity publics **and the adv secret**) and the pairing code end up at DEBUG level → session theft/account takeover via the host's log pipeline.
**Why it happens:** pairing is the most log-happy phase (every stage is diagnostic-worthy) and QR strings look like opaque URLs.
**How to avoid:** structured logging with an explicit redaction helper for QR payload/code/key material; document that pairing code and QR payload are single-use secrets; never put secrets in event args beyond the payload string the host must render (which is itself the secret — document that).
**Warning signs:** QR string or 8-char code present in shipped logs. `[VERIFIED: PITFALLS.md:316]`

### Pitfall 6: Pair-success accepted without verification, or verification without durability
**What goes wrong:** (a) trusting `pair-success` → forged/unauthenticated identity persisted; (b) verifying then crashing before the confirmation iq + durable write → server-side paired, client-side empty → re-pair loop.
**Why it happens:** the verification chain is multi-step (HMAC over `details` with `advSecretKey` → account signature → device signature → reply `pair-device-sign`) `[CITED: …/whatsmeow/main/pair.go]`.
**How to avoid:** fail closed on any mismatch (send pair-error, disconnect, emit `timeout`/error — never a partial `success`); order = verify → persist (awaited) → reply → emit `success`; unit-test each failure branch.
**Warning signs:** `success` emitted with a store row that a second read cannot observe.

## Code Examples

### 1. QR payload construction + rotation (CONN-01)
```csharp
// Source: https://baileys.wiki/authentication/qr-code.md (docs) +
//  https://raw.githubusercontent.com/WhiskeySockets/Baileys/0af2386/src/Utils/companion-reg-client-utils.ts
//  return 'https://wa.me/settings/linked_devices#' +
//         [ref, noiseKeyB64, identityKeyB64, advB64, getCompanionPlatformId(browser)].join(',')
//  https://raw.githubusercontent.com/tulir/whatsmeow/main/pair.go (makeQRData, same format)
public static string Build(string pairingRef, ReadOnlySpan<byte> noisePub,
                           ReadOnlySpan<byte> identityPub, ReadOnlySpan<byte> advSecret,
                           string platformId) =>
    "https://wa.me/settings/linked_devices#" + string.Join(',',
        pairingRef, Convert.ToBase64String(noisePub), Convert.ToBase64String(identityPub),
        Convert.ToBase64String(advSecret), platformId);

// Rotation (whatsmeow qrchan.go): first of 6 refs → 60s, subsequent → 20s; exhausted → timeout.
TimeSpan RotationFor(int index, int batchCount) =>
    index == 0 && batchCount == 6 ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(20);
```

### 2. Link-code wrap (CONN-02) — 5 bytes → 8 chars, PBKDF2, CTR
```csharp
// Source: https://raw.githubusercontent.com/tulir/whatsmeow/main/pair-code.go (behavioral reference)
//   var linkingBase32 = base32.NewEncoding("123456789ABCDEFGHJKLMNPQRSTVWXYZ")
//   linkingCode := random.Bytes(5); encodedLinkingCode = linkingBase32.EncodeToString(linkingCode)
//   linkCodeKey := pbkdf2.Key([]byte(encodedLinkingCode), salt, 2<<16, 32, sha256.New)
//   ephemeralKey = salt(32) || iv(16) || AES-CTR-encrypted ephPub(32)   // 80 bytes total
private const string LinkAlphabet = "123456789ABCDEFGHJKLMNPQRSTVWXYZ"; // no I/L/O/U, no padding (40 bits = 8 chars)

byte[] codeBytes = RandomNumberGenerator.GetBytes(5);
string code = CrockfordBase32(codeBytes);                    // exactly 8 chars
byte[] key = Rfc2898DeriveBytes.Pbkdf2(
    Encoding.ASCII.GetBytes(code), salt32, iterations: 131072, HashAlgorithmName.SHA256, outputLength: 32);
byte[] wrapped = new byte[80];                               // salt ‖ iv ‖ AES-256-CTR(ephPub)
AesCtrXor(key, iv16, ephPub, destination: wrapped.AsSpan(48)); // CTR composed over in-box Aes (ECB blocks)
string display = string.Concat(code.AsSpan(0, 4), "-", code.AsSpan(4, 4)); // "XXXX-XXXX"
```

### 3. adv secret + key bundle (CONN-02, notification stage)
```csharp
// Source: https://raw.githubusercontent.com/tulir/whatsmeow/main/pair-code.go
//   advSecret := hkdfutil.SHA256(append(ephemeralSharedSecret, identitySharedKey...)+advSecretRandom,
//                                nil, []byte("adv_secret"), 32)
//   keyBundleEncryptionKey := hkdfutil.SHA256(ephemeralSharedSecret, keyBundleSalt,
//                                []byte("link_code_pairing_key_bundle_encryption_key"), 32)
byte[] advSecret = HKDF.DeriveKey(HashAlgorithmName.SHA256,
    ikm: Concat(ephShared, identityShared, advRandom32), salt: null,
    info: Encoding.UTF8.GetBytes("adv_secret"), hashLengthInBytes: 32);

byte[] bundleKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, ephShared, keyBundleSalt,
    Encoding.UTF8.GetBytes("link_code_pairing_key_bundle_encryption_key"), 32);
// plaintext = ourIdentityPub ‖ primaryIdentityPub ‖ advRandom32 → AES-GCM, nonce 12B,
// wire = keyBundleSalt(32) ‖ nonce(12) ‖ ciphertext
```

### 4. Verification + confirmation ordering (CONN-01 `success`)
```csharp
// Source: https://raw.githubusercontent.com/tulir/whatsmeow/main/pair.go
//   h := hmac.New(sha256.New, advSecretKey); h.Write(details)   // + AdvAccountSignaturePrefix {6,0}
//   if !hmac.Equal(h.Sum(nil), container.HMAC) { sendPairError(401); return Err... }
//   prefixes: AdvAccountSignaturePrefix={6,0} AdvDeviceSignaturePrefix={6,1}
//             AdvHostedAccountSignaturePrefix={6,5} AdvHostedDeviceSignaturePrefix={6,6}
byte[] hmac = HMACSHA256.HashData(advSecretKey, WithPrefix(accountType, details));
CryptographicOperations.FixedTimeEquals(hmac, container.Hmac)  // fail → pair-error + disconnect, no event
// verify account sig over prefix(6,0|6,5) ‖ details ‖ ourIdentityPub   (NSec Ed25519 verify)
// sign device sig over prefix(6,1) ‖ details ‖ ourIdentityPub ‖ accountSignatureKey
// → persist (awaited) → reply iq result { pair-device-sign / device-identity / key-index } → emit success
```

### 5. 405 policy extension (CONN-01 `client_outdated`)
```csharp
// Existing (verified verbatim) — src/.../Connection/ReconnectPolicy.cs:144-150
return statusCode switch
{
    401 or 403 or 411 or 419 => ReconnectDecision.Terminal.Instance,
    515 => ReconnectDecision.RestartImmediate.Instance,
    440 => ReconnectDecision.SurfaceAndStop.Instance,
    _ => new ReconnectDecision.RetryAfter(ComputeDelay(attempt, resolved, Random.Shared.NextDouble())),
};
// Phase 3 adds:  ClientOutdated = 405  (DisconnectReason) and
//   405 => ReconnectDecision.Terminal.Instance,
// then the coordinator maps Terminal+405 → typed `client_outdated` and clears the version cache.
```

### 6. Registration vs login payload (CONN-03 resume)
```csharp
// Source: https://raw.githubusercontent.com/WhiskeySockets/Baileys/0af2386/src/Utils/validate-connection.ts
// registration (no stored creds):
//   devicePairingData { buildHash = md5("x.y.z"), deviceProps = DeviceProps,
//     eRegid = BE(registrationId) /* 14-bit */, eKeytype = KEY_BUNDLE_TYPE, eIdent = identityPub,
//     eSkeyId = BE(signedPreKey.keyId,3), eSkeyVal, eSkeySig }, passive=false, pull=false
// login (stored creds): passive=true, pull=true, username=<user>, device=<device>
// both wrapped in ClientPayload { connectType=WIFI_UNKNOWN, connectReason=USER_ACTIVATED, userAgent, webInfo }
ClientPayload payload = hasStoredCredentials
    ? _factory.Login(jid.User, jid.Device)     // resume: same session, no re-pair
    : _factory.Registration(identity, signedPreKey, registrationId, version);
// returned from INoiseHandshakePayloads.GetClientFinish() → encrypted into Noise message C
```

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The live WA server's pairing flow matches Baileys@`0af2386` + whatsmeow `main` behavior (no live interop run this session — no phone/account in CI) | Summary, all patterns | **High** — field/attr drift would fail only at live pairing; mitigate with golden fixtures + end-of-phase human proof |
| A2 | Hand-written `ClientPayload`/ADV/`DeviceProps` `.proto` schemas (derived from Baileys field roles, never whatsmeow bytes) will be accepted byte-for-byte by the server | Standard Stack, Open Question 4 | **High** — protobuf field-number error → registration rejected (400/405); mitigate with cross-implementation fixtures + live proof |
| A3 | Pairing-code validity window ≈160s (inferred from the QR ref window: `60 + 5×20`), so `RequestPairingCode` should be called right after connect | Pitfall 4, CONN-02 | Medium — code may expire sooner/later; surface a typed timeout instead of assuming |
| A4 | Our `logged_out` pairing event derives from connect-failure reasons 401/403/406 (whatsmeow QR vocabulary has no `logged_out`) | Pattern 2 table | Medium — event set differs slightly from upstream; requirement is ours, so define + document explicitly |
| A5 | `client_revision` extraction from `https://web.whatsapp.com/sw.js` still works today (mechanism verified in Baileys source; live fetch not run this session) | Pattern 4 | Medium — if blocked/changed, fall back to bundled pin + log loudly; 405 handling covers drift |
| A6 | QRCoder may be added to a **samples-only** project for the human QR proof; core stays dependency-free | Standard Stack | Low — if the planner skips the sample, print the raw payload string instead |
| A7 | `IsLoggedOut()` reason set (401/403/406) — taken from a cached research digest, not re-read from `types/events/events.go` this session | Pattern 2, Pitfall 1 | Low — extra/missing reason only widens/narrows `logged_out`; confirm during implementation |

## Open Questions (RESOLVED)

1. **Pull BinaryNode correlation forward from Phase 4 into Phase 3?**
   - What we know: `QueryRouter` documents the skeleton envelope and defers migration "to Phase 4, when the loopback peer and pump can move together" `[VERIFIED: QueryRouter.cs:22-25]`; live pairing requires node-tag dispatch + id-attribute correlation.
   - What's unclear: whether the planner treats this as a planned scope pull-forward (deviation entry) or limits Phase 3 to loopback-only pairing proofs.
   - RESOLVED: Recommendation: **pull it forward** as an explicit deviation with its own task (pump + loopback peer migrate together, router stays payload-agnostic, golden node fixtures added) — CONN-01/02/03 cannot be proven live otherwise.

2. **Where does runtime version resolution live?**
   - Known: PITFALLS assigns it to this phase `[VERIFIED: PITFALLS.md:28]`; mechanism is ~30 lines with a 6h cache.
   - Unclear: whether Phase 3 scope includes it or defers to a drift/release phase.
   - RESOLVED: Recommendation: include it (it is the difference between "QR never appears" and working pairing weeks later), behind `ConnectionOptions` so tests inject a fixed version.

3. **Event surface shape: separate pairing events vs. widening `StateChanged`?**
   - RESOLVED: Recommendation: **separate `PairingStateChanged` (or coordinator events)** — keeps the locked 4-state `ConnectionState` vocabulary intact and lets hosts that only care about connection state ignore pairing entirely. Planner should lock the exact API (event args vs. `IAsyncEnumerable` vs. callback) since it is public surface tracked in `PublicAPI.Unshipped.txt` `[VERIFIED: src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt:72-73]`.

4. **Proto schema provenance approach for `ClientPayload`/ADV/CompanionReg**
   - Known: `handshake.proto` precedent — hand-written, citing Baileys field roles read-only, "No whatsmeow bytes were involved" `[VERIFIED: handshake.proto:3-17]`.
   - RESOLVED: Recommendation: same approach + NOTICE.md provenance rows; never open whatsmeow `proto/*.proto` files as copy sources (MPL-2.0).

5. **How much of CONN-01/02/03 is CI-provable vs. human-gated?**
   - Known: `human_verify_mode: "end-of-phase"` `[VERIFIED: .planning/config.json:33]`; no WhatsApp account/phone in CI; loopback harness exists (`LoopbackFrameTransport`, fake-server pairing scripts needed).
   - RESOLVED: Recommendation: CI = scripted fake-server pairing (QR + code paths, kill/restart resume, injected drop) + crypto KATs + node golden fixtures; human end-of-phase = real QR scan, real 8-char code entry, real restart-without-re-pair.

6. **Should `RequestPairingCodeAsync` be per-account public API on the connection, or on the coordinator?**
   - RESOLVED: Recommendation: on the pairing surface, taking a digits-only E.164-without-`+` number (validated: digits only, length > 6, no leading `0` `[CITED: …/whatsmeow/main/pair-code.go]`), returning the `XXXX-XXXX` display string.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | build/test | ✓ | **10.0.401** (checked this session) | — |
| NuGet restore (existing pins) | build | ✓ (Phase 1/2 restored) | — | — |
| nuget.org (QRCoder, optional sample) | sample QR rendering | ✓ (registry reachable — flatcontainer/registration fetched this session) | 1.8.0 | print raw payload string; host renders |
| `web.whatsapp.com` (version `sw.js` + live WS) | runtime version resolution + live pairing | ✗ in CI (no egress/credentials assumed) | — | injected `ConnectionOptions` version + bundled pin; live proofs → human verification |
| Live WhatsApp account + phone | CONN-01/02/03 end-to-end proofs | ✗ in CI | — | **fallback exists:** scripted loopback fake server (`LoopbackFrameTransport`) + golden fixtures; real proofs are `human_verify_mode: end-of-phase` |
| `gsd-tools query package-legitimacy` | NuGet audit | ✗ (npm/pypi/crates only — error reproduced this session) | — | direct NuGet registration + GitHub API verification (done) |

**Missing dependencies with no fallback:** none blocking — the only non-CI-able items (live pairing) have a human verification slot by config.
**Missing dependencies with fallback:** live WhatsApp network (→ loopback + human proof), package-legitimacy seam for NuGet (→ registry/API verification).

## Validation Architecture

> `workflow.nyquist_validation: true` `[VERIFIED: .planning/config.json:25]` — section included.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 **4.0.1** + Microsoft.NET.Test.Sdk **18.10.1** + AwesomeAssertions **9.6.0** + NSubstitute **6.2.0** `[VERIFIED: Directory.Packages.props:21-26]` |
| Config file | none (SDK-style csproj + central package management) |
| Quick run command | `dotnet test Iwasug.WhatsApp.Sdk.sln` `[VERIFIED: .planning/config.json:54]` (also the full suite) |
| Build command | `dotnet build Iwasug.WhatsApp.Sdk.sln --nologo` `[VERIFIED: .planning/config.json:53]` |
| Fast subset (suggested) | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~Pairing"` |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|--------------|
| CONN-01 | QR payload format + 60s/20s rotation + 160s `timeout` | unit (FakeTimeProvider) | `dotnet test --filter "FullyQualifiedName~PairingQr"` | ❌ Wave 0 |
| CONN-01 | Typed event sequence `code→success` / `code→timeout` / `→client_outdated(405)` / `→logged_out` on scripted fake server | integration (loopback) | `dotnet test --filter "FullyQualifiedName~PairingLifecycle"` | ❌ Wave 0 |
| CONN-01 | `ReconnectPolicy` maps 405 → Terminal | unit | `dotnet test --filter "FullyQualifiedName~ReconnectPolicyTests"` | ✅ exists, needs new case |
| CONN-02 | 5B→8C code, alphabet, `XXXX-XXXX`, PBKDF2/CTR/HKDF KATs, hello/finish node shapes | unit + KAT | `dotnet test --filter "FullyQualifiedName~LinkCode"` | ❌ Wave 0 |
| CONN-02 | Phone-number validation (digits, >6, no leading 0) | unit | same filter | ❌ Wave 0 |
| CONN-03 | pair → dispose → reload from store → login payload → `<success>` → stays up through injected drop + 515 | integration (loopback) | `dotnet test --filter "FullyQualifiedName~Resume"` | ❌ Wave 0 |
| CONN-03 | Credential store contract (durability D-06, failure≠missing, idempotent remove) | contract kit | `dotnet test --filter "FullyQualifiedName~CredentialStore"` | ❌ Wave 0 (pattern exists for 5 stores) |
| all | Public API surface additions tracked | analyzer gate | included in build | ✅ `PublicAPI.Unshipped.txt` |

### Sampling Rate
- **Per task commit:** `dotnet build Iwasug.WhatsApp.Sdk.sln --nologo` + filtered `dotnet test` (<30s)
- **Per wave merge:** `dotnet test Iwasug.WhatsApp.Sdk.sln` (full)
- **Phase gate:** full suite green + NOTICE.md provenance rows added → then `/gsd-verify-work` (human QR/code/restart proofs)

### Wave 0 Gaps
- [ ] `tests/…/Pairing/` — QR payload, rotation timers, lifecycle event sequences, code-gen + crypto KATs (REQ CONN-01/02)
- [ ] `tests/…/Connection/ResumeTests.cs` — restart-without-re-pair + injected-drop survival (CONN-03)
- [ ] `src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/CredentialStoreContractTests.cs` + InMemory/Scratch implementations (completeness backstop `ComplianceTestBase` requires a concrete subclass per contract class `[VERIFIED: tests/Iwasug.WhatsApp.Sdk.Tests/ComplianceTests.cs:5-11]`)
- [ ] Scripted fake-server pairing peer (extends existing loopback: `pair-device` refs, `pair-success`, `<success>`, `<failure reason=405>`, `link_code_companion_reg` notification)
- [ ] Golden node fixtures: registration/login `ClientPayload` bytes, `companion_hello`/`companion_finish` nodes, `pair-success` container (cross-checked against upstream encodings, **no upstream bytes copied into the repo**)
- [ ] No framework install required (xunit v3 already wired)

## Security Domain

> `security_enforcement: true`, `security_asvs_level: 1`, `security_block_on: "high"` `[VERIFIED: .planning/config.json:49-51]`.

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|------------------|
| V2 Authentication | **yes** | Pairing *is* device authentication: advSecret HMAC + account/device signature verification, fail-closed before any credential write; no credentials in URLs other than the QR payload the host must render (documented as a single-use secret) |
| V3 Session Management | partial | Credential row lifecycle (create at pair-success, load on start, delete on logged-out 401/403/406); at-rest protection is the **host's** responsibility — document loudly (`auth folders … equivalent to an SSH private key` `[CITED: baileys.wiki/authentication/session-management.md]`) |
| V4 Access Control | partial | Per-account isolation: the store instance *is* the namespace (D-02) — pairing state must never cross accounts; no cross-account event leakage |
| V5 Input Validation | **yes** | Fail-closed parsing of server nodes (existing depth/size caps), phone-number validation (digits, length, no leading 0), QR ref / platform-id validation, `AccountId`/JID validation reuse |
| V6 Cryptography | **yes** | In-box PBKDF2/HKDF/HMAC/AES-GCM; NSec X25519/Ed25519; AES-CTR composed over in-box `Aes` with KATs; **never** hand-rolled primitives; key material disposed/zeroized (existing `SharedSecret` discipline) |

### Known Threat Patterns for this stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Forged `pair-success` (attacker-controlled node) → attacker-bound credentials | Tampering | Verify HMAC with stored `advSecretKey` + account signature over `details` + our identity pub; mismatch → pair-error + disconnect, no `success` event |
| Stale client accepted then rejected → 405 reconnect loop | DoS / Elevation of bundled risk | `ClientOutdated=405 → Terminal` + version re-resolution (ban-safety: no loop) |
| Secret exfiltration via logs (QR payload, pairing code, store rows) | Information Disclosure | Redaction helper at every pairing log site; document DEBUG-unsafe-in-prod `[VERIFIED: PITFALLS.md:316]` |
| Partial credential persistence (verify ok, write lost) → re-pair / desync | Reproducibility / Integrity | Persist-before-continue (D-06), ordered steps, store contract tests; never emit `success` before the write is observed |
| Replay/rollback of a captured `companion_hello` ref or code | Spoofing | Refs are single-use per connection; pairing state is per-account and cleared on disconnect; codes expire with the QR window |
| Malformed/huge pairing nodes (evil lengths, deep nesting) | Tampering / DoS | Existing decoder caps (`MaxFrameBytes`, depth 64) + fail-closed abort |
| Cross-account credential bleed in multi-tenant hosts | Elevation of Privilege | Account-scoped store instances; pairing coordinator instantiated per account |

## Sources

### Primary (HIGH confidence)
- `api.nuget.org` flatcontainer + registration5-semver1 (`qrcoder`) — version 1.8.0, publish 2026-04-04, first publish 2016-05-16, MIT — fetched this session
- `api.github.com/search/repositories` — `Shane32/QRCoder` (MIT, 5167★, pushed 2026-09-11) — fetched this session
- `learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.ciphermode` (net-10.0 Fields table: CBC 1, ECB 2, OFB 3, CFB 4, CTS 5) — fetched this session
- In-repo reads this session: `Noise/NoiseHandshake.cs:1-45`, `Connection/WaConnection.cs:640-709,900-954`, `Connection/ReconnectPolicy.cs:110-169`, `Connection/DisconnectReason.cs:1-95`, `Connection/ConnectionState.cs:1-48`, `Connection/QueryRouter.cs:1-40`, `Stores/StoreRegistryOptions.cs:1-52`, `Stores/ISafetyStateStore.cs:1-74`, `Proto/handshake.proto:1-59`, `Transport/FrameReader.cs:11-148` (via grep), `PublicAPI.Unshipped.txt:1-341`, `Directory.Packages.props:1-47`, `NOTICE.md:1-50`, `.planning/config.json:1-97`, `.planning/REQUIREMENTS.md:1-30`, `.planning/ROADMAP.md:70-109`, `.planning/research/PITFALLS.md:1-30` (+grep), `.planning/phases/02-…/02-SPIKE-NOTES.md:1-167`, `tests/…/ComplianceTests.cs:1-12`

### Secondary (MEDIUM confidence)
- Baileys (MIT) @ `0af2386`, fetched this session: `src/Utils/companion-reg-client-utils.ts` (QR payload), `src/Utils/validate-connection.ts` (registration/login payload + `configureSuccessfulPairing`), `src/Utils/generics.ts` (`sw.js` revision fetch, `baileysVersion`, Crockford alphabet)
- Baileys official docs, fetched this session: `baileys.wiki/authentication/qr-code.md`, `baileys.wiki/authentication/pairing-code.md`, `baileys.wiki/authentication/session-management.md`
- whatsmeow (MPL-2.0 — **behavioral reference only, no code copied**), fetched this session: `qrchan.go` (event vocabulary + 60/20s timers), `pair-code.go` (link-code crypto/stages), `pair.go` (QR data + pair-success verification + prefixes), `handshake.go` (ClientPayload in message C), `client.go` (`success`/`failure` handler registration), `connectionevents.go` (405 → ClientOutdated, logged-out handling)
- Cross-checked claims: QR format, pair-success verification chain, and lifecycle vocab each appear in **two independent implementations** (Baileys + whatsmeow), which is what lifts these to MEDIUM rather than LOW

### Tertiary (LOW confidence)
- Cached research digest `.planning/research/.cache/75f40028….json` — logged-out reason set 401/403/406 and issue-tracker-derived failure modes (not re-verified against `types/events/events.go` this session) → carried into Assumptions A4/A7
- `PITFALLS.md` upstream issue references (whatsmeow #1191/#1066, Baileys #2248/#1914) — inherited from prior research, used for pitfall narrative only

## Metadata

**Confidence breakdown:**
- Standard Stack: **HIGH** — every version re-read from `Directory.Packages.props` or fetched from the NuGet registry this session; the only CTR conclusion is grounded in Microsoft's own enum docs
- Architecture: **MEDIUM** — patterns derive from official sources cross-checked across two implementations, but no live-server observation; seam/pull-forward decisions are flagged as Open Questions
- Pitfalls: **MEDIUM** — in-repo `PITFALLS.md` (prior cross-checked research) + verified current code state (405 gap, probe-open, QueryRouter deferral)

**Seam note:** `query classify-confidence` returns LOW for `webfetch`/`github`/`microsoft-docs` provider ids (executed this session) and MEDIUM for `websearch --verified`; claims above are therefore tagged by *source authority* per the provenance rules — first-party registry/MS-docs/in-repo file reads → `[VERIFIED]`, official upstream project source/docs → `[CITED]`, unverified memory/derived sets → `[ASSUMED]`. Research digests for this phase were cached via `query research-store put` (4 keys: QR payload/rotation, link-code flow, connect success/failure + 405, .NET CipherMode).

**Research date:** 2026-10-09
**Valid until:** ~2026-11-08 (30 days — protocol behavior is stable, but upstream WA version churn is continuous; re-verify `sw.js` revision mechanism and Baileys `master` before execution if more than ~2 weeks pass)
