using Stampede.Http.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using System.Net;

namespace Stampede.Http.Tests.Caching;

/// <summary>
/// Verifies that <see cref="MemoryCacheStore.Set"/> sets <c>AbsoluteExpiration</c> on the underlying
/// <see cref="IMemoryCache"/> entry so that stale entries are automatically evicted once all configured
/// stale windows (stale-if-error, stale-while-revalidate) have elapsed.
/// </summary>
public sealed class MemoryCacheStoreExpirationTests
{
    private static MemoryCacheStore CreateStore(CacheOptions? options = null) =>
        new(new MemoryCache(new MemoryCacheOptions()), options ?? new CacheOptions());

    private static CacheEntry BuildEntry(
        DateTimeOffset expiresAt,
        long staleIfErrorSeconds = 0,
        long staleWhileRevalidateSeconds = 0,
        string? eTag = null,
        DateTimeOffset? lastModified = null)
    {
        return new CacheEntry
        {
            StatusCode = (int)HttpStatusCode.OK,
            Body = [1, 2, 3],
            Headers = new Dictionary<string, string[]>(),
            ExpiresAt = expiresAt,
            StoredAt = DateTimeOffset.UtcNow,
            StaleIfErrorSeconds = staleIfErrorSeconds,
            StaleWhileRevalidateSeconds = staleWhileRevalidateSeconds,
            ETag = eTag,
            LastModified = lastModified
        };
    }

    // ── AbsoluteExpiration placement ─────────────────────────────────────────

    [Fact]
    public void Set_WithoutStaleWindow_PastExpiry_NoValidator_EntryNotRetained()
    {
        MemoryCacheStore store = CreateStore();
        // ExpiresAt in the past, no stale window and no validator → the entry can neither be served
        // (it is already stale) nor revalidated (nothing to condition on), so it must not be retained.
        CacheEntry entry = BuildEntry(expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1));

        store.Set("key1", entry);

