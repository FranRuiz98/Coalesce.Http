using System.Net.Http.Headers;

namespace Coalesce.Http.Coalesce.Http.Caching;

/// <summary>
/// Computes the freshness lifetime of an HTTP response according to RFC 9111 §4.2.1.
/// Priority order: s-maxage → max-age → Expires → configured default TTL.
/// </summary>
internal static class FreshnessCalculator
{
    /// <summary>
    /// Resolves the expiration time for a cacheable response.
    /// </summary>
    /// <param name="response">The HTTP response message to evaluate.</param>
    /// <param name="options">The cache options providing the fallback TTL.</param>
    /// <returns>The absolute <see cref="DateTimeOffset"/> at which the entry should be considered stale.</returns>
    public static DateTimeOffset ComputeExpiresAt(HttpResponseMessage response, CacheOptions options)
    {
        CacheControlHeaderValue? cc = response.Headers.CacheControl;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // §4.2.1 — s-maxage (shared-cache directive, highest priority)
        if (cc?.SharedMaxAge is TimeSpan sMaxAge)
        {
            return now + sMaxAge;
        }

        // §4.2.1 — max-age
        if (cc?.MaxAge is TimeSpan maxAge)
        {
            return now + maxAge;
        }

        // §4.2.1 — Expires header (fall back to now if Date is absent)
        if (response.Content?.Headers.Expires is DateTimeOffset expires)
        {
            DateTimeOffset date = response.Headers.Date ?? now;
            TimeSpan age = expires - date;
            return now + (age > TimeSpan.Zero ? age : TimeSpan.Zero);
        }

        // §4.2.2 — heuristic / configured default
        return now + options.DefaultTtl;
    }
}
