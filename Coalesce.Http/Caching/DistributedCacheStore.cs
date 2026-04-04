using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;

namespace Coalesce.Http.Caching;

/// <summary>
/// An <see cref="ICacheStore"/> implementation backed by <see cref="IDistributedCache"/>,
/// enabling shared caching across multiple application instances (e.g., Redis, SQL Server).
/// </summary>
/// <remarks>
/// <para>
/// Register via <c>UseDistributedCacheStore()</c> on the <c>IHttpClientBuilder</c> after calling
/// <c>AddCoalesceHttp()</c> or <c>AddCachingOnly()</c>:
/// </para>
/// <code>
/// services.AddStackExchangeRedisCache(o => o.Configuration = "localhost");
///
/// services.AddHttpClient("catalog")
///     .AddCoalesceHttp()
///     .UseDistributedCacheStore();
/// </code>
/// <para>
/// <see cref="CacheEntry"/> objects are serialized with <see cref="System.Text.Json"/> using
/// the source-generated <see cref="CacheEntryJsonContext"/>
/// The <see cref="DistributedCacheEntryOptions.AbsoluteExpiration"/> is extended
/// by <c>Max(StaleIfErrorSeconds, StaleWhileRevalidateSeconds)</c> beyond <see cref="CacheEntry.ExpiresAt"/>
/// so the backing store retains entries long enough for stale serving while still evicting them once
/// all configured windows have expired, even between process restarts.
/// </para>
/// </remarks>
public sealed class DistributedCacheStore(IDistributedCache distributedCache) : ICacheStore
{
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
    /// 
    /// Serializes <paramref name="entry"/> to UTF-8 JSON using the source-generated context and
    /// builds the <see cref="DistributedCacheEntryOptions"/> with an <c>AbsoluteExpiration</c>
    /// extended by the maximum stale window so entries remain available for stale-if-error and
    /// stale-while-revalidate serving after <see cref="CacheEntry.ExpiresAt"/>.
    /// </summary>
    private static (byte[] Bytes, DistributedCacheEntryOptions Options) Serialize(CacheEntry entry)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(entry, CacheEntryJsonContext.Default.CacheEntry);

        long staleWindowSeconds = Math.Max(entry.StaleIfErrorSeconds, entry.StaleWhileRevalidateSeconds);
        DateTimeOffset absoluteExpiration = staleWindowSeconds > 0
            ? entry.ExpiresAt + TimeSpan.FromSeconds(staleWindowSeconds)
            : entry.ExpiresAt;

        return (bytes, new DistributedCacheEntryOptions { AbsoluteExpiration = absoluteExpiration });
    }
}
