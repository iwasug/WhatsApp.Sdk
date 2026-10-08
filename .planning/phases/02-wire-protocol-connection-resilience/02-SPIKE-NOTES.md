# 02 Spike Notes — Wire Minutiae Confirmation (read-only, Baileys MIT only)

**Date:** 2026-10-08
**Source repo:** WhiskeySockets/Baileys (MIT — `spdx_id: MIT` per GitHub API),
  read at `master` commit `0af2386` (2026-08-04, "fix: advertise WIN_HYBRID…").
**NSec source:** ektrah/nsec `master` (`src/Cryptography/KeyAgreementAlgorithm.cs`,
  `src/Cryptography/PublicKey.cs`), fetched 2026-10-08.
**Hygiene:** No whatsmeow (MPL-2.0) source was opened for this spike — behavioral
reference only, per PROJECT.md. No token-dictionary bytes are transcribed here;
they land in 02-04 with NOTICE attribution. No file under `src/` or `tests/` was
modified by this task.

## F1 — Noise cipher suite: CONFIRMED (AESGCM)

- **Source:** `src/Defaults/index.ts` @ `0af2386`:
  `export const NOISE_MODE = 'Noise_XX_25519_AESGCM_SHA256\0\0\0\0'`
- **Verdict: CONFIRMED.** Phase 2 cipher is `Noise_XX_25519_AESGCM_SHA256`
  (28-char name + 4× `0x00` = 32-byte construction). The STACK.md ChaChaPoly line
  is superseded for the Noise transport path.
- **Consequence (favorable):** the transport cipher is BCL-covered (`AesGcm`);
  NSec is needed in Phase 2 for X25519 key agreement only. Corroborated by
  `src/Utils/noise-handler.ts` using `aesEncryptGCM`/`aesDecryptGCM` from
  `./crypto` (not XChaCha).
- **Fallback:** n/a (confirmed). If a later live-interop run ever shows a
  ChaChaPoly suite, 02-03 fails closed: handshake aborts on protocol-name
  mismatch rather than attempting a second suite.

## F2 — Message-A wire bytes: CONTRADICTED (as "raw 32-byte pubkey")

- **Sources:** `src/Socket/socket.ts` (`validateConnection`) +
  `src/Utils/noise-handler.ts` (`encodeFrame`, `authenticate`) @ `0af2386`.
- **Verdict: CONTRADICTED** for assumption A2's "raw 32-byte ephemeral pubkey"
  half; **CONFIRMED** for the WA-header half.
- **Observed shape:** Message-A = `introHeader` + `3-byte BE length` +
  `protobuf(HandshakeMessage{ clientHello: { ephemeral: <32B pubkey> } })`,
  sent *unencrypted* (transport not yet established; `encodeFrame` skips AEAD
  while `transport == null`). `introHeader` is `NOISE_WA_HEADER` =
  `[87, 65, 6, 3]` (`'W','A', proto 6, dict 3`), or — when resuming with routing
  info — an `ED`-prefixed header (`'ED', 0x00, 0x01, 3-byte routing-info len`
  + routing bytes + `NOISE_WA_HEADER`).
- **Prologue authentication:** `authenticate(NOISE_HEADER)` then
  `authenticate(publicKey)` (ephemeral pubkey bytes feed `MixHash`
  `h = SHA256(h || data)`), matching Noise XX prologue semantics.
- **Fail-closed fallback:** `NoiseProtocol` (Task 3) records the WA-header const
  from this finding; the Message-A builder MUST emit header + HandshakeMessage
  protobuf (schema owned by 02-03), never a bare pubkey. A peer that answers a
  bare-pubkey Message-A is talking to something other than WA — abort.

## F3 — Length-prefix width + segmentation: CONTRADICTED (as "2-byte")

- **Source:** `src/Utils/noise-handler.ts` (`encodeFrame` / `processData`) @ `0af2386`.
- **Verdict: CONTRADICTED** on width; **CONFIRMED** that segmentation/coalescing
  is handled by an accumulation loop (no one-frame-per-WS-message assumption).
