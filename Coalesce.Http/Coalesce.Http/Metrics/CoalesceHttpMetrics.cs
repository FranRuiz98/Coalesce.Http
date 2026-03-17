using System.Diagnostics.Metrics;

namespace Coalesce.Http.Coalesce.Http.Metrics;

/// <summary>
/// Provides <see cref="Meter"/>-based instrumentation for Coalesce.Http.
/// </summary>
/// <remarks>
/// <para>Meter name: <c>Coalesce.Http</c></para>
/// <para>Instruments emitted:</para>
/// <list type="table">
///   <item><term>coalesce_http.cache.hits</term><description>Requests served directly from cache.</description></item>
///   <item><term>coalesce_http.cache.misses</term><description>Requests forwarded to the origin due to a cache miss.</description></item>
///   <item><term>coalesce_http.cache.revalidations</term><description>Conditional revalidation requests (If-None-Match / If-Modified-Since).</description></item>
///   <item><term>coalesce_http.cache.stale_errors_served</term><description>Stale responses served under stale-if-error (RFC 5861).</description></item>
///   <item><term>coalesce_http.coalescing.deduplicated</term><description>Requests that reused an in-flight coalesced response.</description></item>
///   <item><term>coalesce_http.coalescing.inflight</term><description>Current number of in-flight coalesced requests at the origin.</description></item>
/// </list>
/// <para>Register in DI via <c>AddCoalesceHttp</c> — the instance is resolved automatically.</para>
/// </remarks>
public sealed class CoalesceHttpMetrics : IDisposable
{
    /// <summary>Name of the <see cref="Meter"/> published by this library.</summary>
    public const string MeterName = "Coalesce.Http";

    private readonly Meter _meter;
    private readonly Counter<long> _cacheHits;
    private readonly Counter<long> _cacheMisses;
    private readonly Counter<long> _cacheRevalidations;
    private readonly Counter<long> _staleErrorsServed;
    private readonly Counter<long> _coalescedDeduplicated;
    private readonly UpDownCounter<long> _coalescedInflight;

    /// <summary>Initialises a new instance of <see cref="CoalesceHttpMetrics"/> with the default meter name.</summary>
    public CoalesceHttpMetrics()
    {
        _meter = new Meter(MeterName, "0.0.4");

        _cacheHits = _meter.CreateCounter<long>(
            "coalesce_http.cache.hits",
            unit: "requests",
            description: "Number of requests served from cache without contacting the origin.");

        _cacheMisses = _meter.CreateCounter<long>(
            "coalesce_http.cache.misses",
            unit: "requests",
            description: "Number of requests not satisfied from cache and forwarded to the origin.");

        _cacheRevalidations = _meter.CreateCounter<long>(
            "coalesce_http.cache.revalidations",
            unit: "requests",
            description: "Number of conditional revalidation requests (If-None-Match / If-Modified-Since).");

        _staleErrorsServed = _meter.CreateCounter<long>(
            "coalesce_http.cache.stale_errors_served",
            unit: "requests",
            description: "Number of stale responses served under stale-if-error (RFC 5861 §4).");

        _coalescedDeduplicated = _meter.CreateCounter<long>(
            "coalesce_http.coalescing.deduplicated",
            unit: "requests",
            description: "Number of requests that reused an in-flight coalesced response instead of hitting the origin.");

        _coalescedInflight = _meter.CreateUpDownCounter<long>(
            "coalesce_http.coalescing.inflight",
            unit: "requests",
            description: "Current number of in-flight coalesced requests at the origin.");
    }

    internal void RecordCacheHit() => _cacheHits.Add(1);
    internal void RecordCacheMiss() => _cacheMisses.Add(1);
    internal void RecordRevalidation() => _cacheRevalidations.Add(1);
    internal void RecordStaleErrorServed() => _staleErrorsServed.Add(1);
    internal void RecordCoalescedDeduplicated() => _coalescedDeduplicated.Add(1);
    internal void IncrementInflight() => _coalescedInflight.Add(1);
    internal void DecrementInflight() => _coalescedInflight.Add(-1);

    /// <inheritdoc/>
    public void Dispose() => _meter.Dispose();
}
