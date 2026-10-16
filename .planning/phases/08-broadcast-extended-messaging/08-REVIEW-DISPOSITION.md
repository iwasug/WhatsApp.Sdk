---
phase: 08
review: 08-REVIEW.md
titles: json
findings:
  - id: CR-01
    severity: critical
    disposition: fixed
    title: "OutboxPump background loop ignores its own stop/dispose token, so `DisposeAsync` can hang"
  - id: WR-01
    severity: warning
    disposition: fixed
    title: "`OutboxPump.DefaultSendAsync` rejects every non-text payload kind despite the store accepting them"
  - id: WR-02
    severity: warning
    disposition: fixed
    title: "`OutboxPump.DisposeAsync` blocks synchronously on store disposal"
  - id: WR-03
    severity: warning
    disposition: fixed
    title: "`InMemoryOutboxStore` filters due rows against wall-clock time instead of an injected clock"
  - id: WR-04
    severity: warning
    disposition: fixed
    title: "`Messenger.SendBroadcastAsync` aborts on the first failure and discards partial results"
  - id: WR-05
    severity: warning
    disposition: fixed
    title: "`Messenger.RaiseMessageReceivedAsync` does not isolate handler exceptions, unlike the new Phase 8 raise methods"
  - id: WR-06
    severity: warning
    disposition: fixed
    title: "`OutboxPump.StartAsync` ignores stop requests that use an unlinked caller token"
  - id: IN-01
    severity: info
    disposition: fixed
    title: "Unused static `Random _jitter` field is dead code"
  - id: IN-02
    severity: info
    disposition: fixed
    title: "Inbound presence state parse mislabels unknown types as `Available`"
  - id: IN-03
    severity: info
    disposition: fixed
    title: "`BroadcastOptions` / `SendBroadcastAsync` do not validate a negative inter-recipient delay"
  - id: IN-04
    severity: info
    disposition: fixed
    title: "`Testing.StoreContract.FaultingStoreFactory` has no outbox fault-injection seam"
open: 0
total: 11
recorded: 2026-10-16T13:05:24.361Z
---

# Phase 08: Code Review Disposition

| Finding | Severity | Disposition | Source |
|---------|----------|-------------|--------|
| CR-01 | critical | fixed | 08-REVIEW-FIX.md |
| WR-01 | warning | fixed | 08-REVIEW-FIX.md |
| WR-02 | warning | fixed | 08-REVIEW-FIX.md |
| WR-03 | warning | fixed | 08-REVIEW-FIX.md |
| WR-04 | warning | fixed | 08-REVIEW-FIX.md |
| WR-05 | warning | fixed | 08-REVIEW-FIX.md |
| WR-06 | warning | fixed | 08-REVIEW-FIX.md |
| IN-01 | info | fixed | 08-REVIEW-FIX.md |
| IN-02 | info | fixed | 08-REVIEW-FIX.md |
| IN-03 | info | fixed | 08-REVIEW-FIX.md |
| IN-04 | info | fixed | 08-REVIEW-FIX.md |

Dispositions: `open` (recorded, not yet triaged), `fixed`, `skipped`, `deferred`.
Set `deferred` by hand and put the reason in the Source cell; both are preserved. A `|` in the reason is kept as prose and escaped on the next run.
Re-running the gate keeps every row it can. A row the current review no longer reports is kept and its Source cell flagged, so a finding does not leave this record silently. ONE exception: when a finding id is REUSED by a different finding, the earlier decision cannot keep a row — the id is taken — and it is dropped. A RECORDED decision (anything but `open`) is named on the console when that happens; a row still at `open` is replaced silently, because `open` records no decision to lose.
