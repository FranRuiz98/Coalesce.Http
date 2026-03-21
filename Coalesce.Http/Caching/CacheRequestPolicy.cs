namespace Coalesce.Http.Caching;

/// <summary>
/// Provides per-request cache policy overrides via <see cref="HttpRequestMessage.Options"/>.
/// </summary>
/// <remarks>
/// Use these keys to control caching behavior on individual requests without changing global <see cref="CacheOptions"/>.
/// <para>
/// <b>Example:</b>
/// <code>
/// var request = new HttpRequestMessage(HttpMethod.Get, "/api/data");
/// request.Options.Set(CacheRequestPolicy.BypassCache, true);
/// </code>
/// </para>
/// </remarks>
public static class CacheRequestPolicy
{
    /// <summary>
    /// When set to <see langword="true"/>, the request bypasses the cache entirely:
    /// no cache lookup, no storage, and no unsafe-method invalidation.
    /// The request is forwarded directly to the inner handler.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> BypassCache = new("Coalesce.Http.BypassCache");

    /// <summary>
    /// When set to <see langword="true"/>, forces conditional revalidation of a cached entry
    /// even if the entry is still fresh. Behaves like the <c>no-cache</c> request directive
    /// (RFC 9111 §5.2.1.4) but set programmatically.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> ForceRevalidate = new("Coalesce.Http.ForceRevalidate");

    /// <summary>
    /// When set to <see langword="true"/>, prevents the response from being stored in the cache.
    /// Cache reads, conditional revalidation, and 304 TTL refreshes still function normally.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> NoStore = new("Coalesce.Http.NoStore");
}
