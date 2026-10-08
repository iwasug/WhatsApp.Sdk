# Phase 2: Wire Protocol & Connection Resilience - Pattern Map

**Mapped:** 2026-10-08
**Files analyzed:** 23 (15 src + 1 proto + 7 test)
**Analogs found:** 21 / 23 (2 partial/no-analog noted)

All analog paths verified git-tracked via `git ls-files` this session.
Conventions source: Phase 1 code is the convention — CONVENTIONS.md is still empty,
so every excerpt below IS the project convention for Phase 2 to follow.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|---|---|---|---|---|
| `src/.../Transport/IFrameTransport.cs` | service (interface seam) | streaming | `src/.../Stores/ISignalSessionStore.cs` (+ factory shape from `StoreRegistryOptions.cs`) | role-match |
| `src/.../Transport/LoopbackFrameTransport.cs` | utility (test fake, ships in test kit or tests/) | streaming | `src/.../Stores/InMemory/InMemoryStoreFactory.cs` + test-kit `FaultingStoreFactory` | role-match |
| `src/.../Transport/WebSocketFrameTransport.cs` | service | streaming | `src/.../Stores/StoreRegistry.cs` (lifecycle/disposal/CT) | role-match |
| `src/.../Transport/FrameReader.cs` | utility (pure parser) | streaming/transform | `src/.../Domain/Jid.cs` (parse-reject discipline) | partial |
| `src/.../Noise/NoiseHandshake.cs` | service (crypto composition) | request-response (3-message exchange) | `src/.../Stores/Runtime/DurableWriteRetryPolicy.cs` (stateful policy class shape) | partial |
| `src/.../Noise/NoiseTransport.cs` | service (crypto composition) | streaming (encrypt/decrypt) | `src/.../Stores/Runtime/DurableWriteRetryPolicy.cs` + `InMemoryStoreFactory.cs` (single-owner disposable, no statics) | partial |
| `src/.../Noise/NoiseProtocol.cs` | config (constants) | transform | `src/.../Accounts/AccountId.cs` (`MaxLength` const pattern) + `Domain/Jid.cs` grammar remarks | role-match |
| `src/.../Codec/BinaryNode.cs` | model (immutable value) | transform | `src/.../Domain/Jid.cs` | exact |
| `src/.../Codec/BinaryNodeCodec.cs` | utility (pure codec) | transform | `src/.../Domain/Jid.cs` (parse/format + `SdkFormatException` discipline) | role-match |
| `src/.../Codec/TokenDictionary.cs` | config (immutable shared table) | transform | `src/.../Domain/Jid.cs` (pure data, no behavior) | role-match |
| `src/.../Connection/WaConnection.cs` | service (lifecycle owner) | event-driven + streaming | `src/.../Stores/StoreRegistry.cs` | exact |
| `src/.../Connection/QueryRouter.cs` | service (correlation map) | request-response | `src/.../Stores/StoreRegistry.cs` (`ConcurrentDictionary` + eviction) | role-match |
| `src/.../Connection/ReconnectPolicy.cs` | utility (pure policy) | event-driven | `src/.../Stores/Runtime/DurableWriteRetryPolicy.cs` | exact |
| `src/.../Connection/DisconnectReason.cs` | model (enum) | event-driven | `src/.../Domain/SignalIdentity.cs` (lean record/enum file shape) / `SdkException.cs` taxonomy file | role-match |
| `src/.../Connection/ConnectionState.cs` | model (enum) | event-driven | same as above | role-match |
| `src/.../Connection/ConnectionOptions.cs` | config (options) | request-response | `src/.../Stores/Runtime/DurableWriteRetryPolicy.cs` (`RetryOptions`) + `Stores/StoreRegistryOptions.cs` | exact |
| `src/.../Proto/handshake.proto` | config (schema) | transform | — (first .proto in repo) | no-analog |
| `src/.../Errors/SdkException.cs` (extend) | model (exception hierarchy) | request-response | itself — add `SdkTransportException` / `SdkConnectionException` subtypes | exact |
| `tests/.../Transport/FrameReaderTests.cs` | test | streaming | `tests/.../Core/JidTests.cs` (parse-reject table) | role-match |
| `tests/.../Noise/NoiseVectorTests.cs` | test | transform | `tests/.../Core/JidTests.cs` (golden round-trip table) | role-match |
| `tests/.../Noise/NoiseHandshakeTests.cs` | test | request-response | `tests/.../StoreContract/FaultingBindingTests.cs` (fake-harness round-trip) | role-match |
| `tests/.../Codec/BinaryNodeRoundTripTests.cs` | test | transform | `tests/.../Core/JidTests.cs` + `AccountIdTests.cs` | exact |
| `tests/.../Connection/QueryRouterTests.cs` | test | request-response | `tests/.../Stores/StoreRegistryTests.cs` (NSubstitute + AwesomeAssertions) | role-match |
| `tests/.../Connection/ReconnectPolicyTests.cs` | test | event-driven | `tests/.../Stores/DurableWriteRetryPolicyTests.cs` | exact |
| `tests/.../Connection/LoopbackTests.cs` | test | streaming | `tests/.../StoreContract/FaultingBindingTests.cs` + `Stores/StoreRegistryTests.cs` | role-match |
| `Directory.Packages.props` (NSec/Protobuf/Grpc.Tools pins) | config | — | itself, lines 13-32 | exact |

---

## Pattern Assignments

### `src/.../Transport/IFrameTransport.cs` (service interface, streaming)

