# Phase 5: Ban-Safety Layer - Research

**Researched:** 2026-10-14
**Domain:** Mandatory per-account ban-safety gate on every outbound send
**Confidence:** HIGH

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| SAF-01 | Every send passes through a mandatory safety gate; no bypass path exists in public API | Gate decorator is the only egress path from `Messenger.SendMessageAsync`, `SendGroupMessageAsync`, and `ReplyAsync`; protocol-generated acks/receipts/retry resends are explicitly outside the gate (they are responses, not account traffic) and are inventoried by the send-path audit test |
| SAF-02 | Per-account token-bucket rate limits (minute/hour/day) with randomized inter-send jitter | In-box `System.Threading.RateLimiting.TokenBucketRateLimiter` instances (one per window, per account) plus a jittered `TimeProvider`-based delay; no new NuGet dependency |
| SAF-03 | Warm-up ramp for new accounts with account-age state persisted in the store | `SafetyStateRecord` (`ISafetyStateStore`, already shipped by Phase 1) is extended minimally or used as the authority for `CreatedAt`/`WarmupStage`; `ISafetyStateStore` is durable and already has an in-memory reference implementation and contract kit |
| SAF-04 | Separate cold-outreach budget for never-messaged recipients, hard-enforced | One recipient ledger behind the Phase 1 granular store seam: a durable `IRecipientContactStore` per account (recommended addition) with `HasContactedAsync`/`MarkContactedAsync` |
| SAF-05 | Circuit breaker + exponential backoff on throttle signals, emitting typed safety events | Hand-rolled breaker (Polly stays HTTP-only per `STACK.md`) with three required event names: `BudgetExhausted`, `ThrottleEngaged`, `AccountRestricted` |
| SAF-06 | Human-like pacing hooks (typing/recording presence before send, active-hours window) | Gate owns a `PacingDelay` plus a presence-pause seam; active hours are checked before the inner send; a typed rejection (no silent deferral) |
| SAF-07 | Safe-by-default configuration with explicit `AllowUnsafeThroughput` opt-in and honest risk ledger | `SafetyOptions` with conservative defaults + startup validation; README risk ledger updated; NOTICE audit passes |
</phase_requirements>

## Summary

Phase 4 deliberately left a "ban-policy-free" `MessageSender`. Phase 5 is the one place where ban policy becomes product code, and it must be introduced as a chokepoint, not as `if (count > limit)` tests inside `SendMessageAsync`. The existing send path gives three public egress callers in `Messenger` (`SendMessageAsync`, `SendGroupMessageAsync`, `ReplyAsync`), a per-account `SemaphoreSlim(1, 1)` gate inside `MessageSender`, and a durable `ISafetyStateStore` seam [VERIFIED: src/Iwasug.WhatsApp.Sdk/Stores/ISafetyStateStore.cs:128-157, src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs:400-418]. Those three callers are the only mandatory interception points.

The standard implementation recommendation is an internal `SafetyGate` decorator that owns all ban-related state and is constructed per account. It wraps an inner `SendDelegate` that delegates to `MessageSender` after validation and pacing. No new NuGet dependency is needed: the policy's rate windows and typed gate state are built from `System.Threading.RateLimiting.TokenBucketRateLimiter`, `System.Threading.Channels`, the in-box BCL, and `Microsoft.Extensions.Logging.Abstractions` [CITED: .planning/research/STACK.md "Supporting Libraries" row].

**Primary recommendation:** Do not gate inside `MessageSender`. Add `SafetyGate : ISafetyGate` and inject it into `Messenger` in the lazily constructed `WaConnection.Messaging` property, then have every public send call `await _safety.ExecuteAsync(chat, recipientWasContacted, cancellationToken, inner: () => _sender.SendAsync(...))` before returning. The inner send remains the sole owner of sockets, Signal, and ACK correlation; the gate owns denial and pacing outcomes.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Mandatory send gate + policy checks | API / Backend (SDK process) | — | Ban policy is in-process logic that must be unkozable by hosts; putting it in a hosted backend or middleware would let hosts disable it |
| Token buckets, warm-up, cold budgets, circuit breaker | API / Backend (SDK process) | Database/Storage (host store via `ISafetyStateStore`) | State ownership is in-process but durability is explicitly host persistence by Phase 1's seam [VERIFIED: .planning/phases/01-foundation-store-seam/01-CONTEXT.md D-01..D-05] |
| Typed safety events | API / Backend (SDK process) | Host UI / downstream bus | Phase 4 already established the `EventHandler` + bounded `WhatsAppEvent` two-shape stream; Phase 5 only extends that vocabulary [VERIFIED: src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs:110-149, MessagingEvents.cs:177-193] |
| Presence / active-hours window effects | API / Backend (SDK process) | WhatsApp Web client / server | Phase 8 later owns the full presence contract; Phase 5 only emits pacing using a minimal best-effort presence node or a no-op fallback |
| Store persistence of safety and recipient-ledger facts | Database/Storage (host implementation) | SDK references in tests | The SDK never installs a DB or owns persistence by PROJECT.md constraint |

