---
phase: 04
review: 04-REVIEW.md
titles: json
findings:
  - id: CR-01
    severity: critical
    disposition: open
    title: "Signed-prekey signature is made by a key that no verifier in this codebase (or real WhatsApp) can verify — every fresh session establishment fails"
  - id: CR-02
    severity: critical
    disposition: open
    title: "Inbound first-contact `pkmsg` never initializes a session — the receive path can never decrypt a message from a peer that fetched our prekeys"
  - id: WR-01
    severity: warning
    disposition: fixed
    title: "Retry-keys fallback advertises a one-time prekey whose private key is immediately destroyed"
  - id: WR-02
    severity: warning
    disposition: open
    title: "Sender-key state is process-local — group E2EE material and replay counters are not durable and have no host store seam"
  - id: WR-03
    severity: warning
    disposition: open
    title: "Wholesale retry-counter clear lets a hostile flood reset the Pattern-5 cap for legitimate senders"
  - id: WR-04
    severity: warning
    disposition: open
    title: "Group retry receipts are silently never answered, justified by a stale comment that 04-05 invalidated"
  - id: WR-05
    severity: warning
    disposition: open
    title: "Prekey-readiness recompute can overwrite still-live one-time prekeys after a retry-path spend"
  - id: IN-01
    severity: info
    disposition: fixed
    title: "`TryReadBigEndian` returns a constant `true`"
  - id: IN-02
    severity: info
    disposition: open
    title: "Retry `<keys>` block wire shape deviates from the real-WA form (acknowledged in-tree)"
  - id: IN-03
    severity: info
    disposition: skipped
    title: "`DecryptPreKey` replay-dedup fallback reports a less specific failure reason"
open: 7
total: 10
recorded: 2026-10-11T23:20:14.469Z
---

# Phase 04: Code Review Disposition

| Finding | Severity | Disposition | Source |
|---------|----------|-------------|--------|
| CR-01 | critical | open | - |
| CR-02 | critical | open | - |
| WR-01 | warning | fixed | 04-REVIEW-FIX.md |
| WR-02 | warning | open | - |
| WR-03 | warning | open | - |
| WR-04 | warning | open | - |
| WR-05 | warning | open | - |
| IN-01 | info | fixed | 04-REVIEW-FIX.md |
| IN-02 | info | open | - |
| IN-03 | info | skipped | 04-REVIEW-FIX.md |

Dispositions: `open` (recorded, not yet triaged), `fixed`, `skipped`, `deferred`.
Set `deferred` by hand and put the reason in the Source cell; both are preserved. A `|` in the reason is kept as prose and escaped on the next run.
Re-running the gate keeps every row it can. A row the current review no longer reports is kept and its Source cell flagged, so a finding does not leave this record silently. ONE exception: when a finding id is REUSED by a different finding, the earlier decision cannot keep a row — the id is taken — and it is dropped. A RECORDED decision (anything but `open`) is named on the console when that happens; a row still at `open` is replaced silently, because `open` records no decision to lose.
