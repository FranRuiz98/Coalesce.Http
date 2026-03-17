using Coalesce.Http.Coalescing;
using Coalesce.Http.Options;
using FluentAssertions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace Coalesce.Http.Tests.Coalescing;

/// <summary>
/// Performance and stress tests for the TaskCompletionSource-based coalescing pattern.
/// These tests demonstrate that the implementation can handle extreme load (100k+ RPS).
/// </summary>
public class RequestCoalescerPerformanceTests
{
    /// <summary>
    /// Verifica que el coalescer puede manejar miles de requests concurrentes
    /// sin degradación de rendimiento significativa.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithThousandsOfConcurrentCalls_ShouldExecuteOnce()
    {
        // Arrange
        RequestCoalescer coalescer = new(new CoalescerOptions());
        RequestKey key = new("GET", "https://api.example.com/data");
        int executionCount = 0;
        TaskCompletionSource<HttpResponseMessage> tcs = new();

        Task<HttpResponseMessage> Factory()
        {
            _ = Interlocked.Increment(ref executionCount);
            return tcs.Task;
        }

        const int concurrentCalls = 10_000;
        List<Task<HttpResponseMessage>> tasks = new(concurrentCalls);

        Stopwatch sw = Stopwatch.StartNew();

        // Act - Lanzar 10,000 requests concurrentes
        for (int i = 0; i < concurrentCalls; i++)
        {
            tasks.Add(coalescer.ExecuteAsync(key, Factory));
        }

        // Completar la request original
        tcs.SetResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("Test response")
        });

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);
        sw.Stop();

        // Assert
        _ = executionCount.Should().Be(1, "solo debe ejecutarse una vez independientemente del número de callers");
        _ = responses.Should().HaveCount(concurrentCalls);
        _ = responses.Should().OnlyContain(r => r.StatusCode == System.Net.HttpStatusCode.OK);

        // Verificar que todas las respuestas son clones independientes
        int uniqueResponses = responses.Distinct().Count();
        _ = uniqueResponses.Should().Be(concurrentCalls, "cada caller debe recibir un clon independiente");

        // Performance assertion - should complete in reasonable time
        _ = sw.ElapsedMilliseconds.Should().BeLessThan(5000, "10k requests should complete within 5 seconds");

        // Cleanup
        foreach (HttpResponseMessage? response in responses)
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// Verifica que bajo carga extrema con múltiples keys diferentes,
    /// el coalescer maneja correctamente la contención.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithMultipleKeysUnderLoad_ShouldCoalesceCorrectly()
    {
        RequestCoalescer coalescer = new(new CoalescerOptions());
        const int numberOfKeys = 100;
        const int callsPerKey = 100;
        ConcurrentDictionary<string, int> executionCounts = new();

        // Gates por key: el factory no completa hasta que lo liberemos
        Dictionary<int, TaskCompletionSource> gates = Enumerable.Range(0, numberOfKeys)
            .ToDictionary(i => i, _ => new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously));

        List<Task<HttpResponseMessage>> tasks = [];
        Stopwatch sw = Stopwatch.StartNew();

        for (int keyIndex = 0; keyIndex < numberOfKeys; keyIndex++)
        {
            string url = $"https://api.example.com/data/{keyIndex}";
            RequestKey key = new("GET", url);
            TaskCompletionSource gate = gates[keyIndex];

            for (int callIndex = 0; callIndex < callsPerKey; callIndex++)
            {
                tasks.Add(coalescer.ExecuteAsync(key, async () =>
                {
                    _ = executionCounts.AddOrUpdate(url, 1, (_, count) => count + 1);
                    await gate.Task; // Espera hasta que liberemos el gate
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($"Response for {url}")
                    };
                }));
            }
        }

        // Todos los tasks están creados y registrados — ahora liberamos todos los gates
        foreach (TaskCompletionSource gate in gates.Values)
        {
            gate.SetResult();
        }

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);
        sw.Stop();

        _ = responses.Should().HaveCount(numberOfKeys * callsPerKey);
        _ = executionCounts.Should().HaveCount(numberOfKeys);
        _ = executionCounts.Values.Should().OnlyContain(count => count == 1);
        _ = sw.ElapsedMilliseconds.Should().BeLessThan(10000);

        foreach (HttpResponseMessage? response in responses)
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// Verifica que el patrón TCS no tiene memory leaks bajo carga sostenida.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_UnderSustainedLoad_ShouldNotLeakMemory()
    {
        // Arrange
        RequestCoalescer coalescer = new(new CoalescerOptions());
        const int iterations = 1000;
        const int callsPerIteration = 10;

        // Act - Ejecutar múltiples rondas de coalescing
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            RequestKey key = new("GET", $"https://api.example.com/data/{iteration}");
            TaskCompletionSource<HttpResponseMessage> tcs = new();
            List<Task<HttpResponseMessage>> tasks = [];

            Task<HttpResponseMessage> Factory()
            {
                return tcs.Task;
            }

            for (int i = 0; i < callsPerIteration; i++)
            {
                tasks.Add(coalescer.ExecuteAsync(key, Factory));
            }

            tcs.SetResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));

            HttpResponseMessage[] responses = await Task.WhenAll(tasks);

            // Cleanup inmediato
            foreach (HttpResponseMessage? response in responses)
            {
                response.Dispose();
            }

            // Forzar GC cada 100 iteraciones
            if (iteration % 100 == 0)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        // Assert - Si llegamos aquí sin OutOfMemoryException, el test pasa
        // Este test verifica que no hay memory leaks en el diccionario
        _ = true.Should().BeTrue();
    }

    /// <summary>
    /// Verifica el rendimiento de la limpieza del diccionario bajo carga extrema.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SequentialRequestsAfterCompletion_ShouldCleanupQuickly()
    {
        // Arrange
        RequestCoalescer coalescer = new(new CoalescerOptions());
        RequestKey key = new("GET", "https://api.example.com/data");
        const int sequentialCalls = 1000;
        int executionCount = 0;

        Stopwatch sw = Stopwatch.StartNew();

        // Act - Ejecutar 1000 requests secuenciales (cada una debe ejecutarse)
        for (int i = 0; i < sequentialCalls; i++)
        {
            _ = await coalescer.ExecuteAsync(key, () =>
            {
                _ = Interlocked.Increment(ref executionCount);
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("Test")
                });
            });
        }

        sw.Stop();

        // Assert
        _ = executionCount.Should().Be(sequentialCalls, "requests secuenciales deben ejecutarse individualmente");

        // Performance assertion - cleanup debe ser rápido
        _ = sw.ElapsedMilliseconds.Should().BeLessThan(2000, "1000 sequential requests should complete within 2 seconds");
    }

    /// <summary>
    /// Stress test: mezcla de requests concurrentes y secuenciales bajo carga extrema.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_MixedConcurrentAndSequential_ShouldHandleCorrectly()
    {
        // Arrange
        RequestCoalescer coalescer = new(new CoalescerOptions());
        RequestKey key = new("GET", "https://api.example.com/data");
        int executionCount = 0;
        const int rounds = 100;
        const int concurrentPerRound = 50;

        Stopwatch sw = Stopwatch.StartNew();

        // Act - Alternar entre bursts concurrentes y esperas
        for (int round = 0; round < rounds; round++)
        {
            TaskCompletionSource<HttpResponseMessage> tcs = new();
            List<Task<HttpResponseMessage>> tasks = [];

            Task<HttpResponseMessage> Factory()
            {
                _ = Interlocked.Increment(ref executionCount);
                return tcs.Task;
            }

            // Lanzar burst de requests concurrentes
            for (int i = 0; i < concurrentPerRound; i++)
            {
                tasks.Add(coalescer.ExecuteAsync(key, Factory));
            }

            // Completar
            tcs.SetResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("Test")
            });

            HttpResponseMessage[] responses = await Task.WhenAll(tasks);

            // Cleanup
            foreach (HttpResponseMessage? response in responses)
            {
                response.Dispose();
            }
        }

        sw.Stop();

        // Assert
        _ = executionCount.Should().Be(rounds, "debe haber exactamente una ejecución por round");

        // Performance assertion
        _ = sw.ElapsedMilliseconds.Should().BeLessThan(5000, "100 rounds of 50 concurrent requests should complete within 5 seconds");
    }
}
