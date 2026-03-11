using BenchmarkDotNet.Attributes;
using Coalesce.Http.Coalesce.Http.Caching;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.VSDiagnostics;
using System.Net;

[ShortRunJob]
[CPUUsageDiagnoser]
public class CachingMiddlewareBenchmarks
{
    private TestableHttpMessageHandler _innerHandler = null!;
    private HttpClient _client = null!;
    private CacheOptions _options = null!;
    private IMemoryCache _cache = null!;
    private DefaultCacheKeyBuilder _keyBuilder = null!;
    private readonly HttpRequestMessage _cachedRequest = null!;
    private readonly HttpRequestMessage _missRequest = null!;
    [GlobalSetup]
    public async Task Setup()
    {
        _options = new CacheOptions
        {
            DefaultTtl = TimeSpan.FromMinutes(5)
        };
        IMemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());
        _cache = memoryCache;
        _keyBuilder = new DefaultCacheKeyBuilder();
        _innerHandler = new TestableHttpMessageHandler(() =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":1,\"name\":\"Test\",\"value\":42}")
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return Task.FromResult(response);
        });
        CachingMiddleware middleware = new(_cache, _keyBuilder, _options)
        {
            InnerHandler = _innerHandler
        };
        _client = new HttpClient(middleware)
        {
            BaseAddress = new Uri("https://api.example.com")
        };
        // Pre-warm the cache with a hit entry
        _ = await _client.GetAsync("/cached-resource");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _cachedRequest?.Dispose();
        _missRequest?.Dispose();
    }

    [Benchmark(Description = "Cache Hit - GET request served from cache")]
    public async Task<HttpResponseMessage> CacheHit()
    {
        HttpResponseMessage response = await _client.GetAsync("/cached-resource");
        response.Dispose();
        return response;
    }

    [Benchmark(Description = "Cache Miss - GET request forwarded and stored")]
    public async Task<HttpResponseMessage> CacheMiss()
    {
        string uniqueUrl = $"/miss-resource-{Guid.NewGuid():N}";
        HttpResponseMessage response = await _client.GetAsync(uniqueUrl);
        response.Dispose();
        return response;
    }

    [Benchmark(Description = "Non-cacheable request (POST) - bypasses cache logic")]
    public async Task<HttpResponseMessage> NonCacheableRequest()
    {
        HttpResponseMessage response = await _client.PostAsync("/data", new StringContent("{}"));
        response.Dispose();
        return response;
    }
}

internal sealed class TestableHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<Task<HttpResponseMessage>> _handler;
    public TestableHttpMessageHandler(Func<Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        return _handler();
    }
}