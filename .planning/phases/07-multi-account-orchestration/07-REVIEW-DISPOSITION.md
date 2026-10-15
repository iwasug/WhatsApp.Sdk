---
phase: 07
review: 07-REVIEW.md
titles: json
findings:
  - id: CR-01
    severity: critical
    disposition: open
    title: "The only host-reachable account factory wires a throwing transport and stub handshake payloads — composed accounts can never connect or pair"
  - id: CR-02
    severity: critical
    disposition: open
    title: "`AccountStatus.CircuitState` is permanently `Closed` — status is wired to a second safety gate that never gates any send"
  - id: WR-01
    severity: warning
    disposition: open
    title: "Four of eight `AccountStatus` fields are fabricated or always-default"
  - id: WR-02
    severity: warning
    disposition: open
    title: "`WhatsAppSdkOptions.EventChannelCapacity` is silently ignored"
  - id: WR-03
    severity: warning
    disposition: open
    title: "`SessionManager` lifecycle races: add-after-dispose leaks a live runtime; remove/restart can resurrect an account"
  - id: WR-04
    severity: warning
    disposition: open
    title: "The fused event reader cannot see accounts added after it starts while existing accounts are quiet"
  - id: WR-05
    severity: warning
    disposition: open
    title: "The per-account scoped logger is never wired into the account graph"
  - id: WR-06
    severity: warning
    disposition: open
    title: "DI composition hazards: host `IConfiguration` is clobbered; host `StoreRegistry` is ignored and the per-account registry is never disposed"
  - id: WR-07
    severity: warning
    disposition: open
    title: "`WhatsAppHostedService.StopAsync` has a dead drain CTS and abandons the dispose task on timeout"
  - id: WR-08
    severity: warning
    disposition: open
    title: "No public path from `IWhatsAppSessionManager` to an account's messenger/connection"
  - id: WR-09
    severity: warning
    disposition: open
    title: "AOT-compatibility claim vs. suppressed `IL2026`/`IL3050` on the configuration overload"
  - id: IN-01
    severity: info
    disposition: fixed
    title: "Stale doc comment on `OutboundEnvelope`"
  - id: IN-02
    severity: info
    disposition: open
    title: "`SessionManager._registrations` is dead state"
  - id: IN-03
    severity: info
    disposition: open
    title: "Redundant `try { ... } catch { throw; }` wrappers"
  - id: IN-04
    severity: info
    disposition: open
    title: "`LinkedCts` and `OutboundQueue` are unwired scaffolding"
  - id: IN-05
    severity: info
    disposition: open
    title: "`ScopedLogger<T>` duplicates `AccountScopedLogger<T>` in the public surface"
  - id: IN-06
    severity: info
    disposition: open
    title: "`Messenger.EventWriter` internal seam is unused"
  - id: IN-07
    severity: info
    disposition: open
    title: "`ReadAccountEventsAsync` throws on first enumeration, not on call, and double-looks-up"
  - id: IN-08
    severity: info
    disposition: open
    title: "`WhatsAppSdkOptions.Validate` reports the wrong parameter name"
  - id: IN-09
    severity: info
    disposition: open
    title: "Delegate `AddWhatsAppSdk` overload lacks `ValidateOnStart()`"
  - id: IN-10
    severity: info
    disposition: open
    title: "Plan-mandated lifecycle/health tests are missing, and one existing test encodes CR-02 as an invariant"
open: 20
total: 21
recorded: 2026-10-15T22:43:43.582Z
---

# Phase 07: Code Review Disposition

| Finding | Severity | Disposition | Source |
|---------|----------|-------------|--------|
| CR-01 | critical | open | - |
| CR-02 | critical | open | - |
| WR-01 | warning | open | - |
| WR-02 | warning | open | - |
| WR-03 | warning | open | - |
| WR-04 | warning | open | - |
| WR-05 | warning | open | - |
| WR-06 | warning | open | - |
| WR-07 | warning | open | - |
| WR-08 | warning | open | - |
| WR-09 | warning | open | - |
| IN-01 | info | fixed | 07-REVIEW-FIX.md |
| IN-02 | info | open | - |
| IN-03 | info | open | - |
| IN-04 | info | open | - |
| IN-05 | info | open | - |
| IN-06 | info | open | - |
| IN-07 | info | open | - |
| IN-08 | info | open | - |
| IN-09 | info | open | - |
| IN-10 | info | open | - |

Dispositions: `open` (recorded, not yet triaged), `fixed`, `skipped`, `deferred`.
Set `deferred` by hand and put the reason in the Source cell; both are preserved. A `|` in the reason is kept as prose and escaped on the next run.
Re-running the gate keeps every row it can. A row the current review no longer reports is kept and its Source cell flagged, so a finding does not leave this record silently. ONE exception: when a finding id is REUSED by a different finding, the earlier decision cannot keep a row — the id is taken — and it is dropped. A RECORDED decision (anything but `open`) is named on the console when that happens; a row still at `open` is replaced silently, because `open` records no decision to lose.
