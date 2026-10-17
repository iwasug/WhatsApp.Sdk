---
phase: 09-ship
plan: 03
subsystem: tooling
tags: [interop-probe, drift-detection, ops-02, console-tool, tdd, loopback-peer, exit-codes, bounded-wait, mtp]
requires:
  - phase: 03-pairing-session-durability
    provides: "PairingCoordinator StartAsync/StateChanged pairing surface + the 405 -> ClientOutdated terminal (03-02 seam) the probe classifies"
  - phase: 09-ship
    provides: "713/713 suite baseline (09-01) the probe tests extend"
provides:
  - "tools/Iwasug.WhatsApp.Sdk.InteropProbe (Exe, IsPackable=false): fresh-registration connect against wss://web.whatsapp.com/ws/chat with classified outcomes accepted=0 / drift=10 / infra=20"
  - "ProbeOptions process contract: --timeout-seconds <n> (default 120, range 1..900); arg failures write one stderr message and exit 2"
  - "20 network-free probe tests: scripted 405 -> Drift, scripted ref -> Accepted, mute peer -> bounded Infra, fail-fast transport -> Infra, classifier totality, options contract"
affects: [09-04-live-interop-workflow]
actuals:
  tokens: 11073
  tasks: 3
  commits: 4
  plan_head_before: cdf4a6867cc0f1b9b83f15273d23e91b71f868be
  plan_head_after: 972c26266416c85911d11545168105f20e7a83d3
tech-stack:
  added: []
  patterns:
    - "Phone-free drift probe: fresh in-memory store + zero-material RegistrationDefaults; the pairing surface (Code/ClientOutdated) IS the verdict vocabulary"
    - "Bounded decision by construction: race terminal vs injected timeout vs connect vs pairing attempt; elapsing classifies as Infra(20), never a hang"
    - "Scripted-peer proofs over LoopbackFrameTransport (PairingQrTests stanza shapes): 405 failure node -> Drift/10, ref reply -> Accepted/0, mute peer -> Infra/20 at the bound"
    - "TDD evidence via MTP's bare --report-xunit-junit flag (JUnit XML -> check tdd-red-evidence); record persisted under the gitignored TestResults/"
key-files:
  created:
    - tools/Iwasug.WhatsApp.Sdk.InteropProbe/Iwasug.WhatsApp.Sdk.InteropProbe.csproj
    - tools/Iwasug.WhatsApp.Sdk.InteropProbe/ProbeOutcome.cs
    - tools/Iwasug.WhatsApp.Sdk.InteropProbe/ProbeRunner.cs
    - tools/Iwasug.WhatsApp.Sdk.InteropProbe/ProbeOptions.cs
    - tools/Iwasug.WhatsApp.Sdk.InteropProbe/Program.cs
  modified:
    - tests/Iwasug.WhatsApp.Sdk.Tests/Tools/InteropProbeTests.cs
    - tests/Iwasug.WhatsApp.Sdk.Tests/Iwasug.WhatsApp.Sdk.Tests.csproj
    - Iwasug.WhatsApp.Sdk.sln
decisions:
  - "The runner starts Pairing.StartAsync on the live connection before awaiting ConnectAsync — the verdict vocabulary is the pairing surface and the coordinator's attempt loop gates its first send on the channel-ready pulse, so the order is safe"
  - "RED evidence captured via MTP's bare --report-xunit-junit flag only (adding filename/result-junit flags triggers the exit-5 swallowed-error class); the record lives under the gitignored TestResults/"
  - "The three plan verify commands' '-x' flag was dropped: broken under the repo's MTP runner (Zero tests ran, exit 5) — verified via the working form per 08-VERIFICATION practice"
  - "The silence test passes in RED by design: bounded-wait behavior already existed from Task 1; the drift/ref classifications are the RED targets and both failed as intended before the start path landed"
