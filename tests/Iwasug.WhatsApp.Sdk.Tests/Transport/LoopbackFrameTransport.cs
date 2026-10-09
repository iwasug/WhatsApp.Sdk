using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Transport;

namespace Iwasug.WhatsApp.Sdk.Tests.Transport;

/// <summary>
/// Tests-only in-memory <see cref="IFrameTransport"/> fake (Wave-0 harness for 02-02):
/// a linked pair of ends over two <see cref="Pipe"/>s — what end A sends, end B receives.
/// </summary>
/// <remarks>
/// <para>
/// No-statics + per-connection isolation (InMemoryStoreFactory precedent): both pipes
/// live as instance fields of the pair, created per <see cref="CreatePair"/> call. Two
/// pairs never observe each other's frames. Obtain ends only via
/// <see cref="CreatePair"/> — there is no shared default instance.
/// </para>
/// <para>
/// Harness-private framing: each frame travels as a 4-byte big-endian length followed by
/// the payload bytes. This framing belongs to the fake, NOT to the WhatsApp wire (the
/// wire uses a 3-byte big-endian prefix per 02-SPIKE-NOTES.md F3 — 02-04's FrameReader
/// owns that). Never assert wire bytes through this fake.
/// </para>
/// </remarks>
internal sealed class LoopbackFrameTransport : IFrameTransport
{
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly SharedClose _shared;
    private readonly bool _isLeft;
    private int _disposed;

    private LoopbackFrameTransport(PipeReader reader, PipeWriter writer, SharedClose shared, bool isLeft)
    {
        _reader = reader;
        _writer = writer;
        _shared = shared;
        _isLeft = isLeft;
    }

    /// <summary>
    /// Creates a linked pair of transport ends: everything the left end sends arrives at
    /// the right end's <see cref="ReceiveAsync"/>, and vice versa.
    /// </summary>
    /// <returns>The two linked ends. Each pair is independent (no shared pipes).</returns>
    internal static (LoopbackFrameTransport Left, LoopbackFrameTransport Right) CreatePair()
    {
        var leftToRight = new Pipe();
        var rightToLeft = new Pipe();
        var shared = new SharedClose();
        return (
            new LoopbackFrameTransport(rightToLeft.Reader, leftToRight.Writer, shared, isLeft: true),
            new LoopbackFrameTransport(leftToRight.Reader, rightToLeft.Writer, shared, isLeft: false));
    }

    /// <inheritdoc />
    public int? CloseStatusCode
    {
        get
        {
            int peer = _isLeft ? Volatile.Read(ref _shared.RightCode) : Volatile.Read(ref _shared.LeftCode);
            if (peer != SharedClose.Open)
            {
                return peer;
            }

            int local = _isLeft ? Volatile.Read(ref _shared.LeftCode) : Volatile.Read(ref _shared.RightCode);
            return local == SharedClose.Open ? null : local;
        }
    }

    /// <inheritdoc />
    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (frame.IsEmpty)
        {
            throw new SdkTransportException("Frame must not be empty.");
        }

        if (CloseStatusCode is not null)
        {
            throw new SdkConnectionException("Transport is closed — no more sends are accepted.");
        }

        try
        {
            Span<byte> prefix = _writer.GetSpan(4);
            BinaryPrimitives.WriteInt32BigEndian(prefix, frame.Length);
            _writer.Advance(4);
            await _writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            FlushResult flush = await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (flush.IsCompleted || flush.IsCanceled)
            {
                throw new SdkTransportException("Frame send failed because the peer is gone.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SdkException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SdkTransportException("Frame send failed.", ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        try
        {
            byte[]? lengthBytes = await ReadExactlyAsync(4, cancellationToken).ConfigureAwait(false);
            if (lengthBytes is null)
            {
                return null;
            }

            int length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
            if (length < 0)
            {
                throw new SdkTransportException("Frame length prefix is negative.");
            }

            if (length == 0)
            {
                throw new SdkTransportException("Frame payload must not be empty.");
            }

            byte[]? payload = await ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false) ?? throw new SdkTransportException("Frame payload truncated by peer close.");

            return payload;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SdkException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SdkTransportException("Frame receive failed.", ex);
        }
    }

    /// <inheritdoc />
    public ValueTask CloseAsync(int statusCode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_isLeft)
        {
            Interlocked.CompareExchange(ref _shared.LeftCode, statusCode, SharedClose.Open);
        }
        else
        {
            Interlocked.CompareExchange(ref _shared.RightCode, statusCode, SharedClose.Open);
        }

        _writer.Complete();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _writer.Complete();
        _reader.Complete();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes, spanning pipe reads. Returns
    /// <c>null</c> on orderly completion with zero bytes outstanding (peer closed);
    /// throws <see cref="SdkTransportException"/> on completion with a partial frame.
    /// </summary>
    private async Task<byte[]?> ReadExactlyAsync(int count, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[count];
        int filled = 0;
        while (filled < count)
        {
            ReadResult read = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> sequence = read.Buffer;
            if (sequence.IsEmpty && read.IsCompleted)
            {
                _reader.AdvanceTo(sequence.End);
                return filled == 0 ? null : throw new SdkTransportException("Frame truncated by peer close.");
            }

            long take = Math.Min(sequence.Length, count - filled);
            sequence.Slice(0, take).CopyTo(buffer.AsSpan(filled));
            filled += (int)take;
            _reader.AdvanceTo(sequence.GetPosition(take));

            if (read.IsCompleted && filled < count)
            {
                throw new SdkTransportException("Frame truncated by peer close.");
            }
        }

        return buffer;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new ObjectDisposedException(nameof(LoopbackFrameTransport));
        }
    }

    /// <summary>Close codes shared by one pair (both directions observable from either end).</summary>
    private sealed class SharedClose
    {
        internal const int Open = -1;

        internal int LeftCode = Open;
        internal int RightCode = Open;
    }
}
