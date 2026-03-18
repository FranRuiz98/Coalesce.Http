using BenchmarkDotNet.Attributes;
using Coalesce.Http.Caching;
using Coalesce.Http.Coalescing;
using Coalesce.Http.Handlers;
using Coalesce.Http.Options;
using Microsoft.Extensions.Caching.Memory;
using System.Net;

/// <summary>
/// End-to-end pipeline benchmark comparing:
///   • Plain HttpClient (no caching, no coalescing)
///   • Coalesce.Http full pipeline (CachingMiddleware → CoalescingHandler → backend)
///
/// Demonstrates the combined benefit of caching + coalescing for repeated requests.
/// </summary>
[MemoryDiagnoser]
[MediumRunJob]
public class EndToEndPipelineBenchmarks
{
    private HttpClient _plainClient = null!;
    private HttpClient _coalesceClient = null!;

    private const int RequestsPerIteration = 50;
    private static readonly TimeSpan BackendLatency = TimeSpan.FromMilliseconds(10);

    [GlobalSetup]
    public async Task Setup()
    {
        // ── Plain client: every request hits the (simulated) backend ──
        DelayHandler plainHandler = new(BackendLatency);
        _plainClient = new HttpClient(plainHandler)
        {
            BaseAddress = new Uri("https://api.example.com")
        };

        // ── Coalesce.Http pipeline: CachingMiddleware → CoalescingHandler → backend ──
        IMemoryCache cache = new MemoryCache(new MemoryCacheOptions());
        DefaultCacheKeyBuilder keyBuilder = new();
        CacheOptions cacheOptions = new() { DefaultTtl = TimeSpan.FromMinutes(5) };
        CoalescerOptions coalescerOptions = new();
        RequestCoalescer coalescer = new(coalescerOptions);

        CoalescingHandler coalescingHandler = new(coalescer, coalescerOptions)
        {
            InnerHandler = new DelayHandler(BackendLatency)
        };
        CachingMiddleware cachingMiddleware = new(cache, keyBuilder, cacheOptions)
        {
            InnerHandler = coalescingHandler
        };

        _coalesceClient = new HttpClient(cachingMiddleware)
        {
            BaseAddress = new Uri("https://api.example.com")
        };

        // Pre-warm the cache with one request so subsequent hits are served from cache
        using HttpResponseMessage _ = await _coalesceClient.GetAsync("/products/1");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _plainClient.Dispose();
        _coalesceClient.Dispose();
    }

    [Benchmark(Baseline = true, Description = "Plain HttpClient (no cache, no coalescing)")]
    public async Task PlainHttpClient()
    {
        // All requests hit the backend
        for (int i = 0; i < RequestsPerIteration; i++)
        {
            using HttpResponseMessage response = await _plainClient.GetAsync("/products/1");
        }
    }

    [Benchmark(Description = "Coalesce.Http pipeline (cache hits after first request)")]
    public async Task CoalesceHttpPipeline()
    {
        // First request already cached in Setup; all 50 requests are cache hits
        for (int i = 0; i < RequestsPerIteration; i++)
        {
            using HttpResponseMessage response = await _coalesceClient.GetAsync("/products/1");
        }
    }

    [Benchmark(Description = "Coalesce.Http concurrent burst (coalescing + cache)")]
    public async Task CoalesceHttpConcurrentBurst()
    {
        // Fire N requests concurrently to the same endpoint
        Task<HttpResponseMessage>[] tasks = new Task<HttpResponseMessage>[RequestsPerIteration];
        for (int i = 0; i < RequestsPerIteration; i++)
        {
            tasks[i] = _coalesceClient.GetAsync("/products/1");
        }

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);
        foreach (HttpResponseMessage r in responses)
        {
            r.Dispose();
        }
    }

    /// <summary>
    /// Simulates a backend handler with configurable latency and a realistic JSON payload.
    /// </summary>
    private sealed class DelayHandler(TimeSpan delay) : HttpMessageHandler
    {
        private static readonly byte[] Payload =
            """{"id":1,"name":"Widget","price":9.99,"category":"electronics","inStock":true}"""u8.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Payload)
            };
        }
    }
}