requirements-completed: [OPS-02]
coverage:
  - id: D1
    description: "A server-scripted 405 reaches the runner as Drift with process exit 10, and the probe never retries the connect"
    requirement: OPS-02
    verification:
      - kind: unit
        ref: "InteropProbeTests.Run_maps_a_scripted_405_to_drift_exit_10 over the loopback peer (failure node -> ClientOutdated terminal -> Drift; pairs.Created == 1)"
        status: pass
    human_judgment: false
  - id: D2
    description: "A server-issued pairing ref reaches the runner as Accepted with process exit 0"
    requirement: OPS-02
    verification:
      - kind: unit
        ref: "InteropProbeTests.Run_maps_a_server_issued_ref_to_accepted_exit_0 (ref reply -> Code -> Accepted)"
        status: pass
    human_judgment: false
  - id: D3
    description: "A peer that never emits a pairing status is classified Infra(20) once the injected bound elapses — the process can never hang"
    requirement: OPS-02
    verification:
      - kind: unit
        ref: "InteropProbeTests.Run_is_bounded_when_the_peer_never_emits_a_pairing_status (2s bound; detail 'bounded wait elapsed'; elapsed within bound tolerance)"
        status: pass
      - kind: unit
        ref: "InteropProbeTests.Run_classifies_a_fail_fast_transport_as_infra_within_the_injected_bound (refusing transport; Infra/20; elapsed in [bound-250ms, bound+5s])"
        status: pass
    human_judgment: false
  - id: D4
    description: "The process contract is frozen: --timeout-seconds (default 120, 1..900), arg failures exit 2 with one stderr message, exactly one PROBE_RESULT line per probe run"
    requirement: OPS-02
    verification:
      - kind: unit
        ref: "InteropProbeTests.Options_default_to_120_seconds_when_no_arguments_are_given, Options_parse_explicit_values_and_both_boundaries, Options_reject_values_outside_the_contract (0/-1/901/abc/empty/12.5), Options_reject_missing_values_unknown_arguments_and_bare_tokens"
        status: pass
      - kind: integration
        ref: "dotnet run --project tools/Iwasug.WhatsApp.Sdk.InteropProbe --no-build -- --timeout-seconds 0 -> stderr 'interop-probe: --timeout-seconds must be between 1 and 900, got 0.', exit 2, no PROBE_RESULT line; same for 'abc'"
        status: pass
    human_judgment: false
  - id: D5
    description: "Full solution suite green with the probe included"
    requirement: OPS-02
    verification:
      - kind: integration
        ref: "dotnet test Iwasug.WhatsApp.Sdk.sln -> total 733, failed 0, succeeded 733, skipped 0 (38s)"
        status: pass
    human_judgment: false
metrics:
  duration: 35m
  completed: 2026-10-17
status: complete
---

# Phase 9 Plan 03: InteropProbe Drift Detector Summary

**A phone-free drift probe now connects with a fresh registration, classifies the pairing surface into exit 0/10/20 behind a validated `--timeout-seconds` contract, and is proven by 20 network-free tests (scripted 405 -> 10, ref -> 0, mute peer -> bounded 20) inside a 733/733 green suite**

## Performance

- **Duration:** ~35m
- **Started:** ~2026-10-17T06:31:00+07:00 (first probe source file on disk; Task 1 committed 06:44:31)
- **Completed:** 2026-10-17T07:06:00+07:00
- **Tasks:** 3
- **Files modified:** 8

## Accomplishments

- Task 1 (tracer) landed the probe end-to-end: console project in the solution's new `tools` folder (SDK-project reference only, zero packages), total `ProbeClassifier` (Code -> Accepted/0, ClientOutdated -> Drift/10, everything else -> Infra/20; exceptions -> Infra with a material-free one-line detail), a bounded `ProbeRunner` (fresh `InMemoryStoreFactory`, zero-material `RegistrationDefaults`, one-shot terminal subscription, race of terminal/timeout/connect), and a thin entry point emitting exactly one `PROBE_RESULT exit=<n> detail=<text>` line.
- Task 2 (TDD) proved the live decision path over a scripted loopback peer: a 405 failure node classifies as Drift/10 with exactly one connect (`pairs.Created == 1` — no retry), a server-issued ref as Accepted/0, and a mute peer as the bounded Infra/20 timeout. GREEN was the missing `Pairing.StartAsync` path in the runner; before it, both drift/ref tests failed with Infra at the full bound (the intentional RED).
- Task 3 froze the process contract: `ProbeOptions` (`--timeout-seconds <n>` only, default 120, 1..900, typed `ProbeOptionsException`), `Main(string[] args)` mapping parse failures to one stderr message + exit 2, and 9 option tests covering default, explicit, both boundaries, and every invalid class; the exit-2 path was smoke-verified against the real executable (stderr message, no `PROBE_RESULT` line).
- Full suite grew 713 -> 733 and passed 733/733 in 38s; probe-filter runs: RED 2 failed / 9 passed (46s — the two targets waited their full 20s bounds), GREEN 20/20 (7.4s).
- All waits are injected/bounded (no wall-clock sleeps in tests); the probe never touches `FileCredentialStore`, the only egress is the entry point's real WebSocket transport, and no key material is printed (T-9-06/T-9-07/T-9-SC mitigations held).

