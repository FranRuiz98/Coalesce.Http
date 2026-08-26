using Stampede.Http.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using System.Net;

namespace Stampede.Http.Tests.Caching;

/// <summary>
/// Verifies tag-based invalidation: tags collected from the response headers named in
/// <see cref="CacheOptions.TagHeaderNames"/> (or attached per request via
/// <see cref="CacheRequestPolicy.Tags"/>) index stored entries so
/// <see cref="IStampedeHttpCache.EvictByTagAsync"/> can invalidate a whole group of URIs in one call.
/// </summary>
public sealed class TagInvalidationTests
{
    private static CachingMiddleware BuildPipeline(
        ICacheStore store,
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        CacheOptions options)
    {
        return new CachingMiddleware(store, new DefaultCacheKeyBuilder(), options)
        {
            InnerHandler = new StubTransport(handler)
        };
    }

    private static CacheOptions OptionsWithTagHeader(params string[] headerNames) => new()
    {
        DefaultTtl = TimeSpan.FromMinutes(5),
        TagHeaderNames = headerNames
    };

    private static HttpResponseMessage TaggedResponse(string body, string headerName, string headerValue)
    {
        HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent(body) };
        r.Headers.TryAddWithoutValidation(headerName, headerValue);
        return r;
    }

    [Fact]
    public async Task EvictByTag_EvictsEveryTaggedUri_AndLeavesOthersCached()
    {
        int callCount = 0;
        MemoryCacheStore store = new(new MemoryCache(new MemoryCacheOptions()));
        HttpMessageInvoker invoker = new(BuildPipeline(store, req =>
        {
            callCount++;
            string tag = req.RequestUri!.AbsolutePath.Contains("other") ? "misc" : "products";
            return TaggedResponse(req.RequestUri.AbsolutePath, "Cache-Tag", tag);
        }, OptionsWithTagHeader("Cache-Tag")));

        Uri productA = new("https://api.test/products/1");
        Uri productB = new("https://api.test/products/2");
        Uri other = new("https://api.test/other");

        foreach (Uri uri in new[] { productA, productB, other, productA, productB, other })
        {
            _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), TestContext.Current.CancellationToken);
        }

        callCount.Should().Be(3, "the second round is served entirely from cache");

        await new StampedeHttpCache(store, new DefaultCacheKeyBuilder())
            .EvictByTagAsync("products", TestContext.Current.CancellationToken);

        foreach (Uri uri in new[] { productA, productB, other })
        {
            _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), TestContext.Current.CancellationToken);
        }

        callCount.Should().Be(5, "both 'products' entries must be refetched while the 'misc'-tagged entry stays cached");
    }

    [Fact]
    public async Task EvictByTag_SpaceSeparatedSurrogateKeys_EachTagIsIndexed()
    {
        int callCount = 0;
        MemoryCacheStore store = new(new MemoryCache(new MemoryCacheOptions()));
        HttpMessageInvoker invoker = new(BuildPipeline(store,
            _ => { callCount++; return TaggedResponse("body", "Surrogate-Key", "products featured"); },
            OptionsWithTagHeader("Surrogate-Key")));

        Uri uri = new("https://api.test/surrogate");
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), TestContext.Current.CancellationToken);
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), TestContext.Current.CancellationToken);
        callCount.Should().Be(1);

        // Fastly-style Surrogate-Key values are space-separated: each token is its own tag.
        await new StampedeHttpCache(store, new DefaultCacheKeyBuilder())
            .EvictByTagAsync("featured", TestContext.Current.CancellationToken);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), TestContext.Current.CancellationToken);
        callCount.Should().Be(2, "evicting either space-separated tag must reach the entry");
    }

    [Fact]
    public async Task RequestPolicyTags_IndexWithoutAnyTagHeaderConfigured()
    {
        int callCount = 0;
        MemoryCacheStore store = new(new MemoryCache(new MemoryCacheOptions()));
        HttpMessageInvoker invoker = new(BuildPipeline(store,
            _ => { callCount++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") }; },
            new CacheOptions { DefaultTtl = TimeSpan.FromMinutes(5) }));

        Uri uri = new("https://api.test/request-tags");

        HttpRequestMessage first = new(HttpMethod.Get, uri);
        first.Options.Set(CacheRequestPolicy.Tags, ["client-group"]);
        _ = await invoker.SendAsync(first, TestContext.Current.CancellationToken);
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), TestContext.Current.CancellationToken);
        callCount.Should().Be(1);

        await new StampedeHttpCache(store, new DefaultCacheKeyBuilder())
            .EvictByTagAsync("client-group", TestContext.Current.CancellationToken);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), TestContext.Current.CancellationToken);
        callCount.Should().Be(2, "per-request tags must be honored even with TagHeaderNames unset");
    }

    [Fact]
    public async Task EvictByTag_UnknownTag_IsANoOp()
    {
        StampedeHttpCache cache = new(
            new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions())),
            new DefaultCacheKeyBuilder());

        Func<Task> act = async () => await cache.EvictByTagAsync("never-used", TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync("evicting a tag nothing carries is a no-op, not an error");
    }

    [Fact]
    public async Task EvictByTag_NullOrWhitespaceTag_Throws()
    {
        StampedeHttpCache cache = new(
            new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions())),
            new DefaultCacheKeyBuilder());

        Func<Task> nullTag = async () => await cache.EvictByTagAsync(null!, TestContext.Current.CancellationToken);
        Func<Task> blankTag = async () => await cache.EvictByTagAsync("  ", TestContext.Current.CancellationToken);

        await nullTag.Should().ThrowAsync<ArgumentNullException>();
        await blankTag.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task EvictByTag_VaryingEntry_SweepsAllVariants()
    {
        int callCount = 0;
        MemoryCacheStore store = new(new MemoryCache(new MemoryCacheOptions()));
        HttpMessageInvoker invoker = new(BuildPipeline(store, req =>
        {
            callCount++;
            string lang = req.Headers.TryGetValues("Accept-Language", out IEnumerable<string>? v) ? string.Join(",", v) : "none";
            HttpResponseMessage r = TaggedResponse($"lang={lang}", "Cache-Tag", "products");
            r.Headers.Vary.Add("Accept-Language");
            return r;
        }, OptionsWithTagHeader("Cache-Tag")));

        Uri uri = new("https://api.test/tagged-vary");

        HttpRequestMessage Localized(string lang)
        {
            HttpRequestMessage req = new(HttpMethod.Get, uri);
            req.Headers.TryAddWithoutValidation("Accept-Language", lang);
            return req;
        }

        _ = await invoker.SendAsync(Localized("en"), TestContext.Current.CancellationToken);
        _ = await invoker.SendAsync(Localized("es"), TestContext.Current.CancellationToken);
        _ = await invoker.SendAsync(Localized("en"), TestContext.Current.CancellationToken);
        callCount.Should().Be(2, "both language variants are cached");

        await new StampedeHttpCache(store, new DefaultCacheKeyBuilder())
            .EvictByTagAsync("products", TestContext.Current.CancellationToken);

        _ = await invoker.SendAsync(Localized("en"), TestContext.Current.CancellationToken);
        _ = await invoker.SendAsync(Localized("es"), TestContext.Current.CancellationToken);
        callCount.Should().Be(4, "tag eviction must sweep every variant of the tagged URI, not just its marker");
    }

    [Fact]
    public async Task EvictByTag_WorksAcrossDistributedStoreSerialization()
    {
        int callCount = 0;
        DistributedCacheStore store = new(
            new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())),
            new CacheOptions());

        HttpMessageInvoker invoker = new(BuildPipeline(store,
            _ => { callCount++; return TaggedResponse("body", "Cache-Tag", "products"); },
            OptionsWithTagHeader("Cache-Tag")));

        Uri uri = new("https://api.test/distributed-tags");
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), TestContext.Current.CancellationToken);
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), TestContext.Current.CancellationToken);
        callCount.Should().Be(1);

        await new StampedeHttpCache(store, new DefaultCacheKeyBuilder())
            .EvictByTagAsync("products", TestContext.Current.CancellationToken);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), TestContext.Current.CancellationToken);
        callCount.Should().Be(2, "the tag index (CacheEntry.TrackedKeys) must survive the JSON round-trip of the distributed store");
    }

    [Fact]
    public async Task Revalidation304_ExtendsTheTagIndexDeadline_AlongWithTheEntry()
    {
        Helpers.FakeTimeProvider clock = new();
        MemoryCacheStore store = new(new MemoryCache(new MemoryCacheOptions()));

        CachingMiddleware middleware = new(store, new DefaultCacheKeyBuilder(),
            OptionsWithTagHeader("Cache-Tag"), timeProvider: clock)
        {
            InnerHandler = new StubTransport(req =>
            {
                if (req.Headers.IfNoneMatch.Count > 0)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotModified);
                }

                HttpResponseMessage r = TaggedResponse("body", "Cache-Tag", "products");
                r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
                r.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(30) };
                return r;
            })
        };

        HttpMessageInvoker invoker = new(middleware);
        Uri uri = new("https://api.test/reval-extends-index");

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), TestContext.Current.CancellationToken);

        store.TryGetValue(CacheIndexing.BuildTagKey("products"), out CacheEntry? initialIndex).Should().BeTrue();
        DateTimeOffset initialDeadline = initialIndex!.ExpiresAt;

        // A 304 refresh restarts the entry's retention from the revalidation time; the tag index must
        // follow, or an entry that keeps revalidating would eventually outlive its index and become
        // unreachable by EvictByTagAsync.
        clock.Advance(TimeSpan.FromSeconds(120));
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), TestContext.Current.CancellationToken);

        store.TryGetValue(CacheIndexing.BuildTagKey("products"), out CacheEntry? refreshedIndex).Should().BeTrue();
        refreshedIndex!.ExpiresAt.Should().BeAfter(initialDeadline,
            "the 304 refresh must push the tag index deadline out to the entry's new retention deadline");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class StubTransport(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
