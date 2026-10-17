---
gsd_state_version: "1.0"
current_phase: 09
current_phase_name: Ship
current_plan: 8
status: executing
stopped_at: Completed 09-07-PLAN.md
last_updated: "2026-10-17T07:03:06.760Z"
state_head: 987154961261c94c129214ef6896fa46220b3bad
progress:
  total_phases: 9
  completed_phases: 7
  total_plans: 57
  completed_plans: 54
  percent: 78
last_activity: 2026-10-11
last_activity_desc: Phase 04 plan 04-01 complete - Signal crypto foundation (KAT gate, durable sessions, prekeys); 30 Signal facts green
---

# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-10-14)

**Core value:** Reliably send and receive messages through a linked WhatsApp account — across many accounts at once, without getting those accounts banned.

**Current focus:** Phase 09 — Ship

## Current Position

Phase: 09 (Ship) — EXECUTING
Current Plan: 8
Total Plans in Phase: 9
Status: Ready to execute

Progress: [████████░░] 78%

## Performance Metrics

**Velocity:**
- Total plans completed: 34
- Average duration: —
- Total execution time: — hours

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 01 | 5 | 5 | — |
| 02 | 8+2 | 10 | — |
| 03 | 7 | 7 | — |
| 04 | 5 | - | - |
| 05 | 6 | - | - |
| 06 | 0/TBD | Not started | - |
| 07 | 4 | - | - |
| 08 | 7 | - | - |
| 9 | 0/TBD | Not started | - |

**Recent Trend:**
- Last 5 plans: all 7 phase-3 plans completed in 3 waves + verification
- Trend: Velocity increasing as store seam stabilizes

*Updated after each plan completion*
**Per-Plan Metrics:**

| Plan | Duration | Tasks | Files |
|------|----------|-------|-------|
| Phase 04 P03 | ~31min | 3 tasks | 14 files |
| Phase 04 P04 | 2h46m | 3 tasks | 15 files |
| Phase 04 P04-05 | 3h46m | 3 tasks | 17 files |
| Phase 05 P02 | 1 | 3 tasks | 10 files |
| Phase 05-ban-safety-layer P03 | 1 execution pass | 3 tasks | 17 files |
| Phase 05-ban-safety-layer P04 | 1 execution pass | 3 tasks | 19 files |
| Phase 05-ban-safety-layer P05 | 1 execution pass | 3 tasks | 11 files |
| Phase 05-ban-safety-layer P06 | 1 execution pass | 3 tasks | 4 files |
| Phase 06 P04 | task | 1 tasks | 4 files |
| Phase 7 P02 | 45m | 3 tasks | 3 files |
| Phase 7 P04 | 1 | 3 tasks | 7 files |
| Phase 08 P01 | tracer | 3 tasks | 17 files |
| Phase 8 P02 | 30 | 3 tasks | 2 files |
| Phase 08 P03 | 30m | 3 tasks | 12 files |
| Phase 08 P04 | 08-04 | 1 tasks | 7 files |
| Phase 8 P05 | executed | 3 tasks | 1 files |
| Phase 08 P06 | executed | 4 tasks | 9 files |
| Phase 08 P07 | 20m | 1 tasks | 1 files |
| Phase 09 P01 | 45m | 3 tasks | 7 files |
| Phase 09 P03 | 35m | 3 tasks | 8 files |
| Phase 09 P05 | 8m | 2 tasks | 2 files |
| Phase 09 P06 | 10m | 3 tasks | 3 files |
| Phase 09 P02 | 7min | 2 tasks | 5 files |
| Phase 09 P04 | 7 min | 3 tasks | 4 files |
| Phase 09 P07 | 15 | 3 tasks | 1 files |

## Per-Plan Metrics:

| Plan | Duration | Tasks | Files |
|------|----------|-------|-------|
| Phase 01 P01 | 38 min | 2 tasks | 26 files |
| Phase 01 P03 | 26min | 2 tasks | 9 files |
| Phase 01 P04 | 460m | 3 tasks | 10 files |
| Phase 01 P05 | 14 min | 2 tasks | 3 files |
| Phase 02 P02-07 | 35min | 3 tasks | 8 files |
| Phase 03 03-01 | ~55min | 3 tasks | 24 files |
| Phase 03 03-02 | ~30min | 3 tasks | 8 files |
| Phase 03 03-03 | ~2h15m | 3 tasks | 6 files |
| Phase 03 03-04 | ~24min | 3 tasks | 5 files |
| Phase 03 03-05 | ~24min | 3 tasks | 5 files |
| Phase 03 03-06 | ~1h | 3 tasks + sample | 6 files + sample host |
| Phase 03 03-07 | ~1h | 3 tasks | 3 files |
| Phase 04 04-01 | ~60min | 3 tasks | 15 files |

