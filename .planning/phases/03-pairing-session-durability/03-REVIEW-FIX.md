---
phase: 03
fixed_at: 2026-10-10T13:10:48Z
review_path: .planning/phases/03-pairing-session-durability/03-REVIEW.md
iteration: 1
findings_in_scope: 13
fixed: 13
skipped: 0
status: all_fixed
---

# Phase 03: Code Review Fix Report

**Fixed at:** 2026-10-10T13:10:48Z
**Source review:** .planning/phases/03-pairing-session-durability/03-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 13 (fix_scope=all: CR-01..04, WR-01..05, IN-01..04)
- Fixed: 13
- Skipped: 0

Each fix was applied against the live source (not blind patching of the review's line
numbers), verified with the 3-tier strategy, and committed atomically — one commit per
finding. Findings whose fix changes runtime behavior are recorded as
**"fixed: requires human verification"**: the tiers verify syntax and structure, never
semantic correctness, and this environment cannot execute the test suite (see
Verification).

## Verification

- **Where the gates ran:** the isolated git worktree
  `.claude/worktrees/rf-03-1822-1791631668` (branch `gsd-reviewfix/03-1822`), NOT the
  main checkout. The worktree has since been torn down after the workflow fast-forwarded
  `agent-03-07` to `1bf5d76`, so the same build is now reproducible directly from the
  main checkout.
- **Gate:** `dotnet build Iwasug.WhatsApp.Sdk.sln --nologo -v q` → **0 warnings, 0
  errors** after every finding's fix (final run after IN-04: 0/0). `TreatWarningsAsErrors`
  is on, so this also covers analyzer gates (PublicAPI/RS rules, xUnit analyzers).
