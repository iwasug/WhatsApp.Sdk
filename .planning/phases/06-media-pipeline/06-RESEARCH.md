# Phase 6: Media Pipeline - Research

**Researched:** 2026-10-14
**Domain:** WhatsApp media exchange (image / document / voice-note) over the existing gated E2EE send path — media-key crypto envelope, mmg upload/download transport, download-expiry + media-retry recovery
**Confidence:** HIGH (media schemas + crypto layout + transport + retry flows all read verbatim from MIT Baileys @ 0af2386 and behaviorally cross-checked against whatsmeow `download.go` / `upload.go` / `mediaconn.go` / `mediaretry.go` / `send.go` read-only this session; residual byte-level risks are fixture-gated, see Assumptions)

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| MED-01 | Host can send image, document, and voice note | Schema table (imageMessage=3, documentMessage=7, audioMessage=8 + field roles); upload-then-E2EE-send ordering; `type="media"` node attr + `mediatype` enc attr; three new gated `Messenger` APIs reusing `MessageSender` gate + ack correlation; group media via existing skmsg path |
| MED-02 | Host can receive/download media including download-expiry handling and media-retry flow | Download URL construction from `direct_path` + media-conn hosts; verify-then-decrypt envelope; 403/404/410 → server-error receipt → `rmr` notification → fresh `direct_path` re-download, host-invisible with caps; typed media events with Stream discipline |
</phase_requirements>

## Summary

Phase 6 adds media to the now-gated send path without changing any of the machinery Phases 4–5 built. The single most important finding is that **media needs no new wire transport**: in both reference implementations the media bytes ride *inside* the E2EE-encrypted `Message` proto (as `imageMessage` / `documentMessage` / `audioMessage` branches carrying `url`, `directPath`, `mediaKey`, `fileSha256`, `fileEncSha256`, `fileLength`, `mimetype` plus type-specific fields), while the plaintext `<message>` stanza keeps the exact shape Phase 4 already assembles — with only two attribute deltas (`type="media"` on the node, `mediatype="image|document|audio|ptt"` on 1:1 `enc` children). Upload (encrypt → POST to mmg host → get `url`/`direct_path`) happens *before* the E2EE send; download + verify + decrypt happens *after* the existing dispatcher decrypts the outer E2EE layer.

The media crypto envelope is fully pinned this session: fresh 32-byte `mediaKey` per upload → HKDF-SHA256 (empty salt, per-type `WhatsApp <Kind> Keys` info, 112 bytes) → `iv[0:16] + cipherKey[16:48] + macKey[48:80]` → AES-256-CBC (BCL, PKCS7) with the *derived* IV → HMAC-SHA256 over `iv || ciphertext` truncated to **10 bytes** appended → `fileSha256 = SHA256(plaintext)`, `fileEncSha256 = SHA256(ciphertext || mac)`. The Baileys MIT source and the whatsmeow behavioral read agree on every byte, including the easy-to-miss details (derived IV, not random; MAC covers IV; 10-byte truncation; `fileEncSha256` covers the MAC too).

Expiry recovery is also fully pinned: HTTP 403/404/410 on download → `server-error` receipt with AES-256-GCM-encrypted `ServerErrorReceipt{stanzaId}` (retry key = HKDF 32 bytes under `"WhatsApp Media Retry Notification"`, 12-byte IV, AAD = message id) + `<rmr>` child → peer re-uploads → inbound receipt carrying `<rmr>` delivers either `<error code>` or fresh `<encrypt>` bytes → decrypt to `MediaRetryNotification{stanzaId=1, directPath=2, result=3}` → re-download. All field numbers below come from Baileys MIT `WAProto.proto` at commit `0af2386`.

**Primary recommendation:** Plan as 4 plans — (1) proto schema extension + `MediaCrypto` envelope + golden hash/MAC fixtures (byte-exact gate, no network), (2) upload transport (`MediaConn` IQ + mmg POST) + three gated send APIs + send-path audit extension, (3) receive path (media branch parse → download → verify → typed Stream event) + expiry/media-retry loop with loopback proofs, (4) safety/store integration (file-size caps, recording presence, risk ledger) + validation closeout + UAT. No new NuGet packages; `Polly.Core` 8.8.0 is approved for the HTTP layer only (STACK.md pre-authorizes this exact use).

## Hard Constraints (binding on all Phase-6 plans)

1. **Clean-room:** NO copying of whatsmeow (MPL-2.0) code, `.proto` files, or generated output. whatsmeow sources were read behaviorally only this session; every schema line comes from Baileys (MIT) at commit `0af2386`. Each new/edited `.proto` file must carry the provenance header (message.proto precedent) and each upstream read must land a NOTICE.md row (Phase-4 provenance discipline).
2. **Stack:** `net10.0`; `Google.Protobuf` 3.36.2 + `Grpc.Tools` 2.84.0 (`PrivateAssets=all`, `internal_access`); `NSec.Cryptography` 26.4.0 for X25519/Ed25519 only (media crypto is BCL-only: AES-256-CBC, HMAC-SHA256, HKDF, SHA-256, RNG — media-retry needs AES-256-GCM, in-box since .NET; never NSec for symmetric crypto); in-box `ClientWebSocket` + `HttpClient`; `System.Threading.RateLimiting`/`Channels`; `Polly.Core` 8.8.0 allowed for HTTP upload/download resilience only, never WS reconnect.
3. **Gate:** Every public media send MUST traverse `ISafetyGate.ExecuteSendAsync` with `SendContext` — no independent send loop, no ungated egress (05-01 send-path audit precedent; the audit test MUST be extended to the new media APIs).
4. **Baseline stability:** `Proto/message.proto` field numbers `conversation=1`, `senderKeyDistributionMessage=2`, `extendedTextMessage=6`, `messageContextInfo=35` are pinned by fixtures and MUST NOT move. `Messaging/MessageSender` (send gate, one-network-send, retry-resend), `Messaging/Messenger` (three gated sends), `Messaging/MessageDispatcher` (decrypt→persist→ack→event→cache, D-06/D-20), `Messaging/MessageBuilder` node seam are reused, not forked. Media branches are additive only.

## Schema / Field-Number Table (Baileys provenance)

