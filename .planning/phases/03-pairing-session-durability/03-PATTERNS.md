# Phase 3: Pairing & Session Durability - Pattern Map

**Mapped:** 2026-10-09
**Files analyzed:** 30 (17 new src, 9 modified src/infra, 12 new/modified test files — grouped below)
**Analogs found:** 29 / 30 (only `WaVersionResolver.cs` has no analog — no HTTP client exists anywhere in `src/`)

All analog paths below were verified git-tracked (`git ls-files -- <path>` non-empty) this session.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/…/Pairing/PairingCoordinator.cs` (new) | controller/coordinator | event-driven (state machine) | `src/…/Connection/WaConnection.cs` | role-match |
| `src/…/Pairing/PairingEvents.cs` (new) | model (event surface) | event-driven | `Connection/WaConnection.cs:13-34` + `Connection/ConnectionState.cs` | role-match |
| `src/…/Pairing/HandshakePayloadProvider.cs` (implied — implements `INoiseHandshakePayloads`) | provider/adapter | transform | `src/…/Noise/NoiseHandshake.cs:19-28` (seam) + `LoopbackRoundTripTests.cs:43-57` (double) | role-match |
| `src/…/Pairing/PairingQrPayload.cs` (new) | utility | transform | `src/…/Transport/WaFrame.cs` | role-match |
| `src/…/Pairing/PairingCodeGenerator.cs` (new) | utility | transform | `src/…/Transport/WaFrame.cs` | role-match |
| `src/…/Pairing/LinkCodeCrypto.cs` (new) | utility (crypto composition) | transform | `src/…/Noise/NoiseHandshake.cs` | role-match |
| `src/…/Pairing/ClientPayloadFactory.cs` (new) | service/factory | transform (protobuf) | `Proto/handshake.proto` provenance + `NoiseHandshake.cs` payload seam | role-match |
| `src/…/Pairing/PairSuccessVerifier.cs` (new) | service (fail-closed gate) | transform | `src/…/Codec/BinaryNode.cs:41-46` (Decode/TryDecode fail-closed convention) + `NoiseTransport` zeroization discipline | role-match |
| `src/…/Pairing/WaVersionResolver.cs` (new) | service | request-response (HTTP) | **none** — no `HttpClient` usage in `src/` | no analog |
| `src/…/Proto/client_payload.proto` (new) | schema | transform | `src/…/Proto/handshake.proto` | exact |
| `src/…/Proto/adv.proto` (new) | schema | transform | `src/…/Proto/handshake.proto` | exact |
| `src/…/Proto/companion_reg.proto` (new) | schema | transform | `src/…/Proto/handshake.proto` | exact |
| `src/…/Stores/ICredentialStore.cs` (+ factory, new) | model/store seam | CRUD (single-row) | `src/…/Stores/ISafetyStateStore.cs` | exact |
| `src/…/Domain/CredentialRecord.cs` (implied, D-04 typed row) | model | CRUD | `src/…/Domain/SafetyState.cs` | exact |
| `src/…/Stores/StoreRegistryOptions.cs` (modify + 6th property) | config | CRUD | same file (existing 5 properties) | exact |
| `src/…/Stores/StoreRegistry.cs` (modify: resolve seam, `StoreSession`) | composition | CRUD | same file `ResolveStoreAsync` block 289-349 | exact |
| `src/…/Stores/InMemory/InMemoryStoreFactory.cs` (modify: credential row) | reference impl | CRUD | same file 441-489 (`SafetyStateRow`) | exact |
| `src/…/Connection/DisconnectReason.cs` (modify: `ClientOutdated = 405`) | enum vocabulary | config | same file (`RestartRequired = 515` doc style, 88-94) | exact |
| `src/…/Connection/ReconnectPolicy.cs` (modify: `405 => Terminal`) | policy (pure) | pure function | same file `Decide` switch 144-150 | exact |
| `src/…/Connection/WaConnection.cs` (modify: Open = `<success>`, remove probe, node dispatch) | controller | event-driven / request-response | same file 640-697 (probe→open) + 932-970 (`Emit`) | exact |
| `src/…/Connection/QueryRouter.cs` (modify: id-attribute correlation — planned deviation, Open Q1) | router | request-response | same file `BuildFrame`/`TryParseFrame` 189-230 | role-match |
| `src/…/Connection/ConnectionOptions.cs` (modify: injectable WA version) | config | request-response | same file + `ValidateOptions` 66-95 | exact |
| `src/…/PublicAPI.Unshipped.txt` (modify) | analyzer gate | n/a | same file | exact |
| `src/…Testing/StoreContract/CredentialStoreContractTests.cs` (new) | test (contract kit) | CRUD | `Testing/StoreContract/SafetyStateStoreContractTests.cs` | exact |
| `src/…Testing/FaultInjection/FaultingStoreFactory.cs` (modify: `credential` param) | test infra | CRUD | same file ctor 78-89 | exact |
| `tests/…/StoreContract/InMemoryCredentialStoreContractTests.cs` + Scratch binding (new) | test | CRUD | `tests/…/StoreContract/InMemorySafetyStateStoreContractTests.cs` | exact |
| `tests/…/Pairing/PairingQrTests.cs`, `LinkCodeTests.cs` (new) | test (unit/KAT) | transform | `tests/…/Noise/NoiseVectorTests.cs` | exact |
| `tests/…/Pairing/PairingLifecycleTests.cs` (new) | test (integration) | event-driven | `tests/…/Connection/LoopbackRoundTripTests.cs` | exact |
| `tests/…/Connection/ResumeTests.cs` (new) | test (integration) | event-driven + store reload | `LoopbackRoundTripTests.cs` + `StoreContract/HostStyleSecondRunTests.cs` | role-match |
| Scripted fake-server pairing peer (test helper, new) | test harness | event-driven | `LoopbackRoundTripTests.cs:138-228` + `tests/…/Transport/LoopbackFrameTransport.cs` | exact |
| `tests/…/Connection/ReconnectPolicyTests.cs` (modify: 405 cases) | test | pure function | same file Theory patterns 19-24, 87-91 | exact |
| `tests/…/TestData/*.json` golden node fixtures (new) | fixture | transform | `tests/…/TestData/noise-xx-aesgcm-vectors.json` | exact |

---

## Pattern Assignments

### `src/…/Stores/ICredentialStore.cs` (store seam, single-row CRUD) — 6th seam

**Analog:** `src/Iwasug.WhatsApp.Sdk/Stores/ISafetyStateStore.cs` (whole file, 74 lines)

Copy the shape verbatim: interface + async factory in one file, single-row semantics, identical doc-decision tags (D-02/D-06/D-19/D-22/D-24/D-25).

**Imports** (lines 1-4):
```csharp
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;

namespace Iwasug.WhatsApp.Sdk.Stores;
```

**Interface + durability contract** (lines 26-54, reworded for credentials):
```csharp
public interface ICredentialStore : IAsyncDisposable
{
    Task<CredentialRecord?> GetAsync(CancellationToken cancellationToken);
    Task PutAsync(CredentialRecord credentials, CancellationToken cancellationToken);
    Task<bool> RemoveAsync(CancellationToken cancellationToken);
}
```

**Factory** (lines 64-73):
```csharp
public interface ICredentialStoreFactory
{
    ValueTask<ICredentialStore> CreateAsync(AccountId accountId, CancellationToken cancellationToken = default);
}
```

**Doc rules to carry over verbatim** (ISafetyStateStore.cs:11-24): "the instance IS the namespace", "the write completes only when an independent subsequent read observes the value; the SDK awaits it before continuing (D-06)", "Failure ≠ missing (D-19): a missing row returns `null`; failures throw", "Removing an absent row is idempotent and returns `false`", exception type `Errors.SdkStoreException`.

**Domain record analog:** `src/…/Domain/SafetyState.cs:14`
```csharp
public sealed record SafetyStateRecord(DateTimeOffset CreatedAt, int WarmupStage);
```
→ `CredentialRecord` follows the same one-line record shape (D-04: typed domain objects, not byte blobs).

---

### `src/…/Stores/StoreRegistryOptions.cs` + `StoreRegistry.cs` + `InMemoryStoreFactory.cs` (modify, 6th seam wiring)

**Analog:** the existing safety-state wiring in the same files.

**Options property** (`StoreRegistryOptions.cs:47-51` — duplicate as `CredentialFactory`):
```csharp
/// <summary>
/// Factory for <see cref="ICredentialStore"/> instances. <c>null</c> = the in-memory
/// reference store fills this capability (D-03).
/// </summary>
public ICredentialStoreFactory? CredentialFactory { get; set; }
```
Note: the class docs say "five factory seams" — update those counts (lines 4-7, 16-18).

**Registry resolution** (`StoreRegistry.cs:333-342` — clone block, add `created.Add(credential.Store)` and a `_fallbackCredential` field next to line 69):
```csharp
ISafetyStateStoreFactory? safetyFactory = _options.SafetyStateFactory;
(ISafetyStateStore Store, bool FromHost) safety = await ResolveStoreAsync(
    safetyFactory is null
        ? null
        : (id, token) => safetyFactory.CreateAsync(id, token),
    _fallbackSafety.CreateAsync,
    "safety-state",
    accountId,
    cancellationToken).ConfigureAwait(false);
created.Add(safety.Store);
```
Also extend `StoreSession` (lines 499-560: ctor param, `FromHost` tuple at 539-543, public property) and the `new StoreSession(...)` call at 344-349.

**In-memory reference impl** (`InMemoryStoreFactory.cs:441-489` — copy as `CredentialRow`/`InMemoryCredentialStore`; add `ICredentialStoreFactory` to the interface list at lines 34-40 and a `CredentialCapability` const next to line 46):
```csharp
private sealed class SafetyStateRow
{
    internal object Gate { get; } = new();
    internal SafetyStateRecord? Value;
}

private sealed class InMemorySafetyStateStore(SafetyStateRow row) : ISafetyStateStore
{
    public Task<SafetyStateRecord?> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (row.Gate)
        {
            return Task.FromResult(row.Value);
        }
    }

    public Task PutAsync(SafetyStateRecord state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Observable to any subsequent read before the task completes (D-06 store-side).
        lock (row.Gate)
        {
            row.Value = state;
        }
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (row.Gate)
        {
            if (row.Value is null) { return Task.FromResult(false); }
            row.Value = null;
            return Task.FromResult(true);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```
Factory entry point pattern (`InMemoryStoreFactory.cs:107-114`): `cancellationToken.ThrowIfCancellationRequested(); ThrowIfDisposed(); GetOrAdd(accountId, CredentialCapability, () => new CredentialRow());`.

---

### `src/…/Connection/ReconnectPolicy.cs` (modify, 405 → Terminal) + `DisconnectReason.cs`

**Analog:** same files.

**The switch to extend** (`ReconnectPolicy.cs:144-150`) — add `405 => ReconnectDecision.Terminal.Instance,`:
```csharp
return statusCode switch
{
    401 or 403 or 411 or 419 => ReconnectDecision.Terminal.Instance,
    515 => ReconnectDecision.RestartImmediate.Instance,
    440 => ReconnectDecision.SurfaceAndStop.Instance,
    _ => new ReconnectDecision.RetryAfter(ComputeDelay(attempt, resolved, Random.Shared.NextDouble())),
};
```
Update the doc summaries at lines 76-79 and 122-130 that enumerate the terminal set.

**Enum member style** (`DisconnectReason.cs:88-94` — model `ClientOutdated = 405` on this):
```csharp
/// <summary>
/// Server demands a fresh session (Baileys <c>restartRequired</c>; also fires right
/// after QR scan in the Baileys flow). <b>Immediate restart:</b> fresh socket +
/// fresh Noise handshake (fresh ephemeral — never reuse <c>e</c>), backoff counter
/// reset.
/// </summary>
RestartRequired = 515,
```
Scope guard to preserve (lines 11-16): "this enum carries the code vocabulary only — the code-to-action table lives in `ReconnectPolicy.Decide`". Numeric values are the wire contract — never renumber.

**Test edits required (planner note):** `tests/…/ReconnectPolicyTests.cs` currently asserts 405 is *unknown-retry* in two places — `[InlineData(405)]` in `Decide_unknown_codes_retry_bounded_for_drift_safety` (lines 87-91) and in `Decide_huge_attempt_counts_stay_bounded_without_overflow` (lines 190-196). Both must move 405 into the Terminal family, next to the existing terminal Theory (lines 19-24):
```csharp
[Theory]
[InlineData(401)]
[InlineData(403)]
[InlineData(411)]
[InlineData(419)]
public void Decide_terminal_codes_return_Terminal_at_any_attempt(int statusCode)
```

---

### `src/…/Connection/WaConnection.cs` (modify: Open = `<success>`, kill the probe, node dispatch)

**Analog:** same file — the block Phase 3 replaces, and the event machinery it must keep.

**Probe block to replace** (`WaConnection.cs:657-697`; `ProbeBody` declared at line 103):
```csharp
Task<int?> watch = WatchAsync(current, monitorToken);
Task<byte[]> probe;
try
{
    probe = router.SendQueryAsync(ProbeBody, attemptToken).AsTask();
}
catch (SdkException)
{
    return await AbortChannelAsync(current).ConfigureAwait(false);
}
// … WhenAny(probe, watch) dance …
Emit(ConnectionState.Open, statusCode: null, _backoff.Attempt);
CompleteFirstOpenOk();
Volatile.Write(ref _lastHandshakeHash, transcriptHash);
```
Phase 3: the "winner" becomes *node dispatch receives `<success>` (after credential write is observed)* instead of the probe reply; the same `Emit`/`CompleteFirstOpenOk`/`_lastHandshakeHash` sequence is kept. The "Stillborn rule" doc (lines 85-91) must be reworded from "liveness probe succeeds" to "`<success>` received + durable write".

**Single-writer event dispatch to reuse** (`WaConnection.cs:932-970`) — this is the house rule for `PairingCoordinator`'s event surface too:
```csharp
private void Emit(ConnectionState state, int? statusCode, int attempt)
{
    _state = state;
    // … LogWarning for Closing/LoggedOut, LogInformation otherwise — account id only …
    EventHandler<ConnectionStateChangedEventArgs>? handlers = StateChanged;
    if (handlers is null) { return; }
    try
    {
        handlers.Invoke(this, new ConnectionStateChangedEventArgs(state, statusCode, attempt));
    }
    catch (Exception ex)
    {
        _logger?.LogError(ex, "Connection event handler failed for account {AccountId}.", _accountId.Value);
    }
}
```

**Event-args shape** (`WaConnection.cs:13-34`) — copy for `PairingCodeEventArgs`:
```csharp
public sealed class ConnectionStateChangedEventArgs : EventArgs
{
    public ConnectionStateChangedEventArgs(ConnectionState state, int? statusCode, int attempt) { … }
    public ConnectionState State { get; }
    public int? StatusCode { get; }
    public int Attempt { get; }
}
```

**Ctor/lifecycle discipline** (`WaConnection.cs:148-169`): default-`AccountId` guard throwing `SdkFormatException`, `ArgumentNullException` for each required dependency, `ConnectionOptions.ValidateOptions(options)`, `timeProvider ?? TimeProvider.System`, sole-log-site rule (doc lines 50-53).

---

### `src/…/Pairing/PairingCoordinator.cs` + `PairingEvents.cs` (coordinator, event-driven)

**Analog:** `WaConnection` (event surface + lifecycle) and `ConnectionState.cs` (scope-guard vocabulary doc).

**Scope-guard doc pattern for the NEW 5-value pairing vocabulary** (`ConnectionState.cs:16-21` — do NOT add pairing states to `ConnectionState`, its transition rule at lines 10-14 is locked):
```csharp
/// <para>
/// Scope guard (Jid-style): this enum carries the state vocabulary only — backoff
/// attempt depth stays in the <c>Closing</c> event payload and logs, never as extra
/// public states.
/// </para>
```

**Event handler contract to copy** (`WaConnection.cs:127-133`):
```csharp
/// <summary>
/// Raised on every state transition, from the connection's single-writer loop.
/// Handlers must return quickly (copy to your own queue — Channels precedent) and
/// must not call <see cref="DisposeAsync"/> (it fails fast instead of deadlocking).
/// Handler exceptions are logged and swallowed.
/// </summary>
public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;
```

**Injection point:** the coordinator feeds `INoiseHandshakePayloads` (`Noise/NoiseHandshake.cs:19-28`) — Phase 3 writes its first production implementation:
```csharp
public interface INoiseHandshakePayloads
{
    byte[] GetClientHello();
    byte[] GetClientFinish();
}
```
The test double shows minimal shape (`LoopbackRoundTripTests.cs:43-57`, `StaticPayloads`); production implementation returns `ClientPayload` bytes (registration when no stored credentials, login when present — RESEARCH Code Example 6).

---

### `src/…/Proto/*.proto` (3 new schemas)

**Analog:** `src/…/Proto/handshake.proto` (whole file, 59 lines) — copy the entire structure:

1. **Provenance header** (lines 1-22) — mandatory shape:
```csharp
// Copyright (c) Iwa Sugriwa. Licensed under the MIT License.
//
// PROVENANCE (NOTICE.md "whatsmeow rule" + provenance-recording policy):
//   - This schema is HAND-WRITTEN for this repository. No bytes were copied
//     from whatsmeow (MPL-2.0) or from any other upstream .proto file.
//   - Field roles … were learned read-only from the MIT-licensed Baileys sources
//     at commit 0af2386: … No whatsmeow bytes were involved.
```
2. Syntax/package/namespace (lines 23-27): `syntax = "proto3";` / `package wa.handshake;` / `option csharp_namespace = "Iwasug.WhatsApp.Sdk.Noise";` → new files use their own package (e.g. `wa.pairing`) and `csharp_namespace = "Iwasug.WhatsApp.Sdk.Pairing"`.
3. Field numbers on the wire ARE the contract (handshake.proto uses tags 18/26/34 per Baileys) — same discipline for `ClientPayload`.

---

### `src/…/Pairing/LinkCodeCrypto.cs` (crypto composition, transform)

**Analog:** `src/…/Noise/NoiseHandshake.cs` (composition-over-primitives discipline) — doc pattern lines 36-59:
```csharp
/// <para>
/// Provenance: composed from the public Noise spec (…) and the
/// MIT-licensed Baileys handshake mechanics read-only in 02-SPIKE-NOTES.md.
/// … crypto correctness is imported, never self-generated (T-2-05).
/// </para>
/// <para>
/// Key agreement uses NSec typed keys (F6 call shape); the Diffie-Hellman
/// composition here is protocol logic, never primitive math (STACK.md rule).
/// Single-owner disposable: <see cref="Dispose"/> zeroizes …
/// </para>
```
Secret-zeroization call shape (used throughout `NoiseHandshake.cs`/`NoiseTransport.cs`):
```csharp
CryptographicOperations.ZeroMemory(buffer);   // NoiseHandshake.cs:123-140
```
Static-validation style for byte layout (`Transport/WaFrame.cs:15-54`): bounds-check first, throw a typed `Sdk*Exception` naming the violated rule, never embedding the bytes in the message.

---

### `src/…/Pairing/PairingQrPayload.cs` + `PairingCodeGenerator.cs` (pure transform utilities)

**Analog:** `src/…/Transport/WaFrame.cs` (whole file, 55 lines) — the house pattern for pure static helpers:
- `internal static class`, `internal const` widths/alphabet at top
- validate → throw typed exception (`SdkTransportException` here; pairing uses `SdkFormatException`) naming the rule, never the payload
- `Span`/`ReadOnlySpan` parameters, no allocations beyond the result
- no logging inside the utility (connection/coordinator is the sole log site)

QR payload string + 60s/20s rotation timer semantics come from RESEARCH Code Example 1; rotation timing is tested with `FakeTimeProvider` (test pattern: `ReconnectPolicyTests.cs:220-235`).

---

### `src/…/Pairing/PairSuccessVerifier.cs` (fail-closed verification gate)

**Analog:** fail-closed parse convention documented in `Codec/BinaryNode.cs:41-46` + zeroization discipline in `NoiseTransport.cs:87-88`:
```csharp
/// <para>
/// Companion convention (Jid.TryParse precedent):
/// <c>BinaryNodeCodec.Decode</c> throws <see cref="SdkFormatException"/> naming
/// the violated rule (never embedding node bytes), and
/// <c>BinaryNodeCodec.TryDecode</c> catches it — … the shape never grows a
/// second parsing entry point.
/// </para>
```
Ordering contract (RESEARCH Pattern 3 / Pitfall 6): `verify → persist (awaited) → reply → emit success`; any mismatch → pair-error + disconnect, **no** `success` event. Comparison must be `CryptographicOperations.FixedTimeEquals` (RESEARCH Code Example 4).

---

### `src/…/Connection/QueryRouter.cs` (modify: id-attribute correlation — explicit deviation, Open Q1)

**Analog:** same file. The deferral doc to replace (lines 22-25):
```csharp
/// … The skeleton correlation envelope is <c>idLength(u8) || id(ASCII) || body</c> …
/// migrating correlation onto binary-node id attributes waits
/// for real node traffic in Phase 4, when the loopback peer and pump can move
/// together.
```
Reusable internals for the migration: `BuildFrame`/`TryParseFrame` (189-230) move to node-attribute correlation; `TryDeliver` (139-148), `NotifyConnectionClosing` (156-164), `FailAllPending` (171-180) stay as-is. Router stays payload-agnostic — tag dispatch (`<success> <failure> <iq> <notification>`) lives in the `WaConnection` pump, decoding via `Codec/BinaryNodeCodec` into `Codec/BinaryNode` (`BinaryNode.cs:48-69`: `Tag` / `Attrs` / `Children` / `Text` / `Data`).

**Migrate together:** pump + loopback peer helpers `ReceivePeerQueryAsync`/`SendPeerReplyAsync`/`RunPeerEchoLoopAsync` (`LoopbackRoundTripTests.cs:167-228`) + `QueryRouterTests` — the QueryRouter doc itself requires they move as one unit.

---

### `src/…Testing/StoreContract/CredentialStoreContractTests.cs` (contract kit)

**Analog:** `src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/SafetyStateStoreContractTests.cs` (whole file, 184 lines) — the single-row contract. Copy these facts (renamed to credential semantics):

```csharp
public abstract class CredentialStoreContractTests
{
    protected abstract ICredentialStoreFactory CreateFactory();

    [Fact] public async Task Put_then_Get_round_trips_typed_record() { … }        // :36-51
    [Fact] public async Task Get_missing_row_returns_null_not_throw() { … }        // :53-63
    [Fact] public async Task Two_accounts_see_independent_single_rows() { … }      // :65-87
    [Fact] public async Task Double_put_overwrites_single_row_last_write_wins() { … } // :89-103
    [Fact] public async Task Double_remove_second_returns_false() { … }            // :105-118
    [Fact] public async Task FailReads_Get_throws_SdkStoreException_never_returns_null() { … } // :120-132
    [Fact] public async Task Faulting_factory_Mode_None_absent_row_returns_null() { … }        // :134-145
    [Fact] public async Task Write_observed_by_independent_second_store_instance() { … }       // :147-163 (D-06)
    [Fact] public async Task Parallel_interleaved_writes_and_reads_produce_no_cross_reads() { … } // :165-183 (D-11)
}
```
Durability-await + parallel helpers are shared, not re-implemented:
```csharp
await StoreContractRules.AssertWriteVisibleToIndependentReadAsync<ISafetyStateStore>(  // :153-162
    createStore: () => factory.CreateAsync(account, CancellationToken.None),
    writeAsync: writer => writer.PutAsync(new SafetyStateRecord(…), CancellationToken.None),
    assertVisibleAsync: async reader => { … });

await StoreContractRules.AssertParallelAccessNoCrossReadsAsync<ISafetyStateStore>(…)   // :175-182
```

**Fault injection:** `FaultingStoreFactory` ctor (`FaultInjection/FaultingStoreFactory.cs:78-89`) gains a `credential` parameter next to `safetyState`, wired like line 130 (`_safetyState ?? throw NotWired(nameof(ISafetyStateStoreFactory))`).

**Bindings** — exact copy of `tests/…/StoreContract/InMemorySafetyStateStoreContractTests.cs` (14 lines):
```csharp
public sealed class InMemoryCredentialStoreContractTests : CredentialStoreContractTests
{
    protected override ICredentialStoreFactory CreateFactory() => new InMemoryStoreFactory();
}
```
Plus the `Scratch*` binding (see `tests/…/StoreContract/ScratchSafetyStateStoreContractTests.cs` + `ScratchStoreFactory.cs`) and the completeness backstop: `ComplianceTestBase` requires a concrete subclass per contract class (`tests/…/ComplianceTests.cs:5-11`).

---

### `tests/…/Pairing/PairingLifecycleTests.cs` + `tests/…/Connection/ResumeTests.cs` (integration, event-driven)

**Analog:** `tests/Iwasug.WhatsApp.Sdk.Tests/Connection/LoopbackRoundTripTests.cs` (700+ lines) — the scripted fake-server pattern. Reusable harness pieces:

- **Pairing/double seam** (lines 43-57 `StaticPayloads : INoiseHandshakePayloads`; production `HandshakePayloadProvider` replaces it in pairing tests)
- **PairSource transport pairing** (86-98): client factory enqueues the peer transport for the test script
- **Peer handshake script** (138-165): `NoiseHandshakeResponder` drives the responder side over the same production core — "one production implementation speaks both sides"
- **Encrypted send/receive helpers** (167-228): `WaFrame.Deframe` → `noise.Decrypt` → parse → reply via `QueryRouter.BuildFrame` → `noise.Encrypt` → `WaFrame.Frame` (+ 1-byte flag prefix)
- **Event assertion** (230-250):
```csharp
private static async Task WaitForQueuedAsync(
    ConcurrentQueue<ConnectionStateChangedEventArgs> states,
    Func<ConnectionStateChangedEventArgs, bool> predicate,
    string what)
{
    using var timeout = new CancellationTokenSource(WaitTimeout);
    while (!timeout.IsCancellationRequested)
    {
        foreach (ConnectionStateChangedEventArgs state in states)
            if (predicate(state)) { return; }
        await Task.Delay(10, timeout.Token).ConfigureAwait(false);
    }
    throw new TimeoutException($"Timed out waiting for connection event: {what}.");
}
```
- **Lifecycle test shape** (252-292): arrange `PairSource` + event queue → `Task.Run` peer script → `ConnectAsync` → `WaitForQueuedAsync(…, Open)` → assert ordered state sequence + `Attempt` counters → dispose both sides.

Drop-scenario scripts to mirror for pairing states: `Injected428Drop_reconnects…` (:295), `Drop401_after_open_emits_logged_out_exactly_once…` (:357), `Drop515_during_connecting_restarts_immediately…` (:402).

**Restart-resume proof** additionally follows the store-reload mechanism proven by `tests/…/StoreContract/HostStyleSecondRunTests.cs` (fresh factory instance reads what the first wrote — here: new `StoreRegistry`/credential row after process-kill simulation).

---

### `tests/…/Pairing/LinkCodeTests.cs` + `PairingQrTests.cs` (unit/KAT)

**Analog:** `tests/Iwasug.WhatsApp.Sdk.Tests/Noise/NoiseVectorTests.cs` (golden-vector KAT pattern):
- committed fixture under `tests/…/TestData/` (`noise-xx-aesgcm-vectors.json` precedent), JSON records deserialized with `[JsonPropertyName]` (lines 38-55)
- doc block stating fixture provenance ("extracted verbatim from … no values invented, converted, or reordered")
- **output discipline** (lines 29-34): "assertions compare hex strings of ciphertexts/payloads/hashes (public vector data) … — raw key bytes never enter an assertion message, so even a failing run cannot print key material" → apply to PBKDF2/CTR/HKDF KATs and the advSecret
- timer tests (`60s/20s` rotation, `160s` timeout) use `FakeTimeProvider` per `ReconnectPolicyTests.cs:220-235`

---

### `src/…/PublicAPI.Unshipped.txt` (modify — analyzer gate)

**Analog:** same file (`src/Iwasug.WhatsApp.Sdk/PublicAPI.Unshipped.txt:1-40`). Every new public type/member of `Pairing/`, `ICredentialStore`, `DisconnectReason.ClientOutdated`, `ConnectionOptions` additions gets a line, e.g.:
```
Iwasug.WhatsApp.Sdk.Connection.DisconnectReason.ClientOutdated = 405 -> Iwasug.WhatsApp.Sdk.Connection.DisconnectReason
Iwasug.WhatsApp.Sdk.Stores.ICredentialStore
Iwasug.WhatsApp.Sdk.Stores.ICredentialStoreFactory.CreateAsync(...) -> System.Threading.Tasks.ValueTask<...>
```

---

## Shared Patterns

### Error handling / typed exceptions
**Source:** `src/…/Errors/SdkException.cs`, `src/…/Errors/StoreErrorSurface.cs` (wrap-and-log shape referenced by `WaConnection.cs:49-53`)
**Apply to:** all pairing services and the credential seam
- `SdkStoreException` for store failures (D-19: failure ≠ missing — throw, never null)
- `SdkConnectionException` for query/pump failures (`QueryRouter.cs:94,124`)
- `SdkFormatException` naming the violated rule, never embedding bytes (`BinaryNode.cs:41-46`)
- `OperationCanceledException` never wrapped, never retried (`WaConnection.cs:47-48`)

### Logging + redaction
**Source:** `WaConnection.cs:932-951` (sole log site rule, lines 50-53)
**Apply to:** `PairingCoordinator` becomes the sole log site for the pairing vertical (verifier, crypto, QR builder never log). Account id + operation/rule names only — **never** QR payload, pairing code, key material, or node bytes (RESEARCH Pitfall 5; log level discipline: pairing secrets never at DEBUG).

### Validation / options
**Source:** `ConnectionOptions.ValidateOptions` (`ConnectionOptions.cs:66-95`)
**Apply to:** `ConnectionOptions` gains the injectable WA-version seam (Open Q2) with the same fail-fast guard ("throws `ArgumentOutOfRangeException` … before any socket I/O"). Host inputs (E.164 digits-only phone number, ref/platform id) validate fail-closed in the same guard-before-I/O spirit.

### Durability-await (persist-before-continue, D-06)
**Source:** `ISafetyStateStore.cs:20-23` + contract proof `SafetyStateStoreContractTests.cs:147-163`
**Apply to:** every credential mutation in `PairingCoordinator` (pair-success, LID update, registration-id advance, advSecret rotation) and to the `Open` redefinition in `WaConnection` — await the write, confirm via independent read, only then reply/emit `success`.

### Public-surface tracking
**Source:** `src/…/PublicAPI.Unshipped.txt`
**Apply to:** every new public type in `Pairing/`, `Stores/` — analyzer gate runs in `dotnet build Iwasug.WhatsApp.Sdk.sln --nologo`.

### Deterministic time in tests
**Source:** `tests/…/Connection/ReconnectPolicyTests.cs:220-235`, `ConnectionStateTests.cs:580`
**Apply to:** QR rotation timers, pairing timeouts, link-code expiry — `new FakeTimeProvider()` injected via the ctor's `TimeProvider?` parameter (all production ctors accept it: `WaConnection`, `QueryRouter`, `ReconnectBackoff`).

---

## No Analog Found

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| `src/…/Pairing/WaVersionResolver.cs` | service | request-response (HTTP) | No `HttpClient`/`SocketsHttpHandler` usage exists anywhere in `src/` (grep verified) — Phase 2 was WebSocket-only. Planner should fall back to RESEARCH Pattern 4 (fetch `sw.js`, regex `/\\?"client_revision\\?":\s*(\d+)/`, 6h cache, bundled pin fallback `[2, 3000, 1043857760]`, invalidate on 405) + `ConnectionOptions` injection for tests; log-only failure mode per Pitfall "fall back to bundled pin + log loudly". |

## Metadata

**Analog search scope:** `src/Iwasug.WhatsApp.Sdk/**` (Connection, Stores, Noise, Codec, Transport, Proto, Domain, Errors), `src/Iwasug.WhatsApp.Sdk.Testing/**`, `tests/Iwasug.WhatsApp.Sdk.Tests/**`
**Files scanned:** ~30 listed in globs; 14 full reads + 8 targeted greps (no re-reads)
**Tracked-source gate:** every analog path above verified via `git ls-files` (all non-empty; no `.gsd/capabilities` mirrors involved)
**Pattern extraction date:** 2026-10-09
