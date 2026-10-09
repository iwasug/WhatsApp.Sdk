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
| 2026-10-08 | Baileys `src/WABinary/constants.ts` @ `0af2386` (MIT): `SINGLE_BYTE_TOKENS` (236), `DOUBLE_BYTE_TOKENS` (4×256), `TOKEN_MAP` build order | Token-dictionary bytes + single-then-double lookup precedence | `src/.../Codec/TokenDictionary.cs` (ported table), `src/.../Codec/BinaryNodeCodec.cs` (lookup order) |
| 2026-10-08 | Baileys `src/WABinary/encode.ts` + `decode.ts` @ `0af2386` (MIT): list framing, nibble/hex packing, JID forms, flag-byte rule | Codec logic SHAPE only (behavioral reference — algorithms independently written in C#; no code copied) | `src/.../Codec/BinaryNodeCodec.cs`, `src/.../Transport/FrameReader.cs` |
| 2026-10-09 | Baileys `src/Utils/noise-handler.ts` @ `0af2386` (MIT): intro header sent once per connection + 3-byte big-endian frame prefix; `src/Socket/socket.ts` @ `0af2386`: message A sent as bare protobuf; `WAProto/index.js` @ `0af2386`: outer envelope tags 18/26/34 | Handshake wire shape — intro-once framing, length-prefixed frames, bare `HandshakeMessage` envelopes (behavior reference; C# independently written) | `src/.../Transport/WaFrame.cs`, `src/.../Noise/NoiseHandshake.cs`, `src/.../Connection/WaConnection.cs` |

## PR checklist

- [ ] No code, `.proto` files, or generated output copied from an MPL-2.0 (or
      other copyleft) source.
- [ ] Any protocol constant or structure introduced cites its source.
- [ ] Provenance log updated when upstream source was read during development.
