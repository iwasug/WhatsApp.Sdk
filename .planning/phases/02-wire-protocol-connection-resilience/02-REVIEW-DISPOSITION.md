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
open: 6
total: 6
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

Dispositions: `open` (recorded, not yet triaged), `fixed`, `skipped`, `deferred`.
Set `deferred` by hand and put the reason in the Source cell; both are preserved. A `|` in the reason is kept as prose and escaped on the next run.
Re-running the gate keeps every row it can. A row the current review no longer reports is kept and its Source cell flagged, so a finding does not leave this record silently. ONE exception: when a finding id is REUSED by a different finding, the earlier decision cannot keep a row — the id is taken — and it is dropped. A RECORDED decision (anything but `open`) is named on the console when that happens; a row still at `open` is replaced silently, because `open` records no decision to lose.
