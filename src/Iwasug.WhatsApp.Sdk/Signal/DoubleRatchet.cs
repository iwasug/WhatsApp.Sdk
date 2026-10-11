using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.Runtime;

namespace Iwasug.WhatsApp.Sdk.Signal;

/// <summary>Typed outcome of one ratchet encrypt (the stanza layer maps the case to the enc node type).</summary>
internal abstract record SignalEncryptOutcome
{
    /// <summary>A regular message wire form (enc type <c>msg</c>).</summary>
    internal sealed record MessageSent(byte[] WireBytes) : SignalEncryptOutcome;

    /// <summary>A first-contact prekey wire form (enc type <c>pkmsg</c>).</summary>
    internal sealed record PreKeyMessageSent(byte[] WireBytes) : SignalEncryptOutcome;
}

/// <summary>
/// Per-address mutual-exclusion gate for ratchet mutation (04-01): all
/// state-advancing work on one session address serializes behind its gate,
/// so concurrent decrypts cannot interleave chain steps. Process-local only
/// (Baileys <c>PreKeyManager</c> per-keytype queue precedent, read-only at
/// commit 0af2386); durability across restarts is the store's job.
/// </summary>
internal static class SignalSessionGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    /// <summary>Acquires the exclusive gate for <paramref name="address"/> (async, cancellation-safe).</summary>
    /// <exception cref="SdkFormatException"><paramref name="address"/> is empty.</exception>
    internal static async ValueTask<IDisposable> AcquireAsync(string address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.Length == 0)
        {
            throw new SdkFormatException("Signal session addresses must not be empty.");
        }

        SemaphoreSlim gate = Gates.GetOrAdd(address, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}

/// <summary>
/// Double Ratchet session (04-01, internal): symmetric-key ratcheting with
/// header-carrying <c>SignalMessage</c> wire forms, DH ratcheting on peer
/// key change, and a bounded skipped-key map — encrypt/decrypt through
/// <see cref="SignalCipher"/>, key agreement through <see cref="X3dh"/>.
/// </summary>
/// <remarks>
/// <para>
/// Construction is factory-only: <see cref="InitializeAlice"/> (from an
/// <see cref="AliceSession"/> plus bundle coordinates) or
/// <see cref="InitializeBob"/> (from a <see cref="BobSession"/> plus
/// Alice's advertised base key). Alice's base keypair doubles as her first
/// sending ratchet keypair (public Signal construction); Bob mints his
/// sending chain on his first send.
/// </para>
/// <para>
/// Exactly-once: skipped keys are removed on use and in-order counters only
/// advance, so re-decrypting a consumed counter is
/// <see cref="SignalDecryptFailure.OldCounter"/> and replaying an
/// established base key is
/// <see cref="SignalDecryptFailure.DuplicatePreKey"/> — never a silent
/// double-apply. The skipped map is fail-closed: staging beyond
/// <see cref="MaxSkippedMessageKeys"/> returns
/// <see cref="SignalDecryptFailure.SkippedKeyLimit"/> with state untouched,
/// and the next in-order message still decrypts afterwards.
/// </para>
/// <para>
/// Cancellation and atomicity: <see cref="Encrypt"/>/<see cref="Decrypt"/>
/// check the token before any mutation and run synchronously to completion,
/// so a cancelled call never advances state (persistence of the advanced
/// state is the Task-2 repository's job, always after the mutation).
/// No logging on this path (T-4-01).
/// </para>
/// </remarks>
internal sealed class DoubleRatchet
{
    /// <summary>Hard per-session cap on staged skipped keys (SDK-chosen bound; fail-closed, never silently evicted).</summary>
    internal const int MaxSkippedMessageKeys = 2000;

    /// <summary>Session-structure version written by Task-2 serialization.</summary>
    internal const uint SessionVersion = 1;

    /// <summary>HKDF info label for the root-key ratchet step (SDK-pinned).</summary>
    internal const string RatchetInfoLabel = "WhisperRatchet";

    private byte[] _rootKey;
    private byte[]? _sendingChainKey;
    private uint _ns;
    private byte[]? _receivingChainKey;
    private uint _nr;
    private byte[] _ownRatchetPrivate;
    private byte[] _ownRatchetPublic;
    private byte[] _peerRatchetPublic;
    private uint _pn;
    private readonly List<SkippedKey> _skipped = new();
    private readonly byte[] _aliceBaseKey;
    private readonly byte[] _localIdentityDjb;
    private readonly byte[] _remoteIdentityDjb;
    private readonly uint _localRegistrationId;
    private readonly uint _remoteRegistrationId;
    private bool _needsPreKeySend;
    private bool _receivedAny;
    private readonly uint _peerSignedPreKeyId;
    private readonly uint? _peerOneTimePreKeyId;

    private DoubleRatchet(
        byte[] rootKey,
        byte[]? sendingChainKey,
        byte[]? receivingChainKey,
        byte[] ownRatchetPrivate,
        byte[] ownRatchetPublic,
        byte[] peerRatchetPublic,
        byte[] aliceBaseKey,
        byte[] localIdentityDjb,
        byte[] remoteIdentityDjb,
        uint localRegistrationId,
        uint remoteRegistrationId,
        bool needsPreKeySend,
        uint peerSignedPreKeyId,
        uint? peerOneTimePreKeyId)
    {
        _rootKey = rootKey;
        _sendingChainKey = sendingChainKey;
        _receivingChainKey = receivingChainKey;
        _ownRatchetPrivate = ownRatchetPrivate;
        _ownRatchetPublic = ownRatchetPublic;
        _peerRatchetPublic = peerRatchetPublic;
        _aliceBaseKey = aliceBaseKey;
        _localIdentityDjb = localIdentityDjb;
        _remoteIdentityDjb = remoteIdentityDjb;
        _localRegistrationId = localRegistrationId;
        _remoteRegistrationId = remoteRegistrationId;
        _needsPreKeySend = needsPreKeySend;
        _peerSignedPreKeyId = peerSignedPreKeyId;
        _peerOneTimePreKeyId = peerOneTimePreKeyId;
    }

