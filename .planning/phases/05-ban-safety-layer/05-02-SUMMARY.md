---
phase: 05-ban-safety-layer
plan: 02
status: complete
subsystem: ban-safety
tags: [rate-limit, warm-up, token-bucket, jitter, persistence]
requires:
  - 05-01
provides:
  - TokenBucketRateLimiterSet minute/hour/day windows
  - SafetyOptions.BaseInterSendJitter and ThrottleNoticeThreshold
  - WarmupLadder with persisted account-age progression
affects: [Safety, Stores, SafetyEvents]
tech-stack:
  added:
    - System.Threading.RateLimiting 10.0.12
  patterns:
    - in-box TokenBucketRateLimiter per window
    - TimeProvider deterministic waits
    - store-as-source-of-truth warm-up
key-files:
  created:
    - src/Iwasug.WhatsApp.Sdk/Safety/TokenBucketRateLimiterSet.cs
    - src/Iwasug.WhatsApp.Sdk/Safety/WarmupLadder.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Safety/TokenBucketRateLimitTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SafetyJitterTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Safety/WarmupLadderTests.cs
  modified:
    - src/Iwasug.WhatsApp.Sdk/Safety/SafetyGate.cs
    - src/Iwasug.WhatsApp.Sdk/Safety/SafetyOptions.cs
metrics:
  duration: "1 execution pass"
  completed: 2026-10-14
  tasks: 3
  files: 10
actuals:
  tokens: 55000
  tasks: 3
  commits: 2
  plan_head_before: 6d4bffb2a88dde8dbf17d7968eb3620aebbd30b8
  plan_head_after: fa361ef18ce73665c61339c473a0feece24b86a0
---

# Phase 05 Plan 02: Ban-Safety Layer Summary

**Per-account token-bucket rate limits with randomized jitter and a persisted warm-up ramp.**

## Accomplishments

- Added `TokenBucketRateLimiterSet` with minute/hour/day `TokenBucketRateLimiter` windows, precedence-minute-first acquisition, partial-lease rollback, and delayed-success flagging.
- Added `SafetyOptions.BaseInterSendJitter` and `ThrottleNoticeThreshold`, integrated acquisition and jitter into `SafetyGate.AcquireRateLimits` and `ApplyJitter`.
- Implemented `WarmupLadder`: new accounts start at stage 0, `CreatedAt` is written once when a paired credential exists, and stage advances by elapsed calendar days with restart durability.
- Added focused tests for rate caps, jitter bounds/cancellation, warm-up creation/progression/restart/single-write/unpaired handling.

## Deviations from Plan

### Auto-fixed Issues

None beyond standard wiring (package added to central package management for the in-box rate limiter).

## Known Stubs

- `WarmupLadder.AdmitAsync` currently admits every send; durable per-day counters are process-local and not yet enforced. Warm-up stage math and persistence meet the plan requirements.
- Other `SafetyGate` stages (circuit, active hours, cold outreach, pacing) remain no-op and are filled by later plans.

## Self-Check: PASSED

Created files exist; build and Safety tests passed.

## Verification

- `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror --nologo`: 0 warnings, 0 errors.
- `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~Safety"`: 37 passed.

## Commits

- b4a8bf7: feat(05-02): per-account token buckets and randomized inter-send jitter
- fa361ef: feat(05-02): persisted warm-up ladder derived from account age