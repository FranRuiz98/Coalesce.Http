using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Polly;
using Stampede.Http.Extensions;
using Stampede.Http.Metrics;
using System.Diagnostics;
using System.Diagnostics.Metrics;

// ---------------------------------------------------------------------------
// Sample client — a realistic Stampede.Http pipeline:
//
//   CachingMiddleware  (RFC 9111, entries shared across instances via Redis)
//     └─ CoalescingHandler  (per-process request deduplication)
//          └─ Polly  (retry with exponential backoff + per-attempt timeout)
//               └─ SocketsHttpHandler → the sample API
//
// Run two replicas (docker compose does) and watch the Redis-backed cache be
// shared while coalescing stays per-process.
// ---------------------------------------------------------------------------

string instance = Environment.GetEnvironmentVariable("INSTANCE") ?? Environment.MachineName;
string apiBase = Environment.GetEnvironmentVariable("API__BASEURL") ?? "http://localhost:5080";
string redisConn = Environment.GetEnvironmentVariable("REDIS__CONNECTION") ?? "localhost:6379";

Log($"starting — api: {apiBase}  redis: {redisConn}", ConsoleColor.Cyan);

// -- In-process metrics: aggregate every stampede_http.* instrument ----------
var metrics = new Dictionary<string, long>(StringComparer.Ordinal);
using var listener = new MeterListener();
listener.InstrumentPublished = (instrument, l) =>
{
    if (instrument.Meter.Name == StampedeHttpMetrics.MeterName)
        l.EnableMeasurementEvents(instrument);
};
listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
{
    lock (metrics)
    {
        metrics.TryGetValue(instrument.Name, out long prev);
        metrics[instrument.Name] = prev + measurement;
    }
});
listener.Start();

// -- Prometheus scrape endpoint: the same "Stampede.Http" meter, exported via
// OpenTelemetry so Prometheus/Grafana can graph it in real time. -------------
int metricsPort = int.TryParse(Environment.GetEnvironmentVariable("METRICS__PORT"), out int parsedPort) ? parsedPort : 9464;
// Must be a real resolvable hostname/IP — the Prometheus exporter's Host option is validated
// via UriBuilder (rejects "+"/"*") and .NET's HttpListener itself refuses a literal "0.0.0.0".
// docker-compose sets METRICS__HOST to each container's own `hostname` (e.g. "client-a"),
// which Docker's embedded DNS also resolves to that same container from other containers —
// so this is reachable AND unprivileged. Local (non-container) runs keep "localhost" below.
string metricsHost = Environment.GetEnvironmentVariable("METRICS__HOST") ?? "localhost";
using MeterProvider meterProvider = Sdk.CreateMeterProviderBuilder()
    .AddMeter(StampedeHttpMetrics.MeterName)
    .AddPrometheusHttpListener(o =>
    {
        o.Host = metricsHost;
        o.Port = metricsPort;
    })
    .Build();
Log($"Prometheus metrics exposed — host: {metricsHost}  port: {metricsPort}  path: /metrics", ConsoleColor.Cyan);

// -- The pipeline ------------------------------------------------------------
var services = new ServiceCollection();

services.AddStackExchangeRedisCache(o => o.Configuration = redisConn);

services.AddHttpClient("api", c => c.BaseAddress = new Uri(apiBase))
    .AddStampedeHttp(
        configureCaching: o => o.DefaultTtl = TimeSpan.FromSeconds(5),
        configureCoalescing: o => o.CoalescingTimeout = TimeSpan.FromSeconds(10))
    .UseDistributedCacheStore()
    .AddResilienceHandler("sample-resilience", b =>
    {
        // Polly sits BELOW the coalescer: a retry storm is coalesced too.
        b.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 2,
            Delay = TimeSpan.FromMilliseconds(200),
            BackoffType = DelayBackoffType.Exponential,
        });
        b.AddTimeout(TimeSpan.FromSeconds(5));
    });

await using var provider = services.BuildServiceProvider();
var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");

