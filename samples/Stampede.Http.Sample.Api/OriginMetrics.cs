using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Stampede.Http.Sample.Api;

/// <summary>
/// Origin-side instrumentation. This is the control instrument for the whole sample:
/// it counts requests that actually reached the origin, broken down by endpoint and by
/// which client sent them (via the <c>X-Client</c> header the clients attach).
/// </summary>
/// <remarks>
/// Comparing this counter for a Stampede.Http-enabled client against the one running a
/// bare <see cref="HttpClient"/> is what turns "caching helps" into a number.
/// </remarks>
public sealed class OriginMetrics : IDisposable
{
    /// <summary>Name of the meter published by the sample origin.</summary>
    public const string MeterName = "Stampede.Http.Sample.Api";

    private readonly Meter _meter;
    private readonly Counter<long> _requests;
    private readonly Histogram<double> _duration;

    /// <summary>Initialises the origin meter and its instruments.</summary>
    public OriginMetrics()
    {
        _meter = new Meter(MeterName);
        // No unit on the counter: the OTel Prometheus exporter appends the unit to the
        // metric name, and "sample_api_origin_requests_requests_total" helps nobody.
        _requests = _meter.CreateCounter<long>(
            "sample_api.origin.requests",
            description: "Requests that actually reached the origin.");
        _duration = _meter.CreateHistogram<double>(
            "sample_api.origin.duration",
            unit: "ms",
            description: "Origin-side handling time.");
    }

    /// <summary>Records one request that reached the origin.</summary>
    /// <param name="endpoint">Route pattern (e.g. <c>/catalog</c>).</param>
    /// <param name="client">Value of the <c>X-Client</c> request header, or <c>unknown</c>.</param>
    /// <param name="statusCode">Response status code the origin produced.</param>
    /// <param name="elapsedMs">Origin-side handling time in milliseconds.</param>
    public void Record(string endpoint, string client, int statusCode, double elapsedMs)
    {
        TagList tags = new()
        {
            { "endpoint", endpoint },
            { "client", client },
            { "status", statusCode },
        };

        _requests.Add(1, tags);
        _duration.Record(elapsedMs, tags);
    }

    /// <inheritdoc/>
    public void Dispose() => _meter.Dispose();
}
