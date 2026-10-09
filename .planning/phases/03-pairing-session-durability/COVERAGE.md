# API Coverage — WhatsApp Web (web.whatsapp.com) device-linking surface

> Full coverage by default. Opt-outs are explicit, reasoned decisions.
> Produced at plan time (plan:pre) by the api-coverage detector over the Phase 3 scope.
> Detector signals fired on first-party "SDK" vocabulary; the genuinely external surface this
> phase touches is the WhatsApp Web protocol endpoints, so the matrix is written against that
> capability set rather than declared away.

| capability | decision | reason |
|---|---|---|
| WebSocket session (wss://web.whatsapp.com/ws/chat) carrying login/registration `ClientPayload` in Noise message C | INTEGRATE | CONN-03 resume depends on it; transport landed in Phase 2 |
| QR device pairing (`pair-device` / `pair-success` iqs, ref rotation, `<success>`) | INTEGRATE | CONN-01 |
| Pairing-code linking (`link_code_companion_reg`: `companion_hello` → notification → `companion_finish`) | INTEGRATE | CONN-02 |
| Connect-failure code classification (401/403/405/406/408/411/428/440/515) driving typed lifecycle events | INTEGRATE | CONN-01 event vocabulary + Phase 2 reconnect policy |
| Runtime version resolution (web.whatsapp.com/sw.js `client_revision`, 6h cache, 405 invalidation) | INTEGRATE | 03-RESEARCH Pattern 4 — PITFALLS assigns it to this phase |
| Send/receive messages, receipts, retries, groups | OPT-OUT | Phase 4 (Signal E2EE & Messaging) scope |
| Presence, reactions, edit/delete, broadcast, durable outbox | OPT-OUT | Phase 8 scope |
| Media upload/download (mmg.whatsapp.net + CDN) | OPT-OUT | Phase 6 scope |
| App-state sync and multi-device directory (LID) sync | OPT-OUT | No v1 requirement — dropped from the roadmap |
| Push notifications (FCM/APNs) | OPT-OUT | Not part of a linked-client SDK surface |
| Live-server interop job against the real endpoints | OPT-OUT | Phase 9 ships the drift-detecting live job; this phase proves pairing on loopback + human end-of-phase verification |
