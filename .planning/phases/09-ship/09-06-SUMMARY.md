---
phase: 09-ship
plan: 06
subsystem: docs
tags: [store-guide, webhook-recipe, contract-kit, events, backpressure, idempotency, redaction, ops-01, tests-readme]
requires:
  - phase: 01-account-foundation
    provides: "the eight store interfaces + StoreRegistry/StoreRegistryOptions + the contract kit (Iwasug.WhatsApp.Sdk.Testing) that docs/store-implementation.md documents"
  - phase: 04-signal-e2ee-messaging
    provides: "ReadEventsAsync + the four WhatsAppEvent subtypes (raised after the durable write/ack) that docs/webhook-recipe.md bridges"
  - phase: 09-ship
    provides: "09-05's forward links to docs/store-implementation.md (absolute URL in docs/risk-ledger.md) and the packaging truth that docs/ is not packed (relative in-repo links are correct here)"
provides:
  - "docs/store-implementation.md: eight-interface host guide — the seam (StoreRegistry + StoreRegistryOptions), per-store table + notes with kit contract classes, the binding flow, the RegistrationDefaults first-registration prerequisite, and failure-vs-missing / real-database notes"
  - "docs/webhook-recipe.md: host-side events-to-webhook bridge — explicit no-HTTP-server boundary, EventChannelCapacity backpressure semantics, cancellation shutdown path, typed-switch mapping, idempotency discipline, redaction guidance, end-to-end bridge sketch"
  - "corrected tests/Iwasug.WhatsApp.Sdk.Tests/README.md: empirical runner dialect (FullyQualifiedName~X), eight-interface count, all eight contract classes, relative link to the store guide"
affects: [09-07, 09-08-publish, 09-09-uat-honesty]
actuals:
  tokens: 5772
  tasks: 3
  commits: 3
  plan_head_before: 1f87895a18352729ac664dbc93e8bd71b96cb96b
  plan_head_after: 9b05ef8b0ee793d17c4f67f0642e69f8b3f5b05e
tech-stack:
  added: []
  patterns:
    - "Host docs source every name and semantic from shipped files (interface XML docs, contract classes, runtime log lines) — never invented; each task carries a machine verify asserting the required names"
    - "Recipe docs mirror the sample host's public-API shapes verbatim (await foreach + catch (OperationCanceledException); typed switch) and explicitly mark host-side sketch types as host-side"
    - "Boundary honesty stated in doc prose: the recipe says outright that the SDK ships no HTTP server and the host owns endpoint/retries/delivery"
key-files:
  created:
    - docs/store-implementation.md
    - docs/webhook-recipe.md
  modified:
    - tests/Iwasug.WhatsApp.Sdk.Tests/README.md
key-decisions:
  - "README honesty-section link inside docs/store-implementation.md corrected to ../README.md (the plan text said ../../README.md from docs/, which escapes the repo root) — Rule 1 fix"
  - "tests README's HostStyleSecondRunTests sentence rewritten to name the five capabilities it actually drives (session, identity, pre-key, recent-message cache, safety state) so it cannot read as contradicting the corrected eight-interface count"
  - "Recipe imposes no payload format and sketches host-side queue records inline (clearly labeled host-side) — the SDK-side switch is the sample's exact public-API usage, nothing invented"
requirements-completed: [OPS-01]
coverage:
  - id: D1
    description: "docs/store-implementation.md documents the seam, all eight stores with their kit contract classes, the binding flow, and the RegistrationDefaults first-registration prerequisite"
    requirement: OPS-01
    verification:
      - kind: other
        ref: "Task 1 <automated> verify: all 8 interface names, 8 contract class names, StoreRegistry/StoreRegistryOptions/ComplianceTestBase/RegistrationDefaults/CreateFactory present — pass"
        status: pass
    human_judgment: true
    rationale: "The machine check proves required names only; whether every stated per-store rule traces to an interface XML doc / contract class (no invented semantics) is a reading judgment, and OPS-01's docs-alone journey reads this guide at UAT."
  - id: D2
    description: "docs/webhook-recipe.md documents the host-side bridge: four event subtypes, EventChannelCapacity=128 backpressure, cancellation shutdown, idempotency discipline, redaction guidance, and the explicit no-HTTP-server boundary"
    requirement: OPS-01
    verification:
      - kind: other
        ref: "Task 3 <automated> verify: 4 subtypes + ReadEventsAsync + EventChannelCapacity + OperationCanceledException + 'no HTTP server' + redaction regex — pass"
        status: pass
    human_judgment: true
    rationale: "Whether the guidance could be read as teaching silent retries or implying an SDK-hosted endpoint is a reading judgment; machine checks assert only the required vocabulary and boundary phrases."
  - id: D3
    description: "tests/Iwasug.WhatsApp.Sdk.Tests/README.md corrected: stale filter claim gone, FullyQualifiedName~X dialect documented, 'eight store interfaces', all eight contract classes listed, closing link to the store guide, stale promise removed"
    requirement: OPS-01
    verification:
      - kind: other
        ref: "Task 2 <automated> verify: no 'VSTest-only'; 'FullyQualifiedName~' present; 'eight store interfaces'; CredentialStoreContractTests/OutboxStoreContractTests/IRecipientContactStoreContractTests spot-checks; docs/store-implementation.md link; no 'ships with the Phase 9 documentation' — pass"
        status: pass
    human_judgment: false
  - id: D4
    description: "Plan-level: solution build and full test suite remain green after the docs edits"
    requirement: OPS-01
    verification:
      - kind: integration
        ref: "dotnet build Iwasug.WhatsApp.Sdk.sln --nologo -> Build succeeded, 0 Warning(s), 0 Error(s); dotnet test Iwasug.WhatsApp.Sdk.sln -> total 733, failed 0, succeeded 733, skipped 0 (44s) — pass"
        status: pass
    human_judgment: false
