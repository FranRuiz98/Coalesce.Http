using Stampede.Http.Caching;
using Stampede.Http.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Http.Headers;

namespace Stampede.Http.Tests.Caching;

/// <summary>
/// Regression tests for the <c>Age</c> header after a <c>304 Not Modified</c> revalidation.
/// RFC 9111 §4.3.4 requires the stored response to be updated with the 304's header fields, and
/// §4.2.3 restarts the age calculation from the validation response — so a successful revalidation
/// must reset <see cref="CacheEntry.StoredAt"/>. Previously only <c>ExpiresAt</c> was refreshed,
/// so <c>Age</c> kept growing from the original store time (observed live: <c>max-age=10</c>
/// reporting <c>Age: 65</c>, <c>68</c>, <c>70</c>… across revalidations).
/// </summary>
public sealed class NotModifiedAgeResetTests
{
    private const string Url = "https://api.test/age-reset/resource";
    private const string ETag = "\"v1\"";

    private static (CachingMiddleware middleware, FakeTimeProvider clock, Func<int> callCount) BuildPipeline(
        Func<HttpRequestMessage, HttpResponseMessage> onRevalidate,
        ICacheStore? store = null,
        DateTimeOffset? startTime = null,
        long staleWhileRevalidate = 0)
    {
        FakeTimeProvider clock = startTime is DateTimeOffset start ? new(start) : new();
        int calls = 0;

        StubTransport stub = new(req =>
        {
            calls++;
            if (calls == 1)
            {
                HttpResponseMessage first = new(HttpStatusCode.OK) { Content = new StringContent("origin-body") };
                first.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(10) };
                if (staleWhileRevalidate > 0)
                {
                    first.Headers.TryAddWithoutValidation("Cache-Control", $"stale-while-revalidate={staleWhileRevalidate}");
                }

                first.Headers.ETag = new EntityTagHeaderValue(ETag);
                return first;
            }

            return onRevalidate(req);
        });

