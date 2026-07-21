using Stampede.Http.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Http.Headers;

namespace Stampede.Http.Tests.Caching;

/// <summary>
/// Regression tests that run against the real clock end-to-end. Most caching tests advance a
/// <see cref="Helpers.FakeTimeProvider"/>, but <see cref="IMemoryCache"/> evicts on the system
/// clock — so those tests can never observe a physical eviction. This class exercises the case
/// that slipped through: a response with <c>max-age=N</c>, a validator, and no stale windows was
/// evicted from the store exactly at expiry, so <see cref="CachingMiddleware"/> could never send
/// a conditional revalidation and every expiry degraded to a full refetch.
/// </summary>
public sealed class RealClockRevalidationTests
{
    private static CachingMiddleware BuildPipeline(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        CacheOptions options)
    {
        MemoryCacheStore store = new(new MemoryCache(new MemoryCacheOptions()), options);
        return new CachingMiddleware(store, new DefaultCacheKeyBuilder(), options)
        {
            InnerHandler = new StubTransport(handler)
        };
    }

    private static HttpResponseMessage OkWithETag(string body, string eTag)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new StringContent(body) };
        response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(1) };
        response.Headers.ETag = new EntityTagHeaderValue(eTag);
        return response;
    }

    [Fact]
    public async Task ExpiredEntryWithETag_DefaultOptions_SendsIfNoneMatchAndServes304FromCache()
    {
        const string eTag = "\"v1\"";
        List<string?> ifNoneMatchPerCall = [];

        CachingMiddleware middleware = BuildPipeline(req =>
        {
            ifNoneMatchPerCall.Add(req.Headers.IfNoneMatch.Count > 0 ? req.Headers.IfNoneMatch.First().Tag : null);

            if (ifNoneMatchPerCall.Count == 1)
            {
                return OkWithETag("origin-body", eTag);
            }

            // Conditional revalidation succeeded — resource unchanged
            HttpResponseMessage notModified = new(HttpStatusCode.NotModified);
            notModified.Headers.ETag = new EntityTagHeaderValue(eTag);
            return notModified;
        }, new CacheOptions());

        HttpMessageInvoker invoker = new(middleware);
        const string url = "https://api.test/real-clock/etag";

        HttpResponseMessage first = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), TestContext.Current.CancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // Let max-age=1 elapse on the REAL clock, so IMemoryCache would evict the entry
        // if the eviction TTL did not include the revalidation grace period.
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        HttpResponseMessage second = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), TestContext.Current.CancellationToken);

        ifNoneMatchPerCall.Should().HaveCount(2, "the stale entry must trigger a conditional request, not disappear");
        ifNoneMatchPerCall[1].Should().Be(eTag, "the retained entry's ETag must be sent as If-None-Match");
        second.StatusCode.Should().Be(HttpStatusCode.OK, "the 304 must be converted into the cached response");
        (await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("origin-body");
    }

    [Fact]
    public async Task ExpiredEntryWithETag_ZeroGrace_IsRefetchedWithoutConditionalHeaders()
    {
        List<string?> ifNoneMatchPerCall = [];

        CachingMiddleware middleware = BuildPipeline(req =>
        {
            ifNoneMatchPerCall.Add(req.Headers.IfNoneMatch.Count > 0 ? req.Headers.IfNoneMatch.First().Tag : null);
            return OkWithETag("origin-body", "\"v1\"");
        }, new CacheOptions { RevalidationGraceSeconds = 0 });

        HttpMessageInvoker invoker = new(middleware);
        const string url = "https://api.test/real-clock/zero-grace";

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), TestContext.Current.CancellationToken);

        await Task.Delay(1500, TestContext.Current.CancellationToken);

        HttpResponseMessage second = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), TestContext.Current.CancellationToken);

        ifNoneMatchPerCall.Should().HaveCount(2);
        ifNoneMatchPerCall[1].Should().BeNull("with a zero grace period the entry is evicted at expiry, so no validator is available");
        second.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed class StubTransport(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
