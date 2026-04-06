using Coalesce.Http.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Http.Headers;

namespace Coalesce.Http.Tests.Caching;

/// <summary>
/// Verifies <c>Cache-Control: immutable</c> behaviour (RFC 8246):
/// a fresh immutable entry must be served even when the client sends <c>no-cache</c>
/// or the pipeline uses <c>ForceRevalidate</c>; a stale immutable entry is still revalidated.
/// </summary>
public sealed class ImmutableCacheTests
{
    private readonly ICacheStore _cache;
    private readonly DefaultCacheKeyBuilder _keyBuilder;

    public ImmutableCacheTests()
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

    private static HttpRequestMessage Req(string url, Action<HttpRequestMessage>? configure = null)
    {
        HttpRequestMessage req = new(HttpMethod.Get, url);
        configure?.Invoke(req);
        return req;
    }

    // ── Immutable fresh entry served despite client no-cache ──────────────────

    [Fact]
    public async Task ImmutableFreshEntry_ServedDespiteClientNoCache()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("immutable-body") };
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
            r.Headers.TryAddWithoutValidation("Cache-Control", "immutable");
            return r;
        });

        HttpMessageInvoker invoker = new(middleware);

        // Populate the cache
        _ = await invoker.SendAsync(Req("https://api.test/immutable"), CancellationToken.None);

        // Second request with no-cache — immutable should override it
        HttpResponseMessage response = await invoker.SendAsync(
            Req("https://api.test/immutable", r =>
                r.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true }),
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        callCount.Should().Be(1, "immutable fresh entry must be served without hitting the origin");
    }

    // ── Immutable fresh entry served despite ForceRevalidate policy ───────────

    [Fact]
    public async Task ImmutableFreshEntry_ServedDespiteForceRevalidate()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("immutable-body") };
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
            r.Headers.TryAddWithoutValidation("Cache-Control", "immutable");
            return r;
        });

        HttpMessageInvoker invoker = new(middleware);

        // Populate the cache
        _ = await invoker.SendAsync(Req("https://api.test/immutable-force"), CancellationToken.None);

        // Second request with ForceRevalidate — immutable should override it
        HttpRequestMessage forceReq = Req("https://api.test/immutable-force");
        forceReq.Options.Set(CacheRequestPolicy.ForceRevalidate, true);

        HttpResponseMessage response = await invoker.SendAsync(forceReq, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        callCount.Should().Be(1, "immutable fresh entry must be served despite ForceRevalidate");
    }

    // ── Immutable stale entry is still revalidated ────────────────────────────

    [Fact]
    public async Task ImmutableStaleEntry_IsStillRevalidated()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (req.Headers.IfNoneMatch.Any())
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("immutable-body") };
            r.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
            r.Headers.TryAddWithoutValidation("Cache-Control", "immutable");
            return r;
        });

        HttpMessageInvoker invoker = new(middleware);

        // Populate (immediately stale due to max-age=0)
        _ = await invoker.SendAsync(Req("https://api.test/immutable-stale"), CancellationToken.None);

        // Second request — entry is stale, so revalidation should still occur
        _ = await invoker.SendAsync(Req("https://api.test/immutable-stale"), CancellationToken.None);

        callCount.Should().Be(2, "immutable does not prevent revalidation of stale entries");
    }

    // ── Non-immutable entry with no-cache triggers revalidation (regression) ──

    [Fact]
    public async Task NonImmutableEntry_WithClientNoCache_TriggersRevalidation()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (req.Headers.IfNoneMatch.Any())
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
            r.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
            return r;
        });

        HttpMessageInvoker invoker = new(middleware);

        // Populate the cache
        _ = await invoker.SendAsync(Req("https://api.test/non-immutable"), CancellationToken.None);

        // Second request with no-cache — non-immutable must revalidate
        HttpResponseMessage response = await invoker.SendAsync(
            Req("https://api.test/non-immutable", r =>
                r.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true }),
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        callCount.Should().Be(2, "non-immutable entry with no-cache must not be served from cache without revalidation");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class StubTransport(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
