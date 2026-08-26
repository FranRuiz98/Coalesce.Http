using Stampede.Http.Internal;
using Stampede.Http.Metrics;
using Stampede.Http.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Stampede.Http.Caching;

internal sealed partial class CachingMiddleware(ICacheStore cache,
                                        ICacheKeyBuilder keyBuilder,
                                        IOptionsMonitor<CacheOptions> optionsMonitor,
                                        string clientName,
                                        BackgroundRevalidationCoordinator backgroundRevalidations,
                                        StampedeHttpMetrics? metrics = null,
                                        ILogger<CachingMiddleware>? logger = null,
                                        TimeProvider? timeProvider = null,
                                        Func<double>? randomSource = null) : DelegatingHandler
{
    private static readonly string[] _notModifiedHeaders = ["ETag", "Cache-Control", "Content-Location", "Date", "Expires", "Vary"];

    private readonly ILogger logger = logger ?? NullLogger<CachingMiddleware>.Instance;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Source of uniform [0, 1) randomness for <see cref="ShouldEarlyRevalidate"/> (XFetch). Injectable so
    /// tests can make the otherwise-probabilistic trigger deterministic, the same role
    /// <see cref="TimeProvider"/> plays for freshness calculations.
    /// </summary>
    private readonly Func<double> _random = randomSource ?? Random.Shared.NextDouble;

    private CacheOptions Options => optionsMonitor.Get(clientName);

    /// <summary>
    /// Convenience constructor for testing — wraps a static options instance and gives this handler its own
    /// background-revalidation scope.
    /// </summary>
    internal CachingMiddleware(ICacheStore cache, ICacheKeyBuilder keyBuilder, CacheOptions options,
        StampedeHttpMetrics? metrics = null, ILogger<CachingMiddleware>? logger = null, TimeProvider? timeProvider = null,
        Func<double>? randomSource = null)
        : this(cache, keyBuilder, new StaticOptionsMonitor<CacheOptions>(options), string.Empty,
               new BackgroundRevalidationCoordinator(), metrics, logger, timeProvider, randomSource) { }

    /// <summary>
    /// Determines whether the specified HTTP request is eligible for caching based on its method, headers, and content.
    /// </summary>
    /// <remarks>
    /// A request is considered cacheable if it uses the GET method, does not include content, and does not
    /// specify <c>no-store</c> in its Cache-Control header. A request carrying an <c>Authorization</c> header
    /// is additionally gated on <see cref="CacheOptions.AuthorizationCaching"/> (default
    /// <see cref="AuthorizationCachingMode.Never"/> — excluded, matching pre-2.4 behavior). See
    /// <see cref="AuthorizationCachingMode"/> for the credential-isolation guarantees that apply once this
    /// is enabled.
    /// </remarks>
    /// <param name="request">The HTTP request message to evaluate for cacheability. Must not be null.</param>
    /// <returns>true if the request can be cached; otherwise, false.</returns>
    private bool IsRequestCacheable(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Get)
        {
            return false;
        }

        if (request.Headers.Authorization is not null && Options.AuthorizationCaching == AuthorizationCachingMode.Never)
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
    /// Checks the client's <c>max-age</c> and <c>min-fresh</c> request directives (RFC 9111 §5.2.1.1,
    /// §5.2.1.3) against a structurally fresh entry. A request can tighten the entry's own freshness
    /// lifetime — asking for a response no older than <c>max-age</c>, or one that will stay fresh for
    /// at least <c>min-fresh</c> longer — even when the entry itself has not expired yet. Neither
    /// directive widens freshness; an entry that has already expired is handled separately (stale-while-
    /// revalidate, <c>max-stale</c>, or conditional revalidation).
    /// </summary>
    /// <remarks>
    /// Deliberately independent of <see cref="CacheEntry.Immutable"/>: RFC 8246 exempts immutable
    /// responses from revalidation prompted by the <em>server's</em> own <c>no-cache</c>/<c>must-revalidate</c>
    /// semantics, but says nothing about a client's explicit recency requirement — a caller asking for
    /// data no older than 5 seconds should not receive a two-day-old immutable entry.
    /// </remarks>
    private bool SatisfiesRequestFreshnessDirectives(CacheEntry entry, HttpRequestMessage request)
    {
        CacheControlHeaderValue? cc = request.Headers.CacheControl;
        if (cc is null)
        {
            return true;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();

        // §5.2.1.1 — max-age: reject a response older than the client's bound, even if still fresh
        // by the entry's own lifetime.
        if (cc.MaxAge is TimeSpan requestMaxAge && (now - entry.StoredAt) > requestMaxAge)
        {
            return false;
        }

        // §5.2.1.3 — min-fresh: reject a response that won't remain fresh long enough into the future.
        if (cc.MinFresh is TimeSpan minFresh && (entry.ExpiresAt - now) < minFresh)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Checks whether the client's <c>max-stale</c> request directive (RFC 9111 §5.2.1.2) permits
    /// serving an already-expired entry as-is, without contacting the origin.
    /// </summary>
    /// <remarks>
    /// <c>max-stale</c> with no value accepts any amount of staleness; <c>max-stale=N</c> accepts up to
    /// <c>N</c> seconds past <see cref="CacheEntry.ExpiresAt"/>. Per §5.2.2.2, a cache MUST NOT honor
    /// this when the stored response carries <c>must-revalidate</c>/<c>proxy-revalidate</c> — those are
    /// the origin's explicit instruction that no staleness is acceptable under any circumstances,
    /// which overrides what any individual client is willing to tolerate.
    /// <para>
    /// This directive can only widen acceptance of an entry the backing store still holds — it cannot
    /// resurrect one already evicted. <see cref="MemoryCacheStore"/> drops an entry immediately once it
    /// has no freshness, no stale-if-error/stale-while-revalidate window, and no validator-driven
    /// revalidation grace left (see <see cref="MemoryCacheStore.ComputeRetention"/>), since at that point
    /// nothing — including this directive — could ever serve or revalidate it again. In practice
    /// <c>max-stale</c> matters most for entries that already carry an <c>ETag</c>/<c>Last-Modified</c>
    /// validator or an origin-configured stale window, which is what keeps them retrievable past
    /// <c>ExpiresAt</c> in the first place.
    /// </para>
    /// </remarks>
    private bool IsWithinRequestMaxStale(CacheEntry entry, HttpRequestMessage request)
    {
        if (entry.MustRevalidate)
        {
            return false;
        }

        CacheControlHeaderValue? cc = request.Headers.CacheControl;
        if (cc?.MaxStale != true)
        {
            return false;
        }

        if (cc.MaxStaleLimit is not TimeSpan limit)
        {
            return true; // max-stale with no value: any staleness is acceptable
        }

        TimeSpan staleness = _timeProvider.GetUtcNow() - entry.ExpiresAt;
        return staleness <= limit;
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
    /// <param name="request">
    /// The request that produced <paramref name="response"/>. Only consulted for its <c>Authorization</c>
    /// header, to apply the RFC 9111 §3.5 permission check when
    /// <see cref="CacheOptions.AuthorizationCaching"/> is <see cref="AuthorizationCachingMode.WhenPermittedByResponse"/>.
    /// </param>
    /// <returns>true if the response is cacheable; otherwise, false.</returns>
    private bool IsResponseCacheable(HttpResponseMessage response, HttpRequestMessage request)
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

        // §3.5 — a request carrying Authorization may only be cached when the response explicitly permits
        // it: public, must-revalidate, or an explicit shared-cache freshness directive (s-maxage). Only
        // enforced in WhenPermittedByResponse; Always skips this and Never never reaches here at all
        // (IsRequestCacheable already excluded the request from the pipeline).
        if (request.Headers.Authorization is not null
            && Options.AuthorizationCaching == AuthorizationCachingMode.WhenPermittedByResponse
            && cacheControl?.Public != true
            && cacheControl?.MustRevalidate != true
            && cacheControl?.SharedMaxAge is null)
        {
            return false;
        }

        return response.StatusCode switch
        {
            HttpStatusCode.OK => true, // 200 is always eligible (guards above already applied)
            HttpStatusCode.MovedPermanently => true, // 301: heuristically cacheable
            HttpStatusCode.NotFound or
            HttpStatusCode.MethodNotAllowed or
            HttpStatusCode.Gone or
            HttpStatusCode.RequestUriTooLong =>
                // 404/405/410/414: only cache when an explicit freshness directive is present
                cacheControl?.MaxAge is not null
                || cacheControl?.SharedMaxAge is not null
                || response.Content?.Headers.Expires is not null,
            _ => false
        };
    }

    /// <summary>
    /// Creates an HTTP response message based on the specified cache entry.
    /// </summary>
    /// <param name="entry">The cache entry containing the status code, response body, and headers to be used for constructing the HTTP
    /// response.</param>
    /// <param name="includeBody">
    /// When <see langword="false"/>, the stored header fields are replayed over an empty body. Used for HEAD, which
    /// repeats the header fields the equivalent GET would have sent — including <c>Content-Type</c> and
    /// <c>Content-Length</c> — but carries no content (RFC 9110 §9.3.2).
    /// </param>
    /// <returns>An instance of HttpResponseMessage populated with the status code, body, and headers from the provided cache
    /// entry.</returns>
    private HttpResponseMessage CreateResponse(CacheEntry entry, bool includeBody = true)
    {
        HttpResponseMessage response = new((HttpStatusCode)entry.StatusCode)
        {
            Content = new ByteArrayContent(includeBody ? entry.Body : [])
        };

        foreach (KeyValuePair<string, string[]> header in entry.Headers)
        {
            if (!response.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        if (!includeBody)
        {
            // RFC 9110 §9.3.2 — the HEAD response reports the content length the equivalent GET would have sent.
            // Set it from the stored body rather than relying on a stored Content-Length header: HttpContentHeaders
            // computes that value lazily and does not enumerate it, so it is often absent from the entry.
            response.Content.Headers.ContentLength = entry.Body.Length;
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
    /// <param name="request">The request that produced the response; its headers are captured for <c>Vary</c> handling.</param>
    /// <param name="response">The HTTP response message to be cached.</param>
    /// <param name="fetchDuration">
    /// Wall-clock time the origin call took, recorded on the entry as
    /// <see cref="CacheEntry.OriginFetchDurationMs"/> for early revalidation (XFetch) to scale by.
    /// </param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task StoreAsync(string key, HttpRequestMessage request, HttpResponseMessage response, TimeSpan fetchDuration, CancellationToken ct)
    {
        if (response.Content is null)
        {
            return;
        }

        long maxBodySizeBytes = Options.MaxBodySizeBytes;

        // Skip oversized responses before touching the body. Buffering one only to discard it would allocate
        // the whole payload — and would also consume a live network stream that the caller still has to read.
        if (response.Content.Headers.ContentLength is long declaredLength && declaredLength > maxBodySizeBytes)
        {
            LogBodyTooLarge(key, declaredLength, maxBodySizeBytes);
            return;
        }

        // Capture Last-Modified before replacing Content, since ByteArrayContent has no content headers.
        DateTimeOffset? capturedLastModified = response.Content.Headers.LastModified;

        byte[] body;

        if (response.Content is BufferedByteArrayContent buffered)
        {
            // Already materialised by the coalescer (or an inner cache layer): reuse the array rather than
            // copying it out and rebuffering into a second ByteArrayContent. Under a stampede every waiter
            // reaches this path, so the saving is one full body copy per coalesced caller.
            body = buffered.Buffer;
        }
        else
        {
            // Capture all content headers before replacing Content so they survive the swap.
            List<KeyValuePair<string, IEnumerable<string>>> contentHeaders = [.. response.Content.Headers];

            body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

            // Reading consumed the original stream, so hand the caller a replayable copy.
            response.Content = new BufferedByteArrayContent(body);

            // Restore original content headers (Content-Type, Content-Encoding, etc.)
            foreach (KeyValuePair<string, IEnumerable<string>> header in contentHeaders)
            {
                response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        // Chunked responses carry no Content-Length, so the limit can only be enforced after the read.
        if (body.Length > maxBodySizeBytes)
        {
            LogBodyTooLarge(key, body.Length, maxBodySizeBytes);
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
            Immutable = IsImmutableEntry(cc),
            OriginFetchDurationMs = Math.Max(0L, (long)fetchDuration.TotalMilliseconds)
        };

        await WriteEntryAsync(key, entry, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Character separating the primary cache key from the Vary secondary key. U+001F (unit separator) is a
    /// control character that cannot appear in a URI or header value, so it never collides with real key content.
    /// </summary>
    private const char VariantKeySeparator = (char)0x1f;

    /// <summary>
    /// Writes a representation to the store (RFC 9111 §4.1). When the response carries a <c>Vary</c> header,
    /// the representation is stored under a secondary (variant) key derived from the request's values for the
    /// Vary fields, and a small <see cref="CacheEntry.IsVaryMarker"/> entry is written at the primary key so
    /// future lookups know which request headers to key on. Non-varying responses are stored at the primary key
    /// directly. <c>Vary: *</c> stores only a marker (the response is never served from cache).
    /// </summary>
    private async ValueTask WriteEntryAsync(string primaryKey, CacheEntry entry, CancellationToken ct)
    {
        if (entry.VaryFields.Length == 0)
        {
            await cache.SetAsync(primaryKey, entry, ct).ConfigureAwait(false);
            return;
        }

        if (IsVaryStar(entry))
        {
            await cache.SetAsync(primaryKey, CreateVaryMarker(entry), ct).ConfigureAwait(false);
            return;
        }

        string variantKey = BuildVariantKey(primaryKey, entry.VaryFields, entry.VaryValues);

        await cache.SetAsync(variantKey, entry, ct).ConfigureAwait(false);
        await cache.SetAsync(primaryKey, CreateVaryMarker(entry), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the stored representation for <paramref name="request"/>, following a Vary marker
    /// (RFC 9111 §4.1) at <paramref name="primaryKey"/> to the matching secondary-key variant when present.
    /// Returns <see langword="null"/> on a miss or when the marker is <c>Vary: *</c>.
    /// </summary>
    private async ValueTask<CacheEntry?> ResolveEntryAsync(string primaryKey, HttpRequestMessage request, CancellationToken ct)
    {
        CacheEntry? entry = await cache.GetAsync(primaryKey, ct).ConfigureAwait(false);

        if (entry is null || !entry.IsVaryMarker)
        {
            return entry;
        }

        // Vary: * — the resource is never served from cache (§4.1).
        if (IsVaryStar(entry))
        {
            return null;
        }

        string variantKey = BuildVariantKey(primaryKey, entry.VaryFields, request);

        return await cache.GetAsync(variantKey, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a Vary secondary cache key from the values <paramref name="request"/> carries for each Vary
    /// field (RFC 9111 §4.1). Used on the read path, where the field names come from the stored marker.
    /// </summary>
    private static string BuildVariantKey(string primaryKey, string[] normalizedFields, HttpRequestMessage request)
    {
        StringBuilder sb = StartVariantKey(primaryKey);

        foreach (string field in normalizedFields)
        {
            sb.Append(VariantKeySeparator).Append(field).Append('=');

            if (request.Headers.TryGetValues(field, out IEnumerable<string>? values))
            {
                AppendValues(sb, values);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds a Vary secondary cache key from the request values captured when the entry was stored
    /// (RFC 9111 §4.1). Used on the write path.
    /// </summary>
    private static string BuildVariantKey(string primaryKey, string[] normalizedFields, IReadOnlyDictionary<string, string[]> varyValues)
    {
        StringBuilder sb = StartVariantKey(primaryKey);

        foreach (string field in normalizedFields)
        {
            sb.Append(VariantKeySeparator).Append(field).Append('=');

            if (varyValues.TryGetValue(field, out string[]? values))
            {
                AppendValues(sb, values);
            }
        }

        return sb.ToString();
    }

    private static StringBuilder StartVariantKey(string primaryKey)
    {
        return new StringBuilder(primaryKey.Length + 32).Append(primaryKey);
    }

    /// <summary>
    /// Appends a comma-separated, lower-cased rendering of <paramref name="values"/> so the key agrees with the
    /// case-insensitive comparison performed by <see cref="VaryMatches"/>.
    /// </summary>
    private static void AppendValues(StringBuilder sb, IEnumerable<string> values)
    {
        bool first = true;

        foreach (string value in values)
        {
            if (!first)
            {
                sb.Append(',');
            }

            AppendLowerInvariant(sb, value);
            first = false;
        }
    }

    /// <summary>
    /// Appends <paramref name="value"/> in lower case without allocating an intermediate string. Header values
    /// are short, so the common case folds through the stack.
    /// </summary>
    private static void AppendLowerInvariant(StringBuilder sb, string value)
    {
        const int StackAllocThreshold = 256;

        if (value.Length > StackAllocThreshold)
        {
            _ = sb.Append(value.ToLowerInvariant());
            return;
        }

        Span<char> buffer = stackalloc char[value.Length];
        int written = MemoryExtensions.ToLowerInvariant(value.AsSpan(), buffer);
        _ = sb.Append(buffer[..written]);
    }

    /// <summary>
    /// Creates a Vary marker for the given representation. The marker carries no body and mirrors the
    /// representation's expiry/stale/validator metadata only so its eviction deadline matches the variant it
    /// points to; it is never returned as a response.
    /// </summary>
    private static CacheEntry CreateVaryMarker(CacheEntry representation) => new()
    {
        StatusCode = representation.StatusCode,
        Body = [],
        Headers = new Dictionary<string, string[]>(),
        ExpiresAt = representation.ExpiresAt,
        StoredAt = representation.StoredAt,
        ETag = representation.ETag,
        LastModified = representation.LastModified,
        VaryFields = representation.VaryFields,
        VaryValues = new Dictionary<string, string[]>(),
        StaleIfErrorSeconds = representation.StaleIfErrorSeconds,
        StaleWhileRevalidateSeconds = representation.StaleWhileRevalidateSeconds,
        IsVaryMarker = true
    };

    /// <summary>
    /// Extracts the <c>Vary</c> field names, normalized once here rather than on every lookup: lower-cased and
    /// sorted, so the secondary key is deterministic regardless of the order or casing the origin used.
    /// </summary>
    /// <remarks>
    /// Field names are matched case-insensitively, so lower-casing loses nothing — and the variant key is
    /// rebuilt on every cache read, which is where copying, sorting and lower-casing the names again would
    /// otherwise be paid. Lower-cased names sort identically under ordinal and case-insensitive comparison.
    /// </remarks>
    private static string[] ExtractVaryFields(HttpResponseMessage response)
    {
        if (response.Headers.Vary.Count == 0)
        {
            return [];
        }

        string[] fields = [.. response.Headers.Vary];

        for (int i = 0; i < fields.Length; i++)
        {
            fields[i] = fields[i].ToLowerInvariant();
        }

        Array.Sort(fields, StringComparer.Ordinal);

        return fields;
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

        // §4.1 — follow a Vary marker to the representation matching this request's Vary values.
        CacheEntry? entry = await ResolveEntryAsync(key, request, ct).ConfigureAwait(false);

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

        // Fresh cache hit — skip if client demands revalidation (§5.2.1.4), unless entry is immutable (RFC 8246).
        // A request's own max-age/min-fresh (§5.2.1.1, §5.2.1.3) can still tighten this even for an
        // otherwise-fresh, even immutable, entry — see SatisfiesRequestFreshnessDirectives.
        if (entry is not null && !entry.IsExpired(_timeProvider) && (!requestNoCache || entry.Immutable)
            && SatisfiesRequestFreshnessDirectives(entry, request))
        {
            metrics?.RecordCacheHit(clientName: clientName);
            LogCacheHit(key);

            // XFetch (opt-in, §CacheOptions.EnableEarlyRevalidation): probabilistically refresh a
            // still-fresh entry ahead of its expiry. Purely a background side effect — the response
            // served below is unaffected either way.
            if (Options.EnableEarlyRevalidation && ShouldEarlyRevalidate(entry))
            {
                metrics?.RecordEarlyRevalidationTriggered(clientName);
                LogEarlyRevalidationTriggered(key);
                ScheduleBackgroundRevalidation(key, entry, request);
            }

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
            metrics?.RecordStaleWhileRevalidateServed(clientName);
            LogStaleWhileRevalidate(key);
            ScheduleBackgroundRevalidation(key, entry, request);
            return CreateResponse(entry);
        }

        // §5.2.1.2 — max-stale: the client accepts an expired entry directly, no origin contact.
        if (entry is not null && !requestNoCache && entry.IsExpired(_timeProvider) && IsWithinRequestMaxStale(entry, request))
        {
            metrics?.RecordCacheHit(clientName: clientName);
            LogMaxStaleServed(key);
            return CreateResponse(entry);
        }

        // Stale entry (or no-cache demand, or an unmet request freshness directive) with a validator
        // → conditional revalidation
        if (entry is not null && (entry.ETag is not null || entry.LastModified is not null))
        {
            // RFC 9111 §5.2.1.7 — only-if-cached: must not contact origin; return 504
            if (request.Headers.CacheControl?.OnlyIfCached == true)
            {
                return new HttpResponseMessage(HttpStatusCode.GatewayTimeout);
            }

            metrics?.RecordRevalidation(clientName: clientName);
            LogRevalidation(key);
            return await RevalidateAsync(key, entry, request, ct).ConfigureAwait(false);
        }

        // Cache miss (or stale without validator) — full request
        // RFC 9111 §5.2.1.7 — only-if-cached: no usable entry, return 504 immediately
        if (request.Headers.CacheControl?.OnlyIfCached == true)
        {
            return new HttpResponseMessage(HttpStatusCode.GatewayTimeout);
        }

        metrics?.RecordCacheMiss(clientName);
        LogCacheMiss(key);

        DateTimeOffset fetchStart = _timeProvider.GetUtcNow();
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch when (CanServeStaleOnError(entry))
        {
            metrics?.RecordStaleErrorServed(clientName);
            LogStaleIfErrorServed(key);
            return CreateResponse(entry!);
        }

        TimeSpan fetchDuration = _timeProvider.GetUtcNow() - fetchStart;

        // RFC 5861 §4 — stale-if-error: serve stale on 5xx if within the error window
        if (entry is not null && (int)response.StatusCode >= 500 && CanServeStaleOnError(entry))
        {
            response.Dispose();
            metrics?.RecordStaleErrorServed(clientName);
            LogStaleIfErrorServed(key);
            return CreateResponse(entry);
        }

        bool noStore = request.Options.TryGetValue(CacheRequestPolicy.NoStore, out bool ns) && ns;

        if (!noStore && IsResponseCacheable(response, request))
        {
            LogCacheStore(key);
            await StoreAsync(key, request, response, fetchDuration, ct).ConfigureAwait(false);
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

        // §4.1 — follow a Vary marker to the representation matching this request's Vary values.
        CacheEntry? entry = await ResolveEntryAsync(getKey, request, ct).ConfigureAwait(false);

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

        // Fresh GET entry — serve headers with empty body; immutable entries ignore no-cache (RFC 8246).
        // A request's own max-age/min-fresh can still tighten this — see SatisfiesRequestFreshnessDirectives.
        if (entry is not null && !entry.IsExpired(_timeProvider) && (!requestNoCache || entry.Immutable)
            && SatisfiesRequestFreshnessDirectives(entry, request))
        {
            metrics?.RecordCacheHit(HttpMethod.Head, clientName);
            LogCacheHit(getKey);
            return CreateResponse(entry, includeBody: false);
        }

        // §5.2.1.2 — max-stale: the client accepts an expired GET entry directly, no origin contact.
        if (entry is not null && !requestNoCache && entry.IsExpired(_timeProvider) && IsWithinRequestMaxStale(entry, request))
        {
            metrics?.RecordCacheHit(HttpMethod.Head, clientName);
            LogMaxStaleServed(getKey);
            return CreateResponse(entry, includeBody: false);
        }

        // Stale entry with a validator — conditional HEAD revalidation
        if (entry is not null && (entry.ETag is not null || entry.LastModified is not null))
        {
            metrics?.RecordRevalidation(HttpMethod.Head, clientName);
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

            DateTimeOffset headFetchStart = _timeProvider.GetUtcNow();
            HttpResponseMessage revalResponse = await base.SendAsync(request, ct).ConfigureAwait(false);
            TimeSpan headFetchDuration = _timeProvider.GetUtcNow() - headFetchStart;

            if (revalResponse.StatusCode == HttpStatusCode.NotModified)
            {
                CacheEntry refreshed = RefreshFromNotModified(entry, revalResponse, headFetchDuration);
                await WriteEntryAsync(getKey, refreshed, ct).ConfigureAwait(false);
                metrics?.RecordCacheHit(HttpMethod.Head, clientName);
                return CreateResponse(refreshed, includeBody: false);
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

        DateTimeOffset fetchStart = _timeProvider.GetUtcNow();
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch when (CanServeStaleOnError(entry))
        {
            metrics?.RecordStaleErrorServed(clientName);
            return CreateResponse(entry);
        }

        TimeSpan fetchDuration = _timeProvider.GetUtcNow() - fetchStart;

        // RFC 5861 §4 — stale-if-error: serve stale on 5xx if within the error window
        if ((int)response.StatusCode >= 500 && CanServeStaleOnError(entry))
        {
            response.Dispose();
            metrics?.RecordStaleErrorServed(clientName);
            return CreateResponse(entry);
        }

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            CacheEntry refreshed = RefreshFromNotModified(entry, response, fetchDuration);
            await WriteEntryAsync(key, refreshed, ct).ConfigureAwait(false);
            metrics?.RecordCacheHit(clientName: clientName);
            return CreateResponse(refreshed);
        }

        // Per-request NoStore: allow 304 TTL refresh (above) but block storing a new response
        bool noStore = request.Options.TryGetValue(CacheRequestPolicy.NoStore, out bool ns) && ns;

        if (!noStore && IsResponseCacheable(response, request))
        {
            await StoreAsync(key, request, response, fetchDuration, ct).ConfigureAwait(false);
        }

        return response;
    }

    /// <summary>
    /// Builds the refreshed cache entry after a successful <c>304 Not Modified</c> revalidation
    /// (RFC 9111 §4.3.4): the stored header fields are replaced by those carried on the 304,
    /// freshness metadata is recomputed, and <see cref="CacheEntry.StoredAt"/> is reset to the
    /// revalidation time so the <c>Age</c> calculation restarts from the validation response (§4.2.3)
    /// instead of continuing to grow from the original store time.
    /// </summary>
    /// <param name="entry">The entry being refreshed.</param>
    /// <param name="response">The <c>304</c> response.</param>
    /// <param name="fetchDuration">
    /// Wall-clock time this revalidation call took, recorded as the entry's new
    /// <see cref="CacheEntry.OriginFetchDurationMs"/> for early revalidation (XFetch) to scale by.
    /// </param>
    private CacheEntry RefreshFromNotModified(CacheEntry entry, HttpResponseMessage response, TimeSpan fetchDuration)
    {
        // §4.3.4 — update the stored response's header fields with those provided in the 304
        Dictionary<string, string[]> headers = new(entry.Headers.Count, StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string[]> header in entry.Headers)
        {
            headers[header.Key] = header.Value;
        }

        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
        {
            headers[header.Key] = [.. header.Value];
        }

        return entry with
        {
            StoredAt = _timeProvider.GetUtcNow(),
            Headers = headers,
            ETag = response.Headers.ETag?.Tag ?? entry.ETag,
            ExpiresAt = FreshnessCalculator.ComputeExpiresAt(response, Options, _timeProvider),
            StaleIfErrorSeconds = FreshnessCalculator.ExtractStaleIfError(response, Options),
            StaleWhileRevalidateSeconds = FreshnessCalculator.ExtractStaleWhileRevalidate(response, Options),
            MustRevalidate = response.Headers.CacheControl?.MustRevalidate == true || response.Headers.CacheControl?.ProxyRevalidate == true,
            OriginFetchDurationMs = Math.Max(0L, (long)fetchDuration.TotalMilliseconds)
        };
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
    /// Decides whether to trigger a background refresh of a still-fresh entry ahead of its expiry
    /// (XFetch — Vattani, Padmanabhan &amp; Gionis, 2015). The probability of triggering rises as
    /// <see cref="CacheEntry.ExpiresAt"/> approaches, scaled by how expensive the entry was to fetch
    /// (<see cref="CacheEntry.OriginFetchDurationMs"/>): an expensive-to-recompute resource starts being
    /// refreshed early well before a cheap one, spreading out — rather than synchronizing — when
    /// concurrent callers or process instances all decide to refetch the same key near its expiry.
    /// </summary>
    /// <remarks>
    /// Formula: trigger when <c>now + delta * beta * -ln(r) &gt;= ExpiresAt</c>, where <c>delta</c> is the
    /// measured origin fetch duration, <c>beta</c> is <see cref="CacheOptions.EarlyRevalidationBeta"/>, and
    /// <c>r</c> is uniform in (0, 1]. <c>-ln(r)</c> is exponentially distributed with mean 1, so the "lead
    /// time" <c>delta * beta * -ln(r)</c> has mean <c>delta * beta</c>: entries are refreshed, on average,
    /// that far ahead of expiry, with the exact moment randomized per attempt.
    /// </remarks>
    private bool ShouldEarlyRevalidate(CacheEntry entry)
    {
        if (entry.OriginFetchDurationMs <= 0)
        {
            return false; // nothing measured yet — a pre-2.5 entry, or a key never actually fetched
        }

        // 1 - r maps _random()'s [0, 1) onto (0, 1], keeping -log(r) away from +infinity at r = 0.
        double r = 1.0 - _random();
        double leadMs = entry.OriginFetchDurationMs * Options.EarlyRevalidationBeta * -Math.Log(r);

        return _timeProvider.GetUtcNow() + TimeSpan.FromMilliseconds(leadMs) >= entry.ExpiresAt;
    }

    /// <summary>
    /// Schedules a fire-and-forget background revalidation for the given cache key.
    /// Only one background revalidation per key runs at a time.
    /// </summary>
    private void ScheduleBackgroundRevalidation(string key, CacheEntry entry, HttpRequestMessage originalRequest)
    {
        // Snapshot the request headers now: the caller's HttpRequestMessage is disposed once its response is
        // returned, which can happen before the background task starts.
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

        backgroundRevalidations.Schedule(key, async () =>
        {
            try
            {
                DateTimeOffset fetchStart = _timeProvider.GetUtcNow();
                HttpResponseMessage response = await base.SendAsync(bgRequest, CancellationToken.None).ConfigureAwait(false);
                TimeSpan fetchDuration = _timeProvider.GetUtcNow() - fetchStart;

                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    CacheEntry refreshed = RefreshFromNotModified(entry, response, fetchDuration);
                    await WriteEntryAsync(key, refreshed, CancellationToken.None).ConfigureAwait(false);
                }
                else if (IsResponseCacheable(response, bgRequest))
                {
                    await StoreAsync(key, bgRequest, response, fetchDuration, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                LogBackgroundRevalidationFailed(key, ex);
            }
            finally
            {
                bgRequest.Dispose();
            }
        });
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
    private string BuildGetKey(Uri? uri) => CacheKeyHelpers.BuildGetKey(keyBuilder, uri);

    /// <summary>
    /// Invalidates cached entries affected by a successful unsafe method response (RFC 9111 §4.4).
    /// Removes the effective request URI and, if present, the Location and Content-Location URIs.
    /// </summary>
    private async ValueTask InvalidateForUnsafeMethod(HttpRequestMessage request, HttpResponseMessage response, CancellationToken ct)
    {
        // §4.4 MUST — effective request URI
        await InvalidateKeyAsync(BuildGetKey(request.RequestUri), request.Method, ct).ConfigureAwait(false);

        // §4.4 MAY — Location header
        if (response.Headers.Location is Uri location && location != request.RequestUri)
        {
            await InvalidateKeyAsync(BuildGetKey(location), request.Method, ct).ConfigureAwait(false);
        }

        // §4.4 MAY — Content-Location header
        if (response.Content?.Headers.ContentLocation is Uri contentLocation
            && contentLocation != request.RequestUri
            && contentLocation != response.Headers.Location)
        {
            await InvalidateKeyAsync(BuildGetKey(contentLocation), request.Method, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes a single cache key as part of §4.4 invalidation.
    /// </summary>
    /// <remarks>
    /// The removal is issued unconditionally rather than probing with a read first: removal is idempotent, so
    /// the read only served to make the log and metric count confirmed deletions — and against a distributed
    /// store that meant fetching the whole stored body over the network just to decide whether to log, doubling
    /// the round-trips of every successful unsafe request. The metric therefore counts invalidations issued.
    /// </remarks>
    private async ValueTask InvalidateKeyAsync(string key, HttpMethod method, CancellationToken ct)
    {
        await cache.RemoveAsync(key, ct).ConfigureAwait(false);
        metrics?.RecordCacheInvalidation(clientName);
        LogCacheInvalidation(key, method.Method);
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache: not storing {CacheKey}, body of {BodyBytes} bytes exceeds MaxBodySizeBytes ({MaxBodySizeBytes})")]
    private partial void LogBodyTooLarge(string cacheKey, long bodyBytes, long maxBodySizeBytes);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache: serving stale-while-revalidate for {CacheKey}")]
    private partial void LogStaleWhileRevalidate(string cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache: serving expired entry for {CacheKey} within the request's max-stale directive")]
    private partial void LogMaxStaleServed(string cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache: early revalidation (XFetch) triggered for {CacheKey}")]
    private partial void LogEarlyRevalidationTriggered(string cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache: background revalidation failed for {CacheKey}")]
    private partial void LogBackgroundRevalidationFailed(string cacheKey, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache: invalidating {CacheKey} after successful {HttpMethod} request (RFC 9111 §4.4)")]
    private partial void LogCacheInvalidation(string cacheKey, string httpMethod);
}
