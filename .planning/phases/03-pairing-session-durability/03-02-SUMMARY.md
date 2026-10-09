---
phase: 03-pairing-session-durability
plan: 03-02
subsystem: connection
tags: [reconnect-policy, client-outdated, version-resolution, wa-connection, terminal-disconnect]

# Dependency graph
requires:
  - phase: 03-pairing-session-durability (03-01)
    provides: [failure/stream-error dispatch path, StanzaReceived seam, handshake payload provider, loopback drop-injection script]
provides:
  - DisconnectReason.ClientOutdated = 405 with Terminal classification (405 relocation)
  - WaVersionResolver (bundled pin, cached runtime revision, 405 invalidation) + ConnectionOptions.WaVersionOverride
  - Pairing-terminal seam (one DisconnectReason signal per terminal disconnect) with connection-layer routing proof
affects: [03-03 (coordinator subscribes to the seam; registration inputs replace defaults), 03-04 (pair-success path), phase-03 pairing proofs]

# Actuals (#2632) — pairs with the plan's `estimate` to calibrate future estimates.
# Same estimateTokens scale (chars/4 over the realized diff), never a harness token count.
actuals:
  tokens: 17003
  tasks: 3
  commits: 3

# Tech tracking
tech-stack:
  added: []
  patterns: [terminal-seam reason reporting, override-wins version resolution, fake-clock terminal proofs]

key-files:
  created: [src/Iwasug.WhatsApp.Sdk/Connection/WaVersionResolver.cs, tests/Iwasug.WhatsApp.Sdk.Tests/Connection/WaVersionResolverTests.cs, tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ClientOutdatedRoutingTests.cs]
  modified: [src/Iwasug.WhatsApp.Sdk/Connection/DisconnectReason.cs, src/Iwasug.WhatsApp.Sdk/Connection/ReconnectPolicy.cs, src/Iwasug.WhatsApp.Sdk/Connection/ConnectionOptions.cs, src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs, src/Iwasug.WhatsApp.Sdk/Pairing/HandshakePayloadProvider.cs, tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ReconnectPolicyTests.cs, src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt]

key-decisions:
  - "WaVersionOverride is a dotted triple string (primary.secondary.tertiary), validated fail-fast in ValidateOptions — one knob covers tests, loopback, and hosts"
  - "Pairing-terminal seam fires for every Terminal verdict carrying the DisconnectReason cast (not just 405) — uniform reason reporting; retryable codes, 440, and 515 never raise it"
  - "stream:error code lookup checks code-then-reason attributes; a codeless stream:error keeps the fail-closed ProtocolHalt (hostile peer is not a network flap)"
  - "HttpSwJsFetcher enforces HTTPS at construction; GeneratedRegex keeps the resolver AOT-safe"
  - "No NOTICE.md rows: no new upstream file was read — implementation sourced from 03-RESEARCH.md Pattern 4/Pitfall 1 and the plan only"

patterns-established:
  - "Override-wins version resolution: ConnectionOptions override ?? cached runtime revision, proven offline through the substituted fetcher into decoded wire bytes"
  - "Fake-clock terminal proofs: FakeTimeProvider + clock advance + event-count asserts prove zero-reconnect without wall-time sleeps"

requirements-completed: [CONN-01]

# Coverage metadata (#1602) — one entry per shipped deliverable. Drives DETERMINISTIC UAT routing in verify-work.
coverage:
  - id: D1
    description: "405 relocated to Terminal via DisconnectReason.ClientOutdated; all other policy arms unchanged"
    requirement: "CONN-01"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ReconnectPolicyTests.cs (36 tests)"
        status: pass
    human_judgment: false
  - id: D2
    description: "WaVersionResolver with documented bundled pin, 6h single-flight cache, 405 invalidation, pin-on-failure, and override precedence into wire bytes"
    requirement: "CONN-01"
    verification:
      - kind: unit
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Connection/WaVersionResolverTests.cs (14 tests)"
        status: pass
    human_judgment: false
  - id: D3
    description: "Connection-layer 405 teardown (zero reconnects, one ClientOutdated seam signal, pairing-agnostic) with 503 control still retrying"
    requirement: "CONN-01"
    verification:
      - kind: integration
        ref: "tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ClientOutdatedRoutingTests.cs (5 tests)"
        status: pass
    human_judgment: false
  - id: D4
    description: "Full suite green with no cross-plan breakage"
    requirement: "CONN-01"
    verification:
      - kind: integration
        ref: "dotnet test Iwasug.WhatsApp.Sdk.sln (348 tests)"
        status: pass
    human_judgment: false
  - id: D5
    description: "Live-server 405/client_outdated behavior against a real WhatsApp account"
    requirement: "CONN-01"
    verification: []
    human_judgment: true
    rationale: "No live WhatsApp account exists in CI (03-RESEARCH A1/A5); loopback proves the routing, the server half needs the end-of-phase human proof"