**Analog:** `src/Iwasug.WhatsApp.Sdk/Stores/ISignalSessionStore.cs` (interface seam shape) + `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistryOptions.cs` (async factory seam)

The store seams are the project's only existing interface-seam precedent: async,
`CancellationToken`-first, `ValueTask<T>`-returning, per-account scoped. The transport
seam copies that shape with frames instead of records.

**Imports pattern** (from `Stores/StoreRegistryOptions.cs`, lines 1-2 — note: file-scoped namespace, no usings needed for same-namespace types):
```csharp
namespace Iwasug.WhatsApp.Sdk.Stores;
```

**Core interface pattern** — async factory + CT default + ValueTask (from `Stores/InMemory/InMemoryStoreFactory.cs`, lines 55-60):
```csharp
/// <inheritdoc />
public ValueTask<ISignalSessionStore> CreateAsync(AccountId accountId, CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    ThrowIfDisposed();
```

**What the planner copies:** `IFrameTransport` lives in `namespace Iwasug.WhatsApp.Sdk.Transport;`,
methods take `CancellationToken cancellationToken = default`, return `ValueTask<T>` /
`Task`, first line is `cancellationToken.ThrowIfCancellationRequested();`. Interface
methods carry `<exception cref="SdkStoreException">`-style XML docs for the transport
exception subtypes. CT is end-to-end (connection loop must honor CT now per MSG-07 foreshadow).

---

### `src/.../Transport/LoopbackFrameTransport.cs` (utility fake, streaming)

**Analog:** `src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs` (lines 1-53) + test-kit `FaultingStoreFactory` (exercised in `tests/.../StoreContract/FaultingBindingTests.cs`)

**No-statics + per-account isolation pattern** (`InMemoryStoreFactory.cs`, lines 7-17):
```csharp
/// <summary>
/// In-memory reference implementation filling ALL five factory seams (D-03: one factory
/// fills any gap a host leaves unregistered; STR-02 reference implementation).
/// </summary>
/// <remarks>
/// <para>
/// All state lives under this factory instance in one dictionary keyed by
/// (capability, account) — no global mutable statics (Pitfall 8 / T-01-02). Two accounts
/// never observe each other's data: per-account isolation is structural, and the
/// contract suite proves it (e.g. <c>Two_accounts_isolated_same_address</c>).
/// </para>
```

**Fake-harness test pattern** (`tests/.../StoreContract/FaultingBindingTests.cs`, lines 23-24, 30):
```csharp
private static FaultingStoreFactory CreateWrappedFactory(FaultMode mode = FaultMode.None) =>
    new(signalSession: new InMemoryStoreFactory()) { Mode = mode };
```

**What the planner copies:** `LoopbackFrameTransport` holds its two linked `Pipe`s as
instance fields (never statics); one instance per connection/account. Tests construct it
via a `Create...` helper like `CreateWrappedFactory`. Where it lives: tests/ helper
first; promote to `Iwasug.WhatsApp.Sdk.Testing` only if Phase 3+ needs it (D-10 test-kit
precedent — the kit already ships `FaultingStoreFactory`).

---

### `src/.../Transport/WebSocketFrameTransport.cs` (service, streaming)

**Analog:** `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs` (composition root + disposal cascade)

**Imports pattern** (`StoreRegistry.cs`, lines 1-5):
```csharp
using System.Collections.Concurrent;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Stores.InMemory;
using Microsoft.Extensions.Logging;
```

**Constructor injection pattern** (`StoreRegistry.cs`, lines 87-103):
```csharp
/// <param name="options">Host registration surface — factories per capability, subset allowed (D-03).</param>
/// <param name="logger">Optional logger; D-21 failure logging works when a logger is present and is skipped when absent.</param>
public StoreRegistry(StoreRegistryOptions options, ILogger<StoreRegistry>? logger = null)
{
    _options = options ?? throw new ArgumentNullException(nameof(options));
    _logger = logger;
```

**Disposal-cascade pattern** (`StoreRegistry.cs`, lines 200-266 — flag + snapshot + aggregate):
```csharp
public async ValueTask DisposeAsync()
{
    KeyValuePair<AccountId, Lazy<Task<StoreSession>>>[] snapshot;
    lock (_lifecycleGate)
    {
        Interlocked.Exchange(ref _disposed, 1);
        snapshot = _sessions.ToArray();
    }

    List<Exception>? failures = null;
    foreach (var kvp in snapshot)
    {
        // ... dispose each owned resource, collect into failures ...
    }
    if (failures is not null)
    {
        throw new AggregateException("One or more store sessions failed to dispose.", failures);
    }
}
```

**Error-surfacing pattern** (`StoreRegistry.cs`, lines 392-418):
```csharp
catch (OperationCanceledException)
{
    throw;
}
catch (Exception ex)
{
    throw StoreErrorSurface.LogAndWrap(
        ex,
        _logger,
        $"store factory for capability '{capability}'",
        accountId);
}
```

**What the planner copies:** `WebSocketFrameTransport` takes `(Uri endpoint, ConnectionOptions options, ILogger<WebSocketFrameTransport>? logger = null)`,
null-guards options, nullable logger. `DisposeAsync` sets a disposed flag first,
snapshots owned resources (socket, pump tasks, channels), disposes each with
aggregate-failure semantics. `OperationCanceledException` is never wrapped/retried;
everything else goes through the shared error surface (new transport subtypes — see
Shared Patterns). All awaits use `.ConfigureAwait(false)`.

