using Coalesce.Http.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using System.Net;

namespace Coalesce.Http.Tests.Caching;

/// <summary>
/// Verifies programmatic cache invalidation via <see cref="ICacheStore.Remove"/>.
/// </summary>
public sealed class CacheInvalidationTests
{
    private readonly ICacheStore _cache;
    private readonly DefaultCacheKeyBuilder _keyBuilder;
    private readonly CacheOptions _options;

    public CacheInvalidationTests()
    {
        _cache = new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions()));
        _keyBuilder = new DefaultCacheKeyBuilder();
        _options = new CacheOptions { DefaultTtl = TimeSpan.FromMinutes(5) };
    }

    [Fact]
    public void Remove_ExistingKey_RemovesEntry()
    {
        string key = _keyBuilder.Build(new HttpRequestMessage(HttpMethod.Get, "https://api.test/item"));

        _cache.Set(key, CreateEntry());

        _cache.TryGetValue(key, out CacheEntry? before).Should().BeTrue();
        before.Should().NotBeNull();

        _cache.Remove(key);

        _cache.TryGetValue(key, out CacheEntry? after).Should().BeFalse();
        after.Should().BeNull();
    }

    [Fact]
    public void Remove_NonExistentKey_DoesNotThrow()
    {
        var act = () => _cache.Remove("non-existent-key");

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Remove_InvalidatesMiddlewareCacheEntry()
    {
        int callCount = 0;
        StubTransport stub = new(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"response-{callCount}") };
        });
        CachingMiddleware middleware = new(_cache, _keyBuilder, _options) { InnerHandler = stub };
        HttpMessageInvoker invoker = new(middleware);

        // Populate cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/invalidate"), CancellationToken.None);
        callCount.Should().Be(1);

        // Verify cache hit
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/invalidate"), CancellationToken.None);
        callCount.Should().Be(1, "second request should be served from cache");

        // Invalidate
        string key = _keyBuilder.Build(new HttpRequestMessage(HttpMethod.Get, "https://api.test/invalidate"));
        _cache.Remove(key);

        // Next request should miss the cache
        HttpResponseMessage response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/invalidate"), CancellationToken.None);
        callCount.Should().Be(2, "after invalidation, the middleware must fetch from origin");

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Be("response-2");
    }

    [Fact]
    public void Remove_OnlyAffectsTargetKey()
    {
        string key1 = _keyBuilder.Build(new HttpRequestMessage(HttpMethod.Get, "https://api.test/a"));
        string key2 = _keyBuilder.Build(new HttpRequestMessage(HttpMethod.Get, "https://api.test/b"));

        _cache.Set(key1, CreateEntry());
        _cache.Set(key2, CreateEntry());

        _cache.Remove(key1);

        _cache.TryGetValue(key1, out _).Should().BeFalse("removed key should be gone");
        _cache.TryGetValue(key2, out _).Should().BeTrue("other keys should be unaffected");
    }

    private static CacheEntry CreateEntry() => new()
    {
        StatusCode = 200,
        Body = "body"u8.ToArray(),
        Headers = new Dictionary<string, string[]>(),
        StoredAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5)
    };

    private sealed class StubTransport(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
