# Phase 8: Broadcast & Extended Messaging - Research

**Researched:** 2026-10-16
**Domain:** Safe broadcast fan-out, durable per-account outbox, real presence stanzas, and richer message operations (reactions, edit, delete-for-everyone) over the already-gated E2EE send path
**Confidence:** MEDIUM-HIGH (scope/composition over locked Phase-1..7 seams is HIGH; protocol-level reaction/edit/delete field numbers and presence stanza payloads are [ASSUMED] pending a clean-room Baileys read, which this phase must repeat before implementing, Phase-4/6 precedent)

## Summary

Phase 8 is composition, not a new transport. Broadcast, outbox, presence, and message edits all reuse the per-account stack already shipped: `Messenger`'s gated `SendMessageAsync` path, `SafetyGate.ExecuteSendAsync` (SAF-01), `MessageBuilder`/`MessageSender` fanout, the dispatcher's D-06 persist→ack→event→cache ordering, `StoreRegistry` account-scoped stores, `AccountRuntime` queues and `SessionManager`. The single most important invariant from the requirements is negative: **broadcast has no independent send loop** - every recipient is a separate 1:1 send that traverses the gate exactly once, sequentially, so warm-up, cold-outreach, and token-bucket accounting stay honest (BCT-01). The same rule applies to outbox pumps, presence pacing upgrades, and reaction/edit/delete: none of them invent a parallel egress path.

The durable outbox is the only part that introduces a new persistence seam. It must follow Phase-1 discipline (granular interface, SDK-owned policy, primitive ops, host owns storage, in-memory reference store, contract kit) rather than tucking a queue behind a static `Channel`. Its payload model, however, is the one genuinely open design question in this phase: durable persistence of text-bearing messages is straightforward, while durable persistence of *media* messages would require persisting upload descriptors and is best time-boxed explicitly.

The biggest assumption-weighted items are reaction/edit/delete and presence semantics. The Baileys token table already carries the wire tokens needed (`reaction`, `edit`, `delete`, `presence`, `composing`, `paused`, `available`, `unavailable`) and Phase-5 already shipped the `IPacingPresence` seam expressly for Phase-8 upgrade. This research recommends keeping all three as ordinary gated E2EE message sends with small `Message` schema additions, and exposing typed events on inbound, following Phase-6's clean-room provenance rules (field numbers read from Baileys @ `0af2386` and pinned in a provenance header before any generated code lands).

**Primary recommendation:** Plan Phase 8 as four plans mirroring Phase 6 — (1) presence upgrade (swap the no-op seam for real nodes + incoming presence parser, wiring safety pacing), (2) reaction/edit/delete (schema additions with provenance header + builders + inbound events), (3) broadcast fan-out API + outcome report (no new egress), (4) durable outbox seam + pump + dead-letter, then safety/store/audit closeout. Nothing in this phase needs a new NuGet package.

## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| BCT-01 | `SendBroadcastAsync` fans out as individual 1:1 sends through the safety gate + warm-up budget and returns a per-recipient outcome report (no independent send loop) | Sections: Architecture Patterns — broadcast fan-out pattern; Standard Stack (no new packages); Common Pitfalls (blast-shaped API); Test map |
| EXT-01 | Durable per-account outbox: enqueue → rate-limit → send → backoff → dead-letter, queue storage via the store abstraction | Sections: Architecture Patterns — outbox seam/pump; Don't Hand-Roll (pluggable store); Open Question 1 (media payloads) |
| EXT-02 | Presence API: send/observe `available`/`unavailable`/`composing`/`recording`/`paused` + peer presence | Sections: Architecture Patterns — presence upgrade; Pitfalls (always-available default is an anti-feature) |
| EXT-03 | Host can send, receive, and remove reactions | Sections: Architecture Patterns — reaction builders/inbound events; Out of Scope boundary check |
| EXT-04 | Host can edit own messages and delete messages for everyone | Sections: Architecture Patterns — protocol-message builders; Pitfalls (idempotency / target attribution) |

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| `SendBroadcastAsync` enumeration + per-recipient outcomes | SDK library (per-account Messenger) | Safety gate (existing) | The loop lives in the SDK only as an enumeration over existing gated sends; recipients, personalization, and cancellation belong to the host but the egress policy stays SDK-owned |
| Broadcast warm-up / cold-outreach accounting | SDK safety layer (per account) | Store (recipient contact) | Contacts are marked through the same seam used by `SendMessageAsync`; no broadcast-local budget |
| Durable outbox storage | Host persistence (via store seam) | SDK library (pump policy) | Host owns durable rows (Phase-1 constraint); SDK owns enqueue→rate-limit→send→backoff→dead-letter semantics and provides the in-memory reference store |
| Outbox pump + backoff / dead-letter | SDK library (per-account) | Host lifecycle (SessionManager) | One pump per account; rate-limiting never crosses account boundaries (MUL-02) |
| Presence wire signaling (send) | SDK library / gated messaging facade | Safety pacing (internal seam) | Real presence replaces `NoOpPacingPresence`; outgoing pacing liveness still feeds safety |
| Peer presence inbound | SDK dispatcher (per-account) | Host consumer (EventHandler + stream) | Inbound presence facts follow the two-shape precedent (`EventHandler` + `ReadEventsAsync`) |
| Reactions / edit / delete (outbound) | SDK library (E2EE message builders) | Safety gate | They are ordinary encrypted sends; the target message is carried as an E2EE `Message` branch through the same gate |
| Reactions / edit / delete (inbound) | SDK dispatcher (per-account) | Host consumer | Typed events, no host-side crypto |
| Store seam extension (`IOutboxStore`) | Host (implementation) | SDK (contract kit + in-memory store) | Granular store interfaces are the project's persistence contract; new seams need contract-test coverage |