        store ??= new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions()));
        CachingMiddleware middleware = new(store, new DefaultCacheKeyBuilder(), new CacheOptions(), timeProvider: clock)
        {
            InnerHandler = stub
        };

        return (middleware, clock, () => calls);
    }

    private static HttpResponseMessage NotModified(TimeSpan? maxAge = null)
    {
        HttpResponseMessage notModified = new(HttpStatusCode.NotModified);
        notModified.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = maxAge ?? TimeSpan.FromSeconds(10) };
        notModified.Headers.ETag = new EntityTagHeaderValue(ETag);
        return notModified;
    }

    private static HttpRequestMessage Req(HttpMethod? method = null) => new(method ?? HttpMethod.Get, Url);

    [Fact]
    public async Task Revalidation304_ResetsAgeToZero()
    {
        (CachingMiddleware middleware, FakeTimeProvider clock, _) = BuildPipeline(_ => NotModified());
        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(Req(), TestContext.Current.CancellationToken);

        // Well past max-age=10 — the second request triggers conditional revalidation
        clock.Advance(TimeSpan.FromSeconds(65));

        HttpResponseMessage revalidated = await invoker.SendAsync(Req(), TestContext.Current.CancellationToken);

        revalidated.StatusCode.Should().Be(HttpStatusCode.OK);
        revalidated.Headers.Age.Should().NotBeNull();
        revalidated.Headers.Age!.Value.TotalSeconds.Should().Be(0,
            "the age calculation restarts from the validation response (RFC 9111 §4.2.3)");
    }

    [Fact]
    public async Task CacheHitsAfterRevalidation_CountAgeFromRevalidationTime()
    {
        (CachingMiddleware middleware, FakeTimeProvider clock, Func<int> callCount) = BuildPipeline(_ => NotModified());
        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(Req(), TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(65));
        _ = await invoker.SendAsync(Req(), TestContext.Current.CancellationToken);

        // Within the refreshed max-age=10 — fresh hit, no origin call
        clock.Advance(TimeSpan.FromSeconds(3));
        HttpResponseMessage hit = await invoker.SendAsync(Req(), TestContext.Current.CancellationToken);

        callCount().Should().Be(2, "the third request must be a fresh cache hit after the TTL refresh");
        hit.Headers.Age.Should().NotBeNull();
        hit.Headers.Age!.Value.TotalSeconds.Should().Be(3,
            "age must be measured from the revalidation, not the original store time (was 68 before the fix)");
    }

    [Fact]
    public async Task Revalidation304_UpdatesStoredHeaderFields()
    {
        DateTimeOffset revalidationDate = new(2024, 1, 1, 0, 1, 5, TimeSpan.Zero);
        (CachingMiddleware middleware, FakeTimeProvider clock, _) = BuildPipeline(_ =>
        {
            HttpResponseMessage notModified = NotModified();
            notModified.Headers.Date = revalidationDate;
            return notModified;
        });
        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(Req(), TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(65));
        _ = await invoker.SendAsync(Req(), TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(3));
        HttpResponseMessage hit = await invoker.SendAsync(Req(), TestContext.Current.CancellationToken);

        hit.Headers.Date.Should().Be(revalidationDate,
            "the 304's header fields must replace the stored response's fields (RFC 9111 §4.3.4)");
    }

    [Fact]
    public async Task BackgroundRevalidation304_ResetsAge()
    {
        (CachingMiddleware middleware, FakeTimeProvider clock, Func<int> callCount) =
            BuildPipeline(_ => NotModified(), staleWhileRevalidate: 300);
        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(Req(), TestContext.Current.CancellationToken);

        // Stale but within the stale-while-revalidate window — served stale, revalidated in background
        clock.Advance(TimeSpan.FromSeconds(65));
        HttpResponseMessage stale = await invoker.SendAsync(Req(), TestContext.Current.CancellationToken);
        stale.Headers.Age!.Value.TotalSeconds.Should().Be(65, "the stale response still carries the old age");

        // Wait for the fire-and-forget background revalidation to complete
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (callCount() < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        await Task.Delay(100, TestContext.Current.CancellationToken);
        callCount().Should().Be(2, "the background revalidation must have fired");

        HttpResponseMessage refreshed = await invoker.SendAsync(Req(), TestContext.Current.CancellationToken);

        callCount().Should().Be(2, "the refreshed entry must be a fresh cache hit");
        refreshed.Headers.Age!.Value.TotalSeconds.Should().Be(0,
            "a background 304 must also reset the entry's stored-at time");
    }

    [Fact]
    public async Task HeadRevalidation304_ResetsAge()
    {
        (CachingMiddleware middleware, FakeTimeProvider clock, _) = BuildPipeline(_ => NotModified());
        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(Req(), TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(65));
        HttpResponseMessage headRevalidated = await invoker.SendAsync(Req(HttpMethod.Head), TestContext.Current.CancellationToken);

        headRevalidated.StatusCode.Should().Be(HttpStatusCode.OK);
        headRevalidated.Headers.Age!.Value.TotalSeconds.Should().Be(0,
            "a HEAD-triggered 304 refresh must reset the GET entry's age");
    }

    [Fact]
    public async Task DistributedStore_Revalidation304_ResetsAge()
    {
        DistributedCacheStore store = new(new MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())));

        // MemoryDistributedCache evicts on the real system clock via the entry's absolute
        // ExpiresAt, so the fake clock must start at real "now" for entries to survive.
        (CachingMiddleware middleware, FakeTimeProvider clock, Func<int> callCount) =
            BuildPipeline(_ => NotModified(), store: store, startTime: DateTimeOffset.UtcNow);
        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(Req(), TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(65));
        HttpResponseMessage revalidated = await invoker.SendAsync(Req(), TestContext.Current.CancellationToken);
        revalidated.Headers.Age!.Value.TotalSeconds.Should().Be(0,
            "the StoredAt reset must survive the distributed store's JSON round-trip");

        clock.Advance(TimeSpan.FromSeconds(3));
        HttpResponseMessage hit = await invoker.SendAsync(Req(), TestContext.Current.CancellationToken);

        callCount().Should().Be(2);
        hit.Headers.Age!.Value.TotalSeconds.Should().Be(3);
    }

    private sealed class StubTransport(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
