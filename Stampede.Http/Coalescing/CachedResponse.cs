using Stampede.Http.Internal;
using System.Buffers;
using System.Net;
using System.Net.Http.Headers;

namespace Stampede.Http.Coalescing;

/// <summary>
/// Represents a cached HTTP response, including status code, headers, and body content.
/// </summary>
/// <remarks>This type is useful for persisting or reusing HTTP responses, such as in caching scenarios. It allows
/// reconstruction of an equivalent HttpResponseMessage from the stored data.</remarks>
/// <param name="StatusCode">The HTTP status code of the response, indicating the result of the request.</param>
/// <param name="Version">The HTTP version used for the response.</param>
/// <param name="ReasonPhrase">An optional reason phrase that describes the status code.</param>
/// <param name="BodyBytes">The raw byte array representing the body content of the response.</param>
/// <param name="ResponseHeaders">A collection of key-value pairs representing the response headers.</param>
/// <param name="ContentHeaders">A collection of key-value pairs representing the content headers of the response.</param>
internal sealed record CachedResponse(
    HttpStatusCode StatusCode,
    Version Version,
    string? ReasonPhrase,
    byte[] BodyBytes,
    IReadOnlyList<KeyValuePair<string, IEnumerable<string>>> ResponseHeaders,
    IReadOnlyList<KeyValuePair<string, IEnumerable<string>>> ContentHeaders
)
{
    public static async Task<CachedResponse> FromResponseAsync(
        HttpResponseMessage response,
        long maxBodyBytes = long.MaxValue,
        CancellationToken cancellationToken = default)
    {
        byte[] bodyBytes = response.Content is not null
            ? await ReadBoundedAsync(response.Content, maxBodyBytes, cancellationToken).ConfigureAwait(false)
            : [];

        // RequestMessage is intentionally not cached. It is IDisposable, not thread-safe,
        // and sharing it across coalesced callers would cause subtle concurrency issues.
        return new CachedResponse(
            StatusCode: response.StatusCode,
            Version: response.Version,
            ReasonPhrase: response.ReasonPhrase,
            BodyBytes: bodyBytes,
            ResponseHeaders: MaterializeHeaders(response.Headers),
            ContentHeaders: response.Content is not null
                ? MaterializeHeaders(response.Content.Headers)
                : []
        );
    }

    /// <summary>
    /// Reads the response body, refusing to buffer more than <paramref name="maxBodyBytes"/>.
    /// </summary>
    /// <remarks>
    /// The limit is enforced <em>while</em> reading rather than afterwards: checking the length of an
    /// already-materialised array means a response far larger than the limit is fully allocated before it can
    /// be rejected, which is exactly the case the limit exists to prevent. A declared <c>Content-Length</c>
    /// rejects the response before a single byte is read; a chunked response is read incrementally and
    /// abandoned as soon as it crosses the limit.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The body exceeds <paramref name="maxBodyBytes"/>.</exception>
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, long maxBodyBytes, CancellationToken cancellationToken)
    {
        // Already materialised by an inner Stampede layer — reuse the array, there is no stream to bound.
        if (content is BufferedByteArrayContent buffered)
        {
            return buffered.Buffer.Length > maxBodyBytes
                ? throw BodyTooLarge(buffered.Buffer.Length, maxBodyBytes, exact: true)
                : buffered.Buffer;
        }

        long? declaredLength = content.Headers.ContentLength;

        if (declaredLength > maxBodyBytes)
        {
            throw BodyTooLarge(declaredLength.Value, maxBodyBytes, exact: true);
        }

        if (declaredLength is not null)
        {
            // Length known and within the limit: let the framework allocate the array exactly once.
            return await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        // Chunked / unknown length: copy incrementally so an oversized body is abandoned mid-stream.
        Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        using MemoryStream sink = new();
        byte[] rented = ArrayPool<byte>.Shared.Rent(CopyBufferSize);

        try
        {
            long total = 0;
            int read;

            while ((read = await stream.ReadAsync(rented, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;

                if (total > maxBodyBytes)
                {
                    throw BodyTooLarge(total, maxBodyBytes, exact: false);
                }

                sink.Write(rented, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return sink.ToArray();
    }

    /// <summary>Matches the default copy buffer size used by <see cref="Stream.CopyToAsync(Stream)"/>.</summary>
    private const int CopyBufferSize = 81920;

    private static InvalidOperationException BodyTooLarge(long observedBytes, long maxBodyBytes, bool exact)
    {
        string size = exact ? $"{observedBytes} bytes" : $"more than {observedBytes} bytes";

        return new InvalidOperationException(
            $"Response body size ({size}) exceeds the configured MaxResponseBodyBytes limit ({maxBodyBytes} bytes).");
    }

    private static KeyValuePair<string, IEnumerable<string>>[] MaterializeHeaders(
        HttpHeaders headers)
    {
        // Avoid LINQ allocations (.Select + delegate + List<T>); iterate once into a right-sized array.
        // HttpHeaders implements IEnumerable but not ICollection, so we count first.
        int count = 0;
        foreach (KeyValuePair<string, IEnumerable<string>> _ in headers)
        {
            count++;
        }

        if (count == 0)
        {
            return [];
        }

        KeyValuePair<string, IEnumerable<string>>[] result = new KeyValuePair<string, IEnumerable<string>>[count];
        int i = 0;
        foreach (KeyValuePair<string, IEnumerable<string>> header in headers)
        {
            result[i++] = header;
        }

        return result;
    }

    public HttpResponseMessage ToHttpResponseMessage()
    {
        HttpResponseMessage clone = new(StatusCode)
        {
            Version = Version,
            ReasonPhrase = ReasonPhrase,
        };

        foreach (KeyValuePair<string, IEnumerable<string>> header in ResponseHeaders)
        {
            _ = clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (BodyBytes.Length > 0 || ContentHeaders.Count > 0)
        {
            // BufferedByteArrayContent, not ByteArrayContent: it lets the caching layer above reuse these
            // bytes directly instead of copying the whole body out again on its way into the cache.
            BufferedByteArrayContent content = new(BodyBytes);
            foreach (KeyValuePair<string, IEnumerable<string>> header in ContentHeaders)
            {
                _ = content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = content;
        }

        return clone;
    }
}