## Standard Stack

No new package. Phase 8 deliberately reuses the locked train:

| Component | Version | Purpose | Notes |
|-----------|---------|---------|-------|
| `net10.0` runtime + existing SDK core | Phase-7 HEAD | Per-account connection, messaging, safety, stores, manager | Composition only |
| `System.Threading.Channels` (in-box) | .NET 10 | Outbox pump input + event fan-out | Already used by `Messenger` and `AccountRuntime.OutboundQueue` |
| `System.Threading.RateLimiting` (in-box) | 10.0.12 | Outbox sends inherit the gate's buckets; no new limiter | Reuse `SafetyGate` |
| `System.IO.Pipelines` (in-box) | 10.0.12 | Unchanged; no new framing | — |
| `Google.Protobuf` + `Grpc.Tools` | 3.36.2 / 2.84.0 | Additive `Message` branches for reaction + protocol (edit/revoke) only | Same `PrivateAssets=all` + `internal_access` discipline as Phase 6; field numbers verified against Baileys before codegen |
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.12 | Rule-only logging for outbox/presence/extended messaging; no payloads | Same as every phase |
| `TimeProvider` | in-box | Outbox next-attempt scheduling and tests | Injected in the pump exactly like Phase-5 `HumanPacing` |

**Installation:** none. No `Directory.Packages.props` changes are expected. If a host-implemented outbox adapter for a specific durable technology (e.g., a SQL store) is later published *as a sample*, it is its own artifact, not a core-project dependency.

### Package Legitimacy Audit

| Situation | Finding |
|-----------|---------|
| New external packages introduced by Phase 8 | **none** — reuses only packages already audited in Phase 1/6/7 |
| Re-verification needed | none; all package pins remain on the 10.0.x servicing train (10.0.12) |

Packages **removed** due to a `[SLOP]` verdict: none. Packages **flagged** `[SUS]`: none.

## Architecture Patterns

### System Architecture Diagram

```
Host
  |
  v
SessionManager (one per process)
  |
  +-- AccountRuntime (per account, hard-isolated)
        |
        +-- StoreSession (host-or-inmemory)
        |     +-- IRecentMessageCache (existing)
        |     +-- IOutboxStore      (NEW seam, Phase 8 EXT-01)
        |     +-- IRecipientContact (existing, SAF-03)
        +-- WaConnection (existing)
        +-- Messenger (existing)            <-- SendBroadcastAsync / presence / reaction / edit / delete land here
        |     +-- SafetyGate (mandatory)  <-- borrowed as-is
        |     +-- MessageSender            <-- 1:1/group fanout + node assembly
        |     +-- PresenceManager          <-- NEW, upgrades NoOpPacingPresence
        |     +-- OutboxPump               <-- NEW, one per account, store-backed
        +-- MessageDispatcher (existing)    <-- ack / receipt / presence / reaction / proto inbound
```

Data flow for `SendBroadcastAsync(chatList)`:
1. Host → `Messenger.SendBroadcastAsync(recipients, contentFactory, ct)`
2. For each recipient (sequential, in host-supplied order): build text, `EnsureOwnPrekeysAsync` (latched), then `_safety.ExecuteSendAsync(SendContext(recipient,...), ct, ct => _sender.SendAsync(recipient, text, ct))`
3. Each gated send settles exactly one `SendOutcome`; record it in the per-recipient outcome list
4. Safety exceptions propagate after recording the cause for the current recipient decision point; cancellation propagates (repo cancel-passes-through discipline)
5. No separate queue, no retry, no `WhenAny` parallelism — the gate already owns timing, jitter, and outcome

Data flow for outbox:
1. Host `EnqueueAsync(envelope, ct)` → durable `IOutboxStore.EnqueueAsync` → returns an entry id; **never** sends synchronously
2. Account pump wakes → loads due entries → for each, re-runs `EnsureOwnPrekeysAsync + _safety.ExecuteSendAsync + Messenger SendAsync` exactly as above
3. Sent: `MarkSentAsync`; accepted ack becomes `Sent`, timed out becomes retried-later, thrown / permanently-failed becomes dead-letter after the configured ceiling
4. Pump backoff is store-local (next-attempt timestamp), so it survives restarts and composes with the gate's own refusals

### Recommended Project Structure

```
src/Iwasug.WhatsApp.Sdk/
├── Messaging/
│   ├── Messenger.cs                    (ADD broadcast / presence / reaction / edit / delete APIs)
│   ├── MessageBuilder.cs               (ADD UpdateContext / reaction / proto branches)
│   ├── MessagingEvents.cs              (ADD PeerPresence*, Reaction*, MessageEdited*, MessageDeleted*, Outbox*?)
│   ├── Presence/
│   │   ├── PresenceManager.cs          (NEW — real node signer, IPacingPresence impl)
│   │   └── PresenceState.cs            (NEW — Available/Unavailable/Composing/Recording/Paused)
│   └── ExtendedMessage... (or keep builders in MessageBuilder)
├── Stores/
│   ├── IOutboxStore.cs                 (NEW granular seam)
│   ├── IOutboxStoreFactory.cs          (NEW)
│   └── InMemory/InMemoryOutboxStore.cs (NEW reference impl)
├── Accounts/
│   ├── OutboxPump.cs                  (NEW per-account)
│   └── AccountRuntime.cs               (wire Optional pump; leave Channel seam for status depth)
├── Media/ ... (unchanged)
└── Proto/message.proto                (ADD reaction + protocol branches only)
```

