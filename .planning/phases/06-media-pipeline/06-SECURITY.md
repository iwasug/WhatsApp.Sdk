---
phase: "06"
slug: media-pipeline
status: verified
# threats_open = count of OPEN threats at or above workflow.security_block_on severity (the blocking gate)
threats_open: 0
asvs_level: 1
created: "2026-10-15"
---

# Phase 06 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| Host Stream → MediaCrypto | Untrusted plaintext bytes and metadata (mimetype, dimensions, caption) cross into the encrypt-then-MAC envelope | Plaintext bytes, metadata |
| mmg HTTP → MediaDownloader | Untrusted network bytes cross into verify-then-decrypt; nothing trusted before all three checks pass | Ciphertext + MAC + hashes |
| Inbound Message proto → dispatcher branch | Adversary-influenced media fields (hashes, directPath, lengths) cross into field validation | Proto fields |
| Public media send → safety gate | Host-driven send volume and byte sizes cross into mandatory admission; upload sits behind admission | Send requests, sizes |
| media-conn IQ → host selection | Server-supplied hosts and auth tokens cross into upload URL construction | Hosts, auth tokens |
| Host metadata → node attrs | Host mimetype/filename/labels cross into wire attrs; peers render from them | MIMetypes, filenames |
| CDN bytes → verify pipeline | Untrusted download bytes and expiry statuses cross into verify and retry trigger | Download bytes, statuses |
| Peer rmr notification → re-download | Adversary-influenced fresh paths and result codes cross into host/peer selection | Paths, result codes |
| Retry tracker → wire | Self-inflicted request volume crosses into protocol egress; cap bounds it | Retry count |

---

## Threat Register

| Threat ID | Category | Component | Severity | Disposition | Mitigation | Status |
|-----------|----------|-----------|----------|-------------|------------|--------|
| T-06-01 | Tampering | MediaCrypto + MediaDownloader (tampered blob delivered as valid) | high | mitigate | Verify-then-decrypt in fixed order: SHA-256(blob)==fileEncSha256, then FixedTimeEquals 10-byte MAC, then SHA-256(plaintext)==fileSha256, with zero plaintext delivered on any failure; Baileys-style unverified streaming never used; frozen tamper vectors gate builds (MediaCrypto.cs:123–238) | closed |
| T-06-02 | Information Disclosure | Media paths (mediaKey/cipherKey/macKey, hashes, URLs in logs, events, exceptions) | medium | mitigate | CryptographicOperations.ZeroMemory on every key temporary; no ILogger usage on media paths; exceptions/events carry rule-only text plus metadata; no URL/key/hash bytes in event payloads (MediaCrypto.cs:39–121, MediaEvents.cs) | closed |
| T-06-03 | Denial of Service | Media-retry requests (retry storms re-uploading/re-fetching without bound) | medium | mitigate | Process-local ConcurrentDictionary tracker capped at 3 requests per message id with hourly eviction past 4096 entries; recovery is protocol egress with a typed expiry-exhausted terminal (MediaRetryTracker.cs) | closed |
| T-06-04 | Elevation of Privilege | Messenger media sends + MediaUploader (upload outside gate or ungated send loop) | high | mitigate | Upload executes only inside ISafetyGate.ExecuteSendAsync delegate after admission; uploader performs no gating; all six public media sends reference the gate entry point (Messenger.cs:230–332) | closed |
| T-06-05 | Spoofing | Media node attrs and metadata (missing type media/mediatype, off-label mimetypes, executable filenames) | medium | mitigate | Mimetype allow-list enforced pre-upload; voice notes fixed to Opus ptt token with ptt forced true; receive-side filename sanitization rejects traversal/separators and executable labels rejected (MediaKind.cs, MediaMetadata.cs:71–184) | closed |
| T-06-06 | Denial of Service | Oversize media (RAM blowup, pipe stalls from slow hosts) | medium | mitigate | Byte caps enforced pre-upload and pre-decrypt (SafetyOptions MaxImageBytes/MaxDocumentBytes/MaxAudioBytes/MaxThumbnailBytes); Content-Length checked against fileLength; temp-file-backed streaming (SafetyOptions.cs, MediaMetadata.cs, MediaDownloader.cs) | closed |

*Status: open · closed · open — below high threshold (non-blocking)*
*Severity: critical > high > medium > low — only open threats at or above workflow.security_block_on (high) count toward threats_open*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

No accepted risks.

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-10-15 | 6 | 6 | 0 | gsd-security-auditor |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-10-15