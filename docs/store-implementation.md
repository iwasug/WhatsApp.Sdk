# Store implementation guide

The SDK never talks to a database. It talks to **eight store interfaces**, and your
host supplies the implementations (EF Core, Dapper, files, …) behind them. This guide
documents the seam, each store and the record it persists, how the store-contract
test kit verifies your implementation, and the two things that trip hosts up most:
first-registration material and the failure-≠-missing rule.

The store contract test kit (`Iwasug.WhatsApp.Sdk.Testing`) is the same mechanism
EF Core uses for provider conformance: one set of abstract contract classes, one
binding per implementation. The kit is also the executable specification for
everything below.

## The seam

- **The host owns persistence.** The SDK declares account-scoped interfaces;
  `StoreRegistry` + `StoreRegistryOptions` assemble them for one account.
- **Registration is per capability.** `StoreRegistryOptions` has one nullable
  factory property per store. A host may register only a subset — capabilities left
  unregistered fall back to the SDK's in-memory reference store.
- **The instance IS the namespace.** Each factory receives an `AccountId` and
  produces an account-scoped store instance; no store method takes an account id.
  Instances are long-lived per account (created once, reused until remove/shutdown)
  and SDK-owned: `StoreRegistry` disposes every instance it created, host-factory
  instances included.
