using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Signal;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.Runtime;

namespace Iwasug.WhatsApp.Sdk.Messaging;

/// <summary>
/// The single send path (04-03, MSG-01/MSG-03): hold the per-account send gate,
/// generate the client message id (<c>3EB0</c> + 18 CSPRNG bytes uppercase hex, 40 chars —
/// Baileys <c>generateMessageID</c> behavioral reference), resolve the peer's device set
/// via <see cref="DeviceDirectory"/>, ensure a session per device (fetch the bundle via
/// <see cref="PreKeyManager"/> when genuinely absent, then X3DH init), encrypt per device
/// through <see cref="DoubleRatchet"/> (<c>pkmsg</c> for a fresh prekey session, <c>msg</c>
/// after), assemble the node through <see cref="MessageBuilder"/>'s node seam, send via
/// the connection's <c>SendNodeAsync</c>, and await the correlated reply before returning
/// the <see cref="SendOutcome"/>. The group branch (04-05, MSG-02) shares the gate, the
/// one-network-send contract, and the ack correlation: members resolve through
/// <see cref="GroupDirectory"/>, an SKDM fans out pairwise per sender-key generation
/// through the same per-device sessions, and one signed <c>skmsg</c> ciphertext rides the
/// same node with the deterministic <c>phash</c>.
/// </summary>
/// <remarks>
/// <para>
/// The per-account gate (<c>SemaphoreSlim(1,1)</c> — the whatsmeow <c>messageSendLock</c>
/// behavioral equivalent, NOT the RateLimiter, which is Phase 5 scope) wraps the full send
/// (resolve → encrypt → send → await ack): two concurrent
/// <see cref="Messenger.SendMessageAsync"/> calls queue instead of interleaving ratchet
/// mutations; a cancelled queued send releases the gate and a subsequent send succeeds.
/// </para>
/// <para>
/// Idempotency contract (carried verbatim on <see cref="SendOutcome"/>): one logical send
/// = one network send; on timeout the SDK returns the timed-out id and drops; the host may
/// re-call, which is a NEW send with a NEW id; the SDK never re-sends a message on its own;
/// a peer retry receipt triggers resend only in plan 04-04 and only with the same id.
/// </para>
/// <para>
/// Discipline: every ratchet advance persists through
/// <see cref="DoubleRatchet.SignalSessionRepository"/> (D-06) before the message node
/// leaves; message ids are compared ordinally; ciphertext rides <c>enc</c> <c>Data</c>
/// (Pitfall 8); no log call on this class ever sees plaintext, ciphertext, or key bytes.
/// </para>
/// </remarks>
internal sealed class MessageSender
{
    private readonly Messenger _owner;
    private readonly DeviceDirectory _directory;
    private readonly GroupDirectory _groups;
    private readonly SenderKeyRepository _senderKeys;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly DurableWriteRetryPolicy _retry = new();
    private readonly ConcurrentDictionary<string, PendingSend> _pending = new(StringComparer.Ordinal);

    /// <summary>One awaiting send: correlation id → chat (phash invalidation target), the fanout phash, and the completion.</summary>
    private sealed record PendingSend(Jid Chat, string Phash, TaskCompletionSource<SendOutcome> Completion);

    /// <summary>
    /// Per-group SKDM distribution tracking (04-05 MSG-02 idempotency edge): the
    /// sender-key generation id this set belongs to plus the member device forms
    /// that generation's SKDM has already been delivered to. Process-local and
    /// SDK-owned policy (D-08); mutations happen only under the send gate, so the
    /// set needs no lock. A restarted process redelistributes once (self-healing).
    /// </summary>
    private sealed class Distribution
    {
        internal Distribution(uint senderKeyId) => SenderKeyId = senderKeyId;

        internal uint SenderKeyId { get; }

        internal HashSet<string> Devices { get; } = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, Distribution> _distributions = new(StringComparer.Ordinal);

    internal MessageSender(Messenger owner, DeviceDirectory directory, GroupDirectory groups, SenderKeyRepository senderKeys)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _groups = groups ?? throw new ArgumentNullException(nameof(groups));
        _senderKeys = senderKeys ?? throw new ArgumentNullException(nameof(senderKeys));
    }

    /// <summary>
    /// Sends one E2EE text message: exactly one network send, exactly one terminal
    /// outcome (reply, timeout, or a typed exception — cancellation unwrapped).
    /// </summary>
    /// <exception cref="SdkFormatException">Text is null/empty or the destination is malformed or resolves no devices.</exception>
    /// <exception cref="SdkConnectionException">The connection is not open or the send raced a teardown.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    internal Task<SendOutcome> SendAsync(Jid chat, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (string.IsNullOrEmpty(text))
        {
            throw new SdkFormatException("Message text must not be null or empty.");
        }

