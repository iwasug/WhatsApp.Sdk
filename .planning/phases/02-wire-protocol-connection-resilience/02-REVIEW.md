---
phase: 02-wire-protocol-connection-resilience
reviewed: 2026-10-09T00:00:00Z
depth: deep
files_reviewed: 18
files_reviewed_list:
  - src/Iwasug.WhatsApp.Sdk/Noise/NoiseProtocol.cs
  - src/Iwasug.WhatsApp.Sdk/Noise/NoiseHandshake.cs
  - src/Iwasug.WhatsApp.Sdk/Noise/NoiseTransport.cs
  - src/Iwasug.WhatsApp.Sdk/Connection/DisconnectReason.cs
  - src/Iwasug.WhatsApp.Sdk/Connection/ConnectionState.cs
  - src/Iwasug.WhatsApp.Sdk/Connection/ConnectionOptions.cs
  - src/Iwasug.WhatsApp.Sdk/Connection/ReconnectPolicy.cs
  - src/Iwasug.WhatsApp.Sdk/Connection/QueryRouter.cs
  - src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs
  - src/Iwasug.WhatsApp.Sdk/Transport/IFrameTransport.cs
  - src/Iwasug.WhatsApp.Sdk/Transport/FrameReader.cs
  - src/Iwasug.WhatsApp.Sdk/Transport/WebSocketFrameTransport.cs
  - src/Iwasug.WhatsApp.Sdk/Codec/BinaryNode.cs
  - src/Iwasug.WhatsApp.Sdk/Codec/BinaryNodeCodec.cs
  - src/Iwasug.WhatsApp.Sdk/Codec/TokenDictionary.cs
  - src/Iwasug.WhatsApp.Sdk/Proto/handshake.proto
  - src/Iwasug.WhatsApp.Sdk/Errors/SdkException.cs
  - NOTICE.md
findings:
  critical: 3
  warning: 11
  info: 10
  total: 24
status: issues_found
---

# Phase 02: Code Review Report — Wire Protocol & Connection Resilience

**Reviewed:** 2026-10-09
**Depth:** deep (per-file review + cross-file call-chain tracing + byte-level
verification against Baileys `@0af2386` sources fetched read-only for this review:
`WAProto/index.js`, `Utils/noise-handler.ts`, `Socket/socket.ts`, `Defaults/index.ts`,
`WABinary/{encode,decode,constants,jid-utils}.ts`)
**Files Reviewed:** 18
**Status:** issues_found

## Summary

Reviewed all five Phase 02 slices (policy table, loopback tracer, Noise core,
codec/framing/router, reconnect lifecycle + WS transport) against their SUMMARYs,
PLAN threat models (T-2-01…T-2-13, T-2-SC), and PROJECT.md constraints.

**The test suite is a self-consistent bubble that cannot detect its worst defects:**
every handshake/codec/router test passes because both ends of every test speak the
SDK's own wire format. Byte-level verification against the cited Baileys sources
proves the handshake envelope and handshake/pump framing diverge from what the real
server speaks — the client as committed cannot complete a handshake with
`wss://web.whatsapp.com` (CR-01, CR-02). Separately, the codec decoder has an
unbounded-recursion remote-crash defect the evil-fixture suite does not cover
(CR-03), and the server-identity cert chain is destroyed before any layer could
verify it (WR-01).

Genuinely strong areas (verified, not assumed): the reconnect verdict table
(401/403/411/419 terminal, 515 restart, 440 stop — `ReconnectPolicy.cs:144-150`),
backoff math (overflow-proof double arithmetic, clamp-after-jitter), Noise token
order (`ee`/`es`/`se` placement matches `noise-handler.ts processHandshake`),
transport nonce shape (byte-identical to Baileys `generateIV`), dict-version
fail-closed paths, TLS posture (no bypass string under `src/`, default validation),
log/message discipline (all 19 `WaConnection` log sites audited — codes/attempts/
account/endpoint/ids only), codec tag constants (all 16 TAGS + `PACKED_MAX` match
`constants.ts` exactly), codec encode order (token→nibble→hex→jid→raw matches
`encode.ts writeString`), flag-byte bitmask (`& 0x02` matches `decode.ts`), JID
domain map (0/1/128/129 matches `WAJIDDomains`), and token-table bounds checking.

