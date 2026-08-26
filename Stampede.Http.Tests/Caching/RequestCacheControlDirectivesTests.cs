using Stampede.Http.Caching;
using Stampede.Http.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Http.Headers;

namespace Stampede.Http.Tests.Caching;

/// <summary>
/// Verifies the client request-side <c>Cache-Control</c> directives added in 2.3 (RFC 9111 §5.2.1):
/// <c>max-age</c> and <c>min-fresh</c> tighten what counts as a fresh hit, and <c>max-stale</c> widens
/// it to accept an already-expired entry without contacting the origin.
/// </summary>
public sealed class RequestCacheControlDirectivesTests
{
    private readonly ICacheStore _cache;
    private readonly DefaultCacheKeyBuilder _keyBuilder;
    private readonly FakeTimeProvider _time;

    public RequestCacheControlDirectivesTests()
    {
        _cache = new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions()));
        _keyBuilder = new DefaultCacheKeyBuilder();
        _time = new FakeTimeProvider();
    }

    private (CachingMiddleware middleware, StubTransport stub) BuildPipeline(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        CacheOptions? options = null)
    {
        options ??= new CacheOptions { DefaultTtl = TimeSpan.FromMinutes(5) };
        StubTransport stub = new(handler);
        CachingMiddleware middleware = new(_cache, _keyBuilder, options, timeProvider: _time) { InnerHandler = stub };
        return (middleware, stub);
    }

    private static HttpRequestMessage Req(string url, CacheControlHeaderValue cc) =>
        new(HttpMethod.Get, url) { Headers = { CacheControl = cc } };

    // ── max-age (§5.2.1.1) ────────────────────────────────────────────────────

    [Fact]
    public async Task MaxAge_EntryOlderThanRequested_TriggersRevalidation()
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
            return r;
        });

        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/max-age-old"), CancellationToken.None);
        callCount.Should().Be(1);

        // Entry is 10s old but the server's own TTL (5 min) means it is not structurally expired.
        _time.Advance(TimeSpan.FromSeconds(10));

        HttpResponseMessage response = await invoker.SendAsync(
            Req("https://api.test/max-age-old", new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(5) }),
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        callCount.Should().Be(2, "the client's max-age=5 is tighter than the entry's 10s age, so it must revalidate");
    }

    [Fact]
    public async Task MaxAge_EntryWithinRequested_ServedFromCache()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") };
        });

        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/max-age-fresh"), CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(2));

        HttpResponseMessage response = await invoker.SendAsync(
            Req("https://api.test/max-age-fresh", new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(5) }),
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        callCount.Should().Be(1, "the entry's 2s age satisfies the client's max-age=5, so it must be served from cache");
    }

    [Fact]
    public async Task MaxAge_ImmutableEntry_StillHonoredByRequestDirective()
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
            r.Headers.CacheControl = CacheControlHeaderValue.Parse("max-age=300, immutable");
            return r;
        });

        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/max-age-immutable"), CancellationToken.None);
        callCount.Should().Be(1);

        _time.Advance(TimeSpan.FromSeconds(10));

        // Immutable exempts the entry from the *server's* no-cache/must-revalidate semantics, but a
        // client's own max-age is a distinct recency requirement and must still be honored.
        HttpResponseMessage response = await invoker.SendAsync(
            Req("https://api.test/max-age-immutable", new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(5) }),
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        callCount.Should().Be(2, "immutable does not exempt an entry from the client's own max-age directive");
    }

    // ── min-fresh (§5.2.1.3) ──────────────────────────────────────────────────

    [Fact]
    public async Task MinFresh_EntryWontStayFreshLongEnough_TriggersRevalidation()
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
            return r;
        }, new CacheOptions { DefaultTtl = TimeSpan.FromSeconds(10) });

        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/min-fresh-fail"), CancellationToken.None);
        callCount.Should().Be(1);

        // Entry has ~10s of remaining freshness; the client wants at least 20s more.
        HttpResponseMessage response = await invoker.SendAsync(
            Req("https://api.test/min-fresh-fail", new CacheControlHeaderValue { MinFresh = TimeSpan.FromSeconds(20) }),
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        callCount.Should().Be(2, "the entry won't stay fresh for the requested 20s, so it must revalidate");
    }

    [Fact]
    public async Task MinFresh_EntrySatisfiesRequirement_ServedFromCache()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") };
        }, new CacheOptions { DefaultTtl = TimeSpan.FromSeconds(10) });

        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/min-fresh-ok"), CancellationToken.None);

        HttpResponseMessage response = await invoker.SendAsync(
            Req("https://api.test/min-fresh-ok", new CacheControlHeaderValue { MinFresh = TimeSpan.FromSeconds(5) }),
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        callCount.Should().Be(1, "the entry has ~10s of remaining freshness, satisfying min-fresh=5");
    }

    // ── max-stale (§5.2.1.2) ──────────────────────────────────────────────────

    [Fact]
    public async Task MaxStale_ExpiredEntryWithinLimit_ServedWithoutContactingOrigin()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("stale-body") };
            r.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
            return r;
        });

        HttpMessageInvoker invoker = new(middleware);

        // Stored with max-age=0: expired from the instant it is stored. The ETag is what keeps the
        // entry in the store past that instant (MemoryCacheStore's revalidation grace period) — without
        // it, an entry with no freshness left and no stale window is dropped immediately, since there
        // would be nothing left to ever serve or revalidate it with.
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/max-stale-ok"), CancellationToken.None);
        callCount.Should().Be(1);

        HttpResponseMessage response = await invoker.SendAsync(
            Req("https://api.test/max-stale-ok", new CacheControlHeaderValue { MaxStale = true, MaxStaleLimit = TimeSpan.FromSeconds(100) }),
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        callCount.Should().Be(1, "max-stale=100 covers the entry's staleness, so it must be served without contacting the origin");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("stale-body");
    }

    [Fact]
    public async Task MaxStale_NoValue_AcceptsAnyStaleness()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
            r.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
            return r;
        });

        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/max-stale-unbounded"), CancellationToken.None);

        // Stays within MemoryCacheStore's default 300s revalidation grace so the entry is still there
        // to observe the effect of max-stale on; the grace period, not this test, bounds retention.
        _time.Advance(TimeSpan.FromSeconds(250));

        HttpResponseMessage response = await invoker.SendAsync(
            Req("https://api.test/max-stale-unbounded", new CacheControlHeaderValue { MaxStale = true }),
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        callCount.Should().Be(1, "bare max-stale (no value) accepts any amount of staleness");
    }

    [Fact]
    public async Task MaxStale_ExceedsLimit_FallsThroughToRevalidation()
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
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
            return r;
        });

        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/max-stale-exceeded"), CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(200));

        HttpResponseMessage response = await invoker.SendAsync(
            Req("https://api.test/max-stale-exceeded", new CacheControlHeaderValue { MaxStale = true, MaxStaleLimit = TimeSpan.FromSeconds(50) }),
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        callCount.Should().Be(2, "200s of staleness exceeds max-stale=50, so the entry's validator must be used to revalidate");
    }

    [Fact]
    public async Task MaxStale_MustRevalidateEntry_NeverServedStaleDirectly()
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
            r.Headers.CacheControl = CacheControlHeaderValue.Parse("max-age=0, must-revalidate");
            return r;
        });

        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/max-stale-must-revalidate"), CancellationToken.None);
        callCount.Should().Be(1);

        // Even an enormous max-stale must not override the origin's must-revalidate (§5.2.2.2).
        HttpResponseMessage response = await invoker.SendAsync(
            Req("https://api.test/max-stale-must-revalidate", new CacheControlHeaderValue { MaxStale = true, MaxStaleLimit = TimeSpan.FromDays(1) }),
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        callCount.Should().Be(2, "must-revalidate forbids serving this entry stale under any client max-stale value");
    }

    [Fact]
    public async Task MaxStale_CombinedWithOnlyIfCached_ServesStaleWithout504()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
            r.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
            return r;
        });

        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/max-stale-oic"), CancellationToken.None);

        HttpResponseMessage response = await invoker.SendAsync(
            Req("https://api.test/max-stale-oic", new CacheControlHeaderValue
            {
                MaxStale = true,
                MaxStaleLimit = TimeSpan.FromSeconds(100),
                OnlyIfCached = true
            }),
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "max-stale is checked before the only-if-cached fallback, so a satisfying stale entry must win over 504");
        callCount.Should().Be(1);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class StubTransport(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
