---
phase: 06
review: 06-REVIEW.md
titles: json
findings:
  - id: CR-01
    severity: critical
    disposition: fixed
    title: "Size check compares response Content-Length to the plaintext fileLength — every legitimate download is rejected (carried: prior WR-01, upgraded)"
  - id: CR-02
    severity: critical
    disposition: fixed
    title: "`<rmr>` routing is gated behind receipt-type parsing — recovery silently dead for `server-error`-typed (or any unknown-typed) notifications"
  - id: WR-01
    severity: warning
    disposition: fixed
    title: "Pending recovery context is consumed before any payload validation — one malformed or failed notification permanently destroys recovery"
  - id: WR-02
    severity: warning
    disposition: fixed
    title: "D-66b terminals are unimplemented — `<error code>` child is never parsed and `Result != 1` drops silently"
  - id: WR-03
    severity: warning
    disposition: fixed
    title: "The planned public `MediaDownloadFailed` signal does not exist — event is internal, never raised, undeclared, and cap exhaustion is ignored"
  - id: WR-04
    severity: warning
    disposition: fixed
    title: "Received media is never written to the recent-message cache — 06-04 ordering and the D-69 correlator design are unimplemented"
  - id: WR-05
    severity: warning
    disposition: fixed
    title: "`_pendingMediaRecoveries` is unbounded — no cap, no eviction, no purge"
  - id: WR-06
    severity: warning
    disposition: fixed
    title: "`SendDocumentAsync` accepts a thumbnail and silently discards it"
  - id: WR-07
    severity: warning
    disposition: fixed
    title: "`MediaReceivedEvent` and the EventHandler twin share one `Stream` instance — double-consumption and double-dispose hazard"
  - id: WR-08
    severity: warning
    disposition: skipped
    title: "Media download and host event run inside the per-address `SignalSessionGate`"
  - id: WR-09
    severity: warning
    disposition: fixed
    title: "The planned retry/recovery facts are not implemented — the routing assumption (CR-02) and the size check (CR-01) are untested"
  - id: WR-10
    severity: warning
    disposition: fixed
    title: "`BuildDownloadUrl` accepts an unvalidated host and leans on the peer-supplied direct path (carried: prior WR-02)"
  - id: WR-11
    severity: warning
    disposition: fixed
    title: "Image messages always declare `image/jpeg`, even for PNG uploads (carried: prior WR-03)"
  - id: IN-01
    severity: info
    disposition: fixed
    title: "Dead null-guard in `MediaDownloader` and a raw `HttpRequestException` escaping the seam"
  - id: IN-02
    severity: info
    disposition: fixed
    title: "`MediaRetryTracker` eviction snapshots then mutates — a refreshed key can be evicted (carried: prior IN-02)"
open: 0
total: 15
recorded: 2026-10-16T01:46:21.961Z
---


# Phase 06: Code Review Disposition

| Finding | Severity | Disposition | Source |
|---------|----------|-------------|--------|
| CR-01 | critical | fixed | 06-REVIEW-FIX.md |
| CR-02 | critical | fixed | 06-REVIEW-FIX.md |
| WR-01 | warning | fixed | 06-REVIEW-FIX.md |
| WR-02 | warning | fixed | 06-REVIEW-FIX.md |
| WR-03 | warning | fixed | 06-REVIEW-FIX.md |
| WR-04 | warning | fixed | 06-REVIEW-FIX.md |
| WR-05 | warning | fixed | 06-REVIEW-FIX.md |
| WR-06 | warning | fixed | 06-REVIEW-FIX.md |
| WR-07 | warning | fixed | 06-REVIEW-FIX.md |
| WR-08 | warning | skipped | 06-REVIEW-FIX.md |
| WR-09 | warning | fixed | 06-REVIEW-FIX.md |
| WR-10 | warning | fixed | 06-REVIEW-FIX.md |
| WR-11 | warning | fixed | 06-REVIEW-FIX.md |
| IN-01 | info | fixed | 06-REVIEW-FIX.md |
| IN-02 | info | fixed | 06-REVIEW-FIX.md |

Dispositions: `open` (recorded, not yet triaged), `fixed`, `skipped`, `deferred`.
Set `deferred` by hand and put the reason in the Source cell; both are preserved. A `|` in the reason is kept as prose and escaped on the next run.
Re-running the gate keeps every row it can. A row the current review no longer reports is kept and its Source cell flagged, so a finding does not leave this record silently. ONE exception: when a finding id is REUSED by a different finding, the earlier decision cannot keep a row — the id is taken — and it is dropped. A RECORDED decision (anything but `open`) is named on the console when that happens; a row still at `open` is replaced silently, because `open` records no decision to lose.
