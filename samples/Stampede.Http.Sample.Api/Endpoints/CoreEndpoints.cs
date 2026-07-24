namespace Stampede.Http.Sample.Api.Endpoints;

/// <summary>
/// The core RFC 9111 / RFC 5861 scenarios: freshness, conditional revalidation,
/// unsafe-method invalidation, stale-while-revalidate, stale-if-error and the
/// slow endpoint used for the stampede.
/// </summary>
public static class CoreEndpoints
{
    /// <summary>Maps the core caching endpoints.</summary>
    /// <param name="app">The route builder to map onto.</param>
    /// <returns>The same route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapCoreEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /catalog — max-age + ETag: fresh hits, then conditional revalidation
        // (a 304 costs no body). stale-if-error keeps the entry usable during outages.
        app.MapGet("/catalog", async (HttpRequest req, HttpResponse res, OriginState state) =>
        {
            state.Count("GET /catalog");
            await Task.Delay(300);

            string etag = $"\"catalog-v{state.CatalogVersion}\"";
            res.Headers.CacheControl = "public, max-age=10, stale-if-error=60";
            res.Headers.ETag = etag;

            if (req.Headers.IfNoneMatch.ToString().Contains(etag, StringComparison.Ordinal))
            {
                state.Count("GET /catalog -> 304");
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            return Results.Json(new
            {
                version = state.CatalogVersion,
                items = 120,
                servedAt = DateTimeOffset.UtcNow,
            });
        });

        // POST /catalog — unsafe method: a 2xx response makes Stampede.Http evict the
        // cached GET /catalog entry (RFC 9111 §4.4). With the Redis store that
        // invalidation is shared by every client instance.
        app.MapPost("/catalog", (OriginState state) =>
        {
            state.Count("POST /catalog");
            state.BumpCatalogVersion();
            return Results.NoContent();
        });

        // GET /feed — stale-while-revalidate: expiries are refreshed in the background,
        // so callers never wait for the origin after the first fetch.
        app.MapGet("/feed", async (HttpResponse res, OriginState state) =>
        {
            state.Count("GET /feed");
            await Task.Delay(500);
            res.Headers.CacheControl = "max-age=5, stale-while-revalidate=30";
            return Results.Json(new
            {
                generation = state.NextFeedGeneration(),
                servedAt = DateTimeOffset.UtcNow,
            });
        });

        // GET /flaky — fails in bursts. Polly retries transient blips; when the outage
        // outlasts the retries, stale-if-error shields the caller with the last good 200.
        app.MapGet("/flaky", async (HttpResponse res, OriginState state) =>
        {
            state.Count("GET /flaky");
            if (OriginState.FlakyIsDown())
            {
                state.Count("GET /flaky -> 503");
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            await Task.Delay(100);
            res.Headers.CacheControl = "max-age=5, stale-if-error=120";
            return Results.Json(new { status = "healthy", servedAt = DateTimeOffset.UtcNow });
        });

        // GET /slow — 2 s of origin latency: the stampede showcase. N concurrent
        // cold-cache callers should produce a single origin call per client process.
        app.MapGet("/slow", async (HttpResponse res, OriginState state) =>
        {
            state.Count("GET /slow");
            await Task.Delay(2000);
            res.Headers.CacheControl = "max-age=30";
            return Results.Json(new { report = "quarterly", servedAt = DateTimeOffset.UtcNow });
        });

        // GET /ledger — max-age + must-revalidate + ETag: once stale the entry may NOT
        // be served without checking the origin, so no stale window applies even under
        // failure. Contrast with /flaky.
        app.MapGet("/ledger", async (HttpRequest req, HttpResponse res, OriginState state) =>
        {
            state.Count("GET /ledger");
            await Task.Delay(200);

            const string Etag = "\"ledger-2026-07\"";
            res.Headers.CacheControl = "max-age=10, must-revalidate";
            res.Headers.ETag = Etag;

            if (req.Headers.IfNoneMatch.ToString().Contains(Etag, StringComparison.Ordinal))
            {
                state.Count("GET /ledger -> 304");
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            return Results.Json(new { balance = 42_150.75m, closed = true });
        });

        // GET /docs/{id} — validator is Last-Modified rather than ETag, so expiry
        // triggers an If-Modified-Since revalidation.
        app.MapGet("/docs/{id}", (string id, HttpRequest req, HttpResponse res, OriginState state) =>
        {
            state.Count("GET /docs/{id}");

            // Stable per-document timestamp so If-Modified-Since can match.
            DateTimeOffset lastModified = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero)
                .AddMinutes(id.GetHashCode(StringComparison.Ordinal) % 60);

            res.Headers.CacheControl = "max-age=5";
            res.Headers.LastModified = lastModified.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

            if (DateTimeOffset.TryParse(
                    req.Headers.IfModifiedSince,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out DateTimeOffset since)
                && lastModified <= since)
            {
                state.Count("GET /docs/{id} -> 304");
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            return Results.Json(new { id, title = $"Document {id}", lastModified });
        });

        return app;
    }
}
