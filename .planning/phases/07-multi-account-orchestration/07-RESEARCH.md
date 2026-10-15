# Phase 7: Multi-Account Orchestration - Research

**Researched:** 2026-10-15
**Domain:** Multi-account orchestration, hard isolation, idiomatic .NET hosting over the existing per-account protocol stack
**Confidence:** MEDIUM-HIGH

## Summary

Phase 7 is composition-only: phases 1-6 already make the per-account stack (`WaConnection` -> `Messenger` -> `SafetyGate` -> `StoreSession`) correct under concurrency, and each instance is fully self-contained. The phase's job is to add one layer that owns N of these independent graphs, exposes them through an idiomatic .NET hosting surface, and proves no cross-account state leaks under load.

The only new dependencies sit in the Microsoft.Extensions family already used by the SDK (Options 10.0.12 / Logging.Abstractions 10.0.12): hosting abstractions (IHostedService), DI abstractions, and configuration-options binding, all at the repo's 10.0.12 servicing pin. No new third-party packages are required. `[VERIFIED: NuGet flatcontainer API - microsoft.extensions.hosting.abstractions 10.0.12 exists]`

**Primary recommendation:** Introduce one `SessionManager` (singleton) that owns a `ConcurrentDictionary<AccountId, AccountRuntime>`, where `AccountRuntime` is the only object that holds an account's complete object graph (connection, messenger, gate, store session, outbound queue, circuit breaker, logger scope, cancellation source). The manager never reaches into an account's internals; it treats each as `IAsyncDisposable`. Isolation is proven mechanically by a runtime suite that drives two accounts through concurrent send/store/event flows and asserts zero cross-reads, plus a static audit for forbidden `static` mutable state in protocol layers.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Account add/remove/restart/status | Host application (via SDK API) | API / Backend (shared server process) | SDK exposes the capability, host owns which accounts and when; no per-account HTTP surface inside SDK |
| Hard isolation of accounts | SDK library process (in-process) | Store (per-account namespace) | Isolation is enforced inside the SDK by per-account object graphs and per-account store sessions |
| DI registration / IOptions binding | Host (Generic Host) | SDK startup extensions | SDK ships extension methods, host registers them |
| IHostedService lifecycle | Host Generic Host | SDK hosted service implementation | SDK provides `IHostedService`, host starts/stops it |
| Outbound queues / rate limiters | SDK library (per account) | — | Must be per-account so a stalled account cannot stall siblings |
| Health aggregation | SDK library | Host UI/monitoring | SDK exposes data-only snapshots; host renders |
| Async-stream event fan-out | SDK library | Host consumer | `IAsyncEnumerable` with cancellation end-to-end |
| Logging scopes | SDK library | Host provider | SDK attaches AccountId scopes at runtime construction |

## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| MUL-01 | Host manages many concurrent accounts via `SessionManager` (add/remove/status/restart, hot add/remove, per-account health) | Sections: Standard Stack (Hosted/DI), Architecture Patterns (AccountRuntime + SessionManager API), Code Examples |
| MUL-02 | Accounts hard-isolated (per-account store namespace, outbound queue, limiter, no shared mutable state) + isolation suite in CI | Sections: Don't Hand-Roll, Common Pitfalls, Architecture Patterns (Isolation Contract) |
| MUL-03 | Idiomatic .NET surface (DI, IOptions, IHostedService, async streams, CancellationToken, per-account ILogger scopes) | Sections: Architecture Patterns (Hosting Surface, Async Streams, Logging), Standard Stack |

## Standard Stack

### Core (no new packages in the SDK core)

| Component | Version | Purpose | Why Standard |
|-----------|---------|---------|--------------|
| `Iwasug.WhatsApp.Sdk` (existing) | Phase 6 HEAD | Per-account connection, messaging, safety, stores | Already battle-tested for one account; phase 7 composes N copies |
| `Microsoft.Extensions.Options` | 10.0.12 (already referenced) | `IOptions<T>` settings surface | Already in `Directory.Packages.props` and csproj |
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.12 (already referenced) | `ILoggerFactory` / per-account scopes | Already referenced |
| `System.Threading.Channels` | in-box (.NET 10) | Per-account outbound queue + merge stream | Canonical backpressure primitive; already used by `Messenger` |
| `System.Threading.RateLimiting` | 10.0.12 (already referenced) | Already inside `SafetyGate`; no new use | Reuse per-account safety layer |

