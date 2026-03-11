namespace Coalesce.Http.Coalesce.Http.Caching;

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
internal sealed record CacheEntry
{
    public required int StatusCode { get; init; }

    public required byte[] Body { get; init; }

    public required IReadOnlyDictionary<string, string[]> Headers { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Determines whether the cache entry has expired based on its expiration time.
    /// </summary>
    /// <returns>Returns <see langword="true"/> if the cache entry has expired; otherwise, <see langword="false"/>.</returns>
    public bool IsExpired() => DateTimeOffset.UtcNow >= ExpiresAt;
}