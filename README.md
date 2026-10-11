# Iwasug.WhatsApp.Sdk

An unofficial, open-source .NET 10 library that implements the WhatsApp Web
multi-device protocol for building customer-service and broadcast integrations:
QR account pairing, end-to-end encrypted messaging, and a store seam designed
to run many concurrent accounts safely.

> **Unofficial project.** This library is not affiliated with, endorsed by, or
> connected to WhatsApp or Meta Platforms, Inc. "WhatsApp" is a trademark of
> Meta Platforms, Inc. Use of this library must respect WhatsApp's Terms of
> Service; see the Compliance note below.

## Status

Phases 1–4 complete: Foundation & Store Seam → Wire Protocol & Connection
Resilience → Pairing & Session Durability → Signal E2EE & Messaging. The
Phase-1 walking skeleton that started it:

- Root solution with two packable projects: the SDK and a published store
  contract test kit (`Iwasug.WhatsApp.Sdk.Testing`).
- First store seam wired end-to-end: `AccountId` → async factory →
  account-scoped `ISignalSessionStore` → typed `SignalSessionRecord`
  write/read, proven by an abstract contract suite running against the
  in-memory reference implementation.

Wire protocol, pairing, Signal end-to-end encryption, and messaging ship
through Phase 4 (see the Messaging section below). Ban-safety, media,
multi-account orchestration, broadcast, and presence arrive in later phases.

## In-memory reference store — read this

The SDK ships an **in-memory reference store** (`InMemoryStoreFactory`) so the
seam works out of the box during prototyping. It is honestly positioned:

- It **does not survive process restart** — all state is lost when the process
  exits.
- It is suitable for prototyping, tests, and low-volume experiments — **not**
  as production persistence.
- In production, register your own store implementations (EF Core, Dapper,
  files, …) behind the same interfaces; the host owns persistence by design.

## Messaging

Everything below runs through `connection.Messaging` (`Messenger`) — the public
API surface only, as exercised by the sample host.

- **Send (1:1):** `await messenger.SendMessageAsync(Jid.Parse("15551234567@s.whatsapp.net"), "hi")`
  returns a `SendOutcome` (`MessageId`, `Status`, server `T`/`ServerId`).
- **Send (group):** `messenger.SendGroupMessageAsync(groupJid, text)` — sender-key
  E2EE: pairwise SKDM distribution to the member devices (**own devices excluded**
  from the fanout — A3), then one signed `skmsg` ciphertext per group message
  with a deterministic `phash` over the participant set.
- **Receive:** the `MessageReceived` event (or the stream below) fires once per
  decrypted inbound message, after the durable session write and the ack.
  Decryption failures raise `DecryptionFailed` — typed, never a throw through
  the receive pump.
- **Receipts:** `ReceiptReceived` reports `delivery` / `read` / `played` /
  `sender` / `inactive` / `retry` with the referenced message ids. Informational
  only: receipts gate no SDK behavior and no security decision (T-4-14) — the
  host decides what to trust.
- **Quotes:** `messenger.ReplyAsync(chat, quotedMessageId, text)` — the quoted id
  arrives as `MessageReceivedEventArgs.QuotedMessageId` and must still be in the
  recent-message cache (otherwise a typed `SdkFormatException`, never a
  half-sent reply).
- **Events:** `messenger.ReadEventsAsync(ct)` yields `WhatsAppEvent` — exactly
  four subtypes: `MessageReceivedEvent`, `ReceiptReceivedEvent`,
  `MessageAckedEvent`, `DecryptionFailedEvent`. One bounded channel
  (`Messenger.EventChannelCapacity = 128`): a slow consumer slows the receive
  pump instead of dropping events, and cancelling the token closes the stream.
  Connection state deliberately stays on `WaConnection.StateChanged` (CONN-05),
  outside this vocabulary.

### Idempotency contract (read before calling send)

- **One logical send = one network send.** The SDK never re-sends on its own.
  No ack inside the send window → the outcome is `TimedOut` **carrying the
  message id**, and that send is dropped — not queued, not retried silently.
  Re-calling `SendMessageAsync` is a NEW send with a NEW id.
- **Retry receipts resend the same id, inside the cache horizon only.** When a
  peer reports decryption failure, the SDK re-sends the same message id from
  the recent-message cache — but only while the plaintext is still retained.
  Trimming past the cache horizon produces the documented **no-resend
  outcome**: the SDK never re-creates plaintext it no longer holds (Pitfall 6 /
  D-05). The Phase-3 socket-drop path re-sends only a frame already in flight
  (protocol-mandated, not message-level retry).
- **Cancellation is not an outcome:** a cancelled send surfaces
  `OperationCanceledException`, releases the send gate, and never touches the
  wire.

### Risk ledger (Phase 4 — honest scope)

**Mitigated:** end-to-end encryption for 1:1 and group text (X3DH + Double
Ratchet, sender keys for groups), typed retry recovery, receipt/ack plumbing,
typed decryption-failure signaling, and send discipline — no silent drops,
bounded event backpressure.

**Not mitigated / deferred:**

- **Signed-prekey rotation is not yet periodic** — signed prekeys upload at
  pairing, but no scheduled rotate/republish cycle exists yet (State of the Art
  gap; tracked for a later phase).
- **Group read-side is deferred (GRPM-01)** — group membership work here is
  participants-for-fanout only; group history/sync read-side is not implemented.
- **Presence / `inactive` nuances are Phase 8** — presence semantics beyond
  parsing the `inactive` receipt type are out of scope here.
- Ban-safety pacing (rate limits, warm-up, circuit breaker) is Phase 5 — see
  the Compliance note; this phase adds no pacing of its own.

### Sample messaging mode

`samples/Iwasug.WhatsApp.Sdk.PairingSample` pairs a real linked account and
exercises messaging against a real phone (public API only):

```bash
# Pair once, persisting credentials:
dotnet run --project samples/Iwasug.WhatsApp.Sdk.PairingSample -- --code <digits> --store <dir>

# Send one message (…@g.us JID takes the group/sender-key path), then stream events:
dotnet run --project samples/Iwasug.WhatsApp.Sdk.PairingSample -- --store <dir> --send 15551234567@s.whatsapp.net "hello"
dotnet run --project samples/Iwasug.WhatsApp.Sdk.PairingSample -- --store <dir> --send 123-456789@g.us "hello group"

# Quoted reply, pure receive side, and multi-account selection:
dotnet run --project samples/Iwasug.WhatsApp.Sdk.PairingSample -- --store <dir> --reply <jid> <quoted-message-id> "text"
dotnet run --project samples/Iwasug.WhatsApp.Sdk.PairingSample -- --store <dir> --listen
dotnet run --project samples/Iwasug.WhatsApp.Sdk.PairingSample -- --store <dir> --account <id> --listen
```

Messaging mode refuses a fresh store (it would attempt registration instead of
login), requires paired credentials in `--store <dir>`, and streams events until
Ctrl+C — so delivery/read receipts for a sent id stay observable in one run.

## Build & test

```bash
dotnet build Iwasug.WhatsApp.Sdk.sln
dotnet test
```

Both commands run offline against local packages — no database, no network
service required.

## Compliance

WhatsApp actively detects and bans automation that violates its ToS. This
library documents the risks it mitigates (rate limiting, jitter, backoff —
planned in later phases) and the risks it cannot. There is no "100% ban-proof"
guarantee; you are responsible for how you use this software.

## License & provenance

MIT — see [LICENSE](LICENSE). This project is a clean-room implementation: no
code or schemas are copied from whatsmeow (MPL-2.0). See
[NOTICE.md](NOTICE.md) for the provenance policy.
