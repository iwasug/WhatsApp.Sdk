---
phase: 05-ban-safety-layer
verified: 2026-10-14T13:00:00Z
status: passed
score: 5/5 must-haves verified
covered_files:
  - .planning/phases/05-ban-safety-layer/05-01-PLAN.md
  - .planning/phases/05-ban-safety-layer/05-01-SUMMARY.md
  - .planning/phases/05-ban-safety-layer/05-02-PLAN.md
  - .planning/phases/05-ban-safety-layer/05-02-SUMMARY.md
  - .planning/phases/05-ban-safety-layer/05-03-PLAN.md
  - .planning/phases/05-ban-safety-layer/05-03-SUMMARY.md
  - .planning/phases/05-ban-safety-layer/05-04-PLAN.md
  - .planning/phases/05-ban-safety-layer/05-04-SUMMARY.md
  - .planning/phases/05-ban-safety-layer/05-05-PLAN.md
  - .planning/phases/05-ban-safety-layer/05-05-SUMMARY.md
  - .planning/phases/05-ban-safety-layer/05-06-PLAN.md
  - .planning/phases/05-ban-safety-layer/05-06-SUMMARY.md
  - NOTICE.md
  - README.md
  - src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs
  - src/Iwasug.WhatsApp.Sdk/Messaging/SafetyEvents.cs
  - src/Iwasug.WhatsApp.Sdk/Safety/CircuitBreaker.cs
  - src/Iwasug.WhatsApp.Sdk/Safety/CircuitState.cs
  - src/Iwasug.WhatsApp.Sdk/Safety/ColdOutreachBudget.cs
  - src/Iwasug.WhatsApp.Sdk/Safety/HumanPacing.cs
  - src/Iwasug.WhatsApp.Sdk/Safety/ISafetyGate.cs
  - src/Iwasug.WhatsApp.Sdk/Safety/SafetyGate.cs
  - src/Iwasug.WhatsApp.Sdk/Safety/SafetyGateException.cs
  - src/Iwasug.WhatsApp.Sdk/Safety/SafetyOptions.cs
  - src/Iwasug.WhatsApp.Sdk/Safety/TokenBucketRateLimiterSet.cs
  - src/Iwasug.WhatsApp.Sdk/Safety/WarmupLadder.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/IRecipientContactStore.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SafetyGateWarmupTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Safety/WarmupLadderTests.cs
covered_digest: "v3:sha256:53841e4bd4f24ee4dec3151d373a482398ca1599021aad6c126169b668db33d1"
behavior_unverified: 0
overrides_applied: 0
re_verification:
  previous_status: gaps_found
  previous_score: 4/5
  gaps_closed:
    - "A warm-up ramp caps new accounts (roadmap SC2 warm-up clause; SAF-03 enforcement)"
  gaps_remaining: []
  regressions: []
advisory:
  - finding: "M-1 concurrent-send warm-up TOCTOU: gate AdmitAsync runs before the MessageSender inner-gate semaphore, so N concurrent in-flight sends can all admit against the same counter value before any RecordSendAsync increments (overshoot bounded by in-flight count)"
    category: architectural
    reason: "Accepted residual per plan threat model T-05-16 (shared lock makes each op atomic; residual admit-then-record race accepted because per-account wire sends serialize in the MessageSender semaphore). No must_have truth requires concurrent atomicity — all SC2b/05-06 must_haves are sequential behaviors proven by sequential tests. Fail-open direction is mild (slightly softer day-0 ceiling under concurrency), bounded, and not a privilege escalation. Verdict: not a gap, not a blocker; recorded for Phase 7 multi-account awareness."
    evidence_status: "code-read (SafetyGate.cs CheckWarmupAsync/RecordOutcomeAsync ordering vs MessageSender.cs _sendGate semaphore); no concurrent test executed"
---

# Phase 05: Ban-Safety Layer Verification Report

**Phase Goal:** Every send traverses a mandatory, safe-by-default gate that materially lowers ban risk. (ROADMAP.md Phase 5; requirements SAF-01..SAF-07)
**Verified:** 2026-10-14T13:00:00Z
**Status:** passed
**Re-verification:** Yes — after gap closure (05-06; prior status gaps_found 4/5, single FAILED truth SC2b)

