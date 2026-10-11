using System.Collections.Concurrent;
using System.Globalization;
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
/// the <see cref="SendOutcome"/>.
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
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly DurableWriteRetryPolicy _retry = new();
    private readonly ConcurrentDictionary<string, PendingSend> _pending = new(StringComparer.Ordinal);

    /// <summary>One awaiting send: correlation id → chat (phash invalidation target), the fanout phash, and the completion.</summary>
    private sealed record PendingSend(Jid Chat, string Phash, TaskCompletionSource<SendOutcome> Completion);

    internal MessageSender(Messenger owner, DeviceDirectory directory)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    }

    /// <summary>
    /// Sends one E2EE text message: exactly one network send, exactly one terminal
    /// outcome (reply, timeout, or a typed exception — cancellation unwrapped).
    /// </summary>
    /// <exception cref="SdkFormatException">Text is null/empty or the destination is malformed or resolves no devices.</exception>
    /// <exception cref="SdkConnectionException">The connection is not open or the send raced a teardown.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    internal async Task<SendOutcome> SendAsync(Jid chat, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (string.IsNullOrEmpty(text))
        {
            throw new SdkFormatException("Message text must not be null or empty.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        string messageId = GenerateMessageId();
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SendCoreAsync(chat, messageId, text, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task<SendOutcome> SendCoreAsync(Jid chat, string messageId, string text, CancellationToken cancellationToken)
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

        byte[] plaintext = MessageBuilder.BuildPlainText(text);
        long t = _owner.Clock.GetUtcNow().ToUnixTimeSeconds();

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

        var completion = new TaskCompletionSource<SendOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[messageId] = new PendingSend(chat, phash, completion);
        try
        {
            await _owner.Connection.SendNodeAsync(node, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Correlates one inbound <c>ack</c> for a sent message id: completes the pending
    /// send exactly once; a late or duplicate ack finds no pending entry and is a no-op.
    /// An ack <c>phash</c> that no longer matches the fanout set (Pitfall 3) invalidates
    /// the device cache for the peer and raises the typed warning before the outcome
    /// completes — the ack itself still settles the send normally.
    /// </summary>
    internal void CompleteFromAck(string messageId, int? error, string? t, string? serverId, string? phash)
    {
        if (!_pending.TryGetValue(messageId, out PendingSend? pending))
        {
            return; // late/duplicate ack: no pending entry — a no-op, outcome untouched
        }

        if (!string.IsNullOrEmpty(phash)
            && !string.Equals(phash, pending.Phash, StringComparison.Ordinal))
        {
            _directory.Invalidate(pending.Chat);
            _owner.RaiseDeviceSetChanged(
                new DeviceSetChangedEventArgs(pending.Chat, messageId, pending.Phash, phash));
        }

        SendOutcome outcome = error is > 0
            ? new SendOutcome(messageId, SendOutcomeStatus.ServerError, error, t, serverId)
            : new SendOutcome(messageId, SendOutcomeStatus.Sent, error, t, serverId);
        pending.Completion.TrySetResult(outcome);
    }

    /// <summary>True when a pending send waits on this id (ack-correlation seam for the dispatcher).</summary>
    internal bool IsPending(string messageId) => _pending.ContainsKey(messageId);

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