        bool found = store.TryGetValue("key1", out _);
        found.Should().BeFalse("an entry that can never be served nor revalidated must not occupy the cache");
    }

    [Fact]
    public void Set_WithoutStaleWindow_PastExpiry_WithValidator_RetainedByGrace()
    {
        MemoryCacheStore store = CreateStore();
        // Same as above but with an ETag: the revalidation grace period keeps it available so the next
        // request can send a conditional If-None-Match instead of a full refetch.
        CacheEntry entry = BuildEntry(expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1), eTag: "\"v1\"");

        store.Set("key1-validator", entry);

        bool found = store.TryGetValue("key1-validator", out _);
        found.Should().BeTrue("a validator-carrying entry is retained by RevalidationGraceSeconds for conditional revalidation");
    }

    [Fact]
    public void Set_UnusableEntry_RemovesPreviousRepresentation()
    {
        MemoryCacheStore store = CreateStore();
        store.Set("superseded", BuildEntry(expiresAt: DateTimeOffset.UtcNow.AddMinutes(5)));

        // Storing an unusable representation must not silently leave the previous one behind.
        store.Set("superseded", BuildEntry(expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1)));

        bool found = store.TryGetValue("superseded", out _);
        found.Should().BeFalse("a superseded representation must not keep being served after an unusable store");
    }

    [Fact]
    public void Set_WithStaleIfError_EntryPersistsBeyondExpiresAt()
    {
        MemoryCacheStore store = CreateStore();
        // ExpiresAt is in the past but the stale-if-error window extends into the future
        CacheEntry entry = BuildEntry(
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1),
            staleIfErrorSeconds: 3600);

        store.Set("key2", entry);

        bool found = store.TryGetValue("key2", out CacheEntry? retrieved);
        found.Should().BeTrue("stale-if-error window extends the memory eviction deadline");
        retrieved.Should().NotBeNull();
    }

    [Fact]
    public void Set_WithStaleWhileRevalidate_EntryPersistsBeyondExpiresAt()
    {
        MemoryCacheStore store = CreateStore();
        CacheEntry entry = BuildEntry(
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1),
            staleWhileRevalidateSeconds: 3600);

        store.Set("key3", entry);

        bool found = store.TryGetValue("key3", out _);
        found.Should().BeTrue("stale-while-revalidate window extends the memory eviction deadline");
    }

    [Fact]
    public void Set_LargerStaleWindow_UsedAsDeadline()
    {
        MemoryCacheStore store = CreateStore();
        // stale-if-error=10, stale-while-revalidate=3600 → max = 3600
        CacheEntry entry = BuildEntry(
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1),
            staleIfErrorSeconds: 10,
            staleWhileRevalidateSeconds: 3600);

        store.Set("key4", entry);

        bool found = store.TryGetValue("key4", out _);
        found.Should().BeTrue("the larger of the two stale windows should be used as the eviction deadline");
    }

    [Fact]
    public void Set_BothStaleWindowsZero_PastExpiry_NoValidator_EntryNotRetained()
    {
        MemoryCacheStore store = CreateStore();
        // Both stale windows are zero, ExpiresAt is in the past and there is no validator → the entry has
        // no usable window at all and must not be retained.
        CacheEntry entry = BuildEntry(
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1),
            staleIfErrorSeconds: 0,
            staleWhileRevalidateSeconds: 0);

        store.Set("key5", entry);

        bool found = store.TryGetValue("key5", out _);
        found.Should().BeFalse("an entry with no freshness, no stale window and no validator must not occupy the cache");
    }

    [Fact]
    public void Set_MaxAgeZeroWithoutValidator_NotRetainedEvenWithoutSizeLimit()
    {
        // Regression: without MaxCacheSize the IMemoryCache has no SizeLimit and therefore no LRU eviction,
        // so an entry stored with no expiration would live for the lifetime of the process.
        MemoryCacheStore store = CreateStore();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        for (int i = 0; i < 100; i++)
        {
            store.Set($"max-age-zero-{i}", new CacheEntry
            {
                StatusCode = (int)HttpStatusCode.OK,
                Body = [1, 2, 3],
                Headers = new Dictionary<string, string[]>(),
                ExpiresAt = now,
                StoredAt = now
            });
        }

        for (int i = 0; i < 100; i++)
        {
            store.TryGetValue($"max-age-zero-{i}", out _)
                .Should().BeFalse("max-age=0 responses without a validator must not accumulate in memory");
        }
    }

    // ── Revalidation grace (real clock — IMemoryCache evicts on its own clock)

    [Fact]
    public async Task Set_WithETag_DefaultGrace_EntrySurvivesFreshnessExpiry()
    {
        MemoryCacheStore store = CreateStore();
        // 1-second freshness TTL, validator present, no stale windows.
        // Without RevalidationGraceSeconds the entry would be physically evicted at expiry,
        // making conditional revalidation impossible (the bug found by the demo app's Demo 2).
        CacheEntry entry = BuildEntry(
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(1.2),
            eTag: "\"v1\"");

        store.Set("grace-etag", entry);

        await Task.Delay(1500, TestContext.Current.CancellationToken);

        bool found = store.TryGetValue("grace-etag", out _);
        found.Should().BeTrue("an entry with an ETag must be retained beyond freshness expiry so If-None-Match revalidation can be sent");
    }

    [Fact]
    public async Task Set_WithLastModified_DefaultGrace_EntrySurvivesFreshnessExpiry()
    {
        MemoryCacheStore store = CreateStore();
        CacheEntry entry = BuildEntry(
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(1.2),
            lastModified: DateTimeOffset.UtcNow.AddMinutes(-10));

        store.Set("grace-lm", entry);

        await Task.Delay(1500, TestContext.Current.CancellationToken);

        bool found = store.TryGetValue("grace-lm", out _);
        found.Should().BeTrue("an entry with Last-Modified must be retained beyond freshness expiry so If-Modified-Since revalidation can be sent");
    }

    [Fact]
    public async Task Set_WithETag_ZeroGrace_EntryEvictedAtFreshnessExpiry()
    {
        MemoryCacheStore store = CreateStore(new CacheOptions { RevalidationGraceSeconds = 0 });
        CacheEntry entry = BuildEntry(
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(1.2),
            eTag: "\"v1\"");

        store.Set("no-grace", entry);

        await Task.Delay(1500, TestContext.Current.CancellationToken);

        bool found = store.TryGetValue("no-grace", out _);
        found.Should().BeFalse("RevalidationGraceSeconds = 0 restores eviction exactly at expiry");
    }

    [Fact]
    public async Task Set_WithoutValidator_GraceDoesNotApply_EntryEvictedAtFreshnessExpiry()
    {
        MemoryCacheStore store = CreateStore();
        // No ETag / Last-Modified → nothing to revalidate with, so the grace period must not apply.
        CacheEntry entry = BuildEntry(expiresAt: DateTimeOffset.UtcNow.AddSeconds(1.2));

        store.Set("no-validator", entry);

        await Task.Delay(1500, TestContext.Current.CancellationToken);

        bool found = store.TryGetValue("no-validator", out _);
        found.Should().BeFalse("entries without a validator cannot be revalidated and must not be retained by the grace period");
    }

    // ── Fresh entries still retrievable ──────────────────────────────────────

    [Fact]
    public void Set_FreshEntry_IsRetrievable()
    {
        MemoryCacheStore store = CreateStore();
        CacheEntry entry = BuildEntry(expiresAt: DateTimeOffset.UtcNow.AddMinutes(5));

        store.Set("fresh-key", entry);

        bool found = store.TryGetValue("fresh-key", out CacheEntry? retrieved);
        found.Should().BeTrue();
        retrieved.Should().NotBeNull();
    }

    [Fact]
    public void Remove_AfterSet_EntryNoLongerRetrievable()
    {
        MemoryCacheStore store = CreateStore();
        CacheEntry entry = BuildEntry(expiresAt: DateTimeOffset.UtcNow.AddMinutes(5));

        store.Set("remove-key", entry);
        store.Remove("remove-key");

        bool found = store.TryGetValue("remove-key", out _);
        found.Should().BeFalse();
    }
}
