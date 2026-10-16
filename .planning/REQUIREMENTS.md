# Requirements: WhatsApp SDK for .NET (sdk-wa)

**Defined:** 2026-10-07
**Core Value:** Reliably send and receive messages through a linked WhatsApp account — across many accounts at once, without getting those accounts banned.

## v1 Requirements

Requirements for initial release. Each maps to roadmap phases.

### Connection & Session

- [ ] **CONN-01**: Host can link an account via QR code and observe typed lifecycle events (`code`/`success`/`timeout`/`logged_out`/`client_outdated`)
- [ ] **CONN-02**: Host can link an account via pairing code without scanning a QR (headless servers)
- [ ] **CONN-03**: Session state persists through the pluggable store; a restarted process resumes without re-pairing
- [x] **CONN-04**: Connection auto-reconnects with backoff and discriminates disconnect reasons (401 terminal, 428/408 retry, 515 restart, 440 replaced, 411 outdated)
- [x] **CONN-05**: Host can observe per-account connection state events (`connecting`/`open`/`closing`/`logged_out`)

### Store

- [x] **STR-01**: Host can plug its own persistence via granular store interfaces (identity, signal sessions, prekeys, recent-message cache, safety state) namespaced per account
- [x] **STR-02**: SDK ships a working in-memory reference store implementation

### Messaging

- [x] **MSG-01**: Host can send and receive E2EE text in 1:1 chats
- [x] **MSG-02**: Host can send and receive E2EE text in groups the account belongs to
- [x] **MSG-03**: Host gets stable message IDs for send→ack→receipt correlation, with documented idempotency (no silent auto-retry)
- [x] **MSG-04**: Host can send and receive delivery/read receipts and mark messages as read (per-message keys)
- [x] **MSG-05**: Host can reply quoting a specific message (per-account recent-message cache)
- [x] **MSG-06**: Inbound decryption failures recover via retry receipts without losing messages
- [x] **MSG-07**: Host receives typed events (message, receipt, connection, logout, safety) with `CancellationToken` end-to-end

### Media

- [ ] **MED-01**: Host can send image, document, and voice note
- [x] **MED-02**: Host can receive/download media including download-expiry handling and media-retry flow

### Ban-Safety

- [x] **SAF-01**: Every send passes through a mandatory safety gate — no bypass path exists in the public API
- [x] **SAF-02**: Per-account token-bucket rate limits (minute/hour/day) with randomized inter-send jitter
- [x] **SAF-03**: Warm-up ramp for new accounts with account-age state persisted in the store
- [x] **SAF-04**: Separate cold-outreach budget for never-messaged recipients, hard-enforced
- [x] **SAF-05**: Circuit breaker + exponential backoff on throttle signals, emitting typed safety events
- [x] **SAF-06**: Human-like pacing hooks (typing/recording presence before send, active-hours window)
- [x] **SAF-07**: Safe-by-default configuration with explicit `AllowUnsafeThroughput` opt-in and honest risk-ledger docs

### Multi-Account

- [x] **MUL-01**: Host can manage many concurrent accounts via `SessionManager` (add/remove/status/restart, hot add/remove, per-account health)
- [x] **MUL-02**: Accounts are hard-isolated (per-account store namespace, outbound queue, limiter, no shared mutable state) with an isolation test suite running in CI
- [x] **MUL-03**: SDK surface is idiomatic .NET (DI, `IOptions<T>`, `IHostedService`, async streams, `CancellationToken`, `ILogger` per-account scopes)

### Broadcast

- [x] **BCT-01**: Host can trigger `SendBroadcastAsync` that fans out as individual 1:1 sends through the safety gate + warm-up budget and returns a per-recipient outcome report (no independent send loop)

### Extras

- [x] **EXT-01**: Durable per-account outbox: enqueue → rate-limit → send → backoff → dead-letter, queue storage via the store abstraction
- [ ] **EXT-02**: Presence API: send/observe `available`/`unavailable`/`composing`/`recording`/`paused` and peer presence
- [ ] **EXT-03**: Host can send, receive, and remove reactions
- [ ] **EXT-04**: Host can edit own messages and delete messages for everyone