### Pattern 1: Broadcast fan-out (BCT-01)

**What:** an `IReadOnlyList<BroadcastRecipientOutcome>` over individually gated 1:1 sends.
**When to use:** every multi-recipient send. There is no other multi-send entry point.

```csharp
public sealed record BroadcastRecipientOutcome(
    Jid Recipient,
    SendOutcome Result);

public sealed record BroadcastOptions(
    // Per-recipient personalization is via the factory; this only carries loop-level knobs.
    TimeSpan? InterRecipientDelay = null);

public async Task<IReadOnlyList<BroadcastRecipientOutcome>> SendBroadcastAsync(
    IEnumerable<Jid> recipients,
    Func<Jid, string> messageFactory,
    BroadcastOptions? options = null,
    CancellationToken cancellationToken = default)
```

Rules (binding):
- `recipients` is enumerated exactly once and de-duplicated by ordinal JID string comparison; empty recipient set returns an empty result (no exception)
- `messageFactory` is invoked lazily per recipient, inside the loop, *after* the cancellation check, so cancellation stops further calls
- each recipient is an independent `SendMessageAsync`-shaped send through `_safety.ExecuteSendAsync` with `SendContext(recipient, IsReply: false)`; nothing goes through a side channel
- an optional `InterRecipientDelay` (default 0) is a *delay*, never a send, and is applied between recipients only when the caller supplies it; the gate still adds its configured jitter
- warmed-up (already-known) recipients and cold-outreach accounting flow through `IRecipientContactStore` unchanged; `AllowUnsafeThroughput` and `WarmupLadder` still govern new accounts
- cancellation stops the loop without settling any remaining recipient with a synthetic outcome (host observes the exception, repo discipline)

### Pattern 2: Durable outbox (EXT-01)

**What:** a new granular seam `IOutboxStore` + a per-account `OutboxPump`, with in-memory reference implementation.
**Primitives only (Phase-1 discipline):** the store exposes queue row operations; retention / attempt ceilings live in SDK-owned `IOptions<OutboxOptions>`, never in the store interface.

```csharp
public sealed record OutboxEntry(
    string Id,                    // client-generated message id (3EB0-style, 40 hex)
    Jid Destination,
    OutboxPayload Payload,         // discriminated: Text / GroupText / Reply / Reaction / ...
    DateTimeOffset EnqueuedUtc,
    OutboxState State,            // Pending / InFlight / Sent / RetryScheduled / DeadLettered
    int Attempt,
    DateTimeOffset? NextAttemptUtc,
    string? LastErrorRuleName);

public interface IOutboxStore : IAsyncDisposable
{
    Task<OutboxEntry> EnqueueAsync(OutboxEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<OutboxEntry>> GetDueAsync(int max, CancellationToken cancellationToken);
    Task MarkSentAsync(string id, CancellationToken cancellationToken);
    Task ScheduleRetryAsync(string id, int attempt, DateTimeOffset nextAttemptUtc, string? ruleName, CancellationToken cancellationToken);
    Task DeadLetterAsync(string id, string? reason, CancellationToken cancellationToken);
    Task<bool> TryRemoveAsync(string id, CancellationToken cancellationToken);
}
```

Rules:
- `EnqueueAsync` completes only after a subsequent read observes the row (D-06 durability) or throws (missing ≠ failure, D-19)
- the pump owns every transition; the store does not know the recipient's send policy
- `OutboxPayload` for media carries the media **upload descriptor only** (already-uploaded URL/direct path/media key + kind), not bytes; host-supplied media streams are never queued durably. Time-box deciding whether media outbox ships in v1 or is documented as in-memory-only (Open Question 1)
- dead-letter entries are retained and queryable; host is the only remover through `TryRemoveAsync`
- a row removed from the durable store is the only removal; the pump never keeps a private shadow list

Pump rules:
- one pump per account, owned by `AccountRuntime`; not started by `SessionManager` until the account is `Open`
- concurrency = 1; per-entry timeout comes from the gate + connection; cancellation is per-account CTS + caller token
- classified outcomes: `Sent`/`TimedOut`/`ServerError` → retry; any thrown `SafetyRuleViolationException` from rule violations (InactiveHours, etc.) → schedule against next window, not dead-letter; truly permanent inputs (empty destination, unknown payload kind, store faults after retries) → dead letter
- exponential backoff with jitter, store-side `NextAttemptUtc`; pump polls with a jittered floor but recomputes from rows, so restarts lose no delay
- gate refusal (circuit open / budget cold start) naturally causes schedule-then-retry with `NextAttemptUtc` clamped to the breaker's/next window's probe time; the pump never sleeps waiting on one recipient

### Pattern 3: Presence (EXT-02)

**What:** upgrade Phase-5's internal `IPacingPresence` seam from a no-op into a real `PresenceManager`, add public `SendPresenceAsync`, and parse inbound `<presence>` stanzas into typed events.

