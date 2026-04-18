using BenchmarkDotNet.Attributes;
using Coalesce.Http.Coalescing;
using Microsoft.VSDiagnostics;

[MediumRunJob]
[CPUUsageDiagnoser]
public class RequestKeyHeadersBenchmarks
{
    // Simulates the common Vary-header coalescing scenario:
    // 1 header, 3 headers (typical), 6 headers (stress)
    private HttpRequestMessage _req1 = null!;
    private HttpRequestMessage _req3 = null!;
    private HttpRequestMessage _req6 = null!;
    private static readonly string[] _headers1 = ["Accept-Language"];
    private static readonly string[] _headers3 = ["Accept-Language", "Accept-Encoding", "Authorization"];
    private static readonly string[] _headers6 = ["Accept-Language", "Accept-Encoding", "Authorization", "X-Tenant-Id", "X-Feature-Flag", "X-Api-Version"];
    [GlobalSetup]
    public void Setup()
    {
        _req1 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        _req1.Headers.TryAddWithoutValidation("Accept-Language", "en-US");
        _req3 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        _req3.Headers.TryAddWithoutValidation("Accept-Language", "en-US");
        _req3.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
        _req3.Headers.TryAddWithoutValidation("Authorization", "Bearer token123");
        _req6 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        _req6.Headers.TryAddWithoutValidation("Accept-Language", "en-US");
        _req6.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
        _req6.Headers.TryAddWithoutValidation("Authorization", "Bearer token123");
        _req6.Headers.TryAddWithoutValidation("X-Tenant-Id", "tenant-abc");
        _req6.Headers.TryAddWithoutValidation("X-Feature-Flag", "feature-xyz");
        _req6.Headers.TryAddWithoutValidation("X-Api-Version", "2024-01-01");
    }

    [Benchmark(Baseline = true, Description = "BuildHeadersKey — 1 header")]
    public string OneHeader() => RequestKey.Create(_req1, _headers1).HeadersKey;
    [Benchmark(Description = "BuildHeadersKey — 3 headers (typical)")]
    public string ThreeHeaders() => RequestKey.Create(_req3, _headers3).HeadersKey;
    [Benchmark(Description = "BuildHeadersKey — 6 headers (stress)")]
    public string SixHeaders() => RequestKey.Create(_req6, _headers6).HeadersKey;
}