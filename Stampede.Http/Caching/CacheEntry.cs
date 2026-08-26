using System.Text.Json.Serialization;

namespace Stampede.Http.Caching;

/// <summary>
/// Represents a cached HTTP response, including its status code, content type, body, headers, and expiration time.
/// </summary>
/// <remarks>This record is intended for internal use and encapsulates all relevant metadata required to manage
/// cached HTTP responses efficiently.</remarks>
[JsonConverter(typeof(CacheEntryJsonConverter))]
public sealed record CacheEntry
{
    /// <summary>HTTP status code of the stored response (RFC 9111 §3 — a cache MUST retain the status code as part of the stored response).</summary>
    public required int StatusCode { get; init; }

    /// <summary>Serialized response payload (RFC 9111 §3 — the response content is stored and replayed verbatim on cache hits).</summary>
    public required byte[] Body { get; init; }

    /// <summary>Response header fields captured at store time and replayed on cache hits (RFC 9111 §3.1 — a cache MUST store and forward the original header fields).</summary>
    public required IReadOnlyDictionary<string, string[]> Headers { get; init; }

    /// <summary>Absolute point in time at which the freshness lifetime ends (RFC 9111 §4.2 — once exceeded, <see cref="IsExpired()"/> returns <see langword="true"/> and the entry must be revalidated or discarded).</summary>
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

    /// <summary>When <see langword="true"/>, the origin included <c>Cache-Control: immutable</c> (RFC 8246). A fresh immutable entry must never be revalidated, even when the request carries <c>no-cache</c> or a <c>ForceRevalidate</c> policy.</summary>
    public bool Immutable { get; init; }

    /// <summary>
    /// Wall-clock time the most recent origin call for this entry took, in milliseconds — the initial
    /// fetch, a conditional revalidation, or a background refresh, whichever last updated this entry.
    /// Zero means unmeasured: entries written before 2.5 deserialize with this defaulted, and it is
    /// otherwise always positive once any origin call has completed for the key.
    /// </summary>
    /// <remarks>
    /// Used by early revalidation (XFetch, <see cref="CacheOptions.EnableEarlyRevalidation"/>) to scale how
    /// far ahead of <see cref="ExpiresAt"/> a background refresh is attempted — a resource that is
    /// expensive to recompute starts being refreshed earlier than one that answers instantly.
    /// </remarks>
    public long OriginFetchDurationMs { get; init; }

    /// <summary>
    /// When <see langword="true"/>, this entry is not a stored response but a <em>Vary marker</em> (RFC 9111 §4.1):
    /// it lives at the primary cache key and records the <see cref="VaryFields"/> that a request must be keyed on,
    /// pointing lookups to the correct secondary-key variant. Markers carry an empty <see cref="Body"/> and are
    /// never served as a response; their expiry/validator metadata mirror the representation they point to purely
    /// so their eviction deadline matches it.
    /// </summary>
    public bool IsVaryMarker { get; init; }

    /// <summary>
    /// Cache keys this entry tracks when it acts as an index rather than (only) a stored response:
    /// on a Vary marker (<see cref="IsVaryMarker"/>), the secondary-key variants stored under this
    /// primary key, so explicit eviction can sweep them; on a tag index entry, the primary keys of the
    /// responses carrying that tag. Empty on ordinary representations, and on entries written before 2.6.
    /// </summary>
    /// <remarks>
    /// Best-effort by design: concurrent writers merging this list can lose a key, and the list is capped
    /// (see <c>CacheIndexing.MaxTrackedKeys</c>) — an untracked key is never served incorrectly, it just
    /// expires on its own retention schedule instead of being actively removed on eviction.
    /// </remarks>
    public string[] TrackedKeys { get; init; } = [];

    /// <summary>
    /// Cache tags this response was stored under, collected from the response headers named in
    /// <see cref="CacheOptions.TagHeaderNames"/> and from <see cref="CacheRequestPolicy.Tags"/>.
    /// Carried on the entry so every <c>304</c> refresh can re-extend the per-tag indexes to the entry's
    /// new retention deadline — without this, a repeatedly revalidated entry would outlive its index and
    /// silently stop being reachable by tag eviction. Empty when untagged, and on pre-2.6 entries.
    /// </summary>
    public string[] Tags { get; init; } = [];

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