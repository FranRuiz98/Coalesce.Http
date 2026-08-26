using Stampede.Http.Internal;
using System.Buffers;
using System.Security.Cryptography;

namespace Stampede.Http.Coalescing;

internal readonly record struct RequestKey(string Method, string Url, string HeadersKey = "")
{
    /// <summary>
    /// Conditional request headers (RFC 9110 §13) that change the meaning of a response — an
    /// <c>If-None-Match</c> revalidation may yield a bodyless <c>304</c> that a non-conditional caller cannot
    /// interpret. These are always folded into the coalescing key so requests with different (or absent)
    /// validators are never collapsed into one another, while identical revalidations still coalesce.
    /// </summary>
    private static readonly string[] ConditionalHeaderNames =
        ["If-None-Match", "If-Modified-Since", "If-Match", "If-Unmodified-Since", "If-Range"];

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
    /// in the key so requests with different header values are coalesced independently. Any conditional
    /// request headers present (<c>If-None-Match</c>, <c>If-Modified-Since</c>, etc.) are always folded in,
    /// so a conditional revalidation is never coalesced with a non-conditional request for the same URL.
    /// </summary>
    /// <param name="request">The HTTP request to key.</param>
    /// <param name="keyHeaders">
    /// Additional header field names to incorporate into the key. When <see langword="null"/> or empty and no
    /// conditional headers are present, the key falls back to method + URL only.
    /// </param>
    public static RequestKey Create(HttpRequestMessage request, IReadOnlyList<string>? keyHeaders)
    {
        bool hasKeyHeaders = keyHeaders is not null && keyHeaders.Count > 0;
        bool hasConditional = HasConditionalHeaders(request);
        string? authHash = CredentialHash.OfAuthorization(request.Headers.Authorization);

        if (!hasKeyHeaders && !hasConditional && authHash is null)
        {
            return Create(request);
        }

        IReadOnlyList<string> effectiveHeaders = hasConditional
            ? MergeConditionalHeaders(keyHeaders, request)
            : keyHeaders ?? [];

        string headersKey = BuildHeadersKey(request, effectiveHeaders);

        // Authorization is always folded in as a hash — independent of CacheOptions.AuthorizationCaching,
        // which governs the caching layer, not this one. Two callers presenting different (or no)
        // credentials for the same URL must never be coalesced into a single shared origin response, even
        // when caching itself is off (AddCoalescingOnly) or set to never cache authorized responses. The
        // hash, not the raw value, keeps this out of the debug logs that key.ToString() feeds.
        if (authHash is not null)
        {
            headersKey = string.Concat(headersKey, "auth=", authHash, ";");
        }

        return new RequestKey(request.Method.Method, request.RequestUri!.AbsoluteUri, headersKey);
    }

    /// <summary>
    /// Creates a key for a method other than <c>GET</c>/<c>HEAD</c> matched by
    /// <see cref="Options.CoalescerOptions.ShouldCoalesce"/>, additionally discriminating by the request
    /// body's content — two requests to the same URL are the same coalesceable operation only if their
    /// bodies are identical too.
    /// </summary>
    /// <param name="request">The HTTP request to key. Its content, if any, is buffered as a side effect.</param>
    /// <param name="keyHeaders">Same as in <see cref="Create(HttpRequestMessage, IReadOnlyList{string})"/>.</param>
    /// <param name="maxBodyBytes">
    /// The most that will be buffered to hash the body. A larger (or unknown-and-overflowing) body is not
    /// an error here: it is the caller's signal to execute that request independently instead.
    /// </param>
    /// <param name="ct">Cancels buffering the body. Never propagated to the eventual origin call.</param>
    /// <returns>
    /// The key, or <see langword="null"/> when the request carries a body larger than
    /// <paramref name="maxBodyBytes"/> — the caller should execute that request without coalescing.
    /// </returns>
    public static async Task<RequestKey?> CreateWithBodyAsync(
        HttpRequestMessage request,
        IReadOnlyList<string>? keyHeaders,
        long maxBodyBytes,
        CancellationToken ct)
    {
        RequestKey baseKey = Create(request, keyHeaders);

        if (request.Content is null)
        {
            return baseKey;
        }

        long? declaredLength = request.Content.Headers.ContentLength;
        if (declaredLength > maxBodyBytes)
        {
            return null;
        }

        byte[] body;
        try
        {
            // Buffering here is a deliberate side effect beyond hashing: whatever sends this request next —
            // the coalescer's factory, and any Polly retry/hedging layered outside it — reads from an
            // already-materialized buffer instead of a live stream, making the body replayable the same way
            // a coalesced GET's response already is.
#if NET9_0_OR_GREATER
            await request.Content.LoadIntoBufferAsync(maxBodyBytes, ct).ConfigureAwait(false);
#else
            // No CancellationToken overload of LoadIntoBufferAsync(long) on net8.0 — acceptable given this
            // step is bounded by maxBodyBytes and expected to be fast; ReadAsByteArrayAsync below still
            // observes ct.
            await request.Content.LoadIntoBufferAsync(maxBodyBytes).ConfigureAwait(false);
#endif
            body = await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // Thrown by LoadIntoBufferAsync when a body with no declared Content-Length turns out to exceed
            // maxBodyBytes once actually read.
            return null;
        }

        string bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        string headersKey = string.Concat(baseKey.HeadersKey, "body=", bodyHash, ";");

        return new RequestKey(baseKey.Method, baseKey.Url, headersKey);
    }

    /// <summary>Returns <see langword="true"/> when the request carries any conditional header (RFC 9110 §13).</summary>
    private static bool HasConditionalHeaders(HttpRequestMessage request)
    {
        foreach (string name in ConditionalHeaderNames)
        {
            if (request.Headers.Contains(name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the union of the configured <paramref name="keyHeaders"/> and any conditional headers present on
    /// the request, de-duplicated case-insensitively.
    /// </summary>
    private static List<string> MergeConditionalHeaders(IReadOnlyList<string>? keyHeaders, HttpRequestMessage request)
    {
        List<string> merged = keyHeaders is null ? new(ConditionalHeaderNames.Length) : [.. keyHeaders];

        foreach (string name in ConditionalHeaderNames)
        {
            if (request.Headers.Contains(name) && !ContainsIgnoreCase(merged, name))
            {
                merged.Add(name);
            }
        }

        return merged;
    }

    private static bool ContainsIgnoreCase(List<string> names, string value)
    {
        foreach (string name in names)
        {
            if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