## Task Commits

1. **Task 1 (tracer): InteropProbe with a classified, bounded outcome** - `6d4601a` (feat)
2. **Task 2 RED: scripted-peer drift/ref verdicts fail as intended** - `bf7874f` (test)
3. **Task 2 GREEN: the pairing start path turns both targets green** - `aef7202` (feat)
4. **Task 3: process contract (options, exit 2, evidence line)** - `972c262` (feat)

## Files Created/Modified

- `tools/Iwasug.WhatsApp.Sdk.InteropProbe/Iwasug.WhatsApp.Sdk.InteropProbe.csproj` - Exe, `IsPackable=false` (D-12 comment), SDK-only ProjectReference.
- `tools/Iwasug.WhatsApp.Sdk.InteropProbe/ProbeOutcome.cs` - exit-code enum, outcome record, total classifier (`FromStatus`/`FromException`).
- `tools/Iwasug.WhatsApp.Sdk.InteropProbe/ProbeRunner.cs` - connect/observe/decide runner; Task 2 GREEN added the pairing start path + attempt-task race/teardown observe.
- `tools/Iwasug.WhatsApp.Sdk.InteropProbe/ProbeOptions.cs` - the argument contract + typed `ProbeOptionsException`.
- `tools/Iwasug.WhatsApp.Sdk.InteropProbe/Program.cs` - thin main: parse -> exit 2 on failure; run -> one `PROBE_RESULT` line, classified exit code.
- `tests/Iwasug.WhatsApp.Sdk.Tests/Tools/InteropProbeTests.cs` - 20 tests: classifier theory + sanitize/truncation, fail-fast bound, 3 scripted-peer facts, options contract.
- `tests/Iwasug.WhatsApp.Sdk.Tests/Iwasug.WhatsApp.Sdk.Tests.csproj` - probe ProjectReference (sample precedent), comment "Plan 09-03 Task 1".
- `Iwasug.WhatsApp.Sdk.sln` - `tools` solution folder + probe project entry (12 configuration rows + NestedProjects).

## Decisions Made

- The runner starts `Pairing.StartAsync` **before** awaiting `ConnectAsync`: the verdict vocabulary is the pairing surface, and the coordinator's attempt loop gates its first send on the channel-ready pulse — starting first cannot miss a pulse and cannot send early.
- Behavior-verdicts are one-shot: the first `StateChanged` event completes a `TaskCompletionSource`; the runner never re-decides, and a terminal (`ClientOutdated`) wins over the racing connect fault deterministically (monitor emits the terminal before completing first-open).
- RED evidence uses MTP's bare `--report-xunit-junit` flag only; adding filename/result-junit flags hits the exit-5 swallowed-error class. The record (`TestResults/09-03-red-405.json`) stays untracked in the gitignored `TestResults/`.
- The silence test is green through RED and GREEN by design: the bounded wait existed from Task 1; the drift/ref classifications (both failing with Infra before the start path) are the RED targets.
- `ProbeOptions` accepts exactly `--timeout-seconds <n>` (space-separated, plan-literal); anything else — unknown flags, bare tokens, `=`-form, missing value, out-of-range, non-integer — throws the typed exception and exits 2.

## Deviations from Plan

### Plan-text deviation

**1. `-x` dropped from all three Task verify commands**
- **Found during:** Task 1 verify (plan `<automated>` reads `dotnet test Iwasug.WhatsApp.Sdk.sln --filter "FullyQualifiedName~InteropProbe" -x`).
- **Issue:** `-x` breaks the repo's MTP runner: "Zero tests ran", exit 5 (same swallowed-error class as IN-10, dotnet/sdk#55309). A broken verify command cannot gate anything.
- **Fix:** Ran the exact filter without `-x` (Task 1: 8/8; RED: exit 2 with the intended 2 failures; GREEN/Task 3: 20/20), matching 08-VERIFICATION practice. Recorded in the broken-windows ledger (id 18) so 09-04's workflow author sees it.
- **Impact:** None on scope; the verify intent (filtered probe tests, non-zero on failure) is fully exercised.

### Auto-fixed Issues

None — plan executed otherwise as written.

**Total deviations:** 1 plan-text (verify command), 0 auto-fixed.
**Impact on plan:** None.

## TDD Gate Compliance

Task 2 (`tdd="true"`) followed the full RED -> GREEN -> REFACTOR cycle:

