using Coalesce.Http.Caching;
using Coalesce.Http.Coalescing;
using Coalesce.Http.Metrics;
using Coalesce.Http.Options;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;

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
    private long _coalescedTimeouts;

    // HEAD-tagged counters (measurements emitted with http.request.method = HEAD)
    private long _headCacheHits;
    private long _headCacheRevalidations;

    private static readonly FieldInfo MeterField =
        typeof(CoalesceHttpMetrics).GetField("_meter", BindingFlags.NonPublic | BindingFlags.Instance)!;

    public CoalesceHttpMetricsTests()
    {
        _metrics = new CoalesceHttpMetrics();

        // Resolve the exact Meter instance so the listener only tracks events from THIS test's metrics.
        Meter ownMeter = (Meter)MeterField.GetValue(_metrics)!;

        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, ownMeter))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            bool isHead = false;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "http.request.method" && tag.Value is string m && m == "HEAD")
                {
                    isHead = true;
                    break;
                }
            }

            switch (instrument.Name)
            {
                case "coalesce_http.cache.hits":
                    _cacheHits += measurement;
                    if (isHead) _headCacheHits += measurement;
                    break;
                case "coalesce_http.cache.misses":          _cacheMisses += measurement; break;
                case "coalesce_http.cache.revalidations":
                    _cacheRevalidations += measurement;
                    if (isHead) _headCacheRevalidations += measurement;
                    break;
                case "coalesce_http.cache.stale_errors_served": _staleErrorsServed += measurement; break;
                case "coalesce_http.coalescing.deduplicated":   _coalescedDeduplicated += measurement; break;
                case "coalesce_http.coalescing.inflight":       _coalescedInflight += measurement; break;
                case "coalesce_http.coalescing.timeouts":       _coalescedTimeouts += measurement; break;
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
        ICacheStore cache = new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions()));
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
        RequestCoalescer coalescer = new(new CoalescerOptions(), _metrics);
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
        RequestCoalescer coalescer = new(new CoalescerOptions(), _metrics);
        RequestKey key = new("GET", "https://api.test/inflight");

        TaskCompletionSource<HttpResponseMessage> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<HttpResponseMessage> t1 = coalescer.ExecuteAsync(key, () => tcs.Task);

        tcs.SetResult(new HttpResponseMessage(HttpStatusCode.OK));
        _ = await t1;

        _listener.RecordObservableInstruments();

        _coalescedInflight.Should().Be(0, "inflight counter must return to zero after the request completes");
    }

    // ── Coalescing timeout ────────────────────────────────────────────────────

    [Fact]
    public async Task CoalescingTimeout_RecordsTimeoutCounter()
    {
        var options = new CoalescerOptions { CoalescingTimeout = TimeSpan.FromMilliseconds(50) };
        RequestCoalescer coalescer = new(options, _metrics);
        RequestKey key = new("GET", "https://api.test/timeout-metric");

        // The winner will never complete, causing the waiter to time out
        TaskCompletionSource<HttpResponseMessage> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Start the winner
        Task<HttpResponseMessage> winner = coalescer.ExecuteAsync(key, () => gate.Task);

        // Give the winner time to register as inflight
        await Task.Delay(10);

        // Start a waiter — it will time out and fall back to independent execution
        Task<HttpResponseMessage> waiter = coalescer.ExecuteAsync(key, () =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([])
            }));

        using HttpResponseMessage waiterResponse = await waiter;

        // Complete the winner so we don't leak
        gate.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([])
        });
        using HttpResponseMessage winnerResponse = await winner;

        _listener.RecordObservableInstruments();

        _coalescedTimeouts.Should().Be(1, "the waiter timed out and fell back to independent execution");
    }

    // ── HEAD-aware metrics ────────────────────────────────────────────────────

    [Fact]
    public async Task HeadCacheHit_RecordsHitMetricWithHeadTag()
    {
        ICacheStore cache = new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions()));
        DefaultCacheKeyBuilder keyBuilder = new();
        CacheOptions options = new() { DefaultTtl = TimeSpan.FromMinutes(5) };

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
            return r;
        }, cache, keyBuilder, options);

        HttpMessageInvoker invoker = new(middleware);

        // Warm the GET cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/head-hit"), CancellationToken.None);

        // HEAD request — should hit the GET cache entry
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Head, "https://api.test/head-hit"), CancellationToken.None);

        _listener.RecordObservableInstruments();

        _headCacheHits.Should().BeGreaterThan(0, "HEAD cache hit must emit the hit metric with http.request.method=HEAD tag");
    }

    [Fact]
    public async Task HeadRevalidation_RecordsRevalidationMetricWithHeadTag()
    {
        ICacheStore cache = new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions()));
        DefaultCacheKeyBuilder keyBuilder = new();
        CacheOptions options = new() { DefaultTtl = TimeSpan.FromMinutes(5) };

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            if (req.Method == HttpMethod.Head && req.Headers.IfNoneMatch.Any())
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
            r.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return r;
        }, cache, keyBuilder, options);

        HttpMessageInvoker invoker = new(middleware);

        // Warm the GET cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/head-reval"), CancellationToken.None);

        // Force stale
        string key = keyBuilder.Build(new HttpRequestMessage(HttpMethod.Get, "https://api.test/head-reval"));
        if (cache.TryGetValue(key, out CacheEntry? entry) && entry is not null)
        {
            cache.Set(key, entry with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) });
        }

        // HEAD request with stale entry — should trigger HEAD revalidation
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Head, "https://api.test/head-reval"), CancellationToken.None);

        _listener.RecordObservableInstruments();

        _headCacheRevalidations.Should().BeGreaterThan(0, "HEAD revalidation must emit the revalidation metric with http.request.method=HEAD tag");
    }

    [Fact]
    public async Task GetCacheHit_RecordsHitWithoutMethodTag()
    {
        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") });

        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(Req("https://api.test/get-hit-tag"), CancellationToken.None);
        _ = await invoker.SendAsync(Req("https://api.test/get-hit-tag"), CancellationToken.None);

        _listener.RecordObservableInstruments();

        _cacheHits.Should().Be(1, "GET cache hit must be recorded");
        _headCacheHits.Should().Be(0, "GET cache hit must not carry the HEAD tag");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HttpRequestMessage Req(string url) =>
        new(HttpMethod.Get, url);

    private (CachingMiddleware, StubTransport) BuildPipeline(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        ICacheStore? cache = null,
        DefaultCacheKeyBuilder? keyBuilder = null,
        CacheOptions? options = null)
    {
        cache ??= new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions()));
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
