using BenchmarkDotNet.Attributes;
using Coalesce.Http.Caching;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.VSDiagnostics;

[MemoryDiagnoser]
[MediumRunJob]
[CPUUsageDiagnoser]
public class ConditionalGetBenchmarks
{
    private HttpClient _client = null!;
    private const string _resourceUrl = "https://api.example.com/conditional-resource";
    private const string _eTag = "\"abc123\"";
    private static readonly DateTimeOffset _lastModified = new(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
    [GlobalSetup]
    public async Task Setup()
    {
        CacheOptions options = new()
        {
            DefaultTtl = TimeSpan.FromMinutes(10)
        };
        ICacheStore cacheStore = new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions()));
        DefaultCacheKeyBuilder keyBuilder = new();
        TestableHttpMessageHandler inner = new TestableHttpMessageHandler(() =>
        {
            HttpResponseMessage r = new(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":1,\"name\":\"Test\"}")
            };
            r.Headers.ETag = new EntityTagHeaderValue(_eTag);
            r.Content.Headers.LastModified = _lastModified;
            r.Headers.CacheControl = new CacheControlHeaderValue
            {
                MaxAge = TimeSpan.FromMinutes(10)
            };
            return Task.FromResult(r);
        });
        CachingMiddleware middleware = new(cacheStore, keyBuilder, options)
        {
            InnerHandler = inner
        };
        _client = new HttpClient(middleware);
        // Warm up: populate the cache with ETag + Last-Modified stored
        using HttpResponseMessage warmup = await _client.GetAsync(_resourceUrl);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _client.Dispose();
    }

    /// <summary>
    /// ETag matches cached entry → TryCreateNotModified builds and returns 304.
    /// Exercises the _notModifiedHeaders static array iteration and Age calculation.
    /// </summary>
    [Benchmark(Description = "Conditional GET - If-None-Match hit (304 from cache)")]
    public async Task<HttpResponseMessage> IfNoneMatch_Hit_Returns304()
    {
        using HttpRequestMessage req = new(HttpMethod.Get, _resourceUrl);
        req.Headers.TryAddWithoutValidation("If-None-Match", _eTag);
        HttpResponseMessage response = await _client.SendAsync(req);
        response.Dispose();
        return response;
    }

    /// <summary>
    /// ETag does not match → TryCreateNotModified returns null → full 200 served from cache.
    /// Baseline: same fresh-hit path without the 304 shortcut.
    /// </summary>
    [Benchmark(Description = "Conditional GET - If-None-Match miss (200 from cache)")]
    public async Task<HttpResponseMessage> IfNoneMatch_Miss_Returns200()
    {
        using HttpRequestMessage req = new(HttpMethod.Get, _resourceUrl);
        req.Headers.TryAddWithoutValidation("If-None-Match", "\"differentETag\"");
        HttpResponseMessage response = await _client.SendAsync(req);
        response.Dispose();
        return response;
    }

    /// <summary>
    /// If-Modified-Since at the stored Last-Modified timestamp → 304 via TryCreateNotModified.
    /// Exercises the Last-Modified branch of the conditional GET logic.
    /// </summary>
    [Benchmark(Description = "Conditional GET - If-Modified-Since not modified (304 from cache)")]
    public async Task<HttpResponseMessage> IfModifiedSince_NotModified_Returns304()
    {
        using HttpRequestMessage req = new(HttpMethod.Get, _resourceUrl);
        req.Headers.IfModifiedSince = _lastModified;
        HttpResponseMessage response = await _client.SendAsync(req);
        response.Dispose();
        return response;
    }

    /// <summary>
    /// Plain cache hit without conditional headers — reference point for the overhead
    /// introduced by TryCreateNotModified when validators ARE present.
    /// </summary>
    [Benchmark(Description = "Plain cache hit - no conditional headers (baseline)", Baseline = true)]
    public async Task<HttpResponseMessage> PlainCacheHit_NoConditional()
    {
        using HttpRequestMessage req = new(HttpMethod.Get, _resourceUrl);
        HttpResponseMessage response = await _client.SendAsync(req);
        response.Dispose();
        return response;
    }
}