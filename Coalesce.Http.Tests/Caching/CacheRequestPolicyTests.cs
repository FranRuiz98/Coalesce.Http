using Coalesce.Http.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Http.Headers;

namespace Coalesce.Http.Tests.Caching;

/// <summary>
/// Verifies per-request cache policy overrides via <see cref="CacheRequestPolicy"/>
/// using <see cref="HttpRequestMessage.Options"/>.
/// </summary>
public sealed class CacheRequestPolicyTests
{
    private readonly ICacheStore _cache;
    private readonly DefaultCacheKeyBuilder _keyBuilder;

    public CacheRequestPolicyTests()
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

    private static HttpMessageInvoker Invoker(CachingMiddleware m) => new(m);

    // ── BypassCache ──────────────────────────────────────────────────────────

    [Fact]
    public async Task BypassCache_SkipsCacheLookupAndStorage()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent($"response-{callCount}") };
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
            return r;
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        // First request — populates nothing because bypass is set
        HttpRequestMessage req1 = new(HttpMethod.Get, "https://api.test/bypass");
        req1.Options.Set(CacheRequestPolicy.BypassCache, true);
        HttpResponseMessage res1 = await invoker.SendAsync(req1, CancellationToken.None);

        // Second request without bypass — should hit the server, not the cache
        HttpRequestMessage req2 = new(HttpMethod.Get, "https://api.test/bypass");
        HttpResponseMessage res2 = await invoker.SendAsync(req2, CancellationToken.None);

        callCount.Should().Be(2, "both requests should reach the server");
        string body2 = await res2.Content.ReadAsStringAsync();
        body2.Should().Be("response-2", "cache was not populated by the bypassed request");
    }

    [Fact]
    public async Task BypassCache_SkipsUnsafeMethodInvalidation()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent($"body-{callCount}") };
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
            return r;
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        // Populate cache with a GET
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/bypass-inv"), CancellationToken.None);

        // DELETE with BypassCache — should NOT invalidate the cached entry
        HttpRequestMessage deleteReq = new(HttpMethod.Delete, "https://api.test/bypass-inv");
        deleteReq.Options.Set(CacheRequestPolicy.BypassCache, true);
        _ = await invoker.SendAsync(deleteReq, CancellationToken.None);

        // GET should still be served from cache
        HttpResponseMessage res = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/bypass-inv"), CancellationToken.None);

        string body = await res.Content.ReadAsStringAsync();
        body.Should().Be("body-1", "bypassed DELETE should not have invalidated the cached GET entry");
    }

    [Fact]
    public async Task BypassCache_False_DoesNotBypass()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("cached") };
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
            return r;
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        // Populate cache
        HttpRequestMessage req1 = new(HttpMethod.Get, "https://api.test/bypass-false");
        req1.Options.Set(CacheRequestPolicy.BypassCache, false);
        _ = await invoker.SendAsync(req1, CancellationToken.None);

        // Second request — should be served from cache
        HttpResponseMessage res = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/bypass-false"), CancellationToken.None);

        callCount.Should().Be(1, "BypassCache=false should not bypass caching");
    }

    // ── ForceRevalidate ──────────────────────────────────────────────────────

    [Fact]
    public async Task ForceRevalidate_ForcesConditionalRevalidation()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("original") };
                r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
                r.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                return r;
            }

            // Conditional revalidation — 304 Not Modified
            req.Headers.TryGetValues("If-None-Match", out IEnumerable<string>? etags);
            etags.Should().NotBeNull("ForceRevalidate should trigger conditional revalidation");

            HttpResponseMessage notModified = new(HttpStatusCode.NotModified);
            notModified.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(2) };
            return notModified;
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        // Populate cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/force-reval"), CancellationToken.None);

        // Force revalidation even though entry is fresh
        HttpRequestMessage req2 = new(HttpMethod.Get, "https://api.test/force-reval");
        req2.Options.Set(CacheRequestPolicy.ForceRevalidate, true);
        HttpResponseMessage res = await invoker.SendAsync(req2, CancellationToken.None);

        callCount.Should().Be(2, "ForceRevalidate should bypass fresh check and reach the server");
        res.StatusCode.Should().Be(HttpStatusCode.OK, "304 is translated to cached body");
    }

    [Fact]
    public async Task ForceRevalidate_FetchesNewResponseWhenNoValidator()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent($"v{callCount}") };
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
            return r;
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        // Populate cache (no ETag, no Last-Modified)
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/force-no-validator"), CancellationToken.None);

        // Force revalidation — entry has no validator so goes to full request path
        HttpRequestMessage req2 = new(HttpMethod.Get, "https://api.test/force-no-validator");
        req2.Options.Set(CacheRequestPolicy.ForceRevalidate, true);
        HttpResponseMessage res = await invoker.SendAsync(req2, CancellationToken.None);

        callCount.Should().Be(2, "ForceRevalidate without validators should fall through to full request");
        string body = await res.Content.ReadAsStringAsync();
        body.Should().Be("v2");
    }

    // ── NoStore ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoStore_PreventsResponseFromBeingCached()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent($"r{callCount}") };
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
            return r;
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        // First request with NoStore — response should not be stored
        HttpRequestMessage req1 = new(HttpMethod.Get, "https://api.test/nostore");
        req1.Options.Set(CacheRequestPolicy.NoStore, true);
        _ = await invoker.SendAsync(req1, CancellationToken.None);

        // Second request without NoStore — should hit the server since nothing was cached
        HttpResponseMessage res = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/nostore"), CancellationToken.None);

        callCount.Should().Be(2, "second request should reach the server because first was not stored");
        string body = await res.Content.ReadAsStringAsync();
        body.Should().Be("r2");
    }

    [Fact]
    public async Task NoStore_AllowsCacheReads()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("cached-body") };
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
            return r;
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        // Populate cache normally
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/nostore-read"), CancellationToken.None);

        // Second request with NoStore — should still serve from cache (reads are allowed)
        HttpRequestMessage req2 = new(HttpMethod.Get, "https://api.test/nostore-read");
        req2.Options.Set(CacheRequestPolicy.NoStore, true);
        HttpResponseMessage res = await invoker.SendAsync(req2, CancellationToken.None);

        callCount.Should().Be(1, "NoStore should not prevent cache reads");
        string body = await res.Content.ReadAsStringAsync();
        body.Should().Be("cached-body");
    }

    [Fact]
    public async Task NoStore_Allows304TtlRefresh()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
                r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
                r.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                return r;
            }

            // 304 — refresh the TTL
            HttpResponseMessage notModified = new(HttpStatusCode.NotModified);
            notModified.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
            return notModified;
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        // Populate cache with immediately-stale entry
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/nostore-304"), CancellationToken.None);

        // Revalidate with NoStore — 304 refresh should still work
        HttpRequestMessage req2 = new(HttpMethod.Get, "https://api.test/nostore-304");
        req2.Options.Set(CacheRequestPolicy.NoStore, true);
        HttpResponseMessage res = await invoker.SendAsync(req2, CancellationToken.None);

        callCount.Should().Be(2, "stale entry triggers conditional revalidation");
        res.StatusCode.Should().Be(HttpStatusCode.OK, "304 is translated to cached body");

        // Third request without NoStore — should be served from the refreshed cache
        HttpResponseMessage res3 = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/nostore-304"), CancellationToken.None);

        callCount.Should().Be(2, "refreshed entry should serve from cache");
    }

    [Fact]
    public async Task NoStore_BlocksStoringNewResponseDuringRevalidation()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("original") };
                r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
                r.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                return r;
            }

            // Return a brand new 200 response (not 304)
            HttpResponseMessage fresh = new(HttpStatusCode.OK) { Content = new StringContent("updated") };
            fresh.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
            fresh.Headers.ETag = new EntityTagHeaderValue("\"v2\"");
            return fresh;
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        // Populate cache with immediately-stale entry
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/nostore-reval"), CancellationToken.None);

        // Revalidate with NoStore — new 200 should not be stored
        HttpRequestMessage req2 = new(HttpMethod.Get, "https://api.test/nostore-reval");
        req2.Options.Set(CacheRequestPolicy.NoStore, true);
        HttpResponseMessage res = await invoker.SendAsync(req2, CancellationToken.None);

        string body = await res.Content.ReadAsStringAsync();
        body.Should().Be("updated", "new response is returned directly");

        // Third request — should hit the server since the new response was not stored
        HttpResponseMessage res3 = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/nostore-reval"), CancellationToken.None);

        callCount.Should().Be(3, "third request should reach the server since the revalidation response was not stored");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class StubTransport(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
