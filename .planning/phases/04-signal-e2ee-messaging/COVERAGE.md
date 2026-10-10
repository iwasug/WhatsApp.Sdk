# API Coverage — WhatsApp Web (web.whatsapp.com) messaging surface

> Full coverage by default. Opt-outs are explicit, reasoned decisions.
> Produced at plan time (plan:pre) by the api-coverage detector over the Phase 4 scope.
> Detector result: `detected: false` — no external API/SDK integration surface (no REST/GraphQL
> client, no third-party service SDK) is introduced this phase. The genuinely external surface
> Phase 4 touches is the WhatsApp Web protocol itself, which this SDK implements end-to-end over
> its own Phase-2/3 transport — the protocol surface IS the product, not an integration with one.
> The matrix below is written against that protocol capability set rather than declared away.

| capability | decision | reason |
|---|---|---|
| Signal X3DH + Double Ratchet session establishment and message encryption (`pkmsg`/`msg` enc children) | INTEGRATE | MSG-01; clean-room over NSec + BCL, byte layout pinned by the Wave-0 spike fixtures (plan 04-01) |
| Prekey upload/fetch IQs (`encrypt` namespace) and USync device-list IQs (`usync` namespace) | INTEGRATE | MSG-01/MSG-03 prerequisite; shapes pinned from Baileys/whatsmeow behavioral reads with NOTICE rows |
| Message send/ack correlation (client `3EB0…` ids, `ack`/error node attrs) | INTEGRATE | MSG-03; rides the Phase-3 correlated transport, no new dependency |
| Delivery/read receipts and per-message mark-read (`receipt` nodes) | INTEGRATE | MSG-04 (plan 04-04) |
| Retry receipts (decrypt-failure recovery, count cap, keys node) | INTEGRATE | MSG-06 (plan 04-04) |
| Sender-key group messaging (SKDM pairwise fanout, signed `skmsg`, phash) | INTEGRATE | MSG-02 (plan 04-05) |
| Full group read-side / membership sync / admin surfaces (`groupMetadata` beyond participants-for-fanout) | OPT-OUT | GRPM-01 deferred to v2; Phase 4 fetches participants-for-fanout only (research OQ7) |
| Own-device sync fanout (`DeviceSentMessage` self-echo) | OPT-OUT | Assumption A3 — own JIDs excluded from fanout; self-sync is a nicety no success criterion requires (research OQ3) |
| Presence, typing/recording indicators, `inactive`-receipt coupling | OPT-OUT | EXT-02 / Phase 5-8 scope — Phase 4 sends delivery receipts without presence coupling (research OQ4) |
| Reactions, edit/delete, broadcast, ephemeral | OPT-OUT | Phase 8 scope |
| Media upload/download (mmg.whatsapp.net + CDN) | OPT-OUT | Phase 6 scope |
| Ban-safety throttling of the new send paths | OPT-OUT | Phase 5 scope — Phase 4 serializes sends with a plain semaphore and never auto-retries; the safety gate lands next phase |
| Periodic signed-prekey rotation | OPT-OUT | Noted in the README risk ledger (research State of the Art — upstream ecosystem gap); scheduling is a later phase |
| New external package or service SDK | NONE | Zero new packages this phase (NuGet or npm); crypto is NSec + BCL, serialization is Google.Protobuf, both already in-tree |
| Live-server interop job against the real endpoints | OPT-OUT | Phase 9 ships the drift-detecting live job; this phase proves messaging on loopback + fixtures with human end-of-phase UAT (documented NOT VERIFIED when no device) |
