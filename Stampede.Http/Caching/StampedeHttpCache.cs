namespace Stampede.Http.Caching;

/// <summary>
/// Default <see cref="IStampedeHttpCache"/> implementation — a thin wrapper over the client's own
/// <see cref="ICacheStore"/> and <see cref="ICacheKeyBuilder"/>, resolving the same key
/// <see cref="CachingMiddleware"/> would for a GET to the given URI.
/// </summary>
internal sealed class StampedeHttpCache(ICacheStore cache, ICacheKeyBuilder keyBuilder) : IStampedeHttpCache
{
    /// <inheritdoc/>
    public ValueTask EvictAsync(Uri uri, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(uri);

        string key = CacheKeyHelpers.BuildGetKey(keyBuilder, uri);
        return cache.RemoveAsync(key, ct);
    }
}
