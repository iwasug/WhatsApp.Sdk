---
gsd_state_version: "1.0"
current_phase: 5
current_phase_name: Ban-Safety Layer
current_plan: 4
status: executing
stopped_at: Completed 05-03-PLAN.md
last_updated: "2026-10-14T04:06:07.060Z"
state_head: 610285557cb1b759eb7903b41b00b79948e1d566
progress:
  total_phases: 9
  completed_phases: 4
  total_plans: 30
  completed_plans: 28
  percent: 44
last_activity: 2026-10-11
last_activity_desc: Phase 04 plan 04-01 complete - Signal crypto foundation (KAT gate, durable sessions, prekeys); 30 Signal facts green
---

# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-10-07)

**Core value:** Reliably send and receive messages through a linked WhatsApp account — across many accounts at once, without getting those accounts banned.

**Current focus:** Phase 04 — Signal E2EE & Messaging (executing, wave 1 complete, wave 2 next)

## Current Position

Phase: 5 — Ban-Safety Layer
Current Plan: 4
Total Plans in Phase: 5
Status: Ready to execute

Progress: [████░░░░░░] 44%

## Performance Metrics

**Velocity:**
- Total plans completed: 17
- Average duration: —
- Total execution time: — hours

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 01 | 5 | 5 | — |
| 02 | 8+2 | 10 | — |
| 03 | 7 | 7 | — |
| 04 | 5 | - | - |
| 05 | 0/TBD | Not started | - |
| 06 | 0/TBD | Not started | - |
| 07 | 0/TBD | Not started | - |
| 8 | 0/TBD | Not started | - |
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

### Pending Todos

None yet.

### Blockers/Concerns

- Research flags for planning: Phase 4 (clean-room Signal), Phase 5 (ban-safety), Phase 6 (media crypto) need `--research-phase`; Phase 7 (multi-account at scale) research optional
- No live phone in CI — UAT outcomes documented as NOT VERIFIED (see 03-VALIDATION.md)
- 03-06 test `kill_restart_resume_with_drops_and_concurrency` has timeout on Phase C (428 drop wait) — separate investigation needed

## Session Continuity

Last session: 2026-10-14T04:06:06.817Z
Stopped at: Completed 05-03-PLAN.md
Resume file: None