# Metrics
duration: 30min
completed: 2026-10-09
status: complete
---

# Phase 03 Plan 02: ClientOutdated Terminal Outcome Summary

**405 stale-client rejections now terminate honestly: ClientOutdated reason → Terminal verdict → zero reconnects + one pairing-terminal seam signal, with a cached runtime version resolver feeding the handshake payload**

## Performance

- **Duration:** 30min
- **Started:** 2026-10-09T20:25:00+07:00
- **Completed:** 2026-10-09T20:55:00+07:00
- **Tasks:** 3
- **Files modified:** 10

## Accomplishments

- Tracer proof green: `DisconnectReason.ClientOutdated = 405` flows through the single `ReconnectPolicy.Decide` switch as `Terminal` at every attempt depth — the silent-405 retry loop (03-RESEARCH.md Pitfall 1) is removed by construction
- `WaVersionResolver` ships the single version home: documented bundled pin `[2, 3000, 1043857760]` (Pattern 4 derivation + A5 status in XML doc), `sw.js` `client_revision` fetch tolerant of minified single/double quotes, 6h single-flight cache, `Invalidate()` for the 405 path, pin-on-failure without cache poisoning — all proven offline (14 tests, zero real HTTP)
- `ConnectionOptions.WaVersionOverride` (dotted triple, fail-fast validated) wins over the resolver value; `HandshakePayloadProvider.CurrentAppVersion` carries override ?? resolver value into both login and registration wire bytes (decoded-payload asserts)
- Connection-layer routing: a 405 drop (transport close, `<failure reason=405>` node) tears down as terminal with zero further reconnect attempts and exactly one `ClientOutdated` signal on the internal `PairingTerminal` seam — pairing-agnostic (fires with no pairing live) — while 503 keeps retrying without raising the seam (5 FakeTimeProvider tests, no wall-time sleeps)
- Full suite 348/348 green (`dotnet build -warnaserror` 0 warnings; no new PackageReference)

## Task Commits

Each task was committed atomically:

1. **Task 1: ClientOutdated reason → terminal reconnect policy (405 relocation)** - `c531e07` (feat)
2. **Task 2: WaVersionResolver — bundled pin, cached revision, 405 invalidation** - `05f41cb` (feat)
3. **Task 3: ClientOutdated routing through the connection** - `508cc03` (feat)

**Plan metadata:** ledger base `ff1d950`, head after `508cc03` (measured `rev-list --count` = 3 task commits)

## Files Created/Modified