### New supporting packages (add to `Directory.Packages.props` + sdk csproj, all at repo servicing pin 10.0.12)

| Library | Version | Purpose | Why |
|---------|---------|---------|-----|
| `Microsoft.Extensions.DependencyInjection.Abstractions` | 10.0.12 | `IServiceCollection` extension target | Lets the SDK register its singleton seam without forcing a particular DI container; standard for embeddable .NET libraries |
| `Microsoft.Extensions.Hosting.Abstractions` | 10.0.12 | `IHostedService`, `BackgroundService` base | SDK implements the hosted-service contract; host supplies the Generic Host |
| `Microsoft.Extensions.Options.ConfigurationExtensions` | 10.0.12 | `BindConfiguration`, `ValidateOnStart` | Idiomatic `IOptions<T>` binding from `IConfiguration` (MUL-03) |

**Verification:** flatcontainer `versions` arrays confirm all four `10.0.12` pins exist (hosting abstractions queried 2026-10-15 returning `10.0.12`). `[VERIFIED: NuGet flatcontainer API]`

**Installation:**
```bash
# in Directory.Packages.props
<PackageVersion Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.12" />
<PackageVersion Include="Microsoft.Extensions.Hosting.Abstractions"            Version="10.0.12" />
<PackageVersion Include="Microsoft.Extensions.Options.ConfigurationExtensions"  Version="10.0.12" />
```

### Alternatives Considered

| Standard | Could Use | Tradeoff |
|----------|-----------|----------|
| `BackgroundService` (hosting abstractions) | A hand-rolled `IHostedService` with `TaskCompletionSource` start signal | `BackgroundService` already solves graceful stop with a linked token; reinventing buys nothing and adds bug surface |
| Per-account `Channel<T>` outbound queues | A single global dispatcher queue tagged by account | Global queue reintroduces the exact cross-account stall the requirement forbids |
| `ConcurrentDictionary<AccountId, AccountRuntime>` | A sharded `Dictionary` plus `ReaderWriterLockSlim` | `ConcurrentDictionary` gives atomic add/remove (needed for hot add/remove) for free; sharding only adds lock-count reasoning |
| Host-owned `IHostedService` startup order | SDK starts on first injected use | First-use startup masks boot-time failures and races handler subscriptions; hosted-service startup is observable and ordered |

## Package Legitimacy Audit

| Package | Registry | Age | Downloads | Source Repo | Verdict | Disposition |
|---------|----------|-----|-----------|-------------|---------|-------------|
| Microsoft.Extensions.DependencyInjection.Abstractions | nuget.org | ~9 yrs | >100M/wk (part of ME train) | github.com/dotnet/runtime | OK | Approved |
| Microsoft.Extensions.Hosting.Abstractions | nuget.org | ~7 yrs | >100M/wk | github.com/dotnet/runtime | OK | Approved |
| Microsoft.Extensions.Options.ConfigurationExtensions | nuget.org | ~9 yrs | part of ME train | github.com/dotnet/runtime | OK | Approved |

Packages removed due to [SLOP] verdict: none.
Packages flagged as suspicious [SUS]: none.

## Architecture Patterns

### System Architecture Diagram

```
Host process
  |
  v
+---------------------------------------------+
|  Generic Host (host-provided)                |
|  IHostedService  IOptions<WhatsAppSdkOptions>|
+--------+------------------------------------+
         |
         v
+---------------------------------------------+
|  WhatsAppSessionManager (singleton, SDK)     |
|  +-- ConcurrentDictionary<AccountId, Runtime>|
|  +-- AccountRuntime factory (injectable)    |
+--+--------+--------+--------+---------------+
   | A      | B      | C      | ... (N accounts, independent graphs)
   v        v        v
   +-----------+  +-----------+  +-----------+
   | WaConnection | | WaConnection | ...
   | Messenger    | | Messenger    |
   | SafetyGate   | | SafetyGate   |  (own rate limiter, circuit breaker)
   | Channel<T>   | | Channel<T>   |  (own outbound queue)
   | StoreSession | | StoreSession |  (own 7 stores)
   | ILogger scope<|end|> | ILogger scope|
   | CancellationTokenSource |
   +-----------+  +-----------+
         |            |
         v            v
   per-account stores (host or in-memory fallback)
```

