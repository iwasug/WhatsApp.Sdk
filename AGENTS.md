<!-- GSD:project-start source:PROJECT.md -->

## Project

**WhatsApp SDK for .NET (sdk-wa)**

An unofficial, open-source .NET 10 library (published to NuGet) that implements the WhatsApp Web multi-device protocol end-to-end — comparable in capability to whatsmeow (Go) — for building customer service and broadcast integrations. It links real WhatsApp accounts via QR pairing, exchanges end-to-end encrypted messages, and is designed from the ground up to run many concurrent accounts safely.

**Core Value:** Reliably send and receive messages through a linked WhatsApp account — across many accounts at once, without getting those accounts banned.

### Constraints

- **Tech stack**: .NET 10 LTS, C# — current LTS, longest support runway
- **State**: pluggable store interface — many accounts in production means the host owns persistence (EF Core, Dapper, files, etc.)
- **Ban safety**: technical + operational mitigations are v1 requirements, not future work — decided during questioning
- **License**: open source, published publicly — must avoid copying MPL-2.0 code from whatsmeow; reference protocol behavior and public docs instead
- **Compliance**: usage must respect WhatsApp ToS; SDK documents the risks it mitigates and the risks it cannot

<!-- GSD:project-end -->

<!-- GSD:stack-start source:research/STACK.md -->

## Technology Stack

## Recommended Stack

### Core Technologies

