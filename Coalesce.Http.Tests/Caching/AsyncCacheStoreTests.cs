using Coalesce.Http.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using System.Net;

namespace Coalesce.Http.Tests.Caching;

/// <summary>
/// Verifies the async <see cref="ICacheStore"/> methods:
/// <list type="bullet">
///   <item><see cref="ICacheStore.GetAsync"/> / <see cref="ICacheStore.SetAsync"/> / <see cref="ICacheStore.RemoveAsync"/> on <see cref="DistributedCacheStore"/> (true async I/O)</item>
///   <item>Default interface method implementations on <see cref="MemoryCacheStore"/> (sync delegation)</item>
/// </list>
/// </summary>
public sealed class AsyncCacheStoreTests
{
    // ── DistributedCacheStore async overrides ─────────────────────────────────

    private static DistributedCacheStore CreateDistributedStore()
    {
        IDistributedCache cache = new MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        return new DistributedCacheStore(cache);
    }

    private static MemoryCacheStore CreateMemoryStore() =>
        new(new MemoryCache(new MemoryCacheOptions()));

    private static CacheEntry BuildEntry(
        string body = "hello",
        long staleIfError = 0,
        long staleWhileRevalidate = 0)
    {
        return new CacheEntry
        {
            StatusCode = (int)HttpStatusCode.OK,
            Body = System.Text.Encoding.UTF8.GetBytes(body),
            Headers = new Dictionary<string, string[]> { ["Content-Type"] = ["text/plain"] },
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            StoredAt = DateTimeOffset.UtcNow,
            StaleIfErrorSeconds = staleIfError,
            StaleWhileRevalidateSeconds = staleWhileRevalidate
        };
    }

    [Fact]
    public async Task Distributed_GetAsync_MissingKey_ReturnsNull()
    {
        DistributedCacheStore store = CreateDistributedStore();

        CacheEntry? result = await store.GetAsync("no-such-key");

        result.Should().BeNull();
    }

    [Fact]
    public async Task Distributed_SetAsync_ThenGetAsync_ReturnsEntry()
    {
        DistributedCacheStore store = CreateDistributedStore();
        CacheEntry entry = BuildEntry("async-body");

        await store.SetAsync("key1", entry);
        CacheEntry? retrieved = await store.GetAsync("key1");

        retrieved.Should().NotBeNull();
        System.Text.Encoding.UTF8.GetString(retrieved!.Body).Should().Be("async-body");
        retrieved.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task Distributed_RemoveAsync_RemovesEntry()
    {
        DistributedCacheStore store = CreateDistributedStore();
        CacheEntry entry = BuildEntry("to-remove");

        await store.SetAsync("key2", entry);
        await store.RemoveAsync("key2");
        CacheEntry? afterRemove = await store.GetAsync("key2");

        afterRemove.Should().BeNull("RemoveAsync should delete the entry");
    }

    [Fact]
    public async Task Distributed_SetAsync_SyncGet_RoundTrips()
    {
        DistributedCacheStore store = CreateDistributedStore();
        CacheEntry entry = BuildEntry("sync-async-interop");

        await store.SetAsync("key3", entry);
        bool found = store.TryGetValue("key3", out CacheEntry? retrieved);

        found.Should().BeTrue();
        System.Text.Encoding.UTF8.GetString(retrieved!.Body).Should().Be("sync-async-interop");
    }

    [Fact]
    public async Task Distributed_SyncSet_AsyncGet_RoundTrips()
    {
        DistributedCacheStore store = CreateDistributedStore();
        CacheEntry entry = BuildEntry("sync-then-async");

        store.Set("key4", entry);
        CacheEntry? retrieved = await store.GetAsync("key4");

        retrieved.Should().NotBeNull();
        System.Text.Encoding.UTF8.GetString(retrieved!.Body).Should().Be("sync-then-async");
    }

    // ── MemoryCacheStore default interface method implementations ─────────────

    [Fact]
    public async Task Memory_GetAsync_MissingKey_ReturnsNull()
    {
        ICacheStore store = CreateMemoryStore();

        CacheEntry? result = await store.GetAsync("no-such-key");

        result.Should().BeNull();
    }

    [Fact]
    public async Task Memory_SetAsync_ThenGetAsync_ReturnsEntry()
    {
        ICacheStore store = CreateMemoryStore();
        CacheEntry entry = BuildEntry("memory-async");

        await store.SetAsync("m1", entry);
        CacheEntry? retrieved = await store.GetAsync("m1");

        retrieved.Should().NotBeNull();
        System.Text.Encoding.UTF8.GetString(retrieved!.Body).Should().Be("memory-async");
    }

    [Fact]
    public async Task Memory_RemoveAsync_RemovesEntry()
    {
        ICacheStore store = CreateMemoryStore();
        CacheEntry entry = BuildEntry("to-remove");

        await store.SetAsync("m2", entry);
        await store.RemoveAsync("m2");
        CacheEntry? afterRemove = await store.GetAsync("m2");

        afterRemove.Should().BeNull("RemoveAsync default impl should call Remove");
    }

    [Fact]
    public async Task Memory_AsyncSetAsync_SyncTryGetValue_Consistent()
    {
        MemoryCacheStore store = CreateMemoryStore();
        CacheEntry entry = BuildEntry("interop");

        await ((ICacheStore)store).SetAsync("m3", entry);
        bool found = store.TryGetValue("m3", out CacheEntry? retrieved);

        found.Should().BeTrue("SetAsync default should delegate to sync Set");
        retrieved.Should().NotBeNull();
    }
}