Source for every row: `WhiskeySockets/Baileys@0af2386`, `WAProto/WAProto.proto` (MIT), read verbatim this session [VERIFIED: Baileys WAProto.proto @ 0af2386].

### `Message` branch numbers (additive to the text-only baseline)

| Branch | Field # | Status |
|--------|---------|--------|
| `conversation` | 1 | stable (Phase 4) |
| `senderKeyDistributionMessage` | 2 | stable (Phase 4) |
| `imageMessage` | 3 | **NEW in Phase 6** |
| `extendedTextMessage` | 6 | stable (Phase 4) |
| `documentMessage` | 7 | **NEW in Phase 6** |
| `audioMessage` | 8 | **NEW in Phase 6** |
| `messageContextInfo` | 35 | stable (Phase 4, still empty placeholder) |

Out of scope (do NOT add): `videoMessage=9`, `stickerMessage=26`, view-once wrappers, `documentWithCaptionMessage=53` (v2 MEDV-01).

### `ImageMessage` fields (Phase-6 subset; wire names lowercased by protoc as usual)

| Field | # | Role in Phase 6 |
|-------|---|-----------------|
| `url` | 1 | mmg upload response URL (populated post-upload, pre-E2EE-send) |
| `mimetype` | 2 | host-declared MIME, validated against allow-list (Pitfall 4) |
| `caption` | 3 | optional UTF-8 caption (same no-normalization discipline as text) |
| `fileSha256` | 4 | SHA-256 of plaintext (verify on download) |
| `fileLength` | 5 | plaintext byte length |
| `height` / `width` | 6 / 7 | host-supplied dimensions; bounds-checked, never sniffed by SDK |
| `mediaKey` | 8 | fresh 32 random bytes per upload |
| `fileEncSha256` | 9 | SHA-256 of `ciphertext \|\| mac` (verify on download) |
| `directPath` | 11 | mmg upload response direct path (download input) |
| `mediaKeyTimestamp` | 12 | upload unix seconds |
| `jpegThumbnail` | 16 | optional host-supplied thumbnail bytes (SDK validates size only, never generates — D-64) |
| `contextInfo` | 17 | quoting/reply context (same `ContextInfo` message; enables captioned replies) |

Deliberately omitted: `firstScanSidecar=18`, `scansSidecar=21`, `midQuality*=23/24`, `viewOnce=25`, `mediaKeyDomain=33`, annotations.

### `DocumentMessage` fields (Phase-6 subset)

| Field | # | Role in Phase 6 |
|-------|---|-----------------|
| `url` | 1 | mmg URL |
| `mimetype` | 2 | host-declared MIME (allow-listed; executables rejected — Pitfall 4) |
| `title` | 3 | optional display title |
| `fileSha256` | 4 | plaintext hash |
| `fileLength` | 5 | plaintext length |
| `pageCount` | 6 | optional (PDFs); host-supplied, untrusted on receive |
| `mediaKey` | 7 | fresh 32 random bytes |
| `fileName` | 8 | host-supplied filename; sanitized on receive (no path separators — Pitfall 4) |
| `fileEncSha256` | 9 | ciphertext hash |
| `directPath` | 10 | download input |
| `mediaKeyTimestamp` | 11 | upload unix seconds |
| `jpegThumbnail` | 16 | optional thumbnail bytes |
| `contextInfo` | 17 | quoting context |
| `caption` | 20 | optional caption |

### `AudioMessage` fields (Phase-6 subset; voice note = `ptt=true`)

| Field | # | Role in Phase 6 |
|-------|---|-----------------|
| `url` | 1 | mmg URL |
| `mimetype` | 2 | voice notes: `audio/ogg; codecs=opus` (host must supply Opus-in-Ogg; SDK validates the label, never transcodes — D-65) [ASSUMED: exact label string; gate in UAT] |
| `fileSha256` | 3 | plaintext hash |
| `fileLength` | 4 | plaintext length |
| `seconds` | 5 | duration seconds (host-supplied on send; untrusted on receive) |
| `ptt` | 6 | **must be `true` for voice notes** (push-to-talk flag drives the voice-note UI) |
| `mediaKey` | 7 | fresh 32 random bytes |
| `fileEncSha256` | 8 | ciphertext hash |
| `directPath` | 9 | download input |
| `mediaKeyTimestamp` | 10 | upload unix seconds |
| `contextInfo` | 17 | quoting context |
| `streamingSidecar` | 18 | omit on send (streaming optimization, not v1) |
| `waveform` | 19 | optional 64 bytes, values 0–100 (Baileys `getAudioWaveform` shape [VERIFIED: Baileys messages-media.ts @ 0af2386]); host-supplied, range-validated |

### Media-retry protos (Baileys provenance, same pin)

| Message | Field | # | Notes |
|---------|-------|---|-------|
| `ServerErrorReceipt` | `stanzaId` | 1 | GCM-encrypted request body; AAD = message id |
| `MediaRetryNotification` | `stanzaId` | 1 | correlates to the original message id |
| `MediaRetryNotification` | `directPath` | 2 | fresh download path on success |
| `MediaRetryNotification` | `result` | 3 | `ResultType`: `GENERAL_ERROR=0`, `SUCCESS=1`, `NOT_FOUND=2`, `DECRYPTION_ERROR=3` |
| `MediaRetryNotification` | `messageSecret` | 4 | omit (bot/reporting surface, out of scope) |

## Media-Crypto Byte Layout (exact — planner writes code + fixtures from this section)

Cross-checked: Baileys MIT `src/Utils/messages-media.ts` (`getMediaKeys`, `encryptedStream`, `downloadEncryptedContent`) @ 0af2386 [VERIFIED] against whatsmeow behavioral read (`download.go` `getMediaKeys`/`downloadAndDecrypt`/`validateMedia`, `upload.go` `Upload`, `mediaretry.go`) [CITED: behavioral, MPL-2.0, no bytes copied]. The two agree on every byte below.

### D-60: Key expansion (exact)

