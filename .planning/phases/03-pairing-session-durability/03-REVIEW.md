---
phase: 03-pairing-session-durability
reviewed: 2026-10-10T11:23:29Z
depth: standard
files_reviewed: 57
files_reviewed_list:
  - NOTICE.md
  - src/Iwasug.WhatsApp.Sdk.Testing/ComplianceTestBase.cs
  - src/Iwasug.WhatsApp.Sdk.Testing/FaultInjection/FaultingStoreFactory.cs
  - src/Iwasug.WhatsApp.Sdk.Testing/PublicAPI.Unshipped.txt
  - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/CredentialStoreContractTests.cs
  - src/Iwasug.WhatsApp.Sdk/Connection/ConnectionOptions.cs
  - src/Iwasug.WhatsApp.Sdk/Connection/DisconnectReason.cs
  - src/Iwasug.WhatsApp.Sdk/Connection/QueryRouter.cs
  - src/Iwasug.WhatsApp.Sdk/Connection/ReconnectPolicy.cs
  - src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs
  - src/Iwasug.WhatsApp.Sdk/Connection/WaVersionResolver.cs
  - src/Iwasug.WhatsApp.Sdk/Domain/CredentialRecord.cs
  - src/Iwasug.WhatsApp.Sdk/Iwasug.WhatsApp.Sdk.csproj
  - src/Iwasug.WhatsApp.Sdk/Noise/NoiseHandshake.cs
  - src/Iwasug.WhatsApp.Sdk/Pairing/ClientPayloadFactory.cs
  - src/Iwasug.WhatsApp.Sdk/Pairing/HandshakePayloadProvider.cs
  - src/Iwasug.WhatsApp.Sdk/Pairing/LinkCodeCrypto.cs
  - src/Iwasug.WhatsApp.Sdk/Pairing/PairSuccessVerifier.cs
  - src/Iwasug.WhatsApp.Sdk/Pairing/PairingCodeGenerator.cs
  - src/Iwasug.WhatsApp.Sdk/Pairing/PairingCodeRequestException.cs
  - src/Iwasug.WhatsApp.Sdk/Pairing/PairingCoordinator.cs
  - src/Iwasug.WhatsApp.Sdk/Pairing/PairingEvents.cs
  - src/Iwasug.WhatsApp.Sdk/Pairing/PairingQrPayload.cs
  - src/Iwasug.WhatsApp.Sdk/Proto/adv.proto
  - src/Iwasug.WhatsApp.Sdk/Proto/client_payload.proto
  - src/Iwasug.WhatsApp.Sdk/Proto/companion_reg.proto
  - src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt
  - src/Iwasug.WhatsApp.Sdk/Stores/ICredentialStore.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistryOptions.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ClientOutdatedRoutingTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ConnectionCredentialTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ConnectionStateTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Connection/LoopbackRoundTripTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Connection/LoopbackTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Connection/QueryRouterTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ReconnectPolicyTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ResumeTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Connection/WaVersionResolverTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Iwasug.WhatsApp.Sdk.Tests.csproj
  - tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/ClientPayloadTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/LinkCodeTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairSuccessVerifierTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairingLifecycleTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairingQrTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/InMemoryCredentialStoreContractTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/ScratchCredentialStoreContractTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/ScratchStoreFactory.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/TestData/client-payload-login.json
  - tests/Iwasug.WhatsApp.Sdk.Tests/TestData/client-payload-registration.json
  - tests/Iwasug.WhatsApp.Sdk.Tests/TestData/link-code-kats.json
  - tests/Iwasug.WhatsApp.Sdk.Tests/TestData/pair-success-container.json
  - samples/Iwasug.WhatsApp.Sdk.PairingSample/Program.cs
  - samples/Iwasug.WhatsApp.Sdk.PairingSample/FileCredentialStore.cs
  - samples/Iwasug.WhatsApp.Sdk.PairingSample/Iwasug.WhatsApp.Sdk.PairingSample.csproj
findings:
  critical: 4
  warning: 5
  info: 4
  total: 13
status: issues_found
---

# Phase 03: Code Review Report

**Reviewed:** 2026-10-10T11:23:29Z
**Depth:** standard
**Files Reviewed:** 57
**Status:** issues_found

