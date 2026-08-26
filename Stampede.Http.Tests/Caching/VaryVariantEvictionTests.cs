using Stampede.Http.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using System.Net;

namespace Stampede.Http.Tests.Caching;

/// <summary>
/// Verifies that explicit eviction of a varying resource sweeps the secondary-key variants tracked by its
/// Vary marker (<see cref="CacheEntry.TrackedKeys"/>), instead of only removing the marker and leaving the
/// variants unreachable-but-alive until their own retention elapses — the pre-2.6 trade-off.
/// </summary>
public sealed class VaryVariantEvictionTests
{
    private const string Url = "https://api.test/vary/evict";

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

    private static HttpRequestMessage Req(string url, string acceptLanguage)
    {
        HttpRequestMessage req = new(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        return req;
    }

    private static HttpResponseMessage VaryingByLanguage(HttpRequestMessage request)
    {
        string lang = request.Headers.TryGetValues("Accept-Language", out IEnumerable<string>? v)
            ? string.Join(",", v)
            : "none";

        HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent($"lang={lang}") };
        r.Headers.Vary.Add("Accept-Language");
        return r;
    }

    [Fact]
    public async Task Marker_TracksEveryStoredVariantKey()
    {
        RecordingCacheStore store = new(new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions())));
        HttpMessageInvoker invoker = new(BuildPipeline(store, VaryingByLanguage));

        _ = await invoker.SendAsync(Req(Url, "en"), TestContext.Current.CancellationToken);
        _ = await invoker.SendAsync(Req(Url, "es"), TestContext.Current.CancellationToken);

        string primaryKey = new DefaultCacheKeyBuilder().Build(new HttpRequestMessage(HttpMethod.Get, Url));
        store.TryGetValue(primaryKey, out CacheEntry? marker).Should().BeTrue();

        marker!.IsVaryMarker.Should().BeTrue();
        marker.TrackedKeys.Should().HaveCount(2, "the marker must track one secondary key per stored variant");
        marker.TrackedKeys.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Marker_ReStoringSameVariant_DoesNotDuplicateItsKey()
    {
        RecordingCacheStore store = new(new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions())));

        // max-age=0 marks entries immediately stale, so every request re-stores the same variant.
        HttpMessageInvoker invoker = new(BuildPipeline(store, req =>
        {
            HttpResponseMessage r = VaryingByLanguage(req);
            r.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
            r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return r;
        }));

        _ = await invoker.SendAsync(Req(Url, "en"), TestContext.Current.CancellationToken);
        _ = await invoker.SendAsync(Req(Url, "en"), TestContext.Current.CancellationToken);

        string primaryKey = new DefaultCacheKeyBuilder().Build(new HttpRequestMessage(HttpMethod.Get, Url));
        store.TryGetValue(primaryKey, out CacheEntry? marker).Should().BeTrue();

        marker!.TrackedKeys.Should().HaveCount(1, "re-storing an existing variant must merge, not append");
    }

    [Fact]
    public async Task EvictAsync_RemovesMarkerAndEveryTrackedVariant_FromTheStore()
    {
        RecordingCacheStore store = new(new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions())));
        DefaultCacheKeyBuilder keyBuilder = new();
        HttpMessageInvoker invoker = new(BuildPipeline(store, VaryingByLanguage));

        _ = await invoker.SendAsync(Req(Url, "en"), TestContext.Current.CancellationToken);
        _ = await invoker.SendAsync(Req(Url, "es"), TestContext.Current.CancellationToken);
        store.LiveKeys.Should().HaveCount(3, "two variants plus the primary-key marker are stored");

        await new StampedeHttpCache(store, keyBuilder).EvictAsync(new Uri(Url), TestContext.Current.CancellationToken);

        store.LiveKeys.Should().BeEmpty(
            "eviction must sweep the tracked variants too, not just the marker — a bare marker removal leaves them unreachable-but-alive");
    }

    [Fact]
    public async Task EvictAsync_VaryingEntry_NextRequestsRefetchEveryVariant()
    {
        int callCount = 0;
        MemoryCacheStore store = new(new MemoryCache(new MemoryCacheOptions()));
        HttpMessageInvoker invoker = new(BuildPipeline(store, req => { callCount++; return VaryingByLanguage(req); }));

        _ = await invoker.SendAsync(Req(Url, "en"), TestContext.Current.CancellationToken);
        _ = await invoker.SendAsync(Req(Url, "es"), TestContext.Current.CancellationToken);
        _ = await invoker.SendAsync(Req(Url, "en"), TestContext.Current.CancellationToken);
        callCount.Should().Be(2, "both variants are cached before eviction");

        await new StampedeHttpCache(store, new DefaultCacheKeyBuilder()).EvictAsync(new Uri(Url), TestContext.Current.CancellationToken);

        _ = await invoker.SendAsync(Req(Url, "en"), TestContext.Current.CancellationToken);
        _ = await invoker.SendAsync(Req(Url, "es"), TestContext.Current.CancellationToken);
        callCount.Should().Be(4, "after eviction both variants must be fetched from the origin again");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Wraps a real store and tracks which keys currently hold an entry, so tests can assert that an
    /// eviction physically removed every stored key — something the middleware's behavior alone cannot
    /// distinguish from variants merely becoming unreachable.
    /// </summary>
    private sealed class RecordingCacheStore(ICacheStore inner) : ICacheStore
    {
        private readonly HashSet<string> _liveKeys = [];

        public IReadOnlyCollection<string> LiveKeys => _liveKeys;

        public bool TryGetValue(string key, out CacheEntry? entry) => inner.TryGetValue(key, out entry);

        public void Set(string key, CacheEntry entry)
        {
            inner.Set(key, entry);
            _ = _liveKeys.Add(key);
        }

        public void Remove(string key)
        {
            inner.Remove(key);
            _ = _liveKeys.Remove(key);
        }
    }

    private sealed class StubTransport(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