### Accumulated Context

### Decisions

Decisions are logged in PROJECT.md Key Decisions table.
Recent decisions affecting current work:

- [Roadmap]: Research's 9-phase structure adopted with app-state sync phase dropped (no v1 requirement) — any minimal app-state work discovered during planning folds into the phase that requires it.
- [Roadmap]: BCT-01 + EXT-01..04 combined into Phase 8 (Broadcast & Extended Messaging) to avoid a single-requirement phase; MUL-03 (idiomatic .NET surface) mapped to Phase 7 where the full surface becomes observable.
- [Phase 01]: A4 AccountId validation: case-sensitive ordinal identity, non-empty, no whitespace-only, no control chars, 128-char cap; violations throw SdkFormatException with rule-only messages (no payload values); uniqueness stays with the account registry (D-26)
- [Phase 01]: Repo-wide async convention: Task on all host-implemented store methods, ValueTask<T> only on factory CreateAsync (RESEARCH Open Question 3 resolved)
- [Phase 01]: Contract kit asserts via AwesomeAssertions: xunit.v3.extensibility.core ships FactAttribute only, so a second xunit package was avoided; RESEARCH scopes AwesomeAssertions to kit + internal tests
- [Phase 01]: dotnet test on .NET 10 runs xunit.v3 in Microsoft.Testing.Platform mode selected by repo-root global.json (test.runner); TestingPlatformDotnetTestSupport rejected by SDK
- [Phase 01]: git.allow_default_branch_commits=true set so executor commits can land on this repo's only (default) branch master under branching_strategy=none — the pre-commit assertion's own named override
- [Phase 01]: FaultingStoreFactory: one class, all five factory interfaces; non-session seams are explicit interface impls — bind through interface-typed variables (documented in class XML doc)
- [Phase 01]: StoreContractRules.AssertParallelAccessNoCrossReadsAsync: keyed + keyless overloads with REQUIRED iterations param (RS0026 forbids repeated optional params); callers pass 100
- [Phase 01]: Fault facts pin the store PRIMITIVE contract only — cache FailWrites throws; SDK log-and-continue is Phase 4/5 policy (D-20)
- [Phase 01]: PublicAPI.Unshipped.txt entries are mandatory for every new kit-public symbol incl. [Fact]s on public abstract contract classes (RS0016 under warnings-as-errors); harvested mechanically from build output
- [Phase 01]: D-24 cache: ConcurrentDictionary<AccountId, Lazy<Task<StoreSession>>> with exact-pair fault eviction so failed creations never poison the cache (01-04)
- [Phase 01]: Retry backoff is clamp-AFTER-jitter: raw=Base*2^n, ±30% jitter, then clamp to [50,250]ms — every delay in bounds and growth provable under adversarial jitter (A1, 01-04)
- [Phase 01]: StoreErrorSurface.LogAndWrap (internal) is the single D-21 wrap-and-log shape at host->SDK boundaries; registry factory failures already route through it (01-04)
- [Phase 01]: InMemoryStoreFactory now implements IAsyncDisposable so the fallback factory joins the D-23 disposal cascade (01-04 Rule 3 deviation, anticipated by 01-02 comment)
- [Phase 01]: Phase 01 plan 05: host-style second run (HostStyleSecondRunTests) is the executed proof of success criterion 3 - one ScratchStoreFactory drives all five kit capabilities + fault injection + completeness in one flow, zero reference-impl references (T-01-16)
- [Phase 01]: Phase 01 plan 05: non-session store seams must bind through interface-typed factory variables - concrete factories non-session CreateAsync overloads are explicit interface implementations, var-typing silently binds the session seam
- [Phase 01]: Phase 01 plan 05: 01-VALIDATION.md re-mapped to 12 actual task IDs (plan text 01-01-T3 was a slip for 01-01-T2; plan 01-01 has two tasks); all rows green, wave_0_complete + nyquist_compliant flipped; validate-phase owns the status: validated flip
- [Phase 02]: 02-07: server-shaped B consumed intro-first — 4-byte intro validated/stripped BEFORE WaFrame.Deframe (server shape intro||len||bytes); headerless B accepted; framing violations wrap into ProtocolHaltException fail-closed
- [Phase 02]: 02-07: FrameReader.TryReadFrame is the sole inbound pump parser (flag strip + zlib inflate) before NoiseTransport.Decrypt; hand-rolled length parse and dead WatchAsync_OLD removed — zero production bypass (T-2-15)
- [Phase 02]: 02-07: loopback fakes call production NoiseHandshake.ValidateIntroHeader instead of hardcoded intro byte-equality (ConnectionStateTests.cs committed as documented Rule 2 deviation — outside plan files list)
- [Phase 03]: 03-01 resume spine proven end-to-end with success-gated Open
- [Phase 03]: 03-02 ClientOutdated as honest terminal outcome replaces silent 405 retry loop
- [Phase 03]: 03-03 pairing surface with QR payload rotation and PairingCoordinator
- [Phase 03]: 03-04 pair-success verification with fail-closed HMAC/signature checks
- [Phase 03]: 03-05 pairing code: 5B→8C generator, link-code crypto chain (PBKDF2→AES-CTR→HKDF→AES-GCM)
- [Phase 03]: 03-06 kill/restart resume proof + 428 survival + concurrency backstop + PairingSample host
- [Phase 03]: 03-07 end-of-phase UAT closeout with VALIDATION/Nyquist/provenance
- [Phase 04]: 04-01 Wave-0 byte-layout gate closed via Baileys-pinned signal-kats.json KATs (fixtures win over prose; 04-05 live UAT is the backstop)
- [Phase 04]: 04-01 libsignal-lineage derivation adopted on primary-source evidence (MAC sender‖receiver‖version‖body, DJB ratchet/base, DH-ratchet first chains, root-only X3DH KDF, exact previousCounter)
- [Phase 04]: 04-01 Ed25519 approximates XEdDSA within the NSec-only constraint; explicit signing-public seam, UAT must confirm a real bundle signature
- [Phase 04]: 04-01 decrypt is verify-before-commit at every layer; skipped-key bound (2000) is fail-closed; PendingPreKey marks the still-unsent first message
- [Phase 04]: 04-01 test runner workaround (Application Control blocks fresh exes): build with dotnet build, run via dotnet <test-dll> -class; 4 PairingLifecycleTests failures proven pre-existing via stash check
- [Phase 04]: 04-03: one logical send = one network send; no silent auto-retry - timeout returns the id and drops (host re-call = new send, new id)
- [Phase 04]: 04-03: phash device-set pinning - ack mismatch invalidates directory cache + raises typed DeviceSetChanged, never fails the acked send
- [Phase 04]: 04-03: prekey-readiness floor = PreKeyManager.ReuploadThreshold (5, whatsmeow prekeys.go); single-flight per connection via _prekeysEnsured latch
- [Phase 04]: 04-03: D-20 cache policy log-and-continue - cache-write failure never breaks the decrypt-persist-ack path (D-06: ack only after durable session writes)
- [Phase 04]: Delivery receipts opt-in per chat (off by default); MarkReadAsync sends one wire-exact read receipt per sender, idempotent at the wire level
- [Phase 04]: Retry cap keyed per (sender, message id): wire receipts counts 1-5, typed DecryptionFailed never caps, keys block rides retry #2 with prekey removed before the wire (D-06)
- [Phase 04]: Retry resend serves only the original 1:1 recipient (T-4-12), recent cache is the only resend source (Pitfall 6), all faults degrade to logged no-ops — never a throw through the receive path
- [Phase 04]: Retry keys-block rebuilds a fresh session under pinned-identity enforcement (TOFU pin on first contact; pin conflict refuses the rebuild)
- [Phase 04]: ReplyAsync quotes only from the recent cache: participant = cached sender when present, remoteJid = cached chat always; cache miss/empty text/bad chat reject SdkFormatException before any node — never a silent plain-text downgrade
- [Phase 04]: MessageReceivedEventArgs.QuotedMessageId (additive param) carries the received message's wire id for feeding straight back into ReplyAsync
- [Phase 04]: 04-05: group send = SKDM pairwise fanout (own devices excluded) + one signed skmsg with deterministic phash; sender-key bounds 2000/2000/5; wire 0x33|proto|Ed25519 sig64
- [Phase 04]: 04-05: ReadEventsAsync = bounded channel (capacity 128, FullMode=Wait) over exactly 4 WhatsAppEvent subtypes sharing emit sites with EventHandler twins; connection state stays on WaConnection.StateChanged
- [Phase 04]: 04-05: GroupDirectory participants-for-fanout only (GRPM-01 read-side deferred, named in README risk ledger); sample messaging mode reuses the stored account with --account for multi-account dirs
- [Phase 05]: [Phase 05-03]: Reply-mark semantics follow must_have truth 4 (replies never mark) over Task 2's stray contradicting sentence; exemption covers cap AND mark
- [Phase 05]: [Phase 05-03]: Recipient-contact contract bindings live in tests/.../StoreContract/ per repo convention (InMemory + Scratch); ComplianceTestBase floor raised to seven
- [Phase 05]: [Phase 05-04]: Circuit breaker is time-based gating (fail-fast CircuitOpen refusals, NextProbeAtUtc) — not a waited delay; CircuitOpen refusals emit nothing (transition owns the single AccountRestricted)
- [Phase 05]: [Phase 05-04]: Inverted pacing bounds clamp (Max := Min) in SafetyOptions.Validate; pacing inversion never throws unlike jitter ratios
- [Phase 05]: [Phase 05-04]: Internal WaConnection.SafetyPolicy seam (StoreRegistry set-before-first-use precedent) + NoSafetyDelays loopback pins; public host configuration story stays with 05-05 (SAF-07); FakeTimeProvider never moves backwards (midnight/order proofs walk forward)
- [Phase 05]: [Phase 05-05]: Day safe-threshold stays at 1000 (not the plan's literal 300) so SafeDefaults remain valid per the 05-01 precedent; hour adopts the plan's 600
- [Phase 05]: [Phase 05-05]: Unsafe opt-in requires non-empty DeclaredUnsafeAcknowledgment; existing unsafe test helpers pinned with explicit acknowledgments
- [Phase 05]: [Phase 05-05]: Phase gate closes with the 4 documented pre-existing PairingLifecycleTests environment failures; no new failures, no new packages
- [Phase 05]: [Phase 05-06]: Warm-up ceiling enforced end-to-end (SAF-03 / SC2b closed) — AdmitAsync takes the resolved stage int with zero store writes, counter is per-UTC-day under a shared lock, unpaired sessions share the stage-0 ceiling, over-ceiling refusal reuses RateLimit-to-BudgetExhausted with rule-only messages
- [Phase 06]: Receive-side media event surface wired: public MediaReceived event, downloader/uploader seams, fail-closed dispatcher media branches
- [Phase 7]: Isolation suite gates static state, concurrent two-account runs, and per-account safety independence.
- [Phase 7]: Phase 07-04: tagged manager event fusion, per-account scoped logger, cancellation end-to-end, health snapshot
- [Phase 8]: Broadcast fan-out remains enumeration over gated 1:1 sends with ordinal JID dedup and cancellation pass-through
- [Phase 08]: 08-04 durable outbox pump lives store-backed per account with retry/backoff and dead-letter
- [Phase 8]: PresenceManager remains account-scoped with no static state (Pitfall 6).
- [Phase 8]: SendComposingAsync best-effort swallows node failures except cancellation.
- [Phase 8]: Inbound presence parse failures are swallowed and never block receive.
- [Phase 09]: 09-01: the four PairingLifecycleTests failures were a test-recorder aliasing bug (RecordingRandomness kept live buffers the PairingCoordinator owns and zeroizes on attempt unwind) - repaired by cloning recorded buffers; no assertion weakened, no quarantine
- [Phase 09]: 09-01: packaging switches live in the IsPackable-conditioned group in Directory.Build.targets; PackageProjectUrl is the only .props packaging addition; ContinuousIntegrationBuild stays workflow-time (09-04)
- [Phase 09]: 09-01: verify-pack.ps1 is fail-closed by construction - proven against five seeded negative cases (stray package, missing README, empty dir, missing snupkg, stripped LICENSE) before landing
- [Phase 09]: 09-01: pre-existing MessagingLoopbackTests flake (peer-assignment race at line 873) deferred to deferred-items.md + broken-windows ledger, not repaired - out of scope
- [Phase 09]: 09-03: the probe runner starts Pairing.StartAsync before awaiting ConnectAsync - the verdict vocabulary is the pairing surface, and the attempt loop gates its first send on the channel-ready pulse
- [Phase 09]: 09-03: RED evidence uses MTP's bare --report-xunit-junit flag (record in gitignored TestResults/); the plan verify commands' -x flag is broken under the MTP runner (exit 5 zero-tests) and was dropped (broken-windows ledger id 18)
- [Phase 09]: 09-03: the silence test is green through RED by design - the bounded wait existed from Task 1; scripted 405 -> Drift/10 and ref -> Accepted/0 were the RED targets, both failing with Infra before the pairing start path landed
- [Phase 09]: 09-05: relocation is byte-verbatim - a mechanical block diff (pre-move README vs docs/risk-ledger.md) shows a single hunk: the dangling '(see Compliance below)' cross-reference repointed at the README Compliance section; every disclaimer byte-identical
- [Phase 09]: 09-05: README status claims bounded by REQUIREMENTS/ROADMAP truth - media declared Phase 6 in-progress (4/6 plans) with MED-01 pending; pairing carries the CONN real-phone NOT VERIFIED caveat inline
- [Phase 09]: 09-05: every README markdown link absolute (README packs into both nupkgs; relative links break on nuget.org); clean-dir repack + verify-pack.ps1 re-run after the edit (0.1.355, 2 packages + 2 snupkgs)
- [Phase 09]: 09-06: docs/store-implementation.md links the README honesty section as ../README.md (the plan text's ../../ from docs/ escapes the repo root) - Rule 1 fix; docs/ is not packed so the relative form is correct in-repo
- [Phase 09]: 09-06: tests README's HostStyleSecondRunTests sentence now names the five capabilities it drives (session, identity, pre-key, recent-message cache, safety) - removes the 'all five' reading contradicting the corrected eight-interface count
- [Phase 09]: 09-06: docs/webhook-recipe.md imposes no payload format and marks its queue records as host-side sketches; every SDK surface in the mapping switch is the sample host's exact public API
- [Phase 09]: 09-02: freeze moved 1352 SDK + 181 testing-kit entries byte-for-byte with SHA-256 payload identity proof; LF written (git index canonical form - working-tree CRLF on Unshipped was autocrlf checkout conversion)
- [Phase 09]: 09-02: RS0016 gate proven live by a seeded negative test (delete one Shipped entry -> SDK build fails "not part of the declared public API"), then entry restored byte-identically - fail-closed proof, not inference
- [Phase 09]: 09-02: Phase 7 audit row dated 2026-10-15 and placed between Phase 6 and Phase 8 rows (chronological + phase order); Phase 9 row dated 2026-10-17 after Phase 8; NOTICE row shape follows the Phase 5 model
- [Phase 09]: 09-02: PR checklist boxes left unchecked (plan: checklist stays as-is unless a claim fails); the walk confirmation with evidence is recorded in 09-02-SUMMARY
- [Phase 09]: 09-04: ^refs/tags/v..$ joins master/main in publicReleaseRefSpec so v* tag builds stay public releases (no -g sha suffix) - same change set as release.yml by design
- [Phase 09]: 09-04: ci.yml never puts the no-logo flag on a test invocation (IN-10), carries no result instrumentation (A5), and pins the setup-dotnet cache key to Directory.Packages.props
- [Phase 09]: 09-04: release.yml has exactly one push step with --skip-duplicate; snupkg siblings ride the nupkg push; verify-pack.ps1 gates between pack and the irreversible push
- [Phase 09]: 09-04: live-interop triage switch proven offline against mock exit codes 0/10/20/2/7; first real workflow runs land at 09-07
- [Phase 09]: 09-04: OPS-01/OPS-02 stayed blocked by the shared-ID gate (sibling plans without SUMMARYs); REQUIREMENTS.md marking defers to the last declaring plan

### Pending Todos

None yet.

### Blockers/Concerns

- Research flags for planning: Phase 4 (clean-room Signal), Phase 5 (ban-safety), Phase 6 (media crypto) need `--research-phase`; Phase 7 (multi-account at scale) research optional
- No live phone in CI — UAT outcomes documented as NOT VERIFIED (see 03-VALIDATION.md)
- 03-06 test `kill_restart_resume_with_drops_and_concurrency` has timeout on Phase C (428 drop wait) — separate investigation needed
- GitHub Actions billing lock on iwasug account blocks all CI runs (startup_failure, 'account is locked due to a billing issue'). CI evidence recorded as billing-blocked; A2/A3 verdicts pending lock lift. Resolve at https://github.com/settings/billing.

## Session Continuity

Last session: 2026-10-17T07:03:06.093Z
Stopped at: Completed 09-07-PLAN.md
Resume file: None