**Note on MVP-mode format guard:** the ROADMAP Phase 5 goal is not an `As a / I want / so that` user story (`user-story.validate` returns false for it — same as Phases 1-4, which were completed under identical goal style). Per orchestrator instruction, verification proceeds against the 5 roadmap Success Criteria, which are precise and observable. No refusal; format deviation recorded here.

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| SC1 | No public API path can send without traversing the safety gate — send-path audit proves zero ungated egress (SAF-01) | ✓ VERIFIED | `Messenger.cs` lines 189/217/282: all three public sends (`SendMessageAsync`, `SendGroupMessageAsync`, `ReplyAsync`) run `EnsureOwnPrekeysAsync` then wrap inner `_sender` calls in `_safety.ExecuteSendAsync` (`IsReply:true` only from `ReplyAsync`). `MessageSender` is `internal`; only `Messenger` invokes it. `SendPathAuditTests.every_send_node_egress_site_is_whitelisted_or_gated` (with `ProtocolEgress` Ack/Receipt/RetryResponder whitelist) passes in the 100/100 safety run. Regression check: unchanged since prior verification. |
| SC2a | Per-account token buckets (minute/hour/day) with randomized inter-send jitter throttle traffic (SAF-02) | ✓ VERIFIED | `TokenBucketRateLimiterSet`: three `TokenBucketRateLimiter` via `Create()` factory (lines 37-39; `QueueLimit=0`, `AutoReplenishment`), minute→hour→day precedence with partial-lease rollback and disposal on cancel; failure throws `SafetyRuleViolationException(RateLimit)` before inner send. Jitter ratio-bounded via `BaseInterSendJitter`, `Task.Delay(..., _clock, ct)` cancellation-aware. Threshold/cancel/jitter-bounds tests pass in the 100/100 run. Regression check: unchanged. |
| SC2b | A warm-up ramp caps new accounts with account-age persisted in the store (SAF-03) | ✓ VERIFIED | **Gap closed by 05-06 — independently re-verified in source.** `WarmupLadder.AdmitAsync(int stage, ct)` (lines 56-70) enforces `_sentToday < CurrentStageCeiling(stage)` under the shared `_countLock` with UTC-day reset; `RecordSendAsync` (lines 72-89) applies the same day-reset then increments `_sentToday++` under the same lock; dead `Dictionary` counts, empty try/catch, and unused stage local are gone. `SafetyGate.CheckWarmupAsync` (lines 190-199) passes the resolved stage into `AdmitAsync` — the `_ = stage;` discard is absent (grep-confirmed zero matches) — and throws `SafetyRuleViolationException(RateLimit)` on refusal before the inner send, reusing the existing RateLimit→BudgetExhausted handler+stream path (`PublishViolationAsync` lines 95/119-121). Behavioral proof: `SafetyGateWarmupTests.warmup_ceiling_rejects_21st_send_before_inner` (20 Sent, 21st throws RateLimit, inner count pinned at 20) and `warmup_rejection_emits_budget_exhausted_once` (exactly one handler + one stream event, zero AccountRestricted) plus `WarmupLadderTests` 3 new facts (`day_zero_ceiling_refuses_21st_send`, `counter_resets_at_utc_midnight`, `restart_preserves_stage_derived_ceiling`) alongside all 6 pre-existing facts — 11/11 warm-up facts green inside the 100/100 run. |
| SC2c | A separate hard-enforced budget blocks first contact with never-messaged recipients (SAF-04) | ✓ VERIFIED | `ColdOutreachBudget`: cap from `MaxColdOutreachPerDay`, `RecipientKey` bare `User@Server`, `SafetyRuleViolationException(ColdOutreach)` + `AccountRestricted` before inner send on exhaustion; reply exemption from cap AND mark; mark only after `Sent`/`TimedOut` on non-reply sends with D-20 swallow (`SafetyGate.RecordOutcomeAsync` lines 246-270). Additive `IRecipientContactStore` seam (`StoreSession.Recipients`, `StoreRegistryOptions.RecipientContactFactory`, in-memory fallback, contract kit ×2 bindings; `StoreRegistry.cs` lines 71/359-364/561-602). Budget/exemption/mark/key tests pass in the 100/100 run. Regression check: unchanged. |
| SC3 | Throttle signals trip a circuit breaker with exponential backoff and emit typed safety events (SAF-05) | ✓ VERIFIED | `CircuitBreaker`: `DefaultFailureThreshold=5` / `DefaultTimeoutThreshold=3`, time-based gating via `NextProbeAtUtc` (30s doubling capped 10min), single-probe HalfOpen, `RecordOutcome` returns true exactly on fresh Open transitions; `SafetyGate` emits `AccountRestricted` once per opening (`PublishBreakerOpenAsync`) and stays silent on steady-state `CircuitOpen` refusals. 6 event types (BudgetExhausted/ThrottleEngaged/AccountRestricted × EventHandler + stream twins) raised via `Messenger` re-raise + bounded publish. Breaker + gate-integration tests pass in the 100/100 run. Regression check: unchanged. |
| SC4 | Human-like pacing runs before sends — typing/recording presence within an active-hours window (SAF-06) | ✓ VERIFIED | `HumanPacing.AssertInsideActiveHours` is a mandatory early gate stage (`CheckActiveHours`), `InactiveHours` refusal, midnight-wrap + zone handling; `ApplyPresencePauseAsync` draws 2000–12000ms via CSPRNG, best-effort presence (only cancellation propagates), `TimeProvider` waits; default `NoOpPacingPresence` is the documented intentional seam for the Phase 8 wire upgrade while the pause itself applies. Pacing tests pass in the 100/100 run. Regression check: unchanged. |
| SC5 | Defaults are safe; exceeding them requires explicit `AllowUnsafeThroughput` opt-in; docs ship an honest risk ledger (SAF-07) | ✓ VERIFIED | `SafetyOptions.SafeDefaults` (20/250/1000, 30 cold, 0.4–0.9 jitter, full-day window); `Validate` rejects >60/min, >600/hr, >1000/day unless `AllowUnsafeThroughput` + non-empty `DeclaredUnsafeAcknowledgment` (lines 125-145, read in full). All 5 `SafetyOptionsValidationTests` facts pass (`defaults_accepted`, `inverted_jitter_rejected`, `unsafe_rejected_without_opt_in`, `unsafe_accepted_with_ack`, `missing_ack_rejected`). README Ban-safety risk ledger: does / does-not / opt-in banner (line 103 daily-ceiling sentence now true as written), zero `ban-proof`/`undetectable` (grep-confirmed). NOTICE.md Phase 5 audit row present (line 64: no new upstream reads, no MPL-2.0 contamination). COVERAGE.md exact declaration present. PublicAPI.Unshipped.txt harvests the full safety surface (91-line grep sample incl. SafetyOptions, RecipientContact, BudgetExhausted). Regression check: unchanged. |

