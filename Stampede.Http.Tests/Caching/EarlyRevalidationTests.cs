using Stampede.Http.Caching;
using Stampede.Http.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using System.Net;

namespace Stampede.Http.Tests.Caching;

/// <summary>
/// Verifies <see cref="CacheOptions.EnableEarlyRevalidation"/> (XFetch — Vattani, Padmanabhan &amp; Gionis,
/// 2015): a fresh cache hit probabilistically triggers a background refresh ahead of expiry, scaled by
/// <see cref="CacheEntry.OriginFetchDurationMs"/> and <see cref="CacheOptions.EarlyRevalidationBeta"/>.
/// </summary>
public sealed class EarlyRevalidationTests
{
    private readonly ICacheStore _cache;
    private readonly DefaultCacheKeyBuilder _keyBuilder;
    private readonly FakeTimeProvider _time;

    public EarlyRevalidationTests()
    {
        _cache = new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions()));
        _keyBuilder = new DefaultCacheKeyBuilder();
        _time = new FakeTimeProvider();
    }

    private (CachingMiddleware middleware, StubTransport stub) BuildPipeline(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        CacheOptions options,
        Func<double>? randomSource = null)
    {
        StubTransport stub = new(handler);
        CachingMiddleware middleware = new(_cache, _keyBuilder, options, timeProvider: _time, randomSource: randomSource) { InnerHandler = stub };
        return (middleware, stub);
    }

    /// <summary>Always returns a value close to 1 — drives <c>r = 1 - random()</c> close to 0, making
    /// <c>-ln(r)</c> (and therefore the lead time) as large as possible: guarantees a trigger.</summary>
    private static double AlwaysTrigger() => 0.999999;

    /// <summary>Always returns a value close to 0 — drives <c>r</c> close to 1, making <c>-ln(r)</c> (and
    /// the lead time) approximately zero: guarantees no trigger this side of expiry itself.</summary>
    private static double NeverTrigger() => 0.0000001;

    // ── Disabled by default ───────────────────────────────────────────────────

    [Fact]
    public void EnableEarlyRevalidation_DefaultsToFalse()
    {
        new CacheOptions().EnableEarlyRevalidation.Should().BeFalse();
    }

    [Fact]
    public async Task Disabled_NeverTriggersEvenWithGuaranteedTriggerRandom()
    {
        int callCount = 0;
        CacheOptions options = new() { DefaultTtl = TimeSpan.FromSeconds(2), EnableEarlyRevalidation = false };

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            _time.Advance(TimeSpan.FromMilliseconds(100)); // origin "takes" 100ms
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") };
        }, options, AlwaysTrigger);

        HttpMessageInvoker invoker = new(middleware);
        Uri uri = new("https://api.test/xfetch-disabled");

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(1)); // 1s remaining of the 2s TTL — would trigger if enabled

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), CancellationToken.None);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        callCount.Should().Be(1, "EnableEarlyRevalidation is false, so no background refresh must ever be triggered");
    }

    // ── Unmeasured duration never triggers ────────────────────────────────────

    [Fact]
    public async Task NoMeasuredFetchDuration_NeverTriggers()
    {
        int callCount = 0;
        CacheOptions options = new() { DefaultTtl = TimeSpan.FromSeconds(2), EnableEarlyRevalidation = true };

        // The handler never advances the clock, so OriginFetchDurationMs ends up exactly 0.
        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") };
        }, options, AlwaysTrigger);

        HttpMessageInvoker invoker = new(middleware);
        Uri uri = new("https://api.test/xfetch-unmeasured");

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(1));

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), CancellationToken.None);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        callCount.Should().Be(1, "an entry with no measured origin fetch duration must never trigger early revalidation");
    }

    // ── OriginFetchDurationMs is recorded ─────────────────────────────────────

    [Fact]
    public async Task StoredEntry_RecordsMeasuredOriginFetchDuration()
    {
        CacheOptions options = new() { DefaultTtl = TimeSpan.FromMinutes(5) };
        Uri uri = new("https://api.test/xfetch-duration");

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            _time.Advance(TimeSpan.FromMilliseconds(250));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") };
        }, options);

        HttpMessageInvoker invoker = new(middleware);
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), CancellationToken.None);

        string key = _keyBuilder.Build(new HttpRequestMessage(HttpMethod.Get, uri));
        _cache.TryGetValue(key, out CacheEntry? entry);

        entry.Should().NotBeNull();
        entry!.OriginFetchDurationMs.Should().Be(250);
    }

    // ── Guaranteed trigger near expiry ─────────────────────────────────────────

    [Fact]
    public async Task EnabledWithMeasuredDuration_GuaranteedTriggerRandom_NearExpiry_TriggersBackgroundRefresh()
    {
        int callCount = 0;
        TaskCompletionSource<bool> backgroundGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CacheOptions options = new() { DefaultTtl = TimeSpan.FromSeconds(2), EnableEarlyRevalidation = true };
        Uri uri = new("https://api.test/xfetch-trigger");

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                _time.Advance(TimeSpan.FromMilliseconds(100)); // origin "takes" 100ms
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("original") };
            }

            backgroundGate.SetResult(true);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("refreshed") };
        }, options, AlwaysTrigger);

        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), CancellationToken.None);

        // Still fresh (1s of the 2s TTL remains) but close enough that the rigged random guarantees a trigger.
        _time.Advance(TimeSpan.FromSeconds(1));

        HttpResponseMessage response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), CancellationToken.None);

        // The triggering request itself must still get the current (pre-refresh) cached body immediately.
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("original",
            "early revalidation is a background side effect and must not change what the triggering hit receives");

        await backgroundGate.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken); // let StoreAsync finish after the gate opened

        callCount.Should().Be(2, "the guaranteed-trigger random draw must have started a background refresh");

        string key = _keyBuilder.Build(new HttpRequestMessage(HttpMethod.Get, uri));
        _cache.TryGetValue(key, out CacheEntry? refreshedEntry);
        refreshedEntry.Should().NotBeNull();
        System.Text.Encoding.UTF8.GetString(refreshedEntry!.Body).Should().Be("refreshed",
            "the background refresh must have updated the cache entry");
    }

    // ── Guaranteed non-trigger far from expiry ─────────────────────────────────

    [Fact]
    public async Task EnabledWithMeasuredDuration_NeverTriggerRandom_NeverTriggers()
    {
        int callCount = 0;
        CacheOptions options = new() { DefaultTtl = TimeSpan.FromSeconds(2), EnableEarlyRevalidation = true };
        Uri uri = new("https://api.test/xfetch-no-trigger");

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            _time.Advance(TimeSpan.FromMilliseconds(100));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") };
        }, options, NeverTrigger);

        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), CancellationToken.None);
        _time.Advance(TimeSpan.FromMilliseconds(1900)); // 100ms remaining — right up against expiry

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), CancellationToken.None);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        callCount.Should().Be(1, "a near-zero lead time must not push a fresh entry into triggering early revalidation");
    }

    // ── EarlyRevalidationBeta scales the lead time ────────────────────────────

    [Fact]
    public async Task HigherBeta_TriggersWhereLowerBetaDoesNot()
    {
        // A fixed, moderate random draw: enough lead time to cross the threshold with a high beta,
        // not enough with a low one, for the same entry and the same point in its freshness window.
        const double fixedRandom = 0.5;
        Uri lowBetaUri = new("https://api.test/xfetch-low-beta");
        Uri highBetaUri = new("https://api.test/xfetch-high-beta");

        int lowBetaCalls = 0;
        CacheOptions lowBetaOptions = new() { DefaultTtl = TimeSpan.FromSeconds(2), EnableEarlyRevalidation = true, EarlyRevalidationBeta = 0.01 };
        (CachingMiddleware lowBetaMiddleware, _) = BuildPipeline(_ =>
        {
            lowBetaCalls++;
            _time.Advance(TimeSpan.FromMilliseconds(100));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") };
        }, lowBetaOptions, () => fixedRandom);

        HttpMessageInvoker lowBetaInvoker = new(lowBetaMiddleware);
        _ = await lowBetaInvoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, lowBetaUri), CancellationToken.None);
        _time.Advance(TimeSpan.FromMilliseconds(1900));
        _ = await lowBetaInvoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, lowBetaUri), CancellationToken.None);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        lowBetaCalls.Should().Be(1, "a small beta keeps the lead time too short to trigger this close to expiry");

        // Fresh clock and cache for the high-beta case.
        _time.Advance(TimeSpan.FromSeconds(10));
        int highBetaCalls = 0;
        CacheOptions highBetaOptions = new() { DefaultTtl = TimeSpan.FromSeconds(2), EnableEarlyRevalidation = true, EarlyRevalidationBeta = 100 };
        (CachingMiddleware highBetaMiddleware, _) = BuildPipeline(_ =>
        {
            highBetaCalls++;
            _time.Advance(TimeSpan.FromMilliseconds(100));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") };
        }, highBetaOptions, () => fixedRandom);

        HttpMessageInvoker highBetaInvoker = new(highBetaMiddleware);
        _ = await highBetaInvoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, highBetaUri), CancellationToken.None);
        _time.Advance(TimeSpan.FromMilliseconds(1900));
        _ = await highBetaInvoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, highBetaUri), CancellationToken.None);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        highBetaCalls.Should().Be(2, "a large beta stretches the same random draw's lead time far enough to trigger");
    }

    // ── Dedup: concurrent fresh hits trigger at most one background refresh ──

    [Fact]
    public async Task ConcurrentFreshHits_TriggerAtMostOneBackgroundRefresh()
    {
        int callCount = 0;
        TaskCompletionSource<bool> backgroundStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseBackground = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CacheOptions options = new() { DefaultTtl = TimeSpan.FromSeconds(2), EnableEarlyRevalidation = true };
        Uri uri = new("https://api.test/xfetch-dedup");

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            int thisCall = Interlocked.Increment(ref callCount);
            if (thisCall == 1)
            {
                _time.Advance(TimeSpan.FromMilliseconds(100));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("original") };
            }

            backgroundStarted.TrySetResult(true);
            releaseBackground.Task.GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("refreshed") };
        }, options, AlwaysTrigger);

        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(1));

        // Several concurrent fresh hits, all past the guaranteed-trigger threshold.
        Task<HttpResponseMessage> t1 = invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), CancellationToken.None);
        Task<HttpResponseMessage> t2 = invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), CancellationToken.None);
        Task<HttpResponseMessage> t3 = invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), CancellationToken.None);

        await Task.WhenAll(t1, t2, t3);
        await backgroundStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        releaseBackground.SetResult(true);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        callCount.Should().Be(2, "BackgroundRevalidationCoordinator must dedup so only one refresh runs per key regardless of how many fresh hits trigger it");
    }

    private sealed class StubTransport(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
