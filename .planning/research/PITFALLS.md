# Pitfalls Research

**Domain:** Unofficial WhatsApp Web multi-device client SDK for .NET (protocol reimplementation, multi-tenant, ban-safety, open-source NuGet)
**Researched:** 2026-10-07
**Confidence:** MEDIUM (cross-checked across upstream issue trackers, official specs, and academic sources; single-source web findings cap at LOW per source hierarchy)

## Critical Pitfalls

### Pitfall 1: Hard-pinning the WhatsApp Web protocol version → connection dies with no QR (status 405)

**What goes wrong:**
The client pins a WA Web version number at build time (e.g. `2.3000.x`). WhatsApp ages the pin out and rejects the connection during registration with a "Connection Failure" / status 405 *before a QR code is ever issued*. Pairing silently stops working weeks after a release. Secondary drift: individual features break after server updates even when the handshake works (whatsmeow #705 view-once broke after a WA update; #1197 "unable to message new contact after July 2nd update"; Baileys #2248 companion-protocol enforcement change → immediate 401).

**Why it happens:**
Version numbers look like compile-time constants, and the handshake works during development, so drift isn't noticed until production. The reconnect layer often retries the *same* rejected version forever.

**How to avoid:**
- Resolve the live version at connect time from `web.whatsapp.com` (the source of truth), cache ~6h, with a fallback to a bundled pin.
- On 405/connection-failure, clear the cached version so the next attempt re-resolves instead of retrying a known-rejected value.
- Keep protobuf definitions additive-friendly (unknown fields tolerated) so a field addition degrades gracefully instead of desyncing.
- Subscribe to upstream signals: watch whatsmeow/Baileys issue trackers and WhatsApp Web changelists as an early-warning feed (cheap, high signal).

**Warning signs:**
- QR never appears but WebSocket connects; logs show status 405 / "Connection Failure".
- Tests pass against a recorded fixture but live pairing fails after N weeks in the field.
- Intermittent "can't link new devices" on phone (whatsmeow #1191 pattern).

**Phase to address:** Pairing/Noise transport phase (runtime version resolution as part of connect flow) + a standing "drift monitoring" item in the release/CI phase.

---

### Pitfall 2: Signal ratchet advanced but not persisted (disconnect mid-encrypt) → permanent desync → 401 forced re-pair

**What goes wrong:**
During send, the double ratchet advances in memory. If the connection/context dies between "encrypt" and "persist session state", the store keeps the *stale* (pre-encrypt) ratchet state. On reconnect the client encrypts from state N while the server has already seen N+1 → MAC verification fails → server terminates the session (401 device_removed). The only recovery is deleting state and re-scanning the QR. (Documented as whatsmeow #1066.)

**Why it happens:**
Session caching is threaded through request-scoped context/cancellation; the persistence write is coupled to the same cancellable operation that the network failure just cancelled. Classic "save happens after the thing it must not outlive" ordering bug.

**How to avoid:**
- Contract: **persist-after-encrypt, with a cancellation-insensitive write.** The store write for advanced ratchet state must not inherit the send's cancellation token (`CancellationToken.None` / detached token in .NET).
- Encrypt-persist-send ordering; never send a ciphertext whose session state isn't durably recorded (or accept the ratchet-advance being idempotently recoverable).
- Treat the pluggable store's `PutSessions` as the most safety-critical operation in the SDK — document it loudly in the store interface XML docs ("must complete or be retried; failure risks forced re-pair").

**Warning signs:**
- 401 logouts that correlate with network blips, not user logouts.
- `BadMAC`/decrypt failures on *our own* outgoing message sequence numbers.
- Any code path where `await store.Put...` is inside a `try/catch` that swallows on `OperationCanceledException`.

**Phase to address:** Signal/E2EE session phase — this contract must be designed in from day one; retrofitting it later means auditing every encrypt path.

---

### Pitfall 3: Treating app-state (LTHash) patch verification failures as fatal → server unlinks the session

**What goes wrong:**
WhatsApp pushes app-state sync patches (contacts, mutes, pins, push name…) with rolling LTHash verification. A patch storm — commonly triggered when the user links *another* WhatsApp Web on a PC — can produce "mismatching LTHash" on some patches. A client that treats that as fatal aborts the sync, the server sends a stream-end, and the device gets unlinked (401). Re-scanning QR does **not** fix it (whatsmeow #651, #1203; upstream #858).

**Why it happens:**
LTHash verification looks like a security check, so implementers wire it to `throw`. But patches can legitimately arrive unverifiable (snapshot/patch gaps, server quirks), and the spec's recovery path is "skip and re-sync", not "die".

**How to avoid:**
- Make patch-verification failure **non-fatal by default**: log, emit an `AppStateSyncError` event, skip the broken patch, and schedule a full re-fetch (`FetchAppState(fullSync)` / snapshot recovery).
- Only escalate to a session teardown if re-sync *repeatedly* fails.
- Persist app-state hash state per collection atomically with the applied mutations — partial application of a patch batch is what makes the hash drift.
- Ack notifications *before* heavy processing so a slow sync doesn't stall the stream.

**Warning signs:**
- Logs: `failed to verify patch v…: mismatching LTHash`, `AppStateSyncError`.
- Session disappears shortly after the account owner opens real WhatsApp Web on a new browser/PC.
- Push name or contact list wrong/stale after reconnect.

**Phase to address:** App-state sync phase (explicitly designated a "survivable error" path in design docs).

---

### Pitfall 4: Reconnect logic that treats every disconnect the same (or loops at a fixed interval)

**What goes wrong:**
Two opposite failures: (a) auto-reconnecting on 401/logged-out forever (looks like a ban-evasion bot, hammers servers, hides the real problem from the operator); (b) giving up or silently retrying on transient 428/408/503, so the session dies when it didn't need to. Fixed 3-second reconnect loops with no status callback leave UIs stuck at "Connecting…" while pairing silently fails. In-flight IQ queries (usync device list) fail with "websocket disconnected before info query returned response" if commands aren't tied to the connection generation.

**Why it happens:**
Disconnect codes are treated as strings/ints without a taxonomy; timers aren't generation-checked, so a stale reconnect races a new one; pending command maps aren't flushed on disconnect.

**How to avoid:**
- Implement a **disconnect-reason taxonomy** with per-reason policy:
  - `401` loggedOut / `device_removed` → terminal: raise `LoggedOut` event, stop reconnecting, mark account as needing re-pair (never auto-reconnect).
  - `428`/`408`/network → reconnect with exponential backoff (e.g. 3s → 60s cap + jitter).
  - `515` restartRequired → reconnect promptly (server requests it after pairing).
  - `440` connectionReplaced → pause, surface to operator (another client took the slot).
  - `411` multideviceMismatch → surface "library too old" (drift signal, see Pitfall 1).
- Track *consecutive* closes; surface an error state after the 2nd failure instead of silently looping; reset on reaching QR/open.
- Attach a connection generation/epoch to every pending IQ; fail-fast in-flight commands on disconnect and retry after the new connection opens.
- Assume periodic server-initiated disconnects are normal (regional policies reportedly force Web session logouts every ~6h in some markets) — "connected for weeks without a reconnect" must not be a design assumption.

**Warning signs:**
- Reconnect attempts logged at a constant interval with no backoff.
- "Connecting…" shown indefinitely; operator never learns pairing is broken.
- Duplicate/stale command responses applied to the wrong connection.

**Phase to address:** Connection lifecycle/reconnect phase (before multi-account scale-out — bugs here multiply by account count).

---

### Pitfall 5: Crypto byte-fidelity errors — Noise framing, certificate chain, XEdDSA

**What goes wrong:**
The handshake is `Noise_XX_25519_AESGCM_SHA256` with WhatsApp-specific deviations: custom header/prologue, 3-byte big-endian length framing, protobuf-wrapped handshake messages, and an XEdDSA certificate chain (root→intermediate→leaf, leaf key bound to the handshake server static, issuer serial check). A real-world port shipped with cert verification that only checked serial linkage and skipped leaf/intermediate **signature** and **validity-window** validation — an active MITM could mint a fake chain and poison the cached IK key (enabling a silent downgrade on subsequent connects). Other classic errors: wrong IV/counter handling in the pre-transport AES-GCM phase, frame reassembly bugs (partial frames across TCP reads), and reusing transport keys as handshake keys.

**Why it happens:**
The handshake is a small state machine where every byte matters, "it connects" looks like success, and dev/test environments don't include a hostile peer. Skipping cert validation is the classic way to make a failing handshake pass.

**How to avoid:**
- Full certificate validation as a hard gate: verify intermediate signature against the pinned WA issuer key, leaf signature against intermediate key, **and both** `not_before`/`not_after` windows — before accepting or caching the server static.
- Vectors-based tests: byte-exact fixtures for handshake messages, frame encoding, and HKDF outputs; a fake-server integration test that fails if framing drifts.
- Interop testing against live WhatsApp in CI (gated/nightly) — recorded fixtures alone can't detect Pitfall 1.
- Keep the crypto primitive choices explicit: SHA-256-based HKDF with the exact salt/info used by the protocol; no "standard" Noise library defaults without the WA prologue.

**Warning signs:**
- Handshake passes only when validation code is commented out.
- Intermittent decrypt failures on frames arriving in bursts (reassembly bug).
- No test ever fails when you deliberately corrupt a certificate field.

**Phase to address:** Noise/transport phase (cert validation is a merge-blocking acceptance criterion).

---

### Pitfall 6: Double Ratchet edge cases — skipped message keys, out-of-order delivery, retry receipts

**What goes wrong:**
Implementations that handle the happy path (in-order, no loss) break in production: messages arrive out of order or dropped, and without a bounded `MKSKIPPED` store (indexed by ratchet key + message number) the client can't decrypt them. Missing **retry receipt** handling means an undecryptable message is never re-sent — the recipient just never gets it ("message delivered but not received", whatsmeow #1027 pattern). Missing the ratchet-state *rollback* on failed decryption corrupts the session. Not bounding skipped keys is a memory DoS vector (Signal's own implementation caps at 2000 messages / 5 DH steps).

**Why it happens:**
Demo testing is two clients on localhost with no packet loss; the subtlety of PN/N header bookkeeping hides until lossy real networks.

**How to avoid:**
- Implement per spec §2.6 exactly: store skipped keys, enforce hard caps, delete on use, discard state changes on decrypt failure.
- Handle both directions of the retry protocol: on `UndecryptableMessage` → send retry receipts (with attempt count); on receiving a retry receipt → re-encrypt from the *original* message (requires a sent-message cache — whatsmeow keeps ~256 recent messages plus a persisted fallback store).
- Property tests: random reorder/drop/duplicate of a message stream must still decrypt everything recoverable and never desync the session.

**Warning signs:**
- Occasional messages that one side shows as sent but the other never displays.
- `MKSKIPPED`/session store growing without bound.
- Decrypt failure count with no corresponding retry-receipt traffic.

**Phase to address:** Signal/E2EE phase — retry receipts specifically belong with the send/receive path, not "later".

---

### Pitfall 7: Ban-safety bolted on late (or not enforced at the SDK boundary)

**What goes wrong:**
The SDK sends fine in tests, then accounts get restricted because: an unwarmed number blasts on day one; the *same* text goes to hundreds of recipients; first-ever messages to strangers (cold outreach) dominate traffic; bursts fire at machine-like regularity with no jitter; failures trigger retry storms; a reconnect causes a catch-up send burst. Sources converge: #1 ban trigger is "new number sending to contacts who never messaged you"; identical templated messages are "the loudest spam signal"; recipient reports/blocks cause near-instant restriction. Rate limiting exists but lives in the *host application* — one careless call path bypasses it.

**Why it happens:**
Rate limits are treated as a config knob on a send method rather than a mandatory gate around *every* egress path, and warm-up state is lost on restart, silently resetting trust.

**How to avoid:**
- Enforce the safety layer **inside the SDK at a chokepoint every send must pass** (not a helper the host may skip): sliding-window caps (conservative defaults ≈ 20/min, 250/hr, 1000/day), per-recipient cooldowns (30–60s), ±50% jitter, warm-up ladder persisted per account (≈ 20, 40, 80, 160, 320, 640, 1000/day over ~6–7 days), and a **separate cold-outreach budget** (first message to a chat with no history — far riskier than replies; inbound-triggered replies never count against it).
- Circuit breakers: burst-fail pause (e.g. 5 failures reaching WhatsApp in 60s → 5 min send-pause), I/O ratio guard (outbound/inbound > 5 at 50+ outbound → tighten), ban-signal detector (401/CONFLICT/timelock → stop all sends for that account and raise `AccountRestricted`).
- Reconnect throttle: no sends for a ramp window after (re)connect.
- Warm-up and known-chat state must live in the pluggable store, not process memory.
- Document honestly: layered mitigations "materially lower risk"; **never** market "ban-proof" — every credible operator states no one can promise it.

**Warning signs:**
- Host can call `SendMessage` without the safety gate (API shape review).
- Warm-up counters reset to day 1 after every deploy.
- Outbound:inbound ratio climbing; zero reply events on recent broadcasts.
- Send failure spikes met with immediate retries instead of backoff.

**Phase to address:** Ban-safety phase must be a *prerequisite gate for any live-traffic phase*, and its chokepoint belongs in the core send API built in the messaging phase.

---

### Pitfall 8: Multi-account isolation leaks in one process

**What goes wrong:**
Tens of accounts share a process; state bleeds between them: session/store records keyed by bare phone number instead of **full JID including device suffix**, so two sessions on one number become indistinguishable → session hijack, wrong-row deletes, registry data loss on boot (go-whatsapp-web-multidevice #762). Shared static/singleton state: one in-memory Signal session cache, one event bus, one rate-limiter bucket, one `HttpClient`/cookie jar. In .NET specifically: `static` fields, `AsyncLocal` flows across tenants, DI singletons capturing the first account's options. Store schemas without cascades leave orphan per-device data (whatsmeow #1038).

**Why it happens:**
Single-account code "works" and gets a dictionary wrapper; identity keys are chosen by the cheapest convenient field; global state is invisible until the second tenant misbehaves.

**How to avoid:**
- Identity is always the **full AD JID** (number + device/@lid parts); never key by bare number except as a fallback that must match exactly one row.
- Hard rule in architecture review: **no static mutable state, no singleton that holds account data**; everything account-scoped lives in an instance owned by the connection object (session cache, rate buckets, event handlers, timers).
- Store interface requires an account namespace on every read/write + cascade-on-delete; write an isolation test suite (two fake accounts, concurrent operations, assert zero cross-reads) that runs in CI.
- Beware the same account connected from two processes → `440` conflict; the SDK should detect and warn.

**Warning signs:**
- One account's reconnect clears another's state.
- Tests pass serially but fail when two clients run concurrently.
- A store implementation that "helpfully" returns the first row matching a JID.

**Phase to address:** Multi-tenant architecture (early — a constraint on store interface and object model, not a retrofit) + enforced by the pluggable-store phase's conformance tests.

---

### Pitfall 9: Licensing/reference contamination (MPL-2.0 hygiene isn't real)

**What goes wrong:**
whatsmeow is MPL-2.0 (file-level weak copyleft). The stated policy is "behavioral reference only, no code copying", but contamination happens through: pasting code then "translating" it (mechanical translation of MPL code is still a derivation), keeping copied files with headers stripped (that *is* distributing Covered Software without notices), letting an LLM assistant inline upstream code, or copying file/structure arrangements wholesale. Conversely, the team may over-restrict itself: MPL obligations only trigger on distributing MPL-covered files — protocol *behavior* and facts aren't the licensed material.

**Why it happens:**
"Reference" is ambiguous under time pressure; AI assistants and cargo-culted snippets blur provenance; nobody records where a constant/structure came from.

**How to avoid:**
- Written clean-room policy in the repo (CONTRIBUTING + docs): no verbatim copying, no mechanical translation, no header-stripped files; when stuck on protocol behavior, consult *public protocol documentation / captured wire behavior / issue discussions*, and if upstream code must be read, record the file+line as provenance and have a second person independently implement (lightweight two-person rule for the hairiest areas: Noise handshake, Signal, app-state).
- Add a provenance/NOTICE doc: what was referenced, where, when — cheap insurance.
- Choose the SDK's own license deliberately (MIT/Apache-2.0 both fine — no MPL obligation if zero MPL code is included) and include a license-attribution section so downstream users aren't confused about whatsmeow's relationship.
- PR checklist item: "any protocol constant or structure introduced — cite source (spec/observation/upstream reference with provenance)".
- Note (not legal advice): using the unofficial protocol also violates WhatsApp ToS; the SDK must document which risks it mitigates and which it cannot — PROJECT.md already requires this.

**Warning signs:**
- A PR containing Go-shaped C# or identical nontrivial functions with the same comment text.
- Constants/arrangements appearing without a citation.
- Copy-paste from a "whatsmeow port" repo (which may itself be contaminated).

**Phase to address:** Package/ship phase finalization — but the *policy file* should land in repo bootstrap, before protocol coding starts.

---

### Pitfall 10: Media pipeline failures — HKDF info strings, sidecars, expiring upload tokens

**What goes wrong:**
Media appears to work for test images then fails: wrong type-specific HKDF info string (each media type derives keys with its own info), wrong hash fields (`fileEncSHA256`/`fileSHA256` mismatch → recipient rejects), HMAC computed over the wrong bytes, no video/audio **sidecar** (64KB chunks, 16-byte overlap, 10-byte truncated HMAC) so seeking shows "media unavailable", and the upload's short-lived media connection token expiring → 401/403 with no refresh-and-retry. Forwarding an existing message re-uploads instead of reusing CDN fields (wasted egress + new key exchange). History-sync media returns 404/410 unless a media-retry receipt asks the phone to re-upload.

**Why it happens:**
Media crypto is a self-contained subsystem that passes "it opened in WhatsApp" tests for the simplest case; sidecar and token-refresh paths only trigger under specific media types, large files, or stale connections.

**How to avoid:**
- One shared crypto core for encrypt/decrypt with per-type info strings; golden-file tests: encrypt a known fixture per media type and compare hashes/MACs against reference outputs.
- Implement sidecar generation for video/audio from day one of the media phase (it's mechanical; retrofitting means re-testing every send path).
- Media connection: fetch → upload → on 401/403 invalidate + refresh once + retry; support `file_offset` resume for large files.
- Implement media-retry receipts for received media that 404s.

**Warning signs:**
- Recipients report "download failed" on documents while text works.
- Large-file uploads fail while small ones succeed (token lifetime / no resume).
- Seeking in sent videos fails (missing sidecar).

**Phase to address:** Media phase.

---

### Pitfall 11: PN/LID dual-identity handling (silent delivery failures)

**What goes wrong:**
Multi-device addressing has two identity systems — phone-number JIDs and LID (logical IDs) — with a persisted bidirectional mapping. Get it wrong and messages silently fail to encrypt for the right session or error with "PN→LID session migration failure" (whatsmeow #1027), device lists come back with the wrong identity, or `PutManyLIDMappings` stalls inbound processing (#1004). History-sync group messages often carry the participant at the `WebMessageInfo` level rather than `key.participant`, so a naive reader drops every inbound group message during sync (go-whatsapp-web-multidevice #763: 198 skipped → fixed by fallback resolution).

**Why it happens:**
WhatsApp migrated identities mid-protocol; two ID schemes coexist on the wire and in stores, and it's invisible until a contact's devices change.

**How to avoid:**
- LID↔PN mapping store as a first-class, persisted, account-scoped component with defensive fallback (resolve ambiguous rows as "unknown", never guess between siblings).
- Normalize every incoming JID through one function before any lookup; test with fixtures containing both `@s.whatsapp.net` and `@lid` forms.
- Watch for identity-change events (contact re-registered) and rebuild sessions deliberately rather than failing to encrypt.

**Warning signs:**
- "Sent" messages with no delivery receipt to specific contacts only.
- Inbound group history empty after re-pair while 1:1 works.
- Mapping-store write errors during sync storms.

**Phase to address:** Addressing/identity sub-piece of the messaging phase; history-sync parsing in the sync phase.

---

## Technical Debt Patterns

| Shortcut | Immediate Benefit | Long-term Cost | When Acceptable |
|----------|-------------------|----------------|-----------------|
| Bundled hard-pinned WA version, no runtime resolution | Ship faster | Pairing silently dies weeks later (405) | Never — resolution is ~30 lines |
| Skip Noise cert-chain signature/validity checks | Green handshake tests | MITM can poison cached IK; undiagnosable "works on my machine" | Never |
| Treat LTHash mismatch as fatal | Less error-path code | Sessions unlink whenever user links another Web client | Never |
| Session-state persist coupled to request cancellation token | Simpler plumbing | Random 401s after network blips → mass re-pairing | Never |
| Rate limiting as an optional helper, not an SDK chokepoint | Host flexibility | One bypass = banned account; undermines core value prop | Never for default path (a documented `Unsafe` escape hatch is the compromise) |
| In-memory-only warm-up/ban-state | Simpler store interface | Trust state resets every restart → new-account behavior on aged numbers | Only in MVP with docs requiring store persistence before production |
| Single shared `Dictionary` of Signal sessions across accounts | Faster first multi-account demo | Cross-tenant session bleed; heisenbugs at scale | Only behind a hard "single account" test mode |
| Implement only Noise XX (skip IK/XXfallback resume) | Less handshake code | Extra RTT per reconnect; more traffic pattern distinctiveness | Acceptable for v1 if fallback path handles server rejections |
| Skip media sidecar (video/audio) | Smaller media scope | Sent videos unseekable for recipients | Only if v1 scope explicitly excludes video (voice/documents OK) |
| Copy proto schemas from a third-party port instead of deriving | Quick protobuf setup | Contaminated provenance; inherits upstream's field mistakes | Never — derive from observed wire/behavior and cite |

## Integration Gotchas

| Integration | Common Mistake | Correct Approach |
|-------------|----------------|------------------|
| WhatsApp WebSocket endpoint | Missing/blocked `Origin` header; wrong host after WA rotates hosts | Set `Origin: https://web.whatsapp.com`; resolve current WS endpoint like version (runtime, cached) |
| QR pairing | Not regenerating expired QR; treating QR as durable credential | QR TTL is short (~20s ref / regenerate on expiry); only the completed pair-success payload creates a session |
| Pairing lifecycle | Assuming creds persisted at pair-success are complete; missing the 515 restart boundary | Persist at each pair step; treat 515 as "reconnect immediately with persisted state" |
| usync device-list / info queries | Query left pending across disconnect → hangs or stale response applied | Generation-tag pending IQs; fail fast on disconnect; retry after reconnect; account for 503 responses from WA servers |
| App-state sync notifications | Processing before ack; no `return_snapshot` fallback | Ack promptly; on version gap larger than available patches, request snapshot/full sync |
| Prekey upload | Ignoring the server's "low prekeys" notification; never rotating signed prekeys | Refill one-time prekeys when notified (batch ≈ 812, refill < 10 left); schedule signed-prekey rotation (WA ≈ monthly) |
| Media CDN | Reusing expired media upload connection; uploading then sending before hash computed | Refresh media token on 401/403 once; compute all hash fields during encrypt; resume via `file_offset` |
| Message retry | No sent-message cache → can't service retry receipts | Keep recent sent messages (memory ring + optional persisted store, ~256+ entries) so re-encryption on retry is possible |
| History sync | Assuming FULL sync on reconnect; any connected process silently consumes the one-shot bootstrap | Persist every sync chunk as it arrives; expose explicit request-history; document that initial sync only happens once after pairing |
| Presence/push name | Sending presence without push name set (`ErrNoPushName` class of errors) | Set push name during login payload; treat as required config |
| Proxy/egress | Assuming `HTTP_PROXY` env covers the WebSocket | .NET `ClientWebSocket` needs explicit proxy configuration; corporate/datacenter egress IP reputation affects acceptance (document proxy guidance) |

## Performance Traps

| Trap | Symptoms | Prevention | When It Breaks |
|------|----------|------------|----------------|
| Store interface chatty per ratchet step (load/put session per message) | Send latency spikes; DB CPU dominates at tens of accounts | Batch session writes per send-batch; in-memory session cache with write-behind (but see Pitfall 2 — flush-before-send is safety-critical, flush-before-*ack* is not) | ~10+ accounts × active chat volume |
| Whole message history / history-sync blobs held in memory | RSS climbs after each pair; GC pressure on LOH | Stream sync chunks to store; cap in-memory buffers | Single large account's initial sync |
| All accounts reconnecting simultaneously after a process/network blip | Thundering herd → rate-limit signals / detection | Jittered reconnect scheduling across accounts (± spread) | Any multi-account deployment restart |
| Sequential fan-out encryption per recipient device | Broadcast latency linear in device count | Parallelize per-device encryption with bounded concurrency; cache sessions | Group/recipient with many devices |
| Event handlers run on the receive loop | Inbound processing stalls heartbeats → disconnects | Decouple dispatch: queue + background workers; never block the read loop | Modest throughput already |
| Encrypting large media in one buffer | OOM on 100MB+ documents | Streaming encrypt (block-wise AES-CBC) + resumable upload | Files > available buffer (often just 50–100MB) |
| Unbounded skipped-key / retry / IQ maps | Memory leak, DoS via crafted frames | Hard caps per spec (skipped keys), TTL on pending IQs, bounded retry cache | Adverse traffic or long-lived busy sessions |

## Security Mistakes

| Mistake | Risk | Prevention |
|---------|------|------------|
| Incomplete Noise cert-chain validation (see Pitfall 5) | MITM poisons cached server static → silent future interception | Full signature + validity-window verification; fail closed |
| Auto-trusting identity-key changes without surfacing them | Silent key substitution for a contact; breaks the security model users think they have | Emit identity-change events; default to explicit trust-on-first-use logging at minimum (whatsmeow exposes `AutoTrustIdentity` — decide consciously, document default) |
| Session/media keys or QR payloads written to logs (common at DEBUG level) | Credential leak via host's log pipeline; session theft = account takeover | Structured logging with key material redaction; document that DEBUG is unsafe in production |
| Pluggable store contract allows plaintext keys "for simplicity" with no guidance | Host stores Signal identity keys in an unencrypted column unknowingly | Document key-material sensitivity in the store interface; offer optional at-rest encryption helpers; sample store shows encryption |
| Message content / JIDs in telemetry or exceptions | Privacy violation (E2EE product logging plaintext) | Content-free metrics by default; opt-in content logging |
| No cancellation/timeout discipline around crypto ops | Hanging handshake holds keys in memory indefinitely; thread-pool starvation | Timeouts on every network+crypto stage; zero sensitive buffers after use (span-based, best-effort) |

## "Looks Done But Isn't" Checklist

- [ ] **QR pairing:** Works on first run — verify **reconnect-without-re-pair after process restart** with the persisted store, and after a forced 515/428 disconnect.
- [ ] **E2EE send:** Works in order — verify **after reconnect mid-conversation** and with **out-of-order/lost** frames (chaos test), plus retry-receipt round trip.
- [ ] **Rate limiting:** Exists — verify **every** send path routes through it (including host-called sends) and counters **survive restart**.
- [ ] **Warm-up:** Implemented — verify it keys off the *account's* first-link time persisted in the store, not process start; verify cold-outreach budget is separate from total budget.
- [ ] **Multi-account:** Two accounts work — verify with **concurrent** operations + isolation test suite; verify full-JID keying (two slots, same number).
- [ ] **Reconnect:** Comes back — verify **backoff** (no fixed-interval loop), **401 terminal** handling, error surfacing after 2nd consecutive failure, and status callbacks (no infinite "Connecting…").
- [ ] **App state:** Syncs on happy path — verify a **mismatching-LTHash patch does not terminate the session**, and recovery re-sync works.
- [ ] **Media:** Small image sends — verify **all four types**, **sidecar for video/audio**, large-file upload resume, and expired-token refresh.
- [ ] **Session durability:** Store saves state — verify store writes are **cancellation-insensitive** and ordered before send (Pitfall 2 chaos test: kill network mid-send).
- [ ] **Version handling:** Connects today — verify **405 triggers re-resolution** not blind retry; fixtures don't gate live interop.
- [ ] **Acks:** Messages flow — verify notifications/receipts are acked (missed acks → server stops delivering; "works for an hour then goes quiet").
- [ ] **License hygiene:** Repo has license — verify **NOTICE/provenance doc + PR checklist** exist before protocol code lands.

## Recovery Strategies

| Pitfall | Recovery Cost | Recovery Steps |
|---------|---------------|----------------|
| Version drift (405) | LOW | Ship version re-resolution; clear cache; redeploy — no user action |
| Ratchet desync (BadMAC/401) | HIGH | Emit `LoggedOut(NeedsRePair)`; operator re-scans QR for affected accounts; add chaos test before next release |
| LTHash fatal desync | MEDIUM | Redeploy with skip-broken-patches; trigger full app-state re-sync; sessions that already got unlinked need re-pair |
| Ban signal / restriction | HIGH (often irreversible) | Stop all sends for account; raise event; operator appeals on phone; restart warm-up from low rung on replacement number; root-cause the message pattern first |
| Media failures (recipient can't open) | MEDIUM | Fix crypto/sidecar; re-send affected messages (original upload is fine — resend with correct fields where possible) |
| Isolation leak | HIGH | Treat cross-tenant data exposure as incident: identify affected accounts, purge contaminated store rows, re-key, add isolation tests |
| License contamination discovered pre-release | MEDIUM (LOW if caught in review) | Re-implement flagged files with recorded provenance; keep review log — cost grows sharply after public release, so catch pre-ship |

## Pitfall-to-Phase Mapping

Phases below are *suggested roadmap topics*; adjust to final roadmap naming.

| Pitfall | Prevention Phase | Verification |
|---------|------------------|--------------|
| 1 Version drift | Pairing/transport phase + release/CI phase | Live interop job fails on 405; version re-resolution unit test; upstream-watch checklist in release docs |
| 2 Ratchet persist ordering | Signal/E2EE session phase | Chaos test: cancel network mid-send → next send still decrypts; store contract doc reviewed |
| 3 App-state fatal errors | App-state sync phase | Inject bad LTHash patch → session stays up, `SyncError` event raised, recovery re-sync succeeds |
| 4 Reconnect taxonomy | Connection lifecycle phase | Test matrix per disconnect code (401/408/428/440/515/411); backoff timing assertion; no fixed-interval loop |
| 5 Crypto fidelity | Noise/transport phase | Byte-fixture tests; corrupt-cert test must fail; live handshake in gated CI |
| 6 Ratchet edge cases + retries | Signal phase (with send/receive) | Randomized reorder/drop property test; retry-receipt round-trip test |
| 7 Ban-safety | Ban-safety phase (gate before live traffic); chokepoint designed into messaging phase | Send-path audit (no ungated egress); warm-up persistence test; breaker simulation; defaults review |
| 8 Multi-account isolation | Architecture/store-interface phases + isolation test suite in CI | Two-account concurrency tests; static-state lint/review; store conformance suite (namespace + cascade) |
| 9 License hygiene | Repo bootstrap policy + package/ship review | NOTICE/provenance doc exists; PR checklist applied; pre-release provenance audit |
| 10 Media pipeline | Media phase | Golden hash/MAC fixtures per type; sidecar test; token-expiry refresh test; resume test |
| 11 PN/LID identity | Messaging/addressing phase | Fixtures with both JID forms; group history-sync test; ambiguous-mapping must not guess |

**Ordering rationale:** transport→crypto→sessions→messaging→app-state→media→multi-account→ban-safety→ship. Pits 1/5/6 are structural (cheapest when built in); Pit 7 gates anything that touches live accounts; Pit 8 is a constraint on early interfaces (store, object model), not a late phase; Pit 9 policy file lands before first protocol commit.

## Sources

- **whatsmeow issue tracker** (MEDIUM, cross-checked): #1066 session loss on context cancellation → 401; #1203/#651 app-state LTHash mismatch → unlink; #1027/#1004 PN→LID migration; #132/#414 usync-on-disconnect; #1191 pairing rejected; #705 view-once breakage; #1038 orphan store rows; #1095 fresh-link first outbound; retry/receipt design (retry.go, receipt.go).
- **Baileys issue tracker & docs** (MEDIUM): #2203 401 device_removed; #107/#141 515 restartRequired; #2248 post-pair 401; #1914 connection failure; disconnect-reason taxonomy & backoff guidance (Baileys Connection Lifecycle docs); `noise-handler.ts` reference implementation shape.
- **rowboat #1067 commit (Baileys version pin → 405, runtime resolution fix)** (MEDIUM): protocol-drift failure mode and remediation pattern.
- **go-whatsapp-web-multidevice releases** (MEDIUM): #762 full-AD-JID slot mapping, #763 history-sync participant fallback, proxy/SOCKS5 WebSocket note, logout-vs-purge semantics.
- **Signal Double Ratchet specification** (HIGH — primary spec): out-of-order keys, MKSKIPPED bounds; **USENIX WOOT'25 "Prekey Pogo"** (HIGH — peer-reviewed): prekey batch 812 / refill <10, signed-prekey rotation ≈ monthly, no rotation in open-source clients, 503 clogging.
- **Meta Engineering blog "How WhatsApp enables multi-device"** (HIGH — primary): client-fanout, per-device identity keys, history-sync blob model, app-state E2EE sync.
- **MPL-2.0 license text + Mozilla MPL 2.0 FAQ** (HIGH — primary): file-level copyleft, Larger Work, new-files-are-not-Modifications, private-use clause.
- **OpenWA safe-sending docs, WAPI anti-ban guide, Wapito anti-ban guide, whatsmeow-antiban, wapisimo warm-up guides** (MEDIUM — convergent operator practice): warm-up ladders, cold-outreach caps, jitter, typing simulation, circuit breakers, honest "no ban-proof" stance.
- **whatsapp-media-crypto (README/implementation), whatsapp-rust upload docs** (MEDIUM): media HKDF/AES-CBC/HMAC details, sidecar chunking, token refresh/resume.
- **sigalor/whatsapp-web-reveng** (LOW — dated but structural): framing, origin header, version churn notes.
- **Double Ratchet security analyses (eprint 2018/1037, 2022/355, 2024/1625)** (MEDIUM): out-of-order/loss subtleties, implementation hard caps.

---
*Pitfalls research for: WhatsApp Web multi-device client SDK for .NET (sdk-wa)*
*Researched: 2026-10-07*
