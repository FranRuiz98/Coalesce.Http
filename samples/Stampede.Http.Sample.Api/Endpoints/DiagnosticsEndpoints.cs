namespace Stampede.Http.Sample.Api.Endpoints;

/// <summary>
/// Endpoints the origin uses to describe itself: liveness, live counters and a
/// counter reset used by the automated smoke test.
/// </summary>
public static class DiagnosticsEndpoints
{
    /// <summary>Maps the diagnostics endpoints.</summary>
    /// <param name="app">The route builder to map onto.</param>
    /// <returns>The same route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /health — compose healthcheck target. Never cached.
        app.MapGet("/health", (HttpResponse res) =>
        {
            res.Headers.CacheControl = "no-store";
            return Results.Ok(new { status = "healthy" });
        });

        // GET /stats — no-store: live origin-side counters. This is how you see how
        // many requests actually reached the origin across ALL client instances.
        app.MapGet("/stats", (HttpResponse res, OriginState state) =>
        {
            res.Headers.CacheControl = "no-store";
            return Results.Json(new
            {
                flakyIsDown = OriginState.FlakyIsDown(),
                catalogVersion = state.CatalogVersion,
                counters = state.Snapshot(),
            });
        });

        // POST /stats/reset — clears the counters so a measurement window can start
        // from zero. Used by scripts/smoke-test.sh.
        app.MapPost("/stats/reset", (HttpResponse res, OriginState state) =>
        {
            res.Headers.CacheControl = "no-store";
            state.ResetCounters();
            return Results.NoContent();
        });

        return app;
    }
}