| Technology | Version | Purpose | Why Recommended | Confidence |
|------------|---------|---------|-----------------|------------|
| .NET SDK / runtime | SDK **10.0.401**, runtime **10.0.12**, C# 14 | Target framework `net10.0` | .NET 10 is the active **LTS** channel (EOL **2028-11-14**); matches PROJECT.md constraint. 10.0.12 is the current security servicing release (2026-09-08). Verified via first-party `release-notes/10.0/releases.json`. | HIGH |
| `System.Net.WebSockets.ClientWebSocket` (in-box) | ships with .NET 10 | WebSocket transport to `wss://web.whatsapp.com/ws/chat` | The in-box client supports everything needed: custom request headers (WA requires an `Origin` header), `KeepAliveInterval`/`KeepAliveTimeout` (added .NET 9), full control over connect/reconnect lifecycle. Every third-party wrapper abstracts exactly the control this protocol needs (see What NOT to Use). New .NET 10 `WebSocketStream` (a `Stream` wrapper over `WebSocket`) is worth a spike for `PipeReader` integration, but it's too new to build on blindly. | HIGH |
| `System.IO.Pipelines` | **10.0.12** (NuGet) | Binary frame parsing (WA framing: 2-byte big-endian length prefixes over WS binary messages, plus deferred/deferred-frames handling) | Zero-copy `PipeReader` parsing of a long-lived binary stream is the canonical Pipelines use case; avoids the buffer-management churn of `ReceiveAsync` into rented arrays. Package reference works from plain class libs (also ships in ASP.NET Core shared framework). | HIGH |
| `Google.Protobuf` + `Grpc.Tools` | **3.36.2** runtime, **2.84.0** codegen | Serialize/deserialize WhatsApp `.proto` schemas | Official protoc + official runtime = byte-exact wire semantics identical to what whatsmeow (`google.golang.org/protobuf`) and Baileys (`protobuf.js`) produce, which you will rely on heavily for byte-level cross-checking during protocol bring-up. Stable 3.36.2 now; a 4.0.0-rc2 prerelease exists — do **not** adopt it yet. `Grpc.Tools` runs `protoc` at build time (`PrivateAssets=all`, no runtime dep). | HIGH |
| `NSec.Cryptography` | **26.4.0** | X25519 key agreement, Ed25519 signatures (and optionally ChaCha20-Poly1305/Blake2b) | The one genuine gap in .NET's crypto box: **X25519 and Ed25519 are not in-box on net10.0** (`System.Security.Cryptography.X25519` doc 404s; .NET 10 what's-new lists only AES KeyWrap-with-Pad + post-quantum additions). NSec is MIT-licensed, actively versioned (26.4.0 = current), built on audited libsodium, Span-based, stores keys in libsodium secure memory, and provides the typed-key API that prevents algorithm mixups — exactly right for a multi-account crypto SDK. Same author as libsodium-core. | HIGH (versions/absence), MEDIUM-HIGH (X25519 absence itself — corroborated by docs 404 + absence from .NET 10 release notes) |
| In-box `System.Security.Cryptography` | .NET 10 | AES-256-CBC (Signal message bodies, media encryption), HMAC-SHA256/512, HKDF (`HKDF` class), SHA-2, `RandomNumberGenerator`, **`ChaCha20Poly1305`** (confirmed in-box for net10.0) | Signal's ratchet chains, X3DH KDF, media key derivation, and Noise's hash/HMAC are all covered by the BCL — no third-party dependency for any primitive except X25519/Ed25519. In-box = platform-maintained, AOT/trimming-safe, zero licensing friction. | HIGH |
| Hand-rolled **Noise XX** state machine | ~400–600 LoC over NSec | `Noise_XX_25519_ChaChaPoly_SHA256` handshake (WA variant) | **Decision: hand-roll the state machine, do NOT take a Noise library dependency.** Evidence: (1) whatsmeow itself hand-rolls Noise — `handshake.go`, no noise package in `go.mod`; (2) the only real .NET option, Noise.NET 1.0.0, is dormant since 2020-12, targets netstandard1.3, and P/Invokes a stale native `libsodium` 1.0.16 package that will collide with NSec's bundled libsodium in the same process; (3) WA's usage has protocol quirks (prologue bytes, `WA` header framing, retry) that sit *outside* any generic library anyway; (4) Noise XX's symmetric-state is small, fully specified (spec rev 33), and objectively testable against the **cacophony** reference vectors — you're not hand-rolling *primitives* (libsodium does that), only well-specified composition. | MEDIUM-HIGH (design decision — validate in the Noise spike phase) |
| Hand-rolled **Signal protocol** (X3DH + Double Ratchet) | clean-room, over NSec + BCL | Session establishment, message encryption, prekey management | NuGet has **no** mature Signal library (search returns only Lucinda.Blazor, 464 downloads, browser-WebCrypto-based). In Go, whatsmeow splits this to a separate lib (`go.mau.fi/libsignal v0.2.2`) — the .NET ecosystem has no equivalent, so clean-room implementation from the public Signal spec + behavioral cross-checks against whatsmeow is the only path. Primitives are all available (NSec X25519/Ed25519; BCL AES-CBC/HMAC/HKDF). | HIGH (no library exists), MEDIUM (effort estimate) |
| `Microsoft.Extensions.Logging.Abstractions` | **10.0.12** | `ILogger` injection only | The SDK logs (connects, resumes, ban-safety events, protocol anomalies) but must not force a logging framework on host applications. Abstractions package only — the host picks Serilog/NLog/whatever. This is the standard contract for embeddable .NET libraries. | HIGH |

