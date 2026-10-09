---
phase: 02-wire-protocol-connection-resilience
fixed_at: 2026-10-09T06:44:05Z
review_path: .planning/phases/02-wire-protocol-connection-resilience/02-REVIEW.md
iteration: 1
findings_in_scope: 6
fixed: 6
skipped: 0
status: all_fixed
---

# Phase 02: Code Review Fix Report

**Fixed at:** 2026-10-09T06:44:05Z
**Source review:** `.planning/phases/02-wire-protocol-connection-resilience/02-REVIEW.md`
**Iteration:** 1

**Summary:**
- Findings in scope: 6 (2 critical, 1 warning, 3 info — `fix_scope: all`)
- Fixed: 6
- Skipped: 0

**Verification environment:** all builds and test runs executed in the isolated
git worktree `.claude/worktrees/rf-02-29684-1791526260` on branch
`gsd-reviewfix/02-29684` (created from `master` @ `8e758b4`). The worktree was
fast-forwarded onto `master` at cleanup, so the numbers below are reproducible
from the main checkout. Full suite: **301/301 passing** after all fixes.

## Fixed Issues

### CR-01: Decoder depth bound bypassed by recursive JID string forms — remote StackOverflowException

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Codec/BinaryNodeCodec.cs`, `tests/Iwasug.WhatsApp.Sdk.Tests/Codec/BinaryNodeDepthTests.cs`
**Commit:** `b46b156`
**Applied fix:** Threaded the `depth` counter through `ReadString(int tag, int depth)`
and the `ReadJidPair`/`ReadAdJid`/`ReadFbJid`/`ReadInteropJid` helpers (each nested
JID form recurses with `depth + 1`). `ReadString` now throws
`SdkFormatException("Encoded node exceeds the maximum nesting depth.")` once
`depth > MaxNestedDepth` — identical guard and message to `ReadNode`. Added
`Decode_rejects_a_10kb_jid_pair_string_chain_with_the_nesting_depth_rule` to
`BinaryNodeDepthTests`: a `[0x00, 0xFA × 10_000]` payload (ListEmpty header + a
JidPair whose user element is another JidPair, ~10 KB deep) throws the depth rule
instead of crashing the test host.
**Status:** fixed: requires human verification — logic-bound change; tiers 1–2
plus the new regression test and full suite pass, but the depth-threading semantics
(one frame per JID nesting level, InteropJid's rewind-on-error path included) deserve
a human read.

### CR-02: FrameReader violation escapes WatchAsync — hostile frame becomes unbounded 408-retry loop and skips channel teardown

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs`
**Commit:** `fa5bef8`
**Applied fix (two parts, per the review's Fix section):**
1. Wrapped the `WatchAsync` drain loop (`FrameReader.TryReadFrame`) in
   `try/catch (SdkTransportException)` that logs and rethrows as
   `ProtocolHaltException(new SdkConnectionException("The connection framing
   failed validation.", ex))` — a post-handshake framing violation now halts the
   loop without retry (Rule 2 / fail-closed), matching the message-B path.
2. `MonitorLoopAsync` now runs `UninstallChannelAsync()` in a `finally` around the
   `_reconnectGate.RunAsync` attempt (idempotent null-exchange; teardown failure is
   logged, never allowed to mask the attempt's outcome), so **no** exit path —
   protocol-halt, cancellation, or unexpected fault — skips channel teardown. The
   review's parenthetical (StopForProtocolViolation never uninstalls) is covered by
   this finally-net rather than a call site inside the sync helper.
**Status:** fixed: requires human verification — control-flow change to the
reconnect loop; 72/72 connection tests pass, but the teardown-on-every-exit
invariant should be confirmed by a human reader.

### WR-01: Handshake message B assumes one complete frame per single ReceiveAsync — benign re-segmentation permanently halts the connection

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs`, `src/Iwasug.WhatsApp.Sdk/Transport/WaFrame.cs`
**Commit:** `340a77f`
**Applied fix:** Replaced the single `ReceiveAsync` + strict `Deframe` handshake-B
read with an accumulation loop: a small `ArrayBufferWriter` collects receives; a
detected intro is validated and stripped once (deferring until all 4 intro bytes
have arrived if the intro itself is split); the loop then distinguishes
"frame not yet complete" (retry the read — length prefix present but body
short, or fewer than 3 bytes buffered) from "definitively malformed" (declared
length over `MaxFrameBytes`, or trailing bytes after a complete frame) before
applying Rule 2 — only the latter throws `ProtocolHaltException`. A stream that
ends before a complete frame still recycles as 408 (socket-error class), and the
attempt timeout bounds assembly. `WaFrame.PrefixWidth` widened from `private` to
`internal` so the connection can peek the length prefix without duplicating the
constant.
**Status:** fixed: requires human verification — reworked receive-loop control
flow; 72/72 connection tests pass, but the incomplete-vs-malformed discrimination
should be confirmed by a human reader.

### IN-01: WriteMessageA doc carries a dangling pre-02-07 fragment and a stale `<returns>`

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Noise/NoiseHandshake.cs`
**Commit:** `dee6394`
**Applied fix:** Deleted the XX-era fragment ("the fresh ephemeral in the clear and
the opaque hello alongside it / (XX: key empty at this point — authenticate only,
never encrypt).") and changed `<returns>` to "The bare
`HandshakeMessage{client_hello}` envelope bytes (unframed)."

### IN-02: ReadMessageA doc still describes the pre-02-07 `WA-header ||` shape and checks it no longer performs

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Noise/NoiseHandshake.cs`
**Commit:** `dee6394` (same commit as IN-01/IN-03 — three doc-only edits, one file)
**Applied fix:** Reworded to "Reads bare `HandshakeMessage{client_hello}` …" and
dropped the header/dictionary-version claim; the doc now points at
`NoiseHandshake.ValidateIntroHeader` (outer-class cref — the unqualified cref failed
CS1574 because `ReadMessageA` lives in a nested class) as the edge-validation site.

### IN-03: WriteMessageB doc has a dangling sentence fragment

**Files modified:** `src/Iwasug.WhatsApp.Sdk/Noise/NoiseHandshake.cs`
**Commit:** `dee6394`
**Applied fix:** Folded the fragment into a complete sentence: "…the connection
wraps this body in the shared three-byte frame after the intro has been sent once.
The envelope carries the fresh ephemeral in the clear, the encrypted static, and
the encrypted hello."

## Skipped Issues

None — all findings were fixed.

## Verification record

| Check | Where | Result |
|---|---|---|
| `dotnet build src/Iwasug.WhatsApp.Sdk` | isolated worktree | succeeded, 0 warnings after every fix |
| `BinaryNodeDepthTests` (incl. new CR-01 test) | isolated worktree | 5/5 pass |
| `Connection.*` tests (CR-02, WR-01) | isolated worktree | 72/72 pass after each fix |
| Full test suite (final) | isolated worktree | **301/301 pass** |

No syntax-check tier above applied (C# — no per-file parser available); build +
tests served as tier-2/3 verification for every fix.

---

_Fixed: 2026-10-09T06:44:05Z_
_Fixer: the agent (gsd-code-fixer)_
_Iteration: 1_