---

### `src/.../Transport/FrameReader.cs` (utility parser, streaming/transform)

**Analog:** `src/Iwasug.WhatsApp.Sdk/Domain/Jid.cs` — partial match (pure parse function with
reject-discipline; no PipeReader precedent exists in the repo — the Pipelines fill/parse
loop shape comes from RESEARCH.md's MS Learn citation, not from codebase).

**Parse-reject pattern** (`Jid.cs`, lines 55-99):
```csharp
/// <exception cref="SdkFormatException">
/// <paramref name="value"/> is null, empty, ... The message
/// names the violated rule only — the offending value is never embedded (message
/// discipline, T-01-01/T-01-06).
/// </exception>
public static Jid Parse(string value)
{
    if (value is null)
    {
        throw new SdkFormatException("JID value must not be null.");
    }
    // ... each rule = one guard, one rule-naming message ...
```

**Round-trip invariant pattern** (`Jid.cs`, lines 220-233):
```csharp
private static Jid Create(string user, string server, string? agent, int? device, string value)
{
    var jid = new Jid(user, server, agent, device);

    // Round-trip invariant (T-01-06): Parse only accepts inputs whose canonical
    // reconstruction is byte-identical to the input ...
    if (!string.Equals(jid.ToString(), value, StringComparison.Ordinal))
    {
        throw new SdkFormatException("JID value is not in canonical form.");
    }

    return jid;
}
```

**What the planner copies:** `FrameReader` is a static pure parser over `ReadOnlySequence<byte>`
/ `PipeReader` (fill/parse loop per RESEARCH.md). Every malformed input (truncated prefix,
evil length, bad compression flag, token overrun) throws `SdkFormatException` with a
rule-naming message — never embedding frame bytes. Max-frame cap aborts the connection
(V5); the cap itself is a `const` (see `AccountId.MaxLength` pattern below).

---

### `src/.../Noise/NoiseHandshake.cs` + `NoiseTransport.cs` (service, crypto composition)

**Analog:** `src/Iwasug.WhatsApp.Sdk/Stores/Runtime/DurableWriteRetryPolicy.cs` — partial
match (closest stateful-policy class in the repo; no crypto precedent exists yet).

**Primary-constructor + validated-options pattern** (`DurableWriteRetryPolicy.cs`, lines 69-72, 121-131):
```csharp
public sealed class DurableWriteRetryPolicy(RetryOptions? options = null, TimeProvider? timeProvider = null)
{
    private readonly RetryOptions _options = ValidateOptions(options);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
```

```csharp
private static RetryOptions ValidateOptions(RetryOptions? options)
{
    var resolved = options ?? new RetryOptions();
    if (resolved.MaxAttempts < 1)
        throw new ArgumentOutOfRangeException(nameof(options), "MaxAttempts must be at least 1.");
    ...
    return resolved;
}
```

**Cancelable-delay pattern** (`DurableWriteRetryPolicy.cs`, lines 100-106):
```csharp
await Task.Delay(
    TimeSpan.FromMilliseconds(ComputeDelayMs(attempt)),
    _timeProvider,
    cancellationToken).ConfigureAwait(false);
```

**Single-owner disposal precedent** (`Stores/InMemory/InMemoryStoreFactory.cs`, lines 50-53):
```csharp
// 0 = live, 1 = disposed: belt-and-braces (WR-01) so a CreateAsync that
// races factory disposal can never mint a fresh session against cleared
// state. StoreRegistry additionally guards its side of the seam.
private int _disposed;
```

**What the planner copies:** `NoiseHandshake` exposes the explicit 3-message API
(`WriteMessageA` → `ReadMessageB` → `WriteMessageC`) as instance methods on a
single-owner disposable class; constructor takes optional config + `TimeProvider`-style
injectable dependencies with fail-fast validation. `NoiseTransport` owns its
send/recv nonce pair as private instance fields starting at 0, constructed fresh per
handshake, never shared/pooled, zeroized on dispose (Pitfall 4). NSec keys use NSec
typed-key API (no `byte[]` key juggling). RESEARCH.md owns the XX token-flow and
AESGCM details — no codebase analog exists for those.

---

### `src/.../Noise/NoiseProtocol.cs` (config constants, transform)

**Analog:** `src/Iwasug.WhatsApp.Sdk/Accounts/AccountId.cs` (const + XML doc pattern)

**Constant pattern** (`AccountId.cs`, lines 29-30):
```csharp
/// <summary>Maximum length of a host-chosen account id, in characters.</summary>
public const int MaxLength = 128;
```

**What the planner copies:** `internal static class NoiseProtocol` (or `public` if the
spike decides hosts need it — default internal) with XML-doc'd consts:
`ProtocolName = "Noise_XX_25519_AESGCM_SHA256"`, 32-byte null-padded construction,
`WaHeader = [0x57, 0x41, 0x06, 0x03]`, `DictVersion = 3`, `MaxFrameBytes` cap.
Fail closed on dict-version mismatch with a loud log (RESEARCH.md Pitfall table).

---

### `src/.../Codec/BinaryNode.cs` (model, transform) — exact analog

**Analog:** `src/Iwasug.WhatsApp.Sdk/Domain/Jid.cs` (immutable parsed value type, D-16)

**Immutable record pattern** (`Jid.cs`, lines 33-53):
```csharp
public sealed record class Jid
{
    /// <summary>The user part (phone/LID portion) of the JID, without agent or device.</summary>
    public string User { get; }

    /// <summary>The server part of the JID (e.g. <c>s.whatsapp.net</c>).</summary>
    public string Server { get; }

    /// <summary>The optional agent component of the local part, when present.</summary>
    public string? Agent { get; }

    /// <summary>The optional device component of the local part, when present.</summary>
    public int? Device { get; }

    private Jid(string user, string server, string? agent, int? device)
    {
        User = user;
        Server = server;
        Agent = agent;
        Device = device;
    }
```

**Scope-guard remark pattern** (`Jid.cs`, lines 26-31 — copy this habit):
```csharp
/// Scope guard (Pitfall 6 / D-15): this type carries no wire vocabulary — no node trees,
/// message ids, or receipt types. Server-set validation, <c>@lid</c> handling, and deeper
/// device/agent canonicalization arrive with Phases 2/4; Phase 1 ships parse, reject, and
/// round-trip.
```

**What the planner copies:** `public sealed record class BinaryNode` (or `record struct`
if the spike shows copy-cost matters — default `record class` per Jid) with get-only
`Tag` / `Attrs` (`IReadOnlyDictionary<string,string>`) / `Content` (children|string|bytes
union), private ctor, `ToString()` rendering the XML debug view (RESEARCH.md Don't-Hand-Roll
row). Pure data, no behavior. Keep the scope-guard remarks style on every new type.

---

### `src/.../Codec/BinaryNodeCodec.cs` (utility codec, transform)

**Analog:** `src/Iwasug.WhatsApp.Sdk/Domain/Jid.cs` (parse/format pair + `TryParse` shape)

**TryParse companion pattern** (`Jid.cs`, lines 128-146):
```csharp
public static bool TryParse(string value, out Jid? jid)
{
    try
    {
        jid = Parse(value);
        return true;
    }
    catch (SdkFormatException)
    {
        jid = null;
        return false;
    }
}
```

**Canonical `ToString` pattern** (`Jid.cs`, lines 148-161):
```csharp
/// <summary>
/// Reconstructs the canonical JID string: <c>User[.Agent[.Device]]@Server</c>.
/// Round-trips exactly for every value <see cref="Parse"/> accepts.
/// </summary>
public override string ToString()
```

**What the planner copies:** `BinaryNodeCodec` as a static class with
`Encode(BinaryNode) -> byte[]` / `Decode(ReadOnlySpan<byte>) -> BinaryNode` (+ `TryDecode`
companion catching `SdkFormatException`). Malformed input (token overrun, truncated
nibble/hex, unknown JID forms) throws `SdkFormatException` naming the rule, never
embedding node bytes. Token table itself lives in `TokenDictionary` (below), injected or
referenced as immutable shared data — never mutated by the codec.

---

### `src/.../Codec/TokenDictionary.cs` (config table, transform)

**Analog:** `src/Iwasug.WhatsApp.Sdk/Domain/Jid.cs` (pure data file, no behavior) +
provenance-header requirement (RESEARCH.md Pitfall 6).

**What the planner copies:** `internal static class TokenDictionary` holding the
Baileys-ported token array as a `static readonly string[]` / `IReadOnlyList<string>`
(immutable shared data — safe for concurrent connections, per RESEARCH.md Anti-Patterns).
File header carries a provenance comment: source (Baileys MIT file + commit), NOTICE.md
attribution, and the explicit "never ported from whatsmeow (MPL-2.0)" guard. No logic in
this file — lookup helpers (index→token with bounds check throwing `SdkFormatException`)
may live here or on the codec; planner's call, but bounds checks are mandatory (V5).

---

### `src/.../Connection/WaConnection.cs` (service lifecycle owner, event-driven) — exact analog

**Analog:** `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs` — the per-account lifecycle
owner precedent (cache, lifecycle gate, disposal cascade, D-21 logging).

**Per-account session-cache pattern** (`StoreRegistry.cs`, lines 71-85):
```csharp
// D-24 session cache. Lazy<Task<T>> guarantees creation runs exactly once per account
// under parallel access (the Lazy memoizes its factory); a faulted creation is evicted
// so a later caller can retry instead of observing a poisoned cache.
private readonly ConcurrentDictionary<AccountId, Lazy<Task<StoreSession>>> _sessions = new();

// 0 = live, 1 = disposing/disposed (WR-03): CreateSessionAsync honors it,
// DisposeAsync sets it first so post-dispose use throws ObjectDisposedException
// and no new session can be registered after the shutdown cascade begins.
private int _disposed;

// Serializes "check flag → GetOrAdd" in CreateSessionAsync against "set flag
// → snapshot" in DisposeAsync so a preempted registration can never insert a
// live session after the shutdown drain completes (WR-01). Contention is a
// once-per-account call path, so a plain lock is sufficient.
private readonly object _lifecycleGate = new();
```

**Connect entry-point shape** (`StoreRegistry.cs`, lines 105-120):
```csharp
/// <param name="accountId">Opaque account key (D-25) — must come from <see cref="Accounts.AccountId.Create"/>.</param>
/// <param name="cancellationToken">Cancellation token (applies to the creating call; the cached session is shared afterwards).</param>
/// <returns>The account's resolved store session.</returns>
/// <exception cref="SdkFormatException"><paramref name="accountId"/> is the default (uninitialized) value.</exception>
/// <exception cref="SdkStoreException">A host or fallback factory failed to create a store (D-21: logged when a logger is present; the original exception is preserved as the inner exception).</exception>
public ValueTask<StoreSession> CreateSessionAsync(AccountId accountId, CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    ThrowIfDefaultAccountId(accountId);
```

**Default-value guard pattern** (`StoreRegistry.cs`, lines 458-465):
```csharp
private static void ThrowIfDefaultAccountId(AccountId accountId)
{
    if (accountId.Value is null)
    {
        throw new SdkFormatException(
            "AccountId must be created via AccountId.Create — the default (uninitialized) AccountId is not valid.");
    }
}
```

**What the planner copies:** `WaConnection` is constructed per `AccountId`
(`new WaConnection(accountId, options, transportFactory, logger)` — no statics, no
shared mutable state across accounts). `ConnectAsync(CancellationToken)` mirrors
`CreateSessionAsync`: CT first line, default-`AccountId` guard, lifecycle-gate against
`DisposeAsync`. Owns transport + handshake + router + reconnect loop; the store seam is
touched only for ephemeral-key read/write (minimal Phase 2 proof). State transitions go
through a single-writer dispatch with an `Interlocked` exactly-once guard for terminal
states (RESEARCH.md Pitfall 7 — no codebase precedent, implement per research).

---

### `src/.../Connection/QueryRouter.cs` (service, request-response)

**Analog:** `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs` (concurrent map + eviction) +
`DurableWriteRetryPolicy.ExecuteAsync` (CT-carrying async operation shape).

**Exact-pair eviction pattern** (`StoreRegistry.cs`, lines 420-425):
```csharp
private void Evict(AccountId accountId, Lazy<Task<StoreSession>> lazy)
{
    // Exact-pair removal: only the entry this session owns is evicted, so a
    // concurrently re-created entry for the same account is never dropped.
    _sessions.TryRemove(new KeyValuePair<AccountId, Lazy<Task<StoreSession>>>(accountId, lazy));
}
```

**Faulted-entry eviction on await path** (`StoreRegistry.cs`, lines 427-450):
```csharp
catch
{
    // A faulted creation must not poison the cache (D-24): evict so a later
    // caller can retry with fresh store instances.
    Evict(accountId, lazy);
    throw;
}
```

**What the planner copies:** `ConcurrentDictionary<string, TaskCompletionSource<BinaryNode>>`
keyed by iq `id` (64-bit monotonic counter, never reused while its window is open —
RESEARCH.md Pitfall 5). `SendQueryAsync(node, CT)` registers the TCS, enforces the 60s
timeout via `TimeProvider` (testable, see `DurableWriteRetryPolicy` ctor pattern), removes
the TCS on timeout AND discards late arrivals with a debug log. Timeout surfaces as a
typed exception (new `SdkTimeoutException`? or transport subtype — planner locks the
name; must derive from `SdkException`). Outbound fails fast when connection is `closing`.

---

### `src/.../Connection/ReconnectPolicy.cs` (utility policy, event-driven) — exact analog

**Analog:** `src/Iwasug.WhatsApp.Sdk/Stores/Runtime/DurableWriteRetryPolicy.cs` in full.

**Retryability-discriminator pattern** (`DurableWriteRetryPolicy.cs`, lines 114-115):
```csharp
private static bool IsRetryable(Exception ex)
    => ex is not OperationCanceledException && ex is not SdkException;
```

**Clamp-after-jitter backoff pattern** (`DurableWriteRetryPolicy.cs`, lines 133-141):
```csharp
// Clamp-AFTER-jitter (A1): raw = BaseDelayMs * 2^attemptIndex, ±30% jitter, then clamp
// to [BaseDelayMs, MaxDelayMs] — observed delays never leave the configured bounds and
// attempt N's maximum possible delay stays below attempt N+1's minimum.
private int ComputeDelayMs(int attemptIndex)
{
    var raw = (double)_options.BaseDelayMs * Math.Pow(2, attemptIndex);
    var jitter = (Random.Shared.NextDouble() * 0.6) - 0.3;
    return (int)Math.Clamp(raw * (1.0 + jitter), _options.BaseDelayMs, _options.MaxDelayMs);
}
```

**Exhaustion-message pattern** (`DurableWriteRetryPolicy.cs`, lines 109-111):
```csharp
throw new SdkStoreException(
    $"durable store write failed after {_options.MaxAttempts} attempts.",
    last);
```

**What the planner copies:** `ReconnectPolicy.Decide(int code)` pure function returning
`Terminal | RetryAfter | RestartImmediate | SurfaceAndStop` per the RESEARCH.md taxonomy
table (401/403/411 terminal; 408/428/500/503 jittered retry; 515 immediate restart;
440 surface-and-stop; unknown bounded retry + loud log). Delay math reuses the
clamp-after-jitter formula but with FULL jitter (`Random(0, min(cap, base·2ⁿ))` per AWS
canonical — RESEARCH.md Pattern 4) and reconnect bounds (base 1s, cap 60s starting point).
Constructor takes `(ReconnectOptions? options = null, TimeProvider? timeProvider = null)`
exactly like `DurableWriteRetryPolicy`. Attempt counter resets on `open` and on 515.

---

### `src/.../Connection/DisconnectReason.cs` + `ConnectionState.cs` (models, event-driven)

**Analog:** `src/Iwasug.WhatsApp.Sdk/Domain/SignalIdentity.cs` (lean single-type file) +
`src/Iwasug.WhatsApp.Sdk/Errors/SdkException.cs` (taxonomy file with doc-per-member).

**Lean type-file pattern** (`Domain/SignalIdentity.cs`, lines 1-18 — whole file is 18 lines):
```csharp
namespace Iwasug.WhatsApp.Sdk.Domain;

---
/// <param name="IdentityKeyPublic">
/// The peer's public identity key in its SDK-owned encoding, or <c>null</c> until real
/// key material arrives in Phase 4. ...
/// </param>
public sealed record SignalIdentityRecord(string? IdentityKeyPublic);
```

**What the planner copies:** One public type per file under
`namespace Iwasug.WhatsApp.Sdk.Connection;`. `DisconnectReason` enum members carry the
Baileys name + Phase 2 behavior in XML docs (401 loggedOut/terminal, 408 connectionLost/retry,
428 connectionClosed/retry, 440 connectionReplaced/surface-stop, 411 multideviceMismatch/terminal,
515 restartRequired/immediate, 403 forbidden/terminal, 500 badSession/retry, 503 retry-longer-base).
`ConnectionState` enum has exactly `Connecting, Open, Closing, LoggedOut` with the
transition rule in remarks (`Connecting → Open → Closing → (Connecting | LoggedOut)`;
`LoggedOut` terminal, exactly-once).

---

### `src/.../Connection/ConnectionOptions.cs` (config, request-response) — exact analog

**Analog:** `src/Iwasug.WhatsApp.Sdk/Stores/Runtime/DurableWriteRetryPolicy.cs` (`RetryOptions`, lines 5-29) + `Stores/StoreRegistryOptions.cs` (options-object remarks).

**Options-record pattern** (`DurableWriteRetryPolicy.cs`, lines 11-29):
```csharp
public sealed class RetryOptions
{
    /// <summary>
    /// Total attempts including the first try (3 = first try + 2 retries). Bounded by
    /// design (D-18): the caller observes a typed failure after this many attempts.
    /// </summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>
    /// Delay before the first retry, in milliseconds; also the floor of the clamp window
    /// applied to every jittered delay.
    /// </summary>
    public int BaseDelayMs { get; init; } = 50;

    /// <summary>
    /// Ceiling of the clamp window, in milliseconds: no single retry delay ever exceeds it.
    /// </summary>
    public int MaxDelayMs { get; init; } = 250;
}
```

**Plain-options remark pattern** (`StoreRegistryOptions.cs`, lines 9-14):
```csharp
/// This is a plain options object: <see cref="StoreRegistry"/> receives it directly.
/// <c>IOptions&lt;T&gt;</c> binding arrives with Phase 7's DI surface (MUL-03) — the
/// registration *shape* is fixed here so hosts write it once.
```

**What the planner copies:** `public sealed class ConnectionOptions` with `init` props +
safe defaults in code: `ConnectTimeoutMs = 20_000`, `QueryTimeoutMs = 60_000`,
`KeepAliveIntervalMs = 30_000`, `RetryBaseDelayMs = 1_000`, `RetryMaxDelayMs = 60_000`
(RESEARCH.md §Disconnect table). Same fail-fast `ValidateOptions` guard as
`DurableWriteRetryPolicy` (throw `ArgumentOutOfRangeException` before any I/O).

---

### `src/.../Proto/handshake.proto` (schema, transform) — no analog

No `.proto` file exists in the repo — this is the first. Conventions the planner must
impose (from RESEARCH.md Pitfall 6 + STACK.md, not from codebase):
- Hand-written minimal schema from Baileys MIT TS types; provenance header comment in
  the file (source, license, "not derived from whatsmeow MPL-2.0").
- `Grpc.Tools` 2.84.0 with `PrivateAssets=all` (build-time only); runtime
  `Google.Protobuf` 3.36.2 — never the 4.0.0-rc prereleases.
- New `Protobuf` ItemGroup in the SDK csproj; versions go in `Directory.Packages.props`
  (central management is repo law — see `Directory.Packages.props` lines 9-11).

---

### `src/.../Errors/SdkException.cs` extension (model — extend, don't create)

**Analog:** itself — `src/Iwasug.WhatsApp.Sdk/Errors/SdkException.cs` (lines 14-55).

**Subtype pattern** (`SdkException.cs`, lines 31-40):
```csharp
public sealed class SdkStoreException : SdkException
{
    /// <summary>Initializes a new instance of the <see cref="SdkStoreException"/> class.</summary>
    /// <param name="message">Operation description — no store payload values.</param>
    /// <param name="inner">The underlying store error, when available.</param>
    public SdkStoreException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
```

**What the planner copies:** Add `SdkTransportException` (frame/WS failures, D-17/D-21
extension) and `SdkConnectionException` (disconnect-surfaced failures incl. 401/411/440
terminal reasons + query timeouts) as `sealed : SdkException` with the same
`(string message, Exception? inner = null)` ctor and operation-naming message discipline.
Keep the `protected SdkException(string, Exception?)` base untouched.

---

### Test files

#### `tests/.../Transport/FrameReaderTests.cs` + `Noise/NoiseVectorTests.cs` + `Codec/BinaryNodeRoundTripTests.cs` (golden/table tests)

**Analog:** `tests/.../Core/JidTests.cs` — the parse-accept/reject table precedent
(and `Core/AccountIdTests.cs` for validation-rule tables).

**What the planner copies:** xunit.v3 `[Fact]`/`[Theory]` + AwesomeAssertions fluent
asserts; golden fixtures as committed files under `tests/.../TestData/` (JSON for Noise
vectors, binary for frames). Frame tests cover valid + truncated + coalesced +
split-byte-by-byte + evil-length (RESEARCH.md Pitfall 3). Vector tests load cacophony
`XX_25519_AESGCM_SHA256` JSON — never self-generated vectors.

#### `tests/.../Noise/NoiseHandshakeTests.cs` + `Connection/LoopbackTests.cs` (fake-harness tests)

**Analog:** `tests/.../StoreContract/FaultingBindingTests.cs` (fake-harness round-trip,
lines 26-45) — two-party handshake over the duplex-pipe fake; LoopbackTests is the ONE
real-WS test via `HttpListener` fake server.

**What the planner copies:** `Create...` factory helpers, Put/Get/Remove-style
round-trip asserts (`fetched.Should().NotBeNull()`, byte-equality via `.Should().Equal(...)`),
fault-clearing mid-test (`faulting.Mode = FaultMode.None` → transport fake injects each
disconnect code), same-instance reuse after clearing (lines 73-81).

#### `tests/.../Connection/QueryRouterTests.cs` (router tests)

**Analog:** `tests/.../Stores/StoreRegistryTests.cs` (NSubstitute fake injection + AwesomeAssertions).

**Substitute-injection pattern** (`StoreRegistryTests.cs`, lines 54-67):
```csharp
var hostStore = Substitute.For<ISignalSessionStore>();
var hostFactory = Substitute.For<ISignalSessionStoreFactory>();
hostFactory.CreateAsync(Arg.Any<AccountId>(), Arg.Any<CancellationToken>())
    .Returns(ValueTask.FromResult(hostStore));
var registry = new StoreRegistry(new StoreRegistryOptions { SignalSessionFactory = hostFactory });

var session = await registry.CreateSessionAsync(account, CancellationToken.None);

session.SignalSessions.Should().BeSameAs(hostStore);
_ = hostFactory.Received(1).CreateAsync(account, Arg.Any<CancellationToken>());
```

**What the planner copies:** `Substitute.For<IFrameTransport>()` for the router's
transport; `Returns(...)` for canned replies; `Received(1)` for send-count asserts;
`.Should().BeSameAs(...)` for instance asserts. Covers correlation, 60s timeout
(FakeTimeProvider), late-reply-after-timeout discard.

#### `tests/.../Connection/ReconnectPolicyTests.cs` (policy tests) — exact analog

**Analog:** `tests/.../Stores/DurableWriteRetryPolicyTests.cs` in full — the
FakeTimeProvider policy-test precedent.

**FakeTimeProvider pattern** (`DurableWriteRetryPolicyTests.cs`, lines 17-31):
```csharp
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Stores.Runtime;
using Microsoft.Extensions.Time.Testing;
using Xunit;
```

```csharp
var policy = new DurableWriteRetryPolicy(new RetryOptions(), new FakeTimeProvider());
```

**Deterministic-delay pattern** (lines 169-196 — advance fake time, assert incomplete):
```csharp
// Attempt 1 failed; first retry delay is clamped into [50, 65] (raw 50 ±30%).
fake.Advance(TimeSpan.FromMilliseconds(49));
task.IsCompleted.Should().BeFalse("the first delay is at least BaseDelayMs (50ms) but only 49ms of fake time elapsed");
```

**Payload-discipline assert pattern** (lines 102-110):
```csharp
var thrown = await Assert.ThrowsAsync<SdkStoreException>(() => task);

calls.Should().Be(3);
thrown.Message.Should().Contain("durable store write failed after 3 attempts");
thrown.InnerException.Should().BeSameAs(failures[2]);

// Payload discipline (T-01-12): the surfaced message names the operation — never the
// inner exception's message content.
thrown.Message.Should().NotContain("gamma");
```

**What the planner copies:** Same usings (swap `Stores.Runtime` for `Connection`),
same `FakeTimeProvider` construction, same advance-and-assert-incomplete delay proofs,
same `NotContain` payload-discipline asserts. Per-code table coverage incl. 440/411
no-retry; backoff bounds sweep; 515 resets the attempt counter.

---

## Shared Patterns

### Logging (ILogger Abstractions-only, per-account scopes)
**Source:** `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs` (line 94: `ILogger<StoreRegistry>? logger = null`) + `src/Iwasug.WhatsApp.Sdk/Errors/StoreErrorSurface.cs` (lines 29-40)
**Apply to:** `WebSocketFrameTransport`, `WaConnection`, `QueryRouter`, `ReconnectPolicy` loop
```csharp
public StoreRegistry(StoreRegistryOptions options, ILogger<StoreRegistry>? logger = null)
// ...
logger?.LogWarning(
    ex,
    "{Operation} failed for account {AccountId}.",
    operation,
    account.Value);
```
Rules: Abstractions only (never Serilog/concrete); logger optional-nullable; structured
template with `{AccountId}` carrying `account.Value` (never key material / payloads /
inner-exception messages — T-01-12). Every connect/drop/reconnect/anomaly logs inside a
per-account scope.

### Error handling (typed hierarchy + wrap-and-log, never double-wrap)
**Source:** `src/Iwasug.WhatsApp.Sdk/Errors/StoreErrorSurface.cs` (lines 29-40) + `Errors/SdkException.cs` (lines 14-23)
**Apply to:** All `Transport/`, `Noise/`, `Connection/` production files
```csharp
internal static SdkStoreException LogAndWrap(Exception ex, ILogger? logger, string operation, AccountId account)
{
    var wrapped = ex as SdkStoreException ?? new SdkStoreException($"{operation} failed.", ex);

    logger?.LogWarning(
        ex,
        "{Operation} failed for account {AccountId}.",
        operation,
        account.Value);

    return wrapped;
}
```
Rules: `OperationCanceledException` passes through unwrapped/unretried; already-SDK-typed
passes through (`as ?? new`); messages name the operation/rule only. Phase 2 extends the
surface with transport/connection subtypes — same shape, new types.

### Message discipline (T-01-01/T-01-12)
**Source:** `src/Iwasug.WhatsApp.Sdk/Accounts/AccountId.cs` (lines 44-47) + `Domain/Jid.cs` (lines 60-67)
**Apply to:** Every exception message and log site in Phase 2 (crypto material makes this critical — V6)
```csharp
/// <exception cref="SdkFormatException">
/// <paramref name="value"/> violates one of the rules above. The message names the
/// violated rule only — it never embeds the value itself (message discipline, T-01-01).
/// </exception>
```
Rules: exception/log messages name rules/operations only — never key material, frame
bytes, node payloads, account values, or inner-exception message content. Review
checkpoint on all new log sites (RESEARCH.md Security Domain).

### Async conventions (CT + ConfigureAwait + ValueTask)
**Source:** `Stores/InMemory/InMemoryStoreFactory.cs` (lines 56-59) + `Stores/Runtime/DurableWriteRetryPolicy.cs` (lines 84-106)
**Apply to:** Every async method in Phase 2
```csharp
public ValueTask<ISignalSessionStore> CreateAsync(AccountId accountId, CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    // ...
    return await operation(cancellationToken).ConfigureAwait(false);
    await Task.Delay(TimeSpan.FromMilliseconds(ComputeDelayMs(attempt)), _timeProvider, cancellationToken).ConfigureAwait(false);
```
Rules: `CancellationToken cancellationToken = default` on every async signature;
`ThrowIfCancellationRequested()` first line; `.ConfigureAwait(false)` on every await;
`ValueTask<T>` for hot seam methods; injectable `TimeProvider` (default `TimeProvider.System`)
wherever time is observed (policy, router timeouts) so tests use `FakeTimeProvider`.

### Test conventions (xunit.v3 + AwesomeAssertions + NSubstitute)
**Source:** `tests/.../Stores/DurableWriteRetryPolicyTests.cs` (lines 1-6) + `tests/.../Stores/StoreRegistryTests.cs` (lines 1-9)
**Apply to:** All 7 Phase 2 test files
```csharp
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Stores.Runtime;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Stores;
```
Rules: file-scoped namespace mirroring the area (`Tests.Transport`, `Tests.Noise`,
`Tests.Codec`, `Tests.Connection`); `public sealed class XTests`; descriptive
`MethodName_condition_result` test names; `InternalsVisibleTo` already covers the test
assembly (see SDK csproj lines 29-31) so internal surfaces are directly testable.

### Project-file conventions (central versions, nullable, analyzers)
**Source:** `src/Iwasug.WhatsApp.Sdk/Iwasug.WhatsApp.Sdk.csproj` (lines 1-37) + `Directory.Packages.props` (lines 9-33)
**Apply to:** `Directory.Packages.props` additions + any csproj edits
```xml
<PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers" PrivateAssets="all" />
...
<ItemGroup>
  <InternalsVisibleTo Include="Iwasug.WhatsApp.Sdk.Tests" />
</ItemGroup>
```
Rules: new versions (`NSec.Cryptography` 26.4.0, `Google.Protobuf` 3.36.2,
`Grpc.Tools` 2.84.0) go in `Directory.Packages.props` — never inline in csproj;
`Grpc.Tools` gets `PrivateAssets="all"`; TFM/LangVersion/nullable come from
`Directory.Build.props` (don't restate). New public APIs must be declared in
`PublicAPI.Unshipped.txt` (PublicApiAnalyzers gate).

---

## No Analog Found

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| `src/.../Proto/handshake.proto` | config (schema) | transform | First `.proto` in repo — no schema precedent; planner follows RESEARCH.md Pitfall 6 (hand-written, provenance header, Baileys-MIT only) + STACK.md versions |
| `src/.../Noise/NoiseHandshake.cs` (crypto core) | service | request-response | No crypto code exists yet (Phase 1 was stores-only); class-shape/harness patterns come from `DurableWriteRetryPolicy` + `FaultingBindingTests`, but the XX state machine, AES-GCM nonce handling, and cacophony vectors have no codebase analog — planner uses RESEARCH.md Code Examples + noise-spec reference |

Partial-analog note: `FrameReader` (Pipelines fill/parse loop), `NoiseTransport`
(AEAD encrypt/decrypt), and `WaConnection`'s single-writer event dispatch (Pitfall 7)
likewise have no in-repo precedent for their *algorithmic* core — the mapped analogs
cover file shape, error handling, disposal, and test harness only. RESEARCH.md is the
authority for the algorithmic content.

---

## Metadata

**Analog search scope:** `src/Iwasug.WhatsApp.Sdk/**` (18 files), `tests/**` (19 files),
`Directory.Packages.props`, SDK csproj
**Files scanned:** ~40
**Pattern extraction date:** 2026-10-08
**Verify story for planner:** `dotnet build Iwasug.WhatsApp.Sdk.sln`, `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests`