```text
mediaKey      := 32 CSPRNG bytes, fresh per upload (RandomNumberGenerator, zeroized after use)
info          := "WhatsApp Image Keys" | "WhatsApp Audio Keys" | "WhatsApp Document Keys"
                (exact ASCII; voice notes use the Audio info — ptt is a proto flag only, NOT a KDF input)
expanded      := HKDF-SHA256(ikm = mediaKey, salt = empty, info = info, length = 112)
iv            := expanded[0:16]
cipherKey     := expanded[16:48]    (AES-256)
macKey        := expanded[48:80]
refKey        := expanded[80:112]   (whatsmeow retains; Baileys discards — SDK MUST NOT use/store refKey)
```

- HKDF salt: whatsmeow passes `nil` (Go `hkdf` pads nil salt to HashLen zeros); Baileys `hkdf(buffer, 112, {info})` with no salt (rust-bridge default). Both reduce to zero-salt HKDF-SHA256 [VERIFIED behaviorally; .NET `HKDF.DeriveKey(HashAlgorithmName.SHA256, mediaKey, output, salt: [], info)`]. The Wave-0 golden fixture (fixed key → assert `iv/cipherKey/macKey` hex) settles any residual salt-encoding doubt before network code exists.
- Per-type isolation: the info string binds keys to one media kind. **Never** reuse one `mediaKey` across kinds (Pitfall 1).

### D-61: Encrypt-then-MAC construction (exact order)

```text
ENCRYPT (streaming; single pass, constant memory):
  aes  := AES-256-CBC(cipherKey, iv=derived iv)          // NOTE: derived IV, NOT random (Pitfall 2)
  hmac := HMAC-SHA256(macKey);  hmac.update(iv)          // MAC COVERS THE IV
  shaP := SHA256(); shaE := SHA256()
  for each plaintext chunk p:
      shaP.update(p); c := aes.update(p)
      shaE.update(c); hmac.update(c); emit c
  c := aes.final(); shaE.update(c); hmac.update(c); emit c
  mac := hmac.digest()[0:10]                             // TRUNCATE to 10 bytes
  shaE.update(mac); emit mac
  fileSha256    := shaP.digest()                          // over plaintext
  fileEncSha256 := shaE.digest()                          // over (ciphertext || mac) — INCLUDES the mac
  upload bytes  := ciphertext || mac                     // IV is NOT prepended (Pitfall 2)

DECRYPT (verify-then-decrypt, fail-closed — D-62):
  require len(blob) > 10, fileEncSHA256/fileSha256 present and 32 bytes
  require SHA256(blob) == fileEncSha256   → else typed MacMismatch/hash error (no decrypt attempt)
  split blob → ciphertext, mac = blob[-10:]
  require HMAC-SHA256(macKey, iv || ciphertext)[0:10] == mac  (FixedTimeEquals) → else fail, no plaintext
  plaintext := AES-256-CBC-Decrypt(cipherKey, derived iv, ciphertext)  // PKCS7; padding error = fail
  require SHA256(plaintext) == fileSha256 → else fail (nothing delivered)
  deliver plaintext stream only after all three checks pass
```

- Padding: PKCS7 (Node auto-padding on encrypt / .NET `PaddingMode.PKCS7`) [ASSUMED standard; pinned by round-trip fixture].
- Baileys' download path does pure decrypt-streaming with NO verification (it trusts the caller); the SDK MUST follow the whatsmeow verify order instead (D-62) — do not copy Baileys' omission.
- Zeroize `mediaKey`, `cipherKey`, `macKey` byte arrays after use (`CryptographicOperations.ZeroMemory`); key bytes never reach logs/events/exceptions (repo secret-hygiene rule).

### D-62: Media-retry crypto (exact)

```text
retryKey   := HKDF-SHA256(ikm = mediaKey, salt = empty, info = "WhatsApp Media Retry Notification", length = 32)
request    := AES-256-GCM(key = retryKey, nonce = 12 CSPRNG bytes, plaintext = ServerErrorReceipt{stanzaId = msgId}, aad = msgId UTF-8 bytes)
response   := AES-256-GCM-Decrypt(key = retryKey, nonce = iv from <enc_iv>, ciphertext = <enc_p>, aad = msgId) → MediaRetryNotification
```

- .NET: in-box `AesGcm` (BCL, no new dependency). GCM tag handling follows the BCL API (`Encrypt(nonce, plaintext, ciphertext, tag, aad)`); tag bytes ride inside `enc_p` per the GCM-suffixed convention both references use — the loopback fixture (encrypt → parse → decrypt round-trip) pins the exact framing before live use.

## Upload / Download Flow

### D-63: Media-conn (upload/download auth — whatsmeow `mediaconn.go` behavioral)

```text
request : <iq xmlns="w:m" type="set" to="s.whatsapp.net"><media_conn/></iq>   (via existing QueryRouter/IQ seam)
response: <media_conn auth="{token}" ttl="{s}" auth_ttl="{s}" max_buckets="{n}">
            <host hostname="{h}"/>…
SDK caches {auth, hosts, fetchedAt} process-local per account; refresh when now > fetchedAt + ttl.
```

### D-64: Upload (whatsmeow `rawUpload` + Baileys `getWAUploadToServer` behavioral; paths from Baileys `MEDIA_PATH_MAP` [VERIFIED])

```text
1. mediaKey := 32 CSPRNG bytes; encrypt per D-61 (streaming: file/Stream → temp-file ciphertext, never full byte[] for large media)
2. token := base64url-nopad(fileEncSha256)
3. POST https://{host}/mms/{mmsType}/{token}?auth={urlenc(auth)}&token={token}
   mmsType: image→"image" | audio/ptt→"audio" | document→"document"   (Baileys MEDIA_PATH_MAP; whatsmeow mediaTypeToMMSType agrees)
   headers: Content-Type: application/octet-stream, Origin: https://web.whatsapp.com (+ Referer), Content-Length exact
   host: first media-conn host; on transport failure refresh conn (force) and try next host (Baileys tries all upload hosts)
4. response JSON {url, direct_path, ...} → populate proto fields url/directPath/mediaKey/fileSha256/fileEncSha256/fileLength(+mediaKeyTimestamp=now)
5. E2EE-send the populated Message proto through the EXISTING gated path (D-66) — upload is inside the gate (after admission, before inner send) so uploads count as send activity and never happen for refused sends
```