- **The in-memory reference store is prototyping-only.** It does not survive
  process restart. Read the honesty section in the repository
  [README](../README.md#in-memory-reference-store--read-this) before shipping with it.

```csharp
var options = new StoreRegistryOptions
{
    SignalSessionFactory = new MySignalSessionStoreFactory(),   // host implementation
    IdentityFactory = new MyIdentityStoreFactory(),
    // … unregistered capabilities fall back to the in-memory reference store
};
var registry = new StoreRegistry(options, logger);
StoreSession session = await registry.CreateSessionAsync(accountId);
```

## The eight stores

| Store interface | What it persists | Key / shape | Kit contract class |
|---|---|---|---|
| `ISignalSessionStore` | Signal protocol sessions (SDK-owned ratchet state) | Peer address (JID string) | `SignalSessionStoreContractTests` |
| `IIdentityStore` | Peer Signal identities (the trust-on-first-use pin source) | Peer address (JID string) | `IdentityStoreContractTests` |
| `IPreKeyStore` | Pre-key bundles and one-time pre-keys (two independent sub-stores) | Bundle: peer address; pre-key: integer id | `PreKeyStoreContractTests` |
| `IRecentMessageCacheStore` | Recently received messages (quote/reply + retry-resend source) | Message key (+ chat, timestamp) | `RecentMessageCacheStoreContractTests` |
| `ISafetyStateStore` | The ban-safety state row | Single mutable row per account | `SafetyStateStoreContractTests` |
| `ICredentialStore` | The pairing credential row | Single mutable row per account | `CredentialStoreContractTests` |
| `IRecipientContactStore` | Recipient-contact ledger ("has this account ever contacted this recipient") | Privacy-neutral recipient key | `IRecipientContactStoreContractTests` |
| `IOutboxStore` | Durable outbox rows | Entry id | `OutboxStoreContractTests` |

Per-store notes, sourced from each interface's own documentation:

- **`ISignalSessionStore`** persists `SignalSessionRecord(address, SessionState)`.
  `SessionState` is SDK-owned serialized ratchet state: persist the bytes
  **opaquely**, never parse them — a format change between SDK versions requires
  only that the bytes round-trip. Must-not-lose.
- **`IIdentityStore`** persists `SignalIdentityRecord` under the peer's address.
  This is the storage behind identity pinning; a read failure must never surface as
  "unknown peer".
- **`IPreKeyStore`** is split into two independent sub-stores: bundles keyed by
  peer address (`GetBundleAsync`/`PutBundleAsync`) and one-time pre-keys keyed by
  their integer id (`GetPreKeyAsync`/`PutPreKeyAsync`/`RemovePreKeyAsync`).
  Consuming a one-time pre-key is `RemovePreKeyAsync`; consuming must not touch the
  bundle, and a bundle overwrite must not resurrect a consumed pre-key.
- **`IRecentMessageCacheStore`** persists `RecentMessageEntry`. It is the only
  **lossy** store: quote/reply degrades gracefully when the cache is empty.
  `TrimOldestAsync` and `PurgeExpiredAsync` are trim/purge **primitives** — the
  retention policy (how many entries, how long) is SDK-owned and is never a
  parameter on the interface.
- **`ISafetyStateStore`** persists `SafetyStateRecord` as one mutable row per
  account (last-write-wins `PutAsync`, `RemoveAsync` clears it).
- **`ICredentialStore`** persists `CredentialRecord` as one mutable row per account:
  the account's long-term key material and pairing identity. Must-not-lose.
- **`IRecipientContactStore`** keeps a set of contacted recipients. Keys are
  deterministic and privacy-neutral (`"{jid.User}@{jid.Server}"` — no display name,
  no message body). `MarkContactedAsync` is idempotent; `HasContactedAsync` returns
  `false` for unknown recipients. A failing read must **throw**, never return
  `false` — a ledger blip misread as "never contacted" would wrongly consume
  cold-outreach budget.
- **`IOutboxStore`** is the durable outbox primitive seam: `EnqueueAsync`,
  `GetDueAsync`, `MarkSentAsync`, `ScheduleRetryAsync`, `DeadLetterAsync`,
  `TryRemoveAsync`, over `OutboxEntry` rows with an `OutboxState`
  (Pending / InFlight / Sent / RetryScheduled / DeadLettered). A dead-lettered row
  is no longer due but is retained until removed.

## Verifying your implementation

1. Reference the two NuGet packages: `Iwasug.WhatsApp.Sdk` (the SDK — it defines
   the eight store interfaces) and `Iwasug.WhatsApp.Sdk.Testing` (the kit).
2. Subclass each kit contract class you want to enforce and override
   `CreateFactory` to return **your** factory. xunit runs every inherited `[Fact]`
   against your implementation:

   ```csharp
   public sealed class MySignalSessionStoreTests : SignalSessionStoreContractTests
   {
       protected override ISignalSessionStoreFactory CreateFactory() => new MySignalSessionStoreFactory();
   }

   // …repeat for IdentityStoreContractTests, PreKeyStoreContractTests,
   // RecentMessageCacheStoreContractTests, SafetyStateStoreContractTests,
   // CredentialStoreContractTests, OutboxStoreContractTests, and
   // RecipientContactStoreContractTests.
   ```

3. Subclass `ComplianceTestBase` once with an empty concrete type so the
   completeness backstop runs in your assembly:

   ```csharp
   public sealed class MyStoreCompliance : ComplianceTestBase { }
   ```

   Its single `[Fact]` asserts that every abstract contract class shipped by the
   kit has at least one concrete subclass among the loaded test assemblies — a
   contract class added by a kit upgrade fails your run by name until you bind it,
   instead of silently not running.

4. Run your test project. **A failing fact is a contract violation in your store —
   fix the store, never the contract class.** The per-store facts cover round-trip,
   failure-≠-missing on both paths, two-account isolation, idempotency edges,
   durability-await (an independent second store instance must observe an awaited
   write), and parallel cross-read enforcement.

Two binding notes:

- If one factory type implements several factory interfaces, bind it through
  interface-typed variables for each capability — all eight `CreateAsync`
  signatures are identical except for the return type, so only the Signal-session
  overload can be called implicitly on such a type. The kit's own
  `FaultingStoreFactory` (a fault-injection decorator you can wrap your factory in
  to test failure paths) documents this exact binding pattern.
- The kit's fault-injection family is part of the shipped kit: wrap your factory in
  `FaultingStoreFactory`, set `FaultMode.FailReads` or `FaultMode.FailWrites`, and
  you get the failure-≠-missing assertions without writing your own fault plumbing.

## First-registration material (read before the first pairing)

First-time pairing/registration (a fresh store with no credential row) builds the
registration payload from `RegistrationDefaults`:

| Field | Meaning |
|---|---|
| `IdentityPublic` | Registration identity public key (32 bytes) |
| `SignedPreKeyId` | Signed-pre-key id |
| `SignedPreKeyValue` | Signed-pre-key public value (32 bytes) |
| `SignedPreKeySignature` | Signed-pre-key signature (64 bytes) |
| `RegistrationId` | Signal registration id (14-bit) |
| `Platform` | Companion platform label |
| `AppPrimary` / `AppSecondary` / `AppTertiary` | App version components |

Configure it on the hosting surface via `WhatsAppSdkOptions.RegistrationDefaults`.
When it is `null`, the SDK uses explicit placeholders and logs, per account:

> First-time pairing/registration will be rejected by the server until real
> signed-prekey material is supplied; login-only accounts with an existing
> credential row are unaffected.

What a host must supply: **real signed-pre-key material** (the triple above).
Login-only accounts with an existing credential row are unaffected by the
placeholder — the persisted row's values (identity public, registration id,
platform) override the defaults on every later connect.