```csharp
public enum PresenceState { Available, Unavailable, Composing, Recording, Paused }

public Task SendPresenceAsync(PresenceState state, Jid? chat = null, CancellationToken cancellationToken = default);
public event EventHandler<PresenceUpdatedEventArgs>? PresenceUpdated;   // peer changes (inbound)
public event EventHandler<PresenceAppliedEventArgs>? PresenceChanged;   // our applied state
```

Rules:
- `Available`/`Unavailable` presence is sent to `s.whatsapp.net` (account-level); `Composing`/`Recording`/`Paused` are chat-targeted and carry a `<chatstate>` node wrapping the matching token (`composing`/`paused`) learned from the Baileys token table already in repo
- persistence of an "always available" (always-`available`) default is an **explicitly documented anti-feature**; the SDK defaults to not sending any presence at all until a host calls the API, and documents that a desktop-active client emits `unavailable` to keep phone notifications flowing
- wire signals remain best-effort for safety pacing (`SendComposingAsync(bestEffort:true)` contract is unchanged); only explicit host calls fail closed
- inbound `<presence ... type="unavailable">` and peer typing nodes route through the dispatcher and raise the event; never block receive or downgrade on parse failures
- presence is not safety-gated traffic (pacing hooks call it best-effort inside the gate but only after admission); explicit host calls go through the gate as well, for accounting parity

### Pattern 4: Reactions, edit, delete (EXT-03, EXT-04)

**What:** three ordinary E2EE sends — reaction (a `Message` with a reaction branch), edit (a protocol message carrying the new body + target key), revoke/delete-for-everyone (a protocol message with the target key).

Required schema additions (compile-time, run-time):
- add `ReactionMessage` and `ProtocolMessage` branches to `Message` under field numbers pinned to Baileys `WAProto/WAProto.proto @ 0af2386` (currently absent), with the Phase-6 provenance header duplicated verbatim style
- inbound dispatch recognizes and raises:
  - `ReactionReceivedEventArgs { Chat, TargetMessageId, Sender, Emoji, Timestamp }` (empty emoji == removal)
  - `MessageEditedEventArgs { Chat, MessageId, NewText, Timestamp }` (only delivered when a valid target+body arrives)
  - `MessageDeletedEventArgs { Chat, MessageIds, Remover, Timestamp }`

Public APIs:

```csharp
Task<SendOutcome> SendReactionAsync(Jid chat, string targetMessageId, string? emoji, CancellationToken = default);
Task<SendOutcome> EditOwnMessageAsync(Jid chat, string originalMessageId, string newText, CancellationToken = default);
Task<SendOutcome> DeleteForEveryoneAsync(Jid chat, string targetMessageId, CancellationToken = default);
```

Rules:
- each call builds bytes through `MessageBuilder`, `EnsureOwnPrekeysAsync`, and `_safety.ExecuteSendAsync(new SendContext(chat, IsReply:false))`, exactly the text-path shape; chat-state typing for reactions follows the pacing seam
- `emoji == null || ""` removes the reaction; a null removal is a real (empty) reaction send, never a no-op
- edit and delete target only keys this account can author (1:1 from-me or group-authored content appropriate to its participant); mismatched ownership surfaces `SdkFormatException` before any node
- target message ids must be present in the recent cache for context validation before node assembly; a missing target id throws `SdkFormatException` — never sends a dangling protocol message
- edits and deletes are **not** idempotent at the wire level by SDK rule: re-calling creates a new send with a new message id, consistent with MSG-03 "one logical send = one network send, never silently retries"
- inbound edited messages must be delivered with the edited text and a flag/field so hosts can replace rather than append; deleted messages surface as typed removals, not as "empty" message events
- grouped targets carry the group participant attribute on the ack (Pattern 7 precedent in the dispatcher)

### Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Broadcast timing / pacing | A separate scheduler or timer loop per recipient list | Existing per-send jitter + optional inter-recipient delay | A second timing system is exactly the "independent send loop" SC-1 forbids |
| Durability of queued sends | In-memory `Channel<OutboundEnvelope>` alone (07 scaffolding) | Pluggable `IOutboxStore` with row durability | `Channel` dies with the process; requirement is restart survival |
| Outbox policy | Retention/attempts in store interfaces | SDK-owned `OutboxOptions` (D-08) | Host implementers must not own retention policy (T-01-07) |
| Presence node re-assembly | A bespoke presence state machine | Upgrade the existing `IPacingPresence` seam and `HumanPacing` | Phase 5 deliberately left this seam exactly for this change |
| Reactions via side-channel messages | Outside-messaging "quick link" bypassing the message wire | Reaction/download as a `Message` branch | Bypassing E2EE breaks plaintext-never-on-wire discipline and audit rows |
| A `SendBulkAsync`-shaped convenience | A single call that hides per-recipient policy | `SendBroadcastAsync(recipients, factory)` with per-recipient outcomes | The shape itself teaches the ban-triggering pattern (PROJECT.md anti-feature) |

## Common Pitfalls

### Pitfall 1: A "fan-out considered one send" accounting bug
**What goes wrong:** one cold-outreach allowance consumed for the whole list, or warm-up counts 1 per list rather than per recipient.
**Why it happens:** treating the loop as an optimization inside one gate call.
**How to avoid:** each recipient is an entire `_safety.ExecuteSendAsync` call (SAF-01 proof); per-recipient contact-store marks happen inside `RecordOutcome`.
**Warning signs:** budget exhaustion events for an entire list appearing as one refusal.

