---
phase: 05-ban-safety-layer
plan: 03
status: complete
subsystem: ban-safety
tags: [cold-outreach, recipient-ledger, safety-gate, reply-exemption, contract-kit]
requires:
  - 05-02
provides:
  - IRecipientContactStore seventh capability with in-memory fallback
  - ColdOutreachBudget daily cap with reply exemption in SafetyGate
  - Recipient-contact contract kit with in-memory + scratch parity
affects: [Safety, Stores, SafetyEvents]
tech-stack:
  added: []
  patterns:
    - keyed-set ledger behind the granular store seam
    - in-process UTC-midnight daily counter over a durable contacted set
    - D-20 log-and-continue on post-send ledger marks
key-files:
  created:
    - src/Iwasug.WhatsApp.Sdk/Stores/IRecipientContactStore.cs
    - src/Iwasug.WhatsApp.Sdk/Safety/ColdOutreachBudget.cs
    - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/IRecipientContactStoreContractTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Safety/ColdOutreachBudgetTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Safety/ColdOutreachReplyExemptionTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/InMemoryRecipientContactStoreContractTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/ScratchRecipientContactStoreContractTests.cs
  modified:
    - src/Iwasug.WhatsApp.Sdk/Safety/SafetyGate.cs
    - src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs
    - src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistryOptions.cs
    - src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs
    - src/Iwasug.WhatsApp.Sdk.Testing/FaultInjection/FaultingStoreFactory.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/ScratchStoreFactory.cs
key-decisions:
  - "Reply-mark semantics follow must_have truth 4 (replies never mark), not Task 2's stray contradicting sentence; exemption covers cap AND mark, so a post-reply normal send is still cold."
  - "Contract bindings live in tests/.../StoreContract/ per repo convention, not the plan's literal tests/.../Stores/ path."
  - "Verification widens the plan's ColdOutreach filter with a RecipientContact filter — the plan filter cannot match the store-named contract suites."
patterns-established:
  - "Recipient key = bare User@Server only (T-05-08); device/agent suffixes stripped, no display name or body."
  - "FaultingStoreFactory recipientContact parameter + FaultingRecipientContactStore (FailReads/Has, FailWrites/Mark)."
metrics:
  duration: "1 execution pass"
  completed: 2026-10-14
  tasks: 3
  files: 17
actuals:
  tokens: 20000
  tasks: 3
  commits: 3
  plan_head_before: 49ae547471bf6c80f5d39ce87699ce94fc2a5f54
  plan_head_after: 610285557cb1b759eb7903b41b00b79948e1d566
requirements-completed: [SAF-04]
coverage:
  - id: D1
    description: "Additive per-account recipient ledger seam (host-pluggable + in-memory fallback) with contract parity"
    requirement: "SAF-04"
    verification:
      - kind: integration
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/InMemoryRecipientContactStoreContractTests + ScratchRecipientContactStoreContractTests (9 facts x 2 bindings)"
        status: pass
    human_judgment: false
  - id: D2
    description: "First cold contact consumes the daily budget; cap exhaustion hard-blocks with AccountRestricted + SafetyRuleViolationException(ColdOutreach) before the inner send"
    requirement: "SAF-04"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Safety/ColdOutreachBudgetTests#cap_block_raises_account_restricted_before_inner_send"
        status: pass
    human_judgment: false
  - id: D3
    description: "Inbound reply to a never-contacted recipient never consumes cold budget; normal first send does"
    requirement: "SAF-04"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Safety/ColdOutreachReplyExemptionTests#reply_exempt_from_cap_when_budget_exhausted + reply_sends_do_not_consume_daily_budget"
        status: pass
    human_judgment: false
  - id: D4
    description: "Recipient marked contacted only after inner Sent/TimedOut on normal (non-reply) sends; mark failure swallowed D-20 without breaking the outcome"
    requirement: "SAF-04"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Safety/ColdOutreachBudgetTests#mark_on_success_sent_and_timed_out_are_contacted + mark_not_on_rejection_server_error_leaves_uncontacted + mark_store_failure_is_swallowed_and_send_still_succeeds"
        status: pass
    human_judgment: false
  - id: D5
    description: "Recipient key derivation is privacy-neutral bare JID (device stripped, no display name or body)"
    requirement: "SAF-04"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Safety/ColdOutreachBudgetTests#recipient_key_is_bare_user_at_server + recipient_key_strips_device_and_holds_no_display_name_or_body"
        status: pass
    human_judgment: false
---

# Phase 05 Plan 03: Ban-Safety Cold Outreach Summary

**Daily cold-outreach budget (30/day, UTC-midnight reset) hard-enforced in SafetyGate over a durable per-account recipient ledger, with reply exemption and mark-on-success.**

## Accomplishments

