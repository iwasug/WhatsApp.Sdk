# Phase 4: Signal E2EE & Messaging - Pattern Map

**Mapped:** 2026-10-11
**Files analyzed:** 26 (13 new source, 6 modified source/contract, 7 new test/fixture files)
**Analogs found:** 26 / 26 (all files map to an existing analog; 13 exact/role-match, remainder pointer-level matches noted inline)

All analog paths below were verified git-TRACKED (`git ls-files` non-empty); no gitignored mirror paths appear in this document.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/.../Signal/X3dh.cs` | service (crypto) | transform (in-memory) | `Noise/NoiseHandshake.cs` | role-match |
| `src/.../Signal/DoubleRatchet.cs` | service (state machine) | transform + persist (store seams) | `Noise/NoiseHandshake.cs` | role-match |
| `src/.../Signal/SignalCipher.cs` | service (crypto primitive) | transform | `Noise/NoiseHandshake.cs` (`NoiseSymmetricState` statics) | exact |
| `src/.../Signal/SenderKey.cs` | service (crypto) | transform | `Noise/NoiseHandshake.cs` (NSec typed-key conventions) | role-match |
| `src/.../Signal/PreKeyManager.cs` | service | request-response (`encrypt`-ns IQ) | `Connection/QueryRouter.cs` + `Pairing/PairingCoordinator.cs` | role-match |
| `src/.../Messaging/MessageBuilder.cs` | utility/builder | transform (proto → plaintext bytes) | `Pairing/ClientPayloadFactory.cs` | exact |
| `src/.../Messaging/MessageSender.cs` | service | request-response (correlated send) | `Connection/QueryRouter.cs` + `WaConnection.SendPairingNodeAsync` | role-match |
| `src/.../Messaging/MessageDispatcher.cs` | dispatcher | event-driven | `Pairing/PairingCoordinator.OnStanzaReceived` | exact |
| `src/.../Messaging/ReceiptHandler.cs` | service | event-driven + request-response | `Pairing/PairingCoordinator.cs` (node build + send delegate) | role-match |
| `src/.../Messaging/DeviceDirectory.cs` | service (cache) | request-response (USync IQ) | `Connection/QueryRouter.cs` | role-match |
| `src/.../Messaging/MessagingEvents.cs` | event definitions | — | `Pairing/PairingEvents.cs` + `ConnectionStateChangedEventArgs` | exact |
| `src/.../Proto/signal.proto` | config/schema | — | `Proto/handshake.proto` + csproj Protobuf item | exact |
| `src/.../Proto/message.proto` | config/schema | — | `Proto/handshake.proto` + csproj Protobuf item | exact |
| `src/.../Stores/IRecentMessageCacheStore.cs` (modified: `RecentMessageEntry` extension) | model | CRUD | itself (record at file tail) + `Testing/StoreContract/RecentMessageCacheStoreContractTests.cs` | exact |
| `src/.../Domain/SignalIdentity.cs` (modified: additive key material) | model | — | `Domain/CredentialRecord.cs` (key-field hygiene docs) | exact |
| `src/.../Domain/PreKeyRecord.cs` (modified: additive key material) | model | — | `Domain/CredentialRecord.cs` | exact |
| `src/.../Connection/WaConnection.cs` (modified: `SendNodeAsync` sibling) | transport seam | request-response | `WaConnection.SendPairingNodeAsync` (same file, lines 696-734) | exact |
| `src/Iwasug.WhatsApp.Sdk.csproj` (modified: 2 Protobuf items) | config | — | existing `<Protobuf Include=…>` items (lines 40-54) | exact |
| `NOTICE.md` (modified: provenance rows) | config/doc | — | existing provenance table (repo law) | exact |
| `PublicAPI.Unshipped.txt` (modified) | config | — | existing entries (PublicApiAnalyzers, csproj:30) | exact |
| `tests/.../Signal/SignalCoreTests.cs` | test | transform | `Noise/NoiseVectorTests.cs` | exact |
| `tests/.../Signal/SenderKeyTests.cs` | test | transform | `Noise/NoiseVectorTests.cs` | exact |
| `tests/.../Messaging/MessageWireFixtureTests.cs` | test (golden fixture) | transform | `Noise/NoiseVectorTests.cs` + `Codec/BinaryNodeRoundTripTests.cs` (pointer) | role-match |
| `tests/.../Messaging/MessageCorrelationTests.cs` | test | request-response | `Connection/QueryRouterTests.cs` (pointer) + `NoiseVectorTests` | role-match |
| `tests/.../Messaging/{Receipt,Quoting,RetryReceipt,MessagingEvent,RecentCachePolicy}Tests.cs` | test | event-driven | `Connection/LoopbackRoundTripTests.cs` (pointer) + `QueryRouterTests.cs` (pointer) | role-match |
| `tests/.../TestData/signal-kats.json`, `message-wire-fixtures.json` | fixture | — | `TestData/noise-xx-aesgcm-vectors.json` + load pattern `NoiseVectorTests.LoadVector` (lines 64-70) | exact |
| Loopback peer extension (`message`/`receipt`/`ack` stanza scripting) | test harness | event-driven | `Transport/LoopbackFrameTransport.cs` + `LoopbackRoundTripTests.cs:294` | exact |

Note: `SignalSessionRecord` / session-state serialization does **not** change the `ISignalSessionStore` interface — `SessionState` bytes become real (`SessionStructure` proto) behind the existing opaque-bytes contract (`ISignalSessionStore.cs:88-92`).

## Pattern Assignments

### `src/.../Signal/SignalCipher.cs` (service, transform) — the spike-gated primitive

**Analog:** `src/.../Noise/NoiseHandshake.cs` — the `NoiseSymmetricState` internal statics (same repo file hosts both the handshake state machine and its raw primitive wrappers; SignalCipher is Phase 4's equivalent wrapper for AES-256-CBC + HMAC-SHA256 + HKDF).

**Imports pattern** (NoiseHandshake.cs:1-5):
```csharp
using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;
using Iwasug.WhatsApp.Sdk.Errors;
using NSec.Cryptography;
```

**Crypto-primitive wrapper pattern** (NoiseHandshake.cs:267-283, 302-319) — static, span-based, zeroizes temporaries, `CryptographicException` on auth failure:
```csharp
internal static (byte[] First, byte[] Second) HkdfTwoBlocks(
    ReadOnlySpan<byte> chainingKey,
    ReadOnlySpan<byte> inputKeyMaterial)
{
    byte[] okm = HKDF.DeriveKey(
        HashAlgorithmName.SHA256,
        inputKeyMaterial.ToArray(),
        2 * HashLength,
        chainingKey.ToArray(),
        Array.Empty<byte>());
    byte[] first = new byte[HashLength];
    byte[] second = new byte[HashLength];
    okm.AsSpan(0, HashLength).CopyTo(first);
    okm.AsSpan(HashLength, HashLength).CopyTo(second);
    CryptographicOperations.ZeroMemory(okm);
    return (first, second);
}

