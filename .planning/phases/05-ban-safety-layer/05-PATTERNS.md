# Phase 05: Ban-Safety Layer - Pattern Map

**Mapped:** 2026-10-14
**Files analyzed:** 26 (new + modified)
**Analogs found:** 24 / 26

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/Iwasug.WhatsApp.Sdk/Safety/ISafetyGate.cs` | interface | request-response | `src/Iwasug.WhatsApp.Sdk/Stores/ISafetyStateStore.cs` | role-match |
| `src/Iwasug.WhatsApp.Sdk/Safety/SafetyGate.cs` | service/gate | request-response + stateful | `src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs` | role-match |
| `src/Iwasug.WhatsApp.Sdk/Safety/SafetyGateException.cs` | error/exception | request-response | `src/Iwasug.WhatsApp.Sdk/Errors/SdkException.cs` | exact |
| `src/Iwasug.WhatsApp.Sdk/Safety/SafetyOptions.cs` | config/options | transform/validation | `src/Iwasug.WhatsApp.Sdk/Connection/ConnectionOptions.cs` | exact |
| `src/Iwasug.WhatsApp.Sdk/Safety/TokenBucketRateLimiterSet.cs` | utility/coordinator | rate-limited (stateful) | `src/Iwasug.WhatsApp.Sdk/Connection/ReconnectBackoff.cs` | role-match |
| `src/Iwasug.WhatsApp.Sdk/Safety/WarmupLadder.cs` | utility/engine | stateful + store I/O | `src/Iwasug.WhatsApp.Sdk/Connection/ReconnectPolicy.cs` | role-match |
| `src/Iwasug.WhatsApp.Sdk/Safety/ColdOutreachBudget.cs` | utility/engine | CRUD + stateful | `src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs` (recipient-state view) | partial |
| `src/Iwasug.WhatsApp.Sdk/Safety/CircuitBreaker.cs` | utility/state-machine | event-driven | `src/Iwasug.WhatsApp.Sdk/Connection/ReconnectBackoff.cs` | role-match |
| `src/Iwasug.WhatsApp.Sdk/Safety/HumanPacing.cs` | utility/seam | request-response | `src/Iwasug.WhatsApp.Sdk/Connection/ReconnectBackoff.cs` | role-match |
| `src/Iwasug.WhatsApp.Sdk/Safety/CircuitState.cs` | enum/type | transform | `src/Iwasug.WhatsApp.Sdk/Messaging/MessagingEvents.cs` (enum style) | role-match |
| `src/Iwasug.WhatsApp.Sdk/Safety/SafetyHealthRecord.cs` | record/domain | transform | `src/Iwasug.WhatsApp.Sdk/Domain/SafetyState.cs` | role-match |
| `src/Iwasug.WhatsApp.Sdk/Messaging/SafetyEvents.cs` | events/vocab | event-driven | `src/Iwasug.WhatsApp.Sdk/Messaging/MessagingEvents.cs` | exact |
| `src/Iwasug.WhatsApp.Sdk/Stores/IRecipientContactStore.cs` | interface + factory | CRUD | `src/Iwasug.WhatsApp.Sdk/Stores/ISafetyStateStore.cs` | exact |
| `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs` | controller/facade (modified) | request-response | self (existing send paths) | exact |
| `src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs` (modified) | store factory | CRUD | self (SafetyStateRow branch) | exact |
| `src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistryOptions.cs` (modified) | config/options | transform | self | exact |
| `tests/Iwasug.WhatsApp.Sdk.Tests/Safety/*.cs` (Wave 0) | test | unit | `tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ReconnectPolicyTests.cs` | role-match |
| `tests/Iwasug.WhatsApp.Sdk.Tests/Stores/IRecipientContactStoreContractTests.cs` | contract test | integration | `src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/SafetyStateStoreContractTests.cs` | exact |

## Pattern Assignments

### `src/Iwasug.WhatsApp.Sdk/Safety/ISafetyGate.cs` (interface, request-response)

**Analog:** `src/Iwasug.WhatsApp.Sdk/Stores/ISafetyStateStore.cs`

**Imports pattern** (lines 1-4):
```csharp
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;
```

**Interface method shape** (lines 32-53):
```csharp
Task<SafetyStateRecord?> GetAsync(CancellationToken cancellationToken);
Task PutAsync(SafetyStateRecord state, CancellationToken cancellationToken);
```

**Copy these conventions:** cancellation passed through unwrapped; `ValueTask` only at factory boundary; methods returning domain types typed (`Task<T>`), never raw blobs; XML doc comments on every member with rule/message discipline.

### `src/Iwasug.WhatsApp.Sdk/Safety/SafetyOptions.cs` (config/options, transform-validation)

**Analog:** `src/Iwasug.WhatsApp.Sdk/Connection/ConnectionOptions.cs`

**Imports/validation shape** (lines 1-8 + 62-109):
```csharp
namespace Iwasug.WhatsApp.Sdk.Connection;

public sealed class ConnectionOptions
{
    public int ConnectTimeoutMs { get; init; } = 20_000;
    ...
    internal static ConnectionOptions ValidateOptions(ConnectionOptions? options)
    {
        ConnectionOptions resolved = options ?? new ConnectionOptions();
        if (resolved.ConnectTimeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ConnectTimeoutMs must be positive.");
        }
        ...
        return resolved;
    }
}
```

**Copy these conventions:**
- `SafeDefaults` accessed via `public static ... SafeDefaults { get; } = new(...)`
- `Validate` is `internal static`, resolves `null` to defaults, throws `ArgumentOutOfRangeException` before any I/O
- Property discipline: `{ get; init; } = default`; non-nullable option objects are `sealed record` where immutable value equality is desired (SafetyOptions is a `record` per research; use `sealed record` to keep immutable)
- `AllowUnsafeThroughput` bool gates threshold checks only

### `src/Iwasug.WhatsApp.Sdk/Errors` extension: `SafetyGateException` (error, request-response)

**Analog:** `src/Iwasug.WhatsApp.Sdk/Errors/SdkException.cs`

**Base + sealed derived shape** (lines 14-23, 25-40):
```csharp
public abstract class SdkException : Exception
{
    protected SdkException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class SdkStoreException : SdkException
{
    public SdkStoreException(string message, Exception? inner = null) : base(message, inner) { }
}
```

**Copy these conventions:** messages name the operation/rule only, never JID or numeric thresholds embedded in a blaming way; inner exception preserved for store failures. For safety, add `SafetyRuleViolationException: SdkException` carrying a typed `SafetyRule` (e.g. `RateLimit`, `InactiveHours`, `ColdOutreach`, `CircuitOpen`) instead of stringly-typed reasons.

### Rate limit / jitter analogs

**Analog:** `src/Iwasug.WhatsApp.Sdk/Connection/ReconnectBackoff.cs` (constructor + TimeProvider discipline)

**Imports + constructor pattern** (lines 1-3, 199-225):
```csharp
internal sealed class ReconnectBackoff
{
    private readonly ConnectionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Func<double> _nextUnitDraw;
    private int _attempt;
    internal ReconnectBackoff(ConnectionOptions? options = null, TimeProvider? timeProvider = null, Func<double>? nextUnitDraw = null)
    {
        _options = ConnectionOptions.ValidateOptions(options);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _nextUnitDraw = nextUnitDraw ?? Random.Shared.NextDouble;
    }
    ...
    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
}
```

**Copy these conventions:**
- Inject `TimeProvider` (never `Task.Delay(TimeSpan)` directly) so tests use `FakeTimeProvider`
- Accept a `Func<double>` jitter seam defaulting to `RandomNumberGenerator.GetDouble` / `Random.Shared.NextDouble` for deterministic proofs
- Null resolves to defaults and out-of-range options throw before any await
- Cancellation-aware waits (pass token through unwrapped)

**Token-bucket allocation shape (research Pattern 3):**
```csharp
internal sealed class TokenBucketRateLimiterSet(TimeProvider clock, SafetyOptions options)
{
    private readonly TokenBucketRateLimiter _perMinute = new(
        new TokenBucketRateLimiterOptions
        {
            TokenLimit = options.MaxMessagesPerMinute,
            TokensPerPeriod = options.MaxMessagesPerMinute,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    // _perHour / _perDay follow the same shape with hour/day windows.
}
```

### Central gate seam (SAF-01)

**Analog:** `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs` (existing send paths + clock/store resolution)

**Imports/version baseline** (lines 1-11):
```csharp
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Signal;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.Runtime;
```
**Messenger send-path discipline** (lines 164-200):
```csharp
public async Task<SendOutcome> SendMessageAsync(Jid chat, string text, CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
    await EnsureOwnPrekeysAsync(cancellationToken).ConfigureAwait(false);
    return await _sender.SendAsync(chat, text, cancellationToken).ConfigureAwait(false);
}
```
**Store resolution seam** (lines 558-560):
```csharp
internal ValueTask<StoreSession> ResolveStoresAsync(CancellationToken cancellationToken) =>
    _connection.ResolveStoreSessionAsync(cancellationToken);
```
**Clock seam** (lines 563):
```csharp
internal TimeProvider Clock => _connection.Clock;
```

**Copy these conventions:**
- Add `internal SafetyGate _safety;` (or a readonly field initialized in constructor)
- Every public egress call wraps the inner delegate with `await _safety.ExecuteSendAsync(...).ConfigureAwait(false)`
- Cancellation passes through unwrapped (research Code Examples; Messenger lines 164-170 / 189-195 contracts)
- `EnsureOwnPrekeysAsync` runs before the gate (existing contract) so prekey readiness remains a separate concern
- Dense XML docs on every member (existing repo convention)

**Target change shape for Messenger (research Pattern 1 / Code Examples):**
```csharp
public async Task<SendOutcome> SendMessageAsync(Jid chat, string text, CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
    await EnsureOwnPrekeysAsync(cancellationToken).ConfigureAwait(false);

    return await _safety.ExecuteSendAsync(new SendContext(chat, ColdOutreachMode.Normal), cancellationToken, async () =>
    {
        return await _sender.SendAsync(chat, text, cancellationToken).ConfigureAwait(false);
    }).ConfigureAwait(false);
}
```
Same for `SendGroupMessageAsync` and `ReplyAsync` (with cold-outreach exemption for replies documented in research Pitfall 3).

### Lazy gate construction injection point

**Analog:** `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs` (Messaging property)

**Lazy construction under lock** (lines 304-318):
```csharp
public Messenger Messaging
{
    get
    {
        lock (_messagingGate)
        {
            if (_messaging is null)
            {
                var messenger = new Messenger(_accountId, this);
                StanzaReceived += messenger.OnStanzaReceived;
                _messaging = messenger;
            }

            return _messaging;
        }
    }
}
```

**Copy these conventions:** SafetyGate should be created inside `Messenger`'s constructor (per research), using `_connection` (clock/store) exactly as `DeviceDirectory`/`GroupDirectory`; do not add a new global. If a gate needs options, resolve from `ConnectionOptions` sibling or a new `SafetyOptions` member of `ConnectionOptions`-style. Messenger already has `Clock` and `ResolveStoresAsync`, both adequate.

###typed event vocabulary

**Analog:** `src/Iwasug.WhatsApp.Sdk/Messaging/MessagingEvents.cs`

**EventHandler + stream twin shape** (lines 68-84, 96-115, 176-214):
```csharp
public sealed record MessageReceivedEventArgs(...);   // EventHandler twin
public abstract record WhatsAppEvent(DateTimeOffset Timestamp);           // stream base
public sealed record MessageReceivedEvent(...) : WhatsAppEvent(Timestamp); // stream twin
```

**Copy these conventions:**
- Add `BudgetExhaustedEventArgs`, `ThrottleEngagedEventArgs`, `AccountRestrictedEventArgs` as EventHandler twins and `BudgetExhaustedEvent`, `ThrottleEngagedEvent`, `AccountRestrictedEvent` as sealed records deriving `WhatsAppEvent`
- Keep fields minimal (account key, trigger window/outcome, next allowed UTC, safety stage)
- Maintain one emit point feeding both surfaces with `PublishAsync` backpressure semantics (Messenger.cs lines 327-383)

### recipient ledger seam

**Analog interface/store factory:** `src/Iwasug.WhatsApp.Sdk/Stores/ISafetyStateStore.cs` + `src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs`

**Interface shape** (ISafetyStateStore.cs lines 26-54):
```csharp
public interface ISafetyStateStore : IAsyncDisposable
{
    Task<SafetyStateRecord?> GetAsync(CancellationToken cancellationToken);
    Task PutAsync(SafetyStateRecord state, CancellationToken cancellationToken);
    Task<bool> RemoveAsync(CancellationToken cancellationToken);
}
```

**Factory seam (research Pattern 2):**
```csharp
public interface IRecipientContactStore : IAsyncDisposable
{
    Task<bool> HasContactedAsync(string recipientKey, CancellationToken cancellationToken);
    Task MarkContactedAsync(string recipientKey, CancellationToken cancellationToken);
}

public interface IRecipientContactStoreFactory
{
    ValueTask<IRecipientContactStore> CreateAsync(AccountId accountId, CancellationToken cancellationToken = default);
}
```

**In-memory branch analog (InMemoryStoreFactory.cs lines 108-117):**
```csharp
ValueTask<ISafetyStateStore> ISafetyStateStoreFactory.CreateAsync(AccountId accountId, CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    ThrowIfDisposed();

    object state = GetOrAdd(accountId, SafetyStateCapability, () => new SafetyStateRow());

    return ValueTask.FromResult<ISafetyStateStore>(new InMemorySafetyStateStore((SafetyStateRow)state));
}
```

**Copy these conventions:**
- Use `ConcurrentDictionary`-backed row state
- `IAsyncDisposable` views; factory-level disposal wipes account state (WR-01)
- Capability constants namespaced per account
- Missing ≠ failure (null vs exception) per D-19

**StoreRegistry registration analog** (`StoreRegistryOptions.cs` lines 21-58): add a nullable `IRecipientContactStoreFactory? RecipientContactFactory { get; set; }` property following the existing per-capability pattern if this seam is wired through `StoreRegistry`/`StoreSession`. If kept SDK-internal (gate owns factory per account), the same factory resolution pattern still applies.

### Warm-up persistence analog

**Analog:** `src/Iwasug.WhatsApp.Sdk/Domain/SafetyState.cs` + `Stores/ISafetyStateStore.cs`

```csharp
public sealed record SafetyStateRecord(DateTimeOffset CreatedAt, int WarmupStage);
```

**Copy these conventions:**
- Create safety state on first paired credential (`row.PairedUser is not null`) using `CreatedAt = _connection.Clock.GetUtcNow()`
- Persist stage changes through `PutAsync` (D-06 await) — never recompute `CreatedAt` from process start
- Store is source of truth across restart (research Pitfall 2)

### JID-based recipient key

**Analog:** `src/Iwasug.WhatsApp.Sdk/Domain/Jid.cs` (User/Server components)

```csharp
public string User { get; }
public string Server { get; }
public override string ToString() { ... }
```

**Copy these conventions:** `recipientKey = $"{jid.User}@{jid.Server}"` (bare, no device/agent), no display name or message body. Hashing/policy stays host-owned.

### Error/testing analogs

**Analog tests:** `src/Iwasug.WhatsApp.Sdk.Testing/StoreContract/SafetyStateStoreContractTests.cs` (contract kit shape) and `tests/Iwasug.WhatsApp.Sdk.Tests/Connection/ReconnectPolicyTests.cs` (pure decision tests with injected TimeProvider/random seam). Test files should follow NSubstitute + AwesomeAssertions already in repo; NSubstitute 6.2.0 (BSD-3) and AwesomeAssertions 9.6.0 per `STACK.md`.

## Shared Patterns

### Authentication / Authorization / Guards
Not applicable (single-account SDK; auth at pairing layer). The ban-safety gate itself is the guard Tier. Apply `ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this)` before every gate evaluation (Messenger.cs:166 pattern).

### Error handling
**Source:** `src/Iwasug.WhatsApp.Sdk/Errors/SdkException.cs`
**Apply to:** all `Safety/*` files and Messenger send-path edits
```csharp
protected SdkException(string message, Exception? inner = null) : base(message, inner) { }
```
Cancellation passes through unwrapped (repo discipline).

### Clock / TimeProvider
**Source:** `src/Iwasug.WhatsApp.Sdk/Connection/ReconnectBackoff.cs` lines 220-239
**Apply to:** TokenBucketRateLimiterSet, WarmupLadder, ColdOutreachBudget, CircuitBreaker, HumanPacing
```csharp
_timeProvider = timeProvider ?? TimeProvider.System;
await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
```

### Durable writes
**Source:** `src/Iwasug.WhatsApp.Sdk/Stores/Runtime/DurableWriteRetryPolicy.cs` (ExecuteAsync shape)
**Apply to:** WarmupLadder stage writes + SafetyState/Credential writes through `StoreSession`
```csharp
await stores.SafetyState.PutAsync(state, cancellationToken).ConfigureAwait(false);
```
Await durable write before continuing (D-06).

### Event publish (backpressure, no drop)
**Source:** `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs` lines 373-383
**Apply to:** SafetyEvents emit site
```csharp
private async ValueTask PublishAsync(WhatsAppEvent evt, CancellationToken cancellationToken)
{
    try { await _events.Writer.WriteAsync(evt, cancellationToken).ConfigureAwait(false); }
    catch (ChannelClosedException) { }
}
```

### In-memory store factory branch
**Source:** `src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs` lines 108-117
**Apply to:** IRecipientContactStore in-memory implementation

## No Analog Found

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| `src/Iwasug.WhatsApp.Sdk/Safety/TokenBucketRateLimiterSet.cs` | utility | rate-limited | No rate limiter exists yet (first use of `System.Threading.RateLimiting.TokenBucketRateLimiter`); analog is the TimeProvider constructor + jitter seam from `ReconnectBackoff` (partial) |
| `src/Iwasug.WhatsApp.Sdk/Safety/CircuitBreaker.cs` | utility/state-machine | event-driven | No circuit breaker in codebase (Polly HTTP-only per STACK.md); analog is the in-process decision table in `ReconnectPolicy` (partial) |
| `src/Iwasug.WhatsApp.Sdk/Safety/WarmupLadder.cs` | utility/engine | stateful | No warm-up logic in codebase yet; analog is `DurableWriteRetryPolicy` + `SafetyStateRecord` persistence (partial) |
| `src/Iwasug.WhatsApp.Sdk/Safety/ColdOutreachBudget.cs` | utility/engine | CRUD | No recipient-contact concept before Phase 5; analog is `IRecentMessageCacheStore` + in-memory capability row pattern (partial/partial) |

## Metadata

**Analog search scope:** `src/Iwasug.WhatsApp.Sdk/**/*.cs` and `tests/Iwasug.WhatsApp.Sdk.Tests/**/*.cs` (git-tracked only)
**Files scanned:** 78 source + test (git ls-files)
**Pattern extraction date:** 2026-10-14