        return SendPayloadAsync(chat, text, MessageBuilder.BuildPlainText(text), cancellationToken);
    }

    /// <summary>
    /// Sends one E2EE message from pre-marshalled plaintext bytes (04-04, MSG-05): the
    /// quote-carrying reply path builds its extended-text payload itself and hands it
    /// here, so the whole send path (gate, one-network-send contract, ack correlation,
    /// D-20 cache write) stays shared with <see cref="SendAsync"/>. The cached display
    /// text is <paramref name="displayText"/>; the cached resend payload is
    /// <paramref name="plaintext"/> verbatim (so a peer retry receipt for this id
    /// resends the exact quoted bytes).
    /// </summary>
    /// <exception cref="SdkFormatException">The plaintext is null/empty, the display text is null/empty, or the destination is malformed or resolves no devices.</exception>
    /// <exception cref="SdkConnectionException">The connection is not open or the send raced a teardown.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    internal async Task<SendOutcome> SendPayloadAsync(Jid chat, string displayText, byte[] plaintext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(plaintext);
        if (plaintext.Length == 0)
        {
            throw new SdkFormatException("Message plaintext must not be empty.");
        }

        if (string.IsNullOrEmpty(displayText))
        {
            throw new SdkFormatException("Message text must not be null or empty.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        string messageId = GenerateMessageId();
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SendCoreAsync(chat, messageId, displayText, plaintext, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task<SendOutcome> SendCoreAsync(Jid chat, string messageId, string displayText, byte[] plaintext, CancellationToken cancellationToken)
    {
        StoreSession stores = await _owner.ResolveStoresAsync(cancellationToken).ConfigureAwait(false);
        CredentialRecord? row = await stores.Credentials.GetAsync(cancellationToken).ConfigureAwait(false);
        if (row?.PairedUser is null)
        {
            throw new SdkConnectionException("The account is not paired: the message was not sent.");
        }

        IReadOnlyList<Jid> devices = await _directory.ResolveAsync(chat, row.PairedUser, cancellationToken).ConfigureAwait(false);
        if (devices.Count == 0)
        {
            throw new SdkFormatException($"No devices resolved for the recipient: the message was not sent.");
        }

        string phash = DeviceDirectory.ComputePhash(devices);

        long t = _owner.Clock.GetUtcNow().ToUnixTimeSeconds();

        // MSG-06 resend source (Pitfall 6: the recent cache is the ONLY resend source):
        // store the marshalled plaintext keyed by this send's id before anything rides
        // the wire, so a peer retry receipt for this id can always find its material.
        await TryCacheRecentAsync(stores, messageId, chat, displayText, t, plaintext, sender: null, cancellationToken).ConfigureAwait(false);

        var ciphertexts = new List<DeviceCiphertext>(devices.Count);
        bool anyPreKey = false;
        foreach (Jid device in devices)
        {
            DoubleRatchet ratchet = await EnsureSessionAsync(stores, row, device, cancellationToken).ConfigureAwait(false);
            SignalEncryptOutcome outcome = ratchet.Encrypt(plaintext, cancellationToken);
            byte[] wire;
            string encType;
            if (outcome is SignalEncryptOutcome.PreKeyMessageSent preKey)
            {
                wire = preKey.WireBytes;
                encType = MessageBuilder.EncTypePreKey;
                anyPreKey = true;
            }
            else
            {
                wire = ((SignalEncryptOutcome.MessageSent)outcome).WireBytes;
                encType = MessageBuilder.EncTypeMessage;
            }

            // D-06: the ratchet advanced — persist the new state before anything rides the wire.
            await PersistSessionAsync(stores, device, ratchet, cancellationToken).ConfigureAwait(false);
            ciphertexts.Add(new DeviceCiphertext(device, encType, wire));
        }

        List<BinaryNode> children = [.. BuildRecipientChildren(ciphertexts)];
        if (anyPreKey)
        {
            // Pattern 2: the device-identity child rides the node when any child is pkmsg.
            // ponytail: content pins the own identity DJB form — upgrade to a marshalled
            // Account proto when the adv schema lands (plan 04-04/UAT).
            children.Add(BinaryNode.FromBytes(
                "device-identity",
                SignalCipher.ToDjbIdentity(X3dh.DeriveX25519Public(row.IdentityKeyPrivate)),
                null));
        }

        BinaryNode node = BinaryNode.FromChildren(
            "message",
            children,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["id"] = messageId,
                ["to"] = chat.ToString(),
                ["type"] = "text",
                ["t"] = t.ToString(CultureInfo.InvariantCulture),
            });

        return await AwaitAckAsync(node, chat, messageId, phash, onSent: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one E2EE group message via the sender-key path (04-05, MSG-02):
    /// members resolve through <see cref="GroupDirectory"/> (participants-for-fanout
    /// only), every member DEVICE resolves through <see cref="DeviceDirectory"/>,
    /// an SKDM fans out pairwise to exactly the devices that have not yet received
    /// this sender-key generation (idempotency edge: a repeated send with a live
    /// key issues NO new distribution), and ONE signed <c>skmsg</c> ciphertext plus
    /// the deterministic <c>phash</c> rides the same <c>&lt;message&gt;</c> node.
    /// </summary>
    /// <exception cref="SdkFormatException">Text is null/empty, the group JID is malformed, the account is not a member (or the participant list resolves empty), or no member devices resolve — always with zero frames.</exception>
    /// <exception cref="SdkConnectionException">The connection is not open or the send raced a teardown.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    internal Task<SendOutcome> SendGroupAsync(Jid group, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (string.IsNullOrEmpty(text))
        {
            throw new SdkFormatException("Message text must not be null or empty.");
        }

        return SendGroupPayloadAsync(group, text, MessageBuilder.BuildPlainText(text), cancellationToken);
    }

    private async Task<SendOutcome> SendGroupPayloadAsync(Jid group, string displayText, byte[] plaintext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(plaintext);
        if (plaintext.Length == 0)
        {
            throw new SdkFormatException("Message plaintext must not be empty.");
        }

        if (string.IsNullOrEmpty(displayText))
        {
            throw new SdkFormatException("Message text must not be null or empty.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        string messageId = GenerateMessageId();
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SendGroupCoreAsync(group, messageId, displayText, plaintext, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task<SendOutcome> SendGroupCoreAsync(Jid group, string messageId, string displayText, byte[] plaintext, CancellationToken cancellationToken)
    {
        StoreSession stores = await _owner.ResolveStoresAsync(cancellationToken).ConfigureAwait(false);
        CredentialRecord? row = await stores.Credentials.GetAsync(cancellationToken).ConfigureAwait(false);
        if (row?.PairedUser is null)
        {
            throw new SdkConnectionException("The account is not paired: the message was not sent.");
        }

        // T-4-16 empty-input edge: a not-a-member group (typed reply failure) or an
        // empty participant list fails BEFORE any node is built — zero frames.
        IReadOnlyList<Jid> members = await _groups.ResolveAsync(group, cancellationToken).ConfigureAwait(false);
        if (members.Count == 0)
        {
            throw new SdkFormatException("The group participant list resolved empty: the group message was not sent.");
        }

        var memberDevices = new List<Jid>();
        foreach (Jid member in members)
        {
            IReadOnlyList<Jid> devices = await _directory.ResolveAsync(member, row.PairedUser, cancellationToken).ConfigureAwait(false);
            memberDevices.AddRange(devices);
        }

        if (memberDevices.Count == 0)
        {
            throw new SdkFormatException("No member devices resolved for the group: the message was not sent.");
        }

        // Deterministic phash over the own-excluded member device set (Pattern 6;
        // fixture-pinned in SenderKeyTests — the spelling is Jid.ToString()).
        string phash = DeviceDirectory.ComputePhash(memberDevices);

        long t = _owner.Clock.GetUtcNow().ToUnixTimeSeconds();
        await TryCacheRecentAsync(stores, messageId, group, displayText, t, plaintext, sender: null, cancellationToken).ConfigureAwait(false);

        // Sender key + SKDM fanout set (idempotency edge): distribute this
        // generation's SKDM exactly once per member device.
        string senderKeyAddress = SenderKeyRepository.OutboundAddress(group);
        SenderKey senderKey = _senderKeys.TryLoad(senderKeyAddress) ?? SenderKey.Create();
        uint senderKeyId = senderKey.ActiveKeyId;
        if (!_distributions.TryGetValue(group.ToString(), out Distribution? distribution)
            || distribution.SenderKeyId != senderKeyId)
        {
            distribution = new Distribution(senderKeyId);
            _distributions[group.ToString()] = distribution;
        }

        var skdmCiphertexts = new List<DeviceCiphertext>();
        bool anyPreKey = false;
        byte[]? skdmPlaintext = null;
        foreach (Jid device in memberDevices)
        {
            if (distribution.Devices.Contains(device.ToString()))
            {
                continue; // already distributed this generation — no duplicate SKDM fanout
            }

            skdmPlaintext ??= MessageBuilder.BuildSenderKeyDistribution(
                group.ToString(),
                senderKey.BuildDistribution());

            DoubleRatchet ratchet = await EnsureSessionAsync(stores, row, device, cancellationToken).ConfigureAwait(false);
            SignalEncryptOutcome outcome = ratchet.Encrypt(skdmPlaintext, cancellationToken);
            byte[] wire;
            string encType;
            if (outcome is SignalEncryptOutcome.PreKeyMessageSent preKey)
            {
                wire = preKey.WireBytes;
                encType = MessageBuilder.EncTypePreKey;
                anyPreKey = true;
            }
            else
            {
                wire = ((SignalEncryptOutcome.MessageSent)outcome).WireBytes;
                encType = MessageBuilder.EncTypeMessage;
            }

            // D-06: the ratchet advanced — persist before anything rides the wire.
            await PersistSessionAsync(stores, device, ratchet, cancellationToken).ConfigureAwait(false);
            skdmCiphertexts.Add(new DeviceCiphertext(device, encType, wire));
        }

        // ONE signed skmsg ciphertext for the whole group; the sender chain advanced
        // and is persisted (D-06) BEFORE the node leaves the wire — a cancelled or
        // timed-out send leaves the chain at a consistent persisted iteration.
        byte[] skmsg = senderKey.Encrypt(plaintext);
        _senderKeys.Save(senderKeyAddress, senderKey);

        var children = new List<BinaryNode>();
        if (skdmCiphertexts.Count > 0)
        {
            children.Add(BinaryNode.FromChildren(
                "participants",
                [.. BuildRecipientChildren(skdmCiphertexts)],
                null));
        }

        if (anyPreKey)
        {
            // Pattern 2: the device-identity child rides the node when any child is pkmsg.
            children.Add(BinaryNode.FromBytes(
                "device-identity",
                SignalCipher.ToDjbIdentity(X3dh.DeriveX25519Public(row.IdentityKeyPrivate)),
                null));
        }

        children.Add(BinaryNode.FromBytes(
            "enc",
            skmsg,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["v"] = MessageBuilder.EncVersion,
                ["type"] = MessageBuilder.EncTypeSenderKey,
            }));

        BinaryNode node = BinaryNode.FromChildren(
            "message",
            children,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["id"] = messageId,
                ["to"] = group.ToString(),
                ["type"] = "text",
                ["t"] = t.ToString(CultureInfo.InvariantCulture),
                ["phash"] = phash,
            });

        return await AwaitAckAsync(
            node,
            group,
            messageId,
            phash,
            () =>
            {
                // The node left the wire: this generation's SKDM is delivered to
                // exactly these devices (a timeout still marks them — the frames
                // reached the wire; a pre-wire failure never reaches this callback).
                foreach (DeviceCiphertext skdm in skdmCiphertexts)
                {
                    distribution.Devices.Add(skdm.Device.ToString());
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Correlates one inbound <c>ack</c> for a sent message id: completes the pending
    /// send exactly once and returns the terminal fact (outcome + chat) for the
    /// MSG-07 stream; a late or duplicate ack finds no pending entry and returns null
    /// (no outcome, no event). An ack <c>phash</c> that no longer matches the fanout
    /// set (Pitfall 3) invalidates the device cache for the peer and raises the typed
    /// warning before the outcome completes — the ack itself still settles the send
    /// normally.
    /// </summary>
    internal (SendOutcome Outcome, Jid Chat)? CompleteFromAck(string messageId, int? error, string? t, string? serverId, string? phash)
    {
        if (!_pending.TryGetValue(messageId, out PendingSend? pending))
        {
            return null; // late/duplicate ack: no pending entry — a no-op, outcome untouched
        }

        if (!string.IsNullOrEmpty(phash)
            && !string.Equals(phash, pending.Phash, StringComparison.Ordinal))
        {
            _directory.Invalidate(pending.Chat);
            if (string.Equals(pending.Chat.Server, GroupDirectory.GroupServer, StringComparison.Ordinal))
            {
                // Group ack: the member device set moved — the participant cache
                // re-queries on the next send (device sets re-resolve through TTL).
                _groups.Invalidate(pending.Chat);
            }

            _owner.RaiseDeviceSetChanged(
                new DeviceSetChangedEventArgs(pending.Chat, messageId, pending.Phash, phash));
        }

        SendOutcome outcome = error is > 0
            ? new SendOutcome(messageId, SendOutcomeStatus.ServerError, error, t, serverId)
            : new SendOutcome(messageId, SendOutcomeStatus.Sent, error, t, serverId);
        pending.Completion.TrySetResult(outcome);
        return (outcome, pending.Chat);
    }

    /// <summary>True when a pending send waits on this id (ack-correlation seam for the dispatcher).</summary>
    internal bool IsPending(string messageId) => _pending.ContainsKey(messageId);

    /// <summary>
    /// Responds to a peer's Pattern-5 retry receipt for one of OUR outgoing messages
    /// (04-04, MSG-06): re-encrypts the cached marshalled plaintext for the requester
    /// and resends it as a new <c>&lt;message&gt;</c> node with the SAME id, type, and
    /// <c>t</c> (Pattern 5 verbatim; <c>device_fanout="false"</c> for 1:1). A retry
    /// carrying a <c>&lt;keys&gt;</c> block rebuilds a fresh session from it first —
    /// pinned-identity enforced (a pinned identity that disagrees with the keys-block
    /// identity refuses the rebuild; the signature verifies under the pin, or under the
    /// keys identity on genuine first contact, per <see cref="PreKeyManager"/> TOFU
    /// semantics).
    /// </summary>
    /// <remarks>
    /// T-4-12 (information disclosure): the resend serves only the message's ORIGINAL
    /// 1:1 recipient — the requester's bare JID must equal the cache entry's chat
    /// exactly; anything else refuses with no resend and no throw. Pitfall 6 degrade
    /// contract: a cache the recent store cannot satisfy (trimmed past the horizon, no
    /// retained payload, or a throwing store) degrades to a logged no-op — this method
    /// never throws through the receive path. No log call ever sees plaintext, the
    /// payload, or key bytes.
    /// </remarks>
    /// <param name="requester">The retry requester's full per-device JID (the receipt's <c>from</c>).</param>
    /// <param name="messageId">The failed message id (the receipt's <c>id</c> — the resend reuses it exactly).</param>
    /// <param name="peerRegistrationId">The peer's 14-bit registration id from the receipt's <c>registration</c> child, when present.</param>
    /// <param name="keys">The receipt's <c>&lt;keys&gt;</c> block for a fresh-session rebuild, or null for an existing-session resend.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal async Task ResendForRetryReceiptAsync(
        Jid requester,
        string messageId,
        uint? peerRegistrationId,
        BinaryNode? keys,
        CancellationToken cancellationToken)
    {
        try
        {
            StoreSession stores = await _owner.ResolveStoresAsync(cancellationToken).ConfigureAwait(false);
            CredentialRecord? row = await stores.Credentials.GetAsync(cancellationToken).ConfigureAwait(false);
            if (row?.PairedUser is null)
            {
                return; // unpaired account: nothing to resend
            }

            RecentMessageEntry? entry = await stores.RecentMessages.GetAsync(messageId, cancellationToken).ConfigureAwait(false);
            if (entry is null || entry.ResendPayload is not { Length: > 0 })
            {
                return; // trimmed past the horizon / no retained payload: the documented no-resend outcome (Pitfall 6, D-05)
            }

            // T-4-12: the resend serves ONLY the message's original 1:1 recipient. The
            // cache entry's chat names that recipient; the requester's bare form must
            // match it exactly. Group chats refuse (membership is unknown to the SDK and
            // no group sends exist yet); the receive path continues unharmed either way.
            Jid requesterChat = Jid.Parse($"{DeviceDirectory.BaseUser(requester)}@{requester.Server}");
            if (!string.Equals(entry.ChatJid, requesterChat.ToString(), StringComparison.Ordinal))
            {
                return; // wrong requester (T-4-12): no resend, no throw
            }

            string address = DoubleRatchet.SignalSessionRepository.AddressFor(requester);
            var repository = new DoubleRatchet.SignalSessionRepository(stores.SignalSessions, _retry);
            using (IDisposable sessionGate = await SignalSessionGate.AcquireAsync(address, cancellationToken).ConfigureAwait(false))
            {
                DoubleRatchet session;
                if (keys is not null)
                {
                    session = await BuildFreshSessionFromKeysAsync(
                        stores, row, address, keys, peerRegistrationId, repository, cancellationToken).ConfigureAwait(false);
                    if (session is null)
                    {
                        return; // malformed or pin-conflicting keys: refuse and degrade (never re-trust)
                    }
                }
                else
                {
                    session = await repository.LoadUnlockedAsync(address, cancellationToken).ConfigureAwait(false);
                    if (session is null)
                    {
                        return; // no keys block and no live session: nothing to re-encrypt under
                    }
                }

                SignalEncryptOutcome outcome = session.Encrypt(entry.ResendPayload, cancellationToken);
                byte[] wire;
                string encType;
                if (outcome is SignalEncryptOutcome.PreKeyMessageSent preKey)
                {
                    wire = preKey.WireBytes;
                    encType = MessageBuilder.EncTypePreKey;
                }
                else
                {
                    wire = ((SignalEncryptOutcome.MessageSent)outcome).WireBytes;
                    encType = MessageBuilder.EncTypeMessage;
                }

                // D-06: the ratchet advanced (fresh or live) — persist before the resend rides the wire.
                await repository.SaveUnlockedAsync(address, session, cancellationToken).ConfigureAwait(false);

                BinaryNode node = BuildResendNode(row, requester, messageId, entry.TimestampUtc, wire, encType);
                await _owner.Connection.SendNodeAsync(node, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // D-20/Pitfall 6 log-and-continue: a resend fault (bad keys, store failure)
            // never breaks the receive path — the retry goes unanswered, by contract.
        }
    }

    /// <summary>
    /// Builds a fresh Alice session from a retry receipt's <c>&lt;keys&gt;</c> block
    /// (bounds-checked parse into a <see cref="PeerBundle"/>), enforcing the pinned
    /// identity before any use: a pin that disagrees with the keys-block identity
    /// returns null (refuse, never re-trust); an absent pin lands first (TOFU, D-06)
    /// and the signed-prekey signature then verifies under it. Returns null for any
    /// malformed block or an invalid signature.
    /// </summary>
    private async Task<DoubleRatchet?> BuildFreshSessionFromKeysAsync(
        StoreSession stores,
        CredentialRecord row,
        string address,
        BinaryNode keys,
        uint? peerRegistrationId,
        DoubleRatchet.SignalSessionRepository repository,
        CancellationToken cancellationToken)
    {
        if (!TryParseKeysBlock(keys, peerRegistrationId, out PeerBundle? bundle) || bundle is null)
        {
            return null; // malformed keys block: degrade
        }

        SignalIdentityRecord? pinned = await stores.Identity.GetAsync(address, cancellationToken).ConfigureAwait(false);
        byte[]? pinnedDjb = pinned?.IdentityKeyDjb;
        if (pinnedDjb is not null
            && !pinnedDjb.AsSpan().SequenceEqual(bundle.IdentityKeyDjb))
        {
            return null; // identity changed under the pin: refuse the rebuild (T-4-02 lineage)
        }

        if (pinnedDjb is null)
        {
            byte[] pin = (byte[])bundle.IdentityKeyDjb.Clone();
            await stores.Identity.PutAsync(address, new SignalIdentityRecord(null, pin, true), cancellationToken).ConfigureAwait(false);
        }

        byte[] verifyKey = pinnedDjb ?? bundle.IdentityKeyDjb;
        (byte[] ephemeralPrivate, _) = X3dh.GenerateX25519KeyPair();
        try
        {
            AliceSession aliceSession = X3dh.InitializeAlice(row.IdentityKeyPrivate, ephemeralPrivate, bundle, verifyKey);
            (byte[] sendPrivate, byte[] sendPublic) = X3dh.GenerateX25519KeyPair();
            DoubleRatchet fresh = DoubleRatchet.InitializeAlice(
                aliceSession,
                sendPrivate,
                sendPublic,
                bundle,
                SignalCipher.ToDjbIdentity(X3dh.DeriveX25519Public(row.IdentityKeyPrivate)),
                bundle.IdentityKeyDjb,
                (uint)row.RegistrationId);
            CryptographicOperations.ZeroMemory(sendPrivate);
            await repository.SaveUnlockedAsync(address, fresh, cancellationToken).ConfigureAwait(false);
            return fresh;
        }
        catch (SdkFormatException)
        {
            return null; // invalid signature or missized keys: degrade, never throw
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ephemeralPrivate);
        }
    }

    /// <summary>
    /// Bounds-checked parse of a retry <c>&lt;keys&gt;</c> block into a
    /// <see cref="PeerBundle"/>: 1-byte <c>type</c>, 32-byte raw <c>identity</c>,
    /// <c>key</c> (3-byte big-endian id + 32-byte value), <c>skey</c> (id/value/
    /// 64-byte signature). The receipt's <c>registration</c> child supplies the
    /// bundle's 14-bit registration id (an out-of-range value fails validation
    /// downstream and degrades). Anything missized fails closed.
    /// </summary>
    private static bool TryParseKeysBlock(BinaryNode keys, uint? peerRegistrationId, out PeerBundle? bundle)
    {
        bundle = null;
        byte[]? identity = ChildData(keys, "identity");
        BinaryNode? key = FirstChild(keys, "key");
        byte[]? keyId = key is null ? null : ChildData(key, "id");
        byte[]? keyValue = key is null ? null : ChildData(key, "value");
        BinaryNode? skey = FirstChild(keys, "skey");
        byte[]? skeyId = skey is null ? null : ChildData(skey, "id");
        byte[]? skeyValue = skey is null ? null : ChildData(skey, "value");
        byte[]? skeySignature = skey is null ? null : ChildData(skey, "signature");
        if (identity is not { Length: X3dh.KeyLength }
            || keyId is not { Length: PreKeyManager.PreKeyIdWidth }
            || keyValue is not { Length: X3dh.KeyLength }
            || skeyId is not { Length: PreKeyManager.PreKeyIdWidth }
            || skeyValue is not { Length: X3dh.KeyLength }
            || skeySignature is not { Length: SignalCipher.SignatureLength }
            || !TryReadBigEndian(skeyId, out uint signedId)
            || !TryReadBigEndian(keyId, out uint oneTimeId))
        {
            return false;
        }

        bundle = new PeerBundle(
            SignalCipher.ToDjbIdentity(identity),
            (byte[])skeyValue.Clone(),
            (byte[])skeySignature.Clone(),
            signedId,
            (byte[])keyValue.Clone(),
            oneTimeId,
            peerRegistrationId ?? 0);
        return true;
    }

    /// <summary>
    /// Pattern-5 verbatim resend node: same id, type, and original <c>t</c>, to the
    /// requesting device only, with <c>device_fanout="false"</c> (1:1) and the
    /// <c>device-identity</c> child when the enc is a prekey message.
    /// </summary>
    private static BinaryNode BuildResendNode(
        CredentialRecord row,
        Jid requester,
        string messageId,
        DateTimeOffset originalTimestamp,
        byte[] wire,
        string encType)
    {
        var children = new List<BinaryNode>
        {
            BinaryNode.FromChildren(
                "to",
                [BinaryNode.FromBytes(
                    "enc",
                    wire,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["v"] = MessageBuilder.EncVersion,
                        ["type"] = encType,
                    })],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["jid"] = requester.ToString(),
                }),
        };
        if (string.Equals(encType, MessageBuilder.EncTypePreKey, StringComparison.Ordinal))
        {
            // Pattern 2 precedent: the device-identity child rides when the enc is pkmsg.
            children.Add(BinaryNode.FromBytes(
                "device-identity",
                SignalCipher.ToDjbIdentity(X3dh.DeriveX25519Public(row.IdentityKeyPrivate)),
                null));
        }

        return BinaryNode.FromChildren(
            "message",
            children,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["id"] = messageId, // SAME id — the Pattern-5 resend contract
                ["to"] = requester.ToString(),
                ["type"] = "text",
                ["t"] = originalTimestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                ["device_fanout"] = "false",
            });
    }

    /// <summary>
    /// Best-effort recent-cache write for one outbound send (D-20 log-and-continue):
    /// the marshalled plaintext is stored keyed by this send's id so a peer retry
    /// receipt or a quoted reply can always find its material; a cache failure
    /// never breaks the send — the retry's documented outcome is a degrade.
    /// </summary>
    private async Task TryCacheRecentAsync(
        StoreSession stores,
        string messageId,
        Jid chat,
        string displayText,
        long timestampSeconds,
        byte[] plaintext,
        string? sender,
        CancellationToken cancellationToken)
    {
        try
        {
            await stores.RecentMessages.PutAsync(
                new RecentMessageEntry(
                    messageId,
                    chat.ToString(),
                    DateTimeOffset.FromUnixTimeSeconds(timestampSeconds),
                    displayText,
                    sender,
                    plaintext),
                cancellationToken).ConfigureAwait(false);
            await stores.RecentMessages.PurgeExpiredAsync(
                DateTimeOffset.FromUnixTimeSeconds(timestampSeconds) - MessageDispatcher.RetentionHorizon,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // D-20 log-and-continue: the send proceeds; a later retry degrades instead.
        }
    }

    /// <summary>
    /// One network send + exactly-one-terminal-outcome: register the pending
    /// entry, send the node, invoke <paramref name="onSent"/> the moment the node
    /// leaves the wire (distribution-marking seam for the group path), then await
    /// the correlated ack inside the send window.
    /// </summary>
    private async Task<SendOutcome> AwaitAckAsync(
        BinaryNode node,
        Jid chat,
        string messageId,
        string phash,
        Action? onSent,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<SendOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[messageId] = new PendingSend(chat, phash, completion);
        try
        {
            await _owner.Connection.SendNodeAsync(node, cancellationToken).ConfigureAwait(false);
            onSent?.Invoke();

            TimeSpan ackWindow = TimeSpan.FromMilliseconds(_owner.Connection.SendAckTimeoutMs);
            Task timeout = Task.Delay(ackWindow, _owner.Clock, cancellationToken);
            Task winner = await Task.WhenAny(completion.Task, timeout).ConfigureAwait(false);
            if (!ReferenceEquals(winner, completion.Task))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new SendOutcome(messageId, SendOutcomeStatus.TimedOut);
            }

            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(messageId, out _);
        }
    }

    private static BinaryNode? FirstChild(BinaryNode parent, string tag)
    {
        IReadOnlyList<BinaryNode>? children = parent.Children;
        if (children is null)
        {
            return null;
        }

        foreach (BinaryNode child in children)
        {
            if (string.Equals(child.Tag, tag, StringComparison.Ordinal))
            {
                return child;
            }
        }

        return null;
    }

    private static byte[]? ChildData(BinaryNode parent, string tag) => FirstChild(parent, tag)?.Data;

    private static bool TryReadBigEndian(byte[] bytes, out uint value)
    {
        value = 0;
        foreach (byte b in bytes)
        {
            value = (value << 8) | b;
        }

        return true;
    }

    private async Task<DoubleRatchet> EnsureSessionAsync(StoreSession stores, CredentialRecord row, Jid device, CancellationToken cancellationToken)
    {
        string address = DoubleRatchet.SignalSessionRepository.AddressFor(device);
        var repository = new DoubleRatchet.SignalSessionRepository(stores.SignalSessions, _retry);
        DoubleRatchet? existing = await repository.LoadAsync(address, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        // First contact has no pinned identity yet: PreKeyManager verifies under the
        // fetched bundle's own identity (TOFU seam, pinned before any use) and hardens
        // every later contact against the pin.
        SignalIdentityRecord? pinned = await stores.Identity.GetAsync(address, cancellationToken).ConfigureAwait(false);
        var manager = new PreKeyManager(
            _owner.RouterProvider() ?? throw new SdkConnectionException("The connection is not open: the prekey bundle was not fetched."),
            _retry);
        EnsureSessionOutcome outcome = await manager.EnsureSessionAsync(
            address,
            device,
            row.IdentityKeyPrivate,
            (uint)row.RegistrationId,
            pinned?.IdentityKeyDjb,
            repository,
            stores.Identity,
            stores.PreKeys,
            cancellationToken).ConfigureAwait(false);

        return outcome switch
        {
            EnsureSessionOutcome.Created created => created.Session,
            EnsureSessionOutcome.Existing => await repository.LoadAsync(address, cancellationToken).ConfigureAwait(false)
                ?? throw new SdkStoreException("The session store lost a session between ensure and load."),
            EnsureSessionOutcome.IdentityChanged => throw new SdkStoreException("The peer identity changed: refusing to send under the pinned session key."),
            EnsureSessionOutcome.FetchFailed => throw new SdkConnectionException("The peer prekey bundle could not be fetched: the message was not sent."),
            _ => throw new SdkStoreException("The session-ensure outcome was unrecognized."),
        };
    }

    private async Task PersistSessionAsync(StoreSession stores, Jid device, DoubleRatchet ratchet, CancellationToken cancellationToken)
    {
        var repository = new DoubleRatchet.SignalSessionRepository(stores.SignalSessions, _retry);
        await repository.SaveAsync(DoubleRatchet.SignalSessionRepository.AddressFor(device), ratchet, cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<BinaryNode> BuildRecipientChildren(IReadOnlyList<DeviceCiphertext> ciphertexts)
    {
        foreach (DeviceCiphertext device in ciphertexts)
        {
            yield return BinaryNode.FromChildren(
                "to",
                [BinaryNode.FromBytes(
                    "enc",
                    device.Ciphertext,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["v"] = MessageBuilder.EncVersion,
                        ["type"] = device.EncType,
                    })],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["jid"] = device.Device.ToString(),
                });
        }
    }

    /// <summary>3EB0 + 18 CSPRNG bytes uppercase hex = 40 chars (Baileys generateMessageID behavioral reference).</summary>
    internal static string GenerateMessageId()
    {
        return string.Create(CultureInfo.InvariantCulture, $"3EB0{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18))}");
    }
}
