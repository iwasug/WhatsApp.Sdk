# Phase 4: Signal E2EE & Messaging - Research

**Researched:** 2026-10-10
**Domain:** Signal protocol (X3DH + Double Ratchet + Sender Keys) as used by WhatsApp Web multi-device; message/receipt/retry stanza flows over the existing BinaryNode transport
**Confidence:** HIGH (protocol schemas read verbatim from MIT Baileys `WAProto.proto` + whatsmeow source; wire-flow semantics cross-checked; a few byte-layout details remain [ASSUMED] and are gated behind the spike's golden-fixture step)

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| MSG-01 | Host can send and receive E2EE text in 1:1 chats | X3DH session init via `encrypt`-ns IQ prekey fetch (prekeys.go shapes), `pkmsg`/`msg` enc nodes, Double Ratchet state machine mapped onto existing `ISignalSessionStore`/`IIdentityStore`/`IPreKeyStore` seams; wire schemas quoted verbatim below |
| MSG-02 | Host can send and receive E2EE text in groups the account belongs to | Sender-key flow: SKDM pairwise fanout + `skmsg` single-ciphertext send + `phash` algorithm (verbatim); minimal group-participants fetch strategy |
| MSG-03 | Stable message IDs for send→ack→receipt correlation, documented idempotency (no silent auto-retry) | Client-generated `3EB0…` message ID format (verbatim from Baileys), server reply node attrs (`t`/`server_id`/`error`/`phash`), `QueryRouter` id-correlation reuse, `messageSendLock`-style serialization rationale |
| MSG-04 | Send/receive delivery/read receipts and mark messages read (per-message keys) | Receipt node shapes (verbatim `MarkRead`), `MessageKey` proto (per-message key), inbound receipt types incl. `sender`/`inactive`/`read_self` |
| MSG-05 | Reply quoting a specific message (per-account recent-message cache) | `ContextInfo`/`ExtendedTextMessage` schema; existing `IRecentMessageCacheStore` + `RecentMessageEntry` seam; cache write policy D-20 lands here |
| MSG-06 | Inbound decryption failures recover via retry receipts without losing messages | Full retry receipt flow (children, `registration`, `keys` node, count cap 5) read from whatsmeow `retry.go`; outgoing retry resend semantics |
| MSG-07 | Typed events (message, receipt, connection, logout, CancellationToken end-to-end) | Existing event surface (`StateChanged`, `StanzaReceived`, `PairingCoordinator` subscription pattern) + node-tag dispatch map (whatsmeow `nodeHandlers`) translated to typed .NET events |
</phase_requirements>

## Summary

Phase 4 builds the Signal protocol stack (X3DH + Double Ratchet + Sender Keys) clean-room over NSec + BCL, and the WhatsApp messaging stanza layer (message send, receipts, retries, quoting) on top of the Phase 2/3 BinaryNode transport. The protocol schemas needed for interop — `MessageKey`, `SignalMessage`, `PreKeySignalMessage`, `SenderKeyMessage`, `SenderKeyDistributionMessage`, `ContextInfo`, `SessionStructure`, `SenderKeyRecordStructure` — were read **verbatim** from Baileys' MIT-licensed `WAProto/WAProto.proto` this session and are quoted below with exact field numbers; the NOTICE.md provenance policy requires these be recorded as the schema source file-by-file. The wire-flow semantics (prekey fetch/upload IQs, USync device list, `enc` node attributes, device fanout with `<to jid=…>`, group `skmsg` + `phash`, retry receipts, `MarkRead` receipts, ack/error codes) were read directly from whatsmeow source files — **behavioral reference only, no code or schema copied** (whatsmeow is MPL-2.0).

The codebase is unusually ready for this phase: Phases 1-3 already shipped every persistence seam (`ISignalSessionStore`, `IIdentityStore`, `IPreKeyStore`, `IRecentMessageCacheStore`, `ISafetyStateStore`, `ICredentialStore`), the correlated `QueryRouter` (id-attribute correlation — the Phase-2 skeleton envelope was migrated in Phase 3), the BinaryNode codec, the internal `StanzaReceived` dispatch event, and a credential row carrying `RegistrationId`, `IdentityKeyPrivate`, and `SigningKeyPrivate`. The dominant gaps are additive: key material is absent from the domain records (`SignalIdentityRecord`, `PreKeyBundle`, `PreKeyRecordEntry` are identifier-only placeholders), no `Message*.proto` files exist, there is no stanza-handler registration seam beyond the pairing coordinator's direct subscription, and there is no outbound message/receipt send path.

**Primary recommendation:** Plan this phase as 4-5 plans — (1) Signal core crypto + session state serialization + KATs from fixtures, (2) proto schemas + wire fixtures + provenance rows, (3) message send/receive 1:1 with device fanout + ack correlation, (4) receipts/retry/quoting/recent-cache policy, (5) group sender-keys + typed event surface + UAT — with a hard spike-first gate on the Signal byte layout (version byte, padding, HMAC placement), which is the single highest-risk [ASSUMED] area.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| X3DH session establishment / prekey management | Client SDK (crypto layer) | Server (prekey store via `encrypt` ns IQ) | SDK computes DH chains and builds sessions; server only serves/deletes prekey bundles |
| Double Ratchet encrypt/decrypt | Client SDK (crypto layer) | — | Pure client-side state machine; persists via `ISignalSessionStore` |
| Sender-key group encryption | Client SDK (crypto layer) | Server (fan-out of single `skmsg`) | SDK encrypts once + signs; server fans out; SKDM distribution is per-device pairwise |
| Device-list resolution (multi-device fanout) | Client SDK | Server (USync `devices` IQ) | SDK queries USync, caches per account, encrypts per device |
| Message/receipt/retry stanza construction | Client SDK (stanza layer) | — | Node shapes are protocol contract; rides existing `WaConnection` transport |
| Message-id correlation (send→ack→receipt) | Client SDK (dispatcher) | Server (ack + receipts reference same id) | Reuse `QueryRouter`/pump correlation; receipts carry `MessageKey.id` |
| Persistence of sessions/identities/keys/cache | Host (via store seams) | SDK (in-memory fallback) | STR-01/STR-02 locked: host owns persistence; SDK sequences single-key CRUD |
| Typed event delivery | Client SDK | Host (subscriber) | SDK emits; host consumes with `CancellationToken` flowing end-to-end |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `NSec.Cryptography` | 26.4.0 (locked, already referenced) | X25519 DH (ratchet + X3DH), Ed25519 signatures (sender-key signatures, signed-prekey verification) | Already the locked gap-filler for .NET's missing X25519/Ed25519; typed-key API prevents algorithm mixups |
| In-box `System.Security.Cryptography` | .NET 10 | AES-256-CBC (message keys), HMAC-SHA256 (chain ratchet + message MAC), `HKDF` (KDF_RK), `SHA256`, `RandomNumberGenerator` | WhatsApp's message key = 32B AES + 32B HMAC + 16B IV — all BCL-reachable `[CITED: WhatsApp Security Whitepaper]` |
| `Google.Protobuf` + `Grpc.Tools` | 3.36.2 / 2.84.0 (locked, already referenced) | New `signal.proto` (session/sender-key record structures) + `message.proto` (Message/MessageKey/ContextInfo/SKDM subset) | Same pattern Phase 3 used: hand-written schemas, `GrpcServices="None"`, `internal_access`, pinned by golden fixtures |
| `System.IO.Pipelines` | 10.0.12 (locked) | Already consumed by `FrameReader` — no Phase-4 change | Framing unchanged |
| `System.Threading.Channels` | in-box | Inbound event dispatch queue + outbound send queue per account | MSG-07 typed events need bounded backpressure |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| `System.Threading.RateLimiting` | 10.0.12 | Present but **Phase 5 scope** — Phase 4 sends may serialize (whatsmeow uses a per-client `messageSendLock`) but must NOT auto-retry (MSG-03 forbids silent retry; SAF-* land next phase) | Only the serialization aspect (no parallel sends to the same session) is needed now, via a plain semaphore — not the limiter |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Hand-rolled Signal over NSec+BCL (chosen, locked) | `Noise.NET` / any Signal NuGet | No mature .NET Signal library exists (STACK.md, verified in project research); Noise.NET is dormant + native-libsodium collision — rejected |
| Hand-written protos pinned by fixtures | Vendoring whatsmeow's `.proto` | **Forbidden** — MPL-2.0 (NOTICE.md). Baileys `WAProto.proto` (MIT) is the schema source; whatsmeow is behavioral reference only |
| `Message.conversation=1` plain string for text | `ExtendedTextMessage.text=1` | Use `conversation` for plain text without quote; `extendedTextMessage` **required** when `contextInfo.quotedMessage` present (WhatsApp renders quotes only via ExtendedTextMessage) — [ASSUMED] |

**Installation:** none — no new packages this phase. Everything needed is already referenced in `Iwasug.WhatsApp.Sdk.csproj` `[VERIFIED: src/Iwasug.WhatsApp.Sdk/Iwasug.WhatsApp.Sdk.csproj:26-28]`:

```xml
<PackageReference Include="NSec.Cryptography" />
<PackageReference Include="Google.Protobuf" />
<PackageReference Include="Grpc.Tools" PrivateAssets="all" />
```

New `.proto` files join the existing `<Protobuf Include="Proto\*.proto" GrpcServices="None" OutputOptions="internal_access" />` items `[VERIFIED: src/Iwasug.WhatsApp.Sdk/Iwasug.WhatsApp.Sdk.csproj:41-53]`.

## Package Legitimacy Audit

No external packages are installed this phase — the audit gate has nothing to run against. All crypto is NSec (already in-tree) + BCL; all serialization is Google.Protobuf (already in-tree).

**Packages removed due to [SLOP] verdict:** none
**Packages flagged as suspicious [SUS]:** none

## Existing Codebase Seams (Phases 1-3 reuse inventory)

The planner MUST build on these — do not design parallel mechanisms.

| Seam | Location | What Phase 4 does with it |
|------|----------|---------------------------|
| `QueryRouter` (correlated query/reply) | `src/.../Connection/QueryRouter.cs` `[VERIFIED: QueryRouter.cs:46-62]` | Reuse for `encrypt`-ns prekey IQs, USync device IQs. Correlates by binary-node `id` attr (`q-{n}` monotonic ids); sender delegate owned by connection; exactly-one-terminal-outcome (reply/timeout/connection-lost). Tag dispatch lives in the pump "never here" |
| `WaConnection.StanzaReceived` (internal `EventHandler<BinaryNode>`) | `src/.../Connection/WaConnection.cs:202` | The Phase-4 message dispatcher subscribes here exactly like `PairingCoordinator.OnStanzaReceived` does (`PairingCoordinator.cs:725`) — copy that subscription pattern for `message`/`receipt`/`ack`/`notification` tags |
| `WaConnection.SendPairingNodeAsync(BinaryNode)` (internal) | `WaConnection.cs:696` | Generalize (or add sibling `SendNodeAsync`) for non-IQ stanzas: messages, receipts, acks, retry receipts |
| `ISignalSessionStore` / `SignalSessionRecord(string Address, byte[] SessionState)` | `src/.../Stores/ISignalSessionStore.cs` (record at file tail) | Session bytes are explicitly "SDK-owned serialized ratchet state… not a public contract" — the Double Ratchet `SessionStructure` serialization lands here; keyed by peer address (JID string — decide device-level keys, see Open Question resolution below) |
| `IIdentityStore` / `SignalIdentityRecord(string? IdentityKeyPublic)` | `src/.../Stores/IIdentityStore.cs`, `src/.../Domain/SignalIdentity.cs` | Additively extend with real key material (NSec bytes + signed-prekey signature state); XML doc already anticipates "NSec typed keys replace this field shape additively" |
| `IPreKeyStore` / `PreKeyBundle(int SignedPreKeyId, int OneTimePreKeyId)`, `PreKeyRecordEntry(int PreKeyId)` | `src/.../Stores/IPreKeyStore.cs`, `src/.../Domain/PreKeyRecord.cs` | Additively extend with key material (identity pub, signed prekey pub+sig, one-time prekey pubs, registration id). Doc: "identifiers only — the key material itself… is added additively with Phase 4's clean-room Signal implementation" |
| `IRecentMessageCacheStore` / `RecentMessageEntry(string MessageKey, string ChatJid, DateTimeOffset TimestampUtc)` | `src/.../Stores/IRecentMessageCacheStore.cs:124` `[VERIFIED: IRecentMessageCacheStore.cs:124]` | MSG-05 quoting + MSG-06 retry resend read here; `GetRecentAsync(chatJid, limit)` exists. **Gap:** entry has no content/participant field — needs additive extension (quoted message payload + sender) |
| `ICredentialStore` / `CredentialRecord(…, byte[] IdentityKeyPrivate, …, int RegistrationId)` | `src/.../Domain/CredentialRecord.cs` | Own X3DH identity: `IdentityKeyPrivate` (32B) is the long-term X25519 identity; `RegistrationId` (14-bit) feeds prekey upload + retry `registration` node. Already persisted — survives restart (CONN-03) |
| `DurableWriteRetryPolicy` + D-20 cache policy | `src/.../Stores/Runtime/DurableWriteRetryPolicy.cs`, `StoreRegistry.cs` remarks | All session/identity/prekey writes route through the retry policy; recent-cache writes use log-and-continue (D-20) — "arriving with Phase 4/5" is now |
| BinaryNode codec | `src/.../Codec/BinaryNodeCodec.cs` (public), `TokenDictionary.cs` (internal) | Message/receipt nodes ride it unchanged. **Known pitfall:** raw string content coerces to bytes on decode — only token/nibble/hex/JID strings survive as `Text` (02-04-SUMMARY); ciphertext/bytes go in `Data`, all text lives inside protobuf payloads anyway |
| Golden-fixture pattern | `tests/.../TestData/*.json` + `NOTICE.md` provenance table | New fixtures: Signal KATs, message-node wire bytes, SKDM bytes. One NOTICE row per upstream read (Baileys schema reads + whatsmeow behavior reads recorded this session must be logged when code lands) |
| Loopback transport fakes | `tests/.../Transport/LoopbackFrameTransport.cs`, `LoopbackRoundTripTests.cs:294` (`HappyPath_header_handshake_query_open_over_loopback`) | Extend the loopback peer to speak `message`/`receipt`/`ack` stanzas for end-to-end proofs without a live phone |
| Test runner | repo-root `global.json` MTP mode; `dotnet test Iwasug.WhatsApp.Sdk.sln` | Unchanged; xunit.v3 + AwesomeAssertions + NSubstitute |

## Architecture Patterns

### System Architecture Diagram

```
Host application
  │  SendMessageAsync(to, text, ct) / IAsyncEnumerable<WhatsAppEvent> ct
  ▼
MessagingFacade (per account) ──────────────────────────────┐
  │ 1. resolve devices (USync IQ ── QueryRouter ──► ws)     │ events:
  │ 2. ensure sessions (encrypt IQ prekey fetch)            │  MessageReceived,
  │ 3. X3DH init or Double-Ratchet encrypt (per device)     │  ReceiptReceived,
  │    └ persist session (DurableWriteRetryPolicy)          │  MessageAcked,
  │ 4. build <message id t to type><participants>           │  DecryptionFailed,
  │    <to jid><enc v=2 type=msg|pkmsg>…                    │  ConnectionStateChanged,
  │    [device-identity]</message> ──► ws transport         │  LoggedOut
  │ 5. correlate reply (id) → return SendResult(id)         │
  ▼                                                        ◄─┘
Inbound pump (WaConnection) ── StanzaReceived(BinaryNode)
  │ tag=message → decrypt (pkmsg→X3DH→ratchet / msg→ratchet / skmsg→sender key)
  │   ├ success → dispatch MessageReceived + cache RecentMessageEntry + send ack + delivery receipt
  │   └ failure → send ack(error?) + receipt type=retry (count+1, registration[, keys])
  │ tag=receipt → dispatch ReceiptReceived; retry receipts trigger resend from recent cache
  │ tag=ack     → correlate to pending sends (class=message) → MessageAcked
  ▼
Store seams (host-owned): sessions │ identities │ prekeys │ recent cache │ credentials │ safety
```

### Recommended Project Structure

```
src/Iwasug.WhatsApp.Sdk/
├── Signal/                 # clean-room X3DH + Double Ratchet + Sender Keys (internal)
│   ├── X3dh.cs             # bundle processing, SK/AD derivation
│   ├── DoubleRatchet.cs    # ratchet state machine + SessionStructure serialization
│   ├── SignalCipher.cs     # AES-256-CBC+HMAC message encrypt/decrypt, version/padding bytes
│   ├── SenderKey.cs        # sender chain state + SenderKeyMessage sign/verify
│   └── PreKeyManager.cs    # own prekey generation + upload IQ (encrypt ns)
├── Messaging/
│   ├── MessageBuilder.cs   # Message/ExtendedTextMessage/ContextInfo proto → plaintext bytes
│   ├── MessageSender.cs    # node construction, device fanout, reply correlation
│   ├── MessageDispatcher.cs# StanzaReceived subscription: message/receipt/ack tags
│   ├── ReceiptHandler.cs   # delivery/read/retry receipt send + inbound receipt parsing
│   ├── DeviceDirectory.cs  # USync devices IQ + per-account cache
│   └── MessagingEvents.cs  # typed event surface (MSG-07)
├── Proto/
│   ├── signal.proto        # SessionStructure, SenderKeyRecordStructure, SignalMessage,
│   │                       #   PreKeySignalMessage, SenderKeyMessage, SKDM (from Baileys MIT)
│   └── message.proto       # Message, MessageKey, ContextInfo, ExtendedTextMessage subset
└── (existing folders unchanged)
```

### Pattern 1: Wire schemas (verbatim from Baileys `WAProto.proto`, MIT)

**What:** the exact field numbers Phase 4's hand-written protos must reproduce (interop = byte-compat; record these in NOTICE.md as the schema source).

```proto
// Source: raw.githubusercontent.com/WhiskeySockets/Baileys/master/WAProto/WAProto.proto [VERIFIED, read 2026-10-10]
message MessageKey {
    optional string remoteJid = 1;
    optional bool fromMe = 2;
    optional string id = 3;
    optional string participant = 4;
}
message SignalMessage {
    optional bytes ratchetKey = 1;
    optional uint32 counter = 2;
    optional uint32 previousCounter = 3;
    optional bytes ciphertext = 4;
}
message PreKeySignalMessage {
    optional uint32 registrationId = 5;
    optional uint32 preKeyId = 1;
    optional bytes baseKey = 2;
    optional bytes identityKey = 3;
    optional bytes message = 4;       // serialized SignalMessage
    optional uint32 signedPreKeyId = 6;
}
message SenderKeyMessage { optional uint32 id = 1; optional uint32 iteration = 2; optional bytes ciphertext = 3; }
message SenderKeyDistributionMessage {   // "axolotl" inner form
    optional uint32 id = 1; optional uint32 iteration = 2;
    optional bytes chainKey = 3; optional bytes signingKey = 4;
}
// Message (outer): conversation = 1; senderKeyDistributionMessage = 2; extendedTextMessage = 6;
//   deviceSentMessage = 31; messageContextInfo = 35
// Message.senderKeyDistributionMessage (WA wrapper): groupId = 1; axolotlSenderKeyDistributionMessage = 2
message ExtendedTextMessage { optional string text = 1; optional ContextInfo contextInfo = 17; /* … */ }
message ContextInfo {
    optional string stanzaId = 1; optional string participant = 2;
    optional Message quotedMessage = 3; optional string remoteJID = 4; repeated string mentionedJID = 15;
}
message SessionStructure {                     // ratchet state persistence format
    optional uint32 sessionVersion = 1; optional bytes localIdentityPublic = 2;
    optional bytes remoteIdentityPublic = 3; optional bytes rootKey = 4;
    optional uint32 previousCounter = 5; optional Chain senderChain = 6;
    repeated Chain receiverChains = 7; optional PendingPreKey pendingPreKey = 9;
    optional uint32 remoteRegistrationId = 10; optional uint32 localRegistrationId = 11;
    optional bool needsRefresh = 12; optional bytes aliceBaseKey = 13;
    // Chain { senderRatchetKey=1; senderRatchetKeyPrivate=2; ChainKey{index=1,key=2}=3; MessageKey{index=1,cipherKey=2,macKey=3,iv=4}=4 }
}
message SenderKeyRecordStructure { repeated SenderKeyStateStructure senderKeyStates = 1; }
// SenderKeyStateStructure { senderKeyId=1; SenderChainKey{iteration=1,seed=2}=2;
//   SenderSigningKey{public=1,private=2}=3; repeated SenderMessageKey{iteration=1,seed=2}=4 }
```

**When to use:** always — this is the interop contract. Trim to the subset above (Phase 4 text-only scope); do not import media/payment fields.

### Pattern 2: 1:1 send flow (behavioral reference: whatsmeow `send.go`, read verbatim this session)

```csharp
// Source: raw.githubusercontent.com/tulir/whatsmeow/main/send.go [VERIFIED read; MPL-2.0 behavioral reference — C# independently written]
// Message id: Baileys MIT: '3EB0' + 18 random bytes, uppercase hex → 40 chars
string id = "3EB0" + Convert.ToHexString(RandomNumberGenerator.GetBytes(18)); // 40-char
// Node attrs (verbatim from prepareMessageNode):
//   attrs := waBinary.Attrs{ "id": id, "type": msgType, "to": to }   (+ optional addressing_mode)
// Per-device content (verbatim shape):
//   <participants> → per device: <to jid="{deviceJid}"><enc v="2" type="msg|pkmsg">{ciphertext}</enc></to>
// enc attrs (verbatim):
//   encAttrs := waBinary.Attrs{ "v": "2", "type": "msg" }
//   if ciphertext.Type() == protocol.PREKEY_TYPE { encAttrs["type"] = "pkmsg" }
// includeDeviceIdentity := encAttrs["type"] == "pkmsg"  → append <device-identity>{marshalled Account}</device-identity>
// Preprocess: for each device without a session → fetch prekey bundle, ProcessBundle, then encrypt.
// Serialize sends (whatsmeow messageSendLock): never two in-flight sends against one account's sessions.
// Server reply for the message id carries attrs: t, server_id, error, phash (error!=0 → failure;
// phash mismatch → some devices missed it, invalidate device cache).
```

### Pattern 3: Prekey fetch / upload / device list (behavioral reference: whatsmeow `prekeys.go`, `user.go`, verbatim)

```csharp
// Fetch peer bundles [VERIFIED: raw.githubusercontent.com/tulir/whatsmeow/main/prekeys.go read this session]:
//   <iq xmlns="encrypt" type="get" to="s.whatsapp.net">
//     <key><user jid="{jid}" reason="identity"/>…</key></iq>
// Response per user: <user jid> { <registration>4B big-endian</registration>
//   [ <keys> ] <identity>32B</identity> <key><id>3B</id><value>32B</value></key>   // one-time, optional
//   <skey><id/><value/><signature/></skey> [ </keys> ] <error/>? }
// Upload own prekeys (counts from whatsmeow): batch 50, initial upload 812, re-upload floor 5
//   <iq xmlns="encrypt" type="set" to="s.whatsapp.net">
//     <registration>4B BE</registration> <type>{0x05}</type> <identity>{pub}</identity>
//     <list><key><id/><value/></key>…</list> <skey><id/><value/><signature/></skey></iq>
// Device list (USync) [VERIFIED: user.go read this session]:
//   <iq xmlns="usync" type="get" to="s.whatsapp.net">
//     <usync sid="{sid}" mode="query" last="true" index="0" context="message">
//       <query><devices version="2"/></query> <list><user jid="{jid}"/>…</list>
//     </usync></iq>
// Response user children carry device jids (…:0 phone excluded, …:N companions).
```

### Pattern 4: Receipts & mark-read (behavioral reference: whatsmeow `receipt.go`, `retry.go` via GitHub excerpts)

```text
# MarkRead [CITED: github.com/tulir/whatsmeow/blob/main/receipt.go] — per-message keys:
<receipt id="{firstId}" type="read" to="{chatJid}" t="{unix-seconds}" [participant="{senderJid}"]>
  <list><item id="{id2}"/><item id="{id3}"/></list>      <!-- only when >1 id -->
</receipt>
# type values: read (peer), read_self, delivery, playback, played, sender (for fromMe),
#   inactive (client not "online" — server forwards but WA apps don't render), retry
# Same-sender only per receipt; different senders → one receipt call each [CITED: receipt.go docs]
# Inbound delivery/read receipts: same node shape, ids in id attr + list/item children;
#   groups carry participant; read receipts reference MessageKey{id, participant} per message
```

### Pattern 5: Retry receipts (MSG-06; behavioral reference: whatsmeow `retry.go` via GitHub excerpt)

```text
# On decryption failure of inbound <message>:[CITED: github.com/tulir/whatsmeow/blob/main/retry.go]
<receipt id="{msgId}" type="retry" to="{sender}" t="{orig-t}" [category="peer"]>
  <retry count="{n}" id="{msgId}" t="{orig-t}" v="1"/>
  <registration>{4-byte BE registrationId}</registration>
  <!-- from retry #2 (or when sender key unavailable), also: -->
  <keys>
    <type>{0x05}</type><identity>{32B pub}</identity>
    <key><id>3B</id><value>32B</value></key>            <!-- fresh one-time prekey -->
    <skey><id/><value/><signature/></skey>
    <device-identity>{marshalled account}</device-identity>
  </keys>
</receipt>
# Cap: stop sending retry receipts at count >= 5; first retry triggers a message re-request
# from the phone (skip for SDK — no phone; rely on sender resend).
# Handling an inbound retry receipt for OUR outgoing message: look up plaintext in the
# recent-message cache (or host-provided getter), re-encrypt for the requester (fresh session
# when retry>=2/bundle included), resend as a new <message> with the SAME id, type, t and
# device_fanout="false" attr for 1:1; groups re-create + embed a fresh SKDM.
```

### Pattern 6: Group sends via sender keys (MSG-02; behavioral reference: whatsmeow `send.go`, read verbatim)

```text
[VERIFIED: raw.githubusercontent.com/tulir/whatsmeow/main/send.go read this session]
1. GroupSessionBuilder.Create → SKDM{groupId, axolotlSenderKeyDistributionMessage(serialized
   SenderKeyDistributionMessage{id, iteration, chainKey, signingKey})} wrapped as
   Message{SenderKeyDistributionMessage} plaintext; delivered pairwise to every member DEVICE
   via the normal prepareMessageNode fanout (skdPlaintext replaces the payload).
2. Group message: GroupCipher.Encrypt(pad(protobuf Message)) → SignedSerialize →
   <enc v="2" type="skmsg">{signed ciphertext}</enc> appended to the SAME <message> node.
3. phash attr (verbatim):
   participantsStrings sorted (ADString form); hash = sha256(join("", strings));
   phash = fmt.Sprintf("2:%s", base64.RawStdEncoding.EncodeToString(hash[:6]))
   ("2:" version prefix + first 6 bytes of SHA-256, standard base64 no padding)
4. Inbound SKDM handled from the decrypted Message proto, keyed by (groupJid, senderIdentity).
   Group retry receipts re-create + include a fresh SKDM.
```

### Pattern 7: Inbound dispatch & acks

```text
# whatsmeow dispatches inbound nodes by tag map (message/receipt/iq/notification/…) — the
# Phase-4 C# dispatcher does the same over WaConnection.StanzaReceived [VERIFIED seam: WaConnection.cs:202].
# Every inbound message/receipt node must be acked:[CITED: receipt.go]
<ack class="{node-tag}" id="{node-id}" to="{node-from}" [participant] [recipient] [type] [error="{code}"/>
# NACK codes seen in the wild: 487 parse, 491 invalid protobuf, 495 missing message secret,
# 496 old counter, 500 unhandled [CITED: receipt.go consts]
# Ack the node only AFTER durable session writes complete (D-06) — never before, or a crash
# loses the message silently (ties to Phase-3 success-gated-Open discipline).
```

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| X25519 / Ed25519 math | Curve arithmetic, key generation | `NSec.Cryptography` (locked) | Constant-time bugs; NSec's typed keys already prevent alg mixups |
| HKDF / HMAC / AES-CBC | Custom KDF code | BCL `HKDF`, `HMACSHA256`, `Aes` | Spec-recommended primitives; BCL is AOT/trimming-safe |
| Prekey upload/fetch, USync wire shapes | Inventing node shapes | The verbatim shapes in Patterns 2-3 | Interop is byte-exact; wrong attr names fail silently server-side |
| Message padding & version bytes | Guessing at serialization | Fixture-pinned `SignalCipher` (spike gate) | The highest-risk [ASSUMED] area — pin by KAT before wiring |
| Receipt/retry node shapes | Ad-hoc nodes | Patterns 4-5 (pinned by fixtures) | Subtle attrs (`participant` only for groups; `t` always) drive WA app rendering |
| Session serialization | Custom binary format | `SessionStructure`/`SenderKeyRecordStructure` protos (Pattern 1) | Same format the ecosystem persists — enables fixture-driven tests and future compat |

**Key insight:** nothing in the Signal stack is novel — the difficulty is byte-exactness. Every custom decision here is an interop regression risk, so every shape in this document must be pinned by a golden fixture captured from (or cross-checked against) a real WhatsApp peer.

## Common Pitfalls

### Pitfall 1: Signal wire byte-layout (version byte, padding, HMAC placement)
**What goes wrong:** messages encrypt fine in loopback tests but real peers reject/ignore them (or retry receipts loop forever).
**Why it happens:** the serialized on-wire form of `SignalMessage`/`PreKeySignalMessage` is *protobuf + framing bytes outside the protobuf* — a version byte and full HMAC appended — and the plaintext is PKCS7-style padded to 64-byte blocks before AES. Exact byte layout is [ASSUMED] this session (version byte value, MAC length/placement, exact padding flavor).
**How to avoid:** Wave-0 spike: capture ciphertexts from a live linked account (or cross-check Baileys-produced bytes via a node script) and pin them as golden fixtures; the `SignalCipher` KAT suite must be green before any network wiring. If fixtures contradict an [ASSUMED] value, the fixture wins.
**Warning signs:** peer sends retry receipt in response to *our* message; `count`/`counter` mismatch errors; decrypt works A→B but B's reply fails.

### Pitfall 2: Ratchet out-of-order handling (skipped message keys)
**What goes wrong:** messages delivered out of order or via retry resend fail to decrypt permanently.
**Why it happens:** Double Ratchet requires storing skipped message keys (MKSKIPPED) per (ratchet key, counter) with a bound; naively advancing `Nr` drops late messages.
**How to avoid:** implement the spec's MKSKIPPED map inside `SessionStructure` (bounded — cap stored keys per chain, evict oldest); test with reordered fixture deliveries.
**Warning signs:** first message after reconnect fails; decrypt failure with `ErrOldCounter`-equivalent (counter already consumed).

### Pitfall 3: Device fanout mistakes
**What goes wrong:** message reaches the phone but not companion devices (or vice versa), or sends to own devices create echo loops.
**Why it happens:** multi-device means every send resolves the target's device list (USync), encrypts per device, and must NOT encrypt for own devices with the same plaintext (whatsmeow sends `DeviceSentMessage`-wrapped plaintext to own devices — Phase 4 may skip own-device fanout entirely, but must explicitly exclude own JIDs).
**How to avoid:** `DeviceDirectory` with per-account cache + explicit own-device exclusion; assert in tests that fanout covers exactly the non-own device set.
**Warning signs:** phash mismatch warnings from the server reply; devices silently missing messages.

### Pitfall 4: Acks before persistence (message loss on crash)
**What goes wrong:** SDK acks a message, crashes before persisting the decrypted session state → sender never retries, message lost (violates MSG-06 "no lost messages").
**Why it happens:** acking is cheap and the natural instinct; D-06 requires durable writes complete before protocol acknowledgment.
**How to avoid:** order = decrypt → persist session/sender-key (DurableWriteRetryPolicy) → ack node → dispatch event → cache recent message (D-20 log-and-continue). The ack gate mirrors Phase-3's success-gated Open.
**Warning signs:** code review finds `sendAck` awaited before a store `PutAsync`.

### Pitfall 5: Session-state keying (per peer vs per device)
**What goes wrong:** sessions collide across devices of the same user, or across `@s.whatsapp.net` vs `@lid` identities for the same human.
**Why it happens:** X3DH/Double Ratchet are per-device; `IIdentityStore`/`ISignalSessionStore` are keyed by a single `string address`. WhatsApp addressing has PN vs LID dual identity and device suffixes (`user.device@server` — `Jid` already models Agent/Device `[VERIFIED: src/.../Domain/Jid.cs]`).
**How to avoid:** key sessions and identities by the **full AD JID including device index** (canonical `Jid.ToString()`); maintain PN↔LID mapping as it arrives in message metadata (Phase 3 already stores `PairedUser`; LID mapping policy noted in Open Questions — resolved below). Contract-test the key format.
**Warning signs:** decrypt failures only when the peer uses a companion device; identity-change false positives on LID/PN transitions.

### Pitfall 6: Recent-message cache as retry source vs. retention
**What goes wrong:** retry receipt arrives after cache trim → cannot resend; or cache stores plaintext forever → secret-hygiene liability.
**Why it happens:** the cache is the ONLY resend source for retry receipts (whatsmeow keeps a retry message store for restarts); it is also lossy by design (D-05).
**How to avoid:** cache entries hold the marshalled plaintext `Message` bytes keyed by message key with SDK-owned retention (IOptions policy lands here per D-08); document the trim horizon and accept "cannot resend after horizon" as a documented outcome (count>=N resends stop). Hosts needing durable retry can implement `IRecentMessageCacheStore` persistently — that seam exists exactly for this.
**Warning signs:** unbounded cache growth; retry handling that throws instead of degrading.

### Pitfall 7: Silent auto-retry (MSG-03 violation)
**What goes wrong:** send times out → SDK silently re-sends → duplicate messages on the peer side and double rate-limit pressure.
**Why it happens:** retry is the reflexive resilience pattern; Phase 5's safety gate explicitly forbids the SDK auto-retrying sends.
**How to avoid:** `SendAsync` returns exactly one outcome per call (success with id / server error / timeout / cancelled) and NEVER re-sends internally; the caller may re-call. Document idempotency: the same id must not be re-sent by the SDK even on reconnect (reconnect re-sends *frames in flight* only as the protocol requires — the Phase-3 `retryFrame` path exists for disconnect-during-send; message-level resend happens only in response to an explicit peer retry receipt).
**Warning signs:** retry loops in logs without a matching inbound retry receipt.

### Pitfall 8: Text placement & codec coercion
**What goes wrong:** `conversation` text arrives at peers as raw bytes or empty.
**Why it happens:** the codec coerces raw string node content to bytes on decode (02-04-SUMMARY) — but message text never rides node content directly; it lives inside the protobuf payload (`Message.conversation`), which is correct. The real trap is building nodes with `Text` content for ciphertext (must be `Data`) and expecting token-dictionary encoding for arbitrary text.
**How to avoid:** ciphertext always in `enc` node `Data` content; all human text inside protobufs; node attrs are the only string surface (`to`, `type`, `id`, `participant` — all JIDs/tokens the codec handles).

## Code Examples

### Example 1: Double Ratchet KDF_CK (spec-derived; pin with KATs)
```csharp
// Source: signal.org/docs/specifications/doubleratchet/ (recommendations §) [CITED]
// KDF_CK: message key = HMAC-SHA256(ck, 0x01); next chain key = HMAC-SHA256(ck, 0x02)
static (byte[] messageKey, byte[] nextChainKey) DeriveChainKey(ReadOnlySpan<byte> chainKey)
{
    byte[] mk = HMACSHA256.HashData(chainKey, [0x01]);
    byte[] ck = HMACSHA256.HashData(chainKey, [0x02]);
    return (mk, ck);
}
// KDF_RK: HKDF(salt = rootKey, ikm = dhOut, info = protocol-specific constant)
//   → 64 bytes: newRootKey(32) || newChainKey(32)
```

### Example 2: Message-key expansion (WhatsApp-specific; 80-byte key)
```csharp
// Source: WhatsApp Security Whitepaper [CITED] — "Message Key … 80 bytes: 32 AES-256, 32 HMAC, 16 IV"
// Expand the 32-byte chain-derived message key into AES key + HMAC key + IV.
// The exact HKDF info/labels used by WA are [ASSUMED] to follow libsignal's
// "WhisperMessage" expansion — pin against fixtures in the spike (Pitfall 1).
```

### Example 3: Typed event surface sketch (MSG-07)
```csharp
// Pattern follows the existing ConnectionStateChangedEventArgs / PairingStateChangedEventArgs
// EventHandler convention [VERIFIED: WaConnection.cs:17-37, Pairing/PairingEvents.cs]
public abstract record WhatsAppEvent;
public sealed record MessageReceivedEvent(string MessageId, Jid Chat, Jid Sender, bool IsFromMe,
    DateTimeOffset Timestamp, string? Text, MessageKey Quoted) : WhatsAppEvent;
public sealed record ReceiptEvent(ReceiptKind Kind, IReadOnlyList<MessageKey> Messages,
    Jid Chat, Jid? Participant, DateTimeOffset Timestamp) : WhatsAppEvent; // delivery/read/played/…
public sealed record MessageAckedEvent(string MessageId, int? Error) : WhatsAppEvent;
public sealed record DecryptionFailedEvent(string MessageId, Jid Sender, int RetryCount) : WhatsAppEvent;
// Consumption: IAsyncEnumerable<WhatsAppEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken ct)
// backed by a bounded Channel — matches MUL-03 async-stream direction; StateChanged/LoggedOut
// continue as EventHandler events on WaConnection for CONN-05 compatibility.
```

### Example 4: Quoting a received message (MSG-05)
```csharp
// Outbound: ExtendedTextMessage with ContextInfo quoting the cached entry
var quoted = new ContextInfo {
    StanzaId = cached.MessageKey.Id,            // the id of the message being replied to
    Participant = cached.SenderJid,             // group sender (from cache extension)
    QuotedMessage = new Message { Conversation = cached.Text }, // minimal quoted render
    RemoteJid = cached.ChatJid,
};
var payload = new Message { ExtendedTextMessage = new ExtendedTextMessage { Text = reply, ContextInfo = quoted } };
// RecentMessageEntry needs an additive extension (text/sender) to populate QuotedMessage —
// the Phase-1 record only has (MessageKey, ChatJid, TimestampUtc) [VERIFIED: IRecentMessageCacheStore.cs:124].
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Signal X3DH | Signal replaced X3DH with **PQXDH** (extra PQ KEM) | Late 2023 [CITED: arxiv 2504.07323] | WhatsApp's ecosystem (whatsmeow/Baileys) still uses X3DH — Phase 4 implements X3DH; keep PQXDH out of scope |
| Signal header encryption variant | Not used by WhatsApp | — | Implement the standard Double Ratchet (unencrypted headers), not the HE variant |
| whatsmeow/Baileys never rotate signed prekeys | noted as PFS weakness upstream | observed 2025 [CITED: arxiv 2504.07323] | SDK SHOULD upload fresh signed prekeys periodically (schedule later phase; document risk) |

**Deprecated/outdated:**
- Pre-key bundle fetching via phone (`requestFromPhone` flow) — superseded by direct `encrypt`-ns IQ on multi-device; do not implement phone-relay paths.
- `Axolotl*` naming — keep the wire field name `axolotlSenderKeyDistributionMessage` (interop) but use Signal naming in C# APIs.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Serialized Signal wire bytes = version byte(s) + protobuf + full HMAC, PKCS7-style padding to 64-byte blocks before AES-256-CBC | Pitfall 1, Examples 2 | **High** — messages rejected by real peers; mitigated by spike fixture gate before network wiring |
| A2 | `conversation=1` is used for plain text without quotes; quotes require `extendedTextMessage` | Standard Stack, Example 4 | Medium — quotes wouldn't render; text would still deliver; fixture-verify in UAT |
| A3 | Own devices are excluded from fanout in Phase 4 (no `DeviceSentMessage` self-sync) | Pitfall 3 | Low — self-message echo is a nicety; success criteria don't require it; document as deferred |
| A4 | The `t` attr on normal (non-retry) outbound `message` nodes is stamped by the sender at send time (seconds) | Pattern 2 | Low-medium — server may reject missing `t`; retry path sets it verbatim; confirm in live UAT |
| A5 | Message-key HKDF expansion labels follow libsignal conventions | Example 2 | High for interop; covered by A1 spike |
| A6 | Group membership/participant listing can be restricted to the minimal IQ needed for SKDM fanout (full group read-side is v2 GRPM-01) | MSG-02 planning | Medium — if the minimal group-info IQ shape is wrong, group sends fail; pin from Baileys `groupMetadata` query during planning |
| A7 | `phash` uses AD-string form (`user.device@server`) of each device JID | Pattern 6 | Medium — wrong JID form → phash mismatch → devices miss messages; fixture-pin |
| A8 | Prekey id nodes carry a **3-byte** big-endian id and registration a **4-byte** big-endian id | Pattern 3 | Medium — off-by-one breaks X3DH; fixture-pin |

## Open Questions

1. **How is `SignalSessionRecord.Address` keyed — peer JID, or per-device AD JID?** — **RESOLVED:** per-device full AD JID (canonical `Jid.ToString()` including device index). Double Ratchet sessions are strictly per-device; keying by bare user collides companion devices. PN↔LID aliasing: key by whichever JID the ciphertext arrived under and store a mapping note; expand LID-migration handling when evidence arrives (Pitfall 5).
2. **Does the phase need a `Message.proto` beyond the Signal record protos?** — **RESOLVED:** yes — two proto files: `signal.proto` (state structures, wire Signal messages) and `message.proto` (Message/MessageKey/ContextInfo/ExtendedTextMessage/SKDM subset), both schema-sourced from Baileys MIT `WAProto.proto` with a NOTICE provenance row each.
3. **Own-device fanout / DeviceSentMessage?** — **RESOLVED:** out of Phase-4 scope (A3); own JIDs excluded from fanout; revisit in a later phase if self-sync events are wanted.
4. **Receipt `inactive` vs active delivery receipts?** — **RESOLVED:** Phase 4 sends delivery receipts unconditionally when the host opts in per chat (no presence coupling); presence (`EXT-02`) is Phase 8. Document that WA apps render `inactive` receipts as nothing (gray-tick timing nuance) in the risk/behavior notes.
5. **Where does device-list caching policy live?** — **RESOLVED:** SDK-owned `IOptions<DeviceDirectoryOptions>` (TTL), store untouched (cache is process-local; not durable — a restart re-queries USync). Consistent with D-08 "policy is SDK-owned".
6. **Idempotency contract for MSG-03?** — **RESOLVED:** `SendAsync` generates the message id client-side and returns it; one logical send = one network send; on timeout the SDK returns `SendOutcome.TimedOut(id)` and drops (host may re-call → new id, new send). Reconnect during an in-flight frame re-sends the same frame only per the Phase-3 `retryFrame` disconnect path (protocol-mandated, not a message-level retry). Documented in XML docs + README.
7. **Group participant fetch for SKDM fanout vs GRPM-01 (v2)?** — **RESOLVED (needs planner attention):** MSG-02 requires the minimal group-participants IQ (Baileys `groupMetadata` query, `w:g2` namespace) even though full group read-side is deferred to v2 — scope it as "participants-for-fanout only", pin the node shape from Baileys during implementation, and log the NOTICE row. This is the only v2-adjacent surface Phase 4 must touch.
8. **Signal version byte / padding exact layout?** — **RESOLVED via spike gate (not by research):** planned as Wave-0 spike with live-capture/Baileys-cross-checked golden fixtures; every [ASSUMED] byte-layout value is fixture-correctable. Planner must make the spike a blocking first task; downstream Signal tasks depend on its fixture set.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | build/test | ✓ | 10.0.401 | — |
| `dotnet test` (MTP via global.json) | validation | ✓ | — | — |
| Node.js (optional) | Baileys cross-check fixtures during spike | ✓ (node v26 present in env) | 26.10.0 | live-capture fixtures instead |
| Live linked WhatsApp account | real-device UAT (end-of-phase, `human_verify_mode: end-of-phase`) | ✗ in CI | — | loopback fakes for CI; UAT documented as NOT VERIFIED if no device (precedent: 03-VALIDATION.md) |

**Missing dependencies with no fallback:** none blocking (CI stays fixture-driven; live UAT is already accepted as a documented manual step).
**Missing dependencies with fallback:** live phone for UAT → loopback + fixtures (fallback used in Phases 2-3, keep).

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 4.0.1 + AwesomeAssertions 9.6.0 + NSubstitute 6.2.0 (locked) |
| Config file | repo-root `global.json` (MTP mode) — unchanged |
| Quick run command | `dotnet test Iwasug.WhatsApp.Sdk.sln --filter <class>` |
| Full suite command | `dotnet test Iwasug.WhatsApp.Sdk.sln` |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| MSG-01 | X3DH+Ratchet roundtrip; 1:1 send/receive E2EE; plaintext absent from wire bytes | unit + golden-fixture | `dotnet test --filter SignalCoreTests` + `--filter MessageWireFixtureTests` | ❌ Wave 0 |
| MSG-02 | SKDM distribution, skmsg encrypt/decrypt, phash determinism | unit | `dotnet test --filter SenderKeyTests` | ❌ Wave 0 |
| MSG-03 | id format (3EB0/40-char), send→ack correlation, timeout=one outcome, no auto-retry | unit (FakeTimeProvider) | `dotnet test --filter MessageCorrelationTests` | ❌ Wave 0 |
| MSG-04 | receipt node shapes, MarkRead per-message keys, inbound receipt parse | unit + wire fixture | `dotnet test --filter ReceiptTests` | ❌ Wave 0 |
| MSG-05 | quote build from recent cache; cache write policy D-20 | unit | `dotnet test --filter QuotingTests` + `RecentCachePolicyTests` | ❌ Wave 0 |
| MSG-06 | retry receipt emit (count cap, keys node), retry-resend from cache, no loss on decrypt fail | unit + loopback | `dotnet test --filter RetryReceiptTests` | ❌ Wave 0 |
| MSG-07 | typed events, CT cancels enumeration + in-flight ops | unit | `dotnet test --filter MessagingEventTests` | ❌ Wave 0 |

### Sampling Rate
- **Per task commit:** `dotnet test Iwasug.WhatsApp.Sdk.sln` (repo is fast; no narrower gate needed)
- **Per wave merge:** full suite + NOTICE provenance-row audit (mirror 03-07 pattern)
- **Phase gate:** full suite green before `/gsd-verify-work`; live UAT with a real account for SC 1-5 (document NOT VERIFIED if unavailable)

### Wave 0 Gaps
- [ ] `Signal/` KAT fixture set (`tests/.../TestData/signal-kats.json`) — from spike (Pitfall 1)
- [ ] `message-wire-fixtures.json` — message-node, receipt-node, retry-node golden bytes (source: Baileys encode cross-check or live capture)
- [ ] Loopback peer extensions for `message`/`receipt`/`ack` stanza scripting
- [ ] `RecentMessageEntry` additive extension decision (content + sender fields) — new contract-kit assertions in `StoreContract` tests

## Security Domain

> `security_enforcement: true`, `security_asvs_level: 1` `[VERIFIED: .planning/config.json:49-50]`

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V5 Input Validation | yes | Every inbound node/proto parsed defensively: proto size caps, unknown-field tolerance, node depth already capped (Phase 2); validate JIDs via `Jid.Parse` |
| V6 Cryptography | yes | NSec (X25519/Ed25519) + BCL (AES-CBC/HMAC/HKDF) — never hand-roll primitives; constant-time MAC compare via `CryptographicOperations.FixedTimeEquals` |
| V2/V3/V4 | no | Auth/session/access-control are host concerns; SDK crypto identity is covered under V6 |
| Secret hygiene | yes (repo rule) | Key material never in logs/exceptions/events (T-01-08 precedent); `CredentialRecord`-grade discipline extends to all new key-bearing records |

### Known Threat Patterns for this stack
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Replay of PreKeySignalMessage without OPK | Tampering | Dedup by `aliceBaseKey` + immediate DH ratchet on first reply (X3DH § security considerations [CITED]); skipped-key cache bounds prevent memory DoS |
| Identity-key swap / MITM on bundle fetch | Tampering | Pin peer identity on first use; on mismatch surface a typed event and require host decision (no silent `AutoTrustIdentity` default) |
| One-time-prekey draining by malicious peer | Repudiation/DoS | Upstream documented (arxiv 2504.07323): WhatsApp doesn't rate-limit bundle queries — SDK-side: fetch bundles only when a session is genuinely missing; log every fetch (safety gate correlation in Phase 5) |
| Plaintext leak via logs/events | Informational | `Message` plaintext and key bytes forbidden in log sites; code-review gate |
| Malformed ciphertext → exception → DoS | Elevation | Decrypt failures are typed outcomes (retry path), never thrown through the pump |

## Sources

### Primary (HIGH confidence)
- `raw.githubusercontent.com/WhiskeySockets/Baileys/master/WAProto/WAProto.proto` (MIT) — all proto schemas quoted verbatim (MessageKey, SignalMessage, PreKeySignalMessage, SenderKeyMessage, SKDM, ContextInfo, ExtendedTextMessage, Message, SessionStructure, SenderKeyRecordStructure) — read 2026-10-10
- `raw.githubusercontent.com/tulir/whatsmeow/main/send.go` (MPL-2.0, behavioral read-only) — prepareMessageNode/encryptMessageForDevices/sendDM/sendGroup/prepareMessageNode, enc attrs, phash algorithm, message-lock serialization — read 2026-10-10
- `raw.githubusercontent.com/tulir/whatsmeow/main/prekeys.go` (MPL-2.0, behavioral read-only) — prekey fetch/upload IQ shapes, nodeToPreKeyBundle, counts (50/812/5) — read 2026-10-10
- `raw.githubusercontent.com/tulir/whatsmeow/main/user.go` (MPL-2.0, behavioral read-only) — USync device-list IQ shape — read 2026-10-10
- `raw.githubusercontent.com/WhiskeySockets/Baileys/master/src/Utils/generics.ts` (MIT) — `generateMessageID` = `'3EB0' + randomBytes(18).toString('hex').toUpperCase()` — read 2026-10-10
- In-repo seams quoted this session: `Stores/*.cs`, `Domain/*.cs`, `Connection/QueryRouter.cs`, `Connection/WaConnection.cs`, `NOTICE.md`, `.planning/config.json` (paths cited inline)

### Secondary (MEDIUM confidence)
- `github.com/tulir/whatsmeow/blob/main/receipt.go` — MarkRead node shape, receipt types, ack/NACK codes [CITED via GitHub source excerpt surfaced in search]
- `github.com/tulir/whatsmeow/blob/main/retry.go` — retry receipt children, keys node, count cap 5, resend attrs [CITED via GitHub source excerpt]
- `github.com/tulir/whatsmeow/blob/main/message.go` — inbound decrypt dispatch (`enc` v=2, skmsg/msg/pkmsg), retry emit wiring [CITED via GitHub source excerpt]
- `signal.org/docs/specifications/x3dh/` + `/doubleratchet/` — X3DH formulas, KDF_RK/KDF_CK recommendations [CITED]
- WhatsApp Security Whitepaper (s3.documentcloud.org) — 80-byte message key, sender-key group model, chain ratchet 0x01/0x02 [CITED]

### Tertiary (LOW confidence)
- `arxiv.org/abs/2504.07323` (Prekey Pogo) — ecosystem observations (X3DH still current in WA, no bundle-query rate limiting, signed-prekey rotation gaps) [CITED, secondary analysis]
- `wacrg.org/encryption-keying/` — corroborating `pkmsg`/`msg` fanout description (cross-checked against whatsmeow source)

## Metadata

**Confidence breakdown:**
- Standard Stack: **HIGH** — locked stack reused; zero new packages; crypto primitives match the WhatsApp whitepaper
- Architecture: **HIGH** for flows/schemas (verbatim primary sources); **MEDIUM** for the Signal byte-level serialization (A1/A5 — gated by spike)
- Pitfalls: **HIGH** — each pitfall traces to either an upstream-observed failure mode or a repo precedent (02/03 phases)

**Research date:** 2026-10-10
**Valid until:** 2026-11-10 (protocol schemas stable; whatsmeow/Baileys drift is gradual — re-check if either major-version bumps)

**Provenance follow-up for the planner:** the upstream reads listed under Primary/Secondary sources must each get a `NOTICE.md` provenance row when the corresponding code lands (repo law, per NOTICE.md "Provenance recording").


