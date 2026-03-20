using Coalesce.Http.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Http.Headers;

namespace Coalesce.Http.Tests.Caching;

/// <summary>
/// Verifies stale-while-revalidate behaviour (RFC 5861 §3):
/// a stale cached response may be served immediately while a background revalidation is triggered.
/// </summary>
public sealed class StaleWhileRevalidateTests
{
    private readonly ICacheStore _cache;
    private readonly DefaultCacheKeyBuilder _keyBuilder;

    public StaleWhileRevalidateTests()
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

    // ── stale-while-revalidate serves stale immediately ──────────────────────

    [Fact]
    public async Task StaleEntry_WithinSWRWindow_ServesStaleImmediately()
    {
        const string body = "swr-body";
        int callCount = 0;
        TaskCompletionSource<bool> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent(body) };
                r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
                r.Headers.TryAddWithoutValidation("Cache-Control", "stale-while-revalidate=3600");
                return r;
            }

            // Block the background revalidation until we release the gate
            gate.Task.GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("updated") };
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        // Populate cache — entry becomes stale immediately (MaxAge=0)
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/swr"), CancellationToken.None);

        // Second request should be served from stale cache immediately
        HttpResponseMessage response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/swr"), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "stale-while-revalidate should serve the cached entry immediately");
        string returned = await response.Content.ReadAsStringAsync();
        returned.Should().Be(body, "the stale body should be served while background revalidation happens");

        // Release the gate so background revalidation can complete
        gate.SetResult(true);
        // Allow background task to finish
        await Task.Delay(100);
    }

    // ── background revalidation updates the cache ────────────────────────────

    [Fact]
    public async Task StaleEntry_BackgroundRevalidation_UpdatesCache()
    {
        const string originalBody = "original";
        const string updatedBody = "updated";
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent(originalBody) };
                r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
                r.Headers.TryAddWithoutValidation("Cache-Control", "stale-while-revalidate=3600");
                return r;
            }

            // Background revalidation returns fresh content
            HttpResponseMessage fresh = new(HttpStatusCode.OK) { Content = new StringContent(updatedBody) };
            fresh.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) };
            return fresh;
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        // Populate cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/swr-update"), CancellationToken.None);

        // Trigger SWR — gets stale body, background revalidation starts
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/swr-update"), CancellationToken.None);

        // Wait for background revalidation to complete
        await Task.Delay(200);

        // Third request should get updated content from cache
        HttpResponseMessage third = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/swr-update"), CancellationToken.None);
        string thirdBody = await third.Content.ReadAsStringAsync();
        thirdBody.Should().Be(updatedBody, "background revalidation should have updated the cache");
    }

    // ── background revalidation handles 304 Not Modified ─────────────────────

    [Fact]
    public async Task StaleEntry_BackgroundRevalidation_Handles304()
    {
        const string etag = "\"v1\"";
        const string body = "etag-body";
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent(body) };
                r.Headers.ETag = new EntityTagHeaderValue(etag);
                r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
                r.Headers.TryAddWithoutValidation("Cache-Control", "stale-while-revalidate=3600");
                return r;
            }

            // Background revalidation returns 304 with a new max-age
            HttpResponseMessage notModified = new(HttpStatusCode.NotModified);
            notModified.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(10) };
            return notModified;
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        // Populate cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/swr-304"), CancellationToken.None);

        // Trigger SWR
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/swr-304"), CancellationToken.None);

        // Wait for background revalidation
        await Task.Delay(200);

        // The refreshed entry should now be fresh
        string key = _keyBuilder.Build(new HttpRequestMessage(HttpMethod.Get, "https://api.test/swr-304"));
        _cache.TryGetValue(key, out CacheEntry? entry);

        entry.Should().NotBeNull();
        entry!.IsExpired().Should().BeFalse("304 background revalidation should have refreshed the TTL");
    }

    // ── outside SWR window falls through to normal revalidation ──────────────

    [Fact]
    public async Task StaleEntry_OutsideSWRWindow_PerformsNormalRevalidation()
    {
        const string etag = "\"v1\"";
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
                r.Headers.ETag = new EntityTagHeaderValue(etag);
                r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
                // stale-while-revalidate=0 → window expired immediately
                r.Headers.TryAddWithoutValidation("Cache-Control", "stale-while-revalidate=0");
                return r;
            }

            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        // Populate cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/no-swr"), CancellationToken.None);

        // Second request — SWR window is 0, should fall through to sync revalidation
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/no-swr"), CancellationToken.None);

        callCount.Should().Be(2, "with SWR window of 0, the middleware should perform synchronous revalidation");
    }

    // ── no-cache on request bypasses SWR ─────────────────────────────────────

    [Fact]
    public async Task RequestWithNoCache_BypassesSWR()
    {
        const string etag = "\"v1\"";
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
                r.Headers.ETag = new EntityTagHeaderValue(etag);
                r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
                r.Headers.TryAddWithoutValidation("Cache-Control", "stale-while-revalidate=3600");
                return r;
            }

            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        // Populate cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/swr-nocache"), CancellationToken.None);

        // no-cache request should bypass SWR and do synchronous revalidation
        HttpRequestMessage noCacheReq = new(HttpMethod.Get, "https://api.test/swr-nocache");
        noCacheReq.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        _ = await invoker.SendAsync(noCacheReq, CancellationToken.None);

        callCount.Should().Be(2, "Cache-Control: no-cache must bypass stale-while-revalidate");
    }

    // ── DefaultStaleWhileRevalidateSeconds fallback ──────────────────────────

    [Fact]
    public async Task DefaultStaleWhileRevalidateSeconds_AppliesWhenNoDirectiveOnResponse()
    {
        const string body = "default-swr";
        int callCount = 0;

        CacheOptions options = new()
        {
            DefaultTtl = TimeSpan.FromMilliseconds(1),
            DefaultStaleWhileRevalidateSeconds = 3600
        };

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("updated") };
        }, options);

        HttpMessageInvoker invoker = Invoker(middleware);

        // Populate cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/default-swr"), CancellationToken.None);

        // Wait for entry to become stale
        await Task.Delay(10);

        // Should serve stale via default SWR window
        HttpResponseMessage response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/default-swr"), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string returned = await response.Content.ReadAsStringAsync();
        returned.Should().Be(body, "DefaultStaleWhileRevalidateSeconds should allow serving the stale entry immediately");
    }

    private sealed class StubTransport(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
