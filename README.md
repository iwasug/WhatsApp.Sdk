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

Phase 1 (Foundation & Store Seam) — walking skeleton:

- Root solution with two packable projects: the SDK and a published store
  contract test kit (`Iwasug.WhatsApp.Sdk.Testing`).
- First store seam wired end-to-end: `AccountId` → async factory →
  account-scoped `ISignalSessionStore` → typed `SignalSessionRecord`
  write/read, proven by an abstract contract suite running against the
  in-memory reference implementation.

Wire protocol, pairing, encryption, messaging, and ban-safety layers arrive in
later phases.

## In-memory reference store — read this

The SDK ships an **in-memory reference store** (`InMemoryStoreFactory`) so the
seam works out of the box during prototyping. It is honestly positioned:

- It **does not survive process restart** — all state is lost when the process
  exits.
- It is suitable for prototyping, tests, and low-volume experiments — **not**
  as production persistence.
- In production, register your own store implementations (EF Core, Dapper,
  files, …) behind the same interfaces; the host owns persistence by design.

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
