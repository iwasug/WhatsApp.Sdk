using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Signal;
using Iwasug.WhatsApp.Sdk.Stores;

namespace Iwasug.WhatsApp.Sdk.Messaging;

/// <summary>
/// Receipt node construction and emission (04-04, MSG-04/MSG-06): Pattern-4 verbatim node
/// shapes over the connection's <c>SendNodeAsync</c> seam (pairing-frame precedent).
/// Outbound: the per-chat opt-in delivery receipt, <c>MarkRead</c> read receipts
/// with per-message keys (one receipt per sender — the same-sender-only rule), and the
/// Pattern-5 decryption-failure retry receipt with its per-(sender, id) count cap.
/// </summary>
/// <remarks>
/// <para>
/// Wire shape (whatsmeow <c>receipt.go</c> behavioral reference — 04-RESEARCH
/// Pattern 4): <c>&lt;receipt id="{firstId}" type="read|delivery" to="{chatJid}"
/// t="{unix}" [participant="{senderJid}"]&gt;</c> with
/// <c>&lt;list&gt;&lt;item id="…"/&gt;&lt;/list&gt;</c> children only when more than one
/// id — the first id rides the attribute, the rest ride the list. <c>t</c> is always
/// present; <c>participant</c> appears only for group senders.
/// </para>
/// <para>
/// Receipts are stateless protocol nodes with no client-side dedup machine: calling
/// twice sends two identical receipts, and that is the documented contract (MSG-04
/// idempotency edge). No log call on this class ever sees message content.
/// </para>
/// </remarks>
internal sealed class ReceiptHandler
{
    /// <summary>
    /// Pattern-5 retry cap (whatsmeow <c>retry.go</c> behavioral reference, 04-RESEARCH
    /// Pattern 5): retry receipts for one (sender, message id) pair stop once the count
    /// exceeds this value; every later failure raises the typed
    /// <see cref="Messenger.DecryptionFailed"/> event only. The cap bounds the
    /// retry-receipt loop (T-4-13) and the one-time-prekey spend it drives.
    /// </summary>
    internal const int RetryCountCap = 5;

    /// <summary>
    /// Bound on the per-(sender, id) counter map (T-4-13 hygiene): a hostile peer
    /// could flood distinct failing ids and grow the map without bound, so the map
    /// evicts its OLDEST half by last-touch sequence once it exceeds this size
    /// (WR-03). Wholesale clearing was a ban-safety lever — a flood could reset the
    /// counter of a legitimate (sender, id) that had already emitted its capped
    /// retries, drawing another <see cref="RetryCountCap"/> receipts (each from
    /// retry #2 carrying a <c>&lt;keys&gt;</c> block that spends a real one-time
    /// prekey). Eviction only ever drops the least-recently-touched half, so hot
    /// counters survive a flood.
    /// </summary>
    internal const int MaxRetryCountKeys = 4096;

    /// <summary>
    /// Bound on the upward store probe for an actually stored one-time prekey
    /// (WR-01): the cursor advances monotonically, so empty slots below it (spent
    /// or never uploaded) are skipped rather than advertised, and this caps one
    /// receipt's store reads.
    /// </summary>
    internal const int MaxOneTimeProbe = 64;

    private readonly Messenger _owner;
    private readonly ConcurrentDictionary<string, bool> _deliveryReceiptChats = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RetryCounter> _retryCounts = new(StringComparer.Ordinal);
    private long _retryTouchSequence;
    private int _oneTimeCursor;

