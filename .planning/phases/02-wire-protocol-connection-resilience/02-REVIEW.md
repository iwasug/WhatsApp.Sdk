---
phase: 02-wire-protocol-connection-resilience
reviewed: 2026-10-09T04:38:46Z
depth: standard
files_reviewed: 35
files_reviewed_list:
  - NOTICE.md
  - src/Iwasug.WhatsApp.Sdk.Testing/ComplianceTestBase.cs
  - src/Iwasug.WhatsApp.Sdk.Testing/FaultInjection/FaultingStoreFactory.cs
  - src/Iwasug.WhatsApp.Sdk.Testing/Rules/StoreContractRules.cs
  - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/IdentityStoreContractTests.cs
  - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/PreKeyStoreContractTests.cs
  - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/RecentMessageCacheStoreContractTests.cs
  - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/SafetyStateStoreContractTests.cs
  - src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/SignalSessionStoreContractTests.cs
  - src/Iwasug.WhatsApp.Sdk/Accounts/AccountId.cs
  - src/Iwasug.WhatsApp.Sdk/Codec/BinaryNodeCodec.cs
  - src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs
  - src/Iwasug.WhatsApp.Sdk/Domain/Jid.cs
  - src/Iwasug.WhatsApp.Sdk/Errors/StoreErrorSurface.cs
  - src/Iwasug.WhatsApp.Sdk/Noise/NoiseHandshake.cs
  - src/Iwasug.WhatsApp.Sdk/Proto/handshake.proto
  - src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/Runtime/DurableWriteRetryPolicy.cs
  - src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs
  - src/Iwasug.WhatsApp.Sdk/Transport/WaFrame.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Codec/BinaryNodeDepthTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ConnectionStateTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Connection/LoopbackRoundTripTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Connection/LoopbackTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Core/AccountIdTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Core/JidTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Noise/HandshakeEnvelopeTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Noise/NoiseHandshakeTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/FaultingBindingTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/HostStyleSecondRunTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/ScratchStoreFactory.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Stores/DurableWriteRetryPolicyTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreErrorSurfaceTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Stores/StoreRegistryTests.cs
  - tests/Iwasug.WhatsApp.Sdk.Tests/Transport/LoopbackFrameTransport.cs
findings:
  critical: 2
  warning: 1
  info: 3
  total: 6
status: issues_found
---

# Phase 2: Code Review Report (incremental, since f8f8ef6)

**Reviewed:** 2026-10-09T04:38:46Z
**Depth:** standard
**Files Reviewed:** 35
**Status:** issues_found

## Summary

Incremental wave-scoped review of everything after `f8f8ef6`: plan 02-06 (handshake
envelope field renumber, tags 18/26/34), 02-07 (intro-once framing, bare-protobuf
handshake, shared `WaFrame`, FrameReader-wired pump), 02-08 (decoder depth/list
bounds), plus the `var`→explicit-type style pass (817150d — no findings raised from
style-only changes). The proto renumber is pinned by `HandshakeEnvelopeTests` and
documented in NOTICE.md provenance; the flag-free handshake vs `0x00`-flagged routed
send split matches 02-07-PLAN.md; the `buffered.Clear()`/remainder reshuffle in
`WatchAsync` is memory-safe; the intro magic-byte heuristic requires a ≥5.7 MB
headerless frame to misfire — none of these were raised as findings.

Two critical findings: the 02-08 depth bound covers `ReadNode` but not the recursive
string/JID forms, leaving a process-fatal stack exhaustion reachable through the
public decoder; and the 02-07 pump wiring lets `FrameReader` framing violations escape
`WatchAsync`, which converts a hostile-peer framing violation into an unbounded
408-retry loop while skipping channel teardown. One warning on the handshake B path's
single-receive framing assumption, and three stale-doc info items left behind by
02-07's bare-protobuf conversion.

## Critical Issues

### CR-01: Decoder depth bound bypassed by recursive JID string forms — remote StackOverflowException

