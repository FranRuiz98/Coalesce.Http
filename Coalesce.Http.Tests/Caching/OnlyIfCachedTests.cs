using Coalesce.Http.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Http.Headers;

namespace Coalesce.Http.Tests.Caching;

/// <summary>
/// Verifies <c>Cache-Control: only-if-cached</c> behaviour (RFC 9111 §5.2.1.7):
/// when there is no usable cache entry, the middleware must return 504 Gateway Timeout
/// instead of contacting the origin.
/// </summary>
public sealed class OnlyIfCachedTests
{
    private readonly ICacheStore _cache;
    private readonly DefaultCacheKeyBuilder _keyBuilder;

    public OnlyIfCachedTests()
    {
        _cache = new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions()));
        _keyBuilder = new DefaultCacheKeyBuilder();
    }

    private (CachingMiddleware middleware, StubTransport stub) BuildPipeline(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        CacheOptions? options = null)
    {
        options ??= new CacheOptions { DefaultTtl = TimeSpan.FromMinutes(5) };
        StubTransport stub = new(handler);
        CachingMiddleware middleware = new(_cache, _keyBuilder, options) { InnerHandler = stub };
        return (middleware, stub);
    }

    private static HttpRequestMessage OnlyIfCachedReq(string url) =>
        new(HttpMethod.Get, url)
        {
            Headers = { CacheControl = new CacheControlHeaderValue { OnlyIfCached = true } }
        };

    // ── Complete miss → 504 ───────────────────────────────────────────────────

    [Fact]
    public async Task Miss_WithOnlyIfCached_Returns504()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") };
        });

        HttpMessageInvoker invoker = new(middleware);

        HttpResponseMessage response = await invoker.SendAsync(
            OnlyIfCachedReq("https://api.test/oc-miss"), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout,
            "only-if-cached with no cache entry must return 504");
        callCount.Should().Be(0, "origin must not be contacted");
    }

    // ── Stale entry (no validator) with only-if-cached → 504 ─────────────────

    [Fact]
    public async Task StaleEntryNoValidator_WithOnlyIfCached_Returns504()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("stale-body") };
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
            return r;
        });

        HttpMessageInvoker invoker = new(middleware);

        // Populate the cache (stale immediately, no ETag)
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/oc-stale"), CancellationToken.None);
        callCount.Should().Be(1);

        // only-if-cached with a stale entry that has no validator → 504
        HttpResponseMessage response = await invoker.SendAsync(
            OnlyIfCachedReq("https://api.test/oc-stale"), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout,
            "only-if-cached must return 504 even for stale entries that would require forwarding");
        callCount.Should().Be(1, "origin must not be contacted");
    }

    // ── Stale entry with validator and only-if-cached → 504 ──────────────────

    [Fact]
    public async Task StaleEntryWithValidator_WithOnlyIfCached_Returns504()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
            r.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
            return r;
        });

        HttpMessageInvoker invoker = new(middleware);

        // Populate cache (stale with ETag)
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/oc-stale-etag"), CancellationToken.None);
        callCount.Should().Be(1);

        // only-if-cached: stale entry has a validator but we must not hit origin
        HttpResponseMessage response = await invoker.SendAsync(
            OnlyIfCachedReq("https://api.test/oc-stale-etag"), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout,
            "only-if-cached must return 504 for stale entries requiring revalidation");
        callCount.Should().Be(1, "origin must not be contacted for revalidation");
    }

    // ── Fresh entry with only-if-cached → served from cache ──────────────────

    [Fact]
    public async Task FreshEntry_WithOnlyIfCached_ServedFromCache()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("fresh-body") };
        });

        HttpMessageInvoker invoker = new(middleware);

        // Populate cache with a fresh entry
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/oc-fresh"), CancellationToken.None);

        // only-if-cached: fresh hit → 200 from cache, no origin contact
        HttpResponseMessage response = await invoker.SendAsync(
            OnlyIfCachedReq("https://api.test/oc-fresh"), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "only-if-cached with a fresh entry must serve from cache");
        callCount.Should().Be(1, "origin must not be contacted again");
    }

    // ── Absent directive → normal forwarding ─────────────────────────────────

    [Fact]
    public async Task AbsentDirective_MissForwardedNormally()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") };
        });

        HttpMessageInvoker invoker = new(middleware);

        HttpResponseMessage response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.test/oc-absent"), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        callCount.Should().Be(1, "without only-if-cached, a miss must be forwarded to origin");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class StubTransport(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