Data flow for a host call "send from account A":
1. Host -> `manager.GetAccount(A)` (or through a manager-routed API)
2. AccountRuntime enqueues an `OutboundEnvelope` onto its own bounded `Channel`
3. The runtime's own pump applies safety gate + connection send
4. Events raised on `Messenger.ReadEventsAsync` flow to the manager merge stream tagged with `AccountId`

### Recommended Project Structure

```
src/Iwasug.WhatsApp.Sdk/
├── Accounts/
│   ├── AccountId.cs                       (existing)
│   ├── IWhatsAppSessionManager.cs         (new seam)
│   ├── SessionManager.cs                  (new: owns account dictionary)
│   ├── AccountRuntime.cs                  (new: per-account graph)
│   ├── IAccountRuntimeFactory.cs          (new: test seam)
│   ├── DefaultAccountRuntimeFactory.cs    (new)
│   └── AccountStatus.cs                   (new)
├── Hosting/
│   ├── WhatsAppOptions.cs                 (new root options)
│   ├── WhatsAppSdkOptions.cs              (alias? keep one name)
│   ├── WhatsAppHostedService.cs           (new IHostedService)
│   ├── ServiceCollectionExtensions.cs     (new AddWhatsAppSdk)
│   ├── AccountEvent.cs                    (new tagged event)
│   └── ScopedLogger.cs                    (new per-account logger)
└── Stores/ ... (unchanged)
```

### Pattern 1: SessionManager (account registry + lifecycle)

**What:** A singleton managing `ConcurrentDictionary<AccountId, AccountRuntime>`, with atomic add/remove/restart and per-account status snapshots. Reuses the existing `StoreRegistry.EnsureUnique` (D-26) as a pre-check and enforces uniqueness atomically at registration.

**When to use:** Every host - even a single account - so there is one code path (matches research ARCHITECTURE.md "one code path").

**Key rules (from existing internal seam):**
- `EnsureUnique` is check-then-act (IN-09). The manager must NOT rely on it for mutual exclusion; it must insert via `GetOrAdd` and let the loser observe the winner and throw `SdkDuplicateAccountException`.
- Removal snapshots the dictionary, evicts entries, then disposes (mirror `StoreRegistry.DisposeAsync()` WR-01/WR-03 discipline) so a racing add cannot resurrect a disposed runtime.
- Add account order: resolve store session first (cold, failure before any wire), then construct connection + messenger, then register, then start connection. On start failure, dispose stores and rethrow typed.

### Pattern 2: Per-account object graph (hard isolation)

**What:** Each `AccountRuntime` owns (no sharing between accounts):
- `StoreSession` (resolved once via `StoreRegistry.CreateSessionAsync`)
- `WaConnection` (own transport factory)
- `Messenger` (constructed lazily over that connection)
- `ISafetyGate` (own token buckets, warm-up, circuit breaker) constructed from that account's safety state store
- outbound `Channel<OutboundEnvelope>` (bounded, `FullMode=Wait`, capacity per options) + pump task
- `ILogger` wrapped in an account-id tagged scope
- `CancellationTokenSource` (account CTS, linked to hosted-service stop token)
- `SendConcurrency` (optional `SemaphoreSlim`, defaults to 1)

**When to use:** Always one runtime per account; manager refuses to share a runtime between two `AccountId`s.

**Invariants (the isolation suite asserts these):**
- No static mutable collections in `Connection`, `Messaging`, `Safety`, `Signal`, `Noise`, `Stores.Runtime` or `Media`. Audit the repo for `static ` field usages before Phase 7 starts.
- All state accessed by account passes through the runtime instance or constructor-injected seams; no Ambient contexts.
- Store reads use `StoreRegistry`-created sessions that are keyed by `AccountId`, so two accounts cannot resolve to the same `StoreSession`.

### Pattern 3: Hosting surface (DI + Options + IHostedService)

**What:** A single extension point consumed by the host:

```csharp
services.AddWhatsAppSdk(options =>
{
    // or bind IOptions<WhatsAppSdkOptions> via AddOptions<...>().BindConfiguration("WhatsApp")
});
```

