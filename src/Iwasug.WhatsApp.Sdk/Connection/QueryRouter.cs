using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Iwasug.WhatsApp.Sdk.Codec;
using Iwasug.WhatsApp.Sdk.Errors;

namespace Iwasug.WhatsApp.Sdk.Connection;

/// <summary>
/// Correlated query/reply router: allocates a never-reused id per query, hands the
/// framed bytes to the connection's sender, and completes the awaiting caller when the
/// reply with the same id arrives — or fails it on timeout, drop, closing, or
/// disposal. Exactly one terminal outcome per query: reply, timeout, or connection-lost
/// (the 408-lesson from production operators — no silent loss in the disconnect window).
/// </summary>
/// <remarks>
/// <para>
/// Payload-agnostic by design (02-SPIKE-NOTES.md F4 fallback): the router correlates by
/// id and enforces timeouts — it never parses node semantics. Correlation rides the
/// binary-node <c>id</c> attribute: <see cref="BuildFrame"/> encodes one node carrying
/// the opaque body with the allocated id stamped in its attributes, and
/// <see cref="TryParseFrame"/> matches inbound nodes on that attribute (both internal
/// so the loopback peer and the connection pump share the single source of truth).
/// This migration landed in Phase 3 (plan 03-01) as a planned deviation: live pairing
/// is the real node traffic the Phase-2 skeleton envelope
/// (<c>idLength(u8) || id(ASCII) || body</c>) was waiting for, so the pump, the
/// loopback peer, and this router moved together. Tag dispatch
/// (<c>success</c>/<c>failure</c>/<c>iq</c>/<c>notification</c>) lives in the
/// connection pump, never here.
/// </para>
/// <para>
/// Ids are <c>q-{n}</c> from a 64-bit monotonic counter that never resets while the
/// router lives (Pitfall 5: no wrap, no reuse while a reply window could be open). The
/// router never touches the wire directly: the <c>sender</c> delegate (owned by the
/// connection, reading its current channel) performs the send, so reconnects that swap
/// transports never disturb pending correlation state. Timeouts run on the injected
/// <see cref="TimeProvider"/> (FakeTimeProvider in unit tests, 02-04 scope).
/// </para>
/// <para>
/// This class does not log — the connection owns every log site (single logging owner,
/// so message discipline is reviewable in one file). Late replies for already-completed
/// queries surface as <c>false</c> from <see cref="TryDeliver"/> and the pump logs the
/// discard.
/// </para>
/// </remarks>
public sealed class QueryRouter : IAsyncDisposable
{
    /// <summary>Maximum encodable query-id length in bytes (ids are short counters).</summary>
    internal const int MaxIdBytes = 64;

    /// <summary>Node tag for outbound queries and their replies (iq stanza shape).</summary>
    internal const string QueryTag = "iq";

    /// <summary>Node attribute carrying the correlation id.</summary>
    internal const string IdAttribute = "id";

    private readonly Func<string, ReadOnlyMemory<byte>, CancellationToken, ValueTask> _sender;
    private readonly ConnectionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<byte[]>> _pending = new();
    private long _counter;
    private int _disposed;
    private int _closing;

