# Webhook recipe: SDK events to your webhook pipeline

`connection.Messaging` (`Messenger`) exposes one event stream. This recipe bridges
that stream to the host's own webhook delivery pipeline: the endpoint, its retries,
and its delivery guarantees belong to the host — this document is the glue code and
the discipline around it.

## Scope: no HTTP server ships in the SDK

- **The SDK ships no HTTP server and no webhook delivery machinery.** There is no
  endpoint, no listener, no delivery queue, and no retry policy inside the SDK. The
  host owns the endpoint and everything behind it.
- **No webhook payload format is imposed.** The mapping below is a pattern; shape
  the payload to what your consumers already accept.
- **Connection state deliberately stays out of this vocabulary** (CONN-05).
  Liveness and reconnects belong to `WaConnection.StateChanged`, not the event
  stream.

What the SDK does guarantee is the stream's mechanics: typed events, one bounded
channel, and a documented shutdown path.

## The event stream

`messaging.ReadEventsAsync(ct)` yields `WhatsAppEvent` — exactly four subtypes:

| Subtype | Fires for |
|---|---|
| `MessageReceivedEvent` | one decrypted inbound message, after the durable session write and the ack |
| `ReceiptReceivedEvent` | one parsed inbound receipt (delivery / read / played / sender / inactive / retry) |
| `MessageAckedEvent` | the terminal outcome of one send (`Sent` / `ServerError`), raised exactly once |
| `DecryptionFailedEvent` | one inbound message that failed to decrypt (raised on every failure, uncapped) |

**Backpressure is the channel contract.** All events flow through one bounded
channel, `Messenger.EventChannelCapacity = 128`. A slow consumer slows the receive
pump — the stream never silently drops events to keep up. Consume promptly; if your
mapping work is slow, hand off to your own queue (see the sketch at the end)
instead of doing the slow work inside the loop.

**Cancellation is the shutdown path.** Cancelling the token passed to
`ReadEventsAsync` closes the stream, and the loop exits by
`OperationCanceledException` — mirroring the sample host:

```csharp
try
{
    await foreach (var ev in messaging.ReadEventsAsync(cts.Token))
    {
        PrintEvent(ev);
    }
}
catch (OperationCanceledException)
{
    // Ctrl+C: the stream closed via cancellation — the documented shutdown path.
}
```

Exceptions from your handler never cross the SDK boundary: enumeration runs on the
host's own task, so a throwing consumer surfaces to you, not into the receive pump.

## Mapping events to webhook payloads

A typed switch over the four subtypes is the whole mapping surface — the sample
host's `PrintEvent` is the exact shape. What each event carries:

- **`MessageReceivedEvent`** — `Key` (message key; `Key.Id` is the message id),
  `Chat`, `Sender`, `IsFromMe`, `Timestamp`, `Text` (nullable: null when the
  message carries no conversation body), `QuotedMessageId` (nullable). This is the
  event with content.
- **`ReceiptReceivedEvent`** — `Kind` (see `ReceiptKind`), `MessageIds` (verbatim
  from the wire), `Chat`, `Participant` (null outside groups), `Timestamp`.
  References message ids; carries no content.
- **`MessageAckedEvent`** — `MessageId`, `Chat`, `Status` (`Sent` / `ServerError`),
  `Error`, `T` (server stamp), `ServerId`, `Timestamp`. The send-correlation event;
  a late or duplicate ack produces no second event.
- **`DecryptionFailedEvent`** — `Chat`, `Sender`, `MessageId`, `RetryCount`
  (1-based, per sender + message id), `Timestamp`. Names rules only: no plaintext,
  ciphertext, or key bytes ever surface (T-4-01). Map it to a typed failure signal
  for your operations surface.

```csharp
// Host-side payload records are sketched inline — your pipeline, your shapes.
switch (ev)
{
    case MessageReceivedEvent m:
        await pipeline.EnqueueAsync(new InboundMessage(
            AccountId, m.Chat, m.Key.Id, m.Sender, m.Text, m.QuotedMessageId, m.Timestamp));
        break;
    case ReceiptReceivedEvent r:
        await pipeline.EnqueueAsync(new ReceiptFact(
            AccountId, r.Kind, r.MessageIds, r.Chat, r.Timestamp));
        break;
    case MessageAckedEvent a:
        await pipeline.EnqueueAsync(new SendOutcomeFact(
            AccountId, a.MessageId, a.Status, a.ServerId, a.Timestamp));
        break;
    case DecryptionFailedEvent f:
        await pipeline.EnqueueAsync(new DecryptFailure(
            AccountId, f.Chat, f.Sender, f.MessageId, f.RetryCount, f.Timestamp));
        break;
}
```

Include the account identity in every payload: one host process consumes many
accounts, and a payload must stay attributable after it leaves the loop.

## Idempotency and delivery discipline

- **Events fire once per fact, after durable state is safe.** `MessageReceivedEvent`
  is raised after the session write and the ack complete; `MessageAckedEvent`
  exactly once per completed send. Duplicate *wire* receipts do produce duplicate
  `ReceiptReceivedEvent`s — dedupe on (kind, message id) if your consumers need it.
- **Receipts and acks are informational.** They gate no SDK behavior and no
  security decision (T-4-14); the host decides what to trust.
- **The host dedupes on message id**, not the SDK. Key your delivery queue's
  idempotency record on (kind, account, message id).
- **No silent retries — anywhere.** The SDK never re-sends on its own: one logical
  send is one network send, an ack timeout surfaces the message id on a `TimedOut`
  outcome and that send is dropped, and re-calling `SendMessageAsync` is a NEW send
  with a NEW id — see the README's
  [idempotency contract](../README.md#idempotency-contract-read-before-calling-send).
  Your webhook pipeline should inherit the same discipline toward its consumers:
  explicit retries under your own policy, never an implicit re-send.

## Redaction and data handling

Event payloads carry plaintext message content and identifiers, so the boundary is
where your policy applies:

- **Redact before persistence and logging.** `Text` is the message body as
  decrypted; never log message content or identifiers (JIDs, message ids) beyond
  operational need.
- **Key material never appears in events.** The vocabulary names rules only;
  `DecryptionFailedEvent` in particular carries no plaintext, ciphertext, or key
  bytes (T-4-01).
- **Treat your delivery queue as sensitive storage.** Content persists in your
  queue payloads — encrypt at rest or bound retention per your own policy.

## The bridge, end to end

1. **Consume** — one long-lived `await foreach` per account over
   `messaging.ReadEventsAsync(ct)`, with `catch (OperationCanceledException)` as
   the shutdown path.
2. **Map** — the typed switch above; include the account identity in every payload.
3. **Enqueue** — hand off to the host's own durable delivery queue immediately
   (the bounded channel backpressures the receive pump if you do slow work inside
   the loop instead).
4. **Deliver and ack** — your endpoint's own retry/backoff and signature scheme;
   dedupe on the message id you keyed in step 2, and never manufacture an SDK
   re-send from a delivery failure.
