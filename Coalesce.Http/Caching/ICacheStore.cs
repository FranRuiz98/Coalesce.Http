namespace Coalesce.Http.Caching;

/// <summary>
/// Defines the contract for a cache store used by the HTTP caching middleware.
/// </summary>
/// <remarks>
/// Implement this interface to provide a custom cache storage mechanism (e.g., distributed cache, Redis, etc.).
/// The default implementation, <see cref="MemoryCacheStore"/>, uses <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/>.
/// Default async methods are provided for convenience; override them to provide truly non-blocking I/O
/// (as <see cref="DistributedCacheStore"/> does).
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

    /// <summary>
    /// Asynchronously retrieves the cached entry for <paramref name="key"/>, or <see langword="null"/> on a miss.
    /// </summary>
    /// <remarks>
    /// The default implementation delegates to <see cref="TryGetValue"/>.
    /// Override to avoid blocking the thread pool (e.g., for Redis or SQL-backed stores).
    /// </remarks>
    ValueTask<CacheEntry?> GetAsync(string key, CancellationToken ct = default)
    {
        TryGetValue(key, out CacheEntry? entry);
        return new(entry);
    }

    /// <summary>
    /// Asynchronously stores a cache entry with the specified key.
    /// </summary>
    /// <remarks>
    /// The default implementation delegates to <see cref="Set"/>.
    /// Override to avoid blocking the thread pool (e.g., for Redis or SQL-backed stores).
    /// </remarks>
    ValueTask SetAsync(string key, CacheEntry entry, CancellationToken ct = default)
    {
        Set(key, entry);
        return default;
    }

    /// <summary>
    /// Asynchronously removes the cache entry associated with the specified key.
    /// </summary>
    /// <remarks>
    /// The default implementation delegates to <see cref="Remove"/>.
    /// Override to avoid blocking the thread pool (e.g., for Redis or SQL-backed stores).
    /// </remarks>
    ValueTask RemoveAsync(string key, CancellationToken ct = default)
    {
        Remove(key);
        return default;
    }
}