## Critical Issues

### CR-01: `handshake.proto` outer field numbers are invented — server envelopes will not parse in either direction (severity: critical)

**File:** `src/Iwasug.WhatsApp.Sdk/Proto/handshake.proto:30-34`
**Issue:** The outer envelope uses `client_hello = 1; server_hello = 2;
client_finish = 3`. The server's actual schema (Baileys `@0af2386`
`WAProto/index.js`, `HandshakeMessage.encode`: wire tags `18/26/34`) is
`clientHello = 2; serverHello = 3; clientFinish = 4`. Consequences, both fatal
against the real server: (1) our message A places `client_hello` at field 1,
which the server decodes as an empty/unknown envelope — the handshake never
starts; (2) the server's message B carries `serverHello` at field 3, so our
`ParseMessageB` sees `ServerHello == null` and throws "inconsistent framing",
which `WaConnection` escalates to a permanent `ProtocolHalt` stop. The `.proto`
header admits "names and numbers below are our own", which directly contradicts
the 02-03 SUMMARY claim of "Baileys-faithful shapes" — and loopback tests cannot
catch this because both sides share the same wrong numbers. (Inner message
numbers were checked and are correct: ephemeral/static/payload = 1/2/3,
client-finish static/payload = 1/2 — only the outer envelope needs renumbering.
Field *names* like `static_key` vs upstream `static` are wire-irrelevant but
worth aligning for honesty.)
**Fix:**
```proto
message HandshakeMessage {
  ClientHello client_hello = 2;
  ServerHello server_hello = 3;
  ClientFinish client_finish = 4;
}
```
Then re-verify: real message-B bytes (field 3) must parse, and our message-A
bytes must place `client_hello` at field 2. Add a golden-bytes test pinning the
outer tags `0x12/0x1A/0x22` so a future renumber breaks the build instead of
production.

### CR-02: Handshake and pump wire framing does not match the server — duplicated intro header, missing length prefixes, `FrameReader` bypassed (severity: critical)