## Standard Stack

### Core

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| In-box `System.Threading.RateLimiting` | 10.0.12 (runtime package) | `TokenBucketRateLimiter` for minute/hour/day windows | Already locked in `STACK.md`; no host install and no async ceremony required at the allocation site |
| In-box `System.Threading.Channels` | 10.0.12 (ships with .NET 10) | Bounded queue for deferred retryable sends, if `QueueDuringInactiveHours` is ever enabled | Phase 4 already uses bounded channels for typed event backpressure [VERIFIED: Messenger.cs:55-61] |
| In-box `System.Security.Cryptography` + `System.Random` | .NET 10 | Jitter and randomized pacing values | CSPRNG-backed `RandomNumberGenerator`; do not hand-roll PRNGs |
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.12 | `ILogger` only | Locked and required pattern; hosts own the pipeline [CITED: STACK.md "Core Technologies" row] |

### Supporting

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| `TimeProvider` (BCL) | .NET 8+ | Deterministic tests for rate-window rollover and jitter | Phase 4 already uses this for ACK windows and backoff [VERIFIED: WaConnection.cs:167, MessageSender.cs:796-800] |
| `System.Text.Json` | .NET 10 | Persistence of typed safety rows in host snapshot tests | Only for sample/test helpers; no production dependence |

### Alternatives Considered

| Recommended | Alternative | When to Use Alternative |
|-------------|-------------|-------------------------|
| In-box `TokenBucketRateLimiter` | `Polly.Core` resilience pipeline | Polly remains HTTP/media-only, per `STACK.md`: its retry vocabulary cannot express chat-level warm-up curves or ban-specific backoff. Do not wire Polly into `SafetyGate` |
| Hand-rolled in-memory circuit breaker | `Polly.Core` circuit breaker | Use Polly only for CDN/HTTP uploads where HTTP error codes are the signal. Protocol send failure takes its signal from `SendOutcome`, not HTTP status |
| Per-account `TokenBucketRateLimiter` | Custom sliding-window counters | Only if partitioned buckets cannot be reset on test boundaries; in-box limiter already supports `TryAcquireAsync` and lease release |
| New `IRecipientContactStore` interface | Reusing `IRecentMessageCacheStore` as the cold-contact test | Only if the recipient list is tiny and a 30-day horizon is acceptable; recent-cache semantics are lossy and purge-based, unlike warm-up/budget state, which must be must-not-lose |

**Installation:** no new packages.

```bash
# Phase 5 adds no new NuGet packages.
# System.Threading.RateLimiting, System.Threading.Channels, and Microsoft.Extensions.Logging.Abstractions
# are already referenced at version 10.0.12 by the workspace.
```

**Version verification:** Phase 5 uses only in-box libraries and extensions already pinned by `STACK.md` [CITED: .planning/research/STACK.md]. No registry probe was needed because no new package is introduced.

## Package Legitimacy Audit

| Dependency | Registry | Verdict | Disposition |
|------------|----------|---------|-------------|
| `System.Threading.RateLimiting` | In-box .NET 10 runtime | N/A (in-box) | Approved |
| `System.Threading.Channels` | In-box .NET 10 runtime | N/A (in-box) | Approved |
| `System.Text.Json` | In-box .NET 10 runtime | N/A (in-box) | Approved |

**Packages removed due to [SLOP] verdict:** none.
**Packages flagged as suspicious [SUS]:** none.
**No external packages are installed by Phase 5.**

## Architecture Patterns

