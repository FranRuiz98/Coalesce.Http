using Coalesce.Http.Caching;
using Coalesce.Http.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Http.Headers;

namespace Coalesce.Http.Tests.Caching;

/// <summary>
/// Verifies that <see cref="CachingMiddleware"/> uses the injected <see cref="TimeProvider"/>
/// for all time-based decisions, enabling fully deterministic tests without real-time delays.
/// </summary>
public sealed class TimeProviderCachingTests
{
    private readonly DefaultCacheKeyBuilder _keyBuilder = new();

    private (CachingMiddleware middleware, FakeTimeProvider clock) BuildPipeline(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        CacheOptions? options = null,
        ICacheStore? store = null)
    {
        FakeTimeProvider clock = new();
        options ??= new CacheOptions { DefaultTtl = TimeSpan.FromMinutes(5) };
        store ??= new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions()));
        StubTransport stub = new(handler);
        CachingMiddleware middleware = new(store, _keyBuilder, options, timeProvider: clock)
        {
            InnerHandler = stub
        };
        return (middleware, clock);
    }

    private static HttpMessageInvoker Invoker(CachingMiddleware m) => new(m);

    // ── Fresh / stale transitions ─────────────────────────────────────────────

    [Fact]
    public async Task FreshEntry_BeforeExpiry_ServedFromCache()
    {
        int callCount = 0;
        (CachingMiddleware middleware, FakeTimeProvider clock) = BuildPipeline(_ =>
        {
            callCount++;
            return OkResponse("body");
        }, new CacheOptions { DefaultTtl = TimeSpan.FromMinutes(5) });

        HttpMessageInvoker invoker = Invoker(middleware);
        const string url = "https://api.test/tp/fresh";

        _ = await invoker.SendAsync(Req(url), CancellationToken.None);

        // Advance 4 minutes — still fresh
        clock.Advance(TimeSpan.FromMinutes(4));

        HttpResponseMessage second = await invoker.SendAsync(Req(url), CancellationToken.None);

        callCount.Should().Be(1, "entry should still be fresh after 4 minutes within a 5-minute TTL");
        second.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ExpiredEntry_AfterTtlElapsed_IsReRequested()
    {
        int callCount = 0;
        (CachingMiddleware middleware, FakeTimeProvider clock) = BuildPipeline(_ =>
        {
            callCount++;
            return OkResponse("body");
        }, new CacheOptions { DefaultTtl = TimeSpan.FromMinutes(5) });

        HttpMessageInvoker invoker = Invoker(middleware);
        const string url = "https://api.test/tp/expired";

        _ = await invoker.SendAsync(Req(url), CancellationToken.None);

        // Advance past the TTL — entry is now stale
        clock.Advance(TimeSpan.FromMinutes(6));

        _ = await invoker.SendAsync(Req(url), CancellationToken.None);

        callCount.Should().Be(2, "origin should be called again once the entry expires");
    }

    [Fact]
    public async Task ExactTtlBoundary_EntryIsStaleAtExpiry()
    {
        int callCount = 0;
        (CachingMiddleware middleware, FakeTimeProvider clock) = BuildPipeline(_ =>
        {
            callCount++;
            return OkResponse("body");
        }, new CacheOptions { DefaultTtl = TimeSpan.FromMinutes(5) });

        HttpMessageInvoker invoker = Invoker(middleware);
        const string url = "https://api.test/tp/boundary";

        _ = await invoker.SendAsync(Req(url), CancellationToken.None);

        // Advance exactly to the expiry instant — should be considered stale (>= semantics)
        clock.Advance(TimeSpan.FromMinutes(5));

        _ = await invoker.SendAsync(Req(url), CancellationToken.None);

        callCount.Should().Be(2, "at the exact expiry instant the entry is stale and origin must be called");
    }

    // ── stale-while-revalidate ────────────────────────────────────────────────

    [Fact]
    public async Task StaleWhileRevalidate_WithinWindow_ServesStaleImmediately()
    {
        const string body = "swr-body";
        int callCount = 0;

        (CachingMiddleware middleware, FakeTimeProvider clock) = BuildPipeline(req =>
        {
            callCount++;
            HttpResponseMessage r = OkResponse(body);
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(1) };
            r.Headers.TryAddWithoutValidation("Cache-Control", "stale-while-revalidate=300");
            return r;
        });

        HttpMessageInvoker invoker = Invoker(middleware);
        const string url = "https://api.test/tp/swr";

        _ = await invoker.SendAsync(Req(url), CancellationToken.None);
        callCount.Should().Be(1);

        // Advance 2 minutes → stale, but within the 5-minute swr window
        clock.Advance(TimeSpan.FromMinutes(2));

        HttpResponseMessage staleResponse = await invoker.SendAsync(Req(url), CancellationToken.None);

        staleResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "stale-while-revalidate should serve the cached response immediately without blocking");

        // Give the background task time to complete
        await Task.Delay(200);
        callCount.Should().BeGreaterThanOrEqualTo(2, "background revalidation should have fired");
    }

    [Fact]
    public async Task StaleWhileRevalidate_OutsideWindow_DoesNotServeStale()
    {
        int callCount = 0;

        (CachingMiddleware middleware, FakeTimeProvider clock) = BuildPipeline(req =>
        {
            callCount++;
            HttpResponseMessage r = OkResponse("body");
            // max-age=1min, swr=2min → window expires after 3 minutes total
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(1) };
            r.Headers.TryAddWithoutValidation("Cache-Control", "stale-while-revalidate=120");
            return r;
        });

        HttpMessageInvoker invoker = Invoker(middleware);
        const string url = "https://api.test/tp/swr-expired";

        _ = await invoker.SendAsync(Req(url), CancellationToken.None);

        // Advance 4 minutes — outside the 3-minute total window
        clock.Advance(TimeSpan.FromMinutes(4));

        _ = await invoker.SendAsync(Req(url), CancellationToken.None);

        // Must not have been served stale — a direct origin request should have been made
        callCount.Should().Be(2, "origin must be called when outside the stale-while-revalidate window");
    }

    // ── stale-if-error ────────────────────────────────────────────────────────

    [Fact]
    public async Task StaleIfError_WithinWindow_ServesStaleOn5xx()
    {
        const string body = "sie-body";
        int callCount = 0;

        (CachingMiddleware middleware, FakeTimeProvider clock) = BuildPipeline(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                HttpResponseMessage r = OkResponse(body);
                r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(1) };
                r.Headers.TryAddWithoutValidation("Cache-Control", "stale-if-error=3600");
                return r;
            }
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        HttpMessageInvoker invoker = Invoker(middleware);
        const string url = "https://api.test/tp/sie";

        _ = await invoker.SendAsync(Req(url), CancellationToken.None);

        // Advance 2 minutes — stale but within the 60-minute stale-if-error window
        clock.Advance(TimeSpan.FromMinutes(2));

        HttpResponseMessage response = await invoker.SendAsync(Req(url), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "stale-if-error must serve the cached response when the origin returns 5xx within the window");
        string returned = await response.Content.ReadAsStringAsync();
        returned.Should().Be(body);
    }

    [Fact]
    public async Task StaleIfError_OutsideWindow_PropagatesError()
    {
        int callCount = 0;

        (CachingMiddleware middleware, FakeTimeProvider clock) = BuildPipeline(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                HttpResponseMessage r = OkResponse("body");
                r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(1) };
                r.Headers.TryAddWithoutValidation("Cache-Control", "stale-if-error=60");
                return r;
            }
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        HttpMessageInvoker invoker = Invoker(middleware);
        const string url = "https://api.test/tp/sie-expired";

        _ = await invoker.SendAsync(Req(url), CancellationToken.None);

        // Advance 3 minutes — stale-if-error window (1 min) + max-age (1 min) both expired
        clock.Advance(TimeSpan.FromMinutes(3));

        HttpResponseMessage response = await invoker.SendAsync(Req(url), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "the error must be propagated when outside the stale-if-error window");
    }

    // ── Age header ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AgeHeader_ReflectsElapsedTimeSinceStorage()
    {
        (CachingMiddleware middleware, FakeTimeProvider clock) = BuildPipeline(_ => OkResponse("body"),
            new CacheOptions { DefaultTtl = TimeSpan.FromMinutes(10) });

        HttpMessageInvoker invoker = Invoker(middleware);
        const string url = "https://api.test/tp/age";

        _ = await invoker.SendAsync(Req(url), CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(90));

        HttpResponseMessage cached = await invoker.SendAsync(Req(url), CancellationToken.None);

        cached.Headers.Age.Should().NotBeNull();
        cached.Headers.Age!.Value.TotalSeconds.Should().BeApproximately(90, 2,
            "Age header should reflect seconds elapsed since the entry was stored");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HttpRequestMessage Req(string url) => new(HttpMethod.Get, url);

    private static HttpResponseMessage OkResponse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class StubTransport(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
