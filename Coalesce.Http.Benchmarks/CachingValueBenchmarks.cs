using BenchmarkDotNet.Attributes;
using Coalesce.Http.Caching;
using Microsoft.Extensions.Caching.Memory;
using System.Net;

/// <summary>
/// Isolates the caching layer to show the throughput difference between
/// cache hits (sub-microsecond) and origin round-trips.
/// </summary>
[MemoryDiagnoser]
[MediumRunJob]
public class CachingValueBenchmarks
{
    private HttpClient _cachedClient = null!;
    private HttpClient _uncachedClient = null!;

    private static readonly TimeSpan BackendLatency = TimeSpan.FromMilliseconds(10);

    [GlobalSetup]
    public async Task Setup()
    {
        // ── Uncached client: always hits the backend ──
        _uncachedClient = new HttpClient(new DelayHandler(BackendLatency))
        {
            BaseAddress = new Uri("https://api.example.com")
        };

        // ── Cached client: CachingMiddleware in front of the backend ──
        IMemoryCache cache = new MemoryCache(new MemoryCacheOptions());
        DefaultCacheKeyBuilder keyBuilder = new();
        CacheOptions options = new() { DefaultTtl = TimeSpan.FromMinutes(5) };

        CachingMiddleware middleware = new(cache, keyBuilder, options)
        {
            InnerHandler = new DelayHandler(BackendLatency)
        };
        _cachedClient = new HttpClient(middleware)
        {
            BaseAddress = new Uri("https://api.example.com")
        };

        // Pre-warm cache
        using HttpResponseMessage _ = await _cachedClient.GetAsync("/products/1");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _cachedClient.Dispose();
        _uncachedClient.Dispose();
    }

    [Benchmark(Baseline = true, Description = "No cache (origin round-trip every time)")]
    public async Task<HttpResponseMessage> NoCache_OriginRoundTrip()
    {
        HttpResponseMessage response = await _uncachedClient.GetAsync("/products/1");
        response.Dispose();
        return response;
    }

    [Benchmark(Description = "Cache hit (served from memory)")]
    public async Task<HttpResponseMessage> CacheHit_ServedFromMemory()
    {
        HttpResponseMessage response = await _cachedClient.GetAsync("/products/1");
        response.Dispose();
        return response;
    }

    private sealed class DelayHandler(TimeSpan delay) : HttpMessageHandler
    {
        private static readonly byte[] Payload =
            """{"id":1,"name":"Widget","price":9.99,"category":"electronics"}"""u8.ToArray();

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
