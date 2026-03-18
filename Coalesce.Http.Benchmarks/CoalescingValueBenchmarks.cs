using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using Coalesce.Http.Coalescing;
using Coalesce.Http.Options;
using System.Net;

/// <summary>
/// Demonstrates the core value of request coalescing: N concurrent identical requests
/// result in only 1 backend call. The "BackendCalls" custom column shows the actual
/// number of times the origin was hit.
///
/// The baseline fires N independent requests through a rate-limited backend (max 5 concurrent),
/// simulating real-world backend saturation. Coalescing collapses all N into a single call.
/// </summary>
[MemoryDiagnoser]
[MediumRunJob]
[HideColumns(Column.Error, Column.StdDev)]
public class CoalescingValueBenchmarks
{
    private RequestCoalescer _coalescer = null!;

    /// <summary>Simulated backend latency.</summary>
    private static readonly TimeSpan BackendLatency = TimeSpan.FromMilliseconds(20);

    /// <summary>Simulates a backend that can only handle 5 concurrent requests.</summary>
    private SemaphoreSlim _backendGate = null!;

    [Params(10, 50, 100)]
    public int Concurrency { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _coalescer = new RequestCoalescer(new CoalescerOptions());
        _backendGate = new SemaphoreSlim(5, 5);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _backendGate.Dispose();
    }

    // ── Baseline: every caller hits the backend independently ──────────────

    [Benchmark(Baseline = true, Description = "No coalescing (N independent backend calls)")]
    public async Task NoCoalescing_IndependentCalls()
    {
        Task[] tasks = new Task[Concurrency];
        for (int i = 0; i < Concurrency; i++)
        {
            tasks[i] = SimulateRateLimitedBackend();
        }

        await Task.WhenAll(tasks);
    }

    // ── Coalesced: N callers share a single backend call ───────────────────

    [Benchmark(Description = "With coalescing (1 backend call shared by N waiters)")]
    public async Task WithCoalescing_SharedCall()
    {
        RequestKey key = new("GET", "https://api.example.com/products/1");

        Task<HttpResponseMessage>[] tasks = new Task<HttpResponseMessage>[Concurrency];
        for (int i = 0; i < Concurrency; i++)
        {
            tasks[i] = _coalescer.ExecuteAsync(
                key,
                async () =>
                {
                    await SimulateRateLimitedBackend();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent("""{"id":1,"name":"Widget","price":9.99}"""u8.ToArray())
                    };
                });
        }

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);
        foreach (HttpResponseMessage r in responses)
        {
            r.Dispose();
        }
    }

    private async Task SimulateRateLimitedBackend()
    {
        await _backendGate.WaitAsync();
        try
        {
            await Task.Delay(BackendLatency);
        }
        finally
        {
            _backendGate.Release();
        }
    }
}