// -- Wait for the API --------------------------------------------------------
for (int attempt = 1; ; attempt++)
{
    try
    {
        using var ping = await client.GetAsync("/stats");
        if (ping.IsSuccessStatusCode) break;
    }
    catch when (attempt < 30)
    {
    }

    if (attempt >= 30)
    {
        Log("API not reachable after 30 attempts — giving up.", ConsoleColor.Red);
        return 1;
    }

    await Task.Delay(1000);
}

Log("API is up.", ConsoleColor.Cyan);

// -- Phase 1: the stampede ---------------------------------------------------
Log("PHASE 1 — stampede: 10 concurrent GET /slow (origin takes ~2 s each)", ConsoleColor.Yellow);
var sw = Stopwatch.StartNew();
var burst = Enumerable.Range(0, 10).Select(_ => client.GetAsync("/slow")).ToArray();
var burstResponses = await Task.WhenAll(burst);
sw.Stop();
Log($"  10 callers finished in {sw.ElapsedMilliseconds} ms — " +
    $"{burstResponses.Count(r => r.IsSuccessStatusCode)}/10 OK. " +
    "Coalescing collapsed this instance's burst into (at most) one origin call.", ConsoleColor.Green);

// -- Phase 2: steady-state loop ----------------------------------------------
Log("PHASE 2 — steady state: /catalog + /feed + /flaky every 2 s. " +
    "Try `docker compose stop api` and watch stale-if-error shield the 200s.", ConsoleColor.Yellow);

int iteration = 0;
while (true)
{
    iteration++;

    await Probe("/catalog");
    await Probe("/feed");
    await Probe("/flaky");

    // Every 10th iteration: mutate the catalog. The 2xx POST makes
    // CachingMiddleware evict the shared /catalog entry (RFC 9111 §4.4),
    // so the next GET — from ANY instance — refetches and sees a new ETag.
    if (iteration % 10 == 0)
    {
        try
        {
            using var post = await client.PostAsync("/catalog", content: null);
            Log($"  POST /catalog -> {(int)post.StatusCode} — cached entry invalidated for every instance", ConsoleColor.Magenta);
        }
        catch (Exception ex)
        {
            Log($"  POST /catalog failed: {ex.GetBaseException().Message}", ConsoleColor.Red);
        }
    }

    if (iteration % 8 == 0)
    {
        PrintMetrics();
    }

    await Task.Delay(2000);
}

async Task Probe(string path)
{
    var probeSw = Stopwatch.StartNew();
    try
    {
        using var response = await client!.GetAsync(path);
        probeSw.Stop();

        TimeSpan? age = response.Headers.Age;
        string ageText = age is null ? "fresh from origin" : $"Age: {age.Value.TotalSeconds:F0}s";
        string body = await response.Content.ReadAsStringAsync();
        if (body.Length > 60) body = body[..60] + "…";

        var color = response.IsSuccessStatusCode
            ? (age is { TotalSeconds: > 10 } ? ConsoleColor.DarkYellow : ConsoleColor.Green)
            : ConsoleColor.Red;
        string staleHint = age is { TotalSeconds: > 10 } ? "  [served beyond max-age: stale window or revalidated entry]" : string.Empty;

        Log($"  GET {path,-9} -> {(int)response.StatusCode}  {probeSw.ElapsedMilliseconds,5} ms  ({ageText}){staleHint}  {body}", color);
    }
    catch (Exception ex)
    {
        probeSw.Stop();
        Log($"  GET {path,-9} -> EXCEPTION after {probeSw.ElapsedMilliseconds} ms: {ex.GetBaseException().Message}", ConsoleColor.Red);
    }
}

void PrintMetrics()
{
    lock (metrics)
    {
        if (metrics.Count == 0) return;
        Log("  ── stampede_http metrics (this instance) ─────────────────", ConsoleColor.Cyan);
        foreach (var (name, value) in metrics.OrderBy(kv => kv.Key))
        {
            Log($"     {name,-50} {value,6}", ConsoleColor.Cyan);
        }
    }
}

void Log(string message, ConsoleColor color)
{
    Console.ForegroundColor = color;
    Console.WriteLine($"[{instance}] {message}");
    Console.ResetColor();
}
