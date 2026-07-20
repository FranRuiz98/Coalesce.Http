using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;

namespace Stampede.Http.Caching;

/// <summary>
/// An <see cref="ICacheStore"/> implementation backed by <see cref="IDistributedCache"/>,
/// enabling shared caching across multiple application instances (e.g., Redis, SQL Server).
/// </summary>
/// <remarks>
/// <para>
/// Register via <c>UseDistributedCacheStore()</c> on the <c>IHttpClientBuilder</c> after calling
/// <c>AddStampedeHttp()</c> or <c>AddCachingOnly()</c>:
/// </para>
/// <code>
/// services.AddStackExchangeRedisCache(o => o.Configuration = "localhost");
///
/// services.AddHttpClient("catalog")
///     .AddStampedeHttp()
///     .UseDistributedCacheStore();
/// </code>
/// <para>
/// <see cref="CacheEntry"/> objects are serialized with <see cref="System.Text.Json"/> using
/// the source-generated <see cref="CacheEntryJsonContext"/>
/// The <see cref="DistributedCacheEntryOptions.AbsoluteExpiration"/> is extended
/// by <c>Max(StaleIfErrorSeconds, StaleWhileRevalidateSeconds)</c> beyond <see cref="CacheEntry.ExpiresAt"/>
/// — plus <see cref="CacheOptions.RevalidationGraceSeconds"/> when the entry carries an <c>ETag</c> or
/// <c>Last-Modified</c> validator — so the backing store retains entries long enough for stale serving
/// and conditional revalidation while still evicting them once all configured windows have expired,
/// even between process restarts.
/// </para>
/// </remarks>
public sealed class DistributedCacheStore(IDistributedCache distributedCache, CacheOptions options) : ICacheStore
{
    /// <summary>
    /// Creates a store with default <see cref="CacheOptions"/> (five-minute revalidation grace).
    /// </summary>
    /// <param name="distributedCache">The <see cref="IDistributedCache"/> backing this store.</param>
    public DistributedCacheStore(IDistributedCache distributedCache) : this(distributedCache, new CacheOptions()) { }

    /// <inheritdoc/>
    public bool TryGetValue(string key, out CacheEntry? entry)
    {
        byte[]? bytes = distributedCache.Get(key);

        if (bytes is null)
        {
            entry = null;
            return false;
        }

        entry = JsonSerializer.Deserialize(bytes, CacheEntryJsonContext.Default.CacheEntry);
        return entry is not null;
    }

    /// <inheritdoc/>
    public void Set(string key, CacheEntry entry)
    {
        (byte[] bytes, DistributedCacheEntryOptions entryOptions) = Serialize(entry);
        distributedCache.Set(key, bytes, entryOptions);
    }

    /// <inheritdoc/>
    public void Remove(string key)
    {
        distributedCache.Remove(key);
    }

    /// <inheritdoc/>
    public async ValueTask<CacheEntry?> GetAsync(string key, CancellationToken ct = default)
    {
        byte[]? bytes = await distributedCache.GetAsync(key, ct).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        return JsonSerializer.Deserialize(bytes, CacheEntryJsonContext.Default.CacheEntry);
    }

    /// <inheritdoc/>
    public async ValueTask SetAsync(string key, CacheEntry entry, CancellationToken ct = default)
    {
        (byte[] bytes, DistributedCacheEntryOptions entryOptions) = Serialize(entry);
        await distributedCache.SetAsync(key, bytes, entryOptions, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask RemoveAsync(string key, CancellationToken ct = default)
    {
        await distributedCache.RemoveAsync(key, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Serializes <paramref name="entry"/> to UTF-8 JSON using the source-generated context and
    /// builds the <see cref="DistributedCacheEntryOptions"/> with an <c>AbsoluteExpiration</c>
    /// extended by the maximum stale window — plus the revalidation grace period when the entry
    /// carries a validator — so entries remain available for stale-if-error / stale-while-revalidate
    /// serving and conditional revalidation after <see cref="CacheEntry.ExpiresAt"/>.
    /// </summary>
    private (byte[] Bytes, DistributedCacheEntryOptions Options) Serialize(CacheEntry entry)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(entry, CacheEntryJsonContext.Default.CacheEntry);

        long staleWindowSeconds = Math.Max(entry.StaleIfErrorSeconds, entry.StaleWhileRevalidateSeconds);
        long graceSeconds = MemoryCacheStore.HasValidator(entry) ? options.RevalidationGraceSeconds : 0;
        long extensionSeconds = staleWindowSeconds + graceSeconds;
        DateTimeOffset absoluteExpiration = extensionSeconds > 0
            ? entry.ExpiresAt + TimeSpan.FromSeconds(extensionSeconds)
            : entry.ExpiresAt;

        return (bytes, new DistributedCacheEntryOptions { AbsoluteExpiration = absoluteExpiration });
    }
}