### System Architecture Diagram

```text
Host (public API)
  -> Messenger.SendMessageAsync / SendGroupMessageAsync / ReplyAsync
      -> EnsureOwnPrekeysAsync (existing 04-03 trigger)
      -> SafetyGate.ExecuteAsync(chat, SafetyDecisionContext, cancellationToken, innerSend)
          -> 1. CircuitBreaker.ThrowIfOpen (or wait under backoff)
          -> 2. ActiveHours.AssertInsideWindow (typed refusal outside window)
          -> 3. RateLimiters (minute -> hour -> day)
          -> 4. WarmupLadder.Admit (daily ceiling based on persisted account age)
          -> 5. ColdOutreachBudget (hard block if recipient never-contacted AND cold cap reached)
          -> 6. Jittered delay (randomized 35-65% of base; TimeProvider)
          -> 7. Presence pacer (typing/recording pause, best-effort)
          -> 8. inner send (MessageSender SendAsync/SendGroupAsync/SendPayloadAsync delegate)
          -> 9. CircuitBreaker.RecordOutcome / WarmupLadder.RecordSend / ColdOutreachBudget.MarkContacted
          -> return SendOutcome
  -> delayed or queued messages use existing bounded Channel<T> if explicitly enabled
```

Protocol-generated outbound nodes are not flowing through the shape above: `MessageDispatcher.SendAckAsync`, `ReceiptHandler` emissions, and the retry-resend in `ResendForRetryReceiptAsync` remain protocol traffic and appear in the whitelist below.

### Recommended Project Structure

```text
src/Iwasug.WhatsApp.Sdk/
├── Safety/
│   ├── ISafetyGate.cs
│   ├── SafetyGate.cs
│   ├── SafetyGateException.cs
│   ├── SafetyOptions.cs
│   ├── TokenBucketRateLimiterSet.cs
│   ├── WarmupLadder.cs
│   ├── ColdOutreachBudget.cs
│   ├── CircuitBreaker.cs
│   ├── HumanPacing.cs
│   ├── CircuitState.cs
│   └── SafetyHealthRecord.cs
└── Messaging/
    ├── Messenger.cs                 # three public send paths call the gate
    └── SafetyEvents .cs             # BudgetExhausted/ThrottleEngaged/AccountRestricted twins
```

### Pattern 1: Mandatory Gate Seam (`SAF-01`)

**What:** `Messenger` owns a `SafetyGate _safety` created under its existing constructor path. Every public send passes `innerSend`:

```csharp
public async Task<SendOutcome> SendMessageAsync(Jid chat, string text, CancellationToken cancellationToken = default)
{
    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
    await EnsureOwnPrekeysAsync(cancellationToken).ConfigureAwait(false);

    return await _safety.ExecuteSendAsync(
        new SendToken(chat),
        cancellationToken,
        inner: () => _sender.SendAsync(chat, text, cancellationToken)).ConfigureAwait(false);
}
```

The same pattern applies to `SendGroupMessageAsync` and `ReplyAsync`. Cancellation is passed through unwrapped, consistent with existing Phase 4 discipline [VERIFIED: Messenger.cs:164-170 and MessageSender.cs:119 argument contracts].

**Static audit test:** a phase-level `SendPathAuditTests` seeds `Messenger` and `MessageSender` and asserts one of the following:

- direct reflection over all calls to `WaConnection.SendNodeAsync` in `WhatsApp.Sdk`:
  - `MessageSender` line 792 in `AwaitAckAsync` is the *inner send* delegate;
  - `MessageSender` line 561 in `ResendForRetryReceiptAsync` is whitelisted as `ProtocolEgress.RetryResponder`;
  - `MessageDispatcher.SendAckAsync` is whitelisted as `ProtocolEgress.Ack`;
  - `ReceiptHandler` emissions are whitelisted as `ProtocolEgress.Receipt`.
- All other egress routes must be added to a documented enum value rather than omitted.

No protocol send may be created outside this audit enum; the test exits non-zero if a new `SendNodeAsync` call site appears.

### Pattern 2: Phase 1 Seam Reuse + Recipient Ledger (`SAF-03`, `SAF-04`)

Existing store seam [VERIFIED: .planning/phases/01-foundation-store-seam/01-CONTEXT.md D-01/D-02/D-05]:

