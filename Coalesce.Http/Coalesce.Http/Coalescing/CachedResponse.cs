using System.Net;

namespace Coalesce.Http.Coalesce.Http.Coalescing;

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
            ? await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false)
            : [];

        if (bodyBytes.Length > maxBodyBytes)
        {
            throw new InvalidOperationException(
                $"Response body size ({bodyBytes.Length} bytes) exceeds the configured MaxResponseBodyBytes limit ({maxBodyBytes} bytes).");
        }

        // RequestMessage is intentionally not cached. It is IDisposable, not thread-safe,
        // and sharing it across coalesced callers would cause subtle concurrency issues.
        return new CachedResponse(
            StatusCode: response.StatusCode,
            Version: response.Version,
            ReasonPhrase: response.ReasonPhrase,
            BodyBytes: bodyBytes,
            ResponseHeaders: response.Headers
                .Select(h => KeyValuePair.Create(h.Key, h.Value))
                .ToList(),
            ContentHeaders: response.Content?.Headers
                .Select(h => KeyValuePair.Create(h.Key, h.Value))
                .ToList() ?? []
        );
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
            ByteArrayContent content = new(BodyBytes);
            foreach (KeyValuePair<string, IEnumerable<string>> header in ContentHeaders)
            {
                _ = content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = content;
        }

        return clone;
    }
}