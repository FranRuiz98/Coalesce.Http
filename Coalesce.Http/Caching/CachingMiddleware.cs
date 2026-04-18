using Coalesce.Http.Metrics;
using Coalesce.Http.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace Coalesce.Http.Caching;

internal sealed partial class CachingMiddleware(ICacheStore cache,
                                        ICacheKeyBuilder keyBuilder,
                                        IOptionsMonitor<CacheOptions> optionsMonitor,
                                        string clientName,
                                        CoalesceHttpMetrics? metrics = null,
                                        ILogger<CachingMiddleware>? logger = null,
                                        TimeProvider? timeProvider = null) : DelegatingHandler
{
    private static readonly string[] _notModifiedHeaders = ["ETag", "Cache-Control", "Content-Location", "Date", "Expires", "Vary"];

    private readonly ILogger logger = logger ?? NullLogger<CachingMiddleware>.Instance;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Task> _backgroundRevalidations = new(StringComparer.Ordinal);

    private CacheOptions Options => optionsMonitor.Get(clientName);

    /// <summary>
    /// Convenience constructor for testing — wraps a static options instance.
    /// </summary>
    internal CachingMiddleware(ICacheStore cache, ICacheKeyBuilder keyBuilder, CacheOptions options,
        CoalesceHttpMetrics? metrics = null, ILogger<CachingMiddleware>? logger = null, TimeProvider? timeProvider = null)
        : this(cache, keyBuilder, new StaticOptionsMonitor<CacheOptions>(options), string.Empty, metrics, logger, timeProvider) { }

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
    /// <remarks>
    /// Cacheable status codes (RFC 9111 §3.2):
    /// <list type="bullet">
    ///   <item><description>200 OK — always cached (subject to no-store/private guards).</description></item>
    ///   <item><description>301 Moved Permanently — cached heuristically; uses max-age/Expires or DefaultTtl.</description></item>
    ///   <item><description>404 Not Found / 405 Method Not Allowed / 410 Gone / 414 URI Too Long — only cached when an
    ///   explicit max-age or Expires directive is present (no heuristic fallback).</description></item>
    /// </list>
    /// </remarks>
    /// <param name="response">The HTTP response message to evaluate for cacheability. Must not be null.</param>
    /// <returns>true if the response is cacheable; otherwise, false.</returns>
    private static bool IsResponseCacheable(HttpResponseMessage response)
    {
        CacheControlHeaderValue? cacheControl = response.Headers.CacheControl;

        // §5.2.2.5 — no-store: must not cache regardless of status code
        if (cacheControl?.NoStore == true)
        {
            return false;
        }

        // §5.2.2.7 — private: must not store in a shared cache
        if (cacheControl?.Private == true)
        {
            return false;
        }

        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                // 200 is always eligible (guards above already applied)
                return true;

            case HttpStatusCode.MovedPermanently:
                // 301: heuristically cacheable — max-age/Expires/DefaultTtl all valid (RFC 9111 §3.2)
                return true;

            case HttpStatusCode.NotFound:
            case HttpStatusCode.MethodNotAllowed:
            case HttpStatusCode.Gone:
            case HttpStatusCode.RequestUriTooLong:
                // 404/405/410/414: only cache when an explicit freshness directive is present
                // (no heuristic fallback — caching indefinite errors is dangerous)
                return cacheControl?.MaxAge is not null
                    || cacheControl?.SharedMaxAge is not null
                    || response.Content?.Headers.Expires is not null;

            default:
                return false;
        }
    }

    /// <summary>
    /// Creates an HTTP response message based on the specified cache entry.
    /// </summary>
    /// <param name="entry">The cache entry containing the status code, response body, and headers to be used for constructing the HTTP
    /// response.</param>
    /// <returns>An instance of HttpResponseMessage populated with the status code, body, and headers from the provided cache
    /// entry.</returns>
    private HttpResponseMessage CreateResponse(CacheEntry entry)
    {
        HttpResponseMessage response = new((HttpStatusCode)entry.StatusCode)
        {
            Content = new ByteArrayContent(entry.Body)
        };

        foreach (KeyValuePair<string, string[]> header in entry.Headers)
        {
            if (!response.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        // §5.1 — Age: elapsed seconds since the response was stored
        long ageSeconds = Math.Max(0L, (long)(_timeProvider.GetUtcNow() - entry.StoredAt).TotalSeconds);
        response.Headers.Age = new TimeSpan(ageSeconds * TimeSpan.TicksPerSecond);

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

        // Capture Last-Modified before replacing Content, since ByteArrayContent has no content headers.
        DateTimeOffset? capturedLastModified = response.Content.Headers.LastModified;

        // Capture all content headers before replacing Content so they survive the swap.
        List<KeyValuePair<string, IEnumerable<string>>> contentHeaders = [.. response.Content.Headers];

        byte[] body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        response.Content = new ByteArrayContent(body);

        // Restore original content headers (Content-Type, Content-Encoding, etc.)
        foreach (KeyValuePair<string, IEnumerable<string>> header in contentHeaders)
        {
            response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (body.Length > Options.MaxBodySizeBytes)
        {
            return;
        }

        CacheControlHeaderValue? cc = response.Headers.CacheControl;
        DateTimeOffset now = _timeProvider.GetUtcNow();

        // §5.2.2.4 — no-cache: store but mark as immediately stale to force revalidation on every use
        DateTimeOffset expiresAt = cc?.NoCache == true
            ? now
            : FreshnessCalculator.ComputeExpiresAt(response, Options, _timeProvider);

        // §4.1 — Vary: capture field names and the corresponding request header values
        string[] varyFields = ExtractVaryFields(response);
        IReadOnlyDictionary<string, string[]> varyValues = CaptureVaryValues(request, varyFields);

        FreshnessCalculator.ExtractStaleExtensions(response, Options, out long staleIfError, out long staleWhileRevalidate);

        CacheEntry entry = new()
        {
            StatusCode = (int)response.StatusCode,
            Body = body,
            Headers = ExtractHeaders(response),
            StoredAt = now,
            ETag = response.Headers.ETag?.Tag,
            LastModified = capturedLastModified,
            VaryFields = varyFields,
            VaryValues = varyValues,
            ExpiresAt = expiresAt,
            StaleIfErrorSeconds = staleIfError,
            StaleWhileRevalidateSeconds = staleWhileRevalidate,
            MustRevalidate = cc?.MustRevalidate == true || cc?.ProxyRevalidate == true,
            Immutable = IsImmutableEntry(cc)
        };

        await cache.SetAsync(key, entry, ct).ConfigureAwait(false);
    }

    private static string[] ExtractVaryFields(HttpResponseMessage response)
    {
        return response.Headers.Vary.Count == 0 ? [] : [.. response.Headers.Vary];
    }

    private static bool IsImmutableEntry(CacheControlHeaderValue? cc)
    {
        if (cc is null)
        {
            return false;
        }

        foreach (NameValueHeaderValue ext in cc.Extensions)
        {
            if (ext.Name == "immutable")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether the client's conditional GET can be satisfied directly from the cached entry
    /// and, if so, returns a <c>304 Not Modified</c> response (RFC 9111 §4.3.2 / RFC 9110 §13.1).
    /// Returns <see langword="null"/> when the condition is not met or no validator is present.
    /// </summary>
    /// <remarks>
    /// <c>If-None-Match</c> takes precedence over <c>If-Modified-Since</c> (RFC 9110 §13.1).
    /// The 304 includes the headers mandated by RFC 9110 §15.4.5 (ETag, Cache-Control,
    /// Content-Location, Date, Expires, Vary) and the <c>Age</c> header.
    /// </remarks>
    private HttpResponseMessage? TryCreateNotModified(HttpRequestMessage request, CacheEntry entry)
    {
        bool hasIfNoneMatch = request.Headers.IfNoneMatch.Count > 0;
        bool hasIfModifiedSince = request.Headers.IfModifiedSince.HasValue;

        if (!hasIfNoneMatch && !hasIfModifiedSince)
        {
            return null;
        }

        // RFC 9110 §13.1 — If-None-Match takes precedence over If-Modified-Since
        if (hasIfNoneMatch)
        {
            if (entry.ETag is null)
            {
                return null;
            }

            bool matched = false;
            foreach (EntityTagHeaderValue tag in request.Headers.IfNoneMatch)
            {
                // Wildcard "*" matches any ETag
                if (tag.Tag == "*" || string.Equals(tag.Tag, entry.ETag, StringComparison.Ordinal))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                return null;
            }
        }
        else
        {
            // If-Modified-Since: last modified must be at or before the client's date
            if (entry.LastModified is null || entry.LastModified > request.Headers.IfModifiedSince!.Value)
            {
                return null;
            }
        }

        // Build the 304 response with required headers (RFC 9110 §15.4.5)
        HttpResponseMessage notModified = new(HttpStatusCode.NotModified);

        foreach (string headerName in _notModifiedHeaders)
        {
            if (entry.Headers.TryGetValue(headerName, out string[]? values))
            {
                _ = notModified.Headers.TryAddWithoutValidation(headerName, values);
            }
        }

        long ageSeconds = Math.Max(0L, (long)(_timeProvider.GetUtcNow() - entry.StoredAt).TotalSeconds);
        notModified.Headers.Age = new TimeSpan(ageSeconds * TimeSpan.TicksPerSecond);

        return notModified;
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
        // Per-request policy: bypass the cache entirely (no read, no write, no invalidation)
        if (request.Options.TryGetValue(CacheRequestPolicy.BypassCache, out bool bypass) && bypass)
        {
            return await base.SendAsync(request, ct).ConfigureAwait(false);
        }

        // RFC 9110 §9.3.2 — HEAD requests are served from the GET cache entry when possible
        if (request.Method == HttpMethod.Head)
        {
            return await HandleHeadAsync(request, ct).ConfigureAwait(false);
        }

        if (!IsRequestCacheable(request))
        {
            HttpResponseMessage unsafeResponse = await base.SendAsync(request, ct).ConfigureAwait(false);

            // RFC 9111 §4.4 — a successful response to an unsafe method invalidates
            // the cached GET entry for the effective request URI (and Location / Content-Location).
            if (IsUnsafeMethod(request.Method) && IsNonErrorResponse(unsafeResponse))
            {
                await InvalidateForUnsafeMethod(request, unsafeResponse, ct).ConfigureAwait(false);
            }

            return unsafeResponse;
        }

        string key = keyBuilder.Build(request);

        CacheEntry? entry = await cache.GetAsync(key, ct).ConfigureAwait(false);

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

        bool requestNoCache = request.Headers.CacheControl?.NoCache == true
            || (request.Options.TryGetValue(CacheRequestPolicy.ForceRevalidate, out bool forceRevalidate) && forceRevalidate);

        // Fresh cache hit — skip if client demands revalidation (§5.2.1.4), unless entry is immutable (RFC 8246)
        if (entry is not null && !entry.IsExpired(_timeProvider) && (!requestNoCache || entry.Immutable))
        {
            metrics?.RecordCacheHit();
            LogCacheHit(key);

            // RFC 9111 §4.3.2 — if the client sent a conditional request whose validator
            // matches the stored entry, return 304 directly without contacting the origin.
            HttpResponseMessage? notModified = TryCreateNotModified(request, entry);
            if (notModified is not null)
            {
                return notModified;
            }

            return CreateResponse(entry);
        }

        // RFC 5861 §3 — stale-while-revalidate: serve stale immediately, revalidate in background
        if (entry is not null && !requestNoCache && CanServeStaleWhileRevalidate(entry))
        {
            metrics?.RecordStaleWhileRevalidateServed();
            LogStaleWhileRevalidate(key);
            ScheduleBackgroundRevalidation(key, entry, request);
            return CreateResponse(entry);
        }

        // Stale entry (or no-cache demand) with a validator → conditional revalidation
        if (entry is not null && (entry.ETag is not null || entry.LastModified is not null))
        {
            // RFC 9111 §5.2.1.7 — only-if-cached: must not contact origin; return 504
            if (request.Headers.CacheControl?.OnlyIfCached == true)
            {
                return new HttpResponseMessage(HttpStatusCode.GatewayTimeout);
            }

            metrics?.RecordRevalidation();
            LogRevalidation(key);
            return await RevalidateAsync(key, entry, request, ct).ConfigureAwait(false);
        }

        // Cache miss (or stale without validator) — full request
        // RFC 9111 §5.2.1.7 — only-if-cached: no usable entry, return 504 immediately
        if (request.Headers.CacheControl?.OnlyIfCached == true)
        {
            return new HttpResponseMessage(HttpStatusCode.GatewayTimeout);
        }

        metrics?.RecordCacheMiss();
        LogCacheMiss(key);

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch when (CanServeStaleOnError(entry))
        {
            metrics?.RecordStaleErrorServed();
            LogStaleIfErrorServed(key);
            return CreateResponse(entry!);
        }

        // RFC 5861 §4 — stale-if-error: serve stale on 5xx if within the error window
        if (entry is not null && (int)response.StatusCode >= 500 && CanServeStaleOnError(entry))
        {
            response.Dispose();
            metrics?.RecordStaleErrorServed();
            LogStaleIfErrorServed(key);
            return CreateResponse(entry);
        }

        bool noStore = request.Options.TryGetValue(CacheRequestPolicy.NoStore, out bool ns) && ns;

        if (!noStore && IsResponseCacheable(response))
        {
            LogCacheStore(key);
            await StoreAsync(key, request, response, ct).ConfigureAwait(false);
        }

        return response;
    }

    /// <summary>Returns true when the stored entry carries <c>Vary: *</c> (§4.1 — must never serve from cache).</summary>
    private static bool IsVaryStar(CacheEntry entry)
    {
        return entry.VaryFields.Length == 1 && entry.VaryFields[0] == "*";
    }

    /// <summary>
    /// Serves a HEAD request from the GET cache when possible (RFC 9110 §9.3.2).
    /// </summary>
    /// <remarks>
    /// The cache is keyed on GET requests, so HEAD looks up the GET entry for the same URI.
    /// A fresh hit returns all stored headers with an empty body (HEAD semantics).
    /// A stale entry with a validator triggers a conditional HEAD revalidation; a 304 refreshes
    /// the GET entry TTL and the cached headers are returned. Any other response is forwarded as-is.
    /// </remarks>
    private async Task<HttpResponseMessage> HandleHeadAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // RFC 9110 §9.3.2 — use the GET cache key for HEAD requests
        string getKey = BuildGetKey(request.RequestUri);

        CacheEntry? entry = await cache.GetAsync(getKey, ct).ConfigureAwait(false);

        if (entry is not null && IsVaryStar(entry))
        {
            entry = null;
        }

        if (entry is not null && !VaryMatches(entry, request))
        {
            entry = null;
        }

        bool requestNoCache = request.Headers.CacheControl?.NoCache == true
            || (request.Options.TryGetValue(CacheRequestPolicy.ForceRevalidate, out bool force) && force);

        // Fresh GET entry — serve headers with empty body; immutable entries ignore no-cache (RFC 8246)
        if (entry is not null && !entry.IsExpired(_timeProvider) && (!requestNoCache || entry.Immutable))
        {
            metrics?.RecordCacheHit(HttpMethod.Head);
            LogCacheHit(getKey);
            HttpResponseMessage headHit = CreateResponse(entry);
            headHit.Content = new ByteArrayContent([]);
            return headHit;
        }

        // Stale entry with a validator — conditional HEAD revalidation
        if (entry is not null && (entry.ETag is not null || entry.LastModified is not null))
        {
            metrics?.RecordRevalidation(HttpMethod.Head);
            LogRevalidation(getKey);

            if (entry.ETag is not null)
            {
                _ = request.Headers.Remove("If-None-Match");
                _ = request.Headers.TryAddWithoutValidation("If-None-Match", entry.ETag);
            }
            else if (entry.LastModified is DateTimeOffset lastModified)
            {
                request.Headers.IfModifiedSince = lastModified;
            }

            HttpResponseMessage revalResponse = await base.SendAsync(request, ct).ConfigureAwait(false);

            if (revalResponse.StatusCode == HttpStatusCode.NotModified)
            {
                CacheEntry refreshed = entry with
                {
                    ExpiresAt = FreshnessCalculator.ComputeExpiresAt(revalResponse, Options, _timeProvider),
                    StaleIfErrorSeconds = FreshnessCalculator.ExtractStaleIfError(revalResponse, Options),
                    StaleWhileRevalidateSeconds = FreshnessCalculator.ExtractStaleWhileRevalidate(revalResponse, Options),
                    MustRevalidate = revalResponse.Headers.CacheControl?.MustRevalidate == true || revalResponse.Headers.CacheControl?.ProxyRevalidate == true
                };
                await cache.SetAsync(getKey, refreshed, ct).ConfigureAwait(false);
                metrics?.RecordCacheHit(HttpMethod.Head);
                HttpResponseMessage headRefreshed = CreateResponse(refreshed);
                headRefreshed.Content = new ByteArrayContent([]);
                return headRefreshed;
            }

            return revalResponse;
        }

        // Miss or stale without validator — forward HEAD to origin
        return await base.SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that all <c>Vary</c> field values in the current request match those captured when the entry was stored (§4.1).
    /// </summary>
    private static bool VaryMatches(CacheEntry entry, HttpRequestMessage request)
    {
        foreach (string field in entry.VaryFields)
        {
            string[] stored = entry.VaryValues.TryGetValue(field, out string[]? v) ? v : [];

            if (!request.Headers.TryGetValues(field, out IEnumerable<string>? currentValues))
            {
                // No current header values; stored must also be empty to match
                if (stored.Length != 0)
                {
                    return false;
                }

                continue;
            }

            // Compare element-by-element without allocating an intermediate array
            using IEnumerator<string> enumerator = currentValues.GetEnumerator();
            int index = 0;
            while (enumerator.MoveNext())
            {
                if (index >= stored.Length ||
                    !string.Equals(stored[index], enumerator.Current, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                index++;
            }

            if (index != stored.Length)
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
            _ = request.Headers.Remove("If-None-Match");
            _ = request.Headers.TryAddWithoutValidation("If-None-Match", entry.ETag);
        }
        else if (entry.LastModified is DateTimeOffset lastModified)
        {
            request.Headers.IfModifiedSince = lastModified;
        }

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, ct).ConfigureAwait(false);
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
                ExpiresAt = FreshnessCalculator.ComputeExpiresAt(response, Options, _timeProvider),
                StaleIfErrorSeconds = FreshnessCalculator.ExtractStaleIfError(response, Options),
                StaleWhileRevalidateSeconds = FreshnessCalculator.ExtractStaleWhileRevalidate(response, Options),
                MustRevalidate = response.Headers.CacheControl?.MustRevalidate == true || response.Headers.CacheControl?.ProxyRevalidate == true
            };
            await cache.SetAsync(key, refreshed, ct).ConfigureAwait(false);
            metrics?.RecordCacheHit();
            return CreateResponse(refreshed);
        }

        // Per-request NoStore: allow 304 TTL refresh (above) but block storing a new response
        bool noStore = request.Options.TryGetValue(CacheRequestPolicy.NoStore, out bool ns) && ns;

        if (!noStore && IsResponseCacheable(response))
        {
            await StoreAsync(key, request, response, ct).ConfigureAwait(false);
        }

        return response;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the given entry has a positive stale-if-error window
    /// that has not yet expired (RFC 5861 §4).
    /// </summary>
    private bool CanServeStaleOnError(CacheEntry? entry)
    {
        return entry is not null
            && !entry.MustRevalidate
            && entry.StaleIfErrorSeconds > 0
            && _timeProvider.GetUtcNow() < entry.ExpiresAt + TimeSpan.FromSeconds(entry.StaleIfErrorSeconds);
    }

    /// <summary>
    /// Returns <see langword="true"/> when the given entry is stale but within the
    /// stale-while-revalidate window (RFC 5861 §3).
    /// </summary>
    private bool CanServeStaleWhileRevalidate(CacheEntry entry)
    {
        return !entry.MustRevalidate
            && entry.StaleWhileRevalidateSeconds > 0
            && entry.IsExpired(_timeProvider)
            && _timeProvider.GetUtcNow() < entry.ExpiresAt + TimeSpan.FromSeconds(entry.StaleWhileRevalidateSeconds);
    }

    /// <summary>
    /// Schedules a fire-and-forget background revalidation for the given cache key.
    /// Only one background revalidation per key runs at a time.
    /// </summary>
    private void ScheduleBackgroundRevalidation(string key, CacheEntry entry, HttpRequestMessage originalRequest)
    {
        _ = _backgroundRevalidations.GetOrAdd(key, k =>
            Task.Run(async () =>
            {
                try
                {
                    HttpRequestMessage bgRequest = new(originalRequest.Method, originalRequest.RequestUri);
                    foreach (KeyValuePair<string, IEnumerable<string>> header in originalRequest.Headers)
                    {
                        _ = bgRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }

                    if (entry.ETag is not null)
                    {
                        _ = bgRequest.Headers.Remove("If-None-Match");
                        _ = bgRequest.Headers.TryAddWithoutValidation("If-None-Match", entry.ETag);
                    }
                    else if (entry.LastModified is DateTimeOffset lastModified)
                    {
                        bgRequest.Headers.IfModifiedSince = lastModified;
                    }

                    HttpResponseMessage response = await base.SendAsync(bgRequest, CancellationToken.None).ConfigureAwait(false);

                    if (response.StatusCode == HttpStatusCode.NotModified)
                    {
                        CacheEntry refreshed = entry with
                        {
                            ExpiresAt = FreshnessCalculator.ComputeExpiresAt(response, Options, _timeProvider),
                            StaleIfErrorSeconds = FreshnessCalculator.ExtractStaleIfError(response, Options),
                            StaleWhileRevalidateSeconds = FreshnessCalculator.ExtractStaleWhileRevalidate(response, Options),
                            MustRevalidate = response.Headers.CacheControl?.MustRevalidate == true || response.Headers.CacheControl?.ProxyRevalidate == true
                        };
                        await cache.SetAsync(key, refreshed, CancellationToken.None).ConfigureAwait(false);
                    }
                    else if (IsResponseCacheable(response))
                    {
                        await StoreAsync(key, bgRequest, response, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    LogBackgroundRevalidationFailed(key, ex);
                }
                finally
                {
                    _ = _backgroundRevalidations.TryRemove(key, out _);
                }
            }));
    }

    /// <summary>
    /// Returns <see langword="true" /> for methods that are not safe (RFC 9110 §9.2.1).
    /// Safe methods: GET, HEAD, OPTIONS, TRACE.
    /// </summary>
    private static bool IsUnsafeMethod(HttpMethod method)
    {
        return method != HttpMethod.Get
            && method != HttpMethod.Head
            && method != HttpMethod.Options
            && method != HttpMethod.Trace;
    }

    /// <summary>
    /// Returns <see langword="true" /> when the response status code is non-error (1xx–3xx).
    /// RFC 9111 §4.4 triggers invalidation only on non-error responses.
    /// </summary>
    private static bool IsNonErrorResponse(HttpResponseMessage response)
    {
        return (int)response.StatusCode < 400;
    }

    /// <summary>
    /// Builds the cache key that would be used for a GET request to the given URI.
    /// Used to invalidate the cached GET entry when an unsafe method succeeds (§4.4).
    /// </summary>
    private string BuildGetKey(Uri? uri)
    {
        using HttpRequestMessage synthetic = new(HttpMethod.Get, uri);
        return keyBuilder.Build(synthetic);
    }

    /// <summary>
    /// Invalidates cached entries affected by a successful unsafe method response (RFC 9111 §4.4).
    /// Removes the effective request URI and, if present, the Location and Content-Location URIs.
    /// </summary>
    private async ValueTask InvalidateForUnsafeMethod(HttpRequestMessage request, HttpResponseMessage response, CancellationToken ct)
    {
        // §4.4 MUST — effective request URI
        string effectiveKey = BuildGetKey(request.RequestUri);
        if (await cache.GetAsync(effectiveKey, ct).ConfigureAwait(false) is not null)
        {
            await cache.RemoveAsync(effectiveKey, ct).ConfigureAwait(false);
            metrics?.RecordCacheInvalidation();
            LogCacheInvalidation(effectiveKey, request.Method.Method);
        }

        // §4.4 MAY — Location header
        if (response.Headers.Location is Uri location && location != request.RequestUri)
        {
            string locationKey = BuildGetKey(location);
            if (await cache.GetAsync(locationKey, ct).ConfigureAwait(false) is not null)
            {
                await cache.RemoveAsync(locationKey, ct).ConfigureAwait(false);
                metrics?.RecordCacheInvalidation();
                LogCacheInvalidation(locationKey, request.Method.Method);
            }
        }

        // §4.4 MAY — Content-Location header
        if (response.Content?.Headers.ContentLocation is Uri contentLocation
            && contentLocation != request.RequestUri
            && contentLocation != response.Headers.Location)
        {
            string contentLocationKey = BuildGetKey(contentLocation);
            if (await cache.GetAsync(contentLocationKey, ct).ConfigureAwait(false) is not null)
            {
                await cache.RemoveAsync(contentLocationKey, ct).ConfigureAwait(false);
                metrics?.RecordCacheInvalidation();
                LogCacheInvalidation(contentLocationKey, request.Method.Method);
            }
        }
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache: hit for {CacheKey}")]
    private partial void LogCacheHit(string cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache: miss for {CacheKey}")]
    private partial void LogCacheMiss(string cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache: conditional revalidation for {CacheKey}")]
    private partial void LogRevalidation(string cacheKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cache: serving stale-if-error for {CacheKey}")]
    private partial void LogStaleIfErrorServed(string cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache: storing response for {CacheKey}")]
    private partial void LogCacheStore(string cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache: serving stale-while-revalidate for {CacheKey}")]
    private partial void LogStaleWhileRevalidate(string cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache: background revalidation failed for {CacheKey}")]
    private partial void LogBackgroundRevalidationFailed(string cacheKey, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cache: invalidated {CacheKey} after successful {HttpMethod} request (RFC 9111 §4.4)")]
    private partial void LogCacheInvalidation(string cacheKey, string httpMethod);
}
