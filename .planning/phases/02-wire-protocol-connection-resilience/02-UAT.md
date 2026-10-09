---
status: testing
phase: 02-wire-protocol-connection-resilience
source: [02-VERIFICATION.md]
started: 2026-10-09T04:56:57Z
updated: 2026-10-09T04:56:57Z
---

## Current Test

number: 1
name: Live-server handshake and session proof
expected: |
  Connect against the real `wss://web.whatsapp.com`: message A is accepted and message B parses
  (envelope tags 18/26/34, intro ‖ length-prefixed frame); the pump sustains the session without
  a 408-retry loop; after an induced network drop the SDK reconnects autonomously with jittered
  backoff and emits the typed state sequence `connecting → open → closing → connecting → open`.
awaiting: user response

## Tests

### 1. Live-server handshake and session proof
expected: Message A accepted, message B parses (envelope tags 18/26/34, intro ‖ length-prefixed frame); session sustained with at least one correlated query/reply and no 408-retry loop; after an induced drop the SDK reconnects autonomously with jittered backoff and emits `connecting → open → closing → connecting → open`.
result: [pending]

## Summary

total: 1
passed: 0
issues: 0
pending: 1
skipped: 0
blocked: 0

## Gaps