## Summary

Adversarial standard-depth review of the Phase 03 diff (`e815228^..HEAD`, 54 committed
files) plus the three untracked sample files that are part of the phase deliverable.
Core crypto surfaces (Noise handshake, link-code chain, pair-success verification,
adv/client-payload schemas) are carefully built — strict fail-closed parsing, fixed-time
HMAC comparison, KAT-pinned derivations, single verified write path into credential
persistence, and a well-ordered success sequence. The problems cluster where the phase
reached beyond that core: the companion handshake routing for link-code pairing is dead
code with false security documentation, plan-mandated durability and code-flow tests are
missing from the repository, the sample host does not build and misreports what it does,
and the sample's file credential store silently drops key material.

**Verification caveats:** `dotnet build Iwasug.WhatsApp.Sdk.sln` succeeds with 0
warnings / 0 errors (confirmed). The test suite could NOT be executed in this review
environment: the runner fails on this machine with `FileLoadException ... AwesomeAssertions.dll —
An Application Control policy has blocked this file (0x800711C7)` for every test, which
is an environment policy artifact, not a code defect — so no test-run evidence is claimed
either way. The sample build failure below was reproduced directly
(`dotnet build samples/...` → NU1504, Build FAILED). The `dotnet test --nologo`
"Zero tests ran" quirk is a known, documented toolchain issue (tests csproj IN-10) and is
NOT reported as a finding.

## Narrative Findings (AI reviewer)

### CR-01: Companion handshake routing is dead code — the hello signal is never completed and the documented bundle-key finish authentication does not exist

**Severity:** Critical (BLOCKER)
**File:** `src/Iwasug.WhatsApp.Sdk/Pairing/PairingCoordinator.cs:648-666` (also `:130`, `:136`, `:421-422`, `:708-785`, `:1571`)

**Issue:** Plan 03-05 Task 3 mandated the companion sequence
"companion_hello → ack → companion notification → GCM bundle authentication → verified
success". None of it is reachable:

- `SendHelloAckOnHelloAsync` (line 648) parks on `attempt.HelloSignal.Task` (await at
  line 652). `HelloSignal` is declared at line 1571 and **no code path anywhere in the
  repository ever calls `TrySetResult` on it** (repo-wide grep: only the declaration and
  the await). `HelloReceived` (line 1574) is only ever set back to `false` (line 1528),
  and `HelloReplyId` is only ever nulled (line 1529).