- Registers `IWhatsAppSessionManager`, `WhatsAppSessionManager`, `IOptions<WhatsAppSdkOptions>`, and `IHostedService` (the host starts accounts at boot).
- Host can still resolve `IWhatsAppSessionManager` and hot-add/remove accounts after boot.
- `WhatsAppHostedService` (BackgroundService) reads `IOptions<WhatsAppSdkOptions>.Value.Accounts` and calls manager add for each; on stop it cancels all accounts and awaits a bounded drain.

**Why:** Matches MUL-03 verbatim. Generic host owns lifetime; the SDK never takes a dependency on `Microsoft.Extensions.Hosting` (full) - only on hosting abstractions. `[VERIFIED: NuGet flatcontainer API - 10.0.12 pin exists]`

**Configuration surface (root options):**
```csharp
public sealed class WhatsAppSdkOptions
{
    public List<AccountDescriptor>          Accounts { get; init; } = new();
    public ConnectionOptions               DefaultConnection { get; init; } = new();
    public SafetyOptions                   DefaultSafety     { get; init; } = new();
    public StoreRegistryOptions?            StoreFactories    { get; init; }  // null = all in-memory
    public int                              StartupJitterMs  { get; init; }  = 250; // per-account stagger
    public int                              ShutdownDrainTimeoutMs { get; init; } = 30_000;
    public bool                             AutoStartAccounts { get; init; }  = true;
}
```
`AccountDescriptor` carries `AccountId` (as string, validated at bind time via `AccountId.Create`), optional per-account name, per-account option overrides.

**Validation at start:** use `IOptionsMonitor`? Simpler: bind, then `ValidateDataAnnotations` + a custom validator running `SafetyOptions.Validate`-style checks; fail fast before any socket I/O.

### Pattern 4: Async streams and cancellation

**What:**
- Per account: `Messenger.ReadEventsAsync(ct)` already exists (bounded channel, capacity 128).
- Manager level:
  - `IAsyncEnumerable<AccountEvent> ReadEventsAsync(CancellationToken)` where `AccountEvent { AccountId; WhatsAppEvent Event }`.
  - Internally each runtime pushes a wrapped event into a manager `Channel<AccountEvent>` (bounded capacity per account, or a shared bounded channel with `TryWrite` + drop-to-overflow? Backpressure preferred: Wait mode preserves ordering but stalls account pump; document this cross-account note).
  - `ReadAccountEventsAsync(AccountId, ct)` delegates to that account's messenger.

- Cancellation: every manager public method takes optional `CancellationToken` defaulting to default; outbound pump observes both the account CTS and caller token.

### Pattern 5: Scoped ILogger per account

**What:** Build an account-scoped logger once at runtime construction and pass it to `WaConnection` via its existing `ILogger<WaConnection>?` parameter. Because `WaConnection` logs into the passed logger, the scope must survive the connection's lifetime rather than be a short `using` block.

Implementation approach: an `AccountScopedLogger<T>` that implements `ILogger<T>` and wraps the factory logger; on each `Log` it calls `inner.BeginScope(new Dictionary<string,object> { ["AccountId"] = id })` and disposes the scope after writing. This attaches the tag to every log entry without requiring a long-lived scope disposable. `EventId`, log level, exceptions and state flow through unchanged.

Also scope at runtime level: runtime owns a factory-created logger for operations (`StartAsync`, add/remove) using the same wrapper.

### Pattern 6: Status / health snapshot

**What:** `AccountStatus` records returned from `GetStatusAsync(id)` (sync-friendly data, no I/O) or `GetStatusesAsync()` - immutable, allocation-cheap:
```csharp
public sealed record AccountStatus(
    AccountId AccountId,
    ConnectionState ConnectionState,
    SafetyCircuitState CircuitState,
    int      QueuedOutbound,
    int      RecentFailures,
    DateTimeOffset? LastConnectedAt,
    DateTimeOffset? LastActivityAt,
    string?          LastErrorRuleName);
```
`LastErrorRuleName` is rule-only (no payloads, per T-01-01 message discipline).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Account registry mutation | A lock + list rebuild | `ConcurrentDictionary.GetOrAdd` + snapshot-on-dispose | Lock discipline across add/remove/status/get is error-prone and hot paths serialize all accounts |
| Hosted-service stop signaling | Custom `ManualResetEventSlim` | `BackgroundService` + linked `CancellationToken` | Stop token propagation and `ExecuteAsync` exception surfacing already solved |
| Configuration binding | Manual reflection over `IConfiguration` | `OptionsBuilder<T>.BindConfiguration` + `ValidateOnStart` | Source-generated binders and validation aggregation exist |
| Logger scopes | ThreadStatic ambient context | Per-account `ILogger` wrapper plus `BeginScope` at log time | Ambient context is the #1 multi-account isolation bug (CONTEXT.md-era anti-pattern) |
| Outbound dispatcher | `Task.Run` per send | Per-account bounded `Channel<T>` with a single pump task | Avoids unbounded threads + gives backpressure + one natural cancellation point |
| Account-unique IDs for events | A global counter | Per-account increment stamped with `AccountId` | Global counter itself is cross-account shared mutable state |

