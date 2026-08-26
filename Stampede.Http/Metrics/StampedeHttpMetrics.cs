using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Stampede.Http.Metrics;

/// <summary>
/// Provides <see cref="Meter"/>-based instrumentation for Stampede.Http.
/// </summary>
/// <remarks>
/// <para>Meter name: <c>Stampede.Http</c></para>
/// <para>
/// Every instrument is tagged with <c>stampede_http.client_name</c> — the name of the
/// <c>HttpClient</c> the measurement came from (as passed to <c>IHttpClientFactory.CreateClient</c>)
/// — whenever the middleware was registered against a named client. The default/unnamed client and
/// the parameterless test constructors emit no tag, so this is purely additive: existing consumers
/// that don't filter or group by it see identical totals.
/// </para>
/// <para>Instruments emitted:</para>
/// <list type="table">
///   <item><term>stampede_http.cache.hits</term><description>Requests served directly from cache.</description></item>
///   <item><term>stampede_http.cache.misses</term><description>Requests forwarded to the origin due to a cache miss.</description></item>
///   <item><term>stampede_http.cache.revalidations</term><description>Conditional revalidation requests (If-None-Match / If-Modified-Since).</description></item>
///   <item><term>stampede_http.cache.stale_errors_served</term><description>Stale responses served under stale-if-error (RFC 5861).</description></item>
///   <item><term>stampede_http.cache.stale_while_revalidate_served</term><description>Stale responses served immediately while a background revalidation was triggered (RFC 5861).</description></item>
///   <item><term>stampede_http.cache.invalidations</term><description>Cache invalidations issued after successful unsafe method responses (RFC 9111 §4.4).</description></item>
///   <item><term>stampede_http.coalescing.deduplicated</term><description>Requests that reused an in-flight coalesced response.</description></item>
///   <item><term>stampede_http.coalescing.inflight</term><description>Current number of in-flight coalesced requests at the origin.</description></item>
///   <item><term>stampede_http.coalescing.timeouts</term><description>Coalesced waiters that timed out and fell back to independent execution.</description></item>
/// </list>
/// <para>Register in DI via <c>AddStampedeHttp</c> — the instance is resolved automatically.</para>
/// </remarks>
public sealed class StampedeHttpMetrics : IDisposable
{
    /// <summary>Name of the <see cref="Meter"/> published by this library.</summary>
    public const string MeterName = "Stampede.Http";

    /// <summary>Tag key carrying the named <c>HttpClient</c> a measurement originated from.</summary>
    private const string ClientNameTagKey = "stampede_http.client_name";

    private readonly Meter _meter;
    private readonly Counter<long> _cacheHits;
    private readonly Counter<long> _cacheMisses;
    private readonly Counter<long> _cacheRevalidations;
    private readonly Counter<long> _staleErrorsServed;
    private readonly Counter<long> _staleWhileRevalidateServed;
    private readonly Counter<long> _cacheInvalidations;
    private readonly Counter<long> _coalescedDeduplicated;
    private readonly UpDownCounter<long> _coalescedInflight;
    private readonly Counter<long> _coalescedTimeouts;

    /// <summary>Initialises a new instance of <see cref="StampedeHttpMetrics"/> with the default meter name.</summary>
    public StampedeHttpMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");

        _cacheHits = _meter.CreateCounter<long>(
            "stampede_http.cache.hits",
            unit: "requests",
            description: "Number of requests served from cache without contacting the origin.");

        _cacheMisses = _meter.CreateCounter<long>(
            "stampede_http.cache.misses",
            unit: "requests",
            description: "Number of requests not satisfied from cache and forwarded to the origin.");

        _cacheRevalidations = _meter.CreateCounter<long>(
            "stampede_http.cache.revalidations",
            unit: "requests",
            description: "Number of conditional revalidation requests (If-None-Match / If-Modified-Since).");

        _staleErrorsServed = _meter.CreateCounter<long>(
            "stampede_http.cache.stale_errors_served",
            unit: "requests",
            description: "Number of stale responses served under stale-if-error (RFC 5861 §4).");

        _staleWhileRevalidateServed = _meter.CreateCounter<long>(
            "stampede_http.cache.stale_while_revalidate_served",
            unit: "requests",
            description: "Number of stale responses served immediately while a background revalidation was triggered (RFC 5861 §3).");

        _cacheInvalidations = _meter.CreateCounter<long>(
            "stampede_http.cache.invalidations",
            unit: "entries",
            description: "Number of cache invalidations issued after successful unsafe method responses (RFC 9111 §4.4). Removal is idempotent, so this counts keys targeted rather than entries confirmed present.");

        _coalescedDeduplicated = _meter.CreateCounter<long>(
            "stampede_http.coalescing.deduplicated",
            unit: "requests",
            description: "Number of requests that reused an in-flight coalesced response instead of hitting the origin.");

        _coalescedInflight = _meter.CreateUpDownCounter<long>(
            "stampede_http.coalescing.inflight",
            unit: "requests",
            description: "Current number of in-flight coalesced requests at the origin.");

        _coalescedTimeouts = _meter.CreateCounter<long>(
            "stampede_http.coalescing.timeouts",
            unit: "requests",
            description: "Number of coalesced waiters that timed out and fell back to independent execution.");
    }

    /// <summary>
    /// Builds the tag set for a measurement: the HTTP method (when known) and the named client
    /// (when set). <see cref="TagList"/> is a stack-allocated struct for up to three inline tags, so
    /// this adds no heap allocation on the hot path for the common 0–2 tag case.
    /// </summary>
    private static TagList BuildTags(HttpMethod? method, string? clientName)
    {
        TagList tags = default;

        if (method is not null)
        {
            tags.Add("http.request.method", method.Method);
        }

        if (!string.IsNullOrEmpty(clientName))
        {
            tags.Add(ClientNameTagKey, clientName);
        }

        return tags;
    }

    internal void RecordCacheHit(HttpMethod? method = null, string? clientName = null) =>
        _cacheHits.Add(1, BuildTags(method, clientName));

    internal void RecordCacheMiss(string? clientName = null) =>
        _cacheMisses.Add(1, BuildTags(method: null, clientName));

    internal void RecordRevalidation(HttpMethod? method = null, string? clientName = null) =>
        _cacheRevalidations.Add(1, BuildTags(method, clientName));

    internal void RecordStaleErrorServed(string? clientName = null) =>
        _staleErrorsServed.Add(1, BuildTags(method: null, clientName));

    internal void RecordStaleWhileRevalidateServed(string? clientName = null) =>
        _staleWhileRevalidateServed.Add(1, BuildTags(method: null, clientName));

    internal void RecordCacheInvalidation(string? clientName = null) =>
        _cacheInvalidations.Add(1, BuildTags(method: null, clientName));

    internal void RecordCoalescedDeduplicated(string? clientName = null) =>
        _coalescedDeduplicated.Add(1, BuildTags(method: null, clientName));

    internal void IncrementInflight(string? clientName = null) =>
        _coalescedInflight.Add(1, BuildTags(method: null, clientName));

    internal void DecrementInflight(string? clientName = null) =>
        _coalescedInflight.Add(-1, BuildTags(method: null, clientName));

    internal void RecordCoalescingTimeout(string? clientName = null) =>
        _coalescedTimeouts.Add(1, BuildTags(method: null, clientName));

    /// <inheritdoc/>
    public void Dispose() => _meter.Dispose();
}