### Pitfall 2: Parallel fan-out under "performance"
**What goes wrong:** simultaneous encryptions to many recipients defeat pacing and saturate CPU on one account.
**Why it happens:** `Task.WhenAll` over recipients looks faster.
**How to avoid:** sequential per recipient (release inter-recipient delay is caller policy, not concurrency); open questions capture whether to allow a capped parallelism later.
**Warning signs:** sustained high CPU + rate-limit saturation during broadcast.

### Pitfall 3: An ungated or conditionally-gated egress path in reactions
**What goes wrong:** a `SendProtocolMessageAsync` bypasses the gate under a "small message" exemption.
**Why it happens:** assuming small bytes == safe.
**How to avoid:** `SendPathAuditTests` extension must include the new APIs; mmg/retry whitespace stays whitelisted protocol egress only.
**Warning signs:** audit test count not increasing with the phase.

### Pitfall 4: Outbox that stores closures / live refs
**What goes wrong:** queued entries hold `Func` or stream handles that cannot survive restarts.
**Why it happens:** convenience over serialization.
**How to avoid:** `OutboxPayload` is a data discriminated union persisted by `IOutboxStore`; bytes are descriptors; a re-hydrated pump always reconstructs from data.
**Warning signs:** store implementers unable to round-trip `OutboxEntry`.

### Pitfall 5: Outbox dead-letter ambiguity
**What goes wrong:** time-outs, server errors, and invalid JIDs all look alike to the host.
**Why it happens:** one shared "failed" state.
**How to avoid:** record `LastSuccessfulStage` + rule name; route permanent validation failures to dead-letter immediately and transient send failures to retry.
**Warning signs:** hosts retrying invalid recipients forever.

### Pitfall 6: Presence leak across accounts
**What goes wrong:** one account's `available` propagates to another's outbound stanza.
**Why it happens:** a static presence table or ambient account context.
**How to avoid:** `PresenceManager` instance-owned per Messenger; no static stores (reuse Phase-7 isolation audit).
**Warning signs:** Phase-7 `NoStaticMutableStateTests` flake.

### Pitfall 7: "Always-available" default
**What goes wrong:** accounts appear online even when nobody is using them, causing phone push-suppression and behavior anomalies.
**Why it happens:** convenient default in WhatsApp Web clients.
**How to avoid:** exported as an anti-feature; default is to emit no presence.
**Warning signs:** user support for "phone stopped ringing".

### Pitfall 8: Edit/delete without ownership check
**What goes wrong:** a host requests an edit of someone else's message; wire rejection is late and costs a send.
**Why it happens:** trusting the caller's target id.
**How to avoid:** validate `fromMe` ownership against recent cache before node assembly; reject with `SdkFormatException` before the gate.
**Warning signs:** `ServerError` outcomes for edits on 1:1 test rows.

### Pitfall 9: Reaction removal misinterpreted as error
**What goes wrong:** empty emoji rejected as empty-message text.
**Why it happens:** reusing text-validation path.
**How to avoid:** reaction emoji goes in the reaction branch, not `Message.conversation`; empty is a valid removal.

## Code Examples

### Broadcast surface (sketch)

```csharp
// Source: repo safety discipline (SAF-01) + BCT-01 requirements
public async Task<IReadOnlyList<BroadcastRecipientOutcome>> SendBroadcastAsync(
    IEnumerable<Jid> recipients,
    Func<Jid, string> messageFactory,
    BroadcastOptions? options = default,
    CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
    ArgumentNullException.ThrowIfNull(recipients);
    ArgumentNullException.ThrowIfNull(messageFactory);

    await EnsureOwnPrekeysAsync(cancellationToken).ConfigureAwait(false);
    await foreach (Jid recipient in DeduplicateAsync(recipients, cancellationToken).ConfigureAwait(false))
    {
        cancellationToken.ThrowIfCancellationRequested();
        string text = messageFactory(recipient);
        SendOutcome outcome;
        try
        {
            outcome = await _safety.ExecuteSendAsync(
                new SendContext(recipient, IsReply: false),
                cancellationToken,
                ct => _sender.SendAsync(recipient, text, ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Classification of threw transport/safety failures is explicit;
            // no synthetic success; rethrow permanent direction errors per planner decision.
            throw;
        }

        _outcomes.Add(new BroadcastRecipientOutcome(recipient, outcome));
        TimeSpan delay = options?.InterRecipientDelay ?? TimeSpan.Zero;
        if (delay > TimeSpan.Zero) await Task.Delay(delay, Clock, cancellationToken).ConfigureAwait(false);
    }
    return result;
}
```

Note: the prekey latch `EnsureOwnPrekeysAsync` (already used by every send) stays outside the loop — safe because prekeys are per-account, not per-recipient; re-running per send would not change behavior but would re-latch needlessly.

### Outbox store skeleton (sketch)

```csharp
// Source: Phase-1 granular-store precedent (IRecentMessageCacheStore shape)
public interface IOutboxStore : IAsyncDisposable
{
    Task<OutboxEntry> EnqueueAsync(OutboxEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<OutboxEntry>> GetDueAsync(int max, CancellationToken cancellationToken);
    Task MarkSentAsync(string id, CancellationToken cancellationToken);
    Task ScheduleRetryAsync(string id, int attempt, DateTimeOffset nextAttemptUtc, string? ruleName, CancellationToken cancellationToken);
    Task DeadLetterAsync(string id, string? reason, CancellationToken cancellationToken);
    Task<bool> TryRemoveAsync(string id, CancellationToken cancellationToken);
}
```