## Runtime State Inventory

This phase is greenfield-composition. No rename/refactor of identifiers, so no stored-data/live-service-state migration. Omit per greenfield guidance.

## Common Pitfalls

### Pitfall 1: Shared safety gate across accounts
**What goes wrong:** Two accounts reuse one `SafetyGate` instance -> one account's circuit breaker trips both.
**Why it happens:** Registration code reaches for a "shared" limiter to save memory.
**How to avoid:** SafetyGate constructed per `AccountRuntime`; isolation suite asserts circuit-breaker state independence by tripping one and sending on the other.
**Warning signs:** A unit test where `BudgetExhausted` on account A surfaces on B's handler.

### Pitfall 2: Static caches and ambient context ("the isolation killer")
**What goes wrong:** A `static ConcurrentDictionary<Jid, ...>` in protocol layers silently shares state across accounts.
**Why it happens:** Phase 1-6 deliberately kept state in instances, but isolated key spaces get tempted.
**How to avoid:** Add a `NoStaticMutableStateTests` audit test that reflects over the public/assembly types and fails on any non-readonly static fields of mutable types. Document explicit allow-list (e.g., `AccountId.MaxLength` consts).
**Warning signs:** Isolation suite flakes under parallelism.

### Pitfall 3: Hot add races with in-flight store creation
**What goes wrong:** Add account succeeds for the caller before `StoreRegistry.CreateSessionAsync` completes; a second add of the same id sees no cached session yet and succeeds (duplicate account).
**Why it happens:** Using `EnsureUnique` (check-then-act) as the sole guard.
**How to avoid:** Registration is one atomic `GetOrAdd` on the runtime dictionary; the factory returns the existing (and throws `SdkDuplicateAccountException`) for the loser. Mirror existing `StoreRegistry` registration discipline and its remarks.

### Pitfall 4: Per-account outbound pump holds connection lock globally
**What goes wrong:** Outbound pump dispatches sends under a shared manager-level semaphore and serializes all accounts.
**Why it happens:** "Simplest" shared dispatcher.
**How to avoid:** Each runtime owns its own pump task; manager merely observes. Rate-limit requirement (4) then holds automatically.

### Pitfall 5: Hosted service blocks startup on first network I/O
**What goes wrong:** `StartAsync` awaits connection establishment; restart loop kills the process restart loop.
**Why it happens:** Treating connected state as start completion.
**How to avoid:** `StartAsync`/`ExecuteAsync` schedules per-account `Start` tasks and returns immediately; connection happens in the background with retry. Status surfaces `Connecting`.

### Pitfall 6: Shutdown leaves dangling hosted-service tasks
**What goes wrong:** `StopAsync` returns before account disposal; process exits mid-write.
**Why it happens:** Forgetting to await per-account disposal.
**How to avoid:** `StopAsync` cancels all account CTSs, then awaits `WhenAll` of disposal tasks under a bounded drain timeout, logging timed-out accounts (rule-only) rather than hanging forever.

### Pitfall 7: Event fan-out head-of-line blocking
**What goes wrong:** One account's high-volume stream blocks the global merge, starving slow accounts' events.
**Why it happens:** Single ordered channel with no fairness.
**How to avoid:** Per-account channel (capacity 128) + manager-level merge via `Channel.Reader.ReadAllAsync` per account combined with `Task.WhenAny` over read waits; drop-to-overflow only when explicitly opted in (default: backpressure, documented).