    internal ReceiptHandler(Messenger owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    /// <summary>
    /// Per-chat delivery-receipt opt-in (Open Question 4 resolved: no presence coupling,
    /// off until the host opts in). Process-local by design — this is host policy, not
    /// durable state (the D-02 store set is unchanged).
    /// </summary>
    internal void SetDeliveryReceipts(Jid chat, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (enabled)
        {
            _deliveryReceiptChats[chat.ToString()] = true;
        }
        else
        {
            _deliveryReceiptChats.TryRemove(chat.ToString(), out _);
        }
    }

    /// <summary>Whether delivery receipts are enabled for <paramref name="chat"/> (default off).</summary>
    internal bool IsDeliveryReceiptsEnabled(Jid chat) =>
        chat is not null && _deliveryReceiptChats.ContainsKey(chat.ToString());

    /// <summary>Sends the opt-in delivery receipt for one decrypted inbound message.</summary>
    internal async Task SendDeliveryReceiptAsync(Jid chat, string messageId, DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        BinaryNode node = BuildDeliveryReceiptNode(chat, messageId, timestamp);
        await _owner.Connection.SendNodeAsync(node, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends Pattern-4 read receipts for <paramref name="keys"/>, grouped per
    /// (chat, participant) sender pair — one wire receipt per sender, in first-seen order.
    /// </summary>
    internal async Task SendReadReceiptsAsync(IReadOnlyList<MessageKeyRecord> keys, CancellationToken cancellationToken)
    {
        var groups = new Dictionary<(string Chat, string Participant), List<string>>();
        foreach (MessageKeyRecord key in keys)
        {
            var groupKey = (key.RemoteJid.ToString(), key.Participant ?? string.Empty);
            if (!groups.TryGetValue(groupKey, out List<string>? ids))
            {
                ids = new List<string>();
                groups[groupKey] = ids;
            }

            ids.Add(key.Id);
        }

        foreach (((string chat, string participant), List<string> ids) in groups)
        {
            BinaryNode node = BuildReadReceiptNode(
                Jid.Parse(chat),
                ids,
                participant.Length == 0 ? null : participant,
                _owner.Clock.GetUtcNow());
            await _owner.Connection.SendNodeAsync(node, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Pattern-4 read receipt (fixture seam: internal static, pinned by message-wire-fixtures.json).</summary>
    internal static BinaryNode BuildReadReceiptNode(Jid chat, IReadOnlyList<string> messageIds, string? participant, DateTimeOffset timestamp) =>
        BuildReceiptNode("read", chat, messageIds, participant, timestamp);

    /// <summary>Pattern-4 delivery receipt for a single message id (fixture seam).</summary>
    internal static BinaryNode BuildDeliveryReceiptNode(Jid chat, string messageId, DateTimeOffset timestamp) =>
        BuildReceiptNode("delivery", chat, [messageId], participant: null, timestamp);

    private static BinaryNode BuildReceiptNode(string type, Jid chat, IReadOnlyList<string> messageIds, string? participant, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(messageIds);
        if (messageIds.Count == 0)
        {
            throw new SdkFormatException("A receipt needs at least one message id.");
        }

        foreach (string id in messageIds)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new SdkFormatException("Receipt message ids must be non-empty.");
            }
        }

        var attrs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = messageIds[0],
            ["type"] = type,
            ["to"] = chat.ToString(),
            ["t"] = timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(participant))
        {
            attrs["participant"] = participant;
        }

        if (messageIds.Count == 1)
        {
            return BinaryNode.Create("receipt", attrs);
        }

        var items = new BinaryNode[messageIds.Count - 1];
        for (int i = 1; i < messageIds.Count; i++)
        {
            items[i - 1] = BinaryNode.Create(
                "item",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["id"] = messageIds[i] });
        }

        return BinaryNode.FromChildren("receipt", [BinaryNode.FromChildren("list", items)], attrs);
    }

    /// <summary>
    /// Pattern-5 decrypt-failure path (04-04, MSG-06): raises the typed
    /// <see cref="Messenger.DecryptionFailed"/> event (every failure, uncapped), then
    /// sends <c>&lt;receipt type="retry"&gt;</c> for this (sender, message id) while the
    /// count stays within <see cref="RetryCountCap"/>. The <c>&lt;keys&gt;</c> block
    /// rides from retry #2 or whenever the sender has no session with us (sender key
    /// material unavailable). Emitted retries are best-effort: callers wrap and swallow
    /// non-cancellation faults so the pump never dies (the event is already raised).
    /// </summary>
    internal async Task EmitRetryReceiptAsync(
        CredentialRecord row,
        Jid sender,
        Jid chat,
        string messageId,
        DateTimeOffset originalTimestamp,
        bool hasSession,
        CancellationToken cancellationToken)
    {
        RetryCounter counter = _retryCounts.AddOrUpdate(
            string.Create(CultureInfo.InvariantCulture, $"{sender}:{messageId}"),
            _ => new RetryCounter(1, Interlocked.Increment(ref _retryTouchSequence)),
            (_, existing) => new RetryCounter(existing.Count + 1, Interlocked.Increment(ref _retryTouchSequence)));
        int count = counter.Count;
        if (_retryCounts.Count > MaxRetryCountKeys)
        {
            EvictOldestRetryCounters(); // bounded map: drop the coldest half, never the whole map (WR-03)
        }

        await _owner.RaiseDecryptionFailedAsync(
            new DecryptionFailedEventArgs(chat, sender, messageId, count, originalTimestamp),
            cancellationToken).ConfigureAwait(false);
        if (count > RetryCountCap)
        {
            return; // Pattern-5 cap: receipts stop, the typed event is the host's signal
        }

        StoreSession stores = await _owner.ResolveStoresAsync(cancellationToken).ConfigureAwait(false);
        BinaryNode? keys = null;
        int consumedPreKeyId = -1;
        if (count >= 2 || !hasSession)
        {
            (keys, consumedPreKeyId) = await BuildKeysBlockAsync(stores.PreKeys, row, cancellationToken).ConfigureAwait(false);
        }

        BinaryNode node = BuildRetryReceiptNode(sender, messageId, originalTimestamp, count, row.RegistrationId, keys);
        if (consumedPreKeyId >= 0)
        {
            // D-06 ordering: the store reflects reality (the prekey is spent) before the
            // wire claims it. A failed removal aborts the send — the key was never claimed.
            await stores.PreKeys.RemovePreKeyAsync(consumedPreKeyId, cancellationToken).ConfigureAwait(false);
        }

        await _owner.Connection.SendNodeAsync(node, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One per-(sender, id) retry counter: the emission count plus a monotonic
    /// touch sequence the bounded eviction orders by (WR-03).
    /// </summary>
    private readonly record struct RetryCounter(int Count, long LastTouched);

    /// <summary>
    /// Bounded-map eviction (WR-03): drops the oldest half of the counter map by
    /// last-touch sequence instead of clearing it wholesale, so a hostile flood of
    /// distinct failing (sender, id) pairs can never reset the Pattern-5 cap of a
    /// recently active pair (which would multiply the retry receipts and the
    /// one-time-prekey spend each one drives — T-4-13, ban safety). Entries
    /// updated concurrently with the eviction snapshot may still be dropped; that
    /// worst case is one counter restarting, never the whole map.
    /// </summary>
    private void EvictOldestRetryCounters()
    {
        int toRemove = _retryCounts.Count / 2;
        if (toRemove == 0)
        {
            return;
        }

        foreach (string key in _retryCounts
                     .OrderBy(static pair => pair.Value.LastTouched)
                     .Take(toRemove)
                     .Select(static pair => pair.Key))
        {
            _retryCounts.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Pattern-5 verbatim retry receipt: <c>&lt;receipt id type="retry" to="{sender device jid}"
    /// t="{orig-t}"&gt;</c> with the <c>&lt;retry count id t v="1"/&gt;</c> and
    /// <c>&lt;registration&gt;</c> (4-byte big-endian own registration id) children, plus
    /// the <c>&lt;keys&gt;</c> block when fresh-session material rides along.
    /// </summary>
    private static BinaryNode BuildRetryReceiptNode(
        Jid sender,
        string messageId,
        DateTimeOffset originalTimestamp,
        int count,
        int registrationId,
        BinaryNode? keys)
    {
        string t = originalTimestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var children = new List<BinaryNode>
        {
            BinaryNode.Create("retry", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["count"] = count.ToString(CultureInfo.InvariantCulture),
                ["id"] = messageId,
                ["t"] = t,
                ["v"] = "1",
            }),
            BinaryNode.FromBytes("registration", BigEndianBytes((uint)registrationId, PreKeyManager.RegistrationIdWidth), null),
        };
        if (keys is not null)
        {
            children.Add(keys);
        }

        return BinaryNode.FromChildren(
            "receipt",
            children,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["id"] = messageId,
                ["type"] = "retry",
                ["to"] = sender.ToString(),
                ["t"] = t,
            });
    }

    /// <summary>
    /// Builds the retry receipt's <c>&lt;keys&gt;</c> block from our own prekey material:
    /// identity (32-byte raw X25519 public), a one-time prekey (3-byte BE id + 32-byte
    /// value) taken from the store (or minted AND persisted when the probed window is
    /// empty — an advertised key must always be one the store can later hand to the
    /// inbound-pkmsg Bob path), the signed prekey (<c>skey</c>: id/value/signature from
    /// the own-upload checkpoint bundle), and the <c>device-identity</c> child. Returns
    /// <c>(null, -1)</c> when the own bundle checkpoint is missing or no advertisable
    /// one-time key exists — a keys block without an <c>skey</c> (or without a usable
    /// one-time key) is unusable, so the receipt degrades to a keys-less form.
    /// </summary>
    private async Task<(BinaryNode? Keys, int ConsumedPreKeyId)> BuildKeysBlockAsync(
        IPreKeyStore preKeys,
        CredentialRecord row,
        CancellationToken cancellationToken)
    {
        byte[] identityRaw = X3dh.DeriveX25519Public(row.IdentityKeyPrivate);
        string ownAddress = string.Create(
            CultureInfo.InvariantCulture, $"{row.PairedUser}:{row.Device}@{DeviceDirectory.UsyncServer}");
        PreKeyBundle? bundle = await preKeys.GetBundleAsync(ownAddress, cancellationToken).ConfigureAwait(false);
        if (bundle?.SignedPreKeyPublic is null || bundle.SignedPreKeySignature is null)
        {
            return (null, -1);
        }

        (byte[]? oneTimePublic, uint oneTimeId, int consumedId) =
            await TakeOneTimePreKeyAsync(preKeys, bundle, cancellationToken).ConfigureAwait(false);
        if (oneTimePublic is null)
        {
            return (null, -1); // no advertisable one-time key (ids exhausted): keys-less degrade
        }

        // ponytail: device-identity pins the own identity DJB form — upgrade to a
        // marshalled Account proto when the adv schema lands (plan 04-05/UAT).
        var keys = BinaryNode.FromChildren(
            "keys",
            [
                BinaryNode.FromBytes("type", [PreKeyManager.KeyBundleType], null),
                BinaryNode.FromBytes("identity", identityRaw, null),
                BinaryNode.FromChildren(
                    "key",
                    [
                        BinaryNode.FromBytes("id", BigEndianBytes(oneTimeId, PreKeyManager.PreKeyIdWidth), null),
                        BinaryNode.FromBytes("value", oneTimePublic, null),
                    ],
                    null),
                BinaryNode.FromChildren(
                    "skey",
                    [
                        BinaryNode.FromBytes("id", BigEndianBytes((uint)bundle.SignedPreKeyId, PreKeyManager.PreKeyIdWidth), null),
                        BinaryNode.FromBytes("value", bundle.SignedPreKeyPublic, null),
                        BinaryNode.FromBytes("signature", bundle.SignedPreKeySignature, null),
                    ],
                    null),
                BinaryNode.FromBytes("device-identity", SignalCipher.ToDjbIdentity(identityRaw), null),
            ],
            null);
        return (keys, consumedId);
    }

    /// <summary>
    /// Takes the next one-time prekey for a retry keys block: probes the store
    /// upward from the own bundle checkpoint's first-unserved id plus a per-process
    /// cursor until an actually stored key is found (bounded). A stored key is
    /// returned with its id (the caller removes it before the wire — D-06). When
    /// the whole probed window is empty, a fresh pair is minted AND persisted under
    /// an observed-free id inside that window before being advertised — a key the
    /// wire names must be one the store can later hand to the inbound-pkmsg Bob
    /// path; the minted pair is returned with its id for the same D-06 removal.
    /// <c>(null, 0, -1)</c> when the checkpoint ids are exhausted (no valid id).
    /// </summary>
    private async Task<(byte[]? Public, uint Id, int ConsumedPreKeyId)> TakeOneTimePreKeyAsync(
        IPreKeyStore preKeys,
        PreKeyBundle bundle,
        CancellationToken cancellationToken)
    {
        int slot = Interlocked.Increment(ref _oneTimeCursor) - 1;
        int? freeId = null;
        for (int probe = 0; probe < MaxOneTimeProbe; probe++)
        {
            int candidateId = bundle.OneTimePreKeyId + slot + probe;
            if (candidateId < 0 || candidateId > PreKeyManager.MaxPreKeyId)
            {
                break;
            }

            PreKeyRecordEntry? entry = await preKeys.GetPreKeyAsync(candidateId, cancellationToken).ConfigureAwait(false);
            if (entry?.PublicKey is { Length: X3dh.KeyLength } storedPublic)
            {
                return (storedPublic, (uint)candidateId, candidateId);
            }

            freeId ??= candidateId; // first observed-empty slot: the mint target
        }

        if (freeId is not int mintedId)
        {
            return (null, 0, -1); // no valid id left to advertise: keys-less degrade upstream
        }

        (byte[] mintedPrivate, byte[] mintedPublic) = X3dh.GenerateX25519KeyPair();
        try
        {
            // Persist BEFORE the wire can name it (D-06): never advertise a key
            // whose private the store cannot later produce.
            await preKeys.PutPreKeyAsync(
                mintedId, new PreKeyRecordEntry(mintedId, mintedPublic, mintedPrivate), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(mintedPrivate);
            CryptographicOperations.ZeroMemory(mintedPublic);
            throw;
        }

        CryptographicOperations.ZeroMemory(mintedPrivate);
        return (mintedPublic, (uint)mintedId, mintedId);
    }

    private static byte[] BigEndianBytes(uint value, int width)
    {
        var bytes = new byte[width];
        for (int i = width - 1; i >= 0; i--)
        {
            bytes[i] = (byte)(value & 0xFF);
            value >>= 8;
        }

        return bytes;
    }
}