    /// <summary>
    /// Creates a router over <paramref name="sender"/>, which transmits one framed query
    /// for a given id (the connection supplies the closure over its current channel).
    /// </summary>
    /// <param name="sender">Sends the framed payload for an allocated id.</param>
    /// <param name="options">Query timeout bounds; null resolves to defaults.</param>
    /// <param name="timeProvider">Clock for timeouts; defaults to system time.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sender"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> is out of range.</exception>
    public QueryRouter(
        Func<string, ReadOnlyMemory<byte>, CancellationToken, ValueTask> sender,
        ConnectionOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _options = ConnectionOptions.ValidateOptions(options);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Sends one query and awaits its correlated reply: allocates a fresh id (never
    /// reused while this router lives), registers the waiter, transmits via the sender,
    /// and completes on reply, timeout, connection drop, or caller cancellation — exactly
    /// once, in that precedence.
    /// </summary>
    /// <param name="payload">Opaque query body (framed with the id by the router).</param>
    /// <param name="cancellationToken">Caller cancellation (unwrapped, never retried).</param>
    /// <returns>The reply body (without the correlation envelope).</returns>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    /// <exception cref="SdkConnectionException">
    /// The query timed out, the connection dropped mid-query, the send path has no open
    /// channel, or the router is disposed. Messages name the outcome only.
    /// </exception>
    public async ValueTask<byte[]> SendQueryAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (Volatile.Read(ref _closing) == 1)
        {
            throw new SdkConnectionException("Query was not sent because the connection is closing.");
        }

        long sequence = Interlocked.Increment(ref _counter);
        string id = string.Create(CultureInfo.InvariantCulture, $"q-{sequence}");
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("Query id was already registered.");
        }

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = Task.Delay(TimeSpan.FromMilliseconds(_options.QueryTimeoutMs), _timeProvider, attemptCts.Token);
        try
        {
            await _sender(id, payload, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            attemptCts.Cancel();
            throw;
        }

        Task winner = await Task.WhenAny(completion.Task, timeout).ConfigureAwait(false);
        if (!ReferenceEquals(winner, completion.Task))
        {
            _pending.TryRemove(id, out _);
            attemptCts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new SdkConnectionException("Query timed out before a reply arrived.");
        }

        attemptCts.Cancel();
        return await completion.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Completes the waiter for <paramref name="id"/> (pump path). Returns false when no
    /// waiter exists — a late reply after timeout/drop, which the caller discards with a
    /// debug log (Pitfall 5).
    /// </summary>
    /// <param name="id">Correlation id from the reply envelope.</param>
    /// <param name="payload">Reply body.</param>
    /// <returns>True when a waiter consumed the reply.</returns>
    internal bool TryDeliver(string id, byte[] payload)
    {
        if (_pending.TryRemove(id, out TaskCompletionSource<byte[]>? completion))
        {
            completion.TrySetResult(payload);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Marks the connection as closing (drop path; the 02-05 pump is the trigger):
    /// fails every pending query as connection-lost and rejects every later send
    /// fail-fast with a typed exception, so no query hangs in the disconnect window.
    /// Idempotent: repeated calls keep the first outcome.
    /// </summary>
    internal void NotifyConnectionClosing()
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
        {
            return;
        }

        FailAllPending(new SdkConnectionException("Query did not complete: the connection is closing."));
    }

    /// <summary>
    /// Fails every pending query with <paramref name="error"/> (drop/dispose path, exact-
    /// pair removal so a concurrently delivered reply still wins its own waiter).
    /// </summary>
    /// <param name="error">The failure every waiter observes.</param>
    internal void FailAllPending(Exception error)
    {
        foreach (KeyValuePair<string, TaskCompletionSource<byte[]>> entry in _pending.ToArray())
        {
            if (_pending.TryRemove(entry.Key, out TaskCompletionSource<byte[]>? completion))
            {
                completion.TrySetException(error);
            }
        }
    }

    /// <summary>
    /// Frames one query for the wire as a binary node: the opaque body as byte content
    /// with the correlation id stamped in the <c>id</c> attribute. Shared with the
    /// loopback peer — the single source of truth.
    /// </summary>
    /// <param name="id">Correlation id (short ASCII).</param>
    /// <param name="body">Opaque query body.</param>
    /// <returns>The encoded node bytes.</returns>
    internal static byte[] BuildFrame(string id, ReadOnlyMemory<byte> body)
    {
        if (string.IsNullOrEmpty(id) || Encoding.ASCII.GetByteCount(id) > MaxIdBytes)
        {
            throw new InvalidOperationException("Query id is outside the encodable range.");
        }

        BinaryNode node = BinaryNode.FromBytes(
            QueryTag,
            body.Span,
            new Dictionary<string, string>(StringComparer.Ordinal) { [IdAttribute] = id });
        return BinaryNodeCodec.Encode(node);
    }

    /// <summary>
    /// Parses node-attribute correlation, bounds-checked (untrusted input): decodes the
    /// node, requires a non-empty <c>id</c> attribute, and returns the content bytes —
    /// byte content when present, UTF-8 text content next, otherwise the re-encoded node
    /// (a children-carrying reply keeps its bytes observable to the waiter).
    /// </summary>
    /// <param name="frame">Candidate frame bytes.</param>
    /// <param name="id">The correlation id, when the frame is well-formed.</param>
    /// <param name="body">The body bytes, when well-formed.</param>
    /// <returns>True when the frame is a well-formed correlated node.</returns>
    internal static bool TryParseFrame(byte[] frame, out string? id, out byte[]? body)
    {
        id = null;
        body = null;
        if (!BinaryNodeCodec.TryDecode(frame, out BinaryNode? node) || node is null)
        {
            return false;
        }

        if (!node.Attrs.TryGetValue(IdAttribute, out id) || string.IsNullOrEmpty(id))
        {
            id = null;
            return false;
        }

        body = node.Data
            ?? (node.Text is not null ? Encoding.UTF8.GetBytes(node.Text) : BinaryNodeCodec.Encode(node));
        return true;
    }

    /// <summary>
    /// Fails every pending query (drop-surfaced, typed) and releases the router.
    /// Disposing twice is a no-op.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        FailAllPending(new SdkConnectionException("Query router is disposed."));
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new ObjectDisposedException(nameof(QueryRouter));
        }
    }
}
