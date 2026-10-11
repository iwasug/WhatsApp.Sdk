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
| 2026-10-09 | whatsmeow `handshake.go` @ `main` (MPL-2.0, behavioral read-only — no code, schema, or bytes copied): message C is `HandshakeMessage{ClientFinish: {Static, Payload}}` where Static is the ENCRYPTED PERSISTED store NoiseKey (not a fresh key) and Payload is the marshalled ClientPayload selected by stored-credential presence (`cli.Store.GetClientPayload()`); nil payload fails the handshake; post-handshake frames dispatch as binary nodes | ClientPayload-in-message-C seam + persisted-static resume identity + node-tag pump dispatch (behavior reference; C# independently written) | `src/.../Pairing/HandshakePayloadProvider.cs`, `src/.../Pairing/ClientPayloadFactory.cs`, `src/.../Noise/NoiseHandshake.cs` (persisted-static overload), `src/.../Connection/WaConnection.cs` (success-gated pump) |
| 2026-10-09 | Baileys `src/Utils/validate-connection.ts` @ `0af2386` (MIT): `generateRegistrationNode` vs `generateLoginNode` — shared base (`connectType` WIFI_UNKNOWN, `connectReason` USER_ACTIVATED, `userAgent` triple + platform/release/os/device/mnc/mcc/locale, `webInfo` only when not android, optional `pushName`); login adds `passive: true, pull: true, username: +user, device, lidDbMigrated: false`; registration adds `passive: false, pull: false, devicePairingData{buildHash = md5(version.join('.')), deviceProps, eRegid = BE(regId), eKeytype, eIdent, eSkeyId = BE(keyId, 3), eSkeyVal, eSkeySig}`; companion descriptor (`os`, `platformType`, `requireFullSync`, history-sync flags, fixed version 10.15.7) | Registration/login ClientPayload contents + DeviceProps roles (behavior reference; schema hand-written with repo-local v1 field numbers, pinned by fixtures) | `src/.../Proto/client_payload.proto`, `src/.../Proto/companion_reg.proto`, `src/.../Pairing/ClientPayloadFactory.cs` |
| 2026-10-09 | Baileys `src/Utils/generics.ts` @ `0af2386` (MIT): `encodeBigEndian(e, t = 4)` fixed-width big-endian; bundled `baileysVersion = [2, 3000, 1043857760]`; Baileys `src/Defaults/index.ts` @ `0af2386` (MIT): `KEY_BUNDLE_TYPE = Buffer.from([5])`, `DICT_VERSION = 3` | Payload integer encodings + key-bundle type byte + bundled app version (behavior reference; C# independently written) | `src/.../Pairing/ClientPayloadFactory.cs` |
| 2026-10-10 | whatsmeow `pair-code.go` @ `main` (MPL-2.0, behavioral read-only — no code, schema, or bytes copied): random 5 bytes → Crockford base32 encoding `XXXX-XXXX`, PBKDF2-SHA256 131072 iterations to generate link-code key, AES-256-CTR wrap of ephemeral public key (5B→8C code format), phone-number validation rules (digits >6, no leading 0) | Link-code key derivation flow + phone-number validation schema (behavior reference; C# independently written using NSec + in-box BCL) | `src/Iwasug.WhatsApp.Sdk/Pairing/LinkCodeCrypto.cs`, `src/Iwasug.WhatsApp.Sdk/Pairing/PairingCodeGenerator.cs`, `tests/Iwasug.WhatsApp.Sdk.Tests/Pairing/LinkCodeTests.cs` |
| 2026-10-09 | 03-RESEARCH.md Code Example 4 (recorded research; cites whatsmeow `pair.go` @ `main` MPL-2.0 behavioral read-only + Baileys `src/Utils/validate-connection.ts` @ `0af2386` configureSuccessfulPairing MIT read-only — no new upstream reads, no code/schema/bytes copied): HMAC-SHA256 over the adv details with the adv secret; account/device signature prefixes `{6,0}`/`{6,1}`; verify → persist (awaited) → reply → emit success ordering | Pair-success verification chain + success ordering (behavior reference; schema hand-written with repo-local v1 field numbers, pinned by the golden fixture) | `src/.../Proto/adv.proto`, `src/.../Pairing/PairSuccessVerifier.cs`, `src/.../Pairing/PairingCoordinator.cs` (03-04) |
| 2026-10-11 | Baileys `WAProto/WAProto.proto` @ `0af2386` (MIT): `SignalMessage` (ratchetKey=1, counter=2, previousCounter=3, ciphertext=4), `PreKeySignalMessage` (preKeyId=1, baseKey=2, identityKey=3, message=4, registrationId=5, signedPreKeyId=6), `SenderKeyMessage`/`SenderKeyDistributionMessage` (id=1, iteration=2, chainKey/ciphertext=3, signingKey=4), `SessionStructure` (+ nested Chain/ChainKey/MessageKey/PendingPreKey with live numbers incl. PendingPreKey.signedPreKeyId=3) and `SenderKeyRecordStructure` (+ nested state structures) | Signal session/record schema field numbers (behavior reference; schema hand-written, C# independently written) | `src/.../Proto/signal.proto`, `src/.../Signal/DoubleRatchet.cs` (Task-2 serialization) |
| 2026-10-11 | Baileys `src/Signal/libsignal.ts` @ `0af2386` (MIT; current path of the planned `src/Utils/signal-utils.ts` read — that file no longer exists at HEAD): pkmsg version-byte check `(version & 0xf) !== 3`, `sigType === 3 → 'pkmsg'` enc-type mapping, pkmsg identity extraction asserting 33-byte DJB identity, per-device `ProtocolAddress(user, device)` session keying, TOFU identity handling | Signal wire version byte, pkmsg/msg mapping, 33-byte DJB identity encoding, per-device session keying (behavior reference; C# independently written) | `src/.../Signal/SignalCipher.cs` (version/MAC/DJB layout), `src/.../Signal/DoubleRatchet.cs` (per-device keying, Task 2), `tests/.../TestData/signal-kats.json` |
| 2026-10-11 | Baileys `src/Utils/signal.ts` @ `0af2386` (MIT): `xmppSignedPreKey`/`xmppPreKey` node shapes (`id` 3-byte BE, `value` 32B, `signature`), upload IQ (`registration` 4-byte BE, `type` KEY_BUNDLE_TYPE, `identity` 32B raw, `list`, `skey`), fetch-response parse (`registration` 4-byte BE uint, `identity` 32B → DJB via `generateSignalPubKey`, per-key `id` 3-byte BE) | Prekey upload/fetch IQ shapes + byte widths (behavior reference; C# independently written) | `src/.../Signal/PreKeyManager.cs` (Task 3), `src/.../Domain/PreKeyRecord.cs` |
| 2026-10-11 | Baileys `src/Utils/crypto.ts` @ `0af2386` (MIT): `generateSignalPubKey = 0x05-prefixed 33-byte DJB form`, `signedKeyPair` signs the DJB-encoded signed-prekey public with the identity private | DJB identity encoding + signature scope (behavior reference; C# independently written) | `src/.../Signal/SignalCipher.cs` (DJB helpers), `src/.../Signal/X3dh.cs` (SignWithIdentity/VerifySignedPreKey) |
| 2026-10-11 | Signal X3DH specification rev 1 §2.1–§2.2/§3.3 (public domain, read 2026-10-11): DH1=DH(IKA,SPKB)/DH2=DH(EKA,IKB)/DH3=DH(EKA,SPKB)/DH4=DH(EKA,OPKB), KDF input `F ‖ KM` (F = 32 0xFF bytes for X25519, salt zeros, info label), `Encode` = type byte + u-coordinate, `Sig` = XEdDSA | X3DH construction (spec-derived, independently written C#; the XEdDSA-vs-Ed25519 gap is a tracked UAT risk, not a copy) | `src/.../Signal/X3dh.cs` |

## PR checklist

- [ ] No code, `.proto` files, or generated output copied from an MPL-2.0 (or
      other copyleft) source.
- [ ] Any protocol constant or structure introduced cites its source.
- [ ] Provenance log updated when upstream source was read during development.