- `ISafetyStateStore` is **already** a granular, per-account, must-not-lose seam.
- `SafetyStateRecord` already carries `CreatedAt` and `WarmupStage` [VERIFIED: Domain/SafetyState.cs:1-22].

**Consequence:** do not add a second account-level safety row. The gate reads `SafetyStateRecord.CreatedAt` (the durable first-link timestamp) and writes stage changes through `PutAsync` with D-06 awaiting. The engine defines the warm-up ladder, but persistence stays at one durable row.

For cold-outreach eligibility, the Phase 1 choice (D-01 says five interfaces, but recipients are an unbounded list, not one mutable row) is a genuine extension point. Recommended additive seam:

```csharp
// stores/IRecipientContactStore.cs
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

Also add an `InMemoryRecipientContactStore` branch to `InMemoryStoreFactory` and contract tests. Tag this decision as a Phase 1 seam amendment, not an undocumented expansion [ASSUMED: the new interface was not in Phase 1's original five-store list; it is arguably scope expansion, so discuss-phase feedback is appropriate].

`recipientKey` must be deterministic and privacy-neutral: `Jid.BaseUser(chat) + "@" + chat.Server` (no display name, no message body), and may be salted only by host policy. The gate marks a recipient contacted only after an inner text send returns `Sent` or `TimedOut` (timeout still means the frame left the process).

### Pattern 3: Token-Bucket Policy (`SAF-02`)

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

    // _perHour and _perDay follow the same shape with hour/day windows.
}
```

Use values from `SafetyOptions`, not literals in `MessageSender`. Limit precedence is minute, then hour, then day, and the first non-acquiring window is reported as `BudgetExhausted(..., level: RateWindow)`. Randomized jitter is applied as an additional delay after acquire and before the inner send:

```csharp
TimeSpan delay = options.BaseInterSendJitter * (0.5 + RandomNumberGenerator.GetDouble() * (options.JitterRatioEnd - 0.5));
await Task.Delay(delay, clock, cancellationToken).ConfigureAwait(false);
```

For throttled events, use `SafetyGate` emitting `ThrottleEngaged` when any window take succeeds only after waiting beyond a `ThrottleNoticeThreshold` (for example 500 ms) and `BudgetExhausted` immediately when a window cannot acquire.

### Pattern 4: Warm-Up Ladder (`SAF-03`)

Recommended defaults [ASSUMED: these numeric thresholds are implementation heuristic, not official WhatsApp policy; treat them as tunable defaults]:

- Stage ladder (messages per day): [20, 40, 80, 160, 320, 640, 1000]
- Automatic progression: one stage/day from `SafetyStateRecord.CreatedAt` stores created at first pairing
- Floor on new accounts: first stage
- No stage downgrade triggered by a single send failure; structural throttle trips the breaker separately

Persistence points:

- Create safety state at the first observed paired credential (`row.PairedUser is not null`) if none exists.
- `WarmupStage` only changes after successful execution (kept in a single atomic update to one record).
- Do not recompute `CreatedAt` on restart; the store is source of truth.
- Warm-up uses the same clock (store load + `TimeProvider`) to avoid process-start clock fantasies.

### Pattern 5: Circuit Breaker (`SAF-05`)

Not Polly: the breaker exists on the send decision itself, not on the network edge.

State machine:

- `Closed`: sends flow.
- `Open`: all application sends throw/refuse immediately, raise `AccountRestricted`, and schedule next probe.
- `HalfOpen`: allow one probe send.
- Transitions:
  - On `SendOutcomeStatus.ServerError` and `TimedOut`, increment `_failureCount`.
  - On repeated failure (`FailureThreshold`), enter `Open`.
  - On `TimedOut` streak alone, the breaker trips immediately and raises `ThrottleEngaged`.
  - On a successful inner send, close and reset.

Signal mapping should remain heuristic because the SDK does not currently have an exhaustive WA Web error code table from official docs in this repo. Open questions below ask for this. The `MessageSender.SendPayloadAsync` return already gives `SendOutcome.Status` and `Error` [VERIFIED: MessagingEvents.cs:58-63 and MessageSender.AwaitAckAsync result].

Three required event type sets:

- EventHandler twins named/searchable in public evidence:
  `BudgetExhaustedEventArgs`, `ThrottleEngagedEventArgs`, `AccountRestrictedEventArgs`
