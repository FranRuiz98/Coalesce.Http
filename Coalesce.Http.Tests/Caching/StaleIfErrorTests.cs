using Coalesce.Http.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Http.Headers;

namespace Coalesce.Http.Tests.Caching;

/// <summary>
/// Verifies stale-if-error behaviour (RFC 5861 §4):
/// a stale cached response may be served when the origin returns an error within the
/// configured window.
/// </summary>
public sealed class StaleIfErrorTests
{
    private readonly IMemoryCache _cache;
    private readonly DefaultCacheKeyBuilder _keyBuilder;

    public StaleIfErrorTests()
    {
        _cache = new MemoryCache(new MemoryCacheOptions());
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

    // ── stale-if-error directive on the response ──────────────────────────────

    [Fact]
    public async Task StaleEntry_OnServerError5xx_ServesStaleResponse()
    {
        const string body = "stale-body";
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                // First response: 200 + stale-if-error=3600 (immediately stale via MaxAge=0)
                HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent(body) };
                r.Headers.CacheControl = new CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.Zero,
                };
                r.Headers.TryAddWithoutValidation("Cache-Control", "stale-if-error=3600");
                return r;
            }

            // Subsequent responses: 503
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        // Populate cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/sie"), CancellationToken.None);

        // Second request: entry is stale (MaxAge=0), server returns 503, stale-if-error should rescue
        HttpResponseMessage response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/sie"), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "stale-if-error should serve the cached body on 5xx");
        string returned = await response.Content.ReadAsStringAsync();
        returned.Should().Be(body);
    }

    [Fact]
    public async Task StaleEntry_OnNetworkException_ServesStaleResponse()
    {
        const string body = "fallback-body";
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent(body) };
                r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
                r.Headers.TryAddWithoutValidation("Cache-Control", "stale-if-error=3600");
                return r;
            }

            throw new HttpRequestException("simulated network failure");
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/sie-exception"), CancellationToken.None);

        HttpResponseMessage response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/sie-exception"), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "stale-if-error should catch network exceptions");
        string returned = await response.Content.ReadAsStringAsync();
        returned.Should().Be(body);
    }

    [Fact]
    public async Task StaleEntry_AfterStaleIfErrorWindow_PropagatesError()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
                r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
                // stale-if-error=0 means the window has already expired
                r.Headers.TryAddWithoutValidation("Cache-Control", "stale-if-error=0");
                return r;
            }
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/sie-expired"), CancellationToken.None);

        HttpResponseMessage response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/sie-expired"), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "when stale-if-error window is 0, the error response must be passed through");
    }

    [Fact]
    public async Task FreshEntry_OnServerError_PropagatesError()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            return callCount == 1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") }
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        // First request caches a fresh entry (default 5-minute TTL)
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/fresh"), CancellationToken.None);

        // Second request hits the fresh cache — backend is never called
        HttpResponseMessage response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/fresh"), CancellationToken.None);

        callCount.Should().Be(1, "a fresh cache hit must never reach the backend");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task StaleEntry_WithoutStaleIfError_PropagatesError()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                // No stale-if-error directive
                HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
                r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
                return r;
            }
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/no-sie"), CancellationToken.None);

        HttpResponseMessage response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/no-sie"), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "without stale-if-error the 5xx must be propagated to the caller");
    }

    [Fact]
    public async Task StaleEntry_On5xxDuringRevalidation_ServesStale()
    {
        const string body = "revalidation-fallback";
        const string etag = "\"v1\"";
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent(body) };
                r.Headers.ETag = new EntityTagHeaderValue(etag);
                r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
                r.Headers.TryAddWithoutValidation("Cache-Control", "stale-if-error=3600");
                return r;
            }
            // Revalidation attempt returns 503
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/sie-revalidate"), CancellationToken.None);

        HttpResponseMessage response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/sie-revalidate"), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "stale-if-error must rescue a 5xx received during conditional revalidation");
        string returned = await response.Content.ReadAsStringAsync();
        returned.Should().Be(body);
    }

    // ── DefaultStaleIfErrorSeconds fallback ───────────────────────────────────

    [Fact]
    public async Task DefaultStaleIfErrorSeconds_AppliesWhenNoDirectiveOnResponse()
    {
        const string body = "default-fallback";
        int callCount = 0;

        // Configure a 1-hour default stale-if-error window
        CacheOptions options = new()
        {
            DefaultTtl = TimeSpan.FromMilliseconds(1),  // entries become stale almost immediately
            DefaultStaleIfErrorSeconds = 3600
        };

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            return callCount == 1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }, options);

        HttpMessageInvoker invoker = Invoker(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/default-sie"), CancellationToken.None);

        // Wait for the entry to become stale
        await Task.Delay(10);

        HttpResponseMessage response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/default-sie"), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "DefaultStaleIfErrorSeconds must apply when the response carries no stale-if-error directive");
        string returned = await response.Content.ReadAsStringAsync();
        returned.Should().Be(body);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class StubTransport(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