- **RED:** `bf7874f` (`test(09-03): ...`). Command: `dotnet test Iwasug.WhatsApp.Sdk.sln --filter "FullyQualifiedName~InteropProbe" --report-xunit-junit` -> exit 2, 11 tests, **2 failed / 9 passed**. Target test `Run_maps_a_scripted_405_to_drift_exit_10` failed distinctly: `Expected outcome.ExitCode to be ProbeExitCode.Drift {value: 10}, but found ProbeExitCode.Infra {value: 20}` (the scripted 405 never fired because the runner did not yet start a pairing attempt). Ref test failed the same way (`Infra` vs `Accepted`). Validated by `check tdd-red-evidence TestResults/09-03-red-405.json --raw` -> `RED_EVIDENCE_OK` (`target_test_failed`; 11 tests / 2 fail / 9 pass; JUnit report was 0 issues). Semantic assessment (recorded in the RED record): intentional RED — the target asserts Task 2 behavior the runner lacked; not a broken test, fixture, or environment fault.
- **GREEN:** `aef7202` (`feat(09-03): ...`) — the pairing start path + attempt race/teardown observe; all 11 probe tests pass in 7.4s.
- **REFACTOR:** Not needed — the GREEN diff is minimal (20 insertions / 6 deletions in one file); no cleanup commit produced.

Gate sequence check: `test(09-03)` precedes `feat(09-03)` in HEAD history; both present.

## Issues Encountered

- **MTP flag quirks:** `-x` (verify commands) and `--report-xunit-junit <file>` both trigger "Zero tests ran", exit 5; the bare `--report-xunit-junit` flag writes `TestResults/<ts>.junit.xml` correctly (the RED evidence source). `--nologo` stays off `dotnet test` (IN-10).
- **xUnit1051 analyzer:** every new `RunAsync(...)` call in the tests needed an explicit token (used `TestContext.Current.CancellationToken` and the scripted tests' own `testToken`); one build error during Task 1, fixed inline.
- **RED runtime:** the two RED targets wait their full injected 20s bounds before failing (no start path = no terminal), so the RED run takes ~46s — expected and documented, GREEN runs in ~7s.
- **Pre-existing flake:** full-suite run #1 surfaced `MessageCorrelationTests.parallel_sends_serialize_and_settle_exactly_one_outcome_each` (`SdkTransportException: Encrypted packet failed validation: unexpected nonce` via `Rig.DisposeAsync`/`ScriptedPeer.RunAsync`). Reproduces intermittently (namespace rerun failed, single rerun + full-suite rerun green); the plan's diff shares no code with the messaging rig. Logged to `deferred-items.md` (with fix direction) and the broken-windows ledger (id 19); not repaired (scope boundary).

## User Setup Required

None - the probe needs no credentials and no configuration; the live run is 09-04's gated workflow.

## Known Stubs

None.

## Next Phase Readiness

- 09-04 (live-interop workflow) can invoke `dotnet run --project tools/Iwasug.WhatsApp.Sdk.InteropProbe -- --timeout-seconds <n>`, triage exit codes 0 (accepted) / 10 (drift) / 20 (infra), and log-grep the single `PROBE_RESULT exit=<n> detail=<text>` line; arg-contract failures exit 2.
- The probe's verdict against the live server is **unproven by design** (all proofs are network-free): the first live run happens in 09-04's workflow — treat Infra(20) as "no verdict" and Drift(10) as the loud signal.
- The deferred `MessageCorrelationTests` flake (deferred-items.md, ledger id 19) can intermittently red CI; flagged for 09-07's workflow authoring alongside the pre-existing `MessagingLoopbackTests` flake from 09-01.
- The plan verify commands' `-x` defect (ledger id 18) should be considered when 09-04/09-07 copy verify-command shapes from phase plans.

---
*Phase: 09-ship*
*Completed: 2026-10-17*

## Self-Check: PASSED

- tools/Iwasug.WhatsApp.Sdk.InteropProbe/Iwasug.WhatsApp.Sdk.InteropProbe.csproj exists.
- tools/Iwasug.WhatsApp.Sdk.InteropProbe/ProbeOutcome.cs exists.
- tools/Iwasug.WhatsApp.Sdk.InteropProbe/ProbeRunner.cs exists.
- tools/Iwasug.WhatsApp.Sdk.InteropProbe/ProbeOptions.cs exists.
- tools/Iwasug.WhatsApp.Sdk.InteropProbe/Program.cs exists.
- tests/Iwasug.WhatsApp.Sdk.Tests/Tools/InteropProbeTests.cs exists.
- .planning/phases/09-ship/09-03-SUMMARY.md exists.
- Commits 6d4601a, bf7874f, aef7202, 972c262 all present in HEAD history.