**Score:** 5/5 truths verified (0 present, behavior-unverified)

**Prior gap closure:** the single FAILED truth SC2b from the prior report is now VERIFIED — all 4 `missing` items addressed (stage ceiling enforced in AdmitAsync; RecordSendAsync increments; RateLimit + BudgetExhausted rejection before inner send; day-0/midnight/restart/gate-rejection/event tests exist). No new gaps; no regressions in previously passing truths (quick regression check: existence + wiring + suite membership, all green).

**Plan-truth rollup:** all must_have truths across 05-01..05-06 hold as written: gate seam + audit + options + events (05-01); buckets + jitter + stage derivation + first-pair write (05-02); cold cap + reply exemption + ledger seam + mark-on-success (05-03); breaker + single-emit + active-hours + pacing (05-04); opt-in+ack + ledger + NOTICE + COVERAGE + closeout gate (05-05); day-0 rejection + counter increment + stage threading + BudgetExhausted emission + midnight reset + restart ceiling (05-06). `SafetyHealthRecord.cs` (listed in 05-04 `files_modified`, never created) remains a documented omission with no truth requiring it — not a gap.

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `Safety/ISafetyGate.cs` | Mandatory gate seam (`ISafetyGate`, `SendContext`) | ✓ VERIFIED | Exists, substantive, wired (3 call sites in Messenger) |
| `Safety/SafetyGate.cs` | Policy sequence + event seams | ✓ VERIFIED | Research order CheckCircuit→ActiveHours→RateLimits→Warmup→ColdOutreach→Jitter→Pacing→inner→RecordOutcome; discard gone; stage threaded |
| `Safety/SafetyOptions.cs` | Safe defaults + validation | ✓ VERIFIED | Defaults + Validate + ack-gated opt-in; consumed by gate, limiter, pacing |
| `Safety/SafetyGateException.cs` | `SafetyRule` + typed violation | ✓ VERIFIED | 4-rule enum + `SafetyRuleViolationException : SdkException` |
| `Messaging/SafetyEvents.cs` | 6-type event vocabulary | ✓ VERIFIED | All 6 types exist; re-raised via Messenger twins + stream |
| `Safety/TokenBucketRateLimiterSet.cs` | Minute/hour/day limiter set | ✓ VERIFIED | 3 buckets via factory, rollback, jitter; called from `AcquireRateLimits` |
| `Safety/WarmupLadder.cs` | Stage math + persistence + enforcement | ✓ VERIFIED | **Was HOLLOW, now enforced**: ceiling check + counter increment under shared lock |
| `Safety/ColdOutreachBudget.cs` | Daily cap broker | ✓ VERIFIED | Called from `CheckColdOutreach`; mark in `RecordOutcomeAsync` |
| `Stores/IRecipientContactStore.cs` | Additive 7th seam | ✓ VERIFIED | Interface + factory; host-first + in-memory fallback; disposal cascade |
| `Safety/CircuitBreaker.cs` + `CircuitState.cs` | Breaker state machine | ✓ VERIFIED | Owned transitions, single-emit; called from `CheckCircuit`/`RecordOutcomeAsync` |
| `Safety/HumanPacing.cs` | Window + presence pause | ✓ VERIFIED | Called from `CheckActiveHours`/`ApplyPresencePauseAsync` |
| `tests/.../Safety/SafetyGateWarmupTests.cs` | Gate-level warm-up proofs | ✓ VERIFIED | 2 facts; both pass; inner-never-called + single-emission proven |
| `tests/.../Safety/WarmupLadderTests.cs` | Ladder proofs | ✓ VERIFIED | 9 facts (6 pre-existing unmodified + 3 new); all pass |
| `README.md` / `NOTICE.md` / `COVERAGE.md` | Ledger / audit / declaration | ✓ VERIFIED | Content confirmed; daily-ceiling sentence now true; audit row; exact declaration |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| Messenger (3 public sends) | ISafetyGate | `_safety.ExecuteSendAsync` wrapping inner delegate | WIRED | Lines 189/217/282; `IsReply:true` only from `ReplyAsync` |
| Messenger | SafetyEvents | Gate event subscription → `Raise*Async` twins + `PublishAsync` | WIRED | Re-raise points intact |
| SafetyGate | TokenBucketRateLimiterSet | `AcquireRateLimits` → `TryAcquireAsync` + `ApplyJitter` | WIRED | ThrottleEngaged/BudgetExhausted paths intact |
| WarmupLadder | ISafetyStateStore | `GetAsync`/`PutAsync` before continuing | WIRED | First-write-wins; durably awaited |
| SafetyGate | WarmupLadder (enforcement seam) | `CheckWarmupAsync` → `AdmitAsync(stage)`; `RecordOutcomeAsync` → `RecordSendAsync` | WIRED | **Was the discarded-stage seam; now threaded** |
| ColdOutreachBudget | IRecipientContactStore | `HasContactedAsync`/`MarkContactedAsync`, bare-JID key | WIRED | Via `StoreSession.Recipients` |
| StoreRegistry | IRecipientContactStore | `RecipientContactFactory` (null = in-memory) | WIRED | 7th resolution + disposal |
| SafetyGate | CircuitBreaker | `CheckCircuit` / `RecordOutcome` | WIRED | Single-emit via `PublishBreakerOpenAsync` |
| HumanPacing | SafetyGate | `CheckActiveHours` + `ApplyPresencePause` | WIRED | Mandatory early stage |
| SafetyOptions | README | Thresholds + opt-in banner documented | WIRED | Ledger mirrors Validate bands (60/600/1000) |

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|---------------|--------|--------------------|--------|
| SafetyGate.ExecuteSendAsync | SendOutcome | Inner `MessageSender` send (wire ack) | ✓ | ✓ FLOWING |
| TokenBucket acquisition | Lease result | In-process `TokenBucketRateLimiter` ×3 | ✓ | ✓ FLOWING |
| Warmup stage | `SafetyStateRecord.CreatedAt` | `ISafetyStateStore` (host/in-memory) | ✓ read, ✓ now enforced | ✓ FLOWING (was STATIC) |
| Warmup counter | `_sentToday` per UTC day | In-process under shared lock; ceiling (not counter) survives restart | ✓ | ✓ FLOWING |
| Cold-outreach decision | `HasContactedAsync` | `IRecipientContactStore` | ✓ | ✓ FLOWING |
| Breaker transitions | SendOutcome stream | Inner outcome incl. ServerError/TimedOut | ✓ | ✓ FLOWING |
| Active-hours decision | `_clock.GetUtcNow()` | Injected TimeProvider + options zone | ✓ | ✓ FLOWING |

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| Build warning-free | `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror --nologo` | 0 warnings, 0 errors (11.59s; independently re-run) | ✓ PASS |
| Safety/cold/ledger/audit suites green | `dotnet test --filter "FullyQualifiedName~Safety\|ColdOutreach\|RecipientContact\|TokenBucket\|Warmup\|CircuitBreaker\|HumanPacing\|SendPathAudit"` | 100 passed, 0 failed (was 95; +5 new, zero regressions; independently re-run) | ✓ PASS |
| Full suite carries only quarantined pairing faults | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` (full, run once) | 599/603; `PairingLifecycleTests` filter shows exactly the same 4 failures (`TaskCanceledException` in `WaitForPairingEventAsync`); pairing path never touches the gate | ✓ PASS (quarantined, out of scope) |
| No discard / debt markers in Safety | grep `_ = stage;` + TODO/FIXME/XXX/TBD/placeholder/console in `Safety/` | Zero matches | ✓ PASS |
| No overclaim language in README | grep `ban-proof`/`undetectable` README.md | Zero matches; `AllowUnsafeThroughput` banner present | ✓ PASS |
| No new packages | 05-05 recorded `dotnet list package` diff empty; no package adds in 05-06 summary | No new packages | ✓ PASS |

### Probe Execution

No probes declared for Phase 5 (in-process policy, no external API; COVERAGE.md declares exactly that). Skipped with reason.

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|-------------|-------------|--------|----------|
| SAF-01 | 05-01, 05-05 | Mandatory gate, no bypass | ✓ SATISFIED | 3 gated sends + passing send-path audit |
| SAF-02 | 05-02 | Token buckets + jitter | ✓ SATISFIED | Limiter set + jitter tests green |
| SAF-03 | 05-02, 05-06 | Warm-up ramp + persisted age + enforced ceiling | ✓ SATISFIED | State + progression + **enforcement** (day-0 rejection, midnight reset, restart ceiling, gate RateLimit + BudgetExhausted) green |
| SAF-04 | 05-03 | Cold-outreach budget | ✓ SATISFIED | Cap + exemption + ledger + mark tests green |
| SAF-05 | 05-04 | Breaker + backoff + events | ✓ SATISFIED | Transition + single-emit tests green |
| SAF-06 | 05-04 | Pacing + active-hours | ✓ SATISFIED | Window + pause tests green (no-op wire seam documented for Phase 8) |
| SAF-07 | 05-01, 05-05 | Safe defaults + opt-in + ledger | ✓ SATISFIED | Validation tests + ledger + NOTICE + COVERAGE |

No orphaned requirements: plan-declared IDs {SAF-01, SAF-07, SAF-02, SAF-03, SAF-04, SAF-05, SAF-06} ∪ {SAF-03} = all 7 SAF IDs mapped to Phase 5 in REQUIREMENTS.md. Every ID accounted for.

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| — | — | `TODO/FIXME/XXX/TBD`, `Console.*`, empty returns, `_ = stage;` in `Safety/` and `Stores/` | None | Clean scan (prior Blocker resolved) |
| `Messaging/Messenger.cs` | 33 | Stale comment ("send gate lives in MessageSender") | ℹ️ Info | Doc-only; no behavior impact; carried forward from prior report |
| `Safety/WarmupLadder.cs` + `SafetyGate.cs` | Admit→Record TOCTOU under concurrency | 📋 Advisory | See `advisory` frontmatter (M-1): accepted residual per T-05-16, bounded, no truth requires concurrent atomicity — not a gap |

### Human Verification Required

None. All phase behaviors are covered by automated tests (100/100 in the scoped run plus full-suite confirmation); no visual, real-time, or external-service surface is introduced by this phase. (Live-traffic ban-effectiveness is inherently unprovable in verification and is correctly disclaimed by the risk ledger rather than asserted. The M-1 concurrent-send residual is an accepted design trade-off documented in the plan threat model, not a human-testable behavior.)

### Gaps Summary

No gaps. The prior report's single FAILED truth (SC2b warm-up enforcement) is closed by 05-06 and independently re-verified in source and behavior: the stage ceiling is enforced on every gated send, the counter increments on every settled send, over-ceiling sends are rejected with `SafetyRuleViolationException(RateLimit)` + exactly-once `BudgetExhausted` before the inner send, and the counter resets at the UTC-midnight boundary with the stage-derived ceiling (not the counter) surviving restart via persisted `CreatedAt`. All other truths show no regressions. The 4 full-suite `PairingLifecycleTests` failures are quarantined pre-existing environment faults (same 4 as the prior report; pairing path never touches the gate) and are not a Phase-5 gap. No deferred items: no later phase covers warm-up admission, and none needs to — it is done here.

---

_Verified: 2026-10-14T13:00:00Z_
_Verifier: the agent (gsd-verifier)_
