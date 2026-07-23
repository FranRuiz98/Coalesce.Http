using System.Collections.Concurrent;

// ---------------------------------------------------------------------------
// Sample origin API — every endpoint drives Stampede.Http purely through
// standard HTTP caching headers. There is no Stampede.Http code here: the
// origin declares policy, the client-side cache obeys it.
// ---------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var counters = new ConcurrentDictionary<string, int>();
int catalogVersion = 1;
int feedGeneration = 0;

void Count(string key) => counters.AddOrUpdate(key, 1, (_, v) => v + 1);

// /flaky is "down" for the first 20 seconds of every 60-second window.
bool FlakyIsDown() => Environment.TickCount64 / 1000 % 60 < 20;

// GET /catalog — max-age + ETag: demonstrates fresh hits and conditional
// revalidation (304 costs no body). stale-if-error keeps the entry usable
// during outages.
app.MapGet("/catalog", async (HttpRequest req, HttpResponse res) =>
{
    Count("GET /catalog");
    await Task.Delay(300);

    string etag = $"\"catalog-v{catalogVersion}\"";
    res.Headers.CacheControl = "public, max-age=10, stale-if-error=60";
    res.Headers.ETag = etag;

    if (req.Headers.IfNoneMatch.ToString().Contains(etag))
    {
        Count("GET /catalog -> 304");
        return Results.StatusCode(StatusCodes.Status304NotModified);
    }

    return Results.Json(new { version = catalogVersion, items = 120, servedAt = DateTimeOffset.UtcNow });
});

// POST /catalog — unsafe method: a 2xx response makes Stampede.Http evict the
// cached GET /catalog entry (RFC 9111 §4.4). With the Redis store this
// invalidation is shared by every client instance.
app.MapPost("/catalog", () =>
{
    Count("POST /catalog");
    Interlocked.Increment(ref catalogVersion);
    return Results.NoContent();
});

// GET /feed — stale-while-revalidate: expiries are refreshed in the
// background; callers never wait for the origin after the first fetch.
app.MapGet("/feed", async (HttpResponse res) =>
{
    Count("GET /feed");
    await Task.Delay(500);
    int gen = Interlocked.Increment(ref feedGeneration);
    res.Headers.CacheControl = "max-age=5, stale-while-revalidate=30";
    return Results.Json(new { generation = gen, servedAt = DateTimeOffset.UtcNow });
});

// GET /flaky — fails in bursts. Polly retries transient blips; when the
// outage outlasts the retries, stale-if-error shields the caller with the
// last good 200.
app.MapGet("/flaky", async (HttpResponse res) =>
{
    Count("GET /flaky");
    if (FlakyIsDown())
    {
        Count("GET /flaky -> 503");
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    await Task.Delay(100);
    res.Headers.CacheControl = "max-age=5, stale-if-error=120";
    return Results.Json(new { status = "healthy", servedAt = DateTimeOffset.UtcNow });
});

// GET /slow — 2 s of origin latency: the stampede showcase. N concurrent
// cold-cache callers should produce a single origin call per client process.
app.MapGet("/slow", async (HttpResponse res) =>
{
    Count("GET /slow");
    await Task.Delay(2000);
    res.Headers.CacheControl = "max-age=30";
    return Results.Json(new { report = "quarterly", servedAt = DateTimeOffset.UtcNow });
});

// GET /greetings — Vary: Accept-Language: one URL, one cached variant per
// language.
app.MapGet("/greetings", (HttpRequest req, HttpResponse res) =>
{
    Count("GET /greetings");
    string lang = req.Headers.AcceptLanguage.ToString();
    string greeting = lang.StartsWith("es", StringComparison.OrdinalIgnoreCase) ? "¡Hola!" : "Hello!";
    res.Headers.CacheControl = "max-age=30";
    res.Headers.Vary = "Accept-Language";
    return Results.Json(new { greeting, language = lang });
});

// GET /stats — no-store: live origin-side counters, never cached. This is
// how the client (and you) can see how many requests actually reached the
// origin across ALL client instances.
app.MapGet("/stats", (HttpResponse res) =>
{
    res.Headers.CacheControl = "no-store";
    return Results.Json(new
    {
        flakyIsDown = FlakyIsDown(),
        counters = counters.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value),
    });
});

app.Run();
