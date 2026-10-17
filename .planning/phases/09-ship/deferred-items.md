# Deferred Items — Phase 09 (ship)

## Out-of-scope discoveries from 09-01

### Flaky: MessagingLoopbackTests.decrypt_failure_no_ack_no_event_and_pump_continues (pre-existing)

- **File:** `tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/MessagingLoopbackTests.cs` (peer launch ~861–865, first use at line 873).
- **Observed:** 2026-10-17, full-suite run while verifying 09-01 Task 2 — `System.NullReferenceException` at line 873 (`peer!.SendStanzaAsync(...)`): total 713 / failed 1 / succeeded 712. An isolated run of the test and the next full-suite run both passed (713/713, 40s).
- **Diagnosis:** test-harness race, pre-existing (the pattern predates 09-01; the file was last touched in 05-05, commit `6607313`). The shape is `ScriptedPeer? peer = null; var peerTask = Task.Run(async () => { peer = await CompletePeerHandshakeAsync(...); ... })`. The client's `ConnectAsync` returns as soon as it processes the peer's encrypted `success` stanza, which `CompletePeerHandshakeAsync` sends as its last act before returning (line ~541). The `peer` field assignment only happens after that method returns on the peer task; under threadpool pressure (full-suite parallelism) the test continuation wins the race and reads `peer == null`.
- **Not caused by 09-01:** the only 09-01 edits are in `PairingLifecycleTests.cs` and a doc comment in `PairingCoordinator.cs` — neither shares code with this test.
- **Fix direction (future plan):** hand the peer back through a `TaskCompletionSource<ScriptedPeer>` completed alongside the return and await that before first use (or otherwise synchronize on the peer task), applied consistently across every test using the `peer = await CompletePeerHandshakeAsync` pattern (`Messaging/MessagingLoopbackTests.cs`, `Connection/*`, `Pairing/PairingQrTests.cs`).
- **Impact if unfixed:** intermittent single-test failure in CI / the 09-04 workflow (rare — one observation across full-suite runs on 2026-10-17; the immediate rerun was green).

## Out-of-scope discoveries from 09-03

### Flaky: MessageCorrelationTests.parallel_sends_serialize_and_settle_exactly_one_outcome_each (pre-existing)

- **File:** `tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/MessageCorrelationTests.cs` (fact at ~line 354; the fault surfaces through `Rig.DisposeAsync` → the shared scripted peer at `Messaging/MessagingLoopbackTests.cs:310`).
- **Observed:** 2026-10-17, while verifying 09-03 — `SdkTransportException: Encrypted packet failed validation: unexpected nonce` raised by `MessagingLoopbackTests.ScriptedPeer.RunAsync`, surfaced via `MessageCorrelationTests.Rig.DisposeAsync` (full suite: 733 total / 1 failed / 732 passed; the isolated `FullyQualifiedName~MessageCorrelationTests` rerun failed the same fact; the single-fact rerun and the next full-suite run both passed).
- **Diagnosis:** test-harness race in the shared scripted peer under full-suite parallelism (same family as the 09-01 discovery above), pre-existing — the only 09-03 edits are probe tooling, probe tests, the tests csproj, and the sln; none share code with the messaging rig.
- **Fix direction (future plan):** same as the 09-01 entry — synchronize the peer hand-back on the peer task (TaskCompletionSource) rather than a captured field; cover the `Rig.DisposeAsync` teardown path in `MessageCorrelationTests`.
- **Impact if unfixed:** intermittent single-fact failure in CI / the 09-04 workflow (observed on two consecutive runs, then green twice; teardown-shaped, not product-shaped).