### Presence factory sketch

```csharp
// Source: Baileys token table in-repo (TokenDictionary: "presence","composing","paused","available","unavailable")
public sealed class PresenceManager : IPacingPresence
{
    private readonly WaConnection _connection;
    private WaConnection RouterProvider => _connection;
    public Task SendComposingAsync(bool bestEffort, CancellationToken cancellationToken) =>
        SendAsync(PresenceState.Composing, bestEffort ? null : default, cancellationToken); // best-effort never throws otherwise
    public Task SendPresenceAsync(PresenceState state, Jid? chat, CancellationToken cancellationToken) =>
        SendAsync(state, chat, cancellationToken); // fail-closed on host calls
}
```

### Reaction target in Message (sketch)

```csharp
// Source: WAProto wiring TBD during the Baileys read; field number is ASSUMED = 30
// and must be re-pinned with a provenance-header row before codegen (Phase-6 discipline)
message ReactionMessage {
  string text = 1;
  MessageKey key = 2;
  google.protobuf.Timestamp senderTimestampMs = 3;  // TBD
}
```

### Inbound dispatch additions (sketch)

```csharp
// inside MessageDispatcher.HandleMessageAsync, before SendAck (except reaction protocol payloads)
if (message.ReactionMessage is not null)
{
    await SendAckAsync(messageId, from, cancellationToken).ConfigureAwait(false);
    await _owner.RaiseReactionReceivedAsync(
        new ReactionReceivedEventArgs(chat, messageId, from, message.ReactionMessage.Text, timestamp),
        cancellationToken).ConfigureAwait(false);
    return;
}
if (message.ExtendedTextMessage?.ContextInfo is not null && /* protocol edit/revoke flag */) { ... }
```

## State of the Art

| Old Approach | Current Approach | Impact |
|--------------|------------------|--------|
| Phase-5 no-op presence | Real, account-scoped `PresenceManager` | Ban-safety pacing gains actual wire signals without changing gate sequencing |
| In-memory `Channel<OutboundEnvelope>` (07 scaffolding) | Durable outbox behind `IOutboxStore` | Enqueued sends survive restarts and operator backoff |
| `SendBulkAsync`-style convenience expected by users | `SendBroadcastAsync` over gated 1:1 sends | Eliminates the blast-pattern footgun while still serving broadcast use cases |
| Message-only TEXT/BASE media | Additive `ReactionMessage` + protocol branches | Reactions, edit, revoke land inside the E2EE boundary without a second message wire |

**Deprecated / avoid:**
- `SendBulkAsync` or any single API that hides per-recipient policy (PROJECT.md anti-feature).
- `always-available` default (documented anti-feature).
- Presence shared static tables.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Reaction/edit/delete wire shapes and `Message` branch field numbers must be re-read from Baileys `WAProto/WAProto.proto @ 0af2386` before codegen; `30` for `ReactionMessage` is illustrative only | Code Examples, Architecture | High — must be verified before implementation; plan an early spike slice |
| A2 | Outbox delivers text-bearing payloads durably; media outbox ships as upload-descriptor only or is deferred to a follow-up | Pattern 2 | Medium — product decision; planning should lock scope early |
| A3 | `SendBroadcastAsync` is sequential rather than capped-parallel | Pattern 1 | Medium — throughput expectation; reversible with an opt-in later |
| A4 | `PresenceState` naming and the peer-presence event pair are internal API choices (no CONTEXT captured) | Summary, Pattern 3 | Low — naming churn only, but must be settled before plan tests are written |
| A5 | Dead-letter = permanent validation failures only; transient network and gate refusals schedule retry | Pattern 2 | Medium — hosts depending on terminal failure counts need this clarified |
| A6 | Edit and delete require recent-cache presence of the target before node build (no dangling protocol message) | Pattern 4 | Low — conservative, safer than racing ambiguous keys |
| A7 | Presence inbound parser failures never block receive (graceful degradation) | Pattern 3 | Low — consistent with D-20 |
| A8 | Phase 6 media dispatcher/media-retry branches remain importable and unchanged by this phase | Architecture | Low — avoids coupling with unfinished media plans |

## Open Questions

1. **Outbox media payloads (blocker for a concrete store schema).**  
   - What we know: upload descriptors are small and durable; raw bytes are not.  
   - What's unclear: whether hosts expect queued media or are happy with text-only outbox.  
   - Recommendation: ship text-first outbox, promote media-descriptor outbox explicitly as an outbox payload variant (gated behind the same upload descriptor) and time-box its scope in planning; if not time-boxed, defer media outbox to v1.1.

2. **`SendBroadcastAsync` result shape.**  
   - What we know: BCT-01 wants a per-recipient outcome report.  
   - What's unclear: list vs async stream of outcomes (partial-failure semantics).  
   - Recommendation: `IReadOnlyList<BroadcastRecipientOutcome>` with ordered recipients; report interleaves naturally during sequential sends; cancellation is the host's abort control.

3. **Presence peer subscription scope.**  
   - What we know: peer presence is announced via stanzas; frequency and expiry ~10s upstream.  
   - What's unclear: whether hosts require per-chat subscription vs one global account stream.  
   - Recommendation: one account-level `PresenceUpdated` stream, tagged with chat; subscription is implicit (no explicit subscribe command v1).

