# Phase 9 CI Evidence

Created: 2026-10-17T07:02:04Z (UTC). Authored by execution of plan 09-07.

## Push

- Pushed head SHA (short): `73e86d6`
- Pushed head SHA (full): `73e86d6f7a39ccab2197f619030eb0f4e61c04d8`
- Push type: fast-forward only (`git push origin master`, no `--force`). `origin/master..master` count = 0 after push.
- Pre-flight (local, immediately before push): `dotnet build Iwasug.WhatsApp.Sdk.sln --nologo` -> success, 0 warnings, 0 errors. `dotnet test Iwasug.WhatsApp.Sdk.sln` -> Passed! total: 733, failed: 0, succeeded: 733, skipped: 0, duration 47s.
- Remote: https://github.com/iwasug/WhatsApp.Sdk (visibility PUBLIC as of 2026-10-17).

## ci.yml first run

- Pushed head SHA (short): `73e86d6`
- Run URL: https://github.com/iwasug/WhatsApp.Sdk/actions/runs/39012085682
- Trigger: push (event `push`)
- Created (UTC): 2026-10-17T07:01:28Z
- Conclusion: `failure` (status `completed` at 2026-10-17T07:01:31Z)
- Isolation pass step: NOT RUN. The job `build-test` never started.
- Cause: GitHub Actions annotation on the job: "The job was not started because your account is locked due to a billing issue." (job ID 117336580967). This is an account-level billing lock on the `iwasug` account, not a code failure. It reproduces on every workflow in this repo and across multiple other `iwasug` repos (documented in STATE.md).
- Therefore the A3 Linux verdict (behavior of the four repaired `PairingLifecycleTests` on `ubuntu-latest`) is NOT available from this run: no test executed, no stdout. The local suite is green (733/733) on Windows; Linux behavior remains unknown until the billing lock is lifted and a run actually executes.
- The prior `ci.yml` startup_failure run on the previous head (not this push) for reference: https://github.com/iwasug/WhatsApp.Sdk/actions/runs/38990401650 (same billing annotation; "This workflow run cannot be retried").

## live-interop calibration

Dispatched runs (all triggered by `workflow_dispatch` against `master` at head `73e86d6`):

| # | Run URL | Created (UTC) | Conclusion | Probe exit code | Classification | Observation |
|---|---------|---------------|------------|-----------------|----------------|-------------|
| 1 | https://github.com/iwasug/WhatsApp.Sdk/actions/runs/39012106148 | 2026-10-17T07:01:49Z | failure | none (job never started) | INFRA / not run | GitHub Actions annotation: "The job was not started because your account is locked due to a billing issue." (job ID 117336648999). The workflow's `probe exit code:` line was never emitted because the runner never picked up the job. No protocol drift evidence (exit 10) and no infra-classified probe evidence (exit 20) could be collected. |

No additional dispatches were attempted: with the account billing lock in effect, every further dispatch reproduces the identical startup_failure and yields no new information (no probe exit code, no log). Dispatching more would only create billing-blocked run records.

## A2 verdict

Hosted-runner acceptance (probe exit 0 = server issued a pairing ref) is NOT measured. No probe job started; no `probe exit code:` line exists in any log. The billing lock prevents the measurement entirely.

Interim classification: INFRA-blocked at the runner-scheduling layer (account billing), which is distinct from the probe's own exit-20 infra classification (datacenter-IP throttling). The probe's exit-code taxonomy (10 = drift, 20 = probe-level infra) was never exercised.

Self-hosted fallback paragraph (documented per plan, to be applied only if/when hosted runs actually execute and exit 20): If three consecutive real (job-started) runs exit 20, hosted-runner egress appears infra-blocked; the documented fallback is a self-hosted runner on a residential IP running the same `live-interop.yml` workflow. That runner must never carry publish capability (release.yml stays on GitHub-hosted runners with OIDC). The schedule stays enabled and its INFRA annotation is correct, not a drift signal. This fallback is NOT yet triggered by the present evidence because no run reached the probe.

## A3 verdict

Linux behavior of the four repaired `PairingLifecycleTests` (`Companion_finish_from_a_rotated_code_epoch_is_rejected`, `Companion_finish_with_foreign_bundle_key_fails_closed`, `Persist_window_suppresses_watchdog_timeout_until_durable_success`, `Companion_hello_ack_finish_reaches_verified_success`) is NOT measured. The ci.yml run on head `73e86d6` (https://github.com/iwasug/WhatsApp.Sdk/actions/runs/39012085682) never started its job due to the billing lock, so no test executed on `ubuntu-latest`. No quarantine-with-tracking path was applied because there is no Linux failure to classify.

## Blocker (account-level, requires human action)

GitHub Actions cannot start any job on the `iwasug` account: every run is created then immediately reaches `startup_failure` with the annotation "The job was not started because your account is locked due to a billing issue." Verified on this repo (push run https://github.com/iwasug/WhatsApp.Sdk/actions/runs/39012085682 and dispatch run https://github.com/iwasug/WhatsApp.Sdk/actions/runs/39012106148) and on multiple other `iwasug` repos. Making the repository PUBLIC did not lift the lock.

Required (account owner only): resolve the billing lock at https://github.com/settings/billing (settle failed payment / update payment method / raise budget). Once lifted, re-push a commit (or re-run via `gh workflow run`) so a fresh ci.yml run executes; the pushed head `73e86d6` is unchanged and ready. Then re-dispatch `live-interop.yml` 2-3 times and append the real probe exit codes to the calibration table above. Do not fabricate green/exits in the interim.

Local gate remains green (733/733) and origin/master is at `73e86d6`.