- `HttpClient` discipline: one `SocketsHttpHandler`-backed `HttpClient` per account (header isolation, Phase-7-safe), `CancellationToken` end-to-end, explicit timeout (Baileys `UPLOAD_TIMEOUT = 30000` — adopt 30 s upload / generous download timeouts as SDK-owned policy, tunable via options), progress via `IProgress<long>` optional param, Polly `ResiliencePipeline` (8.8.0) for transient HTTP retries only.
- Thumbnail (images): host supplies `jpegThumbnail` bytes (optionally with width/height); SDK validates `≤ 64 KiB` and passes through. The SDK takes NO image-processing dependency (no SkiaSharp/ImageSharp — keeps the dependency tree at the STACK.md minimum). Document in XML docs + sample shows a 32-px JPEG recipe.
- Upload size caps are ban-safety + abuse surface: new `SafetyOptions` fields (D-68).

### D-65: Download (whatsmeow `DownloadMediaWithPath`/`downloadAndDecrypt` behavioral)

```text
1. require directPath starts with "/" (fail-closed SdkFormatException otherwise)
2. url := https://{host}{directPath}&hash={base64url(fileEncSha256)}&mms-type={image|audio|video|document}&__wa-mms=
   host := first media-conn host (refresh non-force; Baileys honors a proto-carried non-default host — accept but re-resolve through our conn cache)
3. GET with Origin/Referer (+User-Agent browser string) headers; honor Retry-After; ≤5 attempts across hosts for NETWORK errors only
4. terminal statuses → typed outcomes, NO host retry: 403/404/410 = EXPIRED → media-retry flow (D-66b); hash/MAC failures = fail-closed errors (never deliver partial bytes)
5. verify per D-62 (enc-hash → MAC → decrypt → plain-hash), stream to host (Stream handle, size-capped — D-67); verify Content-Length against fileLength when present
```

## Media-Retry Flow (download-expiry recovery, host-invisible)

### D-66a: Trigger + request (whatsmeow `mediaretry.go` + Baileys `encryptMediaRetryRequest` behavioral, consistent)

```text
trigger: download GET → 403 | 404 | 410   (whatsmeow ErrMediaDownloadFailedWith403/404/410; 410 "gone" also routes here, NOT to host error)
request node:
  <receipt id="{msgId}" to="{own-non-AD-JID}" type="server-error">
    <encrypt><enc_p>{gcm-ciphertext}</enc_p><enc_iv>{12B iv}</enc_iv></encrypt>
    <rmr jid="{chat}" from_me="{true|false}" [participant="{sender}"]/>   // participant only for groups
```

- `to` is the OWN account JID (the phone re-uploads to us) — not the peer. `from_me` reflects the original message direction; `jid` is the chat.
- Sending the retry receipt is protocol traffic (dispatcher/whitelisted egress, NOT safety-gated — same standing as ack/retry receipts), but it MUST be capped: max 3 retry requests per message id, process-local tracker keyed by msg id (timestamp + attempts), then surface a typed `MediaDownloadExpired` outcome/event and stop (prevents retry storms — ban-safety interaction).

### D-66b: Response + recovery (whatsmeow `parseMediaRetryNotification` + Baileys `decodeMediaRetryNode` behavioral)

```text
inbound: receipt node carrying an <rmr> child  → route to media-retry handler (NOT the Pattern-5 decrypt-retry path;
         dispatch key = presence of <rmr>, orthogonal to type="retry")
  <error code="{n}"/> present → typed failure (code 2 = media gone on phone → terminal; else retryable-once)
  else <encrypt><enc_p>/<enc_iv> → GCM-decrypt (D-62) → MediaRetryNotification
    result == SUCCESS(1) → re-download from notification.directPath (fresh), verify per D-62, deliver; cache the fresh path on the recent-cache entry
    result == NOT_FOUND(2)/DECRYPTION_ERROR(3)/GENERAL_ERROR(0) → typed failure event; no further auto-requests past the cap
correlation: stanzaId/msg id == original message id; the mediaKey comes from the recent-cache ResendPayload media branch (D-69) — no new store
host contract: DownloadAsync either returns the verified Stream or throws/surfaces a typed expiry-exhausted outcome; the retry loop itself is invisible (SC-2)
```

- Interaction with Phase-4 retry receipts: disjoint mechanisms sharing only the recent cache. Same-id resend (Pattern 5) re-sends OUR message bytes; media-retry re-fetches THEIR re-uploaded bytes. The dispatcher must test `<rmr>` presence BEFORE `type="retry"` handling. Group media-retry adds `participant` on `<rmr>` (mirror the group ack `participant` precedent).

## Receive-Path Integration (D-67)

1. Dispatcher decrypts outer E2EE exactly as today (1:1 ratchet / group sender-key; D-06 persist→ack ordering unchanged — the ack still goes out after durable session writes, BEFORE any media download).
2. Parse `Message`: if `imageMessage`/`documentMessage`/`audioMessage` present → media branch (else existing text path, untouched).
3. Validate media fields fail-closed: `mediaKey` 32 bytes, `fileSha256`/`fileEncSha256` 32 bytes, `directPath` non-empty starting with `/` after host-prefixing, `fileLength` within caps, mimetype on allow-list, `fileName` sanitized (no directories/traversal), `seconds`/`dimensions` range-checked (untrusted).
4. Download + verify per D-62/D-65. On 403/404/410 → automatic D-66a/D-66b recovery (no host action).
5. Deliver a typed `MediaReceivedEvent` (EventHandler twin + `WhatsAppEvent` stream record, MSG-07 two-shape precedent) carrying metadata (kind, mimetype, fileLength, caption/fileName/seconds/ptt, message id/chat/sender/timestamp) plus a `Stream` content handle. **No unbounded `byte[]` buffering**: Stream pipes the verified download (temp-file-backed for large media so slow hosts apply backpressure without RAM blowup; `Channels` precedent for backpressure). Host owns disposal; undisposed streams are bounded by the temp-file cleanup policy (documented).
6. Recent-cache write keeps the existing entry shape (plaintext `ResendPayload` already contains the media branch incl. `mediaKey` — the media-retry correlator parses it back out). Opt-in delivery receipt + quoting (`contextInfo`) reuse the Phase-4 paths unchanged.

## Safety / Store Integration Notes

### D-68: Safety interaction (all media sends are ordinary sends)

