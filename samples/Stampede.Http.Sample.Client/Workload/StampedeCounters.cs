using System.Diagnostics.Metrics;
using Stampede.Http.Metrics;

namespace Stampede.Http.Sample.Client.Workload;

/// <summary>
/// Keeps a running total of every <c>stampede_http.*</c> instrument in this process, so the
/// container logs and <c>GET /api/metrics</c> can show the same numbers Grafana graphs —
/// handy when you are looking at a terminal rather than a dashboard.
/// </summary>
/// <remarks>
/// This is deliberately separate from the OpenTelemetry pipeline: it demonstrates that the
/// meter is plain <see cref="System.Diagnostics.Metrics"/> and can be consumed by anything.
/// </remarks>
public sealed class StampedeCounters : IDisposable
{
    private readonly Dictionary<string, long> _totals = new(StringComparer.Ordinal);
    private readonly MeterListener _listener;
    private readonly Lock _gate = new();

    /// <summary>Starts listening to the Stampede.Http meter.</summary>
    public StampedeCounters()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == StampedeHttpMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };

        _listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            lock (_gate)
            {
                _totals.TryGetValue(instrument.Name, out long previous);
                _totals[instrument.Name] = previous + measurement;
            }
        });

        _listener.Start();
    }

    /// <summary>Returns a snapshot of the accumulated instrument totals, ordered by name.</summary>
    public IReadOnlyDictionary<string, long> Snapshot()
    {
        lock (_gate)
        {
            return _totals.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                          .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _listener.Dispose();
}