- `OnStanzaReceived` (lines 708-785) handles exactly two things: pair-success containers
  and iq ref/error correlation. There is no handler for `CompanionHelloTag` ("companion-
  hello", line 130) or `CompanionFinishTag` ("companion-finish", line 136) — both consts
  are declared and never referenced anywhere.
- `attempt.BundleKey` is derived at lines 421-422 and the only other use in its lifetime
  is zeroization at line 693. It never authenticates anything.

The class documentation (lines 74-77) claims "a companion finish that authenticates
under the live code's bundle key" — that code path does not exist. The waiter therefore
always runs until the watchdog (`WatchdogTimeoutAsync`, line 673) or a terminal event
cancels it, and `BuildCompanionHelloAck` (line 1317) can never fire. Consequences:
either (a) the live server speaks this handshake and link-code pairing can never finish
(it times out), or (b) the server doesn't, and this is shipped dead code carrying a
false security claim. No test exercises it — no test file references
`companion-hello`, `RunCodeCompanion`, or `HelloSignal`.

**Fix:** Either implement the routing in `OnStanzaReceived` — detect `companion-hello`,
complete `HelloSignal` with the stanza id, gate the companion notification on sequence
state, run `LinkCodeCrypto.UnprotectBundle` under the live attempt's `BundleKey`, and
hand the authenticated container to `CompletePairSuccessAsync` (the single write path) —
plus the five plan-mandated tests; or delete the waiter, both tag consts, the bundle-key
derivation, and correct the class/plan documentation so no unimplemented security
property is claimed. Do not ship with the current state either way.

### CR-02: Plan-03-06 durability tests are absent from the repository; the phase gate reports a failure for a test that does not exist

**Severity:** Critical (BLOCKER)
**File:** `tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ResumeTests.cs:246-386` (also `.planning/STATE.md:125`, `.planning/phases/03-pairing-session-durability/03-VALIDATION.md:57,116`)

**Issue:** Plan 03-06's acceptance criterion states: "ResumeTests contains methods with
`kill_restart`, `drop_428`, `drop_515`, and `concurrent` in their names (seed-06 grep
anchors)". `ResumeTests.cs` contains exactly three methods
(`Resume_seeded_credentials_login_opens_after_success`,
`Resume_missing_row_generates_persists_and_builds_registration_from_row`,
`Resume_second_connect_sends_login_payload_again`); repo-wide grep for `kill_restart`,
`drop_428`, `drop_515`, `concurrency_backstop` in `tests/` returns zero matches, and
`git log -S` across all commits shows the `kill_restart_resume_with_drops_and_concurrency`
test was never committed on any branch. The kill/restart resume proof, the 428/515 drop
survival proofs, and the concurrency backstop — the evidence for CONN-03 session
durability — are not in the shipped tree.

Worse, `.planning/STATE.md:125` leaves an OPEN investigation item for exactly that
missing test: "`kill_restart_resume_with_drops_and_concurrency` has timeout on Phase C
(428 drop wait) — separate investigation needed" — a suspected real defect in 428
drop/reconnect handling was observed, then the test that observed it disappeared, and
03-07-VALIDATION still records "409 succeeded, 1 failed out of 410" against a test that
no longer exists (while the preserved run logs `test-full.txt` / `test-output.txt` show
409/409 green — gate evidence is internally inconsistent).

**Fix:** Restore/author the four plan-mandated tests, investigate the Phase-C 428-drop
timeout (fix the product defect if the test exposes one, per plan: "a resume bug found
here is the test doing its job"), then re-record the 03-07 gate so its numbers match the
committed suite. Close the STATE.md open item with evidence.

### CR-03: The sample host is uncommitted, absent from the solution, fails to restore, and cannot compile against the public API

**Severity:** Critical (BLOCKER)
**File:** `samples/Iwasug.WhatsApp.Sdk.PairingSample/Iwasug.WhatsApp.Sdk.PairingSample.csproj:10,14` (also `Program.cs:96,149,152-175`, `Iwasug.WhatsApp.Sdk.sln`)

**Issue:** Plan 03-06 required "Add the project to `Iwasug.WhatsApp.Sdk.sln` so
`dotnet build` on the solution covers it" and "the sample compiles against ONLY the
SDK's public surface". None of that holds at HEAD:

1. **Not in the repository at all:** `git status` shows `?? samples/` — the entire sample
   deliverable is untracked. The solution lists only the SDK, Testing, and Tests
   projects, so the plan's `-warnaserror` solution gate never covered the sample.
2. **Restore fails deterministically (reproduced):** `dotnet build samples/...` →
   `NU1504: Duplicate 'PackageReference' items found ... Nerdbank.GitVersioning`
   (error, TreatWarningsAsErrors). The sample sets `ManagePackageVersionsCentrally=false`
   (csproj line 10) while `Directory.Build.props:29` injects a versionless NB.GV
   reference and `Directory.Packages.props:45` carries the version under CPM — the sample
   opts out of the repo's central-versioning convention and breaks itself.
3. **Even past restore, it cannot compile:** `Program.cs:152-175` switches on `e.State`
   — `PairingStateChangedEventArgs` exposes only `Status` and `Payload`
   (`PairingEvents.cs:83-89`), and `ConnectionState` has only
   `Connecting/Open/Closing/LoggedOut` (`ConnectionState.cs:22-47`), so
   `ConnectionState.Pairing/.Code/.Success/.Timeout/.ClientOutdated` do not exist
   (CS1061/CS0117). `Program.cs:96` news up `InMemoryCredentialStoreFactory` — a type
   that exists nowhere in the repo. `Program.cs:149` calls
   `new PairingCoordinator(accId, storeFactory)` — that ctor is `internal` with ten
   parameters (`PairingCoordinator.cs:185-195`) and `InternalsVisibleTo` covers only the
   tests assembly, violating plan Behavior 5 ("if it needs an InternalsVisibleTo or a
   src edit, the public API is incomplete — surface that as a finding and fix the API").

**Fix:** Commit `samples/`, add it to the solution, remove the CPM override and the
duplicate NB.GV reference (rely on `Directory.Build.props` + `Directory.Packages.props`),
and rewire `Program.cs` to the public surface (`WaConnection.Pairing`, the
`StateChanged` event's `Status`/`Payload` args, `InMemoryStoreFactory`), or extend the
public API deliberately if the sample legitimately needs a construction seam.

### CR-04: Sample FileCredentialStore silently drops three private keys and the platform label — a must-not-lose data-loss bug

**Severity:** Critical (BLOCKER — data loss risk)
**File:** `samples/Iwasug.WhatsApp.Sdk.PairingSample/FileCredentialStore.cs:83-92` (read-back at `:58-68`)

**Issue:** `PutAsync` serializes an anonymous object containing only
`CreatedAt, NoiseKeyPrivate, Device, PairedUser, RegistrationId` — it omits
`IdentityKeyPrivate`, `SigningKeyPrivate`, `AdvSecret`, and `Platform`. A round-trip
therefore deserializes those four as `null` on a record whose members are non-nullable
(`CredentialRecord.cs:32-41`). The store's own XML docs claim the D-05 "must-not-lose"
durability class; in reality a restart loses the registration identity, the device
signing key, and the adv secret — the account cannot resume or verify pair-success and
downstream consumers NRE (e.g. `row.AdvSecret.Clone()` at
`PairingCoordinator.cs:950`, `DeriveX25519Public(row.IdentityKeyPrivate)` at
`HandshakePayloadProvider.cs:247`). The bug survived because `FileCredentialStore` is
never referenced by `Program.cs` and no contract test binds it
(`ScratchCredentialStoreContractTests`/`InMemoryCredentialStoreContractTests` bind only
in-memory/scratch implementations).

**Fix:** Serialize the whole record (`JsonSerializer.Serialize(credentials)` — the type
is the contract), and bind this store to `CredentialStoreContractTests` (which already
asserts every field round-trips, lines 58-80) so a dropped field fails CI. Wire it into
the sample's `--store` mode as the plan intended.

### WR-01: Pair-success numeric fields are persisted without range validation — a negative device index bricks every subsequent login

**Severity:** Warning
**File:** `src/Iwasug.WhatsApp.Sdk/Pairing/PairSuccessVerifier.cs:280-281` (persist at `PairingCoordinator.cs:968-974`)

**Issue:** `Device` and `RegistrationId` are `uint32` on the wire
(`adv.proto:42-44`) and are cast straight to `int` with no domain check, then persisted
(`PairingCoordinator.cs:968-974`). The comment promises a 14-bit registration id, and
`ClientPayloadFactory` enforces domains only on the SEND side:
`Registration` throws `SdkFormatException` for `registrationId > 16383`
(`ClientPayloadFactory.cs:124-127`) and `Login` throws for `device < 0`
(`ClientPayloadFactory.cs:199-201`). A single pair-success carrying
`device >= 2^31` (casts negative) produces a row that makes **every** future handshake
throw — a persistent, self-inflicted connect failure that only clearing the row fixes.
HMAC/signature verification proves the sender holds the adv secret (the phone) or is
the server; it does not prove the values are in-domain. The fail-closed verifier should
reject out-of-domain numerics like it rejects unknown fields.

**Fix:** In `PairSuccessVerifier.Verify`, reject before building the payload:
`(uint)container.Details.RegistrationId > 16383` or `Device` outside its valid index
range → `PairSuccessResult.Rejected(Malformed)`. Add verifier tests for
`uint32.MaxValue` values on both fields.

### WR-02: Watchdog timeout can land between the credential write and the reply — a paired row with no Success event and no server ack

**Severity:** Warning
**File:** `src/Iwasug.WhatsApp.Sdk/Pairing/PairingCoordinator.cs:673-685` vs `:968-989`

**Issue:** `WatchdogTimeoutAsync` calls `Terminate(Timeout)` (line 678), which cancels
`attempt.Cts` (line 1482). If that fires after `PutAsync` (line 974) completes but
before the readback/reply (lines 976-988), the remaining awaits throw
`OperationCanceledException`: the credential row is already committed with
`PairedUser` set, but the host was told `Timeout`, the server never received the
pair-success ack, and `Terminate(Success)` at line 878 no-ops because `Terminal` is
already 1. State, events, and wire then disagree — the next connect quietly logs in as
paired while the host believes pairing failed (and a host retry starts a second attempt
over an already-paired row). The documented ordering ("verify → awaited write →
independent read → reply → Success", lines 914-920) is not actually fail-closed against
this window; the crash-recovery test covers host-cancel-with-blocked-reply but not the
watchdog variant.

**Fix:** Re-check `attempt.Terminal` after the write and before the readback, and on a
terminal-that-isn't-Success after a completed write either finish the reply + emit
Success (the durable outcome already happened) or record an explicit "persisted but
unsent" marker the next attempt reconciles. At minimum, document and test the chosen
semantics.

### WR-03: The public code-pairing flow has zero test coverage — plan 03-05 Task 2 acceptance unmet

**Severity:** Warning
**File:** `src/Iwasug.WhatsApp.Sdk/Pairing/PairingCoordinator.cs:353-587` (tests: `tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/PairingLifecycleTests.cs`)

**Issue:** Grep for `RequestPairingCodeAsync` across `tests/` returns **zero matches**.
Plan 03-05 Task 2 mandated five tests (happy path with injected randomness, single-attempt
rejection across entry points, cancellation-before-any-iq, server-failure-reply reset,
typed validation surfaces) with acceptance criteria "PairingLifecycleTests contains the
cancellation-resets-attempt test and the failure-reply-resets-attempt test (grep-visible
by name)". None exist. The entire request loop — phone validation, link-code request iq,
`TryExtractCodeRef` correlation, the 405 bounded version refetch (lines 563-569), reply
timeout — runs only against the live server, never in CI. Combined with CR-01, the whole
link-code entry path beyond the pure crypto/format units is unproven.

**Fix:** Add the five mandated tests to `PairingLifecycleTests` (the harness pattern —
`OrderingHarness` with `DirectSender` + `OnStanzaReceived` — is already in place and
reusable), then re-run the plan's `<verify>` filter.

### WR-04: Sample runtime flows are nonfunctional even after the compile errors are fixed

**Severity:** Warning
**File:** `samples/Iwasug.WhatsApp.Sdk.PairingSample/Program.cs:99-146,199-244,246-293`

**Issue:**
- `--code` mode (lines 218-244): `phoneNumber` is declared `null` at line 27 and never
  assigned anywhere — `IsValidPhoneNumber(phoneNumber ?? "")` is always false, so
  `--code <digits>` always exits 1 with a phone-number error before any SDK call.
  (The sample's own validator also accepts 6-digit numbers at line 299 while the SDK
  rejects ≤6 — two validators that disagree.)
- `--store <dir>` mode (lines 246-293) and `--self-test` (lines 99-146) use the
  in-memory factory; `storeDir` (line 102) is computed and never used, yet the program
  prints "Store persistence OK - credentials persist to {dir}" — it persists nowhere.
  `FileCredentialStore` is never referenced, so the plan's directory-backed store is
  dead code (and buggy — CR-04).
- QR mode (lines 199-216) fabricates a placeholder payload
  (`{"version":"0.1.0","account":...}`) instead of driving `PairingQrPayload`/
  `StartAsync`, prints "scan with WhatsApp", and exits immediately without waiting for
  any pairing event.

Plan 03-06's `<done>` requires "code/QR/store modes are wired to the public API only"
and self-test/terminal-status behaviors; none of the three modes currently exercises
the SDK.

**Fix:** Implement the five plan behaviors: ask for (or take) a phone number and call
`RequestPairingCodeAsync`; construct the file store over `--store <dir>` for real; build
the QR from the `StateChanged` `Code` payload and await the terminal event before exit.

### WR-05: Sample FileCredentialStore violates its own D-19 failure≠missing contract

**Severity:** Warning
**File:** `samples/Iwasug.WhatsApp.Sdk.PairingSample/FileCredentialStore.cs:58-68,110-125`

**Issue:** `GetAsync` wraps **every** exception (including
`OperationCanceledException` and `IOException`) as "Failed to deserialize credential
row" — a cancellation or disk error is misreported as corruption. `RemoveAsync`'s
catch-all returns `false` for any error, so a permission/IO failure is reported as
"absent row" — exactly the failure≠missing violation its own XML docs (lines 10-12)
forbid and that `CredentialStoreContractTests.FailReads_Get_throws_SdkStoreException`
guards against for other implementations.

**Fix:** Catch only JSON deserialization failures and wrap those; let
`OperationCanceledException` propagate; map `IOException`/`UnauthorizedAccessException`
to `SdkStoreException` with the cause; `RemoveAsync` should throw on real IO errors and
return `false` only when the file genuinely does not exist.

### IN-01: `PairingCodeRequestException` public constructor leaves `Reason` defaulting to `NonDigits`

**Severity:** Info
**File:** `src/Iwasug.WhatsApp.Sdk/Pairing/PairingCodeRequestException.cs:49-55`

**Issue:** The public ctor (line 49) never sets `Reason`, so it defaults to enum value
0 = `NonDigits`. Any exception created through the public surface carries a confidently
wrong reason; hosts switching on `Reason` get misclassified failures. The SDK's own
throws use the internal ctor (PairingCoordinator.cs:361), so the defect only bites
host-constructed instances — but that is precisely what the public ctor exists for.

**Fix:** Make `Reason` nullable (`PairingCodeValidationReason?`) or give the public
ctor a `reason` parameter with a documented `default`; keep the internal ctor as-is.

### IN-02: adv.proto comment misdocuments the HMAC input

**Severity:** Info
**File:** `src/Iwasug.WhatsApp.Sdk/Proto/adv.proto:35-37`

**Issue:** The comment says "the verifier recomputes the HMAC over
`details.ToByteArray()`", but `PairSuccessVerifier` HMACs (and signs over) the **raw
received length-delimited bytes** (`detailsBytes`, lines 238/251/264) — never a
re-serialization. The implemented behavior is the correct one (one canonical byte string
feeds HMAC, account signature, and device signature — re-encoding would introduce a
malleability gap), but the schema comment is wrong, and "serialized deterministically by
this repo's protoc output" invites a future contributor to "fix" the verifier to match
the comment.

**Fix:** Rewrite the comment to state that the verifier authenticates the exact
received field bytes as they appear on the wire (strict-walked for unknown/duplicate
fields first), and that re-encoding is deliberately not used.

### IN-03: NOTICE.md provenance row cites a file that does not exist

**Severity:** Info
**File:** `NOTICE.md:47`

**Issue:** The whatsmeow `pair-code.go` provenance row lists
`src/Iwasug.WhatsApp.Sdk/Pairing/CodeAlphabet.cs` as a derived artifact — there is no
such file; the Crockford alphabet lives in `PairingCodeGenerator.cs`
(`LinkCodeTests.cs:35` also carries its own copy of the constant). The provenance log
is the project's license-compliance record (MPL behavioral-reference discipline); a
dangling artifact path undermines auditability.

**Fix:** Correct the path to `src/Iwasug.WhatsApp.Sdk/Pairing/PairingCodeGenerator.cs`
and grep the remaining NOTICE rows for other non-existent paths.

### IN-04: Untracked execution artifacts litter the working tree, including a duplicate phase directory

**Severity:** Info
**File:** repository root / `.planning/phases/`

**Issue:** `git status` shows untracked `test-full.txt`, `test-lifecycle.txt`,
`test-linkcode.txt`, `test-output.txt`, `test-verify.txt`, `completion_format.md`, plus
a second, untracked phase directory
`.planning/phases/03-Pairing & Session Durability/` (containing a
`03-06-PAIRING-SUMMARY.md` that claims deliverables absent from the repo) alongside the
canonical `03-pairing-session-durability/`. A `git add -A` would commit all of it, and
the duplicate directory invites future work against the wrong plan set.

**Fix:** Delete the root scratch files, reconcile or remove the duplicate phase
directory (keep the canonical kebab-case one), and add a `.gitignore` entry for
root-level `test-*.txt` run logs.

---

_Reviewed: 2026-10-10T11:23:29Z_
_Reviewer: the agent (gsd-code-reviewer)_
_Depth: standard_
