---
phase: 07-multi-account-orchestration
plan: 03
status: complete
subsystem: hosting
tags: [multi-account, hosting, di]
requires: [07-01]
provides:
  - Idiomatic .NET hosting surface (AddWhatsAppSdk, WhatsAppSdkOptions, AccountDescriptor, WhatsAppHostedService)
  - BackgroundService lifecycle with bounded drain
affects: [07-04]
tech-stack:
  added:
    - Microsoft.Extensions.DependencyInjection
  patterns:
    - IOptions<WhatsAppSdkOptions> binding via BindConfiguration + ValidateOnStart
    - Per-account option overrides resolved before runtime construction
key-files:
  created:
    - src/Iwasug.WhatsApp.Sdk/Hosting/AccountDescriptor.cs
    - src/Iwasug.WhatsApp.Sdk/Hosting/WhatsAppSdkOptions.cs
    - src/Iwasug.WhatsApp.Sdk/Hosting/ServiceCollectionExtensions.cs
    - src/Iwasug.WhatsApp.Sdk/Hosting/WhatsAppHostedService.cs
  modified:
    - src/Iwasug.WhatsApp.Sdk/Accounts/IWhatsAppSessionManager.cs
    - src/Iwasug.WhatsApp.Sdk/Accounts/SessionManager.cs
    - src/Iwasug.WhatsApp.Sdk/Accounts/DefaultAccountRuntimeFactory.cs
    - src/Iwasug.WhatsApp.Sdk/Accounts/AccountRuntime.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Hosting/AddWhatsAppSdkTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Hosting/HostedServiceLifecycleTests.cs
decisions:
  - "AccountDescriptor and WhatsAppSdkOptions public spelling locked"
  - "Per-account override precedence honored by DefaultAccountRuntimeFactory"
metrics:
  duration: "n/a"
  completed: 2026-10-15
  tasks: 3
  files: 16
  commits: 1
actuals:
  tokens: 90000
  tasks: 3
  commits: 1
plan_head_before: 26d9c2e3a50f4b6ecf6d780e51e1c52c0beb1c5c
plan_head_after: bcaab7032bbe2b3f899a2d7d943966dbceb939f6
---

# Phase 07 Plan 03: Idiomatic .NET Hosting Surface Summary

**Added container-neutral DI registration, options binding, hosted-service lifecycle with bounded drain, and per-account option overrides.**

## Accomplishments

- Added `AddWhatsAppSdk` overloads for delegate and `IConfiguration` binding.
- Implemented `WhatsAppSdkOptions`, `AccountDescriptor`, and validation.
- Implemented `WhatsAppHostedService` with prompt start and bounded stop drain.
- Extended manager and factory seams for descriptor-driven, override-aware registration.

## Task Commits

1. `bcaab70` - feat(07-03): hosting, options binding, DI registration, lifecycle drain

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Registered supplied IConfiguration to support ValidateOnStart**
- **Found during:** AddWhatsAppSdk configuration tests
- **Issue:** Bound options validation required IConfiguration in the container.
- **Fix:** Registered the supplied configuration instance inside the config overload.
- **Files modified:** src/Iwasug.WhatsApp.Sdk/Hosting/ServiceCollectionExtensions.cs
- **Commit:** bcaab70

### Known Stubs

None.

## Self-Check

PASSED