metrics:
  duration: 10m
  completed: 2026-10-17
status: complete
---

# Phase 9 Plan 06: Store Guide, Webhook Recipe & Tests-README Correction Summary

**docs/store-implementation.md (eight-interface host guide with kit-contract mapping and the RegistrationDefaults first-registration prerequisite) and docs/webhook-recipe.md (events-to-webhook bridge with bounded-channel backpressure, idempotency discipline, and redaction guidance) shipped; tests/README.md corrected to the empirical runner dialect and the shipped store set**

## Performance

- **Duration:** ~10 min
- **Started:** 2026-10-17T00:28:03Z
- **Completed:** 2026-10-17T00:37:32Z
- **Tasks:** 3
- **Files modified:** 3

## Accomplishments

- **docs/store-implementation.md created** (220 lines) — the host store guide 09-05's forward links already point at. The seam section documents `StoreRegistry` + `StoreRegistryOptions` (per-capability registration, instance-as-namespace, registry-owned disposal) and the in-memory store's prototyping-only position linking the README honesty section. The eight-store table pairs each interface with the record it persists and the kit contract class that verifies it — `ICredentialStore`/`CredentialStoreContractTests` through `IRecipientContactStore`/`IRecipientContactStoreContractTests` (I-prefixed name documented as shipped). The binding flow mirrors the tests README (subclass a contract, override `CreateFactory`, empty `ComplianceTestBase` subclass, failing fact = fix the store), and the **first-registration section** states the `RegistrationDefaults` prerequisite for real signed-prekey material with the shipped rejection log (`DefaultAccountRuntimeFactory`) — the Pitfall-12 closure OPS-01's docs-alone journey hangs on.
- **docs/webhook-recipe.md created** (149 lines) — the host-side bridge from `messaging.ReadEventsAsync(ct)` to the host's own delivery pipeline. Scope states the boundary explicitly: **no HTTP server ships in the SDK**; the host owns the endpoint, retries, and delivery. The stream section documents the four subtypes (`MessageReceivedEvent`, `ReceiptReceivedEvent`, `MessageAckedEvent`, `DecryptionFailedEvent`), `Messenger.EventChannelCapacity = 128` backpressure (slow consumer slows the receive pump, never drops), and `OperationCanceledException` as the documented shutdown path — with the sample host's exact `await foreach` shape. The mapping section mirrors the sample's typed switch and lists what each event carries; the discipline section carries the README idempotency contract (no silent retries, host dedupes on message id); the redaction section forbids logging content/beyond-need identifiers and states key material never appears in events (T-4-01).
- **tests/Iwasug.WhatsApp.Sdk.Tests/README.md corrected** — the stale paragraph claiming the repo's runner rejects the filter option is replaced with the empirically proven `dotnet test … --filter "FullyQualifiedName~X"` dialect (07-VERIFICATION.md: isolation 8/8; union form 17 passed) keeping `dotnet exec … -method` as an equivalent alternative; "the five store interfaces" corrected to **eight** with all eight contract classes listed; the closing "ships with the Phase 9 documentation" promise replaced by the relative link `[docs/store-implementation.md](../../docs/store-implementation.md)`.
- **All three task verifies green** (`TASK1/2/3 VERIFY PASS`) and plan-level verification clean: build 0 warnings / 0 errors; full suite **733/733 passed, 0 failed, 0 skipped** (44s).
- Reinforces T-9-12/T-9-13: no stale shipped-surface claim ships, and the recipe instructs redaction instead of teaching one.

## Task Commits

1. **Task 1: docs/store-implementation.md — the eight-interface host guide** - `1d471be` (docs)
2. **Task 2: tests/README.md — filter dialect, interface count, guide link** - `d15482d` (docs)
3. **Task 3: docs/webhook-recipe.md — events-to-webhook host bridge** - `9b05ef8` (docs)