- **Observed framing:** `3-byte big-endian length prefix`
  (`size = (b[0]<<16) | (b[1]<<8) | b[2]`, 24-bit), then `size` payload bytes.
  Decode loop: suspend while `< 3` bytes available; suspend while `< size + 3`;
  parse *all* complete frames per receive (`while (true)` over the `inBytes`
  accumulator); partial frames buffered across WS messages. So one WS message
  may carry multiple frames or a frame fragment — fragmentation is a fact of
  the wire, not an edge case.
- **Fail-closed fallback (as anticipated in the plan):** `FrameReader` takes the
  **3-byte BE** width from this finding. Golden fixtures (02-04) must cover:
  coalesced frames, split-byte-by-byte delivery, and evil lengths (cap
  `MaxFrameBytes`, abort the connection on violation — never allocate
  untrusted sizes).

## F4 — Pre-auth credential-less iq round-trip: CONTRADICTED (does not exist)

- **Source:** `src/Socket/socket.ts` (`validateConnection`, `query`,
  `onMessageReceived`, post-`CB:success` block) @ `0af2386`.
- **Verdict: CONTRADICTED** for assumption A3. The open sequence performs NO
  credential-less query: immediately after the handshake completes, the first
  stanza is always the authenticated payload — `generateRegistrationNode`
  (unpaired) or `generateLoginNode` (paired) wrapped as `clientFinish` — and
  every observed correlated `iq` (`encrypt` digest/count, `w:p` ping keepalive
  every 30 s, `passive`, pre-key upload) happens post-login. There is no
  supported server behavior for an unauthenticated `iq` round-trip.
- **Fail-closed fallback (as anticipated in the plan):** the 02-02 tracer
  round-trips an *opaque post-handshake-shaped* query against the loopback fake
  (proves framing + id-correlation + timeout machinery end-to-end), and the
  router API stays **payload-agnostic** (ids + timeouts + late-reply discard,
  no xmlns-specific shapes) so the real pre-auth story — if any is ever
  observed in Phase 9 interop — slots in without an API break.

## F5 — Endpoint URL + Origin header: CONFIRMED

- **Sources:** `src/Defaults/index.ts`
  (`waWebSocketUrl: 'wss://web.whatsapp.com/ws/chat'`) +
  `src/Socket/Client/websocket.ts`
  (`new WebSocket(this.url, { origin: DEFAULT_ORIGIN, ... })`) @ `0af2386`,
  with `DEFAULT_ORIGIN = 'https://web.whatsapp.com'`.
- **Verdict: CONFIRMED** for A1. Connect target `wss://web.whatsapp.com/ws/chat`
  with `Origin: https://web.whatsapp.com` browser-mimicry header and
  `connectTimeoutMs` as the WS handshake timeout.
- **Adopted defaults (same file):** `connectTimeoutMs` 20 000,
  `defaultQueryTimeoutMs` 60 000, `keepAliveIntervalMs` 30 000,
  `retryRequestDelayMs` 250 — these are the `ConnectionOptions` defaults in
  Task 3, matching the plan's surface list verbatim.
- **Fallback:** n/a (confirmed). If the endpoint ever moves, it becomes a
  `ConnectionOptions` override in Phase 9 drift work, not a code change.

## F6 — NSec X25519 call shape: CONFIRMED

- **Source:** ektrah/nsec `master`, `src/Cryptography/KeyAgreementAlgorithm.cs`
  + `src/Cryptography/PublicKey.cs` (fetched 2026-10-08).
- **Verdict: CONFIRMED** for A4. Exact type names for 02-03:
  - Singleton: `NSec.Cryptography.KeyAgreementAlgorithm.X25519`
    (type `NSec.Cryptography.X25519 : KeyAgreementAlgorithm`).
  - Key creation: `NSec.Cryptography.Key.Create(algorithm, in KeyCreationParameters)`;
    local public bytes via `key.PublicKey` (`NSec.Cryptography.PublicKey`).
  - Peer import: `PublicKey.Import(algorithm, ReadOnlySpan<byte>, KeyBlobFormat)`
    (or `TryImport` for fail-soft paths; raw 32-byte X25519 keys).
  - Agreement: `SharedSecret? Agree(Key privateKey, PublicKey peerPublicKey,
    in SharedSecretCreationParameters creationParameters = default)` —
    instance method on the algorithm; 32-byte shared secrets per the
    algorithm table (private 32 / public 32 / secret 32).
  - Keys live in libsodium secure memory; `SharedSecret` is disposable —
    02-03 must dispose/zeroize per handshake (fresh ephemeral every
    handshake, Pitfall 4).