- Stream subtypes (sealed records deriving `WhatsAppEvent`):
  `BudgetExhaustedEvent`, `ThrottleEngagedEvent`, `AccountRestrictedEvent`
- Keep fields minimal: account key, trigger (`Window`, `Error`, `Outcome`), duration or next allowed UTC, safety stage, CancellationToken-friendly shape.

### Pattern 6: Human-Like Pacing (`SAF-06`)

- Presence contract must not fail the send promise:
  - If the SDK knows how to send composing/recording presence, call it in a best-effort `try { ... } catch (OperationCanceledException) { throw; } catch { }`.
  - If it does not, keep the interface and a no-op fallback. Phase 8 can upgrade the implementation without touching the gate API.
- Active-hours window:
  - `SafetyOptions.ActiveHoursStart` / `ActiveHoursEnd` expressed in UTC by default (or host-provided `TimeZoneInfo`; document this as a decision).
  - Outside window behavior: by default `SafetyRuleViolationException` of type `InactiveHours`; optional `QueueDuringInactiveHours` with bounded `Channel` is infrastructure for Phase 8 outbox, not required for Phase 5.
- Randomized composing duration cap: for example 2,000-12,000 ms. Pause is applied via `TimeProvider` to remain testable. This token must be respected by the SDK's delay but must not be swallowed if canceled.

### Anti-Patterns to Avoid

- Only one mandatory gate in front of the `MessageSender`; never add another limit inside `DeviceDirectory` or `MessageSender`.
- Never generate the message id before the gate, then let the message fail; the gate rejection happens before ID generation.
- Never persist safety state silently in process memory only; store is source of truth.
- Never treat inbound-triggered replies as cold outreach. A recipient is marked contacted only when the account sends successfully.
- Never "ban-proof" language in docs, and never provide timing/throughput evasions to boost daily volume.
- Never use presence calls to fail a user outcome.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Rate windows and token availability | Custom rolling counters | `System.Threading.RateLimiting.TokenBucketRateLimiter` | Lease fairness, capacity edge cases, cancellation races, dispose behavior are maintained by runtime |
| RNG | Custom RNG for burst pacing | `System.Security.Cryptography.RandomNumberGenerator` | Avoid predictable jitter and subtle biases |
| Circuit breaker across HTTP only | Custom HTTP limiter for sends | In-process breaker on SendOutcome | WA error detection is protocol/semantic, not HTTP 429 |
| Total policy stack | `if` guards in `MessageSender` | Dedicated `SafetyGate` decorator | Easily audited and policy-free lower layers |

**Key insight:** The lower layers already have an idempotent send contract; the gate must fail before that contract, without introducing new partial side effects or ratchet advancement.

## Runtime State Inventory

Not applicable: Phase 5 is a new safety layer, not a rename/refactor/migration phase. Warm-up state is introduced per account and defaults to absence; no migration is required when enabling Phase 5.

## Common Pitfalls

### Pitfall 1: Bypassing the gate via a new public method

**What goes wrong:** Phase 6 media or future APIs add direct `Connection.SendNodeAsync` calls or new `Messenger` methods that skip `SafetyGate`.
**How to avoid:** Phase 6 plan must add any new outbound asset path to `SafetyGate.ExecuteSendAsync` (or an explicitly documented media passthrough Gate variant). Keep the `PublicAPI.Shipped.txt` diff review as the public-surface gate.
**Warning signs:** any new public method on `Messenger` that accepts a chat and writes without `_safety`.

### Pitfall 2: State absent after pairing

**What goes wrong:** New account without row uses default first link age relative to process time, so warm-up never resets.
**How to avoid:** On first paired credential load, create a safety row using `CreatedAt = _connection.Clock.GetUtcNow()` and persist. Restart recovery loads it.
**Warning signs:** `WarmupStage` remains 0 after restart.

### Pitfall 3: Cold budget double counting replies

**What goes wrong:** Every reply to an inbound message consumes cold outreach budget, nullifying it.
**How to avoid:** Cold budget is only consulted for `sendMessageAsync` and `ReplyAsync` to a chat with **no recipient contact**, but mark on successful outbound even for replies (if truly never contacted). For inbound-initiated replies, the caller can pass `recipientWasContacted` from the reply check? Definition: recipient considered contacted only after a prior account send; initial replies still become first-contact but are exempt from cold blocking. Keep `ReplyAsync` exempt from cold `BudgetExhausted` as a semantic policy.

