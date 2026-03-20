using Microsoft.Extensions.Caching.Memory;

namespace Coalesce.Http.Caching;

/// <summary>
/// Default <see cref="ICacheStore"/> implementation backed by <see cref="IMemoryCache"/>.
/// </summary>
/// <remarks>
/// When <see cref="CacheOptions.MaxCacheSize"/> is configured, each entry's <see cref="CacheEntry.Body"/>
/// length is reported as its size so that <see cref="IMemoryCache"/> can enforce the limit.
/// </remarks>
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
        using ICacheEntry cacheEntry = memoryCache.CreateEntry(key);
        cacheEntry.Value = entry;
        cacheEntry.Size = entry.Body.Length;
    }

    /// <inheritdoc/>
    public void Remove(string key)
    {
        memoryCache.Remove(key);
    }
}