internal static byte[] AeadEncrypt(
    ReadOnlySpan<byte> key, ulong nonce,
    ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> plaintext)
{
    byte[] nonceBytes = EncodeNonce(nonce);
    byte[] output = new byte[plaintext.Length + TagLength];
    using var cipher = new AesGcm(key, TagLength);
    cipher.Encrypt(nonceBytes, plaintext,
        output.AsSpan(0, plaintext.Length),
        output.AsSpan(plaintext.Length, TagLength),
        associatedData);
    CryptographicOperations.ZeroMemory(nonceBytes);
    return output;
}
```
SignalCipher replaces `AesGcm` with `Aes` CBC + `HMACSHA256` (WhatsApp 80-byte message key per RESEARCH Example 2), keeps the same zeroize/temporary-wipe discipline and short length-check throws (`NoiseHandshake.cs:331-334`). The Double Ratchet `KDF_CK` (RESEARCH Example 1) uses `HMACSHA256.HashData` the same way `HkdfTwoBlocks` uses `HKDF.DeriveKey`.

**Version/padding/HMAC byte layout is [ASSUMED] (RESEARCH A1/A5): the Wave-0 spike fixtures win over any assumption.** Do not wire networking until `SignalCoreTests` KATs are green (Pitfall 1).

### `src/.../Signal/X3dh.cs` + `src/.../Signal/DoubleRatchet.cs` (service, transform + persist)

**Analog:** `src/.../Noise/NoiseHandshake.cs` — NSec typed-key X25519 usage (the exact API shape Phase 4's DH must use; Ed25519 for sender-key signatures uses the same `Key`/`KeyPair` conventions).

**X25519 agreement pattern** (NoiseHandshake.cs:365-402) — import raw keys fail-closed, typed exception that never names key material:
```csharp
internal static byte[] DhAgree(Key ownPrivate, ReadOnlySpan<byte> peerPublicKey)
{
    ArgumentNullException.ThrowIfNull(ownPrivate);
    PublicKey peer;
    try
    {
        peer = PublicKey.Import(
            KeyAgreementAlgorithm.X25519,
            peerPublicKey,
            KeyBlobFormat.RawPublicKey);
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        throw new SdkConnectionException("Handshake key agreement failed: unusable peer key.", ex);
    }

    var parameters = new SharedSecretCreationParameters
    {
        ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
    };
    try
    {
        using SharedSecret agreement = KeyAgreementAlgorithm.X25519.Agree(ownPrivate, peer, parameters)
            ?? throw new SdkConnectionException("Handshake key agreement failed.");
        return agreement.Export(SharedSecretBlobFormat.RawSharedSecret);
    }
    // …OperationCanceledException/SdkException rethrow discipline
```
For Signal, wrap failures in a new typed decrypt outcome (`DecryptionFailed`), NOT `SdkConnectionException` — RESEARCH Security Domain: "Decrypt failures are typed outcomes (retry path), never thrown through the pump."

**Key create/import pattern** (NoiseHandshake.cs:527, 568) — persisted privates import as raw blobs, ephemerals create fresh:
```csharp
_ephemeral = Key.Create(KeyAgreementAlgorithm.X25519, creationParameters);
_static = Key.Import(KeyAgreementAlgorithm.X25519, staticPrivateKey, KeyBlobFormat.RawPrivateKey, creationParameters);
```

**Session-state serialization:** persist via the existing seam — `ISignalSessionStore.PutAsync(address, new SignalSessionRecord(address, sessionStructure.ToByteArray()), ct)` (`ISignalSessionStore.cs:48`, record at :100). Key by **full AD JID including device index** (`Jid.ToString()`, RESEARCH Open Question 1 / Pitfall 5). Await every write through `DurableWriteRetryPolicy` before acking (D-06, Pitfall 4).

### `src/.../Signal/SenderKey.cs` (service, transform)

**Analog:** `Noise/NoiseHandshake.cs` (NSec conventions above). Ed25519 signing/verification uses `SignatureAlgorithm.Ed25519` with the same typed-key discipline; SKDM/skmsg plaintexts are `SenderKeyDistributionMessage`/`SenderKeyMessage` protos from `signal.proto` (RESEARCH Pattern 1). Group `phash` (RESEARCH Pattern 6) is computed here and tested deterministically in `SenderKeyTests`.

### `src/.../Signal/PreKeyManager.cs` (service, request-response)

**Analog:** `Connection/QueryRouter.cs` (IQ correlation) — prekey fetch/upload IQs ride `SendQueryAsync` exactly as pairing IQs do.

**Correlated IQ send pattern** (QueryRouter.cs:98-139): allocate id → register `TaskCompletionSource` → sender delegate → `Task.WhenAny(reply, timeout)` → exactly-one-terminal-outcome (reply / timeout `SdkConnectionException` / connection-lost / cancelled). Never-retried, never-reused ids. Reuse for `encrypt`-ns prekey IQs and USync device IQs (RESEARCH Pattern 3 shapes: `<iq xmlns="encrypt" type="get" …><key><user jid reason="identity"/>`); parse response children with the codec (`BinaryNodeCodec.TryDecode`, cf. `TryParseFrame` at QueryRouter.cs:224-242 — bounds-checked untrusted decode).

**Node building from attrs/children** (QueryRouter.cs:207-211):
```csharp
BinaryNode node = BinaryNode.FromBytes(
    QueryTag,
    body.Span,
    new Dictionary<string, string>(StringComparer.Ordinal) { [IdAttribute] = id });
return BinaryNodeCodec.Encode(node);
```

### `src/.../Messaging/MessageBuilder.cs` (utility/builder, transform)

**Analog:** `src/.../Pairing/ClientPayloadFactory.cs` — the repo's existing "build protobuf bytes from typed inputs, fail-closed validation, never logs" factory. Copy it structurally.

**Factory shape** (ClientPayloadFactory.cs:31-58, 94-112): `internal static class`, wire-size constants at top, `ArgumentNullException.ThrowIfNull` + exact-length checks throwing `SdkFormatException` with rule-only messages:
```csharp
internal static class ClientPayloadFactory
{
    internal const uint ConnectTypeWifiUnknown = 1;   // role constants, Baileys-role provenance in XML docs
    internal const int PublicKeySize = 32;
    internal const int SignatureSize = 64;
    internal const int MaxRegistrationId = 16383;     // 14-bit registration id

    internal static byte[] Registration(
        byte[] identityPublic, int signedPreKeyId, /* … */)
    {
        ArgumentNullException.ThrowIfNull(identityPublic);
        if (identityPublic.Length != PublicKeySize)
        {
            throw new SdkFormatException("Registration identity keys must be 32 bytes.");
        }
        // …
```
MessageBuilder builds `Message{Conversation}` for plain text and `Message{ExtendedTextMessage{Text, ContextInfo}}` **when a quote is present** (RESEARCH A2 / Example 4) — `ContextInfo.QuotedMessage/StanzaId/Participant/RemoteJid` populated from the extended `RecentMessageEntry`. XML docs carry Baileys-MIT provenance for field roles (same doc discipline as ClientPayloadFactory.cs:15-29); `NOTICE.md` row lands when code lands.

### `src/.../Messaging/MessageSender.cs` (service, request-response)

**Analog:** `Connection/QueryRouter.cs` (send → correlated outcome) + `WaConnection.SendPairingNodeAsync` (uncorrelated stanza send).

**Stanza send pattern** (WaConnection.cs:696-734) — encode node → noise-encrypt → 1-byte flag prefix → transport; typed exception ladder that never swallows cancellation:
```csharp
internal async ValueTask SendPairingNodeAsync(BinaryNode node, CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(node);
    cancellationToken.ThrowIfCancellationRequested();
    ThrowIfDisposed();
    Channel? channel = _channel
        ?? throw new SdkConnectionException("The connection has no open channel: the pairing frame was not sent.");

    byte[] encrypted;
    try { encrypted = channel.Noise.Encrypt(BinaryNodeCodec.Encode(node)); }
    catch (ObjectDisposedException ex)
    { throw new SdkConnectionException("The connection has no open channel: the pairing frame was not sent.", ex); }

    try
    {
        byte[] flagged = new byte[encrypted.Length + 1];
        encrypted.CopyTo(flagged.AsSpan(1));
        await channel.Transport.SendAsync(WaFrame.Frame(flagged), cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) { throw; }
    catch (SdkConnectionException) { throw; }
    catch (ObjectDisposedException ex)
    { throw new SdkConnectionException("The connection has no open channel: the pairing frame was not sent.", ex); }
    catch (Exception ex)
    { throw new SdkTransportException("Pairing frame send failed.", ex); }
```
Phase 4 adds a sibling `SendNodeAsync(BinaryNode, ct)` in WaConnection (same shape; generic name — the coordinator's method stays untouched) and reuses `SendPairingNodeAsync`'s wiring precedent (WaConnection.cs:661-683: coordinator receives the send delegate in its constructor).

**Send correlation (MSG-03):** one logical send = one network send; `SendAsync` returns exactly one outcome (`SendOutcome` with generated `3EB0…` id / server error / `TimedOut`) and **never auto-retries** (Pitfall 7). Message id generation: `"3EB0" + Convert.ToHexString(RandomNumberGenerator.GetBytes(18))` (40 chars, Baileys MIT `generateMessageID`). Server reply attrs `t`/`server_id`/`error`/`phash` are parsed and mapped to the outcome. Per-account serialization via a plain `SemaphoreSlim` (whatsmeow `messageSendLock` equivalent) — NOT the RateLimiter (Phase 5).

### `src/.../Messaging/MessageDispatcher.cs` (dispatcher, event-driven)

**Analog:** `src/.../Pairing/PairingCoordinator.cs` — the `StanzaReceived` subscription pattern (the seam research mandates copying).

**Subscription wiring** (WaConnection.cs:669-682 — exactly how the coordinator plugs in):
```csharp
var coordinator = new PairingCoordinator(
    _accountId,
    provider,
    provider.Credentials,
    SendPairingNodeAsync,          // send delegate owned by connection
    () => _channel is not null,    // channel-presence check
    pairSuccessVerifier: null,
    timeProvider: _timeProvider,
    resolver: () => VersionResolver,
    logger: null);
StanzaReceived += coordinator.OnStanzaReceived;
```

**Handler shape** (PairingCoordinator.cs:725-800): method-group handler `(object? sender, BinaryNode node)` that (a) null-checks, (b) exits early when no live state, (c) dispatches by `node.Tag` (`string.Equals(node.Tag, "iq", StringComparison.Ordinal)` at :789) and by attrs (`node.Attrs.TryGetValue("id", out string? id)` at :796), (d) never mutates the node. `RaiseStanzaReceived` (WaConnection.cs:1308-1327) already logs-and-swallows handler exceptions — the dispatcher never needs its own try/catch around the whole handler.

Phase 4's dispatcher adds the **tag map** (`message`/`receipt`/`ack`/`notification`) that whatsmeow keeps in `nodeHandlers` — new seam, but the subscription + per-tag dispatch mechanics are proven here. Ordering contract (Pitfall 4): decrypt → persist (DurableWriteRetryPolicy) → **ack** → dispatch event → recent-cache write (D-20 log-and-continue). Ack-before-persist is a review-gate failure.

### `src/.../Messaging/ReceiptHandler.cs` (service, event-driven + request-response)

**Analog:** `Pairing/PairingCoordinator.cs` (construction + send-delegate consumption; see wiring above) — receipts are uncorrelated stanzas like pairing frames: build `BinaryNode` with attrs (`id`, `type`, `to`, `t`, optional `participant`) + `<list><item id=…/></list>` children (RESEARCH Pattern 4) and send via the injected delegate. Retry receipts (Pattern 5) follow the same node-build discipline; **cap at `count >= 5`** and degrade (never throw) when the recent cache cannot satisfy a resend (Pitfall 6).

### `src/.../Messaging/DeviceDirectory.cs` (service cache, request-response)

**Analog:** `Connection/QueryRouter.cs` for the USync IQ round-trip (RESEARCH Pattern 3 `<iq xmlns="usync" …><usync sid mode="query" last="true" index="0" context="message"><query><devices version="2"/></query><list><user jid=…/></list>`). Process-local `IOptions<DeviceDirectoryOptions>` TTL cache (Open Question 5, D-08: SDK-owned policy, store untouched). Explicit own-JID exclusion (Pitfall 3, A3) with a test asserting fanout covers exactly the non-own device set.

### `src/.../Messaging/MessagingEvents.cs` (event definitions)

**Analog:** `src/.../Pairing/PairingEvents.cs` + `ConnectionStateChangedEventArgs` (WaConnection.cs:17-38) — the repo's `EventArgs` convention: sealed class, ctor assigns get-only properties, XML doc per member, scope-guard remark per enum.

**Event-args shape** (WaConnection.cs:17-38):
```csharp
public sealed class ConnectionStateChangedEventArgs : EventArgs
{
    public ConnectionStateChangedEventArgs(ConnectionState state, int? statusCode, int attempt)
    {
        State = state;
        StatusCode = statusCode;
        Attempt = attempt;
    }

    public ConnectionState State { get; }
    public int? StatusCode { get; }
    public int { get; } // …get-only properties, XML-documented
}
```
Handler contract repeated on every event (WaConnection.cs:184-189): "return quickly … must not call DisposeAsync … handler exceptions are logged and swallowed." MSG-07's `WhatsAppEvent` record hierarchy (RESEARCH Example 3) is consumed through a bounded `Channel` + `IAsyncEnumerable<WhatsAppEvent> ReadEventsAsync(CancellationToken)` — but keep `ConnectionStateChanged`/`LoggedOut` as `EventHandler` events on the connection for CONN-05 compatibility.

### `src/.../Proto/signal.proto` + `src/.../Proto/message.proto` (config/schema)

**Analog:** `src/.../Proto/handshake.proto` + the csproj `<Protobuf>` items. Copy the file-header discipline verbatim in shape:

**Provenance header pattern** (handshake.proto:1-27):
```proto
// Copyright (c) Iwa Sugriwa. Licensed under the MIT License.
//
// PROVENANCE (NOTICE.md "whatsmeow rule" + provenance-recording policy):
//   - This schema is HAND-WRITTEN for this repository. No bytes were copied
//     from whatsmeow (MPL-2.0) or from any other upstream .proto file.
//   - Field roles … were learned read-only from the MIT-licensed Baileys sources …
syntax = "proto3";

package wa.handshake;

option csharp_namespace = "Iwasug.WhatsApp.Sdk.Noise";
```
For Phase 4 the schemas are schema-sourced from Baileys MIT `WAProto.proto` (RESEARCH Open Question 2) — the header must say so explicitly with the exact upstream read recorded, plus a `NOTICE.md` row each. `csharp_namespace` becomes `Iwasug.WhatsApp.Sdk.Signal` / `.Messaging`; keep `internal_access`.

**csproj wiring** (Iwasug.WhatsApp.Sdk.csproj:40-54) — one item each, beside the existing ones:
```xml
<Protobuf Include="Proto\handshake.proto" GrpcServices="None" OutputOptions="internal_access" />
<!-- same for client_payload / companion_reg / adv -->
```

### `src/.../Stores/IRecentMessageCacheStore.cs` (modified: `RecentMessageEntry` extension)

**Analog:** the record itself (IRecentMessageCacheStore.cs:116-124) and its contract kit `src/.../Testing/StoreContract/RecentMessageCacheStoreContractTests.cs`.

**Existing record** (lines 121-124):
```csharp
/// <param name="MessageKey">Unique key the entry is stored and removed by.</param>
/// <param name="ChatJid">Chat address (JID string) the message belongs to.</param>
/// <param name="TimestampUtc">When the message arrived (UTC) — the basis for purge-primitive cutoffs.</param>
public sealed record RecentMessageEntry(string MessageKey, string ChatJid, DateTimeOffset TimestampUtc);
```
Additive extension adds quoted-message payload + sender (MSG-05) + retry-resend plaintext bytes (MSG-06, Pitfall 6) as new record parameters with defaults so existing host code keeps compiling; XML docs must carry the D-05/D-08 retention warning (lossy, SDK-owned trim horizon; "cannot resend after horizon" is a documented outcome). Update the shared contract kit (`RecentMessageCacheStoreContractTests.cs`) + the three contract-test subclasses in `tests/.../StoreContract/` (InMemory/Scratch patterns at `InMemoryRecentMessageCacheStoreContractTests.cs` / `ScratchRecentMessageCacheStoreContractTests.cs`). Key material in cache entries is plaintext-by-necessity (retry source) — document the hygiene trade per Pitfall 6.

### `src/.../Domain/SignalIdentity.cs` + `src/.../Domain/PreKeyRecord.cs` (modified: additive key material)

**Analog:** `src/.../Domain/CredentialRecord.cs` — the repo's key-bearing record doc discipline (byte[] cloning on boundaries, never-logged fields, SDK-owned encoding):
```csharp
/// <param name="IdentityKeyPrivate">Registration identity private key (32 bytes, the <c>eIdent</c> source). Never logged.</param>
/// <param name="SigningKeyPrivate">Ed25519 device-signing private key (32 bytes, the pair-success device signature source). Never logged.</param>
// Secret hygiene (T-3-01): … The byte arrays are owned by whoever holds the record: stores
// clone on the read/write boundary so a caller mutating its own array after a write
// cannot silently rewrite the persisted row.
```
Extend `SignalIdentityRecord(string? IdentityKeyPublic)` and `PreKeyBundle`/`PreKeyRecordEntry` additively (the XML docs at SignalIdentity.cs:8-11 and PreKeyRecord.cs:8-12 explicitly anticipate "NSec typed keys replace this field shape additively" in Phase 4). Persisted encodings stay SDK-owned; hosts persist opaquely.

### `src/.../Connection/WaConnection.cs` (modified: `SendNodeAsync` sibling)

**Analog:** `SendPairingNodeAsync` in the same file (WaConnection.cs:696-734, excerpted under MessageSender above). New internal sibling with the identical exception ladder, generic name, injected into `MessageSender`/`ReceiptHandler` with the same delegate-closure precedent as the coordinator wiring (WaConnection.cs:669-683). Do not modify `SendPairingNodeAsync` itself.

## Shared Patterns

### 1. Stanza send path (all outbound stanzas: messages, receipts, acks, retry receipts, IQs)
**Source:** `Connection/WaConnection.cs:696-734` (excerpted above)
**Apply to:** `MessageSender.cs`, `ReceiptHandler.cs`, `PreKeyManager.cs`, `DeviceDirectory.cs`
Encode → `channel.Noise.Encrypt` → flag-prefix frame → `Transport.SendAsync`; typed exception ladder; `OperationCanceledException` always rethrown unwrapped.

### 2. Correlated request/response (prekey IQs, USync IQs, message-send acks)
**Source:** `Connection/QueryRouter.cs:98-139` (`SendQueryAsync`), `:149-158` (`TryDeliver`)
**Apply to:** `PreKeyManager.cs`, `DeviceDirectory.cs`, `MessageSender.cs` (ack correlation)
Exactly-one-terminal-outcome; `q-{n}` never-reused ids; `TaskCompletionSource` + `Task.WhenAny` with `TimeProvider` timeout (FakeTimeProvider-testable); late replies surface as `false` and are logged by the pump.

### 3. Inbound stanza subscription + tag dispatch
**Source:** `Connection/WaConnection.cs:679` (subscription), `Pairing/PairingCoordinator.cs:725-800` (handler)
**Apply to:** `MessageDispatcher.cs`, `ReceiptHandler.cs`
`StanzaReceived += handler.OnStanzaReceived;` — one handler method, early-exit guards, `node.Tag` ordinal compare, `node.Attrs` TryGetValue, never mutate the node; connection swallows/logs handler exceptions (WaConnection.cs:1308-1327).

### 4. Crypto primitives & secret hygiene (entire `Signal/` folder)
**Source:** `Noise/NoiseHandshake.cs:267-402`; `Pairing/PairingCoordinator.cs:708-715` (zeroize)
**Apply to:** `X3dh.cs`, `DoubleRatchet.cs`, `SignalCipher.cs`, `SenderKey.cs`
NSec typed keys only (never hand-roll curve math); BCL `HKDF`/`HMACSHA256`/`Aes`; `CryptographicOperations.ZeroMemory` on every temporary (okm, nonce, ratchet privates); constant-time MAC compare via `CryptographicOperations.FixedTimeEquals`; exceptions name rules, never key bytes (T-01-08).

### 5. Durable writes before protocol acknowledgment (D-06, Pitfall 4)
**Source:** `Stores/Runtime/DurableWriteRetryPolicy.cs:84-112`
**Apply to:** session persists in `DoubleRatchet`, identity/prekey writes in `PreKeyManager`, SKDM state in `SenderKey`
```csharp
await retryPolicy.ExecuteAsync(ct => sessionStore.PutAsync(address, record, ct), cancellationToken);
// …only then: send ack node, dispatch event
```
Bounded retry (3 attempts, jittered backoff); exhaustion throws `SdkStoreException` — the failure is loud, never masked. Recent-cache writes deliberately do NOT route through it (D-20 log-and-continue, policy lines 40-43).

### 6. Typed events + handler contract (MSG-07)
**Source:** `Pairing/PairingEvents.cs:67-90`; `Connection/WaConnection.cs:17-38, 184-202`
**Apply to:** `MessagingEvents.cs` and every public event
Sealed `EventArgs` with ctor-assigned get-only properties and per-member XML docs; handler contract text ("return quickly, never call DisposeAsync, exceptions logged and swallowed") on every event; key material never in event args.

### 7. Golden-fixture / KAT tests (Wave-0 spike + all wire shapes)
**Source:** `tests/.../Noise/NoiseVectorTests.cs` (whole file is the pattern); fixture loader at :64-70
**Apply to:** `SignalCoreTests.cs`, `SenderKeyTests.cs`, `MessageWireFixtureTests.cs`, `ReceiptTests.cs`, `RetryReceiptTests.cs`
```csharp
private static HandshakeVector LoadVector()
{
    string path = Path.Combine(AppContext.BaseDirectory, "TestData", "noise-xx-aesgcm-vectors.json");
    string json = File.ReadAllText(path);
    return JsonSerializer.Deserialize<HandshakeVector>(json)
        ?? throw new InvalidOperationException("Noise vector fixture failed to deserialize.");
}
```
Compare hex strings of public wire data (`Convert.ToHexString`); key material only ever compared as SHA-256 fingerprints (`Fingerprint` helper, :72-76) so a failing run cannot print secrets; fixtures carry verbatim provenance in test XML docs (:18-34); tamper tests assert typed auth failures (:200-203). Fixture JSON files must be added as content that copies to output (mirror how `noise-xx-aesgcm-vectors.json` is wired in the test csproj).

### 8. Proto schemas + provenance (NOTICE.md rows)
**Source:** `Proto/handshake.proto:1-27` header; `Iwasug.WhatsApp.Sdk.csproj:40-54` items
**Apply to:** `signal.proto`, `message.proto`, csproj, `NOTICE.md`
Hand-written schema, Baileys-MIT field-role provenance in the file header, `GrpcServices="None" OutputOptions="internal_access"`, one NOTICE row per upstream read when code lands (repo law). Internal generated types add no PublicAPI entries; genuinely public messaging surface goes in `PublicAPI.Unshipped.txt`.

### 9. Store-key format (all Signal seams)
**Source:** `Domain/Jid.cs` (canonical `Jid.ToString()`), `ISignalSessionStore.cs:30-37` (`address` param docs)
**Apply to:** sessions, identities, prekey bundles
Key by full per-device AD JID including device index (`user.device@server`) — never the bare user (Pitfall 5). Contract-test the key format so PN↔LID transitions cannot silently collide.

## No Analog Found

| File | Role | Data Flow | Reason / Fallback |
|------|------|-----------|-------------------|
| `Messaging/MessagingEvents.cs` — `IAsyncEnumerable<WhatsAppEvent> ReadEventsAsync` (bounded-Channel async stream over typed events) | provider | streaming/event-driven | No async-stream event surface exists yet (Phase 1-3 events are `EventHandler`). RESEARCH Example 3 + MUL-03 direction is the spec; the `EventHandler` EventArgs shape (Pattern 6) still governs the event records themselves. |
| Stanza-handler **registration seam** (generic replacement for the coordinator's direct `StanzaReceived` subscription — multiple consumers: pairing + messaging) | seam | event-driven | Phase 3 wired exactly one subscriber. `MessageDispatcher` may either subscribe alongside it (events are multicast — mechanically safe) or introduce a small tag-map registrar; planner decision. Precedent for both sides is Pattern 3. |
| Outbound **send queue** per account (`System.Threading.Channels`, MSG-07 backpressure) | queue | event-driven/streaming | No channel-based queue exists in src yet. In-box `Channel<T>` bounded pattern per RESEARCH Standard Stack; the `SuccessQueue`+signal drain inside `PairingCoordinator` (:741-753) is the closest in-repo queue precedent but is lock-based, not Channel-based. |

## Metadata

**Analog search scope:** `src/Iwasug.WhatsApp.Sdk/**` (Connection, Pairing, Noise, Codec, Stores, Domain, Proto, Errors), `src/Iwasug.WhatsApp.Sdk.Testing/**`, `tests/Iwasug.WhatsApp.Sdk.Tests/**`
**Files scanned:** 60 source files, 48 test files (inventory via directory listing; 15 analogs read in full or targeted non-overlapping sections)
**Tracked-source gate:** all 15 read analogs verified via `git ls-files` (non-empty); no `.gsd/capabilities/` mirror paths used.
**Pattern extraction date:** 2026-10-11
