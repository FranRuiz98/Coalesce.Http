using Microsoft.Extensions.Caching.Memory;

namespace Coalesce.Http.Caching;

/// <summary>
/// Default <see cref="ICacheStore"/> implementation backed by <see cref="IMemoryCache"/>.
/// </summary>
public sealed class MemoryCacheStore(IMemoryCache memoryCache) : ICacheStore
{
    /// <inheritdoc/>
    public bool TryGetValue(string key, out CacheEntry? entry)
    {
        return memoryCache.TryGetValue(key, out entry);
    }

    /// <inheritdoc/>
    public void Set(string key, CacheEntry entry)
    {
        memoryCache.Set(key, entry);
    }
}
