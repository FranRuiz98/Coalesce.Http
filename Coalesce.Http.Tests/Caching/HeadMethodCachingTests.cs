using Coalesce.Http.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Http.Headers;

namespace Coalesce.Http.Tests.Caching;

/// <summary>
/// Verifies RFC 9110 §9.3.2 HEAD method caching:
/// a fresh GET cache entry should satisfy a HEAD request without reaching the origin,
/// and a stale GET entry with a validator should trigger a conditional HEAD revalidation.
/// </summary>
public sealed class HeadMethodCachingTests
{
    private readonly ICacheStore _cache;
    private readonly DefaultCacheKeyBuilder _keyBuilder;
    private readonly CacheOptions _options;

    public HeadMethodCachingTests()
    {
        _cache = new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions()));
        _keyBuilder = new DefaultCacheKeyBuilder();
        _options = new CacheOptions { DefaultTtl = TimeSpan.FromMinutes(5) };
    }

    private (CachingMiddleware middleware, StubTransport stub) BuildPipeline(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        StubTransport stub = new(handler);
        CachingMiddleware middleware = new(_cache, _keyBuilder, _options) { InnerHandler = stub };
        return (middleware, stub);
    }

    private static HttpMessageInvoker Invoker(CachingMiddleware m) => new(m);

    // ── Fresh GET entry satisfies HEAD ────────────────────────────────────────

    [Fact]
    public async Task Head_WhenFreshGetEntryExists_ServedFromCacheWithoutCallingOrigin()
    {
        int callCount = 0;
        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body text") };
        });

        HttpMessageInvoker invoker = Invoker(middleware);
        const string url = "https://api.test/head/fresh";

        // Warm the GET cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None);
        callCount.Should().Be(1);

        // HEAD should be satisfied by the cached GET entry
        HttpResponseMessage headResponse = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), CancellationToken.None);

        callCount.Should().Be(1, "HEAD should be served from the GET cache — origin must not be called");
        headResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Head_FromCache_ResponseBodyIsEmpty()
    {
        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body text") });

        HttpMessageInvoker invoker = Invoker(middleware);
        const string url = "https://api.test/head/empty-body";

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None);

        HttpResponseMessage headResponse = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), CancellationToken.None);

        byte[] body = await headResponse.Content.ReadAsByteArrayAsync();
        body.Should().BeEmpty("HEAD responses must not include a body");
    }

    [Fact]
    public async Task Head_FromCache_AgeHeaderPresent()
    {
        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") });

        HttpMessageInvoker invoker = Invoker(middleware);
        const string url = "https://api.test/head/age";

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None);

        HttpResponseMessage headResponse = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), CancellationToken.None);

        headResponse.Headers.Age.Should().NotBeNull("Age header must be present on a cached HEAD response");
    }

    // ── No GET cache entry — forward to origin ────────────────────────────────

    [Fact]
    public async Task Head_WhenNoCacheEntry_ForwardsToOrigin()
    {
        int callCount = 0;
        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        HttpResponseMessage headResponse = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Head, "https://api.test/head/miss"),
            CancellationToken.None);

        callCount.Should().Be(1, "HEAD with no cache entry must reach the origin");
        headResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Conditional HEAD revalidation ─────────────────────────────────────────

    [Fact]
    public async Task Head_StaleEntryWithETag_Sends304AndRefreshesTtl()
    {
        const string etag = "\"v1\"";
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                // First: GET response — stale immediately (max-age=0) with ETag
                HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("data") };
                r.Headers.ETag = new EntityTagHeaderValue(etag);
                r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
                return r;
            }

            // Second call: conditional HEAD revalidation → 304
            req.Headers.Contains("If-None-Match").Should().BeTrue(
                "conditional HEAD must carry If-None-Match from the stored ETag");
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });

        HttpMessageInvoker invoker = Invoker(middleware);
        const string url = "https://api.test/head/etag";

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None);

        HttpResponseMessage headResponse = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Head, url), CancellationToken.None);

        headResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "304 during HEAD revalidation should result in 200 served from the refreshed cache entry");
        byte[] body = await headResponse.Content.ReadAsByteArrayAsync();
        body.Should().BeEmpty("HEAD response body must be empty even after revalidation");
    }

    [Fact]
    public async Task Head_StaleEntryWithETag_ConditionalHeaderSet()
    {
        const string etag = "\"abc\"";
        string? capturedIfNoneMatch = null;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
                r.Headers.ETag = new EntityTagHeaderValue(etag);
                r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
                return r;
            }

            capturedIfNoneMatch = req.Headers.TryGetValues("If-None-Match", out IEnumerable<string>? v)
                ? string.Join(",", v)
                : null;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        HttpMessageInvoker invoker = Invoker(middleware);
        const string url = "https://api.test/head/cond-header";

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None);
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), CancellationToken.None);

        capturedIfNoneMatch.Should().Be(etag, "stale HEAD revalidation must set If-None-Match to the stored ETag");
    }

    // ── BypassCache policy applies to HEAD ────────────────────────────────────

    [Fact]
    public async Task Head_WithBypassCache_ForwardsToOriginDirectly()
    {
        int callCount = 0;
        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("x") };
        });

        HttpMessageInvoker invoker = Invoker(middleware);
        const string url = "https://api.test/head/bypass";

        // Warm GET cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None);

        // HEAD with BypassCache — must not use cache
        HttpRequestMessage headBypass = new(HttpMethod.Head, url);
        headBypass.Options.Set(CacheRequestPolicy.BypassCache, true);
        _ = await invoker.SendAsync(headBypass, CancellationToken.None);

        callCount.Should().Be(2, "BypassCache must prevent the HEAD from being served from the GET cache");
    }

    // ── HEAD for non-existent URL returns origin response as-is ──────────────

    [Fact]
    public async Task Head_ForNotFoundResource_PropagatesOriginResponse()
    {
        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        HttpMessageInvoker invoker = Invoker(middleware);

        HttpResponseMessage response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Head, "https://api.test/head/notfound"),
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "non-2xx origin HEAD responses must be returned to the caller unchanged");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class StubTransport(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
