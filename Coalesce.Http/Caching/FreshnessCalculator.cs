using System.Net.Http.Headers;

namespace Coalesce.Http.Caching;

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
    /// <param name="timeProvider">
    /// The <see cref="TimeProvider"/> used to obtain the current time.
    /// Defaults to <see cref="TimeProvider.System"/> when <see langword="null"/>.
    /// </param>
    /// <returns>The absolute <see cref="DateTimeOffset"/> at which the entry should be considered stale.</returns>
    public static DateTimeOffset ComputeExpiresAt(HttpResponseMessage response, CacheOptions options, TimeProvider? timeProvider = null)
    {
        CacheControlHeaderValue? cc = response.Headers.CacheControl;
        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();

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

    /// <summary>
    /// Extracts the effective <c>stale-if-error</c> window in seconds from a response (RFC 5861 §4).
    /// Falls back to <see cref="CacheOptions.DefaultStaleIfErrorSeconds"/> when the directive is absent.
    /// </summary>
    /// <param name="response">The HTTP response message to inspect.</param>
    /// <param name="options">The cache options providing the fallback value.</param>
    /// <returns>
    /// Seconds after <c>ExpiresAt</c> during which a stale entry may be served on error,
    /// or <c>0</c> when stale-if-error is disabled.
    /// </returns>
    public static long ExtractStaleIfError(HttpResponseMessage response, CacheOptions options)
    {
        CacheControlHeaderValue? cc = response.Headers.CacheControl;

        if (cc is not null)
        {
            foreach (NameValueHeaderValue ext in cc.Extensions)
            {
                if (ext.Name.Equals("stale-if-error", StringComparison.OrdinalIgnoreCase))
                {
                    ReadOnlySpan<char> raw = ext.Value.AsSpan().Trim('"');
                    if (long.TryParse(raw, out long seconds) && seconds >= 0)
                    {
                        return seconds;
                    }
                }
            }
        }

        return options.DefaultStaleIfErrorSeconds;
    }

    /// <summary>
    /// Extracts the effective <c>stale-while-revalidate</c> window in seconds from a response (RFC 5861 §3).
    /// Falls back to <see cref="CacheOptions.DefaultStaleWhileRevalidateSeconds"/> when the directive is absent.
    /// </summary>
    /// <param name="response">The HTTP response message to inspect.</param>
    /// <param name="options">The cache options providing the fallback value.</param>
    /// <returns>
    /// Seconds after <c>ExpiresAt</c> during which a stale entry may be served immediately while
    /// a background revalidation is triggered, or <c>0</c> when stale-while-revalidate is disabled.
    /// </returns>
    public static long ExtractStaleWhileRevalidate(HttpResponseMessage response, CacheOptions options)
    {
        CacheControlHeaderValue? cc = response.Headers.CacheControl;

        if (cc is not null)
        {
            foreach (NameValueHeaderValue ext in cc.Extensions)
            {
                if (ext.Name.Equals("stale-while-revalidate", StringComparison.OrdinalIgnoreCase))
                {
                    ReadOnlySpan<char> raw = ext.Value.AsSpan().Trim('"');
                    if (long.TryParse(raw, out long seconds) && seconds >= 0)
                    {
                        return seconds;
                    }
                }
            }
        }

        return options.DefaultStaleWhileRevalidateSeconds;
    }
}
