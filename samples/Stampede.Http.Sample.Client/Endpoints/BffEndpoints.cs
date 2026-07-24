using Microsoft.Extensions.Options;
using Stampede.Http.Caching;
using Stampede.Http.Options;
using Stampede.Http.Sample.Client.Workload;

namespace Stampede.Http.Sample.Client.Endpoints;

/// <summary>
/// The client's own HTTP surface. Every endpoint is a thin pass-through to the origin, which
/// is what makes the sample driveable by a real load generator or by curl instead of only by
/// the scripted workload — the stampede then comes from actual concurrent inbound traffic.
/// </summary>
public static class BffEndpoints
{
    /// <summary>Origin paths exposed verbatim under <c>/api</c>, one per caching behaviour.</summary>
    private static readonly string[] PassThroughPaths = ["/catalog", "/feed", "/flaky", "/slow", "/ledger", "/bulk"];

    /// <summary>Maps the client's endpoints.</summary>
    /// <param name="app">The route builder to map onto.</param>
    /// <returns>The same route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapBffEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));

        RouteGroupBuilder api = app.MapGroup("/api");

        // Straight pass-throughs — one per caching behaviour.
        foreach (string path in PassThroughPaths)
        {
            _ = api.MapGet(path, (OriginClient client, CancellationToken ct) =>
                client.GetAsync(path, cancellationToken: ct));
        }

        // Content negotiation: same URL, one cached representation per language.
        _ = api.MapGet("/greetings", (OriginClient client, string? lang, CancellationToken ct) =>
            client.GetAsync(
                "/greetings",
                r => r.Headers.TryAddWithoutValidation("Accept-Language", lang ?? "en-GB"),
                ct));

        // Multi-tenant: the tenant travels in a header that is both a Vary field at the
        // origin and a coalescing key on the client.
        _ = api.MapGet("/tenants/{tenant}", (OriginClient client, string tenant, CancellationToken ct) =>
            client.GetAsync("/tenants/data", r => r.Headers.Add("X-Tenant-Id", tenant), ct));

        _ = api.MapGet("/assets/{id}", (OriginClient client, string id, CancellationToken ct) =>
            client.GetAsync($"/assets/{id}", cancellationToken: ct));

        _ = api.MapGet("/docs/{id}", (OriginClient client, string id, CancellationToken ct) =>
            client.GetAsync($"/docs/{id}", cancellationToken: ct));

        _ = api.MapGet("/search", (OriginClient client, HttpRequest request, CancellationToken ct) =>
            client.GetAsync($"/search{request.QueryString}", cancellationToken: ct));

        // The mutation that evicts the shared GET entry for every instance (RFC 9111 §4.4).
        _ = api.MapPost("/catalog", async (OriginClient client, CancellationToken ct) =>
            Results.Ok(new { originStatus = await client.PostAsync("/catalog", ct) }));

        // What the origin actually received, straight from the origin's own counters.
        _ = api.MapGet("/origin-stats", (OriginClient client, CancellationToken ct) =>
            client.GetOriginCountersAsync(ct));

        // The Stampede.Http instrument totals for this process, in JSON. The same values are
        // exported for Prometheus at /metrics.
        _ = api.MapGet("/counters", (StampedeCounters counters) => counters.Snapshot());

        // The effective, currently-loaded options. Edit samples/config/client.json while the
        // stack is running and refresh this: IOptionsMonitor picks the change up with no
        // restart. Structural options (MaxCacheSize, NormalizeQueryParameters,
        // RevalidationGraceSeconds) are read once at registration and will not move.
        _ = api.MapGet("/config", (
            IOptions<SampleOptions> sample,
            IOptionsMonitor<CacheOptions> cache,
            IOptionsMonitor<CoalescerOptions> coalescing) =>
        {
            if (!sample.Value.Pipeline.Enabled)
            {
                // The control group has no handlers registered, so the options objects would
                // report library defaults that nothing is actually using.
                return Results.Json(new
                {
                    instance = sample.Value.Instance,
                    stampedeEnabled = false,
                    note = "Control group: no Stampede.Http handlers are registered on this instance.",
                });
            }

            CacheOptions c = cache.Get(PipelineRegistration.ClientName);
            CoalescerOptions q = coalescing.Get(PipelineRegistration.ClientName);

            return Results.Json(new
            {
                instance = sample.Value.Instance,
                stampedeEnabled = true,
                cache = new
                {
                    c.DefaultTtl,
                    c.MaxBodySizeBytes,
                    c.DefaultStaleIfErrorSeconds,
                    c.DefaultStaleWhileRevalidateSeconds,
                    c.RevalidationGraceSeconds,
                    c.NormalizeQueryParameters,
                    c.MaxCacheSize,
                },
                coalescing = new
                {
                    q.Enabled,
                    q.CoalescingTimeout,
                    q.MaxResponseBodyBytes,
                    q.CoalesceKeyHeaders,
                },
            });
        });

        return app;
    }
}