Where the material lives: once pairing completes, the account's long-term material
(noise key, identity key pair, signing key pair, adv secret, paired user, device,
registration id) is persisted in **your `ICredentialStore`** — the row documented
above — so material survives restarts only as far as that store does. The
signed-pre-key triple itself stays a host-supplied input on the registration path:
the credential row carries no pre-key fields yet (documented boundary), so there is
no per-store row that holds it.

## Failure vs missing, and running on a real database

Every contract class encodes the same core rules. Design your schema around them:

- **Failure ≠ missing.** An absent key returns `null` (or `false` / empty). A
  failing read or write **throws** — the SDK wraps host failures in
  `SdkStoreException` at the trust boundary; it never converts a failure into a
  "missing" value. A conflation bug in either direction is a data-loss path
  (a credential blip misread as "no row" re-enters registration; a session blip
  misread as "empty session" triggers session re-establishment).
- **Durability-await.** A write method's task completes only once an independent
  subsequent read observes the value. The contracts prove this with a second store
  instance reading what the first wrote.
- **Per-account scoping.** The contracts run two accounts through the same factory
  against the same keys concurrently and assert zero cross-reads — the same
  address or key must be independent per account.
- **Idempotency edges.** Removing an absent key/row returns `false`; double puts are
  last-write-wins. Both are asserted per store.

Practical schema notes for a real database:

- Model per-account namespacing as a composite key column (account id + key) or a
  per-account table/schema set — either satisfies the isolation facts, as long as
  the same key under two accounts never crosses.
- Suggested key columns: peer address for identities, pre-key bundles, and
  sessions; integer pre-key id for one-time pre-keys; message key plus chat and
  timestamp for the cache; recipient key for the contact ledger; outbox entry id
  for the outbox. Credential and safety state are single nullable rows per account.
- `IOutboxStore.GetDueAsync` is an account-wide due-row poll: index
  `(state, next_attempt_at)` so the pump stays cheap. Dead-lettered rows remain
  queryable for removal.
- `IRecentMessageCacheStore.GetRecentAsync(chat, limit)` wants a
  `(chat, timestamp descending)` index; `TrimOldestAsync` / `PurgeExpiredAsync`
  operate account-wide — a `(timestamp)` index serves both.
- **Copy byte arrays on write and on read.** The session contract asserts that
  mutating the caller's buffer after `PutAsync` (or mutating a returned buffer)
  does not change what the store serves; a database provider that keeps references
  violates it.
- **No transaction API is required.** Every store is single-key CRUD; the SDK
  sequences multi-step state changes itself, so any backend — including one
  without transactions — can implement the seam.

For a worked host flow, see `HostStyleSecondRunTests.cs` in
`tests/Iwasug.WhatsApp.Sdk.Tests/StoreContract/`: one deliberately-different
factory drives the session, identity, pre-key, cache, and safety capabilities
through the kit's public API in a single run, and the pairing sample hosts a
file-backed credential store bound the same way.