- The three new public APIs (`SendImageAsync`, `SendDocumentAsync`, `SendVoiceNoteAsync` — names illustrative; planner fixes exact signatures + `PublicAPI.Unshipped.txt` entries) each call `EnsureOwnPrekeysAsync` then `_safety.ExecuteSendAsync(new SendContext(chat, IsReply: false), …)` exactly like the text paths [VERIFIED: Messenger.cs:184-193]. Upload happens INSIDE the gate (after admission): refused sends never touch mmg; successful sends record warm-up + recipient-contact + breaker outcomes identically to text.
- Voice notes request the `recording` pacing pause (SAF-06 `HumanPacing` seam; images/documents use the `composing` pause). Active-hours, jitter, token buckets (minute/hour/day), warm-up ladder, cold-outreach budget, and circuit breaker all apply unchanged — media bytes do not get a separate budget.
- New `SafetyOptions` caps (SDK-owned policy, `Validate` + safe defaults, `AllowUnsafeThroughput` opt-out precedent): `MaxImageBytes`, `MaxDocumentBytes`, `MaxAudioBytes` (+ `MaxThumbnailBytes = 65536`). Values are heuristics, not WA policy [ASSUMED — pick conservative starters (e.g. 16/64/16 MiB), tunable; planner records them as tunables in the risk ledger]. Oversize fails closed BEFORE upload with rule-only `SafetyRuleViolationException`.
- Risk-ledger additions (05-05 ledger precedent): media uploads are high-bandwidth account activity (upload-then-send doubles observable traffic); bulk media fan-out is the fastest ban path; the SDK does not transcode/validate media semantics (a corrupt file still sends); expiry-recovery retry requests are capped protocol traffic.
- Send-path audit extension (05-01 precedent): new media APIs must appear as gated callers; `MediaUploader`'s mmg POST and the retry-receipt sender join the `ProtocolEgress`-style whitelist enum; any new `SendNodeAsync`/`HttpClient` egress outside the inventory fails the test.

### D-69: Store seam (no new store interface)

- **No new store interface.** Media keys for OUR sends and received media both live inside the E2EE `Message` plaintext, which is already the `RecentMessageEntry.ResendPayload` (few hundred bytes — hashes + url + 32-byte key; media bytes NEVER enter the cache). The media-retry correlator parses the media branch back out of the cached payload by message id.
- Media-conn `{auth, hosts, fetchedAt, ttl}` is process-local per account (whatsmeow `MediaConn` precedent) with TTL refresh — never persisted (auth tokens are short-lived; a restart re-queries one IQ).
- Pending media-retry tracker (msg id → attempts + first-request timestamp) is process-local, bounded (evict entries older than 1 h / cap 4096 entries mirroring `ReceiptHandler.MaxRetryCountKeys`), never durable.
- Contract-test impact: none on the store kit (no interface changes); `RecentMessageEntry` needs no shape change. If a future phase wants durable media keys, that is a new seam proposal then — explicitly out of Phase 6.

## Outbound Node Deltas (exact strings — whatsmeow `send.go` behavioral)

- `<message>` `type` attr: `"media"` for all three kinds (`getTypeFromMessage` returns `"media"` whenever a media branch is present; text stays `"text"`). Current C# hardcodes `type="text"` [VERIFIED: MessageSender.cs:215,398] — the media path MUST set `"media"`.
- 1:1 `enc` children carry `mediatype="image" | "document" | "audio" | "ptt"` (`getMediaTypeFromMessage`; voice note with `ptt=true` → `"ptt"`, plain audio → `"audio"`). Present only on the 1:1 path (DSM plaintext present); group `skmsg` enc child carries the same `mediatype` attr.
- Everything else (per-device `<to jid>` fanout, `enc v="2" type="msg|pkmsg"`, `device-identity` on pkmsg, `phash`, `t`, `id`, SKDM fanout for groups) is IDENTICAL to text — reuse `MessageBuilder.AssembleMessageNode` + `MessageSender` fanout, do not fork.
- Media-retry receipt `type="server-error"` and `<rmr>`/`<encrypt>`/`<enc_p>`/`<enc_iv>` tags are exact wire strings from both references.

## Test / Fixture Plan

### Wave-0 byte gate (blocks all network work — 04-01 spike precedent)

- `media-crypto-fixtures.json`: fixed 32-byte `mediaKey` hex × {Image, Audio, Document} info → assert `iv`/`cipherKey`/`macKey` hex; fixed plaintext → assert full `ciphertext||mac` hex, `fileSha256`, `fileEncSha256`; tamper vectors (flip one MAC byte, one ciphertext byte, wrong info string) → all three verifiers fail-closed; cross-type vector (Image-key bytes verified under Document info → fail).
- Fixture generation: a checked-in generator (fixed key, no live traffic) + rectangles: fixtures are generated by the .NET implementation itself on first creation, then FROZEN and hand-verified against an independent Baileys one-liner (`getMediaKeys` + `encryptedStream` outputs for the same fixed key/plaintext) during UAT — any mismatch fails the gate. [ASSUMED: Baileys cross-check available via node in dev env; fallback is whatsmeow-computed vectors by hand.]
- `media-retry-fixtures.json`: fixed mediaKey + msg id → assert retryKey hex; GCM encrypt→parse→decrypt loopback; wrong-AAD decrypt fails; `MediaRetryNotification{SUCCESS, directPath}` proto round-trip bytes.

### Per-plan coverage

- Schema: proto round-trip facts per branch (field numbers asserted, unknown-field tolerance, baseline `1/2/6/35` stability fact).
- Upload: `HttpMessageHandler` stub (fake media-conn IQ + fake mmg POST capturing exact path/query/headers/body) → assert URL shape, `Origin` header, `Content-Length`, response mapping; force-refresh path; Polly transient-retry fact (500 → retry, same bytes).
- Send: gated-send facts per new API (gate refusal → zero HTTP calls, zero ids consumed); `type="media"` + `mediatype` attr facts; group media skmsg+mediatype fact; send-path audit extension green.
- Receive: fake E2EE plaintext → media event facts; Stream content equals original bytes; tamper (bad MAC/enc-hash/plain-hash) → typed failure, nothing delivered; oversize/unsigned fields → fail-closed.
- Expiry/retry: loopback server-error receipt encrypt→route-by-`<rmr>`→notification decrypt→re-download fact; 403→request→fresh-path→deliver end-to-end over fakes; cap exhaustion (3 requests → typed expired outcome, 4th never sent); `<error code=2>` terminal fact.
- No-live-phone CI story: everything above runs offline. Live UAT (real phone: send image/doc/voice-note, receive, expire-and-recover) is end-of-phase manual, documented NOT VERIFIED when no device (03-07/04-05 precedent).

