using Coalesce.Http.Coalesce.Http.Caching;
using Coalesce.Http.Coalesce.Http.Coalescing;
using Coalesce.Http.Coalesce.Http.Metrics;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;

namespace Coalesce.Http.Tests.Metrics;

/// <summary>
/// Verifies that <see cref="CoalesceHttpMetrics"/> emits the correct instrument readings
/// for cache hits, misses, revalidations, stale-if-error, and coalescing deduplication.
/// </summary>
public sealed class CoalesceHttpMetricsTests : IDisposable
{
    private readonly CoalesceHttpMetrics _metrics;
    private readonly MeterListener _listener;

    // Counters tracked by the MeterListener
    private long _cacheHits;
    private long _cacheMisses;
    private long _cacheRevalidations;
    private long _staleErrorsServed;
    private long _coalescedDeduplicated;
    private long _coalescedInflight;

    public CoalesceHttpMetricsTests()
    {
        _metrics = new CoalesceHttpMetrics();

        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == CoalesceHttpMetrics.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            switch (instrument.Name)
            {
                case "coalesce_http.cache.hits":            _cacheHits += measurement; break;
                case "coalesce_http.cache.misses":          _cacheMisses += measurement; break;
                case "coalesce_http.cache.revalidations":   _cacheRevalidations += measurement; break;
                case "coalesce_http.cache.stale_errors_served": _staleErrorsServed += measurement; break;
                case "coalesce_http.coalescing.deduplicated":   _coalescedDeduplicated += measurement; break;
                case "coalesce_http.coalescing.inflight":       _coalescedInflight += measurement; break;
            }
        });

        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _metrics.Dispose();
    }

    // ── Instrument creation ───────────────────────────────────────────────────

    [Fact]
    public void Constructor_MeterName_IsCorrect()
    {
        CoalesceHttpMetrics.MeterName.Should().Be("Coalesce.Http");
    }

    // ── Cache hit ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task CacheHit_RecordsCacheHit_AndNoCacheMiss()
    {
        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") });

        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(Req("https://api.test/hit"), CancellationToken.None);
        _ = await invoker.SendAsync(Req("https://api.test/hit"), CancellationToken.None);

        _listener.RecordObservableInstruments();

        _cacheMisses.Should().Be(1, "only the first request misses");
        _cacheHits.Should().Be(1, "the second request is served from cache");
    }

    // ── Cache miss ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CacheMiss_RecordsCacheMiss()
    {
        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") });

        HttpMessageInvoker invoker = new(middleware);
        _ = await invoker.SendAsync(Req("https://api.test/miss"), CancellationToken.None);

        _listener.RecordObservableInstruments();

        _cacheMisses.Should().Be(1);
        _cacheHits.Should().Be(0);
    }

    // ── Revalidation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Revalidation_RecordsCacheRevalidation()
    {
        IMemoryCache cache = new MemoryCache(new MemoryCacheOptions());
        DefaultCacheKeyBuilder keyBuilder = new();
        CacheOptions options = new() { DefaultTtl = TimeSpan.FromMinutes(5) };

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            if (req.Headers.IfNoneMatch.Any())
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
            r.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return r;
        }, cache, keyBuilder, options);

        HttpMessageInvoker invoker = new(middleware);

        // Populate cache
        _ = await invoker.SendAsync(Req("https://api.test/reval"), CancellationToken.None);

        // Force stale
        string key = keyBuilder.Build(Req("https://api.test/reval"));
        if (cache.TryGetValue(key, out CacheEntry? entry) && entry is not null)
        {
            cache.Set(key, entry with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) });
        }

        _ = await invoker.SendAsync(Req("https://api.test/reval"), CancellationToken.None);

        _listener.RecordObservableInstruments();

        _cacheRevalidations.Should().Be(1);
    }

    // ── Stale-if-error ────────────────────────────────────────────────────────

    [Fact]
    public async Task StaleIfError_RecordsStaleErrorServed()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
                r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
                r.Headers.TryAddWithoutValidation("Cache-Control", "stale-if-error=3600");
                return r;
            }
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(Req("https://api.test/sie-metric"), CancellationToken.None);
        _ = await invoker.SendAsync(Req("https://api.test/sie-metric"), CancellationToken.None);

        _listener.RecordObservableInstruments();

        _staleErrorsServed.Should().Be(1);
    }

    // ── Coalescing deduplication ──────────────────────────────────────────────

    [Fact]
    public async Task Coalescing_RecordsDeduplicatedRequests()
    {
        RequestCoalescer coalescer = new(_metrics);
        RequestKey key = new("GET", "https://api.test/coalesced");

        TaskCompletionSource<HttpResponseMessage> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<HttpResponseMessage> t1 = coalescer.ExecuteAsync(key, () => tcs.Task);
        Task<HttpResponseMessage> t2 = coalescer.ExecuteAsync(key, () => tcs.Task);
        Task<HttpResponseMessage> t3 = coalescer.ExecuteAsync(key, () => tcs.Task);

        tcs.SetResult(new HttpResponseMessage(HttpStatusCode.OK));

        await Task.WhenAll(t1, t2, t3);

        _listener.RecordObservableInstruments();

        _coalescedDeduplicated.Should().Be(2, "two callers reused the winner's in-flight request");
    }

    // ── Inflight counter ─────────────────────────────────────────────────────

    [Fact]
    public async Task Coalescing_InflightReturnsToZeroAfterCompletion()
    {
        RequestCoalescer coalescer = new(_metrics);
        RequestKey key = new("GET", "https://api.test/inflight");

        TaskCompletionSource<HttpResponseMessage> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<HttpResponseMessage> t1 = coalescer.ExecuteAsync(key, () => tcs.Task);

        tcs.SetResult(new HttpResponseMessage(HttpStatusCode.OK));
        _ = await t1;

        _listener.RecordObservableInstruments();

        _coalescedInflight.Should().Be(0, "inflight counter must return to zero after the request completes");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HttpRequestMessage Req(string url) =>
        new(HttpMethod.Get, url);

    private (CachingMiddleware, StubTransport) BuildPipeline(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        IMemoryCache? cache = null,
        DefaultCacheKeyBuilder? keyBuilder = null,
        CacheOptions? options = null)
    {
        cache ??= new MemoryCache(new MemoryCacheOptions());
        keyBuilder ??= new DefaultCacheKeyBuilder();
        options ??= new CacheOptions { DefaultTtl = TimeSpan.FromMinutes(5) };

        StubTransport stub = new(handler);
        CachingMiddleware middleware = new(cache, keyBuilder, options, _metrics) { InnerHandler = stub };
        return (middleware, stub);
    }

    private sealed class StubTransport(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
