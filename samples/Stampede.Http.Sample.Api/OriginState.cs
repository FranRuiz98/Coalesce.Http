using System.Collections.Concurrent;

namespace Stampede.Http.Sample.Api;

/// <summary>
/// Mutable origin state shared by the endpoints: request counters (surfaced by
/// <c>/stats</c>), the catalog version bumped by <c>POST /catalog</c>, and the feed
/// generation incremented on every origin fetch.
/// </summary>
public sealed class OriginState
{
    private readonly ConcurrentDictionary<string, int> _counters = new(StringComparer.Ordinal);
    private int _catalogVersion = 1;
    private int _feedGeneration;

    /// <summary>Gets the current catalog version, bumped by <c>POST /catalog</c>.</summary>
    public int CatalogVersion => Volatile.Read(ref _catalogVersion);

    /// <summary>Bumps the catalog version and returns the new value.</summary>
    public int BumpCatalogVersion() => Interlocked.Increment(ref _catalogVersion);

    /// <summary>Increments and returns the feed generation.</summary>
    public int NextFeedGeneration() => Interlocked.Increment(ref _feedGeneration);

    /// <summary>Increments the named counter surfaced by <c>/stats</c>.</summary>
    public void Count(string key) => _counters.AddOrUpdate(key, 1, static (_, v) => v + 1);

    /// <summary>Returns a snapshot of all counters, ordered by key.</summary>
    public IReadOnlyDictionary<string, int> Snapshot() =>
        _counters.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                 .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    /// <summary>Clears every counter — used by the smoke test to isolate a measurement window.</summary>
    public void ResetCounters() => _counters.Clear();

    /// <summary>
    /// <c>/flaky</c> is "down" for the first 20 seconds of every 60-second window, so the
    /// outage is reproducible without anyone having to stop a container.
    /// </summary>
    public static bool FlakyIsDown() => Environment.TickCount64 / 1000 % 60 < 20;
}