- **Fallback:** n/a (confirmed). Any shape drift is a compile-time error in
  02-03, caught by `-warnaserror`, not a runtime surprise.

## F7 — Grpc.Tools currency vs 2.84.0 pin: CONFIRMED

- **Source:** NuGet flatcontainer `api.nuget.org/v3-flatcontainer/grpc.tools/index.json`
  (fetched 2026-10-08): version list ends at `2.84.0-pre1` → `2.84.0`.
  Nothing newer exists (no 2.85 line, no newer prerelease).
- **Verdict: CONFIRMED** for A9. The `Grpc.Tools 2.84.0` pin stands
  (`PrivateAssets=all`, build-time only).
- **Same check, NSec:** `nsec.cryptography` flatcontainer ends at `26.4.0`
  (no newer stable or preview) — the `NSec.Cryptography 26.4.0` pin stands.
- **Fallback:** n/a (confirmed).

## Corroborating observations (no new verdicts, design inputs for later plans)

- **O1 — Disconnect enum values** (`src/Types/index.ts` @ `0af2386`):
  `connectionClosed = 428`, `connectionLost = 408`, `connectionReplaced = 440`,
  `timedOut = 408` (aliases 408 — supports the plan's "log sub-cause
  distinctly" note), `loggedOut = 401`, `badSession = 500`,
  `restartRequired = 515`, `multideviceMismatch = 411`, `forbidden = 403`,
  `unavailableService = 503`. Matches the plan's `DisconnectReason` member
  list exactly. **Plus:** `UNAUTHORIZED_CODES = [401, 403, 419]` in
  `src/Defaults/index.ts` — **419 is auth-terminal in Baileys but absent from
  the plan's policy table.** Recommendation for the Task 2 surface lock /
  Task 3 table: map 419 → Terminal alongside 401/403 (auth failures must never
  retry); otherwise it falls into "unknown → bounded retry", which risks a
  reconnect loop against dead credentials (T-2-01 class).
- **O2 — Transport nonce discipline** (`noise-handler.ts`, `TransportState`):
  12-byte IV, counter in the last 4 bytes big-endian, separate read/write
  counters from 0, fresh `TransportState` per `finishInit` (i.e. per
  handshake), handshake-phase IV likewise counter-based with the running hash
  as AAD, `HKDF(data, 64, salt, info='')` split into write/read keys. This is
  the normative shape for 02-03's `NoiseTransport` (Pitfall 4: never share,
  never persist, never reuse nonces).
- **O3 — Query-id shape** (`socket.ts`, `generateMessageTag`): `${uqTagId}${epoch++}`,
  a per-connection monotonic counter — never reused while its reply window is
  open. Supports the router's 64-bit-counter rule (Pitfall 5).
- **O4 — Keepalive is an iq, not a WS ping** (`startKeepAliveRequest`): every
  `keepAliveIntervalMs`, an `iq { to: s.whatsapp.net, type: get, xmlns: w:p }`
  with a `<ping/>` child; silence beyond `keepAliveIntervalMs + 5000` kills
  the connection as `connectionLost` (408). `ClientWebSocket.KeepAliveInterval`
  and this iq ping are complementary, not substitutes.
- **O5 — Post-handshake compression flag** (`WABinary/decode.ts`,
  `decompressingIfRequired`): first payload byte bit `0x02` set → zlib-inflate
  the rest; otherwise strip the `0x00` prefix byte. Frame decode (02-04) must
  implement exactly this before node parsing. (Structural fact only — no token
  bytes transcribed.)