### Supporting Libraries

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| `System.Threading.RateLimiting` | **10.0.12** | `TokenBucketRateLimiter` per account for send/upload pacing | Always (v1 requirement: ban-safety). This is the modern in-box rate limiter (inbox since .NET 7); pair with a hand-rolled jitter + warm-up scheduler for the human-like patterns. |
| `System.Threading.Channels` | **10.0.12** (ships with .NET 10) | In-process bounded queues: inbound event dispatch, outbound send queue per account | Always. Bounded channels give you backpressure when the host event handler is slow — critical for tens of concurrent accounts. |
| `Polly.Core` | **8.8.0** | Circuit breaker + retry for HTTP-level operations (media upload/download, prekey fetch) | For media/HTTP endpoints only. **Do not** use it for WebSocket reconnection — resume requires protocol-aware logic (Noise re-handshake, stream resume tokens, jittered backoff tuned for ban-safety) that a generic retry policy can't express. License is BSD-3-Clause (verified via GitHub API), fine for OSS. | 
| `HttpClient` / `MultipartFormDataContent` (in-box) | .NET 10 | Media upload to `mmg.whatsapp.net`, download via CDN, prekey HTTP endpoints | Always; no third-party HTTP client needed. Consider `SocketsHttpHandler` pooling per account for header isolation. |
| `System.IO.Compression.ZLibStream` (in-box) | .NET 10 | zlib streams for app-state sync values and WA "stream shape" compressed payloads | When implementing app-state sync / compressed media markers. In-box since .NET 6 — never take a SharpZipLib/DeflateStream package. |
| `System.Text.Json` (in-box) | .NET 10 | Auxiliary JSON endpoints (feature configs, `.well-known` responses) | Only where the protocol actually speaks JSON; the message wire is protobuf. |
| `QRCoder` | **1.8.0** | Render QR codes **in samples and docs only** | The SDK must emit the raw QR *payload string* (event/callback) and stay dependency-free for rendering; hosts decide how to display it. QRCoder is MIT, pure C#, zero native deps. |

### Development Tools

| Tool | Purpose | Notes |
|------|---------|-------|
| `xunit.v3` | **4.0.1** — test framework | Current generation (Apache-2.0); ships its own runner (Microsoft Testing Platform). .NET 10-compatible (net8.0+). |
| `Microsoft.NET.Test.Sdk` | **18.10.1** — test host | Current stable alongside xunit v3. |
| `NSubstitute` | **6.2.0** — mocking | BSD-3-Clause. Preferred over Moq (see What NOT to Use). Use for `IStore` fakes, event-handler fakes. |
| `AwesomeAssertions` | **9.6.0** — fluent assertions | Apache-2.0 fork of FluentAssertions by the original authors — FluentAssertions 8.x went source-available/Xceed-owned. Drop-in API. |
| `coverlet.collector` | **10.1.0** — coverage | Standard choice; feeds CI coverage gates. |
| `BenchmarkDotNet` | **0.15.8** — benchmarks | For framing/codec/cipher hot paths; you'll want numbers when choosing buffer strategies. |
| `Nerdbank.GitVersioning` | **3.10.94** — package versioning | MIT. SemVer2 + git-height versions + signed reproducible builds for the NuGet package. Alternative: set version in CI manually. |
| `Microsoft.CodeAnalysis.PublicApiAnalyzers` | **5.6.0** — public API tracking | `PublicAPI.Unshipped.txt` makes every public-surface change explicit in PR review — valuable once you publish to NuGet (semver discipline). |
| SourceLink | **bundled in .NET SDK since 8.0** | Enable `EmbedUntrackedSources` + `ContinuousIntegrationBuild=true`; no package reference needed (verify via CI that the snupkg maps to GitHub). Package `Microsoft.SourceLink.GitHub` 10.0.401 exists if pinning is preferred. |

## Installation

# Core library (net10.0)

# Build-time proto codegen (PrivateAssets=all — NOT a runtime dependency)

# Tests

# Samples/docs only (not a dependency of the core SDK)

## Alternatives Considered

