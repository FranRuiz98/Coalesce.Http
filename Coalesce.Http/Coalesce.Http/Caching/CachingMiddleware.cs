using Coalesce.Http.Coalesce.Http.Metrics;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Http.Headers;

namespace Coalesce.Http.Coalesce.Http.Caching;

internal sealed class CachingMiddleware(IMemoryCache cache,
                                        ICacheKeyBuilder keyBuilder,
                                        CacheOptions options,
                                        CoalesceHttpMetrics? metrics = null) : DelegatingHandler
{
    /// <summary>
    /// Determines whether the specified HTTP request is eligible for caching based on its method, headers, and content.
    /// </summary>
    /// <remarks>A request is considered cacheable if it uses the GET method, does not include authorization
    /// headers or content, and does not specify 'no-store' in its Cache-Control header. This method is useful for
    /// deciding whether a response to the request should be stored or reused.</remarks>
    /// <param name="request">The HTTP request message to evaluate for cacheability. Must not be null.</param>
    /// <returns>true if the request can be cached; otherwise, false.</returns>
    private static bool IsRequestCacheable(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Get)
        {
            return false;
        }

        if (request.Headers.Authorization is not null)
        {
            return false;
        }

        if (request.Content is not null)
        {
            return false;
        }

        CacheControlHeaderValue? cacheControl = request.Headers.CacheControl;

        return (cacheControl?.NoStore) != true;
    }

    /// <summary>
    /// Determines whether the specified HTTP response can be cached based on its status code and cache control headers.
    /// </summary>
    /// <remarks>This method checks both the status code and the presence of the 'no-store' directive in the
    /// response's cache control headers. Responses with 'no-store' are considered not cacheable, even if the status
    /// code is OK.</remarks>
    /// <param name="response">The HTTP response message to evaluate for cacheability. Must not be null.</param>
    /// <returns>true if the response has a status code of OK and does not specify 'no-store' in its cache control headers;
    /// otherwise, false.</returns>
    private static bool IsResponseCacheable(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return false;
        }

        CacheControlHeaderValue? cacheControl = response.Headers.CacheControl;

        // §5.2.2.5 — no-store: must not cache
        if (cacheControl?.NoStore == true)
        {
            return false;
        }

        // §5.2.2.7 — private: must not store in a shared cache
        return (cacheControl?.Private) != true;
    }

    /// <summary>
    /// Creates an HTTP response message based on the specified cache entry.
    /// </summary>
    /// <param name="entry">The cache entry containing the status code, response body, and headers to be used for constructing the HTTP
    /// response.</param>
    /// <returns>An instance of HttpResponseMessage populated with the status code, body, and headers from the provided cache
    /// entry.</returns>
    private static HttpResponseMessage CreateResponse(CacheEntry entry)
    {
        HttpResponseMessage response = new((HttpStatusCode)entry.StatusCode)
        {
            Content = new ByteArrayContent(entry.Body)
        };

        foreach (KeyValuePair<string, string[]> header in entry.Headers)
        {
            _ = response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // §5.1 — Age: elapsed seconds since the response was stored
        long ageSeconds = Math.Max(0L, (long)(DateTimeOffset.UtcNow - entry.StoredAt).TotalSeconds);
        response.Headers.Age = TimeSpan.FromSeconds(ageSeconds);

        return response;
    }

    /// <summary>
    /// Stores the specified HTTP response in the cache using the provided key.
    /// </summary>
    /// <param name="key">The cache key under which the response should be stored.</param>
    /// <param name="response">The HTTP response message to be cached.</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task StoreAsync(string key, HttpRequestMessage request, HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content is null)
        {
            return;
        }

        byte[] body = await response.Content.ReadAsByteArrayAsync(ct);

        response.Content = new ByteArrayContent(body);

        if (body.Length > options.MaxBodySizeBytes)
        {
            return;
        }

        CacheControlHeaderValue? cc = response.Headers.CacheControl;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // §5.2.2.4 — no-cache: store but mark as immediately stale to force revalidation on every use
        DateTimeOffset expiresAt = cc?.NoCache == true
            ? now
            : FreshnessCalculator.ComputeExpiresAt(response, options);

        // §4.1 — Vary: capture field names and the corresponding request header values
        string[] varyFields = ExtractVaryFields(response);
        IReadOnlyDictionary<string, string[]> varyValues = CaptureVaryValues(request, varyFields);

        CacheEntry entry = new()
        {
            StatusCode = (int)response.StatusCode,
            Body = body,
            Headers = ExtractHeaders(response),
            StoredAt = now,
            ETag = response.Headers.ETag?.Tag,
            LastModified = response.Content?.Headers.LastModified,
            VaryFields = varyFields,
            VaryValues = varyValues,
            ExpiresAt = expiresAt,
            StaleIfErrorSeconds = FreshnessCalculator.ExtractStaleIfError(response, options)
        };

        _ = cache.Set(key, entry);
    }

    private static string[] ExtractVaryFields(HttpResponseMessage response)
    {
        return !response.Headers.Vary.Any() ? [] : [.. response.Headers.Vary];
    }

    private static Dictionary<string, string[]> CaptureVaryValues(HttpRequestMessage request, string[] varyFields)
    {
        if (varyFields.Length == 0)
        {
            return [];
        }

        Dictionary<string, string[]> values = new(varyFields.Length, StringComparer.OrdinalIgnoreCase);

        foreach (string field in varyFields)
        {
            values[field] = request.Headers.TryGetValues(field, out IEnumerable<string>? headerValues) ? [.. headerValues] : [];
        }

        return values;
    }

    /// <summary>
    /// Sends an HTTP request asynchronously and attempts to serve the response from cache when possible, falling back
    /// to the base handler if caching is not applicable.
    /// </summary>
    /// <remarks>If the request is cacheable and a valid cached entry exists, the response is served from
    /// cache. If the cache entry is stale and contains an ETag, conditional revalidation is performed. Otherwise, the
    /// request is sent to the base handler and the response may be cached if eligible. This method supports conditional
    /// caching and revalidation based on HTTP semantics.</remarks>
    /// <param name="request">The HTTP request message to send. Determines cacheability and is used to build the cache key.</param>
    /// <param name="ct">A cancellation token that can be used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the HTTP response message, which may
    /// be served from cache or obtained from the base handler.</returns>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!IsRequestCacheable(request))
        {
            return await base.SendAsync(request, ct);
        }

        string key = keyBuilder.Build(request);

        _ = cache.TryGetValue(key, out CacheEntry? entry);

        // §4.1 — Vary: * means this response must never be served from cache
        if (entry is not null && IsVaryStar(entry))
        {
            entry = null;
        }

        // §4.1 — Vary field mismatch: treat as a miss
        if (entry is not null && !VaryMatches(entry, request))
        {
            entry = null;
        }

        bool requestNoCache = request.Headers.CacheControl?.NoCache == true;

        // Fresh cache hit — skip if client demands revalidation (§5.2.1.4)
        if (entry is not null && !entry.IsExpired() && !requestNoCache)
        {
            metrics?.RecordCacheHit();
            return CreateResponse(entry);
        }

        // Stale entry (or no-cache demand) with a validator → conditional revalidation
        if (entry is not null && (entry.ETag is not null || entry.LastModified is not null))
        {
            metrics?.RecordRevalidation();
            return await RevalidateAsync(key, entry, request, ct);
        }

        // Cache miss (or stale without validator) — full request
        metrics?.RecordCacheMiss();

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, ct);
        }
        catch when (CanServeStaleOnError(entry))
        {
            metrics?.RecordStaleErrorServed();
            return CreateResponse(entry!);
        }

        // RFC 5861 §4 — stale-if-error: serve stale on 5xx if within the error window
        if (entry is not null && (int)response.StatusCode >= 500 && CanServeStaleOnError(entry))
        {
            response.Dispose();
            metrics?.RecordStaleErrorServed();
            return CreateResponse(entry);
        }

        if (IsResponseCacheable(response))
        {
            await StoreAsync(key, request, response, ct);
        }

        return response;
    }

    /// <summary>Returns true when the stored entry carries <c>Vary: *</c> (§4.1 — must never serve from cache).</summary>
    private static bool IsVaryStar(CacheEntry entry)
    {
        return entry.VaryFields.Length == 1 && entry.VaryFields[0] == "*";
    }

    /// <summary>
    /// Verifies that all <c>Vary</c> field values in the current request match those captured when the entry was stored (§4.1).
    /// </summary>
    private static bool VaryMatches(CacheEntry entry, HttpRequestMessage request)
    {
        foreach (string field in entry.VaryFields)
        {
            string[] stored = entry.VaryValues.TryGetValue(field, out string[]? v) ? v : [];

            string[] current = request.Headers.TryGetValues(field, out IEnumerable<string>? cv)
                ? [.. cv]
                : [];

            if (!stored.SequenceEqual(current, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Revalidates a cached HTTP response using the provided cache entry and request, updating the cache if necessary.
    /// </summary>
    /// <remarks>If the server indicates the resource has not changed, the cache entry is refreshed and a
    /// response is generated from the cache. Otherwise, the cache may be updated with the new response if it is
    /// cacheable.</remarks>
    /// <param name="key">The cache key associated with the entry and request. Cannot be null or empty.</param>
    /// <param name="entry">The cache entry containing metadata such as the ETag and expiration information. Cannot be null.</param>
    /// <param name="request">The HTTP request message used for revalidation. Must include all necessary headers for conditional requests.</param>
    /// <param name="ct">The cancellation token that can be used to cancel the revalidation operation.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the HTTP response message, which may
    /// be a refreshed cached response or a new response from the server.</returns>
    private async Task<HttpResponseMessage> RevalidateAsync(string key, CacheEntry entry, HttpRequestMessage request, CancellationToken ct)
    {
        // §4.3.1 — prefer ETag / If-None-Match; fall back to Last-Modified / If-Modified-Since
        if (entry.ETag is not null)
        {
            // Remove before add: prevents a duplicate value if the same request object
            // reaches RevalidateAsync more than once (e.g. via an outer retry layer).
            request.Headers.Remove("If-None-Match");
            request.Headers.TryAddWithoutValidation("If-None-Match", entry.ETag);
        }
        else if (entry.LastModified is DateTimeOffset lastModified)
        {
            request.Headers.IfModifiedSince = lastModified;
        }

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, ct);
        }
        catch when (CanServeStaleOnError(entry))
        {
            metrics?.RecordStaleErrorServed();
            return CreateResponse(entry);
        }

        // RFC 5861 §4 — stale-if-error: serve stale on 5xx if within the error window
        if ((int)response.StatusCode >= 500 && CanServeStaleOnError(entry))
        {
            response.Dispose();
            metrics?.RecordStaleErrorServed();
            return CreateResponse(entry);
        }

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            CacheEntry refreshed = entry with
            {
                ExpiresAt = FreshnessCalculator.ComputeExpiresAt(response, options),
                StaleIfErrorSeconds = FreshnessCalculator.ExtractStaleIfError(response, options)
            };
            _ = cache.Set(key, refreshed);
            metrics?.RecordCacheHit();
            return CreateResponse(refreshed);
        }

        if (IsResponseCacheable(response))
        {
            await StoreAsync(key, request, response, ct);
        }

        return response;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the given entry has a positive stale-if-error window
    /// that has not yet expired (RFC 5861 §4).
    /// </summary>
    private static bool CanServeStaleOnError(CacheEntry? entry)
    {
        if (entry is null || entry.StaleIfErrorSeconds <= 0)
        {
            return false;
        }

        return DateTimeOffset.UtcNow < entry.ExpiresAt + TimeSpan.FromSeconds(entry.StaleIfErrorSeconds);
    }

    /// <summary>
    /// Extracts all headers from the specified HTTP response, including both response and content headers.
    /// </summary>
    /// <remarks>Header names are compared using a case-insensitive ordinal comparer. Content headers are
    /// included only if the response contains content.</remarks>
    /// <param name="response">The HTTP response message from which headers are to be extracted. Cannot be null.</param>
    /// <returns>A dictionary containing all headers from the response. Each key is a header name, and each value is an array of
    /// header values. If no headers are present, the dictionary will be empty.</returns>
    private static Dictionary<string, string[]> ExtractHeaders(HttpResponseMessage response)
    {
        Dictionary<string, string[]> headers = new(16, StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
        {
            headers[header.Key] = [.. header.Value];
        }

        if (response.Content != null)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Content.Headers)
            {
                headers[header.Key] = [.. header.Value];
            }
        }

        return headers;
    }
}