**File:** `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs:483-484`,
`src/Iwasug.WhatsApp.Sdk/Noise/NoiseHandshake.cs:629-633,1168-1172,923-967`,
`src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs:636-699,701-734`
**Issue:** Baileys `encodeFrame`/`processData` define the real framing: the
4-byte intro header is sent **once** (first frame only), and **every** frame —
handshake messages included — carries a 3-byte big-endian length prefix; the
server's message B arrives as `len(3) || HandshakeMessage` with **no** header
(Baileys decodes it directly, no prefix-strip beyond the length). Our wire
format diverges on all three messages: (a) `ConnectAndWatchAsync` sends a
standalone `WaHeader` frame (line 483) and then `WriteMessageA()`, which
*re-prefixes* the same 4 header bytes (lines 631-632) — the intro goes out
twice, and the server's stream parser will consume header bytes `[0x41,0x06,
0x03]` as a ~4.2 MB length prefix and stall; (b) no handshake message carries
the 3-byte length prefix (A embeds header instead, C is a bare protobuf), so a
stream-parsing server reads protobuf bytes as lengths — garbage; (c)
`ParseMessageB` *requires* the 4-byte header the server never sends on message
B, so even a correctly-numbered (post-CR-01) reply is rejected fail-closed.
Beyond the handshake, the post-handshake pump never uses `FrameReader` at all:
`WatchAsync` feeds raw WS messages (still length-prefixed on the real wire)
straight into `NoiseTransport.Decrypt`, where the 3-byte prefix breaks the
strict nonce check (`unexpected nonce` → 408 → infinite 408 reconnect loop
against the real server), and `SendRoutedAsync` emits unprefixed frames the
server cannot deframe. `FrameReader` is exercised only by its own tests — the
02-05 "production transport proven over real sockets" claim proves
byte-transparency against a fake server speaking the loopback protocol, not
wire compatibility. Fix shape:
```csharp
// per connection, first frame only:
await transport.SendAsync(introHeader, attemptToken);          // 4 bytes, once
// every handshake message AND every pump frame:
byte[] framed = Framed(body); // 3-byte BE length prefix + body
await transport.SendAsync(framed, attemptToken);
// inbound: strip the 3-byte prefix (FrameReader.TryReadFrame) before Decrypt;
// message B: accept headerless len(3) || HandshakeMessage.
```
**Fix:** Introduce one shared `Frame(body)` / `Deframe` helper used by both the
handshake path and the pump send/receive paths (backed by `FrameReader`
semantics), send the intro header exactly once per connection, accept
headerless B/C, and add a golden-bytes test with a Baileys-`encodeFrame`-shaped
fixture (`header || len || bytes`) proving the client consumes a real-shaped
message B.

### CR-03: `BinaryNodeCodec` decoder has no recursion-depth limit — malicious nesting crashes the process via `StackOverflowException` (severity: critical)

**File:** `src/Iwasug.WhatsApp.Sdk/Codec/BinaryNodeCodec.cs:414-446`
**Issue:** `ReadNode` recurses per nested child list with no depth budget.
`StackOverflowException` is uncatchable and terminates the process — this is a
remote-crash DoS, squarely inside the declared T-2-06 trust boundary ("server
bytes → adversary-controlled"). Reachability is trivial: each nesting level
costs 3 wire bytes (`LIST_8(2)` + token tag), so a ~30 KB frame reaches
~10k depth — far past the default-stack limit (frames up to 16 MiB allow ~5M
levels). The evil-fixture suite covers lies/truncations/overruns but no nesting
case, and the 02-04 SUMMARY's "lengths validated BEFORE allocating" claim does
not cover stack exhaustion. (Companion weakness, same function: `new
List<BinaryNode>(count)` at line 442 pre-allocates up to 65535 entries from two
untrusted bytes — see WR-04.)
**Fix:**
```csharp
internal BinaryNode ReadNode(int depth = 0)
{
    if (depth > 64)
    {
        throw new SdkFormatException("Encoded node exceeds the maximum nesting depth.");
    }
    ...
    childList.Add(ReadNode(depth + 1));
    ...
}
```

## Warnings

### WR-01: `ReadMessageB` destroys the serverHello payload containing the Noise cert chain — server-identity verification is impossible (severity: high)

**File:** `src/Iwasug.WhatsApp.Sdk/Noise/NoiseHandshake.cs:662-665`
(`CompleteRawMessage2` returns the payload; `ReadMessageB` zeroizes and drops
it; same pattern responder-side at `1191-1193`)
**Issue:** Baileys `processHandshake` decodes a `CertChain` from
`serverHello.payload` and verifies signatures against `WA_CERT_DETAILS`
(`Curve.verify` + issuer-serial check) — that verification is the *only*
responder authentication in the XX flow. Our initiator authenticates, decrypts,
then wipes and discards those exact bytes, so no current or future layer
(Phase 3 included) can verify what the server sent. "Opaque payloads, Phase 3
owns semantics" is a sound boundary, but *discarded* is not *carried* — as
committed, a MITM that terminates TLS (corporate proxy, compelled endpoint)
can complete the Noise handshake unchallenged once CR-01/CR-02 are fixed.
**Fix:** Surface the authenticated server payload to the connection owner
instead of dropping it — e.g. return it alongside the transport or expose a
`ServerPayload` property consumed once — and file the Phase 3 requirement to
verify the cert chain (signature + issuer serial) before treating the session
as authenticated. Wipe only after handoff.

### WR-02: Abandoned `WatchAsync` task on probe-failure/timeout paths; `ObjectDisposedException` escapes `Watch` unobserved (severity: high)

**File:** `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs:562-599,601-623,625-634,649-668`
**Issue:** When the probe loses the `WhenAny` race (line 571) or faults
(lines 586-593) or the sender throws synchronously (lines 562-568),
`AbortChannelAsync` uninstalls (disposes transport + noise + router) while
`watch` is still pending on `ReceiveAsync` — and the attempt-timeout OCE path
(lines 601-612) returns without observing `watch` at all. Two consequences:
(1) `WatchAsync` catches `SdkException` but **not** `ObjectDisposedException`
(lines 649-656 rethrow OCE; ODE from a disposed transport propagates), so on
the production `WebSocketFrameTransport` (whose post-dispose `ReceiveAsync`
throws ODE) the orphaned watch faults unobserved — an
`UnobservedTaskException` hazard plus retained channel references; whether it
fires depends on dispose/receive timing, i.e. flaky by construction. (2) The
orphan may call `TryDeliver` on a disposed router (harmless today only because
`TryDeliver` has no disposed guard — itself a latent invariant).
**Fix:** Track the watch task and always observe it before returning from
`ConnectAndWatchAsync` (await with a bounded join in `AbortChannelAsync` and
the OCE path), and handle `ObjectDisposedException` in `WatchAsync` as a
408-class drop like the `SdkException` branch.

### WR-03: `BinaryNodeCodec.Decode` silently ignores trailing bytes (severity: medium)

**File:** `src/Iwasug.WhatsApp.Sdk/Codec/BinaryNodeCodec.cs:91-99`
**Issue:** `Decode` returns after the root node without checking `_index ==
_data.Length`. Trailing garbage (framing bugs, smuggled second nodes,
truncation-then-append attacks) is accepted as a clean decode, masking framing
defects that strict parsing would surface. Reference `decodeBinaryNode` consumes
a pre-sliced buffer so the comparison is not 1:1, but for a security-facing
decoder fail-closed is the right default.
**Fix:** After `reader.ReadNode()`, throw `SdkFormatException("Encoded node
has trailing bytes.")` when bytes remain. (`TryDecode` inherits the behavior.)

### WR-04: `LIST_16` size pre-allocates up to 65535 entries from two untrusted bytes (severity: medium)

**File:** `src/Iwasug.WhatsApp.Sdk/Codec/BinaryNodeCodec.cs:442`
(`new List<BinaryNode>(count)`; size source `ReadListSize` at `546-555`)
**Issue:** A two-byte list-size of `0xFFFF` commits a ~512 KB backing array
before a single child is validated — the "cap-before-alloc" discipline claimed
for T-2-06 holds for byte buffers (`CheckRemaining`) but not for this object
graph allocation. Weakens (does not break) the framing hardening; combines
poorly with CR-03's deep nesting.
**Fix:** Grow the list incrementally (`new List<BinaryNode>()` + `Add`) or cap
the initial capacity (e.g. `Math.Min(count, 32)`).

### WR-05: `NoiseTransport` dispose/use race throws raw `NullReferenceException` instead of `ObjectDisposedException` (severity: medium)

**File:** `src/Iwasug.WhatsApp.Sdk/Noise/NoiseTransport.cs:104-129,144-181`
**Issue:** `Encrypt`/`Decrypt` call `ThrowIfDisposed()` and then dereference
`_sendCipher!`/`_receiveCipher!`, but `Dispose` nulls those fields. A
dispose racing an in-flight encrypt/decrypt (exactly what
`UninstallChannelAsync` vs `WatchAsync`/`SendRoutedAsync` can produce)
upgrades a documented ODE into an NRE, which no caller catches: in
`SendRoutedAsync` only ODE is mapped to "not open", and in `WatchAsync` only
`SdkTransportException` is mapped to a drop — an NRE escapes as a monitor-loop
"fault" error log + spurious 408, or raw to a host calling `SendQueryAsync`.
**Fix:** Snapshot the cipher to a local after the disposed check and
throw ODE on null:
```csharp
AesGcm? send = Volatile.Read(ref _sendCipher);
if (send is null) { throw new ObjectDisposedException(nameof(NoiseTransport)); }
```

### WR-06: `ObjectDisposedException` leaks where the contract promises `SdkConnectionException` (severity: medium)

**File:** `src/Iwasug.WhatsApp.Sdk/Connection/QueryRouter.cs:88-129` (doc at
`83-87`), `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs:255-276`
**Issue:** `QueryRouter.SendQueryAsync` documents timeout/drop/closing/disposal
all as `SdkConnectionException`, but a new send after `DisposeAsync` throws raw
ODE from `ThrowIfDisposed` (line 91). Worse, `WaConnection.SendQueryAsync`
only remaps ODE to "not open" when the channel *turned over* (`channel !=
_channel`, line 269) — an in-flight send whose router is disposed without
 Turnover (drop/dispose path) surfaces raw ODE, which hosts cannot
distinguish from caller cancellation (also ODE). Exception-type confusion on
the cancel-vs-drop boundary.
**Fix:** Throw `SdkConnectionException("…router is disposed…")` for post-dispose
sends in the router (keep ODE only for genuinely disposed-object misuse if at
all), and in `WaConnection.SendQueryAsync` map ODE from the *current*
channel's router to "not open" unconditionally.

### WR-07: `WaConnection._state` is a plain field written on the monitor thread and read by hosts — stale polling reads (severity: medium)

**File:** `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs:121,171,797-799`
**Issue:** `Emit` assigns `_state` (line 799) on the background monitor loop;
`State` (line 171) is read from arbitrary host threads with no
volatile/barrier. A host polling `State` without subscribing to `StateChanged`
may spin on a stale value indefinitely (the event path is ordered by the
delegate invocation, but the property has no such guarantee).
**Fix:** Declare `_state` as `volatile` (or use `Volatile.Write`/`Volatile.Read`).

### WR-08: `MapOutboundCloseCode` does unchecked `4000 + code` arithmetic and a blind enum cast (severity: medium)

**File:** `src/Iwasug.WhatsApp.Sdk/Transport/WebSocketFrameTransport.cs:310-313`
**Issue:** For any `statusCode` outside 1000–4999 (e.g. a transport-local code
or a future WA code > 999), `LoopbackCloseBase + statusCode` can overflow or
land outside the valid `WebSocketCloseStatus` range (1000–4999), producing an
`ArgumentOutOfRangeException` from `CloseOutputAsync` at close time — the worst
moment for a surprise throw. Callers today pass bounded `DisconnectReason`
values, so this is latent, but the function is `internal static` shared surface
with no guard.
**Fix:** Validate first:
```csharp
int mapped = statusCode is >= 1000 and <= 4999 ? statusCode : LoopbackCloseBase + statusCode;
if (mapped is < 1000 or > 4999) { throw new SdkTransportException("Close status code is outside the sendable range."); }
return (WebSocketCloseStatus)mapped;
```

### WR-09: Message-A `static_key` is modeled but never validated — a peer static in message 1 is silently ignored (severity: medium)

**File:** `src/Iwasug.WhatsApp.Sdk/Proto/handshake.proto:38-42`,
`src/Iwasug.WhatsApp.Sdk/Noise/NoiseHandshake.cs:1407-1413`
**Issue:** The schema models `ClientHello.static_key`, `WriteMessageA` never
sets it (correct per XX), but `ParseMessageA` never rejects it either — a peer
supplying a static key in message 1 (a protocol violation, and in the real
schema a meaningful field) is silently dropped instead of failing closed.
Per the file's own T-2-09 discipline ("every length is bounds-checked… any
mismatch aborts"), unexpected-but-parseable fields should abort.
**Fix:** Reject message A carrying a non-empty `static_key` with a rule-naming
`SdkConnectionException` (and extend the handshake evil-fixture theories).

### WR-10: `NOTICE.md` still lacks the handshake-envelope provenance row promised by 02-03 (severity: medium)

**File:** `NOTICE.md:36-42`
**Issue:** The 02-03 SUMMARY defers to "orchestrator merge" a provenance-log row
for the Baileys read behind the handshake envelope (`socket.ts
validateConnection` + `noise-handler.ts @0af2386`). It is still absent — a
grep for `handshake|socket.ts|noise-handler` in `NOTICE.md` returns zero hits —
while `handshake.proto` exists and its header points at NOTICE policy. The
project's own provenance-recording rule ("record the upstream file path + what
behavior was learned") is therefore unmet for the Noise handshake, the
highest-risk clean-room surface in the phase. (Companion concern, not a
license breach finding: field *roles* were learned from Baileys MIT sources as
documented — the gap is the missing log row, not the sourcing.)
**Fix:** Add the log row (upstream file:lines, what was learned, where used)
before ship; treat the 02-03 "watch item" as a merge gate.

### WR-11: `Decide` burns a `Random.Shared` draw on every drop even though the loop discards the delay (severity: medium)

**File:** `src/Iwasug.WhatsApp.Sdk/Connection/ReconnectPolicy.cs:149`,
`src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs:428,449-452`
**Issue:** `Decide` computes a full-jitter `RetryAfter(Delay)` via
`Random.Shared.NextDouble()`, but its sole production caller
(`MapDropToOutcome`) throws the delay away — "the authoritative delay comes
from the backoff's own draw". Every drop therefore consumes shared-RNG state
for nothing, and the "pure function… deterministic given the draw" story is
undermined: `Decide(408, n)` returns a different `Delay` on every call, so
callers cannot rely on the verdict's data (tests must ignore it). A hidden RNG
dependency in a function documented as pure is a correctness smell at the
policy core.
**Fix:** Split the verdict from the delay — e.g. return a `Retry` verdict
without a delay from `Decide` (backoff remains the single delay authority) or
document that `RetryAfter.Delay` is advisory and must not drive the loop.

## Info

### IN-01: `HkdfTwoBlocks` temporaries are never zeroized (severity: low)

**File:** `src/Iwasug.WhatsApp.Sdk/Noise/NoiseHandshake.cs:267-283`
**Issue:** `.ToArray()` copies of the chaining key and input key material
(lines 273-275) plus the split `nextCk`/`nextKey` intermediates linger in
managed memory until GC; callers diligently wipe their own copies, but these
callee-side temporaries escape the hygiene boundary. Not key exfiltration (no
logging/dumping path touches them), but the file's zeroization claims
overstate coverage.
**Fix:** Wrap the derivation in `try/finally` and `ZeroMemory` the `ikm`/`salt`
copies and `okm` segments after splitting (or accept explicitly in remarks).

### IN-02: `BinaryNode.ToString` escapes attrs/text but not `Tag` (severity: low)

**File:** `src/Iwasug.WhatsApp.Sdk/Codec/BinaryNode.cs:198-247`
**Issue:** `EscapeXmlInto` is applied to attribute keys/values and text
content, but `Tag` is appended raw (lines 202, 244-245). A tag containing
markup renders a broken debug view — and since `ToString` output routinely
lands in logs, the escaping inconsistency defeats the method's own "never
throws / safe for logs" remarks for hostile tags.
**Fix:** Route `Tag` through `EscapeXmlInto` in both positions.

### IN-03: `BinaryNode.Data` getter exposes the mutable backing array (severity: low)

**File:** `src/Iwasug.WhatsApp.Sdk/Codec/BinaryNode.cs:62-69`
**Issue:** Construction defensively copies, but the getter returns the live
array — any holder can mutate node content post-construction, breaking the
"immutable value type" contract the remarks assert. Low blast radius today
(codec took ownership via `FromBytes` copies), but worth closing before Phase 4
shares nodes across layers.
**Fix:** Return a copy (or `ReadOnlyMemory<byte>`) from the getter, or document
the aliasing explicitly.

### IN-04: `RetryBaseDelayMs = 0` is accepted and yields a zero-delay infinite reconnect loop (severity: low)

**File:** `src/Iwasug.WhatsApp.Sdk/Connection/ConnectionOptions.cs:84-87`
**Issue:** Validation rejects negatives but allows zero; a zero base makes
every full-jitter draw `uniform(0, 0)` — a hot reconnect loop with no backoff
against a dead network (battery/ban-risk amplifier, at odds with the
ban-safety constraint). Almost certainly a host misconfiguration rather than
intent.
**Fix:** Require `RetryBaseDelayMs >= 1` (or document the hot-loop consequence
on the property).

### IN-05: `JidDecode` agent-number parsing diverges from Baileys on trailing-garbage agents (severity: low)

**File:** `src/Iwasug.WhatsApp.Sdk/Codec/BinaryNodeCodec.cs:387-395`
**Issue:** Baileys uses `parseInt(agent)` (lenient: `"12x"` → `12`); ours uses
`int.TryParse` (strict: `"12x"` → `0`). Affects only malformed agent JIDs, but
it is a byte-parity divergence from the reference the file claims to replicate.
**Fix:** Mirror the leniency (parse leading digits) with a comment, or pin the
strict behavior in a test as a deliberate deviation.

### IN-06: `DisconnectReason` remarks promise 503 a "longer base" that does not exist (severity: low)

**File:** `src/Iwasug.WhatsApp.Sdk/Connection/DisconnectReason.cs:81-86`
**Issue:** The `UnavailableService` docs claim backoff with "longer base than
`ConnectionLost`", but `Decide` uses one shared base for all retryable codes —
503 and 408 draw from the identical window. Doc/implementation mismatch that
will mislead Phase 5 ban-safety tuning.
**Fix:** Either implement per-code bases or correct the remark to state the
shared-base behavior.

### IN-07: `MapInboundCloseCode` strips genuine 4000–4999 standard private-use codes as loopback offsets (severity: low)

**File:** `src/Iwasug.WhatsApp.Sdk/Transport/WebSocketFrameTransport.cs:297-303`
**Issue:** Any real private-use close code in 4000–4999 (e.g. `4400`) is
misread as a WA code (`400` → unknown → bounded retry + drift log). Harmless
today (server sends sub-1000 codes; loopback is test-only), and the convention
is documented — recorded so a future production use of that range is not a
mystery.
**Fix:** No change required; consider a remark noting the ambiguity.

### IN-08: `_firstOpen` TCS is left permanently uncompleted on the host-cancel path (severity: low)

**File:** `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs:225,355-363`
**Issue:** When the host cancels before the first open, the `ConnectAsync`
waiter throws via `WaitAsync(cancellationToken)`, but the underlying TCS is
never completed (the OCE monitor path returns without touching it). Harmless
(GC collects it), but an abandoned TCS holding an exception-free void is sloppy
lifecycle hygiene next to the otherwise careful first-open exchange.
**Fix:** `TrySetCanceled(cancellationToken)` on the cancel path.

### IN-09: Single-flag-byte frames decode to empty payloads instead of failing closed (severity: low)

**File:** `src/Iwasug.WhatsApp.Sdk/Transport/FrameReader.cs:76-89`
**Issue:** A 1-byte frame (flag only) passes the `length == 0` guard and yields
an empty node buffer (plain) or an inflate-of-empty (flagged) rather than a
rule-naming rejection. Downstream the codec rejects empties, so this is
defense-in-depth, but the frame layer should own the "frames are never empty"
invariant.
**Fix:** Throw `SdkTransportException("Frame payload must not be empty.")`
when `length <= 1`… (careful: only when the stripped payload would be empty —
i.e. `length == 1`).

### IN-10: Protocol-halt emits `Closing(null)`, indistinguishable from a host-initiated stop (severity: low)

**File:** `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs:456-464`
**Issue:** `StopForProtocolViolation` emits `Closing` with a null code — the
same shape as a clean host stop — forcing hosts to correlate logs to tell
"peer sent evil bytes, loop halted permanently" from "we stopped". Hosts need
the distinction: one requires investigation, the other is routine.
**Fix:** Carry a distinguishing signal (e.g. a dedicated code or a remark in
the `Closing` payload docs) and document it on `ConnectionStateChangedEventArgs`.

---

## Threat-model disposition (plan register T-2-01…T-2-13, T-2-SC)

| Threat | Verdict | Notes |
|--------|---------|-------|
| T-2-01 401/419 terminal | MITIGATED in table | `Decide` maps 401/403/411/419 → `Terminal`; zero-reconnect proven by tests. |
| T-2-02 backoff storm | MITIGATED (math) | Full-jitter + clamp-after-jitter verified; attempt-visibility in events/logs confirmed. Zero-base config gap → IN-04. |
| T-2-03 nonce/key lifecycle | MITIGATED (mechanics) | Fresh keys per handshake, counters from 0, zeroizing dispose, re-handshake tests. Nonce shape byte-identical to Baileys `generateIV`. Callee-side HKDF temporaries → IN-01; dispose race → WR-05. |
| T-2-04 440/411 no-retry | MITIGATED in table | 440 → `SurfaceAndStop`, 411 → `Terminal`; depth-pinned tests. |
| T-2-05 cipher correctness | MITIGATED | Committed cacophony vectors gate the build; init (`NOISE_MODE` is 32 B padded in Baileys `Defaults` — matches `PaddedProtocolName`) verified, not assumed. |
| T-2-06 fuzzed frames | PARTIAL | Cap-before-alloc, flag handling (`& 0x02` matches `decode.ts`), bounds checks, evil fixtures all verified. Gaps: unbounded recursion (CR-03), trailing bytes (WR-03), LIST_16 prealloc (WR-04). |
| T-2-07 state ordering | MITIGATED (mechanics) | Single-writer emit + `Interlocked` terminal guard verified in code. `_state` visibility → WR-07. |
| T-2-08 router correlation | MITIGATED (mechanics) | Monotonic ids, timeout eviction, late-discard, closing latch all verified. Skeleton-envelope→node-attr migration is acknowledged Phase-4 debt (tracked, not a new finding). ODE contract gap → WR-06. |
| T-2-09 dict enforcement | MITIGATED | Header/dict fail-closed branches in both parsers verified; log sites name rules only. |
| T-2-10 key material in logs | MITIGATED | All 19 `WaConnection` log sites + handshake/router (zero sites by design) audited; fingerprint-not-keys in tests; no bypass strings. |
| T-2-11 TLS validation | MITIGATED | Grep over `src/` for `ServerCertificate|ValidationCallback|Certificate` returns zero production hits; loopback uses plaintext `ws://` only. |
| T-2-12 message-A replay | PARTIAL | Fresh ephemeral per attempt verified. Responder authentication missing (cert chain discarded) → WR-01. |
| T-2-13 MPL provenance | GAP | Token-table + codec rows present; handshake-envelope row missing → WR-10. No whatsmeow-derived bytes found (only guard comments). |
| T-2-SC supply chain | MITIGATED | No new packages in 02-04/02-05; `PrivateAssets=all` on Grpc.Tools per summary; NSec/protobuf pins per 02-01. (Pin values not re-audited here.) |
| Interop (unregistered) | **OPEN — CR-01/CR-02** | Envelope numbers + handshake/pump framing diverge from server; no test can catch it (shared-format bubble). Needs golden server-shaped fixtures + Phase 9 interop. |

## Verification notes

- Read all five `02-*-SUMMARY.md` + all five `02-*-PLAN.md` (threat tables) + `PROJECT.md` in full before reviewing.
- Read all 18 listed files in full (including the full 1491-line `NoiseHandshake.cs`).
- Ran pattern greps: TLS-bypass strings (clean), `whatsmeow|tulir` (guard comments only), key-material log patterns (clean — all matches are "never logged" doc remarks), all `Log*` sites in `WaConnection.cs` (19, audited individually).
- Fetched Baileys `@0af2386` read-only for byte-level cross-checks: `WAProto/index.js` (4.4 MB, downloaded to temp and grepped locally — outer envelope fields proven 2/3/4 via wire tags 18/26/34; inner fields 1/2/3 verified), `Utils/noise-handler.ts`, `Socket/socket.ts` (`validateConnection` flow), `Defaults/index.ts` (`NOISE_MODE` 32-byte padded — matches; `UNAUTHORIZED_CODES` 401/403/419 consistent), `WABinary/encode.ts` + `decode.ts` + `TAGS` block + `jid-utils.ts` (codec parity points above). Temp download deleted after review (not committed).
- No source files modified by this review. No tests executed (findings are logic/wire-level; suites report 288/288 green in-plan, which this review treats as bubble-consistent, not as interop evidence).

---

_Reviewed: 2026-10-09_
_Reviewer: the agent (gsd-code-reviewer)_
_Depth: deep_
