using Coalesce.Http.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using System.Net;

namespace Coalesce.Http.Tests.Caching;

/// <summary>
/// Verifies <see cref="CacheOptions.NormalizeQueryParameters"/> and the corresponding
/// <see cref="DefaultCacheKeyBuilder"/> normalization logic.
/// </summary>
public sealed class QueryNormalizationTests
{
    // ── DefaultCacheKeyBuilder unit tests ─────────────────────────────────────

    [Fact]
    public void KeyBuilder_NormalizationOff_DifferentOrderProducesDifferentKey()
    {
        DefaultCacheKeyBuilder builder = new(normalizeQueryParameters: false);

        using HttpRequestMessage r1 = new(HttpMethod.Get, "https://api.test/items?a=1&b=2");
        using HttpRequestMessage r2 = new(HttpMethod.Get, "https://api.test/items?b=2&a=1");

        builder.Build(r1).Should().NotBe(builder.Build(r2),
            "without normalization, different parameter ordering must produce different keys");
    }

    [Fact]
    public void KeyBuilder_NormalizationOn_DifferentOrderProducesSameKey()
    {
        DefaultCacheKeyBuilder builder = new(normalizeQueryParameters: true);

        using HttpRequestMessage r1 = new(HttpMethod.Get, "https://api.test/items?a=1&b=2");
        using HttpRequestMessage r2 = new(HttpMethod.Get, "https://api.test/items?b=2&a=1");

        builder.Build(r1).Should().Be(builder.Build(r2),
            "with normalization enabled, parameter order must not affect the cache key");
    }

    [Fact]
    public void KeyBuilder_NormalizationOn_SameOrderProducesSameKey()
    {
        DefaultCacheKeyBuilder builder = new(normalizeQueryParameters: true);

        using HttpRequestMessage r1 = new(HttpMethod.Get, "https://api.test/items?page=2&size=10");
        using HttpRequestMessage r2 = new(HttpMethod.Get, "https://api.test/items?page=2&size=10");

        builder.Build(r1).Should().Be(builder.Build(r2));
    }

    [Fact]
    public void KeyBuilder_NormalizationOn_ThreeParameters_SortedAlphabetically()
    {
        DefaultCacheKeyBuilder builder = new(normalizeQueryParameters: true);

        using HttpRequestMessage r1 = new(HttpMethod.Get, "https://api.test/items?c=3&a=1&b=2");
        using HttpRequestMessage r2 = new(HttpMethod.Get, "https://api.test/items?a=1&b=2&c=3");

        builder.Build(r1).Should().Be(builder.Build(r2),
            "alphabetical sort must produce identical keys regardless of insertion order");
    }

    [Fact]
    public void KeyBuilder_NormalizationOn_NoQueryString_Unchanged()
    {
        DefaultCacheKeyBuilder builder = new(normalizeQueryParameters: true);

        using HttpRequestMessage r = new(HttpMethod.Get, "https://api.test/items");
        string key = builder.Build(r);

        key.Should().Be("GET:https://api.test/items",
            "URIs without a query string should not be modified by normalization");
    }

    [Fact]
    public void KeyBuilder_NormalizationOn_DifferentValues_ProduceDifferentKeys()
    {
        DefaultCacheKeyBuilder builder = new(normalizeQueryParameters: true);

        using HttpRequestMessage r1 = new(HttpMethod.Get, "https://api.test/items?page=1");
        using HttpRequestMessage r2 = new(HttpMethod.Get, "https://api.test/items?page=2");

        builder.Build(r1).Should().NotBe(builder.Build(r2),
            "normalization must only sort parameters, never merge distinct values");
    }

    [Fact]
    public void KeyBuilder_DefaultConstructor_NormalizationIsOff()
    {
        DefaultCacheKeyBuilder builder = new();

        using HttpRequestMessage r1 = new(HttpMethod.Get, "https://api.test/items?b=2&a=1");
        using HttpRequestMessage r2 = new(HttpMethod.Get, "https://api.test/items?a=1&b=2");

        // Default (false) — ordering matters
        builder.Build(r1).Should().NotBe(builder.Build(r2),
            "default constructor must produce a key builder with normalization disabled");
    }

    // ── CachingMiddleware integration ─────────────────────────────────────────

    [Fact]
    public async Task Middleware_NormalizationOn_DifferentOrder_HitsCacheOnSecondRequest()
    {
        ICacheStore store = new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions()));
        DefaultCacheKeyBuilder builder = new(normalizeQueryParameters: true);
        CacheOptions options = new() { DefaultTtl = TimeSpan.FromMinutes(5) };

        int callCount = 0;
        StubTransport stub = new(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data") };
        });

        CachingMiddleware middleware = new(store, builder, options) { InnerHandler = stub };
        HttpMessageInvoker invoker = new(middleware);

        // First request — populates cache
        _ = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.test/items?a=1&b=2"),
            CancellationToken.None);

        // Second request — same params in different order; should hit cache
        _ = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.test/items?b=2&a=1"),
            CancellationToken.None);

        callCount.Should().Be(1, "normalized keys should produce a cache hit on the reordered request");
    }

    [Fact]
    public async Task Middleware_NormalizationOff_DifferentOrder_MissesCacheOnSecondRequest()
    {
        ICacheStore store = new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions()));
        DefaultCacheKeyBuilder builder = new(normalizeQueryParameters: false);
        CacheOptions options = new() { DefaultTtl = TimeSpan.FromMinutes(5) };

        int callCount = 0;
        StubTransport stub = new(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data") };
        });

        CachingMiddleware middleware = new(store, builder, options) { InnerHandler = stub };
        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.test/items?a=1&b=2"),
            CancellationToken.None);

        _ = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.test/items?b=2&a=1"),
            CancellationToken.None);

        callCount.Should().Be(2, "without normalization the reordered request is a cache miss");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class StubTransport(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