### Pitfall 4: "Jitter" as sleep in process

**What goes wrong:** Long `Task.Delay` prevents shutdown under restart.
**How to avoid:** Always cancellation-aware wait; inject `TimeProvider` and a max pacing cap; test with fake clock.

### Pitfall 5: Event ordering

**What goes wrong:** Gate raises `BudgetExhausted` before awaiting cancellation and duplicates.
**How to avoid:** Emit once per transition; if cancelled, no additional event. Handler contracts remain non-blocking.

### Pitfall 6: recipient key leakage

**What goes wrong:** Persisting raw phone/JID in recipient store may leak in third-party host logs.
**How to avoid:** Keys use bare JID only; typed record stores hash? But host needs resolve. JID strings are expected at persistence tier by `D-04`; host database already contains them in message/call operations. Clarify docs.

## Code Examples

### Gate invocation shape

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

### recipient ledger seam

```csharp
public interface IRecipientContactStore : IAsyncDisposable
{
    Task<bool> HasContactedAsync(string recipientKey, CancellationToken cancellationToken);
    Task MarkContactedAsync(string recipientKey, CancellationToken cancellationToken);
}
```

### safety options (`SAF-07`)

```csharp
public sealed record SafetyOptions(
    int MaxMessagesPerMinute,
    int MaxMessagesPerHour,
    int MaxMessagesPerDay,
    int MaxColdOutreachPerDay,
    double JitterRatioBegin,
    double JitterRatioEnd,
    TimeSpan ActiveHoursStart,
    TimeSpan ActiveHoursEnd,
    bool QueueDuringInactiveHours,
    bool AllowUnsafeThroughput)
{
    public static SafetyOptions SafeDefaults { get; } = new(
        20,
        250,
        1000,
        30,
        0.4,
        0.9,
        TimeSpan.Zero,
        TimeSpan.FromHours(24),
        false,
        false);

    internal static SafetyOptions Validate(SafetyOptions? incoming)
    {
        SafetyOptions resolved = incoming ?? SafeDefaults;
        if (!resolved.AllowUnsafeThroughput)
        {
            if (resolved.MaxMessagesPerMinute > 60
                || resolved.MaxMessagesPerDay > 300
                || resolved.JitterRatioBegin > resolved.JitterRatioEnd)
            {
                throw new ArgumentOutOfRangeException(...);
            }
        }

        return resolved;
    }
}
```

## State of the Art

| Old Approach | Current Approach | Impact |
|--------------|------------------|--------|
| Host add-ons only (baileys-antiban, whatsmeow-antiban) | In-library, default-on gate | SDK differentiator and single mandatory path |
| Random sleep in thread | TimeProvider-aware cancellation-safe delay | Test determinism + graceful shutdown |

**Deprecated/outdated:**
- None added by this research.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Warm-up ladder values (5 day-ish stages, 20/40/80/160/320/640/1000) | Architecture Patterns, Pattern 4 | Defaults can be tuned; they are operational heuristics, not official WhatsApp policy |
| A2 | Cold outreach daily cap (30/day) and warmup levels (20/day new accounts) | Standard + examples | Too strict might block legitimate early use; too loose weakens v1 goal |
| A3 | Composing/recording presence node name and attrs | Pattern 6 | Must verify before implementation; if wrong, limit to no-op pacing |
| A4 | Inactive hours rejection rather than buffering for 24+ hours | Pattern 6 | Hosts may require silent queueuing; Phase 8 adds outbox |
| A5 | Need for new `IRecipientContactStore` | Pattern 2 | May be over-engineering and scope expansion beyond five existing stores |
| A6 | WA throttle server error mapping | Pattern 5 | Without an exhaustive response code dictionary, breaker reliance will be coarse |

## Open Questions

