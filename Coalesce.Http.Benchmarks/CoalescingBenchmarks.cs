using BenchmarkDotNet.Attributes;
using Coalesce.Http.Coalescing;
using Coalesce.Http.Options;
using System.Net;

[MemoryDiagnoser]
[MediumRunJob]
public class CoalescingBenchmarks
{
    private RequestCoalescer _coalescer = null!;
    private RequestKey _key;
    private static readonly HttpResponseMessage _sharedOkResponse =
        new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":1}") };

    [GlobalSetup]
    public void Setup()
    {
        _coalescer = new RequestCoalescer(new CoalescerOptions());
        _key = new RequestKey("GET", "https://api.example.com/benchmark");
    }

    [Params(1, 10, 100)]
    public int Concurrency { get; set; }

    [Benchmark(Description = "Sequential requests (no coalescing)")]
    public async Task SequentialRequests()
    {
        for (int i = 0; i < 10; i++)
        {
            // Each sequential request uses a unique key so no coalescing occurs
            RequestKey unique = new("GET", $"https://api.example.com/seq/{i}");
            using HttpResponseMessage response = await _coalescer.ExecuteAsync(
                unique,
                () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([])
                }));
        }
    }

    [Benchmark(Description = "Concurrent requests coalesced into one backend call")]
    public async Task ConcurrentRequestsCoalesced()
    {
        TaskCompletionSource<HttpResponseMessage> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<HttpResponseMessage>[] tasks = new Task<HttpResponseMessage>[Concurrency];

        for (int i = 0; i < Concurrency; i++)
        {
            tasks[i] = _coalescer.ExecuteAsync(
                _key,
                () => tcs.Task);
        }

        tcs.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([])
        });

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);
        _ = responses;
    }

    [Benchmark(Description = "High-frequency independent keys (no coalescing benefit)")]
    public async Task IndependentKeysNoCoalescing()
    {
        string url = $"https://api.example.com/unique/{Guid.NewGuid():N}";
        RequestKey unique = new("GET", url);

        using HttpResponseMessage response = await _coalescer.ExecuteAsync(
            unique,
            () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([])
            }));
    }
}