- Added `IRecipientContactStore` / `IRecipientContactStoreFactory` as an additive seventh store capability: nullable `StoreRegistryOptions.RecipientContactFactory` (null = in-memory fallback), seventh resolution in `StoreRegistry`, `StoreSession.Recipients` in the owned-stores disposal cascade, and an in-memory `ConcurrentDictionary`-backed branch in `InMemoryStoreFactory`.
- Implemented `ColdOutreachBudget` over the ledger: bare-`User@Server` recipient keys, contacted recipients always allowed, replies exempt from the cap, normal cold sends counted with `SafetyRuleViolationException(SafetyRule.ColdOutreach)` + `AccountRestricted` (handler + stream twin) on exhaustion before the inner send.
- Wired the stage into `SafetyGate`: `CheckColdOutreach` consults the budget via `StoreSession.Recipients` honoring `SendContext.IsReply` (Messenger already passes `IsReply: true` only from `ReplyAsync`); `RecordOutcomeAsync` marks only after `Sent`/`TimedOut` on non-reply sends and swallows store failures with D-20 log-and-continue.
- Shipped the kit contract suite (`RecipientContactStoreContractTests`: happy path, idempotent mark, missing-returns-false, isolation, FailReads/FailWrites fault injection, D-06 second-instance visibility, 100-iteration parallel no-cross-reads) with in-memory + scratch bindings, and `FaultingRecipientContactStore` in the fault factory.
- Proved SAF-04 with 12 budget/exemption/mark/key tests plus 18 contract facts, all green.

## Task Commits

Each task was committed atomically:

1. **Task 1: Additive recipient ledger seam with in-memory fallback and contract kit** - `c6f4a84` (feat)
2. **Task 2: ColdOutreachBudget — daily cap + reply exemption + mark on success** - `52c3072` (feat)
3. **Task 3: Cold-outreach gate integration + recipient key tests** - `6102855` (test)

## Files Created/Modified

- `src/Iwasug.WhatsApp.Sdk/Stores/IRecipientContactStore.cs` - Seventh capability seam (created)
- `src/Iwasug.WhatsApp.Sdk/Safety/ColdOutreachBudget.cs` - Daily cap broker over the ledger (created)
- `src/Iwasug.WhatsApp.Sdk/Safety/SafetyGate.cs` - CheckColdOutreach + RecordOutcomeAsync (modified)
- `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs` - Seventh resolution, seven-count docs (modified)
- `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistryOptions.cs` - RecipientContactFactory (modified)
- `src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs` - Recipient-contact branch (modified)
- `src/Iwasug.WhatsApp.Sdk.Testing/FaultInjection/FaultingStoreFactory.cs` - Recipient decorator (modified)
- `src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/IRecipientContactStoreContractTests.cs` - Contract kit (created)
- `src/Iwasug.WhatsApp.Sdk.Testing/ComplianceTestBase.cs` - Floor raised to seven (modified)
- `tests/Iwasug.WhatsApp.Sdk.Tests/Safety/ColdOutreachBudgetTests.cs` - Budget/mark/key tests (created)
- `tests/Iwasug.WhatsApp.Sdk.Tests/Safety/ColdOutreachReplyExemptionTests.cs` - Exemption tests (created)
- `tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/ScratchStoreFactory.cs` - Second implementation (modified)
- `tests/.../StoreContract/InMemoryRecipientContactStoreContractTests.cs` - Reference binding (created)
- `tests/.../StoreContract/ScratchRecipientContactStoreContractTests.cs` - Scratch binding (created)
- `src/.../PublicAPI.Unshipped.txt` (both projects) - New public symbols harvested from build (modified)

## Decisions Made

- **Reply-mark semantics:** the plan contradicts itself — Task 2's action says both "only when `!context.IsReply`" and "a reply that is the first outbound still becomes contacted after success", while must_have truth 4 says marking is normal-sends-only. Truths win: replies are exempt from cap AND mark, so a normal send after a reply is still cold (conservative, safe-by-default). Consistent with T-05-07 (exemption never skips the normal-send mark path).
- **Binding placement:** repo convention (`tests/.../StoreContract/` with InMemory + Scratch bindings) over the plan's literal `tests/.../Stores/` path.
- **Verification scope:** the plan's `FullyQualifiedName~ColdOutreach` filter cannot match the store-named contract suites; ran `RecipientContact`, `Safety`, `StoreContract`, and `Compliance` filters to cover the whole change surface.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Tree did not compile on spawn: missing FaultingRecipientContactStore**
- **Found during:** Task 1 (baseline build before any new work)
- **Issue:** Uncommitted prior work referenced `FaultingRecipientContactStore` (never written) and changed the `FaultingStoreFactory` ctor without updating the Testing `PublicAPI.Unshipped.txt` — 3 build errors.
- **Fix:** Added the decorator class (FailReads/Has, FailWrites/Mark, decorator lifecycle rules), the missing `<param>` doc, and harvested the ctor + class entries into `PublicAPI.Unshipped.txt`.
- **Files modified:** `FaultingStoreFactory.cs`, `Testing/PublicAPI.Unshipped.txt`
- **Verification:** `dotnet build -warnaserror` clean
- **Committed in:** c6f4a84 (part of task commit)

