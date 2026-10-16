# Phase 8 coverage scope

Phase 8 (Broadcast & Extended Messaging) adds **local WhatsApp protocol message branches**
(`ReactionMessage` at `Message.reactionMessage = 46` and `ProtocolMessage` at
`Message.protocolMessage = 12`) pinned to Baileys `WAProto/WAProto.proto @ 0af2386`,
plus the in-repo outbox store seam. No other `Message` oneof branches, no new
protocol endpoints, and no new NuGet dependencies are introduced by this phase.

Local scope declared:
- `ReactionMessage` (reaction send/receive/remove via empty emoji) — EXT-03
- `ProtocolMessage` type `REVOKE` (delete-for-everyone) and `MESSAGE_EDIT` (edit) — EXT-04
- In-repo outbox store seam + pump + dead-letter — EXT-01
- Broadcast fan-out as gated 1:1 sends + outcome report — BCT-01
- Presence node signalling + inbound peer presence — EXT-02

Out of scope for Phase 8 (deferred): media-bearing outbox payloads (text-first only),
cross-account presence tables, capped-parallel broadcast fan-out.