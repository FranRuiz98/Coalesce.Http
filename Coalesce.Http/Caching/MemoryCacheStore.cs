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
        cacheEntry.Size = ComputeSize(entry);

        // Use a relative TTL so the eviction deadline is clock-agnostic (works with FakeTimeProvider in tests).
        // The window is the freshness TTL plus the largest configured stale window so entries remain available
        // for stale-if-error / stale-while-revalidate after they become stale.
        // Truncate to whole seconds: FreshnessCalculator makes a separate GetUtcNow() call from StoreAsync, so
        // ExpiresAt - StoredAt can be a few ticks positive for max-age=0 responses on the real clock. Treating
        // sub-second residuals as zero keeps max-age=0 entries alive for conditional revalidation (ETag / LM).
        long staleWindowSeconds = Math.Max(entry.StaleIfErrorSeconds, entry.StaleWhileRevalidateSeconds);
        long nominalTtlSeconds = (long)(entry.ExpiresAt - entry.StoredAt).TotalSeconds;
        long evictionTtlSeconds = nominalTtlSeconds + staleWindowSeconds;

        if (evictionTtlSeconds > 0)
        {
            cacheEntry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(evictionTtlSeconds);
        }
        // evictionTtlSeconds <= 0 (e.g. max-age=0 with no stale window): omit expiration so the entry stays
        // in memory and its ETag / Last-Modified can be used for conditional revalidation (LRU eviction only).
    }

    /// <summary>
    /// Computes a size estimate for a cache entry, accounting for body, headers, vary metadata,
    /// and a fixed per-entry overhead. Used to enforce <see cref="CacheOptions.MaxCacheSize"/>.
    /// </summary>
    internal static long ComputeSize(CacheEntry entry)
    {
        const int FixedOverhead = 128;

        long size = entry.Body.Length + FixedOverhead;

        foreach (KeyValuePair<string, string[]> header in entry.Headers)
        {
            size += header.Key.Length;
            foreach (string value in header.Value)
            {
                size += value.Length;
            }
        }

        foreach (string field in entry.VaryFields)
        {
            size += field.Length;
        }

        foreach (KeyValuePair<string, string[]> vary in entry.VaryValues)
        {
            size += vary.Key.Length;
            foreach (string value in vary.Value)
            {
                size += value.Length;
            }
        }

        size += entry.ETag?.Length ?? 0;

        return size;
    }

    /// <inheritdoc/>
    public void Remove(string key)
    {
        memoryCache.Remove(key);
    }
}