**File:** `src/Iwasug.WhatsApp.Sdk/Codec/BinaryNodeCodec.cs:586-656` (bound added at `415-420`)
**Issue:** Commit 951b930 (02-08, "bound binary node decoder depth") added
`MaxNestedDepth = 64` and a `depth` parameter to `ReadNode` — but the depth counter is
never threaded into `ReadString`. `ReadString(JidPair)` → `ReadJidPair()` →
`ReadString(ReadByte())` recurses once per input byte with no depth tracking; the same
applies to `AdJid` (614), `FbJid` (630), and `InteropJid` (638). A crafted payload
such as `[0x00, 0xFA, 0xFA, 0xFA, ...]` (ListEmpty header, then a JidPair tag whose
"user" element is another JidPair, and so on) drives one stack frame per byte — a few
kilobytes exhausts the stack. `Decode` (line 91) and `TryDecode` (line 109) are public;
`TryDecode` catches only `SdkFormatException`, so a `StackOverflowException` —
uncatchable and process-fatal on modern .NET — escapes both. The header read at line
423 (`ReadString(ReadByte())`) executes *before* the tag/emptiness check at line 424,
so the recursion fires before any validation can reject the node. 02-08's stated goal
("bound decoder depth") is therefore incomplete, `BinaryNodeDepthTests` only exercises
`ReadNode` chains, and any future pump wiring that feeds server node bytes into
`Decode` inherits a remote process-kill. Pre-existing recursion, but the reviewed
change was specifically the hardening of this surface.

**Fix:** Thread depth through the string reader, e.g. change the signature to
`ReadString(int tag, int depth)` and have the `JidPair`/`AdJid`/`FbJid`/`InteropJid`
branches (and the `ReadJidPair`/`ReadAdJid`/`ReadFbJid`/`ReadInteropJid` helpers) pass
`depth + 1`, throwing `new SdkFormatException("Encoded node exceeds the maximum
nesting depth.")` once `depth > MaxNestedDepth` — identical to the `ReadNode` guard.
Add a test in `BinaryNodeDepthTests` asserting a ~10 KB JidPair chain throws
`SdkFormatException` instead of crashing the test host.

```csharp
private string ReadString(int tag, int depth = 0)
{
    ...
    case JidPair:
        if (depth >= MaxNestedDepth)
        {
            throw new SdkFormatException("Encoded node exceeds the maximum nesting depth.");
        }
        return ReadJidPair(depth + 1);   // same guard for AdJid/FbJid/InteropJid
    ...
}
```

### CR-02: FrameReader violation escapes WatchAsync — hostile frame becomes unbounded 408-retry loop and skips channel teardown

**File:** `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs:694` (throw site); propagation at `593-607`/`621`, `343`, `365-374`
**Issue:** The 02-07 pump rewiring (`8c73812`) put `FrameReader.TryReadFrame` at
WaConnection.cs:694 *outside* any try/catch. `TryReadFrame` throws
`SdkTransportException` for a zero-length payload (FrameReader.cs:76-79), an invalid
zlib envelope (FrameReader.cs:160-163), or inflated output over 16 MB
(FrameReader.cs:146-149) — all reachable from a single malformed server frame after
handshake. The exception faults the watch task; `await watch` at line 596 (or 621)
rethrows it out of `ConnectAndWatchAsync`, and `MonitorLoopAsync`'s
`catch (Exception ex)` at line 365 logs "Connection loop fault" and sets
`DropOutcome.Retry(408)`. Two consequences:

1. **Fail-closed policy violated.** The same diff applies Rule 2 to message-B framing
   (lines 541-547: framing violation → `ProtocolHaltException`, no retry), and the
   class doc promises "peer-protocol violations halt the loop without retry — a hostile
   peer is not a network flap" (lines 78-79). Here a post-handshake framing violation
   instead takes the retry path, and `ReconnectBackoff` is "unbounded by design" for
   408 (ReconnectPolicy.cs:191-192) — a hostile or drifting server that emits one bad
   frame per connection drives an infinite connect → violate → retry cycle.
2. **Teardown skipped.** Because the exception propagates before line 343,
   `UninstallChannelAsync()` never runs: `_channel` stays installed with its router
   never latched (`NotifyConnectionClosing` skipped, line 789) and its
   transport/noise/router never disposed (lines 790-792). In-flight queries then wait
   the full 60 s `QueryTimeoutMs` instead of failing fast as connection-lost, new
   `SendQueryAsync` calls are written to the old, unpumped socket (guaranteed timeout),
   and each cycle leaks another channel. The next successful attempt overwrites
   `_channel` at line 580 without ever uninstalling the old one.

**Fix:** Wrap the drain loop (lines 694-727) so a framing violation is fail-closed and
teardown still runs:

```csharp
ReadOnlySequence<byte> buffer = new(buffered.WrittenMemory);
try
{
    while (FrameReader.TryReadFrame(ref buffer, out byte[]? payload))
    {
        ... // existing decrypt/deliver body
    }
}
catch (SdkTransportException ex)
{
    _logger?.LogWarning(ex, "Frame framing violation for account {AccountId}.", _accountId.Value);
    throw new ProtocolHaltException(
        new SdkConnectionException("The connection framing failed validation.", ex));
}
```

