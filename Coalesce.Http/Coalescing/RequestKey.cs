using System.Buffers;

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
        int count = headers.Count;

        // Rent a buffer from the pool to sort header names without a heap allocation.
        string[] rented = ArrayPool<string>.Shared.Rent(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                rented[i] = headers[i];
            }

            rented.AsSpan(0, count).Sort(StringComparer.OrdinalIgnoreCase);

            // --- Pass 1: compute exact char length needed ---
            // Format per header: <lowercase-name> '=' [v1 ',' v2 ...] ';'
            int totalLength = 0;
            for (int i = 0; i < count; i++)
            {
                totalLength += rented[i].Length + 2; // name + '=' + ';'
                if (request.Headers.TryGetValues(rented[i], out IEnumerable<string>? vals))
                {
                    bool first = true;
                    foreach (string v in vals)
                    {
                        if (!first) totalLength++; // ','
                        totalLength += v.Length;
                        first = false;
                    }
                }
            }

            // --- Pass 2: write into a char buffer ---
            // stackalloc covers the common case (short header keys) with zero heap allocation.
            // ArrayPool<char> is the fallback for unusually long composite keys.
            const int StackAllocThreshold = 512;
            char[]? charRented = null;
            Span<char> buffer = totalLength <= StackAllocThreshold
                ? stackalloc char[totalLength]
                : (charRented = ArrayPool<char>.Shared.Rent(totalLength)).AsSpan(0, totalLength);

            try
            {
                int pos = 0;
                for (int i = 0; i < count; i++)
                {
                    string name = rented[i];

                    // Lowercase the header name directly into the output buffer — no intermediate allocation.
                    MemoryExtensions.ToLowerInvariant(name.AsSpan(), buffer.Slice(pos, name.Length));
                    pos += name.Length;
                    buffer[pos++] = '=';

                    if (request.Headers.TryGetValues(name, out IEnumerable<string>? values))
                    {
                        bool first = true;
                        foreach (string v in values)
                        {
                            if (!first) buffer[pos++] = ',';
                            v.AsSpan().CopyTo(buffer.Slice(pos, v.Length));
                            pos += v.Length;
                            first = false;
                        }
                    }

                    buffer[pos++] = ';';
                }

                return new string(buffer);
            }
            finally
            {
                if (charRented is not null)
                {
                    ArrayPool<char>.Shared.Return(charRented);
                }
            }
        }
        finally
        {
            ArrayPool<string>.Shared.Return(rented, clearArray: true);
        }
    }
}