### Pitfall 8: Logger scope lost across async boundaries
**What goes wrong:** `BeginScope` set during start; later log lines carry no AccountId.
**Why it happens:** `AsyncLocal` flow breaks when work is scheduled on different contexts.
**How to avoid:** Use the `AccountScopedLogger<T>` wrapper that stamps each `Log` call with its own scope, instead of relying on ambient lifetime.

## Code Examples

### ServiceCollection extension
```csharp
// Source: Microsoft.Extensions DI conventions (MUL-03)
public static class WhatsAppSdkServiceCollectionExtensions
{
    public static IServiceCollection AddWhatsAppSdk(
        this IServiceCollection services,
        Action<WhatsAppSdkOptions>? configure = null)
    {
        services.AddOptions<WhatsAppSdkOptions>()
                .Configure(o =>
                {
                    configure?.Invoke(o);
                    SafetyOptions.Validate(o.DefaultSafety); // reuse existing 05-04 validation pattern
                });
        services.AddSingleton<IWhatsAppSessionManager, WhatsAppSessionManager>();
        services.AddSingleton<IAccountRuntimeFactory, DefaultAccountRuntimeFactory>();
        services.AddHostedService<WhatsAppHostedService>();
        return services;
    }
}
```

### Atomic registration (reuses existing D-26 discipline)
```csharp
// Source: StoreRegistry internal EnsureUnique remarks (IN-09) + ConcurrentDictionary registration
public async Task<AccountStatus> AddAccountAsync(
    AccountId accountId, /* options */, CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();

    // Build OUTSIDE the dictionary so a duplicate loses only its candidate
    // object graph, not account state.
    AccountRuntime candidate = await _factory.CreateAsync(accountId, options, _stopToken, cancellationToken)
                                             .ConfigureAwait(false);

    return await _runTimes.AddOrUpdate(
        accountId,
        addValueFactory:       _ => candidate,
        updateValueFactory:     (key, existing) =>
        {
            candidate.DisposeQuietly();
            throw new SdkDuplicateAccountException("Account already registered (D-26).");
        });
}
```
Note: real implementation uses `TryAdd` returning a bool plus a post-add disposal path rather than `AddOrUpdate` (to avoid swallowing the throw into a value). Sketch intent only: registration must be atomic and losers disposed.

### Atomic unregister with snapshot (WR-01 mirror)
```csharp
public async Task RemoveAccountAsync(AccountId accountId, CancellationToken cancellationToken = default)
{
    if (_accounts.TryRemove(accountId, out AccountRuntime? runtime))
    {
        await runtime.DisposeAsync().ConfigureAwait(false);
    }
    // Removal of an unknown account is a no-op (idempotent).
}
```

### Account-scoped logger wrapper
```csharp
public sealed class AccountScopedLogger<T> : ILogger<T>
{
    private readonly ILogger _inner;
    private readonly AccountId _accountId;
    public AccountScopedLogger(ILoggerFactory factory, AccountId accountId)
    {
        _inner     = factory.CreateLogger<T>();
        _accountId = accountId;
    }

    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState,string> fmt)
    {
        using (_inner.BeginScope(new Dictionary<string, object> { ["AccountId"] = _accountId.Value }))
        {
            _inner.Log(level, id, state, ex, fmt);
        }
    }
    public bool IsEnabled(LogLevel level) => _inner.IsEnabled(level);
    public IDisposable BeginScope<TState>(TState state) => _inner.BeginScope(state);
}
```

### Manager-level merge stream
```csharp
public async IAsyncEnumerable<AccountEvent> ReadEventsAsync(
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    // Merge per-account channels with cooperative cancellation:
    var readers = _accounts.Values.Select(r => r.Events.Reader).ToArray();
    // ... standard fan-in loop using Task.WhenAny over ReadAsync waits,
    //     extracting AccountId tag from each AccountEvent.
    await foreach (var evt in merged.ReadAllAsync(cancellationToken))
        yield return evt;
}
```

## State of the Art

| Old Approach | Current Approach | Impact |
|--------------|------------------|--------|
| One global logger + ambient AccountId | Per-account logger wrapper stamped at log time | Removes cross-account log contamination |
| Shared connection pool across accounts | One `WaConnection` per account | Removes a whole class of shared-state bugs; scales by process sharding if needed |
| Single global send queue | Per-account bounded `Channel<T>` + pump | Per-account rate limiter and breaker become enforceable |
| `EnsureUnique` as enforcement | Atomic `GetOrAdd` registration | Closes the duplicate-race window (IN-09) |

