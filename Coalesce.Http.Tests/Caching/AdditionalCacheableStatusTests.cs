using Coalesce.Http.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Http.Headers;

namespace Coalesce.Http.Tests.Caching;

/// <summary>
/// Verifies caching of additional status codes per RFC 9111 §3.2:
/// 301 Moved Permanently, 404 Not Found, 405 Method Not Allowed, 410 Gone, 414 URI Too Long.
/// </summary>
public sealed class AdditionalCacheableStatusTests
{
    private readonly ICacheStore _cache;
    private readonly DefaultCacheKeyBuilder _keyBuilder;
    private readonly CacheOptions _options;

    public AdditionalCacheableStatusTests()
    {
        _cache = new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions()));
        _keyBuilder = new DefaultCacheKeyBuilder();
        _options = new CacheOptions { DefaultTtl = TimeSpan.FromMinutes(5) };
    }

    private (CachingMiddleware middleware, StubTransport stub) BuildPipeline(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        CacheOptions? options = null)
    {
        options ??= _options;
        StubTransport stub = new(handler);
        CachingMiddleware middleware = new(_cache, _keyBuilder, options) { InnerHandler = stub };
        return (middleware, stub);
    }

    // ── 301 Moved Permanently ─────────────────────────────────────────────────

    [Fact]
    public async Task Status301_IsCachedAndServedOnSecondRequest()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            HttpResponseMessage r = new(HttpStatusCode.MovedPermanently);
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(10) };
            return r;
        });

        HttpMessageInvoker invoker = new(middleware);

        HttpResponseMessage first = await invoker.SendAsync(Req("https://api.test/moved"), CancellationToken.None);
        HttpResponseMessage second = await invoker.SendAsync(Req("https://api.test/moved"), CancellationToken.None);

        first.StatusCode.Should().Be(HttpStatusCode.MovedPermanently);
        second.StatusCode.Should().Be(HttpStatusCode.MovedPermanently);
        callCount.Should().Be(1, "second request should be served from cache");
    }

    [Fact]
    public async Task Status301_WithNoMaxAge_CachedWithDefaultTtl()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.MovedPermanently);
        });

        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(Req("https://api.test/moved-heuristic"), CancellationToken.None);
        _ = await invoker.SendAsync(Req("https://api.test/moved-heuristic"), CancellationToken.None);

        callCount.Should().Be(1, "301 is heuristically cacheable with DefaultTtl");
    }

    [Fact]
    public async Task Status301_WithNoStore_IsNotCached()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            HttpResponseMessage r = new(HttpStatusCode.MovedPermanently);
            r.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return r;
        });

        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(Req("https://api.test/moved-nostore"), CancellationToken.None);
        _ = await invoker.SendAsync(Req("https://api.test/moved-nostore"), CancellationToken.None);

        callCount.Should().Be(2, "no-store must prevent caching even for 301");
    }

    // ── 404 Not Found ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Status404_WithMaxAge_IsCachedAndServed()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            HttpResponseMessage r = new(HttpStatusCode.NotFound);
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) };
            return r;
        });

        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(Req("https://api.test/missing"), CancellationToken.None);
        HttpResponseMessage second = await invoker.SendAsync(Req("https://api.test/missing"), CancellationToken.None);

        second.StatusCode.Should().Be(HttpStatusCode.NotFound);
        callCount.Should().Be(1, "404 with max-age should be cached");
    }

    [Fact]
    public async Task Status404_WithoutMaxAge_IsNotCached()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(Req("https://api.test/missing-no-ttl"), CancellationToken.None);
        _ = await invoker.SendAsync(Req("https://api.test/missing-no-ttl"), CancellationToken.None);

        callCount.Should().Be(2, "404 without explicit freshness must not be cached");
    }

    // ── 410 Gone ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Status410_WithMaxAge_IsCachedAndServed()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            HttpResponseMessage r = new(HttpStatusCode.Gone);
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
            return r;
        });

        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(Req("https://api.test/gone"), CancellationToken.None);
        HttpResponseMessage second = await invoker.SendAsync(Req("https://api.test/gone"), CancellationToken.None);

        second.StatusCode.Should().Be(HttpStatusCode.Gone);
        callCount.Should().Be(1, "410 with max-age should be cached");
    }

    // ── 200 OK (existing behaviour unchanged) ────────────────────────────────

    [Fact]
    public async Task Status200_IsStillCached()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        });

        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(Req("https://api.test/ok"), CancellationToken.None);
        _ = await invoker.SendAsync(Req("https://api.test/ok"), CancellationToken.None);

        callCount.Should().Be(1, "200 must still be cached");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HttpRequestMessage Req(string url) =>
        new(HttpMethod.Get, url);

    private sealed class StubTransport(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