## Files Created/Modified

- `docs/store-implementation.md` (created) - the seam, eight-store table + per-store notes sourced from interface XML docs, contract-kit binding flow, RegistrationDefaults first-registration prerequisite, failure-vs-missing and real-database guidance.
- `docs/webhook-recipe.md` (created) - scope/no-server boundary, event stream semantics + backpressure + shutdown, typed-switch mapping, idempotency discipline, redaction guidance, end-to-end bridge sketch.
- `tests/Iwasug.WhatsApp.Sdk.Tests/README.md` (modified) - empirical filter dialect, eight store interfaces, full eight-class contract list, relative guide link.

## Decisions Made

- The store guide's README honesty link uses `../README.md` (one level), not the plan text's `../../README.md` — from `docs/` the two-level path escapes the repo root; `docs/` is not packed so the relative form is correct in-repo (Rule 1 fix, recorded below).
- The tests README's `HostStyleSecondRunTests` sentence names the five capabilities it actually drives (session, identity, pre-key, recent-message cache, safety state) instead of "all five capabilities", so it is unambiguous next to the corrected eight-interface count.
- The recipe imposes no payload format and labels its queue records as host-side sketches; the switch arms use only shipped public API (`m.Key.Id`, `r.Kind`, `a.Status`, `f.RetryCount`, …) exactly as the sample does — nothing invented.
- Every name/fact in both docs was sourced from shipped files (interface XML docs, contract classes, runtime log line), never from memory — with the task verify scripts machine-asserting the required vocabulary.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] README honesty-section link depth in docs/store-implementation.md**
- **Found during:** Task 1 (store guide authoring)
- **Issue:** The plan's action text specified the link as `../../README.md#in-memory-reference-store--read-this`; from `docs/` that path resolves above the repository root (a dead link at the first jump hosts are asked to make).
- **Fix:** Used `../README.md#in-memory-reference-store--read-this` (correct one-level ascent from `docs/`).
- **Files modified:** docs/store-implementation.md
- **Verification:** Target resolves from `docs/` in the working tree; plan assumption confirms `docs/` is not packed, so relative in-repo links are the correct form here.
- **Committed in:** `1d471be` (Task 1 commit)

**2. [Rule 1 - Bug] tests README sentence would have kept a stale-count reading**
- **Found during:** Task 2 (tests README correction)
- **Issue:** The `HostStyleSecondRunTests.cs` paragraph said it "drives all five capabilities" — accurate about the test, but directly adjacent to the corrected "eight store interfaces" it reads as "five total", i.e. exactly the stale-claim class the plan's prohibition forbids.
- **Fix:** Rewrote the sentence to name the five capabilities actually driven (session, identity, pre-key, recent-message cache, safety state).
- **Files modified:** tests/Iwasug.WhatsApp.Sdk.Tests/README.md
- **Verification:** Re-read `StoreContract/HostStyleSecondRunTests.cs` — the six facts exercise exactly those five capabilities through one factory; verify script passes.
- **Committed in:** `d15482d` (Task 2 commit)

---

**Total deviations:** 2 auto-fixed (2 bugs).
**Impact on plan:** None on scope — both fixes are correctness repairs inside the plan's declared file set; the plan's prohibitions (no stale shipped-surface claims, no silent-retry/no-server misreadings) are strictly better served.

## Issues Encountered

None.

## User Setup Required

None - documentation-only plan; no external service configuration required.

## Known Stubs

None.

## Next Phase Readiness

- `docs/store-implementation.md` now exists, so 09-05's forward references resolve (docs/risk-ledger.md absolute URL; README Install/Compliance sections) — the store guide is the doc OPS-01's "install it and reach first send following the docs alone" journey reads.
- 09-08 (publish): docs/ is not packed (only README.md is, via Directory.Build.targets) — these guides use relative in-repo links and do not affect the nupkg content gate.
- 09-09 (UAT authoring): honesty items to cover — the `RegistrationDefaults` supply path before first registration (now documented in the guide), failure-vs-missing store semantics, and the recipe's host-owns-the-endpoint boundary.
- Pre-existing deferred flakes (WINDOWS.md #17 `MessagingLoopbackTests` line 873, #19 `MessageCorrelationTests` line 354) remain open; untouched by this docs-only plan — the full suite ran green regardless.

---
*Phase: 09-ship*
*Completed: 2026-10-17*

## Self-Check: PASSED

- docs/store-implementation.md exists.
- docs/webhook-recipe.md exists.
- tests/Iwasug.WhatsApp.Sdk.Tests/README.md exists (corrected, committed).
- .planning/phases/09-ship/09-06-SUMMARY.md exists.
- Commits 1d471be, d15482d, 9b05ef8 present in HEAD history.
- Task files committed cleanly (no uncommitted task changes).