1. **What WhatsApp error codes on `SendOutcome` indicate protocol throttling or tempary restriction?** The SDK receives `error` on ack and `SendOutcomeStatus.ServerError`, but the documented table of relevant values is not in this repository. Research needed during implementation [ASSUMED].
2. **Do we implement presence nodes now or wait for Phase 8?** The presence stanza shape is not implemented anywhere in the current code (search returned zero matches [VERIFIED: repository search for "composing" had zero results]). Recommendation: define the gate's `IPacingPresence` seam with no-op fallback, then wire real presence either in Phase 5 with an [ASSUMED] shape or in Phase 8.
3. **Should warm-up progression happen by calendar day only, or be promoted by reply volume?** Phase 5 can implement day-based progression; promotion on reply quality is deferred as a Phase 9 feature [ASSUMED].
4. **Does `AllowUnsafeThroughput` change validation thresholds only, or also gate on an explicit assertion?** Recommend requiring `true` plus either `SafetyHeaders` declared-host acknowledgment or a build-time warning.

## Environment Availability

No external dependencies are added. The environment has .NET 10 SDK/runtime from `STACK.md`; no new CLI dance or system service required [CITED: STACK.md "Version Compatibility" section].

<|message|> Tool | Requirement | Available | Notes |
|------|-------------|-----------|-------|
| .NET 10 SDK (10.0.401+) | Build/run | ✓ in workspace status | Confirmed by existing Phase 4 validation runs |
| xunit runner/MTP | Test execution | ✓ | `dotnet test .Tests.dll` workaround is needed in this environment; note that in repository docs |

## Validation Architecture

Validation strategy is enabled from `.planning/config.json` (no `nyquist_validation: false`) per phase instructions.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 4.0.1 |
| Config file | none designated (tests under `tests/Iwasug.WhatsApp.Sdk.Tests`) |
| Quick run command | `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~Safety"` |
| Full suite command | `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror --nologo && dotnet test tests/Iwasug.WhatsApp.Sdk.Tests` |
| Clock seam | `TimeProvider` (`_timeProvider`) already injected |
| Fakes | NSubstitute 6.2.0 for stores and gate dependencies |

### Phase Requirements -> Test Map

| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|-------|----------|-----------|-------------------|--------------|
| SAF-01 | every public send call invokes `_safety.ExecuteSendAsync` before inner send | unit | `dotnet test ... --filter "SendPathAuditTests"` | ❌ Wave 0 |
| SAF-01 | static audit: all `SendNodeAsync` sites are gated or whitelisted | static test | `dotnet test ... --filter "SendPathAuditTests"` | ❌ Wave 0 |
| SAF-02 | token windows reject/acquire at 20/min, 250/hr, 1000/day | unit | `dotnet test ... --filter "TokenBucketRateLimitTests"` | ❌ Wave 0 |
| SAF-02 | randomized jitter stays in bounded range | unit | `dotnet test ... --filter "SafetyJitterTests"` | ❌ Wave 0 |
| SAF-03 | account-age uses persisted safety state on restart | contract/integration | `dotnet test ... --filter "WarmupPersistenceTests"` | ❌ Wave 0 |
| SAF-03 | new account starts first warmup stage; stage advances by elapsed account age | unit | `dotnet test ... --filter "WarmupLadderTests"` | ❌ Wave 0 |
| SAF-04 | first contact to a chat is counted against cold budget; blocked at cap before inner send | unit | `dotnet test ... --filter "ColdOutreachBudgetTests"` | ❌ Wave 0 |
| SAF-04 | inbound reply does not consume protected cold budget | unit | `dotnet test ... --filter "ColdOutreachReplyExemptionTests"` | ❌ Wave 0 |
| SAF-05 | repeated timeouts convert to open circuit then exponential backoff | unit | `dotnet test ... --filter "SafetyCircuitBreakerTests"` | ❌ Wave 0 |
| SAF-06 | active-hours check fires `SafetyRuleViolationException` outside target window | unit | `dotnet test ... --filter "HumanPacingTests"` | ❌ Wave 0 |
| SAF-07 | lower enhancement settings rejected without `AllowUnsafeThroughput=true` | unit | `dotnet test ... --filter "SafetyOptionsValidationTests"` | ❌ Wave 0 |

### Sampling Rate
- Per task commit: `dotnet build Iwasug.WhatsApp.Sdk.sln -warnaserror --nologo`
- Per wave merge: `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests`
- Phase gate: same full-suite gate plus manual `NOTICE.md` audit

