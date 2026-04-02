namespace Coalesce.Http.Caching;

/// <summary>
/// Default <see cref="ICacheKeyBuilder"/> implementation that produces keys in the form <c>METHOD:absoluteUri</c>.
/// </summary>
/// <remarks>
/// When <paramref name="normalizeQueryParameters"/> is <see langword="true"/>, query parameters are sorted
/// alphabetically before the key is built so that <c>?a=1&amp;b=2</c> and <c>?b=2&amp;a=1</c> map to the
/// same cache entry.
/// </remarks>
public sealed class DefaultCacheKeyBuilder(bool normalizeQueryParameters = false) : ICacheKeyBuilder
{
    /// <inheritdoc/>
    public string Build(HttpRequestMessage request)
    {
        string method = request.Method.Method;
        string uri = normalizeQueryParameters
            ? NormalizeUri(request.RequestUri)
            : (request.RequestUri?.AbsoluteUri ?? string.Empty);

        return string.Create(method.Length + 1 + uri.Length, (method, uri), static (span, state) =>
        {
            state.method.AsSpan().CopyTo(span);
            span[state.method.Length] = ':';
            state.uri.AsSpan().CopyTo(span[(state.method.Length + 1)..]);
        });
    }

    /// <summary>
    /// Returns the absolute URI with query parameters sorted alphabetically by their raw string value.
    /// URIs without a query string are returned unchanged.
    /// </summary>
    private static string NormalizeUri(Uri? uri)
    {
        if (uri is null)
        {
            return string.Empty;
        }

        string query = uri.Query;
        if (query.Length <= 1)
        {
            // No query string or just "?"
            return uri.AbsoluteUri;
        }

        // Split on '&', sort, reassemble — avoids regex and LINQ allocations
        string[] pairs = query.TrimStart('?').Split('&');
        Array.Sort(pairs, StringComparer.Ordinal);

        return string.Concat(
            uri.GetLeftPart(UriPartial.Path),
            "?",
            string.Join('&', pairs));
    }
}