## Pitfalls (media-specific landmines)

1. **Key reuse across types.** Using one `mediaKey` with two info strings (or re-uploading with a cached key) destroys per-type isolation. Fix: fresh CSPRNG key per upload, zeroized after; golden fixture asserts Image≠Document expansion from the same key.
2. **Random-IV instinct.** The media IV is HKDF-derived and stored NOWHERE on the wire (receiver re-derives). Prepending a random IV (Baileys' generic `aesEncrypt` helper shape) breaks `fileEncSha256` and interop. Fix: separate `MediaCrypto` encrypt path that never touches the generic helper; fixture pins exact blob bytes.
3. **MAC-then-compare truncation.** Comparing the full 32-byte HMAC against the 10-byte wire MAC always fails; skipping truncation silently accepts nothing. Fix: `Truncate(10)` constants on both sides + tamper fixture.
4. **Hash-field swap.** `fileSha256` (plaintext) vs `fileEncSha256` (ciphertext+mac) swapped → verifies the wrong object and delivers corrupt bytes as "valid". Fix: named wrapper types or at minimum distinct param names + a swapped-fields negative fact.
5. **Baileys' unverified download.** Baileys decrypt-streams without checking MAC/hashes; copying that shape turns tampering into silent corruption. Fix: D-62 verify-then-decrypt order is non-negotiable; review gate checks the order.
6. **Node-type attr carryover.** Forgetting `type="media"` / `mediatype` (leaving the text defaults) sends undecryptable-by-policy or misrendered messages; peers may retry-loop. Fix: attr facts per kind incl. `ptt → mediatype="ptt"`.
7. **`ptt` + codec discipline.** Voice notes REQUIRE `ptt=true` and Opus-in-Ogg bytes; sending mp3 bytes labeled `audio/ogg` plays nowhere, and `ptt=false` renders a plain audio file (loses voice-note UI + autoplay semantics). Fix: fail-closed mimetype allow-list + `ptt:true` forced on the voice-note API (a separate `SendAudioFileAsync` is out of scope — v1 is voice notes only).
8. **Filename/mimetype trust on receive.** `fileName` with path separators and `mimetype` claiming executables are abuse vectors when hosts save to disk. Fix: sanitize (strip directories, reject traversal), executable-MIME reject list, document that the SDK never writes to disk itself.
9. **410 means gone, not retryable-by-host.** Treating 410 as a host-visible error instead of the media-retry trigger breaks SC-2. Fix: 403/404/410 uniformly enter the retry flow; only cap-exhaustion or `<error>` surfaces.
10. **ResendPayload secret hygiene.** Cached plaintext now embeds `mediaKey` — existing no-keys-in-logs rule extends to media debug sites; the retry correlator parses keys in-memory only. Fix: code-review gate + no new logging near media paths.

## Decisions (numbered D-6x)

| # | Decision | Rationale | Alternatives rejected |
|---|----------|-----------|----------------------|
| D-60 | HKDF-SHA256 key expansion exactly per layout above (112 B, zero salt, `WhatsApp <Kind> Keys` info) | Byte-exact agreement of both references; fixture-pinned | BouncyCastle HKDF (unneeded — BCL `HKDF` covers it); per-file random IV (breaks interop) |
| D-61 | Streaming encrypt-then-MAC (AES-256-CBC + 10-B HMAC over `iv\|\|ct`, hashes over full blob) | Matches both references byte-for-byte; constant memory for large files | Whole-file `byte[]` encrypt (RAM blowup, multi-account hostile); GCM for media at rest (wrong primitive — GCM is retry-only) |
| D-62 | Verify-then-decrypt, fail-closed, three checks (enc-hash → MAC → plain-hash) | whatsmeow order; Baileys' unverified streaming is a corruption vector | Decrypt-then-verify / best-effort delivery (silent corruption) |
| D-63 | Media-conn via existing IQ seam, process-local TTL cache, per account | whatsmeow `MediaConn` precedent; tokens are short-lived, never durable | New store interface for conn tokens (over-persistence of ephemeral auth) |
| D-64 | Upload inside the safety gate via per-account `HttpClient` (SocketsHttpHandler), Polly HTTP-only, host-supplied thumbnails, no image-processing dependency | Gate-before-upload prevents refused-send traffic; Polly's HTTP vocabulary fits mmg; keeps dep tree at STACK.md minimum | SDK-side thumbnail generation (new native dep); upload outside gate (ungated egress); shared static HttpClient (header/cross-account leakage) |
| D-65 | Download URL from `direct_path` + conn hosts, 5-attempt network retry, 403/404/410 → retry flow (never host error) | whatsmeow `DownloadMediaWithPath` precedent; SC-2 demands host-invisible recovery | Surfacing 404 to host (breaks SC-2); unlimited host-hopping (retry storms) |
| D-66 | Media-retry as capped protocol loop (≤3 requests/msg, `<rmr>`-routed, GCM per layout) disjoint from Pattern-5 decrypt retries | Both references agree on wire shape; caps protect ban-safety; `<rmr>` routing avoids conflating the two retry kinds | Reusing the decrypt-retry counter (wrong correlation); uncapped requests (abuse); host-driven recovery (breaks SC-2) |
| D-67 | Typed `MediaReceivedEvent` + Stream handle (temp-file-backed, size-capped, host-disposed), ack-before-download ordering | MSG-07 two-shape precedent; backpressure without RAM blowup; D-06 ack ordering preserved | `byte[]` payload events (unbounded RAM); download-before-ack (crash ⇒ redownload storms) |
| D-68 | New `SafetyOptions` byte caps + recording/composing pacing; media = ordinary sends in all budgets | Ban-safety treats media as high-risk traffic; no parallel budget system (05 anti-pattern: one gate) | Separate media limiter (policy fork); no caps (abuse surface) |
| D-69 | No new store interface; media keys via cached `ResendPayload`, conn/retry state process-local | ResendPayload already carries the media branch (small); ephemeral state stays ephemeral (D-08 policy-is-SDK-owned) | `IMediaKeyStore` seam (scope expansion for bytes already retained) |

## Open Questions

1. **Exact voice-note mimetype label** (`audio/ogg; codecs=opus` vs `audio/ogg`) — [ASSUMED] the former. *Recommendation:* assert permissively on receive (prefix `audio/ogg`), send the exact labeled bytes the host provides after allow-list check, and confirm the label renders as a voice note in live UAT.
2. **Byte-cap starter values** (`MaxImageBytes` etc.) — [ASSUMED] heuristics (16/64/16 MiB). *Recommendation:* planner picks conservative starters, marks them tunable in options XML docs + risk ledger; UAT adjusts only with justification.
3. **Upload/download timeout values** (30 s upload adopted from Baileys `UPLOAD_TIMEOUT`) — [ASSUMED] reasonable. *Recommendation:* options-tunable, `TimeProvider`-testable; no user question needed.
4. **Group media-retry `participant` routing** — mirrors the group-ack `participant` precedent [ASSUMED shape]. *Recommendation:* implement symmetric with acks; loopback fact with a group `<rmr>` pins it.
5. **Caption quoting on media replies** (`contextInfo` inside media branches) — schema supports it; Phase-6 scope is send/receive/download, replies-to-media ride free if trivial, else defer. *Recommendation:* planner timeboxes; no user question.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | HKDF salt is empty/zero (Go nil-salt ≡ zero-salt; Baileys no-salt default) | D-60 | Medium — fixed by Wave-0 fixture before any network code; Baileys cross-check backstop |
| A2 | CBC padding is PKCS7 on both sides | D-61 | Low — universal default; round-trip + cross-check fixtures pin it |
| A3 | Voice-note label `audio/ogg; codecs=opus`, Opus-in-Ogg bytes required | Schema, Pitfall 7 | Medium — cosmetic/functional; live UAT confirms rendering |
| A4 | Byte-cap starters (16/64/16 MiB) + 30 s upload timeout + 3 media-retry requests/msg | D-66/D-68, OQ 2–3 | Low — tunables, documented as heuristics not WA policy |
| A5 | `enc mediatype` values `image/document/audio/ptt` + node `type="media"` for all three kinds | Node deltas | Medium — attr facts + UAT render check; shape read verbatim from send.go |
| A6 | Group `<rmr participant>` mirrors group-ack shape | OQ 4 | Low — loopback-pinned; 1:1 path (SC-2) unaffected |

## Validation Architecture

Nyquist is enabled (`workflow.nyquist_validation: true` [VERIFIED: .planning/config.json:25]); security enforcement on, ASVS L1 [VERIFIED: .planning/config.json:49-51].

### Test Framework

| Property | Value |
|----------|-------|
| Framework | xunit.v3 4.0.1 + AwesomeAssertions 9.6.0 + NSubstitute 6.2.0 (locked) |
| Config file | repo-root `global.json` (MTP mode) — unchanged |
| Quick run command | `dotnet test Iwasug.WhatsApp.Sdk.sln --filter <class>` |
| Full suite command | `dotnet test Iwasug.WhatsApp.Sdk.sln` |

### Phase Requirements → Test Map

| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| MED-01 | media crypto golden fixtures (expand/encrypt/hash/MAC hex + tamper fail-closed) | unit + golden-fixture | `dotnet test --filter MediaCryptoFixtureTests` | ❌ Wave 0 |
| MED-01 | upload URL/headers/body exactness over stubbed mmg + conn refresh | unit (HttpMessageHandler stub) | `dotnet test --filter MediaUploadTests` | ❌ Wave 0 |
| MED-01 | image/doc/voice-note sends gated, `type="media"` + `mediatype` attrs, ack correlation, group skmsg shape | unit + loopback | `dotnet test --filter MediaSendTests` | ❌ Wave 0 |
| MED-01 | send-path audit extension (new media APIs gated; mmg/retry egress whitelisted) | static audit | `dotnet test --filter SendPathAuditTests` | ❌ Wave 0 (extend) |
| MED-02 | download→verify→Stream delivery; tamper/oversize/sanitize fail-closed | unit | `dotnet test --filter MediaDownloadTests` | ❌ Wave 0 |
| MED-02 | 403/404/410 → server-error receipt → `<rmr>` notification → fresh-path re-download, host-invisible; cap + error-code terminals | unit + loopback | `dotnet test --filter MediaRetryTests` | ❌ Wave 0 |
| MED-02 | retry-request GCM loopback (encrypt→parse→decrypt, wrong-AAD fails) | unit + golden-fixture | `dotnet test --filter MediaRetryCryptoTests` | ❌ Wave 0 |
| SAF-01 (regression) | gate refusal → zero HTTP + zero ids; caps enforced pre-upload | unit | `dotnet test --filter MediaSafetyTests` | ❌ Wave 0 |

### Sampling Rate

- **Per task commit:** `dotnet test Iwasug.WhatsApp.Sdk.sln` (repo is fast; no narrower gate needed — Phase-4/5 precedent)
- **Per wave merge:** full suite + NOTICE provenance-row audit (mirror 03-07 pattern)
- **Phase gate:** full suite green before `/gsd-verify-work`; live UAT with a real account for SC-1/2 (send trio intact, expiry recovery) documented NOT VERIFIED if unavailable

### Wave 0 Gaps

- [ ] `tests/.../TestData/media-crypto-fixtures.json` — expand vectors + encrypt/hash/MAC goldens + tampers (D-60/D-61)
- [ ] `tests/.../TestData/media-retry-fixtures.json` — retryKey hex + GCM loopback + notification proto bytes (D-62)
- [ ] `MediaCrypto` + `MediaConn` + `MediaUploader`/`MediaDownloader` + `MediaRetryTracker` production seams (Media/ folder)
- [ ] `message.proto` media-branch extension (hand-written, provenance header, `internal_access` — csproj pattern unchanged)
- [ ] Stub mmg `HttpMessageHandler` + fake media-conn IQ + fake `<rmr>` notification loopback harness
- [ ] `SendPathAuditTests` extension rows for the three new send APIs + mmg/retry whitelist entries
- [ ] `SafetyOptions` byte-cap fields + `PublicAPI.Unshipped.txt` entries for new public surface

## Security Domain

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | pairing + Signal identity unchanged |
| V3 Session Management | no | store seam untouched (D-69) |
| V4 Access Control | no (single-account phase) | account isolation is Phase 7 |
| V5 Input Validation | yes | fail-closed media-field validation (32-B hashes, `/`-prefixed directPath, MIME allow-list, filename sanitize, size caps, `FixedTimeEquals` MAC compare) |
| V6 Cryptography | yes | BCL AES-CBC/HMAC/HKDF/SHA-256/RNG + in-box AES-GCM; NSec untouched; `ZeroMemory` on key material; verify-then-decrypt order |

### Known Threat Patterns for this stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Tampered media blob delivered as valid | Tampering | triple verification (D-62); Baileys' unverified shape explicitly rejected |
| Malicious `fileName`/`mimetype` (path traversal, executable save) | Tampering / Elevation | sanitize + reject-list; SDK never writes to disk itself |
| Retry-storm / re-upload abuse | Denial of Service | ≤3 retry requests per msg id; bounded process-local tracker; protocol-egress (not host-loopable) |
| Ungated media egress (new send APIs bypassing SafetyGate) | Elevation | audit-test extension; upload inside the gate |
| Key/URL material in logs, events, exceptions | Information Disclosure | repo secret-hygiene rule extended to media paths; review gate |
| Oversize media as RAM/pipe DoS | Denial of Service | byte caps pre-upload; Content-Length + streaming caps on download; temp-file backing |

## Sources

### Primary (HIGH confidence)

- `WhiskeySockets/Baileys@0af2386 WAProto/WAProto.proto` (MIT) — `Message` branches (imageMessage=3, documentMessage=7, audioMessage=8), full Image/Document/AudioMessage field tables, `MediaRetryNotification{1,2,3}` + `ResultType`, `ServerErrorReceipt{1}` — read verbatim this session
- `WhiskeySockets/Baileys@0af2386 src/Utils/messages-media.ts` (MIT) — `getMediaKeys` (112-B HKDF, iv/cipher/mac slices), `hkdfInfoKey` (`WhatsApp ${kind} Keys`), `encryptedStream` (derived-IV CBC, IV-covered HMAC, 10-B MAC, shaP/shaE incl. MAC), `downloadEncryptedContent` (URL from directPath, `Origin` header, range/IV handling), `getWAUploadToServer` (path map, auth/token query, host fan-out), `encryptMediaRetryRequest`/`decodeMediaRetryNode`/`decryptMediaRetryData` (GCM, 12-B IV, AAD=msgId, `<rmr>` routing), `getAudioWaveform` (64×0–100) — read verbatim this session
- `WhiskeySockets/Baileys@0af2386 src/Defaults/index.ts` (MIT) — `MEDIA_PATH_MAP` (image/audio/document/video paths), `MEDIA_HKDF_KEY_MAPPING` (Audio/Document/Image/Video/ptt→Audio…), `DEF_MEDIA_HOST=mmg.whatsapp.net`, `DEFAULT_ORIGIN`, `UPLOAD_TIMEOUT=30000` — read verbatim this session
- `tulir/whatsmeow@main download.go/mediaconn.go/upload.go/mediaretry.go/errors.go/send.go` (MPL-2.0, **behavioral read-only, nothing copied**) — `MediaType` info strings, `getMediaKeys` 4-slice split, `Upload`/`UploadReader` (fresh 32-B key, encrypt→10-B MAC→POST `/mms/{type}/{token}?auth&token`), `queryMediaConn` (`w:m` `media_conn` IQ, auth/ttl/hosts), `DownloadMediaWithPath` (URL shape with `hash`/`mms-type`/`__wa-mms=`, `/`-prefix rule, 5-attempt host fan-out, 403/404/410 terminals), `validateMedia` (MAC over iv‖ct, 10-B), `SendMediaRetryReceipt` (`server-error` + `<encrypt enc_p/enc_iv>` + `<rmr jid/from_me/participant>`), `parseMediaRetryNotification` (`<rmr>` routing, error-code terminals), `getTypeFromMessage` (`"media"`), `getMediaTypeFromMessage` (`image/document/audio/ptt`), 1:1 + group `mediatype` enc attrs — read verbatim this session
- In-repo seams: `Proto/message.proto:1-78`, `Messaging/MessageBuilder.cs`, `MessageSender.cs:215,398` (`type="text"` hardcoded), `Messenger.cs:184-193` (gate call shape), `MessageDispatcher.cs` (D-06/D-20 ordering), `Safety/ISafetyGate.cs`, `SafetyGate.cs`, `Stores/IRecentMessageCacheStore.cs:127-133` (`RecentMessageEntry` incl. `ResendPayload`), `Safety/SafetyOptions.cs` (no media caps — gap D-68 fills), `.planning/config.json:25,49-51` (nyquist + security flags)

### Secondary (MEDIUM confidence)

- `.planning/phases/04-signal-e2ee-messaging/04-RESEARCH.md` (patterns A2/P2, schema-provenance discipline) + `.planning/phases/05-ban-safety-layer/05-RESEARCH.md` (gate vocabulary, audit precedent, pacing seam)
- `.planning/research/STACK.md` (pinned versions; Polly-Core-for-HTTP pre-authorization)

### Tertiary (LOW confidence)

- Voice-note mimetype label exactness, byte-cap/timeout starter values, Baileys-cross-check availability in dev env — all [ASSUMED], fixture/UAT-gated (Assumptions A1–A6)

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new packages; versions already pinned (STACK.md + csproj)
- Architecture: HIGH — upload-then-E2EE-send and `<rmr>`-routed recovery verified in both references
- Byte layout: HIGH with fixture gate — every byte cross-checked in two independent implementations; Wave-0 fixtures settle the salt/padding residuals
- Pitfalls: HIGH — each maps to a verified wire/c-indent behavior, not speculation

**Research date:** 2026-10-14
**Valid until:** 2026-11-13 (stable protocol surface; re-verify Baileys pin if upstream advances)