**Deprecated / avoid:**
- `static ConcurrentDictionary` in protocol packages - forbidden by isolation audit.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `SessionManager` names/types proposed here are conventional; exact public spelling is not yet locked | Architecture Patterns | Planner may rename; tests reference these names - keep rename churn low by locking names in discuss phase |
| A2 | "Tens of accounts per process" (10-200) is the design centre; beyond ~500 IO-bound tasks consider process sharding | Standard Stack | Performance numbers unmeasured; manager should avoid assumptions that prevent sharding |
| A3 | All package pins stay on 10.0.12 to match repo servicing train; a 10.0.13 patch exists | Standard Stack | Using 10.0.13 would require central pin bump; stay consistent |
| A4 | Optional mediator DI packages (M.E.DI 10.0.12) already pulled transitively but not central-pinned; pin them explicitly to avoid drift | Standard Stack | Low; host versions typically dominate |
| A5 | No external orchestration library (e.g. MassTransit) is needed; std channels suffice | Don't Hand-Roll | If outbox/durable send appears in scope, revisit |

## Open Questions

1. **Outbox / durable send queueing** (hinted in research SUMMARY Phase 7 as "durable outbox (P2 can slot here)")
   - What we know: outbound messages are one-send semantics (Phase 4 idempotency contract).
   - What's unclear: whether host durability requirement means a DB-backed outbox in this phase.
   - Recommendation: Keep `Channel<T>` in memory for v1, document outbox as optional extension seam (a second implementation of the outbound sink), defer DB outbox to Phase 8 planning unless road map adds it.

2. **Where `AccountId` string pairing happens in config**
   - What we know: `AccountId.Create` validates; config stores strings.
   - What's unclear: whether host provides per-account store factory on AddAccount vs global StoreRegistry.
   - Recommendation: Global `StoreRegistry` (already singleton seam) created from `StoreFactories`; per-account resolution uses AccountId - no change required.

3. **Manager event granularity**
   - Whether `AccountEvent` should wrap the whole `WhatsAppEvent` or expose per-typed streams. Recommend wrapper for simplicity, single consumption shape.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | build/test | yes | 10.0.401 (repo global.json) | — |
| .NET runtime | execution | yes | 10.0.12 | — |
| Generic Host packages | MUL-03 | installable | nuget.org flatcontainer 10.0.12 (verified) | Host supplies them |
| Live WhatsApp account | integration UAT | not in CI | — | unit proofs use in-memory + loopback fakes |

## Validation Architecture

### Test Framework

| Property | Value |
|----------|-------|
| Framework | xunit.v3 4.0.1 + Microsoft.NET.Test.Sdk 18.10.1 |
| Config file | repo-root `global.json` (`test.runner`) |
| Quick run command | `dotnet test --filter "FullyQualifiedName~SessionManager\|Isolation\|Hosting"` |
| Full suite command | `dotnet test` |

### Phase Requirements -> Test Map

| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| MUL-01 | Add/remove/restart/status/hot add | unit + integration | `dotnet test --filter FullyQualifiedName~SessionManagerLifecycle` | no - Wave 0 |
| MUL-01 | per-account health snapshot correctness | unit | `dotnet test --filter FullyQualifiedName~AccountHealth` | no - Wave 0 |
| MUL-02 | two-account isolation suite | integration | `dotnet test --filter FullyQualifiedName~Isolation` | no - Wave 0 |
| MUL-02 | no shared mutable static state audit | reflection test | `dotnet test --filter FullyQualifiedName~NoStaticMutableState` | no - Wave 0 |
| MUL-02 | one account's rate limit / breaker does not stall another's send | integration | `dotnet test --filter FullyQualifiedName~RateLimitIsolation` | no - Wave 0 |
| MUL-03 | DI registration creates singleton manager | unit | `dotnet test --filter FullyQualifiedName~AddWhatsAppSdk` | no - Wave 0 |
| MUL-03 | IOptions<T> binding loads account descriptors | unit | `dotnet test --filter FullyQualifiedName~OptionsBinding` | no - Wave 0 |
| MUL-03 | hosted service starts accounts and stops cleanly | unit | `dotnet test --filter FullyQualifiedName~HostedServiceLifecycle` | no - Wave 0 |
| MUL-03 | async stream cancellation end-to-end | unit | `dotnet test --filter FullyQualifiedName~EventStreamCancellation` | no - Wave 0 |
| MUL-03 | per-account logger scope contains AccountId | unit | `dotnet test --filter FullyQualifiedName~ScopedLogger` | no - Wave 0 |

