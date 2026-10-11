using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Iwasug.WhatsApp.Sdk.Errors;

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

        if (_receivedAny)
        {
            return new SignalDecryptOutcome.Failed(SignalDecryptFailure.DuplicatePreKey);
        }

        // Fresh session's first delivery: the inner wire rides the base
        // ratchet at counter zero and replays through the regular path
        // (same chain, MAC verified under the initial chain key).
        SignalDecryptFailure? innerSplit = SignalCipher.TrySplitEnvelope(
            content.InnerMessage, out _, out byte[]? innerBody, out _);
        if (innerSplit.HasValue || innerBody is null)
        {
            return new SignalDecryptOutcome.Failed(innerSplit ?? SignalDecryptFailure.MalformedMessage);
        }

        SignalMessage? inner = SignalCipher.TryParseWhisperBody(innerBody);
        if (inner is null
            || !CryptographicOperations.FixedTimeEquals(inner.RatchetKey.ToByteArray(), _peerRatchetPublic))
        {
            return new SignalDecryptOutcome.Failed(SignalDecryptFailure.MalformedMessage);
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
}
