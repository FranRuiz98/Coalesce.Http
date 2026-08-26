using System.Net.Http.Headers;

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
        return CacheIndexing.EvictWithVariantsAsync(cache, key, ct);
    }

    /// <inheritdoc/>
    public ValueTask EvictAsync(Uri uri, AuthenticationHeaderValue authorization, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(authorization);

        string key = CacheKeyHelpers.BuildGetKey(keyBuilder, uri, authorization);
        return CacheIndexing.EvictWithVariantsAsync(cache, key, ct);
    }

    /// <inheritdoc/>
    public async ValueTask EvictByTagAsync(string tag, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        string tagKey = CacheIndexing.BuildTagKey(tag);
        CacheEntry? index = await cache.GetAsync(tagKey, ct).ConfigureAwait(false);

        if (index is not null)
        {
            foreach (string primaryKey in index.TrackedKeys)
            {
                await CacheIndexing.EvictWithVariantsAsync(cache, primaryKey, ct).ConfigureAwait(false);
            }
        }

        await cache.RemoveAsync(tagKey, ct).ConfigureAwait(false);
    }
}