4. **Reaction removal semantics when host sends an empty reaction emoji.**  
   - What we know: Baileys represents removal as empty text.  
   - What's unclear: how receivers distinguish "removed" vs "never present".  
   - Recommendation: inbound emits `ReactionReceived` with empty/final emoji and a `IsRemoval` flag; replay ordering left to host.

5. **Edit event idempotency for hosts.**  
   - What we know: edits are new messages targeting an old one on the wire.  
   - What's unclear: whether the host receives an "edited" fact or a fresh `MessageReceived` with the same logic it replaces.  
   - Recommendation: dedicated `MessageEdited` event with original id + new text + server timestamp; hosts can upsert.

6. **Live-device verification unavailable in CI.**  
   - What we know: Project CI has no phone.  
   - What's unclear: jitter/protocol nuances untested in a live WA.  
   - Recommendation: document UAT NOT VERIFIED exactly like Phases 3/4; ship loopback + doctrine tests.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | build/test | yes | 10.0.401 (repo global.json) | — |
| .NET runtime | execution | yes | 10.0.12 | — |
| xunit.v3 + MTP runner | tests | yes | 4.0.1 / SDK-mode (global.json) | — |
| Live WhatsApp account | integration UAT | not in CI | — | loopback + fakes |

Step 2.6 classified Phase 8 as depending only on repo-native tooling; no external services beyond the host's own (build/test). `Polly.Core` (8.8.0) is already approved for HTTP layer only; no new use in this phase.

## Validation Architecture

Nyquist is enabled (`workflow.nyquist_validation: true` [VERIFIED: .planning/config.json:25]); security enforcement is on (`security_enforcement: true` [VERIFIED: .planning/config.json:49]).

### Test Framework

| Property | Value |
|----------|-------|
| Framework | xunit.v3 4.0.1 + AwesomeAssertions 9.6.0 + NSubstitute 6.2.0 (locked) |
| Config file | repo-root `global.json` (MTP mode) — unchanged |
| Quick run command | `dotnet test --filter "FullyQualifiedName~Broadcast\|Outbox\|Presence\|Reaction\|Edit\|Delete"` |
| Full suite command | `dotnet test Iwasug.WhatsApp.Sdk.sln` |

### Phase Requirements → Test Map

| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| BCT-01 | sequential gated sends, per-recipient outcome rows, no side channel | unit | `dotnet test --filter FullyQualifiedName~BroadcastSendTests` | ❌ Wave 0 |
| BCT-01 | safety gate invoked for every recipient (audit) | static audit | `dotnet test --filter FullyQualifiedName~SendPathAuditTests` | ✅ extend |
| BCT-01 | cancellation stops remaining recipients | unit | `dotnet test --filter FullyQualifiedName~BroadcastCancellationTests` | ❌ Wave 0 |
| EXT-01 | enqueue→row durability round-trip through in-memory store | unit | `dotnet test --filter FullyQualifiedName~OutboxStoreTests` | ❌ Wave 0 |
| EXT-01 | pump: throttle/refusal schedules retry, dead-letter after ceiling, restart survives due-rows | unit + loopback | `dotnet test --filter FullyQualifiedName~OutboxPumpTests` | ❌ Wave 0 |
| EXT-01 | per-account isolation (one account's breaker doesn't block another's outbox) | integration | `dotnet test --filter FullyQualifiedName~OutboxIsolationTests` | ❌ Wave 0 |
| EXT-02 | send every PresenceState; peer presence parses to typed event | unit + loopback | `dotnet test --filter FullyQualifiedName~PresenceTests` | ❌ Wave 0 |
| EXT-02 | best-effort pacing never fails a send when presence dies | unit | `dotnet test --filter FullyQualifiedName~PresencePacingTests` | ❌ Wave 0 — extend Phase-5 seam |
| EXT-03 | reaction send/receive/remove; empty-emoji removal stays a real send | unit + loopback | `dotnet test --filter FullyQualifiedName~ReactionTests` | ❌ Wave 0 |
| EXT-04 | edit own message; delete for everyone (incl. group participant ack) | unit + loopback | `dotnet test --filter FullyQualifiedName~EditDeleteTests` | ❌ Wave 0 |
| EXT-03/04 | inbound event ordering preserved (D-06) | integration | `dotnet test --filter FullyQualifiedName~ExtendedMessageEventTests` | ❌ Wave 0 |
| STR-01 (regression) | new seam passes host-style contract run | contract kit | `dotnet test --filter FullyQualifiedName~OutboxStoreContracts` | ❌ Wave 0 |

### Sampling Rate

- **Per task commit:** `dotnet test Iwasug.WhatsApp.Sdk.sln --filter "FullyQualifiedName~Broadcast\|Outbox\|Presence\|Reaction\|Edit\|Delete"`
- **Per wave merge:** full suite + NOTICE provenance-row audit (mirror Phase-3/4)
- **Phase gate:** full suite green before `/gsd-verify-work`; live UAT documented NOT VERIFIED if no device

### Wave 0 Gaps

- [ ] `IOutboxStore` + factory + in-memory implementation + contract kit
- [ ] `OutboxPump` production seam + options (attempts, backoff bounds, poll floor)
- [ ] `PresenceManager` upgrading `IPacingPresence` (real nodes)
- [ ] `PresenceState` enum + inbound dispatcher parse for `<presence>` and peer typing
- [ ] `Message` proto additions (ReactionMessage + ProtocolMessage branches) with provenance header
- [ ] `MessageBuilder` reaction/edit/revoke/presence-bearing helpers (pure functions unit-testable)
- [ ] `BroadcastRecipientOutcome`, `BroadcastOptions`, `OutboxEntry`, `OutboxPayload`, `OutboxState`
- [ ] `Messenger` extended public APIs + `OutboxPump` wiring in `AccountRuntime`/`DefaultAccountRuntimeFactory`
- [ ] Event types + `Messenger` raise helpers for presence/reaction/edit/delete + stream records
- [ ] Store reference-impl update (`InMemoryStoreFactory`) to serve the new seam
- [ ] `SendPathAuditTests` rows for the new APIs + plist baseline
- [ ] `PublicAPI.Unshipped.txt` entries for all new surface

## Security Domain

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | pairing + Signal unchanged |
| V3 Session Management | yes | outbox durability under existing store seam; no new session token handling |
| V4 Access Control | yes (host-facing) | outbox read/delete should be host-authorized; SDK never auto-accepts host edits |
| V5 Input Validation | yes | JID validation, target-id ownership, emoji/payload bounds, presence-state allow-list, outbox entry validation; all fail-closed |
| V6 Cryptography | yes (inherited) | no new primitives; E2EE rides through existing `MessageBuilder`/ratchet paths |

### Known Threat Patterns for this stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Broadcast blast policy bypass | Evasion of safety gate | `SendPathAuditTests` includes new APIs; each recipient is a full gate invocation |
| Outbox permanent-retry storm | Denial of service | attempt ceiling + dead-letter; permanent validation failures don't retry |
| Secret material in durable rows | Information Disclosure | Outbox payloads carry message id + text/descriptor only; never keys/ciphertext; no media bytes (regression-proof through review) |
| Cross-account presence leak | Information Disclosure | instance-owned `PresenceManager`; Phase-7 static-mutable-state audit unchanged |
| Edit/delete against another sender's message | Tampering | ownership validation before node assembly against recent cache |
| Reaction bypass of E2EE gate | Elevation | reactions treated as ordinary gated sends (audit evidence) |

## Sources

### Primary (HIGH confidence)

- In-repo store discipline: `src/Iwasug.WhatsApp.Sdk/Stores/IRecentMessageCacheStore.cs` (primitive-only interface shape, D-18/D-19/D-20, lines 1-133)
- In-repo send path: `src/Iwasug.WhatsApp.Sdk/Messaging/Messenger.cs` (gate invocation shape at lines 213-222), `MessageSender.cs`, `MessageBuilder.cs` (node assembly, lines 334-423)
- In-repo dispatcher ordering: `src/Iwasug.WhatsApp.Sdk/Messaging/MessageDispatcher.cs` (HandleAsync + D-06 ordering + media branch + rmr routing)
- In-repo presence seam: `src/Iwasug.WhatsApp.Sdk/Safety/HumanPacing.cs` (IPacingPresence + NoOpPacingPresence, lines 1-183)
- In-repo outbox scaffolding (NOT wired): `src/Iwasug.WhatsApp.Sdk/Accounts/AccountRuntime.cs` (OutboundQueue `Channel`, lines 70-90), `Accounts/OutboundEnvelope.cs`
- In-repo codec tokens: `src/Iwasug.WhatsApp.Sdk/Codec/TokenDictionary.cs` (`presence 69`, `composing 89`, `paused 192`, `available 174`, `unavailable 86`, `chatstate 84`, `reaction` in dobule-byte dict, `edit 187`, `delete` double-byte, `mediaretry`)
- Requirements / plan docs: `.planning/REQUIREMENTS.md` (BCT-01, EXT-01..04), `.planning/ROADMAP.md` Phase 8 (SC 1-4), `.planning/research/FEATURES.md` (broadcast anti-feature list, presence guidance)
- Project instructions: `PROJECT.md`, `AGENTS.md`

### Secondary (MEDIUM confidence)

- Phase-6 clean-room doctrine: `.planning/phases/06-media-pipeline/06-RESEARCH.md` (provenance header discipline + clean-room rules) — explicitly reused for the protocol additions
- Phase-7 research: `.planning/phases/07-multi-account-orchestration/07-RESEARCH.md` (outbox noted as deferred; atomic registration discipline)
- Baileys WAProto (MIT) at pinned commit — Hausnummer of protocol branch field numbers is the work of the Phase-8 spike, not yet read this session (flagged as A1)

### Tertiary (LOW confidence)

- Voice-of-WhatsApp behavior (typing indicator expiry ~10s, phone push-suppression nuance) — assumed and documented as tunables; live UAT still required
- `WaProto` reaction/edit/revoke field numbers (A1) — open spike item, not yet pinned

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new packages; everything already pinned and audited
- Architecture: HIGH for composition/broadcast/outbox-as-store and presence seam upgrade; MEDIUM for protocol wire shapes (pending Baileys read)
- Pitfalls: MEDIUM-HIGH — derived from Phase-4/5/6 verified pitfalls plus protocol-translation risks
- Wire-number specifics: LOW — gated behind a mandatory clean-room spike

**Research date:** 2026-10-16
**Valid until:** 2026-11-15 (30 days; protocol field numbers must be verified via the spike before implementation locks them)
