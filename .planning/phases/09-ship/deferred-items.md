# Deferred Items — Phase 09 (ship)

## Out-of-scope discoveries from 09-01

### Flaky: MessagingLoopbackTests.decrypt_failure_no_ack_no_event_and_pump_continues (pre-existing)

- **File:** `tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/MessagingLoopbackTests.cs` (peer launch ~861–865, first use at line 873).
- **Observed:** 2026-10-17, full-suite run while verifying 09-01 Task 2 — `System.NullReferenceException` at line 873 (`peer!.SendStanzaAsync(...)`): total 713 / failed 1 / succeeded 712. An isolated run of the test and the next full-suite run both passed (713/713, 40s).
- **Diagnosis:** test-harness race, pre-existing (the pattern predates 09-01; the file was last touched in 05-05, commit `6607313`). The shape is `ScriptedPeer? peer = null; var peerTask = Task.Run(async () => { peer = await CompletePeerHandshakeAsync(...); ... })`. The client's `ConnectAsync` returns as soon as it processes the peer's encrypted `success` stanza, which `CompletePeerHandshakeAsync` sends as its last act before returning (line ~541). The `peer` field assignment only happens after that method returns on the peer task; under threadpool pressure (full-suite parallelism) the test continuation wins the race and reads `peer == null`.
- **Not caused by 09-01:** the only 09-01 edits are in `PairingLifecycleTests.cs` and a doc comment in `PairingCoordinator.cs` — neither shares code with this test.
- **Fix direction (future plan):** hand the peer back through a `TaskCompletionSource<ScriptedPeer>` completed alongside the return and await that before first use (or otherwise synchronize on the peer task), applied consistently across every test using the `peer = await CompletePeerHandshakeAsync` pattern (`Messaging/MessagingLoopbackTests.cs`, `Connection/*`, `Pairing/PairingQrTests.cs`).
- **Impact if unfixed:** intermittent single-test failure in CI / the 09-04 workflow (rare — one observation across full-suite runs on 2026-10-17; the immediate rerun was green).