- `src/Iwasug.WhatsApp.Sdk/Connection/WaVersionResolver.cs` - Single version home: `WaVersion` triple, `IWaVersionFetcher`/`HttpSwJsFetcher` (HTTPS-enforced), 6h single-flight cached resolver with `Invalidate()` and `ResolveAsync` override precedence
- `src/Iwasug.WhatsApp.Sdk/Connection/DisconnectReason.cs` - `ClientOutdated = 405` member (Baileys `clientOutdated` provenance, terminal + invalidation doc)
- `src/Iwasug.WhatsApp.Sdk/Connection/ReconnectPolicy.cs` - Terminal arm extends with `(int)DisconnectReason.ClientOutdated`; terminal-set docs updated in three places
- `src/Iwasug.WhatsApp.Sdk/Connection/ConnectionOptions.cs` - `WaVersionOverride` dotted-triple knob with fail-fast validation
- `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs` - 405 in `KnownCodes`; coded `stream:error` maps via table (codeless halts); `PairingTerminal` seam + `ReportTerminalDisconnect` (seam + 405 invalidation under the logged-out once-guard); per-attempt version resolution into the payload provider
- `src/Iwasug.WhatsApp.Sdk/Pairing/HandshakePayloadProvider.cs` - Optional `WaVersion` ctor input + settable `CurrentAppVersion`; both payload directions emit it
- `src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt` - `ClientOutdated` member + `WaVersionOverride` get/init entries
- `tests/.../Connection/ReconnectPolicyTests.cs` - 405 relocated: terminal theory + any-depth fact; unknown/huge-attempt theories keep 418/999 and 408/428/500/503
- `tests/.../Connection/WaVersionResolverTests.cs` - 14 offline tests: cache window, single-flight, invalidation, pin fallback, quote-tolerant parse, override precedence + wire flow, override validation
- `tests/.../Connection/ClientOutdatedRoutingTests.cs` - 5 integration tests: 405 stop, seam-once, pairing-agnostic pre-open 405, 503 control, failure-node 405

## Decisions Made

- **Dotted-triple override string.** `WaVersionOverride` is `primary.secondary.tertiary` (not a bare revision) so primary/secondary stay host-overridable and one `TryParseDotted` check validates everything fail-fast in `ValidateOptions`.
- **Seam fires per Terminal verdict, not per 405.** Every `Terminal` verdict raises `PairingTerminal` once with the `(DisconnectReason)` cast of the code — uniform reason reporting for the future coordinator; retryable codes, `SurfaceAndStop` (440), and restarts never raise it.
- **`code`-then-`reason` stream-error lookup.** A coded `stream:error` maps through the same table as `<failure>`; a codeless one keeps the fail-closed `ProtocolHaltException` (hostile peer ≠ network flap).
- **HTTPS enforced at fetcher construction; `GeneratedRegex` for the pattern.** T-3-04 mitigations plus AOT safety (`IsAotCompatible=true`).
- **No NOTICE.md rows.** No new upstream file was read for a protocol detail — everything derives from 03-RESEARCH.md Pattern 4/Pitfall 1 (already sourced) and the plan.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing critical] 405 added to WaConnection.KnownCodes**
- **Found during:** Task 3 (405 now classifies terminal, but the unknown-code log is still on the dispatch path)
- **Issue:** Without this, every 405 would log "Unknown disconnect code … bounded retry; possible protocol drift" — factually false for a terminal code and a misleading drift signal
- **Fix:** One-token addition of 405 to `KnownCodes`; the terminal warning still fires
- **Files modified:** `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs`
- **Verification:** Full suite green; 405 tests observe `LoggedOut`, never the unknown-code path
- **Committed in:** `508cc03` (part of task commit)

**2. [Rule 3 - Blocking] Provider seam for the version wire-flow proof**
- **Found during:** Task 2 ("the payload factory accepts the revision as a parameter" is unprovable otherwise)
- **Issue:** `ClientPayloadFactory` methods already take the triple, but nothing connects an override to `HandshakePayloadProvider.GetClientFinish` — the "override reaches the wire" half had no seam
- **Fix:** Optional `WaVersion?` ctor parameter + settable `CurrentAppVersion` (existing 3-arg call sites compile unchanged); both payload directions emit it
- **Files modified:** `src/Iwasug.WhatsApp.Sdk/Pairing/HandshakePayloadProvider.cs`
- **Verification:** `ResolveAsync_override_wins_over_resolver_and_flows_into_handshake_payload` decodes both directions off the wire
- **Committed in:** `05f41cb` (part of task commit)

**3. [Rule 3 - Blocking] Interface-typed factory binding in WaVersionResolverTests**
- **Found during:** Task 2 (build error CS0266)
- **Issue:** `new InMemoryStoreFactory().CreateAsync(...)` binds the session-seam overload — non-session factory overloads are explicit interface implementations
- **Fix:** Bound through `ICredentialStoreFactory`-typed variable (house rule from 03-01)
- **Files modified:** `tests/.../Connection/WaVersionResolverTests.cs`
- **Verification:** `dotnet build -warnaserror` clean
- **Committed in:** `05f41cb` (part of task commit)

