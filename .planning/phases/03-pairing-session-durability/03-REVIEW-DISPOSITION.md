---
phase: 03
review: 03-REVIEW.md
titles: json
findings:
  - id: CR-01
    severity: critical
    disposition: open
    title: "Companion handshake routing is dead code — the hello signal is never completed and the documented bundle-key finish authentication does not exist"
  - id: CR-02
    severity: critical
    disposition: open
    title: "Plan-03-06 durability tests are absent from the repository; the phase gate reports a failure for a test that does not exist"
  - id: CR-03
    severity: critical
    disposition: open
    title: "The sample host is uncommitted, absent from the solution, fails to restore, and cannot compile against the public API"
  - id: CR-04
    severity: critical
    disposition: open
    title: "Sample FileCredentialStore silently drops three private keys and the platform label — a must-not-lose data-loss bug"
  - id: WR-01
    severity: warning
    disposition: open
    title: "Pair-success numeric fields are persisted without range validation — a negative device index bricks every subsequent login"
  - id: WR-02
    severity: warning
    disposition: open
    title: "Watchdog timeout can land between the credential write and the reply — a paired row with no Success event and no server ack"
  - id: WR-03
    severity: warning
    disposition: open
    title: "The public code-pairing flow has zero test coverage — plan 03-05 Task 2 acceptance unmet"
  - id: WR-04
    severity: warning
    disposition: open
    title: "Sample runtime flows are nonfunctional even after the compile errors are fixed"
  - id: WR-05
    severity: warning
    disposition: open
    title: "Sample FileCredentialStore violates its own D-19 failure≠missing contract"
  - id: IN-01
    severity: info
    disposition: open
    title: "`PairingCodeRequestException` public constructor leaves `Reason` defaulting to `NonDigits`"
  - id: IN-02
    severity: info
    disposition: fixed
    title: "adv.proto comment misdocuments the HMAC input"
  - id: IN-03
    severity: info
    disposition: open
    title: "NOTICE.md provenance row cites a file that does not exist"
  - id: IN-04
    severity: info
    disposition: open
    title: "Untracked execution artifacts litter the working tree, including a duplicate phase directory"
open: 12
total: 13
recorded: 2026-10-10T13:16:06.248Z
---

# Phase 03: Code Review Disposition

| Finding | Severity | Disposition | Source |
|---------|----------|-------------|--------|
| CR-01 | critical | open | - |
| CR-02 | critical | open | - |
| CR-03 | critical | open | - |
| CR-04 | critical | open | - |
| WR-01 | warning | open | - |
| WR-02 | warning | open | - |
| WR-03 | warning | open | - |
| WR-04 | warning | open | - |
| WR-05 | warning | open | - |
| IN-01 | info | open | - |
| IN-02 | info | fixed | 03-REVIEW-FIX.md |
| IN-03 | info | open | - |
| IN-04 | info | open | - |

Dispositions: `open` (recorded, not yet triaged), `fixed`, `skipped`, `deferred`.
Set `deferred` by hand and put the reason in the Source cell; both are preserved. A `|` in the reason is kept as prose and escaped on the next run.
Re-running the gate keeps every row it can. A row the current review no longer reports is kept and its Source cell flagged, so a finding does not leave this record silently. ONE exception: when a finding id is REUSED by a different finding, the earlier decision cannot keep a row — the id is taken — and it is dropped. A RECORDED decision (anything but `open`) is named on the console when that happens; a row still at `open` is replaced silently, because `open` records no decision to lose.