**2. [Rule 2 - Missing] Scratch second implementation + compliance floor missing for the seventh seam**
- **Found during:** Task 1 (contract kit requires two bindings per D-10/D-11; ComplianceTestBase fails unbound kits)
- **Issue:** `ScratchStoreFactory` implemented only six seams and the compliance floor was still six — the new kit class would fail the completeness backstop.
- **Fix:** Added `IRecipientContactStoreFactory` + `RecipientView` to `ScratchStoreFactory`, both bindings, and raised the compliance floor to seven (its own comment mandates it).
- **Files modified:** `ScratchStoreFactory.cs`, both binding files, `ComplianceTestBase.cs`
- **Verification:** 18/18 recipient-contract facts + ComplianceTests green
- **Committed in:** c6f4a84 (part of task commit)

**3. [Rule 1 - Bug] Dead alias surface on ColdOutreachBudget**
- **Found during:** Task 2 (review of the uncommitted implementation)
- **Issue:** Three redundant internal aliases (`DeriveRecipientKey`/`GetRecipientKey`/`CreateRecipientKey`) plus a self-namespace `using`; misleading surface, zero callers.
- **Fix:** Removed aliases and the self-using; single `RecipientKey` entry point.
- **Files modified:** `ColdOutreachBudget.cs`
- **Verification:** Build + all cold tests green
- **Committed in:** 52c3072 (part of task commit)

**4. [Rule 2 - Missing] Capability-count docs still said six/five**
- **Found during:** Task 1 (plan requires docs to call it seven capabilities)
- **Issue:** Registry/options/factory remarks, the `fromHost` order doc, rollback-list capacity, and all per-store "one of the N" lines predated the seventh seam.
- **Fix:** Mechanical six/five-to-seven updates; `List<IAsyncDisposable>(6)` to `(7)`; `fromHost` order now lists recipients.
- **Files modified:** `StoreRegistry.cs`, `StoreRegistryOptions.cs`, `InMemoryStoreFactory.cs`, six store interfaces, `CredentialStoreContractTests.cs` remark
- **Verification:** Build clean (doc-only, no API change)
- **Committed in:** c6f4a84 (part of task commit)

---

**Total deviations:** 4 auto-fixed (1 blocking, 2 missing-critical, 1 bug/slop)
**Impact on plan:** All required for a compiling, contract-complete, convention-consistent delivery. No scope creep — no new packages (T-05-SC holds).

## Issues Encountered

- xUnit1051 fires on one explicit `CancellationToken.None` call site in the new tests: fixed with the file-level `#pragma warning disable xUnit1051` already established by `WarmupLadderTests` (both new Safety files).
- Dropped `Inner` delegate argument on one gate call (own typo, CS7036): fixed immediately, no plan impact.

## Known Stubs

None - no stubs introduced. (`WarmupLadder.AdmitAsync` still admits-all and circuit/active-hours/pacing stages remain no-op per 05-02; owned by later plans 05-04/05-05.)

## User Setup Required

None - no external service configuration required. No new packages.

## Verification

- `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror --nologo`: 0 warnings, 0 errors.
- `dotnet test --filter "FullyQualifiedName~ColdOutreach"`: 12 passed (9 budget/mark/key + 3 exemption).
- `dotnet test --filter "FullyQualifiedName~RecipientContact"`: 18 passed (9 facts x in-memory + scratch).
- `dotnet test --filter "FullyQualifiedName~RecipientContact|Safety|Compliance"`: 68 passed.
- `dotnet test --filter "FullyQualifiedName~StoreContract"`: 163 passed (regression over the touched ScratchStoreFactory).

## Next Phase Readiness

- Cold-outreach stage complete; SAF-04 observably true. Remaining gate stages (circuit breaker 05-04, pacing/active-hours 05-05) plug into the same `ExecuteSendAsync` sequence.
- No blockers. Note for 05-04: `SafetyGate.CheckCircuit`/`ApplyPacing`/`CheckActiveHours` are still no-ops; `WarmupLadder.AdmitAsync` still admits-all.

## Self-Check: PASSED

Created files exist; commits c6f4a84, 52c3072, 6102855 are ancestors of HEAD; build and all scoped suites green.

---
*Phase: 05-ban-safety-layer*
*Completed: 2026-10-14*
