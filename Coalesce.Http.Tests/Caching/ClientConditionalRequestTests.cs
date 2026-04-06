using Coalesce.Http.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Http.Headers;

namespace Coalesce.Http.Tests.Caching;

/// <summary>
/// Verifies client-side conditional GET pass-through (RFC 9111 §4.3.2 / RFC 9110 §13.1):
/// when the client sends <c>If-None-Match</c> or <c>If-Modified-Since</c> and the cached
/// entry has a matching validator, the middleware returns 304 directly without hitting the origin.
/// </summary>
public sealed class ClientConditionalRequestTests
{
    private readonly ICacheStore _cache;
    private readonly DefaultCacheKeyBuilder _keyBuilder;
    private readonly CacheOptions _options;

    public ClientConditionalRequestTests()
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

    // ── If-None-Match matches stored ETag → 304 ───────────────────────────────

    [Fact]
    public async Task FreshEntry_MatchingETag_Returns304()
    {
        const string etag = "\"abc123\"";
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
            r.Headers.ETag = new EntityTagHeaderValue(etag);
            return r;
        });

        HttpMessageInvoker invoker = new(middleware);

        // Populate the cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/cond"), CancellationToken.None);

        // Client conditional GET with matching ETag
        HttpRequestMessage conditionalReq = new(HttpMethod.Get, "https://api.test/cond");
        conditionalReq.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));

        HttpResponseMessage response = await invoker.SendAsync(conditionalReq, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.NotModified,
            "matching ETag must return 304 without hitting the origin");
        callCount.Should().Be(1, "origin must not be contacted again");
    }

    // ── If-Modified-Since with entry not modified → 304 ──────────────────────

    [Fact]
    public async Task FreshEntry_LastModifiedAtOrBeforeIfModifiedSince_Returns304()
    {
        DateTimeOffset lastModified = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
            r.Content.Headers.LastModified = lastModified;
            return r;
        });

        HttpMessageInvoker invoker = new(middleware);

        // Populate the cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/cond-lm"), CancellationToken.None);

        // Client asks "did it change since lastModified?" — it did not
        HttpRequestMessage conditionalReq = new(HttpMethod.Get, "https://api.test/cond-lm");
        conditionalReq.Headers.IfModifiedSince = lastModified;

        HttpResponseMessage response = await invoker.SendAsync(conditionalReq, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.NotModified,
            "entry not modified since If-Modified-Since date must return 304");
        callCount.Should().Be(1, "origin must not be contacted");
    }

    // ── If-None-Match with non-matching ETag → 200 ────────────────────────────

    [Fact]
    public async Task FreshEntry_NonMatchingETag_Returns200()
    {
        const string storedEtag = "\"v2\"";
        const string clientEtag = "\"v1\"";
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("new-body") };
            r.Headers.ETag = new EntityTagHeaderValue(storedEtag);
            return r;
        });

        HttpMessageInvoker invoker = new(middleware);

        // Populate the cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/cond-nomatch"), CancellationToken.None);

        // Client has an older ETag
        HttpRequestMessage conditionalReq = new(HttpMethod.Get, "https://api.test/cond-nomatch");
        conditionalReq.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(clientEtag));

        HttpResponseMessage response = await invoker.SendAsync(conditionalReq, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "non-matching ETag means the content has changed; return full 200 from cache");
        callCount.Should().Be(1, "the 200 is still served from cache");
    }

    // ── If-None-Match precedence over If-Modified-Since (RFC 9110 §13.1) ─────

    [Fact]
    public async Task IfNoneMatch_TakesPrecedenceOver_IfModifiedSince()
    {
        const string storedEtag = "\"v1\"";
        DateTimeOffset lastModified = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
            r.Headers.ETag = new EntityTagHeaderValue(storedEtag);
            r.Content.Headers.LastModified = lastModified;
            return r;
        });

        HttpMessageInvoker invoker = new(middleware);

        // Populate the cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/cond-prec"), CancellationToken.None);

        // Client sends BOTH headers: non-matching ETag but matching If-Modified-Since
        // Since If-None-Match takes precedence and the ETag doesn't match → 200 from cache
        HttpRequestMessage conditionalReq = new(HttpMethod.Get, "https://api.test/cond-prec");
        conditionalReq.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"old-etag\""));
        conditionalReq.Headers.IfModifiedSince = lastModified;

        HttpResponseMessage response = await invoker.SendAsync(conditionalReq, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "If-None-Match takes precedence; non-matching ETag must produce 200, not 304");
        callCount.Should().Be(1);
    }

    // ── Stale entry → normal revalidation, not short-circuited ───────────────

    [Fact]
    public async Task StaleEntry_NotShortCircuited_RevalidatesNormally()
    {
        const string etag = "\"v1\"";
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (req.Headers.IfNoneMatch.Any())
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
            r.Headers.ETag = new EntityTagHeaderValue(etag);
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
            return r;
        });

        HttpMessageInvoker invoker = new(middleware);

        // Populate (stale immediately)
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/stale-cond"), CancellationToken.None);

        // Second request with matching ETag but entry is stale → must revalidate with origin
        HttpRequestMessage conditionalReq = new(HttpMethod.Get, "https://api.test/stale-cond");
        conditionalReq.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));

        HttpResponseMessage response = await invoker.SendAsync(conditionalReq, CancellationToken.None);

        callCount.Should().Be(2, "stale entry must trigger real revalidation, not be short-circuited");
    }

    // ── No validator on entry → 200 from cache ────────────────────────────────

    [Fact]
    public async Task FreshEntry_NoValidator_Returns200()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") };
        });

        HttpMessageInvoker invoker = new(middleware);

        // Populate the cache (no ETag, no Last-Modified)
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/cond-novalidator"), CancellationToken.None);

        // Client sends If-None-Match but the stored entry has no ETag
        HttpRequestMessage conditionalReq = new(HttpMethod.Get, "https://api.test/cond-novalidator");
        conditionalReq.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"anything\""));

        HttpResponseMessage response = await invoker.SendAsync(conditionalReq, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "when stored entry has no matching validator, return full 200 from cache");
        callCount.Should().Be(1, "no origin contact needed");
    }

    // ── 304 includes mandatory RFC 9110 §15.4.5 headers and Age ─────────────

    [Fact]
    public async Task NotModified_Response_IncludesAgeAndRequiredHeaders()
    {
        const string etag = "\"v1\"";
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
            r.Headers.ETag = new EntityTagHeaderValue(etag);
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(10) };
            r.Headers.Vary.Add("Accept");
            return r;
        });

        HttpMessageInvoker invoker = new(middleware);

        // Populate the cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/cond-headers"), CancellationToken.None);

        HttpRequestMessage conditionalReq = new(HttpMethod.Get, "https://api.test/cond-headers");
        conditionalReq.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));

        HttpResponseMessage response = await invoker.SendAsync(conditionalReq, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.NotModified);
        response.Headers.Age.Should().NotBeNull("304 must include the Age header");
        response.Headers.ETag.Should().NotBeNull("304 must include the ETag header");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class StubTransport(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