`ProtocolHaltException` is caught by `MonitorLoopAsync` at line 351 and stops without
retry (though note `StopForProtocolViolation` also never uninstalls the channel — add
an `await UninstallChannelAsync()` there, or in a `finally` around the attempt body at
line 343, so no exit path skips teardown).

## Warnings

### WR-01: Handshake message B assumes one complete frame per single ReceiveAsync — benign re-segmentation permanently halts the connection

**File:** `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs:492-535`
**Issue:** The 02-07 intro-strip path (lines 524-535) does exactly one
`transport.ReceiveAsync` (line 495) and then `WaFrame.Deframe` (line 534), which is
strict in both directions: it throws on truncation *and* on trailing bytes
(WaFrame.cs:43-51). `WebSocketFrameTransport.ReceiveAsync` only reassembles within a
single WebSocket message — if the server delivers the intro as one WS message and the
frame-B body as a second (or splits/trails the payload at all), `Deframe` throws
`SdkTransportException`, which lines 541-547 convert into a permanent
`ProtocolHaltException` with no retry. The pump treats fragmentation as structural
("one socket message may carry multiple frames or a frame fragment — fragmentation is
structural, not an edge case", FrameReader.cs:19-21, Pitfall 3), and this same
connection buffers across receives at line 661-688; the handshake B read does not. A
benign wire-segmentation variation is therefore classified the same as a hostile
protocol violation, killing the account connection until the host intervenes.

**Fix:** Accumulate receives until `WaFrame.Deframe` succeeds: keep a small
`ArrayBufferWriter` (as `WatchAsync` does) and loop `ReceiveAsync`, stripping a
detected intro once, until the buffered body contains a complete frame; only then
throw `ProtocolHaltException` if a complete frame cannot be assembled within the
attempt timeout. At minimum, distinguish "frame not yet complete" (retry the read)
from "frame definitively malformed" (halt) before applying Rule 2.

## Info

### IN-01: WriteMessageA doc carries a dangling pre-02-07 fragment and a stale `<returns>`

**File:** `src/Iwasug.WhatsApp.Sdk/Noise/NoiseHandshake.cs:582-589`
**Issue:** Line 583-584 now correctly says the connection sends the intro once and
wraps the body in the shared frame, but line 585 is a leftover fragment of the old
sentence ("the fresh ephemeral in the clear and the opaque hello alongside it / (XX:
key empty at this point — authenticate only, never encrypt)."), and line 589 still
returns "The message-A frame bytes" although `WriteMessageA` now returns bare
`envelope.ToByteArray()` (line 628) — framing is the caller's job.
**Fix:** Delete line 585-586 (the XX-era fragment) and change the `<returns>` to "The
bare `HandshakeMessage{client_hello}` envelope bytes (unframed)."

### IN-02: ReadMessageA doc still describes the pre-02-07 `WA-header ||` shape and checks it no longer performs

**File:** `src/Iwasug.WhatsApp.Sdk/Noise/NoiseHandshake.cs:1121-1128`
**Issue:** Line 1122 documents message A as `WA-header || HandshakeMessage{client_hello}`,
but after 02-07 the wire shape is a bare envelope (the intro is sent separately by
WaConnection:484; `NoiseHandshakeTests.Writer_output_starts_with_the_client_hello_wire_tag_0x12`
pins tag 18 with "no WA header prepended"). Line 1126 claims `ReadMessageA` throws when "the header or dictionary
version mismatches" — `ParseMessageA` (lines 1384-1414) performs no header or
dictionary-version check at all; that moved to `NoiseHandshake.ValidateIntroHeader`
(918-934).
**Fix:** Reword to "Reads bare `HandshakeMessage{client_hello}` … throws when framing
is inconsistent or a required field is missing or missized" and drop the
header/dictionary-version claim (intro validation lives in `ValidateIntroHeader`).

### IN-03: WriteMessageB doc has a dangling sentence fragment

**File:** `src/Iwasug.WhatsApp.Sdk/Noise/NoiseHandshake.cs:1143-1147`
**Issue:** Lines 1144-1145 describe the bare-envelope shape correctly, but line 1146
("fresh ephemeral in the clear, encrypted static, encrypted hello.") is a leftover
fragment of the pre-02-07 sentence, grammatically detached from the summary.
**Fix:** Fold it into a complete sentence, e.g. "…wraps this body in the shared
three-byte frame after the intro has been sent once; the envelope carries the fresh
ephemeral in the clear, the encrypted static, and the encrypted hello."

---

_Reviewed: 2026-10-09T04:38:46Z_
_Reviewer: the agent (gsd-code-reviewer)_
_Depth: standard_
