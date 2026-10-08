namespace Iwasug.WhatsApp.Sdk.Transport;

/// <summary>
/// Byte-duplex frame seam between the connection machinery and the wire (Phase 2
/// transport seam, Pattern 1): frames out, frames in, close-code visibility. Production
/// traffic rides <c>WebSocketFrameTransport</c> (02-02); every other layer — Noise,
/// codec, router, reconnect — is written against this interface and tested against the
/// in-memory loopback fake.
/// </summary>
/// <remarks>
/// <para>
/// Follows the <c>ISignalSessionStore</c> seam shape: <c>CancellationToken</c>-first on
/// every async method (<c>CancellationToken cancellationToken = default</c>),
/// <c>ValueTask</c> returns, <c>cancellationToken.ThrowIfCancellationRequested()</c> as
/// the implementation's first line, <c>.ConfigureAwait(false)</c> on every await.
/// One instance per connection/account — no shared mutable state across accounts and no
/// statics (InMemoryStoreFactory precedent).
/// </para>
/// <para>
/// Close protocol: either side initiates with <see cref="CloseAsync"/>; the peer's code
/// becomes visible in <see cref="CloseStatusCode"/>. After orderly close,
/// <see cref="ReceiveAsync"/> returns <c>null</c> (no more frames will ever arrive) and
/// <see cref="SendAsync"/> throws <see cref="Errors.SdkConnectionException"/>.
/// </para>
/// </remarks>
public interface IFrameTransport : IAsyncDisposable
{
    /// <summary>
    /// Sends one complete frame's bytes (already length-delimited by the caller —
    /// framing the payload is 02-04's job, not the transport's).
    /// </summary>
    /// <param name="frame">Frame bytes to send. Must not be empty.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task completing when the bytes are handed to the wire.</returns>
    /// <exception cref="Errors.SdkTransportException">The bytes could not be sent (socket fault).</exception>
    /// <exception cref="Errors.SdkConnectionException">The transport is closed — no more sends are accepted.</exception>
    ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default);

    /// <summary>
    /// Receives the next complete frame's bytes.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The frame bytes, or <c>null</c> when the transport closed orderly (see
    /// <see cref="CloseStatusCode"/>) — <c>null</c> is terminal, never transient.
    /// </returns>
    /// <exception cref="Errors.SdkTransportException">The read faulted mid-frame (truncated length prefix or payload).</exception>
    ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The close status code once known — set by the local <see cref="CloseAsync"/> or
    /// by the peer's close, <c>null</c> while the transport is open.
    /// </summary>
    int? CloseStatusCode { get; }

    /// <summary>
    /// Initiates orderly close with <paramref name="statusCode"/> (a
    /// <see cref="Connection.DisconnectReason"/> value or a transport-local code).
    /// Idempotent: repeated calls observe the first code.
    /// </summary>
    /// <param name="statusCode">Close status code to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Errors.SdkTransportException">The close handshake faulted.</exception>
    ValueTask CloseAsync(int statusCode, CancellationToken cancellationToken = default);
}
