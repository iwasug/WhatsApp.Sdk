using System.Collections.Concurrent;
using System.Security.Cryptography;
using Google.Protobuf;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Pairing;
using Iwasug.WhatsApp.Sdk.Stores;
using NSec.Cryptography;

namespace Iwasug.WhatsApp.Sdk.Tests.Pairing;

/// <summary>
/// Shared verified pair-success drive (plan 03-06 Task 1): one canonical
/// success-container builder plus a scripted coordinator run that takes the
/// store's current credential row through the production verify/write path to
/// a <see cref="PairingStatus.Success"/> terminal. Extracted from
/// PairingLifecycleTests so the resume lifecycle proof (ResumeTests) pairs
/// through exactly the code the ordering proofs exercise — the suite has one
/// success container, never two hand-rolled copies of a security-critical
/// construction.
/// </summary>
/// <remarks>
/// Output discipline (V6/T-3-07): exceptions here name events and operations
/// only — key material never enters an exception message.
/// </remarks>
internal static class PairingSuccessFixture
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Loopback-only registration defaults for the coordinator's payload
    /// provider — fixture material, never production secrets.
    /// </summary>
    internal static RegistrationDefaults DummyRegistrationDefaults() =>
        new(FixedBytes(0x11), 7, FixedBytes(0x31), FixedSignature(), 42, "android", 2, 3000, 1043857760);

    /// <summary>
    /// Builds the pair-success container the server would push: details bound
    /// to the row's adv secret (HMAC) and signed by the fixed account key and
    /// the row-pinned device signing key — the exact bytes
    /// <see cref="PairSuccessVerifier"/> verifies.
    /// </summary>
    internal static byte[] BuildSuccessContainer(CredentialRecord row, string user, uint device, uint registrationId)
    {
        byte[] accountSeed = FixedBytes(0x50);
        byte[] accountPublic = Ed25519PublicOf(accountSeed);
        var details = new AdvDetails
        {
            PairedUser = user,
            Device = device,
            RegistrationId = registrationId,
            AccountPublic = ByteString.CopyFrom(accountPublic),
        };
        byte[] detailsBytes = details.ToByteArray();
        byte[] hmac = HMACSHA256.HashData(row.AdvSecret, detailsBytes);
        byte[] accountSignature = Ed25519Sign(accountSeed, Join([6, 0], detailsBytes));
        byte[] deviceSignature = Ed25519Sign(row.SigningKeyPrivate, Join([6, 1], detailsBytes, accountPublic));
        var container = new PairSuccessContainer
        {
            Details = details,
            Hmac = ByteString.CopyFrom(hmac),
            AccountSignature = ByteString.CopyFrom(accountSignature),
            DeviceSignature = ByteString.CopyFrom(deviceSignature),
        };
        return container.ToByteArray();
    }

    internal static BinaryNode BuildRefReply(string id, string pairingRef) =>
        BinaryNode.FromChildren(
            "iq",
            [BinaryNode.Create("pair-device", new Dictionary<string, string> { ["ref"] = pairingRef })],
            new Dictionary<string, string> { ["id"] = id, ["type"] = "result" });

    internal static BinaryNode BuildSuccessNotification(string id, byte[] container) =>
        BinaryNode.FromChildren(
            "notification",
            [BinaryNode.FromBytes("pair-success", container)],
            new Dictionary<string, string> { ["id"] = id });

    /// <summary>
    /// Drives one full QR pairing attempt to verified success against the
    /// store's current row: pair-device request → ref reply → Code event →
    /// pair-success notification built from the live row → Success event, all
    /// through the production coordinator. Returns the Success event; throws
    /// (timeout) if any stage stalls.
    /// </summary>
    /// <param name="account">Account the attempt belongs to.</param>
    /// <param name="store">Credential store holding the (unpaired) row.</param>
    /// <param name="user">Paired user the success details declare.</param>
    /// <param name="device">Companion device index the success details declare.</param>
    /// <param name="registrationId">Registration id the success details declare.</param>
    /// <param name="cancellationToken">Test lifetime token.</param>
    /// <returns>The terminal Success event args.</returns>
    internal static async Task<PairingStateChangedEventArgs> DrivePairSuccessAsync(
        AccountId account,
        ICredentialStore store,
        string user,
        uint device,
        uint registrationId,
        CancellationToken cancellationToken)
    {
        var events = new ConcurrentQueue<PairingStateChangedEventArgs>();
        var sender = new RequestCapturingSender();
        var payloads = new HandshakePayloadProvider(account, store, DummyRegistrationDefaults());
        var pairing = new PairingCoordinator(account, payloads, store, sender.SendAsync, () => true);
        pairing.StateChanged += (_, e) => events.Enqueue(e);

        Task attempt = pairing.StartAsync(cancellationToken);

        BinaryNode request = await WaitForSentAsync(sender.Requests, cancellationToken).ConfigureAwait(false);
        pairing.OnStanzaReceived(null, BuildRefReply(request.Attrs["id"], "REF-1"));
        await WaitForEventAsync(events, e => e.Status == PairingStatus.Code, "pairing code", cancellationToken)
            .ConfigureAwait(false);

        CredentialRecord row = await store.GetAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Pair-success drive found no credential row.");
        byte[] container = BuildSuccessContainer(row, user, device, registrationId);
        pairing.OnStanzaReceived(null, BuildSuccessNotification("n-1", container));
        PairingStateChangedEventArgs success = await WaitForEventAsync(
            events, e => e.Status == PairingStatus.Success, "verified pair-success", cancellationToken)
            .ConfigureAwait(false);

        await attempt.WaitAsync(cancellationToken).ConfigureAwait(false);
        return success;
    }

    private static bool HasChild(BinaryNode node, string tag) =>
        node.Children is not null && node.Children.Any(child => string.Equals(child.Tag, tag, StringComparison.Ordinal));

    private static async Task<BinaryNode> WaitForSentAsync(
        ConcurrentQueue<BinaryNode> sent,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        while (!linked.IsCancellationRequested)
        {
            if (sent.TryDequeue(out BinaryNode? node) && node is not null)
            {
                return node;
            }

            await Task.Delay(10, linked.Token).ConfigureAwait(false);
        }

        throw new TimeoutException("Timed out waiting for the coordinator's pair-device request.");
    }

    private static async Task<PairingStateChangedEventArgs> WaitForEventAsync(
        ConcurrentQueue<PairingStateChangedEventArgs> events,
        Func<PairingStateChangedEventArgs, bool> predicate,
        string what,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        while (!linked.IsCancellationRequested)
        {
            foreach (PairingStateChangedEventArgs e in events)
            {
                if (predicate(e))
                {
                    return e;
                }
            }

            await Task.Delay(10, linked.Token).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for pairing event: {what}.");
    }

    private static byte[] FixedBytes(int start, int length = 32)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(start + i);
        }

        return bytes;
    }

    private static byte[] FixedSignature()
    {
        byte[] bytes = new byte[64];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(0xA0 + i);
        }

        return bytes;
    }

    private static byte[] Ed25519PublicOf(byte[] seed)
    {
        byte[] copy = (byte[])seed.Clone();
        try
        {
            using Key key = Key.Import(SignatureAlgorithm.Ed25519, copy, KeyBlobFormat.RawPrivateKey);
            return key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    private static byte[] Ed25519Sign(byte[] seed, byte[] data)
    {
        byte[] copy = (byte[])seed.Clone();
        try
        {
            using Key key = Key.Import(SignatureAlgorithm.Ed25519, copy, KeyBlobFormat.RawPrivateKey);
            return SignatureAlgorithm.Ed25519.Sign(key, data);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    private static byte[] Join(params byte[][] segments)
    {
        int total = 0;
        foreach (byte[] segment in segments)
        {
            total += segment.Length;
        }

        byte[] result = new byte[total];
        int offset = 0;
        foreach (byte[] segment in segments)
        {
            segment.CopyTo(result.AsSpan(offset));
            offset += segment.Length;
        }

        return result;
    }

    /// <summary>
    /// Minimal scripted sender: captures the pair-device request the drive
    /// replies to; every other outbound node (the success ack) completes
    /// synchronously so the coordinator's ordering is unaffected.
    /// </summary>
    private sealed class RequestCapturingSender
    {
        internal readonly ConcurrentQueue<BinaryNode> Requests = new();

        internal ValueTask SendAsync(BinaryNode node, CancellationToken cancellationToken)
        {
            if (HasChild(node, "pair-device"))
            {
                Requests.Enqueue(node);
            }

            return ValueTask.CompletedTask;
        }
    }
}
