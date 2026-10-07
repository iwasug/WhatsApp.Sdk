# NOTICE — Clean-Room Provenance Policy

This repository implements the WhatsApp Web multi-device protocol from public
documentation and first-party protocol observation. It is an **independent,
clean-room implementation** and must stay that way.

## The whatsmeow rule (MPL-2.0)

[tulir/whatsmeow](https://github.com/tulir/whatsmeow) (Go) is the reference
implementation that proves the protocol is implementable. It is licensed
**MPL-2.0** — a file-level copyleft license. Therefore, in this repository:

- **No verbatim copying** of code, `.proto` files, schemas, or generated output
  from whatsmeow (or any MPL-2.0 / copyleft-licensed source).
- **No mechanical translation** of upstream files into C# (line-for-line or
  structure-for-structure porting counts as copying).
- **No header-stripped files** — never paste an upstream file and delete its
  license header.
- **Behavioral reference only** — reading upstream source to understand protocol
  *behavior* (message flow, field semantics, state transitions) is permitted;
  the resulting C# must be independently written.

The same rule applies to any other copyleft source. Permissively-licensed
sources (e.g. Baileys, MIT) may inform shapes, but this repo still prefers
independent implementation from the protocol's public behavior.

## Provenance recording

Whenever upstream source is read while implementing a protocol detail:

1. Record the **upstream file path + line range** and what behavior was learned
   in this file (or the relevant PR/commit message).
2. Cite the source for any protocol constant, structure, or state machine that
   results — "where did this magic value come from" must always be answerable.

### Log

| Date | Upstream file:lines | What was learned | Where it is used |
|------|--------------------|------------------|------------------|
| _(none yet — Phase 1 contains zero protocol code)_ | | | |

## PR checklist

- [ ] No code, `.proto` files, or generated output copied from an MPL-2.0 (or
      other copyleft) source.
- [ ] Any protocol constant or structure introduced cites its source.
- [ ] Provenance log updated when upstream source was read during development.
