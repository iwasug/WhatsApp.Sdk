# WhatsApp SDK for .NET (sdk-wa)

## What This Is

An unofficial, open-source .NET 10 library (published to NuGet) that implements the WhatsApp Web multi-device protocol end-to-end — comparable in capability to whatsmeow (Go) — for building customer service and broadcast integrations. It links real WhatsApp accounts via QR pairing, exchanges end-to-end encrypted messages, and is designed from the ground up to run many concurrent accounts safely.

## Core Value

Reliably send and receive messages through a linked WhatsApp account — across many accounts at once, without getting those accounts banned.

## Requirements

### Validated

(None yet — ship to validate)

### Active

- [ ] Link a WhatsApp account via QR pairing and keep the multi-device session alive (reconnect/resume without re-pairing)
- [ ] Send and receive text messages end-to-end encrypted (full Signal protocol per multi-device spec)
- [ ] Run many concurrent accounts (tens+) in one process with isolated sessions
- [ ] Persist session state through a pluggable store interface (no hard DB dependency)
- [ ] Ban-safety layer: rate limiting, jitter, exponential backoff, circuit breaker, account warm-up, human-like patterns, quality-score monitoring
- [ ] Send and receive media (images, documents, voice as scoped in requirements)
- [ ] Ship as a consumable open-source package: NuGet, docs, CI, tests

### Out of Scope

- Official WhatsApp Business Cloud API — this project targets the unofficial multi-device web protocol specifically
- "100% ban-proof" guarantee — impossible to promise; v1 delivers layered mitigations that materially lower risk
- WhatsApp legacy (non multi-device) protocol — superseded, not worth implementing
- Heavy group-admin tooling (broadcast lists, group management) — deferred until core 1:1 messaging is proven

## Context

- **Reference implementation:** [tulir/whatsmeow](https://github.com/tulir/whatsmeow) (Go, MPL-2.0) proves the protocol is implementable and documents the moving parts: QR device pairing, Noise XX handshake, protobuf binary framing over WebSocket, Signal end-to-end encryption (session establishment + double ratchet), media encryption/upload, app-state sync. Used as a *behavioral/protocol reference only* — code is not copied (MPL-2.0 obligations + clean-room hygiene).
- **Domain reality:** WhatsApp actively detects and bans automation that violates its ToS. Customer service/broadcast at scale through the unofficial protocol is inherently risky; safety mechanisms are product features, not nice-to-haves.
- **Usage shape:** library embedded in the user's .NET application (not a hosted service) — the SDK exposes events/callbacks and leaves message routing, CRM integration, and webhooks to the host application.
- **Audience:** the author's own customer-service/broadcast use first, open-source community second.

## Constraints

- **Tech stack**: .NET 10 LTS, C# — current LTS, longest support runway
- **State**: pluggable store interface — many accounts in production means the host owns persistence (EF Core, Dapper, files, etc.)
- **Ban safety**: technical + operational mitigations are v1 requirements, not future work — decided during questioning
- **License**: open source, published publicly — must avoid copying MPL-2.0 code from whatsmeow; reference protocol behavior and public docs instead
- **Compliance**: usage must respect WhatsApp ToS; SDK documents the risks it mitigates and the risks it cannot

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| Full protocol reimplementation in .NET (not a whatsmeow sidecar/bridge) | User wants a real SDK, idiomatic .NET, no Go binary dependency | — Pending |
| Library (NuGet) shape, not a hosted service | Embeddable in customer-service apps; user's chosen distribution | — Pending |
| Ban-safety built from v1 | Broadcast/CS use case dies if accounts get banned | — Pending |
| Pluggable session store | Multi-tenant scale; host application owns its database | — Pending |
| .NET 10 LTS target | Current LTS with longest support window | — Pending |
| Open source publication | Community adoption; user's explicit choice | — Pending |

## Evolution

This document evolves at phase transitions and milestone boundaries.

**After each phase transition** (via `/gsd-transition`):
1. Requirements invalidated? → Move to Out of Scope with reason
2. Requirements validated? → Move to Validated with phase reference
3. New requirements emerged? → Add to Active
4. Decisions to log? → Add to Key Decisions
5. "What This Is" still accurate? → Update if drifted

**After each milestone** (via `/gsd-complete-milestone`):
1. Full review of all sections
2. Core Value check — still the right priority?
3. Audit Out of Scope — reasons still valid?
4. Update Context with current state

---
*Last updated: 2026-10-07 after initialization*