### Delivery

- [ ] **OPS-01**: Ship as a NuGet package with docs (README lead message, risk ledger, store-implementation guide, webhook recipe)
- [ ] **OPS-02**: CI with unit/golden-fixture tests, isolation suite, and a gated live-interop job that detects protocol drift (405)

## v2 Requirements

Deferred to future release. Tracked but not in current roadmap.

### Telemetry & Sync

- **TEL-01**: Quality/reply-rate telemetry events (trigger: receipts stable + real volume)
- **SYNC-01**: History sync / offline backfill (HIGH cost)

### Groups & Media

- **GRPM-01**: Group read-side + basic management (participants, invite link, group info)
- **MEDV-01**: Media: video, stickers, link previews

### Future

- **POLL-01**: Polls (build/vote/decrypt)
- **NLTR-01**: Newsletters/channels read + post
- **CMTY-01**: Communities
- **PRIV-01**: Disappearing messages, blocklist sync, privacy settings, profile management
- **STAT-01**: Status/Stories writes (upstream eksperimental)

## Out of Scope

Explicitly excluded. Documented to prevent scope creep.

| Feature | Reason |
|---------|--------|
| Broadcast list sends (`@broadcast`) | Protokol web tidak mendukung — pakai BCT-01 fan-out 1:1 |
| Interactive buttons/lists/catalogs | Official-API-only + sinyal account restriction |
| Voice/video calls (implementation) | Auto-reject + event saja; media stack real-time tak bernilai untuk CS |
| Account registration / OTP bypass | Melanggar ToS, berpotensi melanggar hukum |
| Klaim "ban-proof"/undetectable | Tidak bisa dijanjikan; mitigasi + risk ledger yang jujur |
| HTTP webhook server dalam library | Pekerjaan host application; sediakan resep docs |
| `SendBulkAsync`-shaped API | Bentuk API mengajarkan pola blast yang memicu ban |
| Status/Stories writes | Eksperimental upstream, nilai rendah untuk CS |
| Always-`available` presence default | Footgun notifikasi + behavioral flag |

## Traceability

Which phases cover which requirements. Updated during roadmap creation.

| Requirement | Phase | Status |
|-------------|-------|--------|
| CONN-01 | Phase 3 | Pending |
| CONN-02 | Phase 3 | Pending |
| CONN-03 | Phase 3 | Pending |
| CONN-04 | Phase 2 | Complete |
| CONN-05 | Phase 2 | Complete |
| STR-01 | Phase 1 | Complete |
| STR-02 | Phase 1 | Complete |
| MSG-01 | Phase 4 | Complete |
| MSG-02 | Phase 4 | Complete |
| MSG-03 | Phase 4 | Complete |
| MSG-04 | Phase 4 | Complete |
| MSG-05 | Phase 4 | Complete |
| MSG-06 | Phase 4 | Complete |
| MSG-07 | Phase 4 | Complete |
| MED-01 | Phase 6 | Pending |
| MED-02 | Phase 6 | Complete |
| SAF-01 | Phase 5 | Complete |
| SAF-02 | Phase 5 | Complete |
| SAF-03 | Phase 5 | Complete |
| SAF-04 | Phase 5 | Complete |
| SAF-05 | Phase 5 | Complete |
| SAF-06 | Phase 5 | Complete |
| SAF-07 | Phase 5 | Complete |
| MUL-01 | Phase 7 | Complete |
| MUL-02 | Phase 7 | Complete |
| MUL-03 | Phase 7 | Complete |
| BCT-01 | Phase 8 | Complete |
| EXT-01 | Phase 8 | Complete |
| EXT-02 | Phase 8 | Pending |
| EXT-03 | Phase 8 | Pending |
| EXT-04 | Phase 8 | Pending |
| OPS-01 | Phase 9 | Pending |
| OPS-02 | Phase 9 | Pending |

**Coverage:**
- v1 requirements: 33 total
- Mapped to phases: 33
- Unmapped: 0 ✓

---
*Requirements defined: 2026-10-07*
*Last updated: 2026-10-07 after roadmap creation (33/33 mapped)*
