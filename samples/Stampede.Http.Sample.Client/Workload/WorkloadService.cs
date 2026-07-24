using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Stampede.Http.Sample.Client.Workload;

/// <summary>
/// The scripted traffic that makes the sample tell a story on its own, without anyone
/// having to drive it: an opening stampede, the narrated feature tour, then a steady-state
/// loop that runs until the container is stopped.
/// </summary>
/// <remarks>
/// For request-driven load instead, set <c>Sample:Workload:Enabled=false</c> and use the
/// k6 profile (<c>docker compose --profile load up k6</c>) or the endpoints in <c>samples.http</c>.
/// </remarks>
/// <param name="scopeFactory">Used to resolve the typed client per unit of work.</param>
/// <param name="options">Sample options.</param>
/// <param name="counters">Local mirror of the Stampede.Http instruments.</param>
/// <param name="logger">Logger.</param>
public sealed class WorkloadService(
    IServiceScopeFactory scopeFactory,
    IOptions<SampleOptions> options,
    StampedeCounters counters,
    ILogger<WorkloadService> logger) : BackgroundService
{
    private static readonly string[] SteadyStatePaths = ["/catalog", "/feed", "/flaky"];

    private readonly SampleOptions _options = options.Value;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WorkloadOptions workload = _options.Workload;

        if (!workload.Enabled)
        {
            logger.LogInformation("Scripted workload disabled — this instance only serves requests.");
            return;
        }

        try
        {
            if (!await WaitForOriginAsync(stoppingToken).ConfigureAwait(false))
            {
                return;
            }

            await RunStampedeAsync(workload.StampedeSize, stoppingToken).ConfigureAwait(false);

            if (workload.FeatureTour)
            {
                await WithClientAsync((_, tour, ct) => tour.RunAsync(ct), stoppingToken).ConfigureAwait(false);
            }

            await RunSteadyStateAsync(workload, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown: docker compose stop / Ctrl+C.
            logger.LogInformation("Workload stopped.");
        }
        catch (Exception ex)
        {
            // An unhandled BackgroundService exception stops the host by default. The scripted
            // workload is a demo aid, not the reason this service exists — keep serving requests.
            logger.LogError(ex, "Workload failed; the HTTP surface stays up.");
        }
    }

    /// <summary>Polls the origin until it answers, so the sample survives any container start order.</summary>
    private async Task<bool> WaitForOriginAsync(CancellationToken ct)
    {
        const int MaxAttempts = 30;

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                OriginClient client = scope.ServiceProvider.GetRequiredService<OriginClient>();
                using HttpResponseMessage response = await client.Http.GetAsync("/health", ct).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation("Origin is up at {BaseUrl}.", _options.ApiBaseUrl);
                    return true;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Origin not listening yet.
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }

        logger.LogError("Origin not reachable after {Attempts} attempts — giving up.", MaxAttempts);
        return false;
    }

    /// <summary>Phase 1 — the thundering herd: N concurrent cold-cache callers for a 2 s endpoint.</summary>
    private async Task RunStampedeAsync(int size, CancellationToken ct)
    {
        logger.LogInformation(
            "PHASE 1 — stampede: {Size} concurrent GET /slow (the origin takes ~2 s per call)", size);

        long start = Stopwatch.GetTimestamp();

        ProbeResult[] results = await WithClientAsync(
            (client, _, token) => Task.WhenAll(
                Enumerable.Range(0, size).Select(_ => client.GetAsync("/slow", cancellationToken: token))),
            ct).ConfigureAwait(false);

        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

        logger.LogInformation(
            "  {Size} callers finished in {Elapsed} ms — {Ok}/{Size} OK. {Verdict}",
            size,
            (long)elapsed.TotalMilliseconds,
            results.Count(r => r.StatusCode == 200),
            size,
            _options.Pipeline.Enabled
                ? "Coalescing collapsed this instance's burst into a single origin call."
                : "No coalescing on this instance — every caller went to the origin.");
    }

    /// <summary>Phase 3 — steady state: the loop that feeds the dashboards.</summary>
    private async Task RunSteadyStateAsync(WorkloadOptions workload, CancellationToken ct)
    {
        logger.LogInformation(
            "PHASE 3 — steady state: /catalog + /feed + /flaky every {Interval}s, plus a burst of " +
            "{BurstSize} concurrent GET /slow every {BurstEvery} iterations. " +
            "Try `docker compose stop api` and watch stale-if-error keep the 200s coming.",
            workload.Interval.TotalSeconds,
            workload.BurstSize,
            workload.BurstEvery);

        for (int iteration = 1; !ct.IsCancellationRequested; iteration++)
        {
            int currentIteration = iteration;

            await WithClientAsync(async (client, _, token) =>
            {
                foreach (string path in SteadyStatePaths)
                {
                    logger.LogInformation("  {Probe}", (await Probe(client, path, token).ConfigureAwait(false)).Describe());
                }

                // Every Nth iteration: a burst of concurrent callers for one resource. The three
                // probes above are sequential, so on their own they give the coalescer nothing to
                // do — deduplication needs requests to overlap in time, which is what real inbound
                // traffic does and a polling loop does not.
                if (currentIteration % workload.BurstEvery == 0)
                {
                    ProbeResult[] burst = await Task.WhenAll(
                        Enumerable.Range(0, workload.BurstSize)
                                  .Select(_ => Probe(client, "/slow", token))).ConfigureAwait(false);

                    logger.LogInformation(
                        "  burst: {Size} concurrent GET /slow -> {Ok} OK in {Elapsed} ms (slowest caller)",
                        workload.BurstSize,
                        burst.Count(r => r.StatusCode == 200),
                        burst.Max(r => r.ElapsedMs));
                }

                // Every Nth iteration: mutate the catalog. The 2xx POST makes CachingMiddleware
                // evict the shared /catalog entry (RFC 9111 §4.4), so the next GET — from ANY
                // instance — refetches and sees a new ETag.
                if (currentIteration % workload.MutateEvery == 0)
                {
                    int status = await client.PostAsync("/catalog", token).ConfigureAwait(false);
                    logger.LogInformation(
                        "  POST /catalog -> {Status} — cached entry invalidated for every instance", status);
                }

                return 0;
            }, ct).ConfigureAwait(false);

            if (iteration % 8 == 0)
            {
                LogCounters();
            }

            await Task.Delay(workload.Interval, ct).ConfigureAwait(false);
        }
    }

    private static async Task<ProbeResult> Probe(OriginClient client, string path, CancellationToken ct)
    {
        try
        {
            return await client.GetAsync(path, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeResult(path, 0, 0, null, $"EXCEPTION: {ex.GetBaseException().Message}");
        }
    }

    private void LogCounters()
    {
        IReadOnlyDictionary<string, long> snapshot = counters.Snapshot();
        if (snapshot.Count == 0)
        {
            return;
        }

        logger.LogInformation("  ── stampede_http instruments (this instance) ──");
        foreach ((string name, long value) in snapshot)
        {
            logger.LogInformation("     {Name,-52} {Value,6}", name, value);
        }
    }

    /// <summary>
    /// Resolves the typed client from a fresh scope for each unit of work. A
    /// <see cref="BackgroundService"/> is a singleton, and holding a typed client for the
    /// lifetime of the process would pin one <c>HttpMessageHandler</c> past its rotation window.
    /// </summary>
    private async Task<T> WithClientAsync<T>(
        Func<OriginClient, FeatureTour, CancellationToken, Task<T>> work,
        CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        return await work(
            scope.ServiceProvider.GetRequiredService<OriginClient>(),
            scope.ServiceProvider.GetRequiredService<FeatureTour>(),
            ct).ConfigureAwait(false);
    }

    private async Task WithClientAsync(
        Func<OriginClient, FeatureTour, CancellationToken, Task> work,
        CancellationToken ct)
    {
        _ = await WithClientAsync(async (client, tour, token) =>
        {
            await work(client, tour, token).ConfigureAwait(false);
            return 0;
        }, ct).ConfigureAwait(false);
    }
}
