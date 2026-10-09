---
phase: 02
review: 02-REVIEW.md
titles: json
findings:
  - id: CR-01
    severity: critical
    disposition: open
    title: "Decoder depth bound bypassed by recursive JID string forms — remote StackOverflowException"
  - id: CR-02
    severity: critical
    disposition: open
    title: "FrameReader violation escapes WatchAsync — hostile frame becomes unbounded 408-retry loop and skips channel teardown"
  - id: WR-01
    severity: warning
    disposition: open
    title: "Handshake message B assumes one complete frame per single ReceiveAsync — benign re-segmentation permanently halts the connection"
  - id: IN-01
    severity: info
    disposition: open
    title: "WriteMessageA doc carries a dangling pre-02-07 fragment and a stale `<returns>`"
  - id: IN-02
    severity: info
    disposition: open
    title: "ReadMessageA doc still describes the pre-02-07 `WA-header ||` shape and checks it no longer performs"
  - id: IN-03
    severity: info
    disposition: open
    title: "WriteMessageB doc has a dangling sentence fragment"
  - id: CR-03
    severity: critical
    disposition: open
    title: "BinaryNodeCodec decoder has no recursion-depth limit (StackOverflow DoS)"
  - id: WR-02
    severity: warning
    disposition: open
    title: "Abandoned WatchAsync on abort/timeout paths; ObjectDisposedException escapes unobserved"
  - id: WR-03
    severity: warning
    disposition: open
    title: "BinaryNodeCodec.Decode silently ignores trailing bytes"
  - id: WR-04
    severity: warning
    disposition: open
    title: "LIST_16 pre-allocates up to 65535 entries from untrusted bytes"
  - id: WR-05
    severity: warning
    disposition: open
    title: "NoiseTransport dispose/use race throws raw NullReferenceException"
  - id: WR-06
    severity: warning
    disposition: open
    title: "ObjectDisposedException leaks where SdkConnectionException is promised"
  - id: WR-07
    severity: warning
    disposition: open
    title: "WaConnection._state plain field; stale polling reads"
  - id: WR-08
    severity: warning
    disposition: open
    title: "MapOutboundCloseCode unchecked arithmetic + blind enum cast"
  - id: WR-09
    severity: warning
    disposition: open
    title: "Message-A static_key modeled but never validated"
  - id: WR-10
    severity: warning
    disposition: open
    title: "NOTICE.md lacks handshake-envelope provenance row promised by 02-03"
  - id: WR-11
    severity: warning
    disposition: open
    title: "Decide burns Random.Shared draw on every drop though loop discards delay"
  - id: IN-04
    severity: info
    disposition: open
    title: "RetryBaseDelayMs = 0 accepted; zero-delay infinite reconnect loop"
  - id: IN-05
    severity: info
    disposition: open
    title: "JidDecode agent-number parsing diverges from Baileys on trailing garbage"
  - id: IN-06
    severity: info
    disposition: open
    title: "DisconnectReason remarks promise 503 a longer base that does not exist"
  - id: IN-07
    severity: info
    disposition: open
    title: "MapInboundCloseCode strips genuine 4000-4999 private-use codes"
  - id: IN-08
    severity: info
    disposition: open
    title: "_firstOpen TCS left uncompleted on host-cancel path"
  - id: IN-09
    severity: info
    disposition: open
    title: "Single-flag-byte frames decode to empty payloads instead of failing closed"
  - id: IN-10
    severity: info
    disposition: open
    title: "Protocol-halt emits Closing(null), indistinguishable from host-initiated stop"
open: 24
total: 24
recorded: 2026-10-09T04:41:19.697Z
---

# Phase 02: Code Review Disposition

| Finding | Severity | Disposition | Source |
|---------|----------|-------------|--------|
| CR-01 | critical | open | - |
| CR-02 | critical | open | - |
| WR-01 | warning | open | - |
| IN-01 | info | open | - |
| IN-02 | info | open | - |
| IN-03 | info | open | - |
| CR-03 | critical | open | 02-REVIEW.md (not in the current review) |
| WR-02 | warning | open | 02-REVIEW.md (not in the current review) |
| WR-03 | warning | open | 02-REVIEW.md (not in the current review) |
| WR-04 | warning | open | 02-REVIEW.md (not in the current review) |
| WR-05 | warning | open | 02-REVIEW.md (not in the current review) |
| WR-06 | warning | open | 02-REVIEW.md (not in the current review) |
| WR-07 | warning | open | 02-REVIEW.md (not in the current review) |
| WR-08 | warning | open | 02-REVIEW.md (not in the current review) |
| WR-09 | warning | open | 02-REVIEW.md (not in the current review) |
| WR-10 | warning | open | 02-REVIEW.md (not in the current review) |
| WR-11 | warning | open | 02-REVIEW.md (not in the current review) |
| IN-04 | info | open | 02-REVIEW.md (not in the current review) |
| IN-05 | info | open | 02-REVIEW.md (not in the current review) |
| IN-06 | info | open | 02-REVIEW.md (not in the current review) |
| IN-07 | info | open | 02-REVIEW.md (not in the current review) |
| IN-08 | info | open | 02-REVIEW.md (not in the current review) |
| IN-09 | info | open | 02-REVIEW.md (not in the current review) |
| IN-10 | info | open | 02-REVIEW.md (not in the current review) |

Dispositions: `open` (recorded, not yet triaged), `fixed`, `skipped`, `deferred`.
Set `deferred` by hand and put the reason in the Source cell; both are preserved. A `|` in the reason is kept as prose and escaped on the next run.
Re-running the gate keeps every row it can. A row the current review no longer reports is kept and its Source cell flagged, so a finding does not leave this record silently. ONE exception: when a finding id is REUSED by a different finding, the earlier decision cannot keep a row — the id is taken — and it is dropped. A RECORDED decision (anything but `open`) is named on the console when that happens; a row still at `open` is replaced silently, because `open` records no decision to lose.
