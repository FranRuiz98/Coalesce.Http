using Coalesce.Http.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Http.Headers;

namespace Coalesce.Http.Tests.Caching;

/// <summary>
/// Verifies must-revalidate / proxy-revalidate enforcement (RFC 9111 §5.2.2.2):
/// when these directives are present, the cache MUST NOT serve a stale response
/// without successful revalidation — overriding stale-while-revalidate and stale-if-error.
/// </summary>
public sealed class MustRevalidateTests
{
    private readonly ICacheStore _cache;
    private readonly DefaultCacheKeyBuilder _keyBuilder;

    public MustRevalidateTests()
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

    // ── must-revalidate blocks stale-if-error on 5xx ──────────────────────────

    [Fact]
    public async Task MustRevalidate_BlocksStaleIfError_On5xx()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
                r.Headers.CacheControl = new CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.Zero,
                    MustRevalidate = true
                };
                r.Headers.TryAddWithoutValidation("Cache-Control", "stale-if-error=3600");
                return r;
            }

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/mr-sie"), CancellationToken.None);

        HttpResponseMessage response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/mr-sie"), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "must-revalidate prohibits serving stale even when stale-if-error is present");
    }

    // ── must-revalidate blocks stale-if-error on network exception ────────────

    [Fact]
    public async Task MustRevalidate_BlocksStaleIfError_OnNetworkException()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
                r.Headers.CacheControl = new CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.Zero,
                    MustRevalidate = true
                };
                r.Headers.TryAddWithoutValidation("Cache-Control", "stale-if-error=3600");
                return r;
            }

            throw new HttpRequestException("simulated network failure");
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/mr-sie-ex"), CancellationToken.None);

        Func<Task> act = () => invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.test/mr-sie-ex"), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>(
            "must-revalidate prohibits catching exceptions via stale-if-error");
    }

    // ── must-revalidate blocks stale-while-revalidate ─────────────────────────

    [Fact]
    public async Task MustRevalidate_BlocksStaleWhileRevalidate()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("original") };
                r.Headers.CacheControl = new CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.Zero,
                    MustRevalidate = true
                };
                r.Headers.TryAddWithoutValidation("Cache-Control", "stale-while-revalidate=3600");
                return r;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("fresh") };
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/mr-swr"), CancellationToken.None);

        // Second request: entry is stale — must-revalidate should force a synchronous revalidation
        // instead of serving stale + background refresh
        HttpResponseMessage response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/mr-swr"), CancellationToken.None);

        callCount.Should().Be(2, "must-revalidate must force a full request instead of serving stale via SWR");
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Be("fresh", "the fresh response from the origin should be returned, not the stale cache");
    }

    // ── proxy-revalidate has the same effect ──────────────────────────────────

    [Fact]
    public async Task ProxyRevalidate_BlocksStaleIfError()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
                r.Headers.CacheControl = new CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.Zero,
                    ProxyRevalidate = true
                };
                r.Headers.TryAddWithoutValidation("Cache-Control", "stale-if-error=3600");
                return r;
            }

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/pr-sie"), CancellationToken.None);

        HttpResponseMessage response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/pr-sie"), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "proxy-revalidate must have the same effect as must-revalidate for a shared cache");
    }

    [Fact]
    public async Task ProxyRevalidate_BlocksStaleWhileRevalidate()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("original") };
                r.Headers.CacheControl = new CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.Zero,
                    ProxyRevalidate = true
                };
                r.Headers.TryAddWithoutValidation("Cache-Control", "stale-while-revalidate=3600");
                return r;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("fresh") };
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/pr-swr"), CancellationToken.None);

        HttpResponseMessage response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/pr-swr"), CancellationToken.None);

        callCount.Should().Be(2, "proxy-revalidate must force a synchronous request instead of serving stale via SWR");
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Be("fresh");
    }

    // ── without must-revalidate, stale serving still works ────────────────────

    [Fact]
    public async Task WithoutMustRevalidate_StaleIfError_StillWorks()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("stale-body") };
                r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
                r.Headers.TryAddWithoutValidation("Cache-Control", "stale-if-error=3600");
                return r;
            }

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/no-mr-sie"), CancellationToken.None);

        HttpResponseMessage response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/no-mr-sie"), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "without must-revalidate, stale-if-error should still serve stale on 5xx");
    }

    // ── MustRevalidate flag is persisted correctly ────────────────────────────

    [Fact]
    public async Task MustRevalidate_Flag_PersistedInCacheEntry()
    {
        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
            r.Headers.CacheControl = new CacheControlHeaderValue { MustRevalidate = true };
            return r;
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/flag"), CancellationToken.None);

        string key = _keyBuilder.Build(new HttpRequestMessage(HttpMethod.Get, "https://api.test/flag"));
        _cache.TryGetValue(key, out CacheEntry? entry).Should().BeTrue();
        entry!.MustRevalidate.Should().BeTrue("CacheEntry.MustRevalidate should reflect the stored directive");
    }

    [Fact]
    public async Task MustRevalidate_FlagFalse_WhenDirectiveAbsent()
    {
        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") });

        HttpMessageInvoker invoker = Invoker(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/no-flag"), CancellationToken.None);

        string key = _keyBuilder.Build(new HttpRequestMessage(HttpMethod.Get, "https://api.test/no-flag"));
        _cache.TryGetValue(key, out CacheEntry? entry).Should().BeTrue();
        entry!.MustRevalidate.Should().BeFalse("MustRevalidate should default to false when directive is absent");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class StubTransport(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
