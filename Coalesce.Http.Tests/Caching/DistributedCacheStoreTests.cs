using Coalesce.Http.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using System.Net;

namespace Coalesce.Http.Tests.Caching;

public sealed class DistributedCacheStoreTests
{
    private static DistributedCacheStore CreateStore()
    {
        IDistributedCache cache = new MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        return new DistributedCacheStore(cache);
    }

    private static CacheEntry BuildEntry(
        string body = "hello",
        DateTimeOffset? expiresAt = null,
        string? eTag = null,
        DateTimeOffset? lastModified = null,
        string[]? varyFields = null,
        bool mustRevalidate = false,
        long staleIfError = 0,
        long staleWhileRevalidate = 0)
    {
        return new CacheEntry
        {
            StatusCode = (int)HttpStatusCode.OK,
            Body = System.Text.Encoding.UTF8.GetBytes(body),
            Headers = new Dictionary<string, string[]>
            {
                ["Content-Type"] = ["text/plain"],
                ["Cache-Control"] = ["max-age=60"]
            },
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(5),
            StoredAt = DateTimeOffset.UtcNow,
            ETag = eTag,
            LastModified = lastModified,
            VaryFields = varyFields ?? [],
            VaryValues = new Dictionary<string, string[]>(),
            StaleIfErrorSeconds = staleIfError,
            StaleWhileRevalidateSeconds = staleWhileRevalidate,
            MustRevalidate = mustRevalidate
        };
    }

    // ── TryGetValue ──────────────────────────────────────────────────────────

    [Fact]
    public void TryGetValue_WhenKeyNotPresent_ReturnsFalse()
    {
        DistributedCacheStore store = CreateStore();

        bool found = store.TryGetValue("missing-key", out CacheEntry? entry);

        found.Should().BeFalse();
        entry.Should().BeNull();
    }

    [Fact]
    public void TryGetValue_AfterSet_ReturnsTrueAndRestoresEntry()
    {
        DistributedCacheStore store = CreateStore();
        CacheEntry original = BuildEntry("round-trip body");

        store.Set("key1", original);
        bool found = store.TryGetValue("key1", out CacheEntry? restored);

        found.Should().BeTrue();
        restored.Should().NotBeNull();
        restored!.StatusCode.Should().Be(200);
        System.Text.Encoding.UTF8.GetString(restored.Body).Should().Be("round-trip body");
    }

    // ── Serialization round-trip ─────────────────────────────────────────────

    [Fact]
    public void RoundTrip_PreservesAllScalarFields()
    {
        DistributedCacheStore store = CreateStore();
        DateTimeOffset expires = DateTimeOffset.UtcNow.AddSeconds(120);
        DateTimeOffset stored = DateTimeOffset.UtcNow;
        DateTimeOffset lastModified = DateTimeOffset.UtcNow.AddMinutes(-10);

        CacheEntry original = new()
        {
            StatusCode = 200,
            Body = [1, 2, 3, 4, 5],
            Headers = new Dictionary<string, string[]> { ["X-Custom"] = ["a", "b"] },
            ExpiresAt = expires,
            StoredAt = stored,
            ETag = "\"abc123\"",
            LastModified = lastModified,
            VaryFields = ["Accept-Encoding", "Accept-Language"],
            VaryValues = new Dictionary<string, string[]> { ["Accept-Encoding"] = ["gzip"] },
            StaleIfErrorSeconds = 30,
            StaleWhileRevalidateSeconds = 15,
            MustRevalidate = true
        };

        store.Set("full", original);
        bool found = store.TryGetValue("full", out CacheEntry? restored);

        found.Should().BeTrue();
        restored!.StatusCode.Should().Be(200);
        restored.Body.Should().Equal([1, 2, 3, 4, 5]);
        restored.Headers.Should().ContainKey("X-Custom")
            .WhoseValue.Should().Equal("a", "b");
        restored.ExpiresAt.Should().BeCloseTo(expires, TimeSpan.FromMilliseconds(1));
        restored.StoredAt.Should().BeCloseTo(stored, TimeSpan.FromMilliseconds(1));
        restored.ETag.Should().Be("\"abc123\"");
        restored.LastModified.Should().BeCloseTo(lastModified, TimeSpan.FromMilliseconds(1));
        restored.VaryFields.Should().Equal("Accept-Encoding", "Accept-Language");
        restored.VaryValues.Should().ContainKey("Accept-Encoding")
            .WhoseValue.Should().Equal("gzip");
        restored.StaleIfErrorSeconds.Should().Be(30);
        restored.StaleWhileRevalidateSeconds.Should().Be(15);
        restored.MustRevalidate.Should().BeTrue();
    }