**4. [Rule 1 - Bug] Two routing tests deadlocked the peer (caught pre-commit)**
- **Found during:** Task 3 (first `ClientOutdatedRoutingTests` run: 2 failed with `TaskCanceledException` at the 10s wait)
- **Issue:** The seam-only and failure-node tests never sent a client query, so the peer blocked in `ServeOneQueryAsync` and never injected the drop
- **Fix:** Added the one query round-trip each peer script serves before closing/sending the failure node
- **Files modified:** `tests/.../Connection/ClientOutdatedRoutingTests.cs`
- **Verification:** Filtered run 5/5 green, then full suite 348/348
- **Committed in:** `508cc03` (part of task commit)

**5. [Rule 2 - Missing critical] Extra failure-node 405 test (5th test; plan lists 4 behaviors)**
- **Found during:** Task 3 (must-have truth 1 names the failure-dispatch half explicitly)
- **Issue:** The four planned behaviors all drive the transport-close path; the `<failure reason=405>` node path — the documented wire trigger — had no direct proof
- **Fix:** `FailureNode405_after_open_is_terminal_with_seam_signal` sends the failure node through the pump and asserts the same terminal + seam outcome
- **Files modified:** `tests/.../Connection/ClientOutdatedRoutingTests.cs`
- **Verification:** 5/5 green in the filtered run
- **Committed in:** `508cc03` (part of task commit)

---

**Total deviations:** 5 auto-fixed (2 missing-critical, 2 blocking, 1 bug)
**Impact on plan:** All five are necessary consequences of honest wiring (log truthfulness, provable seams, green-first proofs). No scope creep — no new packages, no public-surface additions beyond `ClientOutdated` and `WaVersionOverride` (both tracked in `PublicAPI.Unshipped.txt`).

## Issues Encountered

- **Edit-tool oldString mismatches (2x, caught immediately):** one `HandshakePayloadProvider` ctor edit and one `WaConnection` remarks edit failed on exact-text mismatch (doc wording drifted from memory); re-read the region and re-applied with exact text. No code impact.
- **Stray whitespace-churn edits (caught immediately):** two edits that only touched trailing newlines were superseded by the real insertions in the following edit. Net diff contains no whitespace-only hunks (verified in final `git diff --stat` review).

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Ready: 03-03 (coordinator subscribes to the internal `PairingTerminal` seam and fills it with the richer `PairingStatus` vocabulary; bounded refetch against a persistently-405 fake belongs to 03-03's test files; registration inputs replace `RegistrationDefaults` while `CurrentAppVersion` keeps flowing)
- Watch item: D5 live-405 parity — if the human proof shows the server sending 405 where the loopback shapes differ (e.g. different `stream:error` attribute), `MapStreamErrorCode` gains the attribute in one place
- Resolver cache is per-connection instance (per-account isolation holds); a future multi-account rollout shares nothing across accounts by construction
- No blockers; full suite 348/348 green with `dotnet build -warnaserror` at 0 warnings

## Self-Check: PASSED

- All 3 key files verified present on disk (`WaVersionResolver.cs`, `WaVersionResolverTests.cs`, `ClientOutdatedRoutingTests.cs`); all 3 task commits (`c531e07`, `05f41cb`, `508cc03`) verified in `git log`
- No stub patterns (`TODO|FIXME|placeholder|coming soon|not available`) in the plan diff; no private key material in logs/exceptions/events (resolver handles version numbers only; seam carries the reason enum, never payloads)
- No `.csproj`/package changes in the plan diff (in-box `System.Net.Http` + Abstractions only)
- Threat-register coverage: T-3-04 mitigated (HTTPS-only fetch, numeric-shape validation, pin fallback, no script bytes in logs) and T-3-05 mitigated (single `Decide` terminal arm + zero-reconnect/seam-once proofs); no new threat surface introduced

---
*Phase: 03-pairing-session-durability*
*Completed: 2026-10-09*
