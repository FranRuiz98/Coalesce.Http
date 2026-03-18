using Coalesce.Http.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Http.Headers;

namespace Coalesce.Http.Tests.Caching;

public sealed class CachingMiddlewareTests
{
    private readonly ICacheStore _cache;
    private readonly DefaultCacheKeyBuilder _keyBuilder;
    private readonly CacheOptions _options;

    public CachingMiddlewareTests()
    {
        _cache = new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions()));
        _keyBuilder = new DefaultCacheKeyBuilder();
        _options = new CacheOptions { DefaultTtl = TimeSpan.FromMinutes(5) };
    }

    private (CachingMiddleware middleware, StubHandler stub) BuildPipeline(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        StubHandler stub = new(handler);
        CachingMiddleware middleware = new(_cache, _keyBuilder, _options) { InnerHandler = stub };
        return (middleware, stub);
    }

    private static HttpMessageInvoker Invoker(CachingMiddleware middleware) => new(middleware);

    // ── Fresh hit ────────────────────────────────────────────────────────────

    [Fact]
    public async Task FreshHit_ReturnsCachedResponse_WithoutCallingInner()
    {
        int callCount = 0;
        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            return OkResponse("hello");
        });

        HttpMessageInvoker invoker = Invoker(middleware);
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/res"), CancellationToken.None);

        HttpResponseMessage second = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/res"), CancellationToken.None);

        callCount.Should().Be(1, "second request should be served from cache");
        second.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Cache miss ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CacheMiss_ForwardsRequest_AndStoresEntry()
    {
        int callCount = 0;
        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            return OkResponse("data");
        });

        HttpMessageInvoker invoker = Invoker(middleware);
        HttpResponseMessage response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/new"), CancellationToken.None);

        callCount.Should().Be(1);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Second call should hit cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/new"), CancellationToken.None);
        callCount.Should().Be(1, "entry should have been stored after the first miss");
    }

    // ── Stale + ETag → 304 Not Modified ─────────────────────────────────────

    [Fact]
    public async Task StaleEntryWithETag_Sends304_RefreshesTtlAndReturnsCachedBody()
    {
        const string etag = "\"abc123\"";
        const string body = "original-body";
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (req.Headers.IfNoneMatch.Any())
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
            HttpResponseMessage r = OkResponse(body);
            r.Headers.ETag = new EntityTagHeaderValue(etag);
            return r;
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        // Populate cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/etag"), CancellationToken.None);
        callCount.Should().Be(1);

        // Force entry to become stale
        ForceStale("https://api.test/etag");

        // Revalidation request
        HttpResponseMessage revalidated = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/etag"), CancellationToken.None);

        callCount.Should().Be(2, "should have sent a conditional request");
        revalidated.StatusCode.Should().Be(HttpStatusCode.OK);
        string responseBody = await revalidated.Content.ReadAsStringAsync();
        responseBody.Should().Be(body, "cached body should be reused on 304");
    }

    [Fact]
    public async Task StaleEntryWithETag_OnRevalidation_IfNoneMatchHeaderIsSet()
    {
        const string etag = "\"ver1\"";
        string? capturedIfNoneMatch = null;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            capturedIfNoneMatch = req.Headers.IfNoneMatch.FirstOrDefault()?.Tag;
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        // Seed cache manually with a stale entry
        string key = _keyBuilder.Build(new HttpRequestMessage(HttpMethod.Get, "https://api.test/check"));
        _cache.Set(key, new StaleEntryBuilder().WithETag(etag).Build());

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/check"), CancellationToken.None);

        capturedIfNoneMatch.Should().Be(etag);
    }

    // ── Stale + ETag → 200 OK (server has new content) ───────────────────────

    [Fact]
    public async Task StaleEntryWithETag_Receives200_StoresNewResponseAndReturnsIt()
    {
        const string originalETag = "\"v1\"";
        const string newETag = "\"v2\"";
        const string newBody = "fresh-content";
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (req.Headers.IfNoneMatch.Any())
            {
                HttpResponseMessage fresh = OkResponse(newBody);
                fresh.Headers.ETag = new EntityTagHeaderValue(newETag);
                return fresh;
            }
            HttpResponseMessage r = OkResponse("old-content");
            r.Headers.ETag = new EntityTagHeaderValue(originalETag);
            return r;
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        // Populate cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/updated"), CancellationToken.None);
        ForceStale("https://api.test/updated");

        HttpResponseMessage response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/updated"), CancellationToken.None);

        callCount.Should().Be(2);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Be(newBody);
    }

    // ── Stale without ETag → full miss ───────────────────────────────────────

    [Fact]
    public async Task StaleEntryWithoutETag_PerformsFullRequest_NotConditional()
    {
        bool ifNoneMatchSent = false;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            ifNoneMatchSent = req.Headers.IfNoneMatch.Any();
            return OkResponse("data");
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        string key = _keyBuilder.Build(new HttpRequestMessage(HttpMethod.Get, "https://api.test/noetag"));
        _cache.Set(key, new StaleEntryBuilder().WithoutETag().Build());

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/noetag"), CancellationToken.None);

        ifNoneMatchSent.Should().BeFalse("no ETag means full unconditional request");
    }

    // ── Non-cacheable requests ────────────────────────────────────────────────

    [Fact]
    public async Task PostRequest_IsNotCached_AlwaysForwarded()
    {
        int callCount = 0;
        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            return OkResponse("ok");
        });

        HttpMessageInvoker invoker = Invoker(middleware);
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://api.test/cmd"), CancellationToken.None);
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://api.test/cmd"), CancellationToken.None);

        callCount.Should().Be(2, "POST requests bypass the cache entirely");
    }

    // ── Age header (RFC 9111 §5.1) ───────────────────────────────────────────

    [Fact]
    public async Task CacheHit_InjectsAgeHeader()
    {
        (CachingMiddleware middleware, _) = BuildPipeline(_ => OkResponse("data"));
        HttpMessageInvoker invoker = Invoker(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/age"), CancellationToken.None);
        HttpResponseMessage hit = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/age"), CancellationToken.None);

        hit.Headers.Age.Should().NotBeNull("Age header must be present on cache hits");
        hit.Headers.Age!.Value.TotalSeconds.Should().BeGreaterThanOrEqualTo(0);
    }

    // ── Cache-Control: private (RFC 9111 §5.2.2.7) ───────────────────────────

    [Fact]
    public async Task ResponseWithPrivate_IsNotCached()
    {
        int callCount = 0;
        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            HttpResponseMessage r = OkResponse("secret");
            r.Headers.CacheControl = new CacheControlHeaderValue { Private = true };
            return r;
        });

        HttpMessageInvoker invoker = Invoker(middleware);
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/private"), CancellationToken.None);
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/private"), CancellationToken.None);

        callCount.Should().Be(2, "responses with Cache-Control: private must not be stored");
    }

    // ── Cache-Control: no-cache on response (RFC 9111 §5.2.2.4) ─────────────

    [Fact]
    public async Task ResponseWithNoCache_IsStoredButAlwaysRevalidated()
    {
        int callCount = 0;
        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (req.Headers.IfNoneMatch.Any())
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
            HttpResponseMessage r = OkResponse("body");
            r.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            r.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            return r;
        });

        HttpMessageInvoker invoker = Invoker(middleware);
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/nocache"), CancellationToken.None);
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/nocache"), CancellationToken.None);

        callCount.Should().Be(2, "no-cache response must trigger revalidation on every subsequent use");
    }

    // ── Cache-Control: no-cache on request (RFC 9111 §5.2.1.4) ─────────────

    [Fact]
    public async Task RequestWithNoCache_BypassesFreshEntry_AndRevalidates()
    {
        int callCount = 0;
        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (req.Headers.IfNoneMatch.Any())
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
            HttpResponseMessage r = OkResponse("body");
            r.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return r;
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        // Populate cache (fresh entry)
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/force"), CancellationToken.None);

        // Second request forces revalidation via no-cache
        HttpRequestMessage noCacheRequest = new(HttpMethod.Get, "https://api.test/force");
        noCacheRequest.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        _ = await invoker.SendAsync(noCacheRequest, CancellationToken.None);

        callCount.Should().Be(2, "Cache-Control: no-cache on the request must bypass a fresh entry and revalidate");
    }

    // ── max-age freshness (RFC 9111 §4.2.1) ──────────────────────────────────

    [Fact]
    public async Task ResponseWithMaxAge_UsesThatTtlInsteadOfDefault()
    {
        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            HttpResponseMessage r = OkResponse("data");
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(1) };
            return r;
        });

        HttpMessageInvoker invoker = Invoker(middleware);
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/maxage"), CancellationToken.None);

        string key = _keyBuilder.Build(new HttpRequestMessage(HttpMethod.Get, "https://api.test/maxage"));
        _cache.TryGetValue(key, out CacheEntry? entry);

        entry.Should().NotBeNull();
        (entry!.ExpiresAt - entry.StoredAt).Should().BeCloseTo(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(200),
            "ExpiresAt should reflect max-age=1, not DefaultTtl");
    }

    // ── Vary field matching (RFC 9111 §4.1) ──────────────────────────────────

    [Fact]
    public async Task VaryMismatch_TreatsAsCacheMiss()
    {
        int callCount = 0;
        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            HttpResponseMessage r = OkResponse("data");
            r.Headers.Vary.Add("Accept-Language");
            return r;
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        HttpRequestMessage req1 = new(HttpMethod.Get, "https://api.test/vary");
        req1.Headers.TryAddWithoutValidation("Accept-Language", "en");
        _ = await invoker.SendAsync(req1, CancellationToken.None);

        // Different Accept-Language → must not serve cached entry
        HttpRequestMessage req2 = new(HttpMethod.Get, "https://api.test/vary");
        req2.Headers.TryAddWithoutValidation("Accept-Language", "es");
        _ = await invoker.SendAsync(req2, CancellationToken.None);

        callCount.Should().Be(2, "different Vary header values must result in a cache miss");
    }

    [Fact]
    public async Task VaryMatch_ServesFromCache()
    {
        int callCount = 0;
        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            HttpResponseMessage r = OkResponse("data");
            r.Headers.Vary.Add("Accept-Language");
            return r;
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        HttpRequestMessage req1 = new(HttpMethod.Get, "https://api.test/varymatch");
        req1.Headers.TryAddWithoutValidation("Accept-Language", "en");
        _ = await invoker.SendAsync(req1, CancellationToken.None);

        HttpRequestMessage req2 = new(HttpMethod.Get, "https://api.test/varymatch");
        req2.Headers.TryAddWithoutValidation("Accept-Language", "en");
        _ = await invoker.SendAsync(req2, CancellationToken.None);

        callCount.Should().Be(1, "identical Vary header values must be served from cache");
    }

    [Fact]
    public async Task VaryStar_AlwaysMisses()
    {
        int callCount = 0;
        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            HttpResponseMessage r = OkResponse("data");
            r.Headers.Vary.Add("*");
            return r;
        });

        HttpMessageInvoker invoker = Invoker(middleware);
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/varystar"), CancellationToken.None);
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/varystar"), CancellationToken.None);

        callCount.Should().Be(2, "Vary: * must never be served from cache");
    }

    // ── If-Modified-Since fallback (RFC 9111 §4.3.1) ─────────────────────────

    [Fact]
    public async Task StaleEntryWithLastModified_SendsIfModifiedSince_WhenNoETag()
    {
        DateTimeOffset lastModified = DateTimeOffset.UtcNow.AddHours(-1);
        DateTimeOffset? capturedIfModifiedSince = null;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            capturedIfModifiedSince = req.Headers.IfModifiedSince;
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });

        HttpMessageInvoker invoker = Invoker(middleware);

        string key = _keyBuilder.Build(new HttpRequestMessage(HttpMethod.Get, "https://api.test/lm"));
        _cache.Set(key, new CacheEntry
        {
            StatusCode = 200,
            Body = "body"u8.ToArray(),
            Headers = new Dictionary<string, string[]>(),
            StoredAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(60),
            LastModified = lastModified,
            ExpiresAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1)
        });

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/lm"), CancellationToken.None);

        capturedIfModifiedSince.Should().Be(lastModified,
            "If-Modified-Since must be set from the stored LastModified when no ETag is present");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static HttpResponseMessage OkResponse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private void ForceStale(string url)
    {
        string key = _keyBuilder.Build(new HttpRequestMessage(HttpMethod.Get, url));
        if (_cache.TryGetValue(key, out CacheEntry? existing) && existing is not null)
        {
            _cache.Set(key, existing with { ExpiresAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1) });
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(handler(request));
    }

    private sealed class StaleEntryBuilder
    {
        private string? _etag;

        public StaleEntryBuilder WithETag(string etag) { _etag = etag; return this; }
        public StaleEntryBuilder WithoutETag() { _etag = null; return this; }

        public CacheEntry Build() => new()
        {
            StatusCode = 200,
            Body = "cached"u8.ToArray(),
            Headers = new Dictionary<string, string[]>(),
            StoredAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10),
            ETag = _etag,
            ExpiresAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1)
        };
    }
}