    [Fact]
    public void RoundTrip_NullOptionalFields_ArePreserved()
    {
        DistributedCacheStore store = CreateStore();
        CacheEntry original = BuildEntry(eTag: null, lastModified: null, mustRevalidate: false);

        store.Set("nulls", original);
        store.TryGetValue("nulls", out CacheEntry? restored);

        restored!.ETag.Should().BeNull();
        restored.LastModified.Should().BeNull();
        restored.MustRevalidate.Should().BeFalse();
    }

    [Fact]
    public void RoundTrip_EmptyBody_IsPreserved()
    {
        DistributedCacheStore store = CreateStore();
        CacheEntry original = new()
        {
            StatusCode = 200,
            Body = [],
            Headers = new Dictionary<string, string[]>(),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            StoredAt = DateTimeOffset.UtcNow,
        };

        store.Set("empty-body", original);
        store.TryGetValue("empty-body", out CacheEntry? restored);

        restored!.Body.Should().BeEmpty();
    }

    [Fact]
    public void RoundTrip_MultipleHeaders_ArePreserved()
    {
        DistributedCacheStore store = CreateStore();
        CacheEntry original = BuildEntry();
        // Override headers with richer set
        CacheEntry rich = original with
        {
            Headers = new Dictionary<string, string[]>
            {
                ["Content-Type"] = ["application/json; charset=utf-8"],
                ["Cache-Control"] = ["max-age=300", "public"],
                ["X-Multi"] = ["v1", "v2", "v3"]
            }
        };

        store.Set("multi-headers", rich);
        store.TryGetValue("multi-headers", out CacheEntry? restored);

        restored!.Headers.Should().HaveCount(3);
        restored.Headers["X-Multi"].Should().Equal("v1", "v2", "v3");
    }

    // ── Remove ───────────────────────────────────────────────────────────────

    [Fact]
    public void Remove_ExistingKey_EntryIsNoLongerReturned()
    {
        DistributedCacheStore store = CreateStore();
        store.Set("to-remove", BuildEntry());

        store.Remove("to-remove");

        bool found = store.TryGetValue("to-remove", out _);
        found.Should().BeFalse();
    }

    [Fact]
    public void Remove_NonExistentKey_DoesNotThrow()
    {
        DistributedCacheStore store = CreateStore();

        Action act = () => store.Remove("ghost-key");

        act.Should().NotThrow();
    }

    // ── TTL / eviction ───────────────────────────────────────────────────────

    [Fact]
    public async Task Set_WithPastExpiry_EntryIsNotReturnedAfterEviction()
    {
        // Use a very short TTL so MemoryDistributedCache evicts the entry
        IDistributedCache cache = new MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        DistributedCacheStore store = new(cache);

        CacheEntry expired = BuildEntry(expiresAt: DateTimeOffset.UtcNow.AddMilliseconds(50));
        store.Set("expiring", expired);

        // Verify it's present immediately
        store.TryGetValue("expiring", out _).Should().BeTrue();

        // Wait for the backing MemoryDistributedCache to evict
        await Task.Delay(200);

        bool found = store.TryGetValue("expiring", out _);
        found.Should().BeFalse("the entry TTL has elapsed");
    }

    // ── Isolation ────────────────────────────────────────────────────────────

    [Fact]
    public void TwoDistinctKeys_AreStoredAndRetrievedIndependently()
    {
        DistributedCacheStore store = CreateStore();
        CacheEntry a = BuildEntry("value-a");
        CacheEntry b = BuildEntry("value-b");

        store.Set("key-a", a);
        store.Set("key-b", b);

        store.TryGetValue("key-a", out CacheEntry? restoredA);
        store.TryGetValue("key-b", out CacheEntry? restoredB);

        System.Text.Encoding.UTF8.GetString(restoredA!.Body).Should().Be("value-a");
        System.Text.Encoding.UTF8.GetString(restoredB!.Body).Should().Be("value-b");
    }
}
