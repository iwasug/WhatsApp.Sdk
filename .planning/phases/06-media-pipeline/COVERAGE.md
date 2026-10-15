# Phase 06 — API Coverage

No external API integration: the detector signals ("surface api", "consumes api") match the
SDK's own public media API surface (`Messenger.SendImageAsync` and siblings) and the
WhatsApp/Web mmg endpoints that are this library's subject matter. WhatsApp endpoints are
reached only through the in-box `HttpClient` inside the SDK's own upload/download pipeline —
they are the protocol being implemented, not a third-party service integration, SDK, or
dependency the SDK wraps. No OAuth, no third-party REST/GraphQL/gRPC client, no webhook
consumer, no external API key surface exists in this phase's scope (gaps 1–4 are receive
wiring, retry exchange wiring, audit facts, and tracer facts over code that already ships).

Every capability therefore starts INTEGRATE with the declaration above; no OPT-OUT rows are
required.
