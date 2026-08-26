namespace Stampede.Http;

/// <summary>
/// The synthetic <c>X-Stampede-Cache</c> response header Stampede.Http sets on every response its caching
/// layer handled, reporting how the response was obtained — from cache, from the origin, or from another
/// caller's shared in-flight origin call. Useful for debugging and for integration tests asserting cache
/// behavior without instrumenting metrics.
/// </summary>
/// <remarks>
/// <para>The header carries exactly one of the constant values below:</para>
/// <list type="bullet">
///   <item><term><see cref="Hit"/></term><description>Served from a fresh cache entry, no origin contact (includes locally answered conditional requests returning <c>304</c>).</description></item>
///   <item><term><see cref="Stale"/></term><description>Served from an expired entry — <c>stale-while-revalidate</c>, <c>stale-if-error</c>, or the request's own <c>max-stale</c>.</description></item>
///   <item><term><see cref="Revalidated"/></term><description>Served from cache after the origin confirmed it unchanged with <c>304 Not Modified</c>.</description></item>
///   <item><term><see cref="Coalesced"/></term><description>Shared another concurrent caller's in-flight origin call instead of issuing its own.</description></item>
///   <item><term><see cref="Miss"/></term><description>Fetched from the origin — no usable cache entry, or the entry needed a full refetch.</description></item>
/// </list>
/// <para>
/// The header is absent when the caching layer didn't participate in serving the response: requests it
/// doesn't handle (unsafe methods, non-cacheable requests, <see cref="Caching.CacheRequestPolicy.BypassCache"/>),
/// or pipelines without the caching handler — with the exception of <see cref="Coalesced"/>, which the
/// coalescer sets on its own, caching layer or not. It is set on the response handed back to the caller
/// and never persisted: stored cache entries strip it, so a replayed hit always reports its own status.
/// </para>
/// </remarks>
public static class StampedeCacheStatus
{
    /// <summary>Name of the synthetic response header: <c>X-Stampede-Cache</c>.</summary>
    public const string HeaderName = "X-Stampede-Cache";

    /// <summary>Served from a fresh cache entry without contacting the origin.</summary>
    public const string Hit = "HIT";

    /// <summary>Fetched from the origin — no usable cache entry.</summary>
    public const string Miss = "MISS";

    /// <summary>Served from an expired entry under stale-while-revalidate, stale-if-error, or the request's <c>max-stale</c>.</summary>
    public const string Stale = "STALE";

    /// <summary>Served from cache after a conditional revalidation the origin answered with <c>304 Not Modified</c>.</summary>
    public const string Revalidated = "REVALIDATED";

    /// <summary>Shared a concurrent caller's in-flight origin call instead of issuing an independent one.</summary>
    public const string Coalesced = "COALESCED";

    /// <summary>
    /// Returns the <c>X-Stampede-Cache</c> value carried by <paramref name="response"/>, or
    /// <see langword="null"/> when the header is absent.
    /// </summary>
    public static string? GetStatus(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Headers.NonValidated.TryGetValues(HeaderName, out System.Net.Http.Headers.HeaderStringValues values)
            ? values.ToString()
            : null;
    }

    /// <summary>Sets the header to <paramref name="status"/>, replacing any existing value.</summary>
    internal static void Set(HttpResponseMessage response, string status)
    {
        _ = response.Headers.Remove(HeaderName);
        _ = response.Headers.TryAddWithoutValidation(HeaderName, status);
    }

    /// <summary>
    /// Marks the response as an origin fetch (<see cref="Miss"/>), unless the coalescer already marked it
    /// <see cref="Coalesced"/> — the more specific of the two. Any other pre-existing value (an origin
    /// echoing the header name back) is replaced, so the header always reflects this pipeline's outcome.
    /// </summary>
    internal static void MarkMiss(HttpResponseMessage response)
    {
        if (!string.Equals(GetStatus(response), Coalesced, StringComparison.Ordinal))
        {
            Set(response, Miss);
        }
    }
}