- **Test execution:** BLOCKED — Application Control refuses to launch the test host
  (0x800711C7). No test in this phase ran, before or after these fixes. Test-only findings
  (CR-02, CR-04's contract suite, WR-03) are therefore **build-verified only**; passing
  test bodies are an unverified claim until the suite runs on a machine that permits it.

## Fixed Issues

### CR-01: Companion hello/finish stanzas bypass the verified success path

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Pairing/PairingCoordinator.cs`, `tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairingLifecycleTests.cs`
**Commit:** 586a4fd
**Status:** fixed: requires human verification
**Applied fix:** Routed `companion-hello`/`companion-finish` stanzas into the shared
verify-then-persist drain: hello is sequence-gated and acknowledged, the finish's GCM
bundle is authenticated under the live code's bundle key and only the authenticated
container reaches `DrainPairSuccessAsync` — the same Verify → Put → Read → Reply →
Success ordering the QR path uses. Plan-mandated companion-flow tests added.

### CR-02: Plan-mandated durability tests missing (kill_restart, drop_428, drop_515, concurrent)

**Files modified:** `tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ResumeTests.cs`, `tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairingSuccessFixture.cs`, `tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairingLifecycleTests.cs`
**Commit:** ab06a1b
**Status:** fixed: requires human verification (tests authored, build-verified, NOT executed)
**Applied fix:** Authored the four plan tests with a shared pair-success fixture
(`PairingSuccessFixture`), de-duplicating the pre-existing pairing lifecycle scaffolding
into it.
**Sub-tasks NOT performed (skip reason):** the review's "investigate the Phase-C 428-drop
timeout" and "re-record the 03-07 gate / close the STATE.md item" both require observing
a test run. Test execution is blocked by Application Control (0x800711C7), so neither can
be done honestly in this environment — they remain open for a human run of the suite.

### CR-03: Pairing sample not committed / reworked off the public API

**Files modified:** `Directory.Packages.props`, `Iwasug.WhatsApp.Sdk.sln`, `samples/Iwasug.WhatsApp.Sdk.PairingSample/{Program.cs,FileCredentialStore.cs,Iwasug.WhatsApp.Sdk.PairingSample.csproj}`, `src/Iwasug.WhatsApp.Sdk/Pairing/HandshakePayloadProvider.cs`, `src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt`
**Commit:** b8dd261
**Status:** fixed
**Applied fix:** Committed the sample and wired it into the solution under CPM;
reworked `Program.cs` onto the public API only (`WaConnection.Pairing`, `StateChanged`
events, `InMemoryStoreFactory`); opened the intended public seam —
`HandshakePayloadProvider` public 3-arg constructor delegating to the internal 4-arg one
(`WaVersion?` stays internal), `RegistrationDefaults` as a public sealed record — with 34
RS0016 entries added to `PublicAPI.Unshipped.txt`. (`LoadCredentialsAsync`/
`ReadCredentialsAsync` remain `public` because CS0737 forbids public implementations of
an internal-inherited member.)

### CR-04: FileCredentialStore serializes a projection, not the record

**Files modified:** `samples/Iwasug.WhatsApp.Sdk.PairingSample/FileCredentialStore.cs`, `samples/Iwasug.WhatsApp.Sdk.PairingSample/Program.cs`, `tests/Iwasug.WhatsApp.Sdk.Tests/Iwasug.WhatsApp.Sdk.Tests.csproj`, `tests/Iwasug.WhatsApp.Sdk.Tests/Stores/FileCredentialStoreContractTests.cs`
**Commit:** 9aeeb4d
**Status:** fixed: requires human verification (serialization semantics + contract suite not executed)
**Applied fix:** `PutAsync` now serializes the whole `CredentialRecord` (the record type
IS the contract — no field projection to silently drop must-not-lose material);
`FileCredentialStoreContractTests : CredentialStoreContractTests, IDisposable` binds the
file store to the nine-fact contract suite via a temp-dir factory and adds a torn-write
test (xUnit1051 satisfied with `TestContext.Current.CancellationToken`); tests reference
the sample via `ProjectReference`; `--store` mode is wired to the real file store.

### WR-01: Device/registration-id numerics accepted unvalidated before persistence

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Pairing/PairSuccessVerifier.cs`, `tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairSuccessVerifierTests.cs`
**Commit:** afd4d1b
**Status:** fixed: requires human verification
**Applied fix:** Added the domain range check AFTER device-signature verification and
BEFORE `PairSuccessResult.Verified` — `RegistrationId > ClientPayloadFactory.MaxRegistrationId`
or `Device > int.MaxValue` now yields `Rejected(Malformed)` instead of flowing into the
credential row; `uint32.MaxValue` tests cover both fields.

### WR-02: Watchdog Timeout can race an in-flight durable pair-success write

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Pairing/PairingCoordinator.cs`, `tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairingLifecycleTests.cs`
**Commit:** 7fb27a3
**Status:** fixed: requires human verification
**Applied fix:** Introduced `Attempt.PersistWindow` — opened under `_gate` before
`PutAsync` in `CompletePairSuccessAsync` (new `attempt` parameter, single call site) and
cleared in its `catch`. `Terminate` suppresses ONLY `PairingStatus.Timeout` while the
window is open (LoggedOut/ClientOutdated pass through) and clears the window when taking
any terminal; a durable outcome therefore supersedes the watchdog timeout (option a).
The test's `SlowPersistStore` jumps the fake clock past `AttemptTimeout` mid-write and
asserts exactly one terminal (Success), no Timeout, and the full phase sequence.

### WR-03: Pairing-code entry path untested (five plan behaviors)

**Files modified:** `tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairingLifecycleTests.cs`
**Commit:** a983336
**Status:** fixed (build-verified only — suite cannot execute here)
**Applied fix:** Added the five plan-mandated tests with grep-visible names:
`Pairing_code_happy_path_display_is_deterministic_under_injected_randomness` (display
re-derives exactly from injected code bytes via `RecordingRandomness.ExpectedDisplay()`);
`Pairing_code_single_attempt_rejects_second_entry_point` (both `RequestPairingCodeAsync`
and `StartAsync` throw `InvalidOperationException` while live, with no wire traffic);
`Pairing_code_cancellation_resets_attempt_before_any_iq` (armed `GateFirstGetStore`
parks the bootstrap so cancellation lands before any iq, then a retry proves the attempt
reset); `Pairing_code_failure_reply_resets_attempt` (correlated
`BuildCodeErrorReply` — deliberately not 405, the refetch path — surfaces
`SdkConnectionException` and the retry succeeds); and
`Pairing_code_validation_rejects_bad_phone_numbers_with_typed_reasons` (NonDigits /
TooShort / LeadingZero with no echoed input).

### WR-04: Sample runtime flows are nonfunctional (all three modes)

**Files modified:** `samples/Iwasug.WhatsApp.Sdk.PairingSample/Program.cs`
**Commit:** c31e5f6
**Status:** fixed: requires human verification (cannot run against the live network here)
**Applied fix:** `--code <digits>` now carries the phone number as a raw digit string
(no `int.TryParse`, so leading zeros can't be silently lost), validated ONLY by the SDK —
the sample's disagreeing duplicate validator (`IsValidPhoneNumber`, which accepted 6
digits the SDK rejects) was deleted and `PairingCodeRequestException` prints the typed
reason. Both live modes call `connection.ConnectAsync()` first (the coordinator waits on
the channel-ready pulse before it can send) and exit only after the attempt's terminal
event (`TaskCompletionSource` signalled from `StateChanged`). QR mode renders a real QR
from the `Code` event's payload instead of the fabricated `{"version":...}` placeholder,
which was removed along with the dead `storeDir` computation.

### WR-05: Sample FileCredentialStore violates failure≠missing (D-19)

**Files modified:** `samples/Iwasug.WhatsApp.Sdk.PairingSample/FileCredentialStore.cs`
**Commit:** ffede16
**Status:** fixed: requires human verification
**Applied fix:** `GetAsync` no longer wraps everything as "failed to deserialize":
only `System.Text.Json.JsonException` becomes the deserialization `SdkStoreException`
(with the parse exception as cause); `IOException`/`UnauthorizedAccessException` map to
`SdkStoreException` with their cause; `OperationCanceledException` propagates untouched.
`RemoveAsync` checks cancellation OUTSIDE the try (an OCE can never be swallowed as
`false`), throws `SdkStoreException` on real IO/permission errors, and returns `false`
only when the file genuinely does not exist.

### IN-01: `PairingCodeRequestException.Reason` defaults to `NonDigits` for host-constructed instances

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Pairing/PairingCodeRequestException.cs`, `src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt`
**Commit:** 8ee1c3e
**Status:** fixed
**Applied fix:** `Reason` is now `PairingCodeValidationReason?` — `null` documents "no
validation rule was evaluated" instead of a confidently wrong `NonDigits`. The internal
SDK ctor keeps setting a real reason; the public ctor is unchanged in signature. The
PublicAPI.Unshipped.txt entry was updated (RS0016-clean build).

### IN-02: adv.proto comment misdocuments the HMAC input

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Proto/adv.proto`
**Commit:** 90d99c7
**Status:** fixed
**Applied fix:** Rewrote the `AdvDetails` comment to match the implemented (correct)
behavior: the verifier HMACs the exact length-delimited bytes as received on the wire —
after the strict walk for unknown/duplicate fields — and re-serialization
(`details.ToByteArray()`) is deliberately NOT used because re-encoding could diverge from
the authenticated bytes (malleability gap). Verified against `PairSuccessVerifier`'s use
of `detailsBytes` (HMAC, account-signature, and device-signature inputs).

### IN-03: NOTICE.md provenance row cites a non-existent file

**Files modified:** `NOTICE.md`
**Commit:** 7bafe63
**Status:** fixed
**Applied fix:** Replaced `src/Iwasug.WhatsApp.Sdk/Pairing/CodeAlphabet.cs` with
`src/Iwasug.WhatsApp.Sdk/Pairing/PairingCodeGenerator.cs` (where the Crockford alphabet
actually lives, line 65). Grepped every `.cs`/`.proto`/`.json` path in NOTICE.md — this
was the only dangling citation; all other cited leaf filenames exist.

### IN-04: Untracked execution artifacts, including a duplicate phase directory

**Files modified:** `.gitignore` (worktree); main-checkout deletions performed directly
**Commit:** 1bf5d76
**Status:** fixed
**Applied fix:** Added an anchored `/test-*.txt` ignore rule (verified with
`git check-ignore`: root run logs match, `tests/**/test-*.txt` fixtures do not). The
deletion half of the finding was performed in the MAIN checkout (untracked files only,
each confirmed via `git ls-files` first): removed root `test-full.txt`,
`test-lifecycle.txt`, `test-linkcode.txt`, `test-output.txt`, `test-verify.txt`,
`completion_format.md`, and the duplicate directory
`.planning/phases/03-Pairing & Session Durability/` (one file, `03-06-PAIRING-SUMMARY.md`)
— the canonical `.planning/phases/03-pairing-session-durability/` is kept. The legitimately
untracked phase summaries (`03-05-SUMMARY.md`, `03-07-SUMMARY.md`) and the sentinel (via
the cleanup tail) were NOT deleted as artifacts. One extra step was required for the
fast-forward: main's untracked `samples/` drafts (3 files) sat exactly on the paths the
branch tracks and would have blocked the merge — they are superseded pre-CR-03 drafts
(diffs confirmed the committed versions descend from them), so they were moved to
`%TEMP%\sdk-wa-samples-draft-backup-20261010\` before the merge rather than deleted;
the tracked post-fix versions are now in place.

## Notes

- **CR-04 / WR-04 `--store` overlap:** `--store <dir>` feeds three deliberate flows:
  `--self-test` constructs `FileCredentialStore` over the dir when given (else
  in-memory), live-mode provider creation uses it when `selfTestStoreDir` is set, and
  `RunStorePersistence` constructs `new FileCredentialStore(accId, dir)` when `--store`
  is the only flag (the dedicated persistence probe, which exits 0). Same option, three
  consumers by design — flagged here so the overlap reads as intentional.
- **WR-04 semantics:** `--code <digits>` is the PHONE number's digits, not a pairing
  code; the usage text and example were updated accordingly.
- **CR-02 open sub-tasks** (Phase-C 428-drop investigation, 03-07 gate re-record /
  STATE.md close) are explicitly not done — they need a test run that this environment
  cannot perform.

---

_Fixed: 2026-10-10T13:10:48Z_
_Fixer: the agent (gsd-code-fixer)_
_Iteration: 1_
