using Stampede.Http.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Http.Headers;

namespace Stampede.Http.Tests.Caching;

/// <summary>
/// Verifies that the middleware stores and serves multiple representations of the same URL keyed on their
/// <c>Vary</c> header values (RFC 9111 §4.1 secondary cache keys). Before variant support, a second
/// representation overwrote the first at the shared primary key, so content-negotiated resources could never
/// keep more than one variant cached — every alternation was a full refetch.
/// </summary>
public sealed class VaryVariantCachingTests
{
    private readonly DefaultCacheKeyBuilder _keyBuilder = new();

    private static CachingMiddleware BuildPipeline(
        ICacheStore store,
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        CacheOptions? options = null)
    {
        return new CachingMiddleware(store, new DefaultCacheKeyBuilder(),
            options ?? new CacheOptions { DefaultTtl = TimeSpan.FromMinutes(5) })
        {
            InnerHandler = new StubTransport(handler)
        };
    }

    private static HttpRequestMessage Req(string url, string? acceptLanguage)
    {
        HttpRequestMessage req = new(HttpMethod.Get, url);
        if (acceptLanguage is not null)
        {
            req.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        }

        return req;
    }

    /// <summary>Origin that varies on Accept-Language and echoes the negotiated language in the body.</summary>
    private static HttpResponseMessage VaryingByLanguage(HttpRequestMessage request)
    {
        string lang = request.Headers.TryGetValues("Accept-Language", out IEnumerable<string>? v)
            ? string.Join(",", v)
            : "none";

        HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent($"lang={lang}") };
        r.Headers.Vary.Add("Accept-Language");
        return r;
    }

    // ── Multiple variants coexist ────────────────────────────────────────────

    [Fact]
    public async Task TwoVariants_AreCachedIndependently_ThirdAlternatingRequestIsAHit()
    {
        int callCount = 0;
        CachingMiddleware middleware = BuildPipeline(
            new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions())),
            req => { callCount++; return VaryingByLanguage(req); });

        HttpMessageInvoker invoker = new(middleware);
        const string url = "https://api.test/vary/variants";

        // en → miss (origin call 1)
        _ = await invoker.SendAsync(Req(url, "en"), TestContext.Current.CancellationToken);
        // es → miss, different variant (origin call 2) — must NOT overwrite the en variant
        _ = await invoker.SendAsync(Req(url, "es"), TestContext.Current.CancellationToken);

        // en again → this is the regression case: under a single-entry cache the es response would have
        // overwritten en, forcing a third origin call. With variant keys, en is still cached.
        HttpResponseMessage third = await invoker.SendAsync(Req(url, "en"), TestContext.Current.CancellationToken);

        callCount.Should().Be(2, "each language is fetched once; the repeated 'en' request must be a cache hit");
        (await third.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("lang=en",
            "the cached 'en' variant must be returned, not the 'es' representation");
    }

    [Fact]
    public async Task Variants_DoNotCrossContaminate_EachRequestGetsItsOwnRepresentation()
    {
        CachingMiddleware middleware = BuildPipeline(
            new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions())),
            VaryingByLanguage);

        HttpMessageInvoker invoker = new(middleware);
        const string url = "https://api.test/vary/isolation";

        _ = await invoker.SendAsync(Req(url, "en"), TestContext.Current.CancellationToken);
        _ = await invoker.SendAsync(Req(url, "fr"), TestContext.Current.CancellationToken);

        HttpResponseMessage en = await invoker.SendAsync(Req(url, "en"), TestContext.Current.CancellationToken);
        HttpResponseMessage fr = await invoker.SendAsync(Req(url, "fr"), TestContext.Current.CancellationToken);

        (await en.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("lang=en");
        (await fr.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("lang=fr");
    }

    [Fact]
    public async Task SameVariant_RepeatedRequest_IsServedFromCache()
    {
        int callCount = 0;
        CachingMiddleware middleware = BuildPipeline(
            new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions())),
            req => { callCount++; return VaryingByLanguage(req); });

        HttpMessageInvoker invoker = new(middleware);
        const string url = "https://api.test/vary/same";

        _ = await invoker.SendAsync(Req(url, "en"), TestContext.Current.CancellationToken);
        _ = await invoker.SendAsync(Req(url, "en"), TestContext.Current.CancellationToken);

        callCount.Should().Be(1, "identical Vary values must be served from the same variant");
    }

    [Fact]
    public async Task VaryStar_IsNeverServedFromCache()
    {
        int callCount = 0;
        CachingMiddleware middleware = BuildPipeline(
            new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions())),
            req =>
            {
                callCount++;
                HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("data") };
                r.Headers.Vary.Add("*");
                return r;
            });

        HttpMessageInvoker invoker = new(middleware);
        const string url = "https://api.test/vary/star";

        _ = await invoker.SendAsync(Req(url, "en"), TestContext.Current.CancellationToken);
        _ = await invoker.SendAsync(Req(url, "en"), TestContext.Current.CancellationToken);

        callCount.Should().Be(2, "Vary: * must never be served from cache");
    }

    // ── Variant revalidation writes back to the variant key ──────────────────

    [Fact]
    public async Task StaleVariantWithETag_Revalidates_AndRefreshesTheCorrectVariant()
    {
        int originCalls = 0;
        int conditionalCalls = 0;

        CachingMiddleware middleware = BuildPipeline(
            new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions())),
            req =>
            {
                if (req.Headers.IfNoneMatch.Count > 0)
                {
                    conditionalCalls++;
                    HttpResponseMessage nm = new(HttpStatusCode.NotModified);
                    nm.Headers.ETag = new EntityTagHeaderValue("\"en-v1\"");
                    return nm;
                }

                originCalls++;
                HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("lang=en") };
                r.Headers.Vary.Add("Accept-Language");
                r.Headers.ETag = new EntityTagHeaderValue("\"en-v1\"");
                r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
                return r;
            });

        HttpMessageInvoker invoker = new(middleware);
        const string url = "https://api.test/vary/reval";

        // First request stores the en variant (immediately stale via max-age=0).
        _ = await invoker.SendAsync(Req(url, "en"), TestContext.Current.CancellationToken);
        // Second request finds the stale en variant and revalidates it conditionally (304) — not a full refetch.
        HttpResponseMessage second = await invoker.SendAsync(Req(url, "en"), TestContext.Current.CancellationToken);

        originCalls.Should().Be(1, "the variant must be revalidated conditionally, not refetched in full");
        conditionalCalls.Should().Be(1, "the stale en variant must trigger an If-None-Match revalidation");
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("lang=en");
    }

    // ── Distributed store variant support (serialization round-trip) ─────────

    [Fact]
    public async Task Variants_WorkWithDistributedStore()
    {
        IDistributedCache backing = new MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        int callCount = 0;

        CachingMiddleware middleware = BuildPipeline(
            new DistributedCacheStore(backing),
            req => { callCount++; return VaryingByLanguage(req); });

        HttpMessageInvoker invoker = new(middleware);
        const string url = "https://api.test/vary/distributed";

        _ = await invoker.SendAsync(Req(url, "en"), TestContext.Current.CancellationToken);
        _ = await invoker.SendAsync(Req(url, "es"), TestContext.Current.CancellationToken);
        HttpResponseMessage enAgain = await invoker.SendAsync(Req(url, "en"), TestContext.Current.CancellationToken);

        callCount.Should().Be(2, "variant keying must survive JSON serialization in the distributed store");
        (await enAgain.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("lang=en");
    }

    private sealed class StubTransport(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
