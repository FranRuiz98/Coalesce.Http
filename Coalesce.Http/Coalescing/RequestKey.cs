namespace Coalesce.Http.Coalescing;

internal readonly record struct RequestKey(string Method, string Url, string HeadersKey = "")
{
    public override string ToString()
    {
        return HeadersKey.Length == 0
            ? $"{Method} {Url}"
            : $"{Method} {Url} [{HeadersKey}]";
    }

    /// <summary>Creates a key from the request using only method + URL (no header discrimination).</summary>
    public static RequestKey Create(HttpRequestMessage request)
    {
        return new RequestKey(request.Method.Method, request.RequestUri!.AbsoluteUri);
    }

    /// <summary>
    /// Creates a key from the request, optionally including the values of specific header fields
    /// in the key so requests with different header values are coalesced independently.
    /// </summary>
    /// <param name="request">The HTTP request to key.</param>
    /// <param name="keyHeaders">
    /// Header field names to incorporate into the key. When <see langword="null"/> or empty the
    /// key falls back to method + URL only.
    /// </param>
    public static RequestKey Create(HttpRequestMessage request, IReadOnlyList<string>? keyHeaders)
    {
        if (keyHeaders is null || keyHeaders.Count == 0)
        {
            return Create(request);
        }

        string headersKey = BuildHeadersKey(request, keyHeaders);
        return new RequestKey(request.Method.Method, request.RequestUri!.AbsoluteUri, headersKey);
    }

    /// <summary>
    /// Builds a deterministic string from the listed header values.
    /// Header names are sorted alphabetically and matched case-insensitively.
    /// Format: <c>name1=v1,v2;name2=v3;</c>
    /// </summary>
    private static string BuildHeadersKey(HttpRequestMessage request, IReadOnlyList<string> headers)
    {
        // Sort names so that ["B","A"] and ["A","B"] produce identical keys
        string[] sorted = [.. headers.Order(StringComparer.OrdinalIgnoreCase)];

        System.Text.StringBuilder sb = new();
        foreach (string name in sorted)
        {
            sb.Append(name.ToLowerInvariant());
            sb.Append('=');
            if (request.Headers.TryGetValues(name, out IEnumerable<string>? values))
            {
                sb.AppendJoin(',', values);
            }
            sb.Append(';');
        }

        return sb.ToString();
    }
}
