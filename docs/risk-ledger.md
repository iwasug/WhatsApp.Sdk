# Ban-safety risk ledger

The honest statement of what the ban-safety layer does, what it does not do, and
what no SDK setting can mitigate — read it before configuring throughput.

## Ban-safety / risk ledger (Phase 5 — honest scope)

Every public send (`SendMessageAsync`, `SendGroupMessageAsync`, `ReplyAsync`)
passes a mandatory in-process safety gate before the inner send. There is no
bypass path in the public API; protocol-generated acks, receipts, and retry
resends are responses, not account traffic, and stay outside the gate.

**What the layer does:**

- Minute/hour/day token buckets with randomized inter-send jitter on every
  send (safe defaults: 20/min, 250/hr, 1000/day).
- Warm-up ramp for new accounts: the daily ceiling grows with persisted
  account age (`ISafetyStateStore` is the source of truth across restarts).
- Hard daily cold-outreach cap (30/day) over a durable per-account recipient
  ledger; replies to inbound messages are exempt from the cap.
- Circuit breaker with exponential backoff on repeated send failures: trips
  open, refuses fast while open, admits a single probe, and closes on success.
  Typed safety events (`BudgetExhausted`, `ThrottleEngaged`,
  `AccountRestricted`) fire on the corresponding transitions.
- Active-hours window with typed refusal outside the window, plus best-effort
  human-like presence pacing (bounded typing/recording pause, currently a
  no-op wire signal until Phase 8).
- Safe-by-default configuration: `SafetyOptions.SafeDefaults` plus startup
  validation that rejects unsafe throughput unless the host explicitly opts in
  (see below).

**What the layer does NOT do:**

- No guarantee against bans or restrictions. Raising throughput raises
  restriction risk; no setting lowers risk.
- No rehabilitation of already-flagged or restricted accounts.
- No guarantee of compliance with WhatsApp Terms of Service. The host is
  responsible for lawful, policy-compliant use (see the Compliance section in
  the [README](https://github.com/iwasug/WhatsApp.Sdk/blob/master/README.md)).

**Media additions (Phase 6):**
- Uploads are high-bandwidth account activity; upload-then-send doubles
  observable traffic.
- Bulk media fan-out is the fastest ban path.
- The SDK validates labels but never transcodes or judges media semantics.
- Expiry-recovery requests are capped protocol traffic (three per message id).
- No visibility into server-side signals the SDK cannot observe (account age
  reputation, contact quality, complaint rates, device reputation). Those
  operational realities remain the host's responsibility.
- Signed-prekey rotation is still not periodic; group read-side is still
  deferred (GRPM-01); full presence semantics remain Phase 8.

**Unsafe opt-in acknowledgment (required):**

Exceeding the safe bands (above 60/min, 600/hr, or 1000/day) requires both
`AllowUnsafeThroughput = true` **and** a non-empty
`DeclaredUnsafeAcknowledgment` stating the host has read this ledger and
accepts the elevated restriction risk. Without both, validation throws
`ArgumentOutOfRangeException` before any I/O.

```csharp
using Iwasug.WhatsApp.Sdk.Safety;

var options = new SafetyOptions
{
    AllowUnsafeThroughput = true,
    DeclaredUnsafeAcknowledgment =
        "We have read the Ban-Safety risk ledger and accept the elevated restriction risk.",
    MaxMessagesPerMinute = 120,
};
```

Byte-level protocol observations in this phase: none new. Phase 5 adds policy
only over the existing send path and pluggable stores; no protocol code,
schemas, or constants were copied from any source, including MPL-2.0 sources.

## First-registration material (read before the first pairing)

First-time pairing/registration requires real signed-prekey material in
`RegistrationDefaults`. The shipped default uses explicit placeholders and logs,
per account, that the server will reject first-time registration until real
material is supplied (`DefaultAccountRuntimeFactory.cs`, verbatim):

> No RegistrationDefaults configured for {AccountId}: using placeholder registration material. First-time pairing/registration will be rejected by the server until real signed-prekey material is supplied; login-only accounts with an existing credential row are unaffected.

Following the docs alone, a new host that never supplies `RegistrationDefaults`
gets stuck between install and first send: login-only accounts with an existing
credential row keep working, but a first-time pairing attempt is rejected by the
server. The host path for supplying real registration material is documented in
[docs/store-implementation.md](https://github.com/iwasug/WhatsApp.Sdk/blob/master/docs/store-implementation.md).

## Live-verification status

The pairing flows are exercised against a scripted server in the test suite;
real-phone UAT remains pending. Carried verbatim from the Phase 3 validation
record (`03-VALIDATION.md`) — no simulated result is a substitute for a real
device:

- **CONN-01 (QR scan):** NOT VERIFIED — No real WhatsApp account/phone available in execution environment. Sample host cannot be paired via QR scan without device. [Reason: No phone/account in CI — per human_verify_mode: end-of-phase config]
- **CONN-02 (8-char code entry):** NOT VERIFIED — No real WhatsApp account/phone available. Code entry on physical phone cannot be performed. [Reason: No phone/account in CI — per human_verify_mode: end-of-phase config]
- **CONN-03 (kill/restart resume):** NOT VERIFIED — No real linked session to test kill/restart resume behavior. [Reason: No phone/account in CI — per human_verify_mode: end-of-phase config]
- **CONN-03 (missing credential fallback):** NOT VERIFIED — No credential file to remove/test fallback behavior. [Reason: No phone/account in CI — per human_verify_mode: end-of-phase config]

## Cannot be mitigated by any SDK setting:

No SDK setting, configuration, or policy can influence these server-side
signals, and none of them is observable by this library:

- Account-age reputation.
- Contact quality.
- Complaint rates.
- Device reputation.

There is no guarantee against bans or restrictions — raising throughput raises
restriction risk, and no setting lowers risk — and there is no rehabilitation
of already-flagged or restricted accounts. Those operational realities remain
the host's responsibility.