### Sampling Rate

- **Per task commit:** `dotnet test --filter FullyQualifiedName~SessionManager`
- **Per wave merge:** `dotnet test`
- **Phase gate:** Full suite green before `/gsd-verify-work`; isolation suite must run (not skip) in CI.

### Wave 0 Gaps

- [ ] `tests/.../SessionManager/SessionManagerTests.cs` - add/remove/status/restart + concurrency
- [ ] `tests/.../Isolation/TwoAccountRuntimeTests.cs` - concurrent send/store/events, zero cross-reads
- [ ] `tests/.../Isolation/NoStaticMutableStateTests.cs` - reflection audit
- [ ] `tests/.../Hosting/AddWhatsAppSdkTests.cs` - DI reg + options binding
- [ ] `tests/.../Hosting/HostedServiceLifecycleTests.cs` - start/stop/drain
- [ ] `tests/.../Hosting/ScopedLoggerTests.cs` - scope tagging
- [ ] `tests/.../Messaging/EventStreamCancellationTests.cs` - manager fan-in cancellation
- [ ] Install new ME abstractions (csproj + packages.props) - otherwise compile fails

## Security Domain

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | No | WhatsApp handles auth via pairing protocol (Phase 3) |
| V3 Session Management | Yes | Per-account `StoreSession` + isolated `CancellationToken`; manager never stores tokens in shared state |
| V4 Access Control | Yes | Host decides which callers may add accounts / view health; SDK exposes manager as the single control surface (no per-account backdoors) |
| V5 Input Validation | Yes | `AccountId.Create` (validated), `WhatsAppSdkOptions` validation at start |
| V6 Cryptography | No | Composes Phase 4 crypto; no new crypto code in this phase |

### Known Threat Patterns for Multi-Account Hosts

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Cross-account store read (isolation failure) | Information Disclosure | Per-account `StoreSession` keyed by AccountId; isolation test suite in CI |
| Global static state leaking per-account data | Information Disclosure | Reflection audit test for non-readonly static fields |
| One account DoSing all accounts via global queue | Denial of Service | Per-account bounded `Channel<T>` + pump |
| Account enumeration from health endpoints | Information Disclosure | Health snapshot contains rule-only error names and no JIDs/contents |
| Configuration secret leakage | Confidentiality | `StoreRegistryOptions` keeps factory delegates; SDK never logs payloads |

## Sources

### Primary (HIGH confidence)

- StoreRegistry remarks (`src/Iwasug.WhatsApp.Sdk/Stores/StoreRegistry.cs`) - D-23/D-24/D-26, WR-01/WR-03, IN-09 - 10.0-era in-repo source
- Connection/Messaging surface (`src/Iwasug.../Connection/WaConnection.cs`, `Messaging/Messenger.cs`) - constructor params, events, async streams
- Phase research summary (`/.planning/research/ARCHITECTURE.md`, `SUMMARY.md`) - multi-account composition boundaries
- NuGet flatcontainer API (`api.nuget.org`) - 10.0.12 pins for hosting abstractions - queried 2026-10-15

### Secondary (MEDIUM confidence)

- Microsoft.Extensions.Hosting `BackgroundService` and DI conventions - official .NET documentation
- wa-multi-session / WaSP orchestration sketches (referenced in ARCHITECTURE.md sources)

### Tertiary (LOW confidence)

- Empirical scale numbers ("tens of accounts") - A2, to be confirmed by benchmarks

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - existing train + 3 verified abstractions
- Architecture: MEDIUM-HIGH - composition boundaries fixed by phases 1-5; manager specifics are MEDIUM (no .NET reference at scale)
- Pitfalls: MEDIUM-HIGH - derived from existing phase pitfalls (static state, scope lifetime) plus known concurrent .NET pitfalls

**Research date:** 2026-10-15
**Valid until:** 2026-11-14 (30 days for stable .NET abstractions)
