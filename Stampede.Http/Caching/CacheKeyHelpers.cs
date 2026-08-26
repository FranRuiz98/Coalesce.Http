namespace Stampede.Http.Caching;

/// <summary>
/// Shared helper for building the cache key that would apply to a plain, unauthenticated GET request
/// for a given URI — the key every stored entry lives under, since only GET responses are ever cached
/// (HEAD reuses the GET entry via this same key, and unsafe methods are never cached themselves, only
/// invalidate it).
/// </summary>
/// <remarks>
/// Used by <see cref="CachingMiddleware"/> for §4.4 invalidation and HEAD lookups, and by
/// <see cref="StampedeHttpCache"/> for explicit eviction — kept in one place so both stay in sync with
/// <see cref="ICacheKeyBuilder"/>'s key format.
/// </remarks>
internal static class CacheKeyHelpers
{
    /// <summary>
    /// Builds the cache key for a synthetic, header-less GET request to <paramref name="uri"/>.
    /// </summary>
    public static string BuildGetKey(ICacheKeyBuilder keyBuilder, Uri? uri)
    {
        using HttpRequestMessage synthetic = new(HttpMethod.Get, uri);
        return keyBuilder.Build(synthetic);
    }
}