    /// <summary>
    /// Alice side: the X3DH root plus the first sending chain; her ephemeral
    /// base keypair doubles as the first sending ratchet keypair.
    /// </summary>
    /// <param name="session">X3DH output (root, first chain, base public).</param>
    /// <param name="basePrivate">The ephemeral private behind <c>session.BasePublic</c>. Never logged.</param>
    /// <param name="localIdentityDjb">Our identity (33-byte DJB).</param>
    /// <param name="remoteIdentityDjb">Peer identity (33-byte DJB).</param>
    /// <param name="bundle">The bundle the session was built from (coordinates for the first prekey message).</param>
    /// <param name="localRegistrationId">Our 14-bit registration id (the credential row owns it; carried on the first prekey message).</param>
    internal static DoubleRatchet InitializeAlice(
        AliceSession session,
        byte[] basePrivate,
        byte[] localIdentityDjb,
        byte[] remoteIdentityDjb,
        PeerBundle bundle,
        uint localRegistrationId)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(basePrivate);
        ArgumentNullException.ThrowIfNull(localIdentityDjb);
        ArgumentNullException.ThrowIfNull(remoteIdentityDjb);
        ArgumentNullException.ThrowIfNull(bundle);
        if (basePrivate.Length != X3dh.KeyLength)
        {
            throw new SdkFormatException("Signal private keys must be 32 bytes.");
        }

        if (localRegistrationId > X3dh.MaxRegistrationId)
        {
            throw new SdkFormatException("Signal registration ids must fit 14 bits.");
        }

        if (localIdentityDjb.Length != SignalCipher.DjbIdentityLength
            || remoteIdentityDjb.Length != SignalCipher.DjbIdentityLength)
        {
            throw new SdkFormatException("Signal identity keys must be 33-byte DJB-encoded values.");
        }

