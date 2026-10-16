---
phase: 08
plan: 04
subsystem: accounts-outbox
tags: [outbox, durable, pump, backoff, dead-letter]
requires: [08-03]
provides:
  - OutboxPump
  - OutboxOptions
  - AccountRuntime.OutboxPump
key-files:
  created:
    - src/Iwasug.WhatsApp.Sdk/Accounts/OutboxPump.cs
    - src/Iwasug.WhatsApp.Sdk/Options/OutboxOptions.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/OutboxPumpTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Isolation/OutboxIsolationTests.cs
  modified:
    - src/Iwasug.WhatsApp.Sdk/Accounts/AccountRuntime.cs
    - src/Iwasug.WhatsApp.Sdk/Accounts/DefaultAccountRuntimeFactory.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Hosting/HostedServiceLifecycleTests.cs
decisions:
  - "OutboxPump owns no shadow list; every transition is a store row transition."
  - "Safety-rule violations schedule retry against the next window; only permanent validation failures dead-letter."
  - "Factory constructs one pump per account over StoreSession.Outbox."
metrics:
  duration: "executed"
  completed: "2026-10-16"
status: complete
actuals:
  tokens: 18000
  tasks: 1
  commits: 1
  plan_head_before: c76f84b83b542fa477053fef2ecba23d7829bbf2
  plan_head_after: 35d0d9f5f33d9f8433d206a451c7749a3832a852
---

# Phase 08 Plan 04: Durable Outbox Pump Summary

**Durable per-account outbox pump with store-backed due rows, retry/backoff, and dead-lettering.**

## Performance

- **Tasks:** 1 completed
- **Files:** 3 created, 4 modified

## Accomplishments

- Added `OutboxPump` polling `IOutboxStore.GetDueAsync`, classifying send outcomes, scheduling retries with exponential backoff + jitter, and dead-lettering permanent validation failures.
- Added `OutboxOptions` carrying SDK-owned attempt/backoff/poll policy.
- Wired an `OutboxPump` instance into `AccountRuntime` (property + disposal) and constructed it in `DefaultAccountRuntimeFactory` over `StoreSession.Outbox`.
- Added `OutboxPumpTests` and `OutboxIsolationTests`; updated a naming conflict in `HostedServiceLifecycleTests`.

## Deviations from Plan

- Fixed a pre-existing test namespace collision caused by the new `Iwasug.WhatsApp.Sdk.Options` namespace by fully qualifying `Microsoft.Extensions.Options.Options.Create`.
- Existing pairing tests have pre-existing environment flakiness (4 failures), unrelated to this plan.

## Self-Check: PASSED