| Recommended | Alternative | When to Use Alternative |
|-------------|-------------|-------------------------|
| Hand-rolled Noise XX over NSec | **Noise.NET 1.0.0** (Metalnem, MIT) | Only as a *read-only reference implementation* while writing tests. Never as a shipped dependency: dormant since 2020-12-26, netstandard1.3, and it pulls native `libsodium` 1.0.16 which conflicts with NSec's bundled libsodium (`[DllImport("libsodium")]` name collision). |
| Hand-rolled Noise XX over NSec | **Torann 0.2.0** (pure managed, cacophony-verified) | Revisit if you decide a managed-only (zero native deps) build is a hard requirement — but at 370 total downloads and 2 releases, it's not mature enough to underpin E2EE today. |
| Google.Protobuf | **protobuf-net 3.4.30** | Only if you never need byte-level parity with other implementations' encodings and prefer attribute-based contracts. It's excellent tech, but .proto-first + official protoc is the safer call for a wire-compat protocol SDK. |
| NSec.Cryptography | **BouncyCastle.Cryptography 2.7.0** (MIT, pure managed) | If a consumer requirement forces **zero native binaries** (rare: unusual AOT/sandbox scenarios). You lose libsodium's audited assembly and NSec's secure-memory/typed-key model; BC also carries a much larger API surface and payload. |
| NSec.Cryptography | **Sodium.Core 1.4.1** (same author's older binding) | If NSec's typed-key abstraction ever fights you; Sodium.Core is thinner/more literal P/Invoke. NSec is the more modern, safer default. |
| In-box `ClientWebSocket` | **WebSocketStream** (.NET 10 new) | After GA shakedown, if PipeReader-over-Stream integration proves materially simpler. It's brand new in .NET 10 — don't be first on the critical path. |
| `System.Threading.RateLimiting` + hand-rolled jitter | **Polly.Core** rate limiter | If you already adopt Polly for circuit-breaking and want one resilience vocabulary. The ban-safety pacing logic (warm-up curves, human-like jitter) is bespoke regardless. |
| NSubstitute | **Moq 4.21.0** | If the team already knows Moq deeply. Note Moq's 2023 SponsorLink controversy; NSubstitute sidesteps it and is BSD-3. |
| xunit.v3 | **xunit v2 (2.9.3)** | Only if a downstream tool demands v2 — v3 is the current generation and required for .NET 10-era tooling. |
| Nerdbank.GitVersioning | **MinVer / manual CI version** | If you want zero build-time magic; NB.GV earns its keep for signed, reproducible, git-height NuGet versions. |

## What NOT to Use

| Avoid | Why | Use Instead |
|-------|-----|-------------|
| **Any code, `.proto` files, or generated output copied from tulir/whatsmeow** | whatsmeow is **MPL-2.0** — a *file-level* copyleft license. Copying its `proto/` schemas, `handshake.go`, or generated types into an MIT project triggers MPL obligations on those files and breaks your clean-room posture. PROJECT.md explicitly mandates behavioral-reference-only. | Derive schemas from permissively-licensed sources (Baileys is MIT), cross-check *behavior* against whatsmeow (read-only), and log protocol observations. Track `.proto` provenance file-by-file. |
| **`Noise.NET` as a runtime dependency** | Dormant since 2020; pins native libsodium 1.0.16 (stale, 2019-era); DLL-name collision with NSec. | Hand-rolled XX state machine over NSec, cacophony-vector tested. |
| **`protobuf-net`** | Right tool for .NET-idiomatic DTO serialization — wrong tool when byte-parity with protoc-based implementations is your debugging lifeline. | Google.Protobuf 3.36.2 + Grpc.Tools 2.84.0 |
| **`FluentAssertions` 8.x** | v8+ is Xceed-owned under a custom source-available license (no OSI SPDX on the package). An unnecessary license question mark in a published OSS SDK. | `AwesomeAssertions` 9.6.0 (Apache-2.0, original authors) |
| **`Moq`** | Viable, but the SponsorLink episode (phoning home for sponsorship data) burned trust; also one fewer permissive-license edge to reason about. | NSubstitute 6.2.0 (BSD-3) |
| **`Websocket.Client`, `Fleck`, SignalR, gRPC, SuperSocket** | All impose their own connection/reconnect/abstraction models over the raw socket. WA needs: exact header control (`Origin`, `User-Agent` browser mimicry), protocol-driven resume (Noise re-handshake + stream tokens), per-frame ping policy — every wrapper fights you here. gRPC/SignalR are wrong protocol shapes entirely (WA is a custom binary protocol over plain WS, not HTTP/2 or a signalr hub). | In-box `ClientWebSocket` + own connection state machine |
| **Hand-rolling cryptographic *primitives*** (X25519, Ed25519, ChaCha20, HMAC) | Constant-time curve math is where subtle, exploitable bugs live; you'd also own the audit burden. | NSec (libsodium, audited) / in-box BCL. Hand-roll *protocol composition* only, tested against reference vectors. |
| **`Chaos.NaCl`, old `libsodium` NuGet (1.0.16), `libsodium-nuget` wrappers** | Chaos.NaCl is unmaintained and does managed-only crypto (GC-hostile); the old `libsodium` package ships a 2019 native binary. | NSec.Cryptography 26.4.0 |
| **EF Core / Dapper / any DB inside the SDK** | Violates the pluggable-store constraint: tens of accounts means the host owns persistence. | `ISessionStore` (etc.) interfaces + optional in-memory impl for tests |
| **A logging framework dependency (Serilog etc.)** | Hosts bring their own pipeline; a concrete logger makes the SDK a bad neighbor (version conflicts, config hijacking). | `ILogger` from `Microsoft.Extensions.Logging.Abstractions` only |

## Stack Patterns by Variant

- `net10.0` only, NSec + Google.Protobuf + BCL, MIT license. Because: single modern TFM keeps the code span-friendly (`Span<T>`, `ZLibStream`, in-box ChaCha) and the dependency tree minimal.
- Swap NSec → `BouncyCastle.Cryptography` 2.7.0 (pure managed X25519/Ed25519), keep everything else. Because: only NSec pulls a native binary; BC is the credible managed fallback.
- Cost: larger payload, less ergonomic API, need own secure-memory story.
- Keep package refs on 10.0.x *only if* you verify net8-compatible assets; practically you'd pin `Microsoft.Extensions.*`/`System.*` to 8.0.x on that TFM and lose .NET 10 `WebSocketStream` anyway. Prefer `net10.0`-only until there's a proven consumer need.
- All recommended libs are AOT-safe (NSec uses source-generated marshalling; Google.Protobuf supports trimming with generated code; in-box BCL fine). Avoid reflection-based serializers. Verify with a publish trim-warning gate in CI.

## Version Compatibility

| Package | Compatible With | Notes |
|---------|-----------------|-------|
| Google.Protobuf 3.36.2 | Grpc.Tools 2.84.0 | `protoc` version bundled by Grpc.Tools generates code for runtime 3.x — do not mix with the 4.0.0-rc2 runtime until GA. |
| Google.Protobuf 3.36.x | net10.0 | Runtime is netstandard2.0; no conflicts with BCL. |
| NSec.Cryptography 26.4.0 | net10.0 | Brings its own libsodium native runtimes per-RID (win/linux/osx × x64/arm64). **Do not also reference any package that ships `libsodium` (e.g., Noise.NET, `libsodium` 1.0.16) — DllImport name collision.** |
| Microsoft.Extensions.* 10.0.12 | .NET 10 runtime 10.0.12 | Stay on the 10.0.x train; the 11.0.0-rc.1 packages visible on NuGet are for .NET 11 preview — ignore. |
| System.IO.Pipelines / System.Threading.RateLimiting / System.Threading.Channels 10.0.12 | net10.0 | Runtime-aligned servicing versions; bump together on Patch Tuesday. |
| xunit.v3 4.0.1 | Microsoft.NET.Test.Sdk 18.10.1 | xunit v3 runs its own (MTP) runner — test SDK pairing verified as current stable pair today. |
| Polly.Core 8.8.0 | net10.0 | Polly 8.x is the current API (`ResiliencePipeline`); the old Polly 7 `IAsyncPolicy` API is superseded. |

## License & Provenance (open-source NuGet constraint)

| Dependency | License (verified) |
|------------|--------------------|
| Google.Protobuf | BSD-3-Clause |
| Grpc.Tools | Apache-2.0 (build-time only) |
| NSec.Cryptography | MIT |
| Microsoft.Extensions.Logging.Abstractions | MIT |
| System.* (Pipelines, RateLimiting, Channels) | MIT (runtime packages) |
| Polly.Core | **BSD-3-Clause** (verified via GitHub license API — not MIT/Apache) |
| NSubstitute | BSD-3-Clause |
| AwesomeAssertions | Apache-2.0 |
| xunit.v3 | Apache-2.0 |
| QRCoder (samples only) | MIT |
| Nerdbank.GitVersioning | MIT |

## Sources

- NuGet v3 registry API (`api.nuget.org/v3-flatcontainer/*/index.json`, `registration5-semver1`, search endpoint) — all package versions, licenses, descriptions, download counts — **HIGH** (first-party registry data, fetched 2026-10-07)
- `dotnet/core` release-notes `10.0/releases.json` (raw.githubusercontent) — .NET 10 LTS status, 10.0.12/SDK 10.0.401/C# 14, EOL 2028-11-14 — **HIGH**
- Microsoft Learn API docs — `ChaCha20Poly1305`, `HKDF`, `WebSocketStream` class pages (net-10.0) and .NET 10 overview what's-new — **HIGH**
- GitHub API — `Metalnem/noise` (last push 2020-12-26, MIT, source file list incl. `Libsodium.cs`, `Noise.csproj` pinning `libsodium` 1.0.16) — **HIGH**
- `tulir/whatsmeow` `go.mod` + root file listing (read-only behavioral reference: `handshake.go`, `go.mau.fi/libsignal v0.2.2`, `coder/websocket`, `google.golang.org/protobuf v1.36.12`) — **HIGH**
- GitHub API — `App-vNext/Polly` license field (BSD-3-Clause) — **HIGH**
- `nsec.rocks` homepage + NuGet registration — NSec feature set (X25519/Ed25519/ChaCha20-Poly1305, secure memory), version 26.4.0 — **HIGH**
- NuGet search endpoint — absence of mature .NET Signal-protocol library; presence of `Baileys.NET` 1.9.1 (.NET 10 port, low adoption, unclear provenance — note only, not a foundation) — **MEDIUM** (absence proofs from search are inherently incomplete)
- FluentAssertions/Xceed licensing — package metadata shows Xceed ownership + non-SPDX license page; full license text not read line-by-line — **MEDIUM**

<!-- GSD:stack-end -->

<!-- GSD:conventions-start source:CONVENTIONS.md -->

## Conventions

Conventions not yet established. Will populate as patterns emerge during development.
<!-- GSD:conventions-end -->

<!-- GSD:architecture-start source:ARCHITECTURE.md -->

## Architecture

Architecture not yet mapped. Follow existing patterns found in the codebase.
<!-- GSD:architecture-end -->

<!-- GSD:skills-start source:skills/ -->

## Project Skills

No project skills found. Add skills to any of: `.claude/skills/`, `.agents/skills/`, `.cursor/skills/`, `.github/skills/`, or `.codex/skills/` with a `SKILL.md` index file.
<!-- GSD:skills-end -->

<!-- GSD:workflow-start source:GSD defaults -->

## GSD Workflow Enforcement

Before using Edit, Write, or other file-changing tools, start work through a GSD command so planning artifacts and execution context stay in sync.

Use these entry points:
- `/gsd-quick` for small fixes, doc updates, and ad-hoc tasks
- `/gsd-debug` for investigation and bug fixing
- `/gsd-execute-phase` for planned phase work

Do not make direct repo edits outside a GSD workflow unless the user explicitly asks to bypass it.
<!-- GSD:workflow-end -->

<!-- GSD:profile-start -->

## Developer Profile

> Profile not yet configured. Run `/gsd-profile-user` to generate your developer profile.
> This section is managed by `generate-claude-profile` -- do not edit manually.
<!-- GSD:profile-end -->
