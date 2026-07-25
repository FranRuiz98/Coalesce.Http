using System.Text;

namespace Stampede.Http.Sample.Api.Endpoints;

/// <summary>
/// Endpoints that exercise content negotiation (<c>Vary</c>), multi-tenancy,
/// <c>immutable</c>, the body-size ceiling and query-parameter normalisation.
/// </summary>
public static class VariantEndpoints
{
    private static readonly string[] BulkPayload = Enumerable.Range(0, 20_000)
        .Select(i => $"row-{i:D6}-{new string('x', 80)}")
        .ToArray();

    /// <summary>Maps the variant / policy-showcase endpoints.</summary>
    /// <param name="app">The route builder to map onto.</param>
    /// <returns>The same route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapVariantEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /greetings — Vary: Accept-Language. One URL, one cached variant per
        // language (RFC 9111 §4.1). Before v2.2.0 these overwrote each other.
        app.MapGet("/greetings", async (HttpRequest req, HttpResponse res, OriginState state) =>
        {
            state.Count("GET /greetings");
            await Task.Delay(200);

            string lang = req.Headers.AcceptLanguage.ToString();
            string greeting = lang.StartsWith("es", StringComparison.OrdinalIgnoreCase) ? "¡Hola!"
                : lang.StartsWith("fr", StringComparison.OrdinalIgnoreCase) ? "Bonjour !"
                : "Hello!";

            res.Headers.CacheControl = "max-age=30";
            res.Headers.Vary = "Accept-Language";
            return Results.Json(new { greeting, language = lang, servedAt = DateTimeOffset.UtcNow });
        });

        // GET /tenants/data — the multi-tenant case: one URL, tenant chosen by header.
        // Vary: X-Tenant-Id gives each tenant its own cache entry, and the client adds
        // X-Tenant-Id to CoalesceKeyHeaders so concurrent bursts are deduplicated
        // per tenant instead of collapsing two tenants into one response.
        app.MapGet("/tenants/data", async (HttpRequest req, HttpResponse res, OriginState state) =>
        {
            string tenant = req.Headers["X-Tenant-Id"].ToString();
            tenant = string.IsNullOrWhiteSpace(tenant) ? "public" : tenant;

            state.Count($"GET /tenants/data [{tenant}]");
            await Task.Delay(1000);

            res.Headers.CacheControl = "max-age=20";
            res.Headers.Vary = "X-Tenant-Id";
            return Results.Json(new { tenant, seats = tenant.Length * 10, servedAt = DateTimeOffset.UtcNow });
        });

        // GET /assets/{id} — Cache-Control: immutable (RFC 8246). A fresh immutable
        // entry skips revalidation even when the caller asks for one, which is exactly
        // what you want for content-addressed assets.
        app.MapGet("/assets/{id}", (string id, HttpResponse res, OriginState state) =>
        {
            state.Count("GET /assets/{id}");
            res.Headers.CacheControl = "public, max-age=31536000, immutable";
            res.Headers.ETag = $"\"asset-{id}\"";
            return Results.Json(new { id, bytes = 4096, servedAt = DateTimeOffset.UtcNow });
        });

        // GET /bulk — a ~1.8 MB body with a perfectly good max-age. It still never gets
        // cached: it exceeds CacheOptions.MaxBodySizeBytes (1 MB by default), so the
        // cache declines to store it rather than blowing up memory.
        app.MapGet("/bulk", (HttpResponse res, OriginState state) =>
        {
            state.Count("GET /bulk");
            res.Headers.CacheControl = "max-age=300";

            StringBuilder sb = new(BulkPayload.Length * 90);
            foreach (string row in BulkPayload)
            {
                _ = sb.AppendLine(row);
            }

            return Results.Text(sb.ToString(), "text/plain");
        });

        // GET /search — the same logical query can arrive with its parameters in any
        // order. With CacheOptions.NormalizeQueryParameters the client folds
        // ?a=1&b=2 and ?b=2&a=1 onto one entry; without it they are two misses.
        app.MapGet("/search", (HttpRequest req, HttpResponse res, OriginState state) =>
        {
            state.Count("GET /search");
            res.Headers.CacheControl = "max-age=60";

            var query = req.Query
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal);

            return Results.Json(new { query, servedAt = DateTimeOffset.UtcNow });
        });

        return app;
    }
}
