# Phase 9 coverage scope

Phase 9 (Ship) is release engineering: packaging completeness, CI workflows, host-facing
docs, a public-API freeze, and a phone-free protocol-drift probe. It adds **no external API
integration surface to the SDK, and no new dependency of any kind** (09-RESEARCH: "No new
packages are required for this phase").

The api-coverage detector returned `detected:false` on the ROADMAP section (2026-10-17),
but the PLAN bodies necessarily contain words like api/sdk/webhook — the scan below is the
reasoned declaration that those are surface (docs, CI infra, existing SDK code under test),
not new external integrations.

Local scope declared:
- Packaging/provenance: sample + probe un-packable, icon/LICENSE/snupkg/CI-build switches,
  `scripts/verify-pack.ps1` content assertions, PublicAPI freeze, NOTICE audit rows — OPS-01, SC3
- Release pipeline: `ci.yml`, `release.yml` (nuget.org Trusted Publishing over OIDC),
  `live-interop.yml` (drift probe), `version.json` tag refspec — OPS-01/OPS-02
- Drift probe: `tools/Iwasug.WhatsApp.Sdk.InteropProbe` — a console tool that exercises the
  EXISTING shipped SDK connect path (`WaConnection` + `PairingStatus`) against
  `wss://web.whatsapp.com/ws/chat` as a black box; it is test/release infrastructure using
  the SDK's public API, not a new integration the SDK must own
- Docs: README rewrite (package readme), `docs/risk-ledger.md`,
  `docs/store-implementation.md`, `docs/webhook-recipe.md`, UAT checklist
- CI external endpoints are first-party only: GitHub Actions services and nuget.org's own
  OIDC exchange (`NuGet/login@v1`); no third-party API client exists in the repo

Out of scope for Phase 9 (deferred): `docs/webhook-recipe.md` documents a **host-side** bridge
over the shipped `ReadEventsAsync`/`WhatsAppEvent` API — the SDK ships no HTTP server and no
webhook client (REQUIREMENTS: the webhook server is host-application work). Live media-path
CI tests, coverage artifacts in CI (09-RESEARCH A5: skipped), self-hosted probe runner
(fallback only if hosted IPs are blocked), and repo-visibility automation are also out of scope.
