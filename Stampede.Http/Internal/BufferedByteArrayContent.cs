namespace Stampede.Http.Internal;

/// <summary>
/// A <see cref="ByteArrayContent"/> that exposes the buffer it was built from, so a downstream layer in the
/// same pipeline can reuse the bytes instead of copying them out again.
/// </summary>
/// <remarks>
/// <para>
/// Both the coalescer and the cache hand responses to callers as fully buffered byte arrays. Without this
/// type the caching layer has no way to tell an already-materialised body from a live network stream, so it
/// must call <c>ReadAsByteArrayAsync</c> — which copies the whole payload again — and then rebuffer the
/// result into a fresh <see cref="ByteArrayContent"/>. On a coalesced miss that is two extra full-size
/// allocations per caller, and every waiter sharing one origin call pays them.
/// </para>
/// <para>
/// Sharing the array is safe: <see cref="ByteArrayContent"/> never mutates it, hands out copies from
/// <c>ReadAsByteArrayAsync</c>, and exposes it only through a read-only stream. This is the same sharing the
/// cache already relies on when it serves many responses from one stored <c>CacheEntry.Body</c>.
/// </para>
/// </remarks>
/// <param name="buffer">The response body. Must not be mutated after construction.</param>
internal sealed class BufferedByteArrayContent(byte[] buffer) : ByteArrayContent(buffer)
{
    /// <summary>The body this content was built from.</summary>
    public byte[] Buffer { get; } = buffer;
}