        return new DoubleRatchet(
            (byte[])session.RootKey.Clone(),
            (byte[])session.ChainKey.Clone(),
            null,
            (byte[])basePrivate.Clone(),
            (byte[])session.BasePublic.Clone(),
            (byte[])bundle.SignedPreKey.Clone(),
            (byte[])session.BasePublic.Clone(),
            (byte[])localIdentityDjb.Clone(),
            (byte[])remoteIdentityDjb.Clone(),
            localRegistrationId,
            bundle.RegistrationId,
            needsPreKeySend: true,
            bundle.SignedPreKeyId,
            bundle.OneTimePreKeyId);
    }

    /// <summary>
    /// Bob side: the X3DH root plus the first receiving chain; the sending
    /// chain is minted on Bob's first send (standard lazy DH ratchet).
    /// </summary>
    /// <param name="session">X3DH output (root, first chain).</param>
    /// <param name="localIdentityDjb">Our identity (33-byte DJB).</param>
    /// <param name="remoteIdentityDjb">Alice's identity (33-byte DJB).</param>
    /// <param name="aliceBaseKey">Alice's advertised base public (replay-dedup pin).</param>
    /// <param name="localRegistrationId">Our 14-bit registration id.</param>
    /// <param name="remoteRegistrationId">Alice's 14-bit registration id.</param>
    internal static DoubleRatchet InitializeBob(
        BobSession session,
        byte[] localIdentityDjb,
        byte[] remoteIdentityDjb,
        byte[] aliceBaseKey,
        uint localRegistrationId,
        uint remoteRegistrationId)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(localIdentityDjb);
        ArgumentNullException.ThrowIfNull(remoteIdentityDjb);
        ArgumentNullException.ThrowIfNull(aliceBaseKey);
        if (localIdentityDjb.Length != SignalCipher.DjbIdentityLength
            || remoteIdentityDjb.Length != SignalCipher.DjbIdentityLength)
        {
            throw new SdkFormatException("Signal identity keys must be 33-byte DJB-encoded values.");
        }

        if (aliceBaseKey.Length != X3dh.KeyLength)
        {
            throw new SdkFormatException("Signal base keys must be 32 bytes.");
        }

        (byte[] priv, byte[] pub) = X3dh.GenerateX25519KeyPair();
        return new DoubleRatchet(
            (byte[])session.RootKey.Clone(),
            null,
            (byte[])session.ChainKey.Clone(),
            priv,
            pub,
            (byte[])aliceBaseKey.Clone(),
            (byte[])aliceBaseKey.Clone(),
            (byte[])localIdentityDjb.Clone(),
            (byte[])remoteIdentityDjb.Clone(),
            localRegistrationId,
            remoteRegistrationId,
            needsPreKeySend: false,
            peerSignedPreKeyId: 0,
            peerOneTimePreKeyId: null);
    }

    /// <summary>The established base key (replay-dedup pin; fresh copy).</summary>
    internal byte[] AliceBaseKey => (byte[])_aliceBaseKey.Clone();

    /// <summary>Staged skipped-key count (diagnostics surface; no key material).</summary>
    internal int SkippedKeyCount => _skipped.Count;

    /// <summary>Whether any message has been successfully decrypted (fresh-session prekey dedup).</summary>
    internal bool ReceivedAny => _receivedAny;

    /// <summary>
    /// Encrypts one message under the current sending chain (minting Bob's
    /// sending chain on first use). The first Alice encrypt emits the
    /// prekey form; everything after is the regular form.
    /// </summary>
    /// <param name="plaintext">Message bytes; must not be empty.</param>
    /// <param name="cancellationToken">Observed before any mutation; a cancelled call leaves state untouched.</param>
    /// <exception cref="SdkFormatException">The plaintext is empty.</exception>
    /// <exception cref="OperationCanceledException">The token was already cancelled (state untouched).</exception>
    internal SignalEncryptOutcome Encrypt(byte[] plaintext, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(plaintext);
        if (plaintext.Length == 0)
        {
            throw new SdkFormatException("Signal plaintext must not be empty.");
        }

        if (_sendingChainKey is null)
        {
            MintSendingChain();
        }

        (byte[] messageKey, byte[] nextChain) = KdfCk(_sendingChainKey!);
        CryptographicOperations.ZeroMemory(_sendingChainKey!);
        _sendingChainKey = nextChain;
        uint counter = _ns;
        _ns++;

        (byte[] aes, byte[] hmac, byte[] iv) = SignalCipher.ExpandMessageKey(messageKey);
        CryptographicOperations.ZeroMemory(messageKey);
        try
        {
            byte[] wire = SignalCipher.EncryptWhisperMessage(
                aes, hmac, iv, plaintext,
                _localIdentityDjb, _remoteIdentityDjb,
                _ownRatchetPublic, counter, _pn);
            if (!_needsPreKeySend)
            {
                return new SignalEncryptOutcome.MessageSent(wire);
            }

            byte[] preKeyWire = SignalCipher.EncryptPreKeyMessage(
                wire,
                _aliceBaseKey,
                _localIdentityDjb,
                _localRegistrationId,
                _peerSignedPreKeyId,
                _peerOneTimePreKeyId);
            _needsPreKeySend = false;
            return new SignalEncryptOutcome.PreKeyMessageSent(preKeyWire);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aes);
            CryptographicOperations.ZeroMemory(hmac);
            CryptographicOperations.ZeroMemory(iv);
        }
    }

    /// <summary>
    /// Decrypts one wire message: prekey form is base-key deduped (a fresh
    /// session's first delivery decrypts the inner message; any later
    /// delivery with the same base is a replay); regular form advances the
    /// receiving chain, staging skipped keys for gaps.
    /// </summary>
    /// <param name="wire">Untrusted peer wire bytes.</param>
    /// <param name="isPreKey">True for enc type <c>pkmsg</c>, false for <c>msg</c> (the stanza layer supplies it).</param>
    /// <param name="cancellationToken">Observed before any mutation; a cancelled call leaves state untouched.</param>
    /// <exception cref="OperationCanceledException">The token was already cancelled (state untouched).</exception>
    internal SignalDecryptOutcome Decrypt(byte[] wire, bool isPreKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(wire);

        SignalDecryptOutcome outcome = isPreKey ? DecryptPreKey(wire) : DecryptRegular(wire);
        if (outcome is SignalDecryptOutcome.Decrypted)
        {
            _receivedAny = true;
        }

        return outcome;
    }

    private SignalDecryptOutcome DecryptPreKey(byte[] wire)
    {
        PreKeyEnvelopeOutcome envelope = SignalCipher.DecryptPreKeyEnvelope(wire);
        if (envelope is PreKeyEnvelopeOutcome.Failed failed)
        {
            return new SignalDecryptOutcome.Failed(failed.Reason);
        }

        PreKeyWireContent content = ((PreKeyEnvelopeOutcome.Parsed)envelope).Content;
        if (!CryptographicOperations.FixedTimeEquals(content.BaseKey, _aliceBaseKey))
        {
            return new SignalDecryptOutcome.Failed(SignalDecryptFailure.UnexpectedPreKey);
        }

        // The inner wire replays through the regular path, so a first message
        // arriving after later ones still decrypts (its key was staged as a
        // skipped key under the base ratchet). Only a stale first message for
        // a session that already ratcheted past the base - no staged key and
        // a moved-on peer ratchet - reports a duplicate.
        SignalDecryptFailure? innerSplit = SignalCipher.TrySplitEnvelope(
            content.InnerMessage, out _, out byte[]? innerBody, out _);
        if (innerSplit.HasValue || innerBody is null)
        {
            return new SignalDecryptOutcome.Failed(innerSplit ?? SignalDecryptFailure.MalformedMessage);
        }

        SignalMessage? inner = SignalCipher.TryParseWhisperBody(innerBody);
        if (inner is null)
        {
            return new SignalDecryptOutcome.Failed(SignalDecryptFailure.MalformedMessage);
        }

        byte[] innerRatchet = inner.RatchetKey.ToByteArray();
        if (!CryptographicOperations.FixedTimeEquals(innerRatchet, _peerRatchetPublic)
            && FindSkipped(innerRatchet, inner.Counter) < 0)
        {
            return new SignalDecryptOutcome.Failed(SignalDecryptFailure.DuplicatePreKey);
        }

        return DecryptRegular(content.InnerMessage);
    }

    private SignalDecryptOutcome DecryptRegular(byte[] wire)
    {
        SignalDecryptFailure? split = SignalCipher.TrySplitEnvelope(
            wire, out byte version, out byte[]? body, out byte[]? receivedMac);
        if (split.HasValue || body is null || receivedMac is null)
        {
            return new SignalDecryptOutcome.Failed(split ?? SignalDecryptFailure.MalformedMessage);
        }

        SignalMessage? header = SignalCipher.TryParseWhisperBody(body);
        if (header is null)
        {
            return new SignalDecryptOutcome.Failed(SignalDecryptFailure.MalformedMessage);
        }

        byte[] headerKey = header.RatchetKey.ToByteArray();

        // Skipped-key hit: verify first, consume only on success, so a
        // forged message can never burn the staged key of the real one.
        int hit = FindSkipped(headerKey, header.Counter);
        if (hit >= 0)
        {
            SkippedKey staged = _skipped[hit];
            SignalDecryptOutcome stagedOutcome = DecryptWithKey(version, body, receivedMac, header, (byte[])staged.MessageKey.Clone());
            if (stagedOutcome is SignalDecryptOutcome.Decrypted)
            {
                CryptographicOperations.ZeroMemory(staged.MessageKey);
                _skipped.RemoveAt(hit);
            }

            return stagedOutcome;
        }

        if (CryptographicOperations.FixedTimeEquals(headerKey, _peerRatchetPublic))
        {
            return DecryptStaged(version, body, receivedMac, header);
        }

        return DecryptNewChain(version, body, receivedMac, header, headerKey);
    }

    /// <summary>
    /// Same-chain path: derives the target key plus gap stagings into locals,
    /// verifies and decrypts FIRST, and commits staged keys and counters only
    /// on success — a forged message leaves every counter untouched, so the
    /// real message still decrypts afterwards. Cap breach fails before any
    /// derivation (fail-closed).
    /// </summary>
    private SignalDecryptOutcome DecryptStaged(byte version, byte[] body, byte[] receivedMac, SignalMessage header)
    {
        if (_receivingChainKey is null)
        {
            return new SignalDecryptOutcome.Failed(SignalDecryptFailure.MalformedMessage);
        }

        if (header.Counter < _nr)
        {
            return new SignalDecryptOutcome.Failed(SignalDecryptFailure.OldCounter);
        }

        ulong needed = (ulong)header.Counter - _nr;
        if ((ulong)_skipped.Count + needed > (ulong)MaxSkippedMessageKeys)
        {
            return new SignalDecryptOutcome.Failed(SignalDecryptFailure.SkippedKeyLimit);
        }

        // Derive into locals: no field is written until the MAC verifies.
        byte[]? chain = (byte[])_receivingChainKey.Clone();
        var staged = new List<(uint Counter, byte[] Key)>();
        byte[]? messageKey = null;
        byte[]? nextChain = null;
        try
        {
            while ((ulong)_nr + (ulong)staged.Count < header.Counter)
            {
                (byte[] mk, byte[] next) = KdfCk(chain);
                CryptographicOperations.ZeroMemory(chain);
                chain = next;
                staged.Add((_nr + (uint)staged.Count, mk));
            }

            (messageKey, nextChain) = KdfCk(chain);
            CryptographicOperations.ZeroMemory(chain);
            chain = null;

            SignalDecryptOutcome outcome = DecryptWithKey(version, body, receivedMac, header, messageKey);
            messageKey = null;
            if (outcome is not SignalDecryptOutcome.Decrypted)
            {
                return outcome;
            }

            foreach ((uint counter, byte[] key) in staged)
            {
                _skipped.Add(new SkippedKey((byte[])_peerRatchetPublic.Clone(), counter, key));
            }

            staged.Clear();
            CryptographicOperations.ZeroMemory(_receivingChainKey);
            _receivingChainKey = nextChain;
            nextChain = null;
            _nr = header.Counter + 1;
            return outcome;
        }
        finally
        {
            if (chain is not null)
            {
                CryptographicOperations.ZeroMemory(chain);
            }

            if (messageKey is not null)
            {
                CryptographicOperations.ZeroMemory(messageKey);
            }

            if (nextChain is not null)
            {
                CryptographicOperations.ZeroMemory(nextChain);
            }

            foreach ((_, byte[] key) in staged)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    private SignalDecryptOutcome DecryptNewChain(
        byte version, byte[] body, byte[] receivedMac, SignalMessage header, byte[] headerKey)
    {
        // Stage the previous receiving chain for the sender's unacked span
        // (header.PreviousCounter bounds it); fail-closed on the cap.
        ulong previousSpan = _receivingChainKey is null || header.PreviousCounter < _nr
            ? 0
            : (ulong)header.PreviousCounter - _nr + 1;
        if ((ulong)_skipped.Count + previousSpan + header.Counter > (ulong)MaxSkippedMessageKeys)
        {
            return new SignalDecryptOutcome.Failed(SignalDecryptFailure.SkippedKeyLimit);
        }

        // Everything below derives into locals tracked for wiping; fields
        // commit only after the MAC verifies (DecryptStaged discipline).
        var owned = new List<byte[]>();
        void Own(byte[] buffer) => owned.Add(buffer);
        void Transfer(byte[] buffer) => owned.Remove(buffer);
        try
        {
            return DecryptNewChainCore(version, body, receivedMac, header, headerKey, owned, Own, Transfer);
        }
        finally
        {
            foreach (byte[] buffer in owned)
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }
    }

    private SignalDecryptOutcome DecryptNewChainCore(
        byte version,
        byte[] body,
        byte[] receivedMac,
        SignalMessage header,
        byte[] headerKey,
        List<byte[]> owned,
        Action<byte[]> own,
        Action<byte[]> transfer)
    {
        var stagedOld = new List<(uint Counter, byte[] Key)>();
        uint cursor = _nr;
        if (_receivingChainKey is not null)
        {
            byte[] chain = (byte[])_receivingChainKey.Clone();
            own(chain);
            while (cursor <= header.PreviousCounter)
            {
                (byte[] mk, byte[] next) = KdfCk(chain);
                CryptographicOperations.ZeroMemory(chain);
                chain = next;
                own(mk);
                stagedOld.Add((cursor, mk));
                cursor++;
            }

            CryptographicOperations.ZeroMemory(chain);
            owned.Remove(chain);
        }

        byte[] oldPeerRatchet = (byte[])_peerRatchetPublic.Clone();
        own(oldPeerRatchet);

        byte[] dh = X3dh.Agree(_ownRatchetPrivate, headerKey);
        own(dh);
        (byte[] root1, byte[] recvChain) = KdfRk(_rootKey, dh);
        own(root1);
        own(recvChain);
        CryptographicOperations.ZeroMemory(dh);
        owned.Remove(dh);

        (byte[] freshPriv, byte[] freshPub) = X3dh.GenerateX25519KeyPair();
        own(freshPriv);
        own(freshPub);

        byte[] dh2 = X3dh.Agree(freshPriv, headerKey);
        own(dh2);
        (byte[] root2, byte[] sendChain) = KdfRk(root1, dh2);
        own(root2);
        own(sendChain);
        CryptographicOperations.ZeroMemory(root1);
        owned.Remove(root1);
        CryptographicOperations.ZeroMemory(dh2);
        owned.Remove(dh2);

        var stagedNew = new List<(uint Counter, byte[] Key)>();
        byte[] recvCursor = (byte[])recvChain.Clone();
        own(recvCursor);
        uint newNr = 0;
        while (newNr < header.Counter)
        {
            (byte[] mk, byte[] next) = KdfCk(recvCursor);
            CryptographicOperations.ZeroMemory(recvCursor);
            recvCursor = next;
            own(mk);
            stagedNew.Add((newNr, mk));
            newNr++;
        }

        (byte[] targetKey, byte[] finalRecv) = KdfCk(recvCursor);
        own(targetKey);
        own(finalRecv);
        CryptographicOperations.ZeroMemory(recvCursor);
        owned.Remove(recvCursor);

        SignalDecryptOutcome outcome = DecryptWithKey(version, body, receivedMac, header, targetKey);
        if (outcome is not SignalDecryptOutcome.Decrypted)
        {
            return outcome;
        }

        foreach ((uint counter, byte[] key) in stagedOld)
        {
            _skipped.Add(new SkippedKey(oldPeerRatchet, counter, key));
            transfer(key);
        }

        byte[] newPeerRatchet = (byte[])headerKey.Clone();
        foreach ((uint counter, byte[] key) in stagedNew)
        {
            _skipped.Add(new SkippedKey(newPeerRatchet, counter, key));
            transfer(key);
        }

        transfer(oldPeerRatchet);
        transfer(newPeerRatchet);
        CryptographicOperations.ZeroMemory(_rootKey);
        _rootKey = root2;
        transfer(root2);
        if (_receivingChainKey is not null)
        {
            CryptographicOperations.ZeroMemory(_receivingChainKey);
        }

        _receivingChainKey = finalRecv;
        transfer(finalRecv);
        if (_sendingChainKey is not null)
        {
            CryptographicOperations.ZeroMemory(_sendingChainKey);
        }

        _sendingChainKey = sendChain;
        transfer(sendChain);
        CryptographicOperations.ZeroMemory(_ownRatchetPrivate);
        _ownRatchetPrivate = freshPriv;
        transfer(freshPriv);
        _ownRatchetPublic = freshPub;
        transfer(freshPub);
        _peerRatchetPublic = newPeerRatchet;
        _pn = _ns;
        _ns = 0;
        _nr = header.Counter + 1;
        return outcome;
    }

    private SignalDecryptOutcome DecryptWithKey(
        byte version, byte[] body, byte[] receivedMac, SignalMessage header, byte[] messageKey)
    {
        (byte[] aes, byte[] hmac, byte[] iv) = SignalCipher.ExpandMessageKey(messageKey);
        CryptographicOperations.ZeroMemory(messageKey);
        try
        {
            return SignalCipher.DecryptParsedMessage(
                version, body, receivedMac, header, aes, hmac, iv, _remoteIdentityDjb, _localIdentityDjb);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aes);
            CryptographicOperations.ZeroMemory(hmac);
            CryptographicOperations.ZeroMemory(iv);
        }
    }

    private void MintSendingChain()
    {
        // Bob's first send: DH-ratchet forward under a fresh keypair.
        (byte[] newPriv, byte[] newPub) = X3dh.GenerateX25519KeyPair();
        byte[] dh = X3dh.Agree(newPriv, _peerRatchetPublic);
        try
        {
            (byte[] root, byte[] sendChain) = KdfRk(_rootKey, dh);
            CryptographicOperations.ZeroMemory(_rootKey);
            _rootKey = root;
            _sendingChainKey = sendChain;
            _ns = 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dh);
        }

        CryptographicOperations.ZeroMemory(_ownRatchetPrivate);
        _ownRatchetPrivate = newPriv;
        _ownRatchetPublic = newPub;
    }

    /// <summary>KDF_CK: message key = HMAC(ck, 0x01); next chain = HMAC(ck, 0x02) (public Double Ratchet construction).</summary>
    /// <exception cref="SdkFormatException">The chain key is not 32 bytes.</exception>
    internal static (byte[] MessageKey, byte[] NextChainKey) KdfCk(ReadOnlySpan<byte> chainKey)
    {
        if (chainKey.Length != X3dh.KeyLength)
        {
            throw new SdkFormatException("Signal chain keys must be 32 bytes.");
        }

        byte[] key = chainKey.ToArray();
        byte[] mk = HMACSHA256.HashData(key, new byte[] { 0x01 });
        byte[] next = HMACSHA256.HashData(key, new byte[] { 0x02 });
        CryptographicOperations.ZeroMemory(key);
        return (mk, next);
    }

    /// <summary>KDF_RK: HKDF(salt = root, ikm = dh, info = WhisperRatchet) into new root ‖ new chain.</summary>
    /// <exception cref="SdkFormatException">The root key or DH output is not 32 bytes.</exception>
    internal static (byte[] RootKey, byte[] ChainKey) KdfRk(ReadOnlySpan<byte> rootKey, ReadOnlySpan<byte> dhOutput)
    {
        if (rootKey.Length != X3dh.KeyLength || dhOutput.Length != X3dh.KeyLength)
        {
            throw new SdkFormatException("Signal root keys and DH outputs must be 32 bytes.");
        }

        byte[] okm = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            dhOutput.ToArray(),
            2 * X3dh.KeyLength,
            rootKey.ToArray(),
            Encoding.ASCII.GetBytes(RatchetInfoLabel));
        try
        {
            byte[] root = new byte[X3dh.KeyLength];
            byte[] chain = new byte[X3dh.KeyLength];
            okm.AsSpan(0, X3dh.KeyLength).CopyTo(root);
            okm.AsSpan(X3dh.KeyLength, X3dh.KeyLength).CopyTo(chain);
            return (root, chain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(okm);
        }
    }

    private sealed record SkippedKey(byte[] RatchetKey, uint Counter, byte[] MessageKey);

    private int FindSkipped(byte[] ratchetKey, uint counter)
    {
        for (int i = 0; i < _skipped.Count; i++)
        {
            if (_skipped[i].Counter == counter && _skipped[i].RatchetKey.AsSpan().SequenceEqual(ratchetKey))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Serializes the full session state to SDK-owned <c>SessionStructure</c>
    /// bytes - the producer behind <c>SignalSessionRecord.SessionState</c>.
    /// Hosts persist these bytes opaquely and must not parse them.
    /// </summary>
    /// <remarks>
    /// SDK-owned encoding notes (not a public contract): the sender chain is
    /// always emitted (own ratchet keys are required to keep sending after a
    /// restart); its <c>chainKey</c> is absent exactly when no sending chain
    /// exists yet (Bob before his first send). The first receiver chain is
    /// the current receiving chain; a keyless first chain with no staged keys
    /// is the no-receiving marker (Alice before Bob's reply) recording the
    /// peer ratchet. Later keyless chains carry staged keys of exhausted
    /// ratchets. Staged <c>MessageKey.cipherKey</c> holds the raw 32-byte
    /// chain message key with <c>macKey</c>/<c>iv</c> empty (strict on load).
    /// <c>pendingPreKey</c> is emitted exactly when the first send must still
    /// use prekey form, carrying the bundle coordinates it must name;
    /// <c>needsRefresh</c> stays false: replays are rejected via the base-key
    /// pin and counters.
    /// </remarks>
    internal byte[] Serialize()
    {
        var structure = new SessionStructure
        {
            SessionVersion = SessionVersion,
            LocalIdentityPublic = ByteString.CopyFrom(_localIdentityDjb),
            RemoteIdentityPublic = ByteString.CopyFrom(_remoteIdentityDjb),
            RootKey = ByteString.CopyFrom(_rootKey),
            PreviousCounter = _pn,
            RemoteRegistrationId = _remoteRegistrationId,
            LocalRegistrationId = _localRegistrationId,
            NeedsRefresh = false,
            AliceBaseKey = ByteString.CopyFrom(_aliceBaseKey),
        };

        var senderChain = new SessionStructure.Types.Chain
        {
            SenderRatchetKey = ByteString.CopyFrom(_ownRatchetPublic),
            SenderRatchetKeyPrivate = ByteString.CopyFrom(_ownRatchetPrivate),
        };
        if (_sendingChainKey is not null)
        {
            senderChain.ChainKey = new SessionStructure.Types.Chain.Types.ChainKey
            {
                Index = _ns,
                Key = ByteString.CopyFrom(_sendingChainKey),
            };
        }

        structure.SenderChain = senderChain;

        var current = new SessionStructure.Types.Chain
        {
            SenderRatchetKey = ByteString.CopyFrom(_peerRatchetPublic),
        };
        if (_receivingChainKey is not null)
        {
            current.ChainKey = new SessionStructure.Types.Chain.Types.ChainKey
            {
                Index = _nr,
                Key = ByteString.CopyFrom(_receivingChainKey),
            };
        }

        foreach (SkippedKey staged in _skipped)
        {
            if (staged.RatchetKey.AsSpan().SequenceEqual(_peerRatchetPublic))
            {
                current.MessageKeys.Add(new SessionStructure.Types.Chain.Types.MessageKey
                {
                    Index = staged.Counter,
                    CipherKey = ByteString.CopyFrom(staged.MessageKey),
                });
            }
        }

        structure.ReceiverChains.Add(current);

        foreach (System.Linq.IGrouping<string, SkippedKey> group
            in _skipped
                .Where(s => !s.RatchetKey.AsSpan().SequenceEqual(_peerRatchetPublic))
                .GroupBy(s => Convert.ToHexString(s.RatchetKey)))
        {
            var exhausted = new SessionStructure.Types.Chain
            {
                SenderRatchetKey = ByteString.CopyFrom(group.First().RatchetKey),
            };
            foreach (SkippedKey staged in group)
            {
                exhausted.MessageKeys.Add(new SessionStructure.Types.Chain.Types.MessageKey
                {
                    Index = staged.Counter,
                    CipherKey = ByteString.CopyFrom(staged.MessageKey),
                });
            }

            structure.ReceiverChains.Add(exhausted);
        }

        if (_needsPreKeySend)
        {
            structure.PendingPreKey = new SessionStructure.Types.PendingPreKey
            {
                PreKeyId = _peerOneTimePreKeyId ?? 0,
                BaseKey = ByteString.CopyFrom(_aliceBaseKey),
                SignedPreKeyId = (int)_peerSignedPreKeyId,
            };
        }

        return structure.ToByteArray();
    }

    /// <summary>
    /// Rebuilds a session from <see cref="Serialize"/> bytes. Every length
    /// rule is validated before any state is constructed: a tampered blob
    /// throws a rule-only <see cref="SdkFormatException"/> and never yields
    /// a partial session.
    /// </summary>
    /// <exception cref="SdkFormatException">The bytes are not a well-formed v1 session.</exception>
    internal static DoubleRatchet Deserialize(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        SessionStructure structure;
        try
        {
            structure = SessionStructure.Parser.ParseFrom(bytes);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new SdkFormatException("Signal session bytes failed validation.", ex);
        }

        if (structure.SessionVersion != SessionVersion)
        {
            throw new SdkFormatException("Signal session bytes failed validation.");
        }

        byte[] localIdentity = structure.LocalIdentityPublic.ToByteArray();
        byte[] remoteIdentity = structure.RemoteIdentityPublic.ToByteArray();
        byte[] rootKey = structure.RootKey.ToByteArray();
        byte[] aliceBaseKey = structure.AliceBaseKey.ToByteArray();
        if (localIdentity.Length != SignalCipher.DjbIdentityLength
            || localIdentity[0] != SignalCipher.DjbTypeByte
            || remoteIdentity.Length != SignalCipher.DjbIdentityLength
            || remoteIdentity[0] != SignalCipher.DjbTypeByte
            || rootKey.Length != X3dh.KeyLength
            || aliceBaseKey.Length != X3dh.KeyLength)
        {
            throw new SdkFormatException("Signal session bytes failed validation.");
        }

        if (structure.RemoteRegistrationId > X3dh.MaxRegistrationId
            || structure.LocalRegistrationId > X3dh.MaxRegistrationId)
        {
            throw new SdkFormatException("Signal session bytes failed validation.");
        }

        if (structure.SenderChain is null
            || structure.SenderChain.SenderRatchetKey.Length != X3dh.KeyLength
            || structure.SenderChain.SenderRatchetKeyPrivate.Length != X3dh.KeyLength)
        {
            throw new SdkFormatException("Signal session bytes failed validation.");
        }

        byte[]? sendingChainKey = null;
        uint ns = 0;
        if (structure.SenderChain.ChainKey is not null)
        {
            if (structure.SenderChain.ChainKey.Key.Length != X3dh.KeyLength)
            {
                throw new SdkFormatException("Signal session bytes failed validation.");
            }

            sendingChainKey = structure.SenderChain.ChainKey.Key.ToByteArray();
            ns = structure.SenderChain.ChainKey.Index;
        }

        // PendingPreKey is the explicit first-send marker: present exactly
        // when the first send must still use prekey form, carrying the bundle
        // coordinates it must name. Anything else here is foreign bytes.
        bool needsPreKeySend = false;
        uint peerSignedPreKeyId = 0;
        uint? peerOneTimePreKeyId = null;
        if (structure.PendingPreKey is not null)
        {
            if (structure.PendingPreKey.SignedPreKeyId < 0
                || structure.PendingPreKey.PreKeyId == 0 && structure.PendingPreKey.SignedPreKeyId == 0)
            {
                throw new SdkFormatException("Signal session bytes failed validation.");
            }

            byte[] pendingBase = structure.PendingPreKey.BaseKey.ToByteArray();
            if (pendingBase.Length != X3dh.KeyLength
                || !pendingBase.AsSpan().SequenceEqual(aliceBaseKey))
            {
                throw new SdkFormatException("Signal session bytes failed validation.");
            }

            needsPreKeySend = true;
            peerSignedPreKeyId = (uint)structure.PendingPreKey.SignedPreKeyId;
            peerOneTimePreKeyId = structure.PendingPreKey.PreKeyId == 0
                ? null
                : structure.PendingPreKey.PreKeyId;
        }

        if (structure.NeedsRefresh)
        {
            throw new SdkFormatException("Signal session bytes failed validation.");
        }

        byte[]? receivingChainKey = null;
        uint nr = 0;
        byte[]? peerRatchetPublic = null;
        var skipped = new List<SkippedKey>();
        var seen = new HashSet<(string Ratchet, uint Counter)>();
        if (structure.ReceiverChains.Count == 0)
        {
            throw new SdkFormatException("Signal session bytes failed validation.");
        }

        for (int i = 0; i < structure.ReceiverChains.Count; i++)
        {
            SessionStructure.Types.Chain chain = structure.ReceiverChains[i];
            if (chain.SenderRatchetKey.Length != X3dh.KeyLength)
            {
                throw new SdkFormatException("Signal session bytes failed validation.");
            }

            if (chain.ChainKey is not null && chain.ChainKey.Key.Length == 0)
            {
                throw new SdkFormatException("Signal session bytes failed validation.");
            }

            byte[] ratchet = chain.SenderRatchetKey.ToByteArray();
            bool hasKey = chain.ChainKey is not null;
            if (hasKey)
            {
                if (i != 0)
                {
                    throw new SdkFormatException("Signal session bytes failed validation.");
                }

                if (chain.ChainKey!.Key.Length != X3dh.KeyLength)
                {
                    throw new SdkFormatException("Signal session bytes failed validation.");
                }

                peerRatchetPublic = ratchet;
                receivingChainKey = chain.ChainKey.Key.ToByteArray();
                nr = chain.ChainKey.Index;
            }
            else if (i == 0)
            {
                if (chain.MessageKeys.Count != 0 || structure.ReceiverChains.Count != 1)
                {
                    throw new SdkFormatException("Signal session bytes failed validation.");
                }

                peerRatchetPublic = ratchet;
            }

            foreach (SessionStructure.Types.Chain.Types.MessageKey staged in chain.MessageKeys)
            {
                if (staged.CipherKey.Length != X3dh.KeyLength
                    || staged.MacKey.Length != 0
                    || staged.Iv.Length != 0)
                {
                    throw new SdkFormatException("Signal session bytes failed validation.");
                }

                string fingerprint = Convert.ToHexString(ratchet);
                if (!seen.Add((fingerprint, staged.Index)))
                {
                    throw new SdkFormatException("Signal session bytes failed validation.");
                }

                skipped.Add(new SkippedKey(
                    (byte[])ratchet.Clone(), staged.Index, staged.CipherKey.ToByteArray()));
            }
        }

        if (peerRatchetPublic is null)
        {
            throw new SdkFormatException("Signal session bytes failed validation.");
        }

        if (skipped.Count > MaxSkippedMessageKeys)
        {
            throw new SdkFormatException("Signal session bytes failed validation.");
        }

        bool receivedAny = nr > 0 || skipped.Count > 0;

        var session = new DoubleRatchet(
            rootKey,
            sendingChainKey,
            receivingChainKey,
            structure.SenderChain.SenderRatchetKeyPrivate.ToByteArray(),
            structure.SenderChain.SenderRatchetKey.ToByteArray(),
            peerRatchetPublic,
            aliceBaseKey,
            localIdentity,
            remoteIdentity,
            structure.LocalRegistrationId,
            structure.RemoteRegistrationId,
            needsPreKeySend,
            peerSignedPreKeyId,
            peerOneTimePreKeyId);
        session._ns = ns;
        session._nr = nr;
        session._pn = structure.PreviousCounter;
        session._receivedAny = receivedAny;
        foreach (SkippedKey staged in skipped)
        {
            session._skipped.Add(staged);
        }

        return session;
    }

    /// <summary>
    /// Thin durable adapter over <see cref="ISignalSessionStore"/>: sessions are keyed
    /// by the full per-device AD JID, every state-advancing mutation persists
    /// through <see cref="DurableWriteRetryPolicy"/> before the protocol
    /// continues (D-06), and every mutation serializes behind the per-address
    /// gate. The repository holds no cached instances: each operation loads
    /// fresh bytes, so a persist failure can never leave a caller holding
    /// state newer than what a restart would reload.
    /// </summary>
    internal sealed class SignalSessionRepository
    {
        private readonly ISignalSessionStore _store;
        private readonly DurableWriteRetryPolicy _retry;

        /// <summary>Creates a repository over <paramref name="store"/> with <paramref name="retryPolicy"/> durability.</summary>
        internal SignalSessionRepository(ISignalSessionStore store, DurableWriteRetryPolicy retryPolicy)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _retry = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        }

        /// <summary>
        /// The store key for a peer session: the canonical full per-device AD
        /// JID (<see cref="Jid.ToString"/> including the device index) - never
        /// the bare user (04-RESEARCH Open Question 1 / Pitfall 5: Double
        /// Ratchet sessions are strictly per-device, so two devices of one
        /// user never collide). Keyed by the arrival JID; PN to LID aliasing
        /// is a mapping note for later phases, not a second key.
        /// </summary>
        internal static string AddressFor(Jid peer)
        {
            ArgumentNullException.ThrowIfNull(peer);
            return peer.ToString();
        }

        /// <summary>
        /// Loads the session for <paramref name="address"/>, or null when no
        /// session exists (missing is not failure). A tampered blob throws a
        /// rule-only <see cref="SdkFormatException"/> - never a partial session.
        /// </summary>
        internal async Task<DoubleRatchet?> LoadAsync(string address, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(address);
            using IDisposable loadLease = await SignalSessionGate.AcquireAsync(address, cancellationToken).ConfigureAwait(false);
            SignalSessionRecord? record = await _store.GetAsync(address, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                return null;
            }

            return DoubleRatchet.Deserialize(record.SessionState);
        }

        /// <summary>
        /// Persists <paramref name="session"/> under <paramref name="address"/>,
        /// awaiting durability before returning (D-06). On persist failure the
        /// durable bytes are untouched: callers must reload rather than keep
        /// using the unpersisted advance.
        /// </summary>
        internal async Task SaveAsync(string address, DoubleRatchet session, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(address);
            ArgumentNullException.ThrowIfNull(session);
            using IDisposable saveLease = await SignalSessionGate.AcquireAsync(address, cancellationToken).ConfigureAwait(false);
            byte[] state = session.Serialize();
            await _retry.ExecuteAsync<bool>(
                async ct =>
                {
                    await _store.PutAsync(address, new SignalSessionRecord(address, state), ct).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
    }
}
