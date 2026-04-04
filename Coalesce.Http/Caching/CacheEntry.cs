using System.Text.Json.Serialization;

namespace Coalesce.Http.Caching;

/// <summary>
/// Represents a cached HTTP response, including its status code, content type, body, headers, and expiration time.
/// </summary>
/// <remarks>This record is intended for internal use and encapsulates all relevant metadata required to manage
/// cached HTTP responses efficiently.</remarks>
/// <param name="StatusCode">The HTTP status code returned by the cached response.</param>
/// <param name="ContentType">The media type of the cached content. May be null if not specified.</param>
/// <param name="Body">The raw byte array containing the body of the cached response.</param>
/// <param name="Headers">A read-only dictionary of HTTP headers associated with the cached response. Each key maps to an array of header
/// values.</param>
/// <param name="ExpiresAt">The date and time when the cached entry expires and is no longer valid.</param>
[JsonConverter(typeof(CacheEntryJsonConverter))]
public sealed record CacheEntry
{
    /// <summary>HTTP status code of the stored response (RFC 9111 §3 — a cache MUST retain the status code as part of the stored response).</summary>
    public required int StatusCode { get; init; }

    /// <summary>Serialized response payload (RFC 9111 §3 — the response content is stored and replayed verbatim on cache hits).</summary>
    public required byte[] Body { get; init; }

    /// <summary>Response header fields captured at store time and replayed on cache hits (RFC 9111 §3.1 — a cache MUST store and forward the original header fields).</summary>
    public required IReadOnlyDictionary<string, string[]> Headers { get; init; }

    /// <summary>Absolute point in time at which the freshness lifetime ends (RFC 9111 §4.2 — once exceeded, <see cref="IsExpired"/> returns <see langword="true"/> and the entry must be revalidated or discarded).</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The date and time at which this entry was stored, used to compute the <c>Age</c> response header (RFC 9111 §5.1).</summary>
    public required DateTimeOffset StoredAt { get; init; }

    /// <summary>Value of the <c>ETag</c> response header, used as the primary validator in <c>If-None-Match</c> conditional requests (RFC 9111 §4.3.2).</summary>
    public string? ETag { get; init; }

    /// <summary>Value of the <c>Last-Modified</c> response header, used as a fallback validator for <c>If-Modified-Since</c> (RFC 9111 §4.3.1).</summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>Header field names from the <c>Vary</c> response header (RFC 9111 §4.1). Empty means no Vary constraint.</summary>
    public string[] VaryFields { get; init; } = [];

    /// <summary>Values of the request headers listed in <see cref="VaryFields"/>, captured when this entry was stored.</summary>
    public IReadOnlyDictionary<string, string[]> VaryValues { get; init; } = new Dictionary<string, string[]>();

    /// <summary>Number of seconds after <see cref="ExpiresAt"/> during which a stale response may be served when the origin returns an error (RFC 5861 §4). Zero means the directive is absent.</summary>
    public long StaleIfErrorSeconds { get; init; }

    /// <summary>Number of seconds after <see cref="ExpiresAt"/> during which a stale response may be served immediately while a background revalidation is triggered (RFC 5861 §3). Zero means the directive is absent.</summary>
    public long StaleWhileRevalidateSeconds { get; init; }

    /// <summary>When <see langword="true"/>, the origin included <c>must-revalidate</c> or <c>proxy-revalidate</c> (RFC 9111 §5.2.2.2), prohibiting the cache from serving a stale response without successful revalidation.</summary>
    public bool MustRevalidate { get; init; }

    /// <summary>
    /// Determines whether the cache entry has expired based on its expiration time.
    /// </summary>
    /// <returns>Returns <see langword="true"/> if the cache entry has expired; otherwise, <see langword="false"/>.</returns>
    public bool IsExpired() => IsExpired(TimeProvider.System);

    /// <summary>
    /// Determines whether the cache entry has expired relative to the clock provided by <paramref name="timeProvider"/>.
    /// </summary>
    /// <param name="timeProvider">The <see cref="TimeProvider"/> used to obtain the current time.</param>
    /// <returns>Returns <see langword="true"/> if the cache entry has expired; otherwise, <see langword="false"/>.</returns>
    public bool IsExpired(TimeProvider timeProvider) => timeProvider.GetUtcNow() >= ExpiresAt;
}