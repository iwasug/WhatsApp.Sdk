---
phase: 04-signal-e2ee-messaging
plan: 04-02
type: execute
wave: 2
---

## Wave 2 Complete

**04-02: Message wire fixtures + device directory + cache extension** — message.proto schema pinned by Baileys MIT WaProto.proto, MessageBuilder pins plain-text/quote payloads with fixture-backed wire bytes, DeviceDirectory resolves peer JIDs through USync IQ with own-device exclusion, RecentMessageEntry extended with text/sender/resend payload for MSG-05 quoting, message-wire-fixtures.json pins conversation/quote/node bytes, 7 MessageWireFixtureTests + 7 DeviceDirectoryTests + 28 cache contract facts all green.

### What was built

- `Proto/message.proto` — hand-written schema with Baileys provenance header (`No bytes were copied`, `Baileys`), package `wa.message`, option `csharp_namespace = "Iwasug.WhatsApp.Sdk.Messaging"`. Defines `MessageKey`, `Message`, `ExtendedTextMessage`, `ContextInfo`. Protobuf build item wired with `OutputOptions="internal_access"`.

- `Messaging/MessageBuilder.cs` — internal static factory with wire-size constants, `ArgumentNullException.ThrowIfNull` guards throwing `SdkFormatException` with rule-only messages. Constructs `Message { Conversation = text }` for plain text, and `Message { ExtendedTextMessage { Text, ContextInfo { … } } }` for quotes. Node-assembly seam places ciphertext in `enc Data` child, never in node `Text` or human text inside node content (Pitfall 8 discipline).

- `Messaging/DeviceDirectory.cs` — internal service issuing USync IQ per Pattern 3 (`<usync sid=… mode="query" last="true" index="0" context="message"><query><devices version="2"/></query><list><user jid=…/></list></usync>`), parsing response children bounds-checked into typed device lists. Process-local TTL cache (SDK-owned, no store seam), own-device exclusion enforced (Pitfall 3 / A3): account's own JID family filtered from every resolved set.

- `Stores/IRecentMessageCacheStore.cs` — additive extension of `RecentMessageEntry` with three new defaulted parameters: message text (quoted-message render source), sender JID string (group participant), and marshalled plaintext `Message` bytes for retry resend (MSG-06). XML docs document retention horizon and D-20 log-and-continue write policy. Existing three-parameter construction sites compile unchanged; new fields carry defaults.

- `TestData/message-wire-fixtures.json` — provenance doc field naming Baileys MIT schema source, no whatsmeow bytes. Pins: (a) serialized `Message{Conversation}` bytes for fixed ASCII and non-ASCII inputs, (b) serialized quote-carrying `ExtendedTextMessage` bytes, (c) full assembled node's encoded bytes round-tripping through `BinaryNodeCodec` with byte-identical `enc` content.

- `tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/MessageWireFixtureTests.cs` — 7 methods: `conversation`, `extended_text`, `node_roundtrip`, `cross_contamination`, plus 3 others asserting fixture pins and cross-contamination rules. All wire data carries hex of pinned values and fixed public inputs only.

- `tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/DeviceDirectoryTests.cs` — 7 methods: `resolves`, `own_device`, `cache`, `ttl`, `malformed`, `two_accounts`, and one other. Own-exclusion fact asserts resolved set equals expected non-own device list via set equality. Cache TTL fact: second resolve inside TTL issues zero queries; expired TTL re-queries. Malformed child yields typed failure with no partial list.

- NOTICE provenance row for the Baileys `WAProto.proto` schema read in the established format.

### Test Results

- `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~MessageWireFixtureTests"` — 7/7 passed
- `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~DeviceDirectoryTests"` — 7/7 passed
- `dotnet test tests/Iwasug.WhatsApp.Sdk.Tests --filter "FullyQualifiedName~RecentMessageCacheStoreContractTests"` — 28/28 passed
- Build: `0 Warning(s), 0 Error(s)` under `-warnaserror`

### Acceptance Criteria Verification

- `message.proto` contains `syntax = "proto3"`, `package wa.message`, `option csharp_namespace = "Iwasug.WhatsApp.Sdk.Messaging"`, the message types `MessageKey`, `Message`, `ExtendedTextMessage`, `ContextInfo`, and a provenance header containing `No bytes were copied` and `Baileys`
- `tests/Iwasug.WhatsApp.Sdk.Tests/TestData/message-wire-fixtures.json` exists with a provenance field containing `Baileys` and pins at least: plain-conversation bytes, quote-carrying bytes, and one full encoded `<message>` node
- `tests/Iwasug.WhatsApp.Sdk.Tests/Messaging/MessageWireFixtureTests.cs` contains test methods whose names include `conversation`, `extended_text`, `node_roundtrip`, and `cross_contamination`
- `src/Iwasug.WhatsApp.Sdk/Messaging/MessageBuilder.cs` contains no `ILogger` reference and throws `SdkFormatException` on null/empty text (fact asserted in the fixture test file)
- `grep -c "WAProto.proto" NOTICE.md` outputs at least 1

### Dependencies

- Wave-0 spike (04-01) provides the Signal crypto foundation: byte-layout fixtures, cipher, X3DH→ratchet roundtrip, durable sessions, prekey management
- These foundations enable the wire-layer correctness proved in this wave

### Output

Created `.planning/phases/04-signal-e2ee-messaging/04-02-SUMMARY.md` when done