### Wave 0 Gaps
- [ ] `tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SafetyGateTests.cs`
- [ ] `tests/Iwasug.WhatsApp.Sdk.Tests/Safety/CircuitBreakerTests.cs`
- [ ] `tests/Iwasug.WhatsApp.Sdk.Tests/Safety/SafetyOptionsValidationTests.cs`
- [ ] `tests/Iwasug.WhatsApp.Sdk.Tests/Stores/IRecipientContactStoreContractTests.cs` (if recipient ledger seam is approved)
- [ ] Gate public-surface guard in `PublicAPI.Unshipped.txt`

## Security Domain

Required because `security_enforcement` is not explicitly false in config.

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-------------------|
| V2 Authentication | no | already handled by pairing + Signal identity |
| V3 Session Management | no | session state persists only through existing store seam |
| V4 Access Control | no for single account; yes for multi-account later | account isolation is Phase 7 |
| V5 Input Validation | yes | `SafetyOptions.Validate`, JID and recipient key validation |
| V6 Cryptography | no new cryptographic needs | NSec and BCL primitives already locked by Phase 4 |

### Known Threat Patterns for .NET SDK

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Ungated egress | Elevation/Denial of Service | Mandatory gate plus audit test for all `SendNodeAsync` call sites |
| Wildcard recipients and message blasts | Privacy/Self-safety | Hard cold-outreach limit and typed exhaustion event |
| Automatic retry storms after timeout | Denial of Service | Gate admission before send; breaker plus no internal re-send beyond retry receipt |
| Loss of warm-up state on restart | Tampering/Loss | Durable store, first-link timestamp, `CreatedAt` persistence |
| Unsafe throughput public opt-in | Compliance | Explicit `AllowUnsafeThroughput`, no accidental threshold bump |

## Sources

### Primary (HIGH confidence)
- `.planning/REQUIREMENTS.md` - `SAF-01`..`SAF-07`
- `.planning/ROADMAP.md` - Phase 5 goal and success criteria
- `.planning/phases/01-foundation-store-seam/01-CONTEXT.md` - granular store seam and durability decisions
- `.planning/phases/04-signal-e2ee-messaging/04-03-PLAN.md` - send path, per-account `SemaphoreSlim(1,1)`, NO RateLimiter yet
- `.planning/phases/04-signal-e2ee-messaging/04-05-SUMMARY.md` - typed event stream vocabulary
- `src/Iwasug.WhatsApp.Sdk/Stores/ISafetyStateStore.cs` - per-account safety row contract [VERIFIED]
- `src/Iwasug.WhatsApp.Sdk/Domain/SafetyState.cs` - `SafetyStateRecord` [VERIFIED]
- `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs` - public send surfaces and events [VERIFIED]
- `src/Iwasug.WhatsApp.Sdk/Stores/InMemory/InMemoryStoreFactory.cs` - reference safety-state factory implementation [VERIFIED]
- `src/Iwasug.WhatsApp.Sdk/Connection/WaConnection.cs` - messaging access, clock, and store registry [VERIFIED]

### Secondary (MEDIUM confidence)
- `.planning/research/ARCHITECTURE.md` - gate decorator pattern and recommended send-path section
- `.planning/research/PITFALLS.md` - warm-up and cold-outreach guidance
- `.planning/research/FEATURES.md` - ban-safety feature inventory and honest risk stance
- `.planning/research/SUMMARY.md` - research synthesis
- `src/Iwasug.WhatsApp.Sdk/Messaging/MessageSender.cs` - inner send path and outcomes [VERIFIED]
- `src/Iwasug.WhatsApp.Sdk/Messaging/ReceiptHandler.cs` - protocol-generated egress [VERIFIED]

### Tertiary (LOW confidence)
- WS presence stanza behavior [ASSUMED] and WA error throttle mapping [ASSUMED]
- Numeric policy thresholds and cold-outreach caps [ASSUMED]
- `System.Threading.RateLimiting` API details from in-box BCL (verified by runtime packages, not re-run in this session)

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - all dependencies are already present at 10.0.12 in the workspace; no new package
- Architecture: HIGH for mandatory gate and seam reuse; MEDIUM for recipient ledger extension
- Pitfalls: HIGH for code paths actually inspected; MEDIUM for operational thresholds

**Research date:** 2026-10-14
**Valid until:** 2026-11-13
