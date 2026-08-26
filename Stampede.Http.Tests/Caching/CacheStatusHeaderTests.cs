using Stampede.Http.Caching;
using Stampede.Http.Coalescing;
using Stampede.Http.Extensions;
using Stampede.Http.Handlers;
using Stampede.Http.Options;
using Stampede.Http.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;

namespace Stampede.Http.Tests.Caching;

/// <summary>
/// Verifies the synthetic <c>X-Stampede-Cache</c> response header (<see cref="StampedeCacheStatus"/>):
/// every response the caching layer handles reports how it was obtained — HIT, MISS, STALE, REVALIDATED —
/// and coalesced waiters report COALESCED, while untouched requests carry no header at all.
/// </summary>
public sealed class CacheStatusHeaderTests
{
    private static CachingMiddleware BuildPipeline(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        CacheOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        return new CachingMiddleware(
            new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions())),
            new DefaultCacheKeyBuilder(),
            options ?? new CacheOptions { DefaultTtl = TimeSpan.FromMinutes(5) },
            timeProvider: timeProvider)
        {
            InnerHandler = new StubTransport(handler)
        };
    }

    [Fact]
    public async Task MissThenHit_ReportsEachStatus()
    {
        HttpMessageInvoker invoker = new(BuildPipeline(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") }));

        HttpResponseMessage first = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.test/status/basic"), TestContext.Current.CancellationToken);
        HttpResponseMessage second = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.test/status/basic"), TestContext.Current.CancellationToken);

        StampedeCacheStatus.GetStatus(first).Should().Be(StampedeCacheStatus.Miss);
        StampedeCacheStatus.GetStatus(second).Should().Be(StampedeCacheStatus.Hit);
    }

    [Fact]
    public async Task Revalidation304_ReportsRevalidated()
    {
        FakeTimeProvider clock = new();
        int calls = 0;

        HttpMessageInvoker invoker = new(BuildPipeline(req =>
        {
            calls++;

            if (req.Headers.IfNoneMatch.Count > 0)
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
            r.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(30) };
            return r;
        }, timeProvider: clock));

        _ = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.test/status/reval"), TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(60)); // past max-age, within revalidation grace

        HttpResponseMessage revalidated = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.test/status/reval"), TestContext.Current.CancellationToken);

        calls.Should().Be(2, "the second request must be a conditional revalidation");
        StampedeCacheStatus.GetStatus(revalidated).Should().Be(StampedeCacheStatus.Revalidated);
    }

    [Fact]
    public async Task StaleWhileRevalidate_ReportsStale()
    {
        FakeTimeProvider clock = new();

        HttpMessageInvoker invoker = new(BuildPipeline(_ =>
        {
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
            r.Headers.TryAddWithoutValidation("Cache-Control", "max-age=10, stale-while-revalidate=120");
            return r;
        }, timeProvider: clock));

        _ = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.test/status/swr"), TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(30)); // expired, inside the stale-while-revalidate window

        HttpResponseMessage stale = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.test/status/swr"), TestContext.Current.CancellationToken);

        StampedeCacheStatus.GetStatus(stale).Should().Be(StampedeCacheStatus.Stale);
    }

    [Fact]
    public async Task StaleIfError_ReportsStale()
    {
        FakeTimeProvider clock = new();
        bool failNow = false;

        HttpMessageInvoker invoker = new(BuildPipeline(_ =>
        {
            if (failNow)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
            r.Headers.TryAddWithoutValidation("Cache-Control", "max-age=10, stale-if-error=120");
            return r;
        }, timeProvider: clock));

        _ = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.test/status/sie"), TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(30));
        failNow = true;

        HttpResponseMessage stale = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.test/status/sie"), TestContext.Current.CancellationToken);

        stale.StatusCode.Should().Be(HttpStatusCode.OK, "the stale entry is served instead of the 500");
        StampedeCacheStatus.GetStatus(stale).Should().Be(StampedeCacheStatus.Stale);
    }

    [Fact]
    public async Task NonCacheableMethod_CarriesNoStatusHeader()
    {
        HttpMessageInvoker invoker = new(BuildPipeline(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("done") }));

        HttpResponseMessage post = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "https://api.test/status/post"), TestContext.Current.CancellationToken);

        StampedeCacheStatus.GetStatus(post).Should().BeNull("the caching layer does not handle unsafe methods");
    }

    [Fact]
    public async Task BypassCache_CarriesNoStatusHeader()
    {
        HttpMessageInvoker invoker = new(BuildPipeline(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") }));

        HttpRequestMessage request = new(HttpMethod.Get, "https://api.test/status/bypass");
        request.Options.Set(CacheRequestPolicy.BypassCache, true);

        HttpResponseMessage response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        StampedeCacheStatus.GetStatus(response).Should().BeNull("BypassCache skips the caching layer entirely");
    }

    [Fact]
    public async Task CoalescedWaiter_ReportsCoalesced_WinnerDoesNot()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int originCalls = 0;

        CoalescingHandler handler = new(new RequestCoalescer(new CoalescerOptions()))
        {
            InnerHandler = new AsyncStubTransport(async () =>
            {
                Interlocked.Increment(ref originCalls);
                await release.Task;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") };
            })
        };

        HttpMessageInvoker invoker = new(handler);
        const string url = "https://api.test/status/coalesced";

        Task<HttpResponseMessage> first = invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), TestContext.Current.CancellationToken);
        Task<HttpResponseMessage> second = invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), TestContext.Current.CancellationToken);

        // Wait until one caller owns the in-flight slot, then let the origin answer.
        while (Volatile.Read(ref originCalls) == 0)
        {
            await Task.Yield();
        }

        release.SetResult();
        HttpResponseMessage[] responses = await Task.WhenAll(first, second);

        originCalls.Should().Be(1, "both callers share one origin call");
        responses.Count(r => StampedeCacheStatus.GetStatus(r) == StampedeCacheStatus.Coalesced)
            .Should().Be(1, "exactly the waiter reports COALESCED — the winner performed the origin call itself");
    }

    [Fact]
    public async Task FullPipeline_CoalescedIsNotOverwrittenByMiss_AndLaterHitsReportHit()
    {
        ServiceCollection services = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int originCalls = 0;

        services.AddHttpClient("catalog")
            .AddStampedeHttp(o => o.DefaultTtl = TimeSpan.FromMinutes(5))
            .ConfigurePrimaryHttpMessageHandler(() => new AsyncStubTransport(async () =>
            {
                Interlocked.Increment(ref originCalls);
                await release.Task;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") };
            }));

        ServiceProvider sp = services.BuildServiceProvider();
        HttpClient client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("catalog");
        const string url = "https://api.test/status/pipeline";

        Task<HttpResponseMessage> first = client.GetAsync(url, TestContext.Current.CancellationToken);
        Task<HttpResponseMessage> second = client.GetAsync(url, TestContext.Current.CancellationToken);

        while (Volatile.Read(ref originCalls) == 0)
        {
            await Task.Yield();
        }

        release.SetResult();
        HttpResponseMessage[] responses = await Task.WhenAll(first, second);

        originCalls.Should().Be(1);
        string?[] statuses = [.. responses.Select(StampedeCacheStatus.GetStatus)];
        statuses.Should().Contain(StampedeCacheStatus.Miss, "the winner's response is an origin fetch");
        statuses.Should().Contain(StampedeCacheStatus.Coalesced, "the waiter's COALESCED must survive the caching layer's miss marking");

        HttpResponseMessage third = await client.GetAsync(url, TestContext.Current.CancellationToken);
        StampedeCacheStatus.GetStatus(third).Should().Be(StampedeCacheStatus.Hit,
            "a stored entry must report its own status when replayed, never the COALESCED/MISS of the response that populated it");
    }

    [Fact]
    public void GetStatus_AbsentHeader_ReturnsNull()
    {
        using HttpResponseMessage response = new(HttpStatusCode.OK);

        StampedeCacheStatus.GetStatus(response).Should().BeNull();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class StubTransport(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }

    private sealed class AsyncStubTransport(Func<Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => handler();
    }
}
