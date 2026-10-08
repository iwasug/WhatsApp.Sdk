---
phase: 02-wire-protocol-connection-resilience
review: 02-REVIEW.md
review_status: issues_found
created: 2026-10-09
open: 24
fixed: 0
skipped: 0
deferred: 0
---

# Phase 02: Code Review Disposition

**Review:** 02-REVIEW.md (24 findings — 3 critical, 11 warning, 10 info)
**Status:** All rows `open` — no fix pass has run. Triage before `/gsd-secure-phase 02` and before any gap-closure planning.

## Legend

- `open` — untriaged, fix promised, or fix outstanding
- `fixed` — resolved, reconciled against REVIEW-FIX.md
- `skipped` — intentionally not fixed, reason recorded
- `deferred` — postponed, reason recorded in Source cell

| ID | Title | Severity | Disposition | Source |
|----|-------|----------|-------------|--------|
| CR-01 | handshake.proto outer field numbers invented (1/2/3 vs server 2/3/4) | critical | open | 02-REVIEW.md |
| CR-02 | Handshake/pump wire framing diverges from server; FrameReader bypassed | critical | open | 02-REVIEW.md |
| CR-03 | BinaryNodeCodec decoder has no recursion-depth limit (StackOverflow DoS) | critical | open | 02-REVIEW.md |
| WR-01 | ReadMessageB destroys serverHello cert chain; server-identity verification impossible | warning | open | 02-REVIEW.md |
| WR-02 | Abandoned WatchAsync on abort/timeout paths; ObjectDisposedException escapes unobserved | warning | open | 02-REVIEW.md |
| WR-03 | BinaryNodeCodec.Decode silently ignores trailing bytes | warning | open | 02-REVIEW.md |
| WR-04 | LIST_16 pre-allocates up to 65535 entries from untrusted bytes | warning | open | 02-REVIEW.md |
| WR-05 | NoiseTransport dispose/use race throws raw NullReferenceException | warning | open | 02-REVIEW.md |
| WR-06 | ObjectDisposedException leaks where SdkConnectionException is promised | warning | open | 02-REVIEW.md |
| WR-07 | WaConnection._state plain field; stale polling reads | warning | open | 02-REVIEW.md |
| WR-08 | MapOutboundCloseCode unchecked arithmetic + blind enum cast | warning | open | 02-REVIEW.md |
| WR-09 | Message-A static_key modeled but never validated | warning | open | 02-REVIEW.md |
| WR-10 | NOTICE.md lacks handshake-envelope provenance row promised by 02-03 | warning | open | 02-REVIEW.md |
| WR-11 | Decide burns Random.Shared draw on every drop though loop discards delay | warning | open | 02-REVIEW.md |
| IN-01 | HkdfTwoBlocks temporaries never zeroized | info | open | 02-REVIEW.md |
| IN-02 | BinaryNode.ToString escapes attrs/text but not Tag | info | open | 02-REVIEW.md |
| IN-03 | BinaryNode.Data getter exposes mutable backing array | info | open | 02-REVIEW.md |
| IN-04 | RetryBaseDelayMs = 0 accepted; zero-delay infinite reconnect loop | info | open | 02-REVIEW.md |
| IN-05 | JidDecode agent-number parsing diverges from Baileys on trailing garbage | info | open | 02-REVIEW.md |
| IN-06 | DisconnectReason remarks promise 503 a longer base that does not exist | info | open | 02-REVIEW.md |
| IN-07 | MapInboundCloseCode strips genuine 4000-4999 private-use codes | info | open | 02-REVIEW.md |
| IN-08 | _firstOpen TCS left uncompleted on host-cancel path | info | open | 02-REVIEW.md |
| IN-09 | Single-flag-byte frames decode to empty payloads instead of failing closed | info | open | 02-REVIEW.md |
| IN-10 | Protocol-halt emits Closing(null), indistinguishable from host-initiated stop | info | open | 02-REVIEW.md |

**Counts:** open: 24 of 24. Shortfall: 0 unparsed (frontmatter total 24 = 24 rows).
