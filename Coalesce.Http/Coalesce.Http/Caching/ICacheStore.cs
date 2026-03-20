namespace Coalesce.Http.Caching;

/// <summary>
/// Defines the contract for a cache store used by the HTTP caching middleware.
/// </summary>
/// <remarks>
/// Implement this interface to provide a custom cache storage mechanism (e.g., distributed cache, Redis, etc.).
/// The default implementation, <see cref="MemoryCacheStore"/>, uses <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/>.
/// </remarks>
public interface ICacheStore
{
    /// <summary>
    /// Attempts to retrieve a cached entry associated with the specified key.
    /// </summary>
    /// <param name="key">The cache key to look up.</param>
    /// <param name="entry">When this method returns, contains the cached entry if found; otherwise, <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if an entry was found for the specified key; otherwise, <see langword="false"/>.</returns>
    bool TryGetValue(string key, out CacheEntry? entry);

    /// <summary>
    /// Stores a cache entry with the specified key.
    /// </summary>
    /// <param name="key">The cache key under which to store the entry.</param>
    /// <param name="entry">The cache entry to store.</param>
    void Set(string key, CacheEntry entry);

    /// <summary>
    /// Removes the cache entry associated with the specified key.
    /// </summary>
    /// <param name="key">The cache key to remove.</param>
    void Remove(string key);
}
