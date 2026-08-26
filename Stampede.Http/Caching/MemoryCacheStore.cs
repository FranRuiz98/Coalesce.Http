using Microsoft.Extensions.Caching.Memory;

namespace Stampede.Http.Caching;

/// <summary>
/// Default <see cref="ICacheStore"/> implementation backed by <see cref="IMemoryCache"/>.
/// </summary>
/// <remarks>
/// <para>
/// When <see cref="CacheOptions.MaxCacheSize"/> is configured, each entry's <see cref="CacheEntry.Body"/>
/// length is reported as its size so that <see cref="IMemoryCache"/> can enforce the limit.
/// </para>
/// <para>
/// Entries carrying a validator (<c>ETag</c> or <c>Last-Modified</c>) are retained for
/// <see cref="CacheOptions.RevalidationGraceSeconds"/> beyond their freshness lifetime and stale
/// windows so conditional revalidation can be attempted instead of a full refetch.
/// </para>
/// </remarks>
public sealed class MemoryCacheStore(IMemoryCache memoryCache, CacheOptions options) : ICacheStore
{
    /// <summary>
    /// Creates a store with default <see cref="CacheOptions"/> (five-minute revalidation grace).
    /// </summary>
    /// <param name="memoryCache">The <see cref="IMemoryCache"/> backing this store.</param>
    public MemoryCacheStore(IMemoryCache memoryCache) : this(memoryCache, new CacheOptions()) { }

    /// <inheritdoc/>
    public bool TryGetValue(string key, out CacheEntry? entry)
    {
        return memoryCache.TryGetValue(key, out entry);
    }

    /// <inheritdoc/>
    public void Set(string key, CacheEntry entry)
    {
        TimeSpan retention = ComputeRetention(entry, options.RevalidationGraceSeconds);

        if (retention <= TimeSpan.Zero)
        {
            // The entry has no freshness left, no stale window and no revalidation grace, so it can never be
            // served nor revalidated. Retaining it would leak: IMemoryCache evicts by LRU only when a SizeLimit
            // is configured (CacheOptions.MaxCacheSize), which is null by default — an entry stored without an
            // expiration would then live for the lifetime of the process. Drop any previous representation at
            // this key too, so a superseded response is never served.
            memoryCache.Remove(key);
            return;
        }

        using ICacheEntry cacheEntry = memoryCache.CreateEntry(key);
        cacheEntry.Value = entry;
        cacheEntry.Size = ComputeSize(entry);

        // Use a relative TTL so the eviction deadline is clock-agnostic (works with FakeTimeProvider in tests).
        cacheEntry.AbsoluteExpirationRelativeToNow = retention;
    }

    /// <summary>
    /// Computes how long an entry must be retained by the backing store: its freshness lifetime, plus the
    /// largest configured stale window so it remains available for stale-if-error / stale-while-revalidate
    /// after it becomes stale, plus — when it carries a validator (<c>ETag</c> / <c>Last-Modified</c>) — the
    /// revalidation grace period, so a conditional <c>If-None-Match</c> / <c>If-Modified-Since</c> request is
    /// still possible once all serve-stale windows have elapsed.
    /// </summary>
    /// <returns>
    /// The retention window. Zero or negative means the entry can never be served nor revalidated.
    /// A <c>max-age=0</c> response with no validator lands here: <c>FreshnessCalculator</c> makes a separate
    /// <c>GetUtcNow()</c> call from <c>StoreAsync</c>, so on the real clock the window is a few ticks rather
    /// than exactly zero — small enough that the entry is evicted before it could ever be read back.
    /// </returns>
    internal static TimeSpan ComputeRetention(CacheEntry entry, long revalidationGraceSeconds)
    {
        long staleWindowSeconds = Math.Max(entry.StaleIfErrorSeconds, entry.StaleWhileRevalidateSeconds);
        long graceSeconds = HasValidator(entry) ? revalidationGraceSeconds : 0;

        return (entry.ExpiresAt - entry.StoredAt) + TimeSpan.FromSeconds(staleWindowSeconds + graceSeconds);
    }

    /// <summary>
    /// Returns <see langword="true"/> when the entry carries a validator usable for conditional
    /// revalidation (RFC 9111 §4.3.1 — <c>ETag</c> or <c>Last-Modified</c>).
    /// </summary>
    internal static bool HasValidator(CacheEntry entry)
    {
        return entry.ETag is not null || entry.LastModified is not null;
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

        foreach (string trackedKey in entry.TrackedKeys)
        {
            size += trackedKey.Length;
        }

        foreach (string tag in entry.Tags)
        {
            size += tag.Length;
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
