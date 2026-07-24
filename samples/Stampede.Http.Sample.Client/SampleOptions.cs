using System.ComponentModel.DataAnnotations;

namespace Stampede.Http.Sample.Client;

/// <summary>
/// Everything about this instance that is worth changing without a rebuild.
/// Bound from <c>appsettings.json</c> plus environment variables, and validated at startup.
/// </summary>
public sealed class SampleOptions
{
    /// <summary>Configuration section this class binds to.</summary>
    public const string SectionName = "Sample";

    /// <summary>Instance name used in logs, metrics and the <c>X-Client</c> header sent to the origin.</summary>
    [Required]
    public string Instance { get; set; } = Environment.MachineName;

    /// <summary>Base address of the sample origin API.</summary>
    [Required]
    public string ApiBaseUrl { get; set; } = "http://localhost:5080";

    /// <summary>Redis connection string used as the shared second-level cache.</summary>
    public string? RedisConnection { get; set; }

    /// <summary>Pipeline configuration.</summary>
    public PipelineOptions Pipeline { get; set; } = new();

    /// <summary>Background workload configuration.</summary>
    public WorkloadOptions Workload { get; set; } = new();
}

/// <summary>
/// How the outbound pipeline is assembled. Turning <see cref="Enabled"/> off produces the
/// control group: an identical app making identical calls with a bare <see cref="HttpClient"/>.
/// </summary>
public sealed class PipelineOptions
{
    /// <summary>
    /// Whether Stampede.Http is in the pipeline at all. <see langword="false"/> yields the
    /// baseline instance the dashboards compare against.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether to use Redis (via <c>IDistributedCache</c>) instead of the in-memory store.</summary>
    public bool UseRedis { get; set; } = true;

    /// <summary>Fallback freshness lifetime. Hot-reloadable — see <c>config/client.json</c>.</summary>
    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a coalesced waiter waits before falling back to its own request.</summary>
    public TimeSpan CoalescingTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Request headers folded into the coalescing key, so concurrent bursts for different
    /// tenants are deduplicated independently rather than collapsing into one response.
    /// </summary>
    /// <remarks>
    /// Left empty on purpose: the configuration binder concatenates bound array elements onto
    /// a non-empty default, so a default here would duplicate every value in appsettings.json.
    /// </remarks>
    public string[] CoalesceKeyHeaders { get; set; } = [];

    /// <summary>
    /// Largest body the coalescer will buffer while sharing one response among waiters.
    /// </summary>
    /// <remarks>
    /// This is a different ceiling from <see cref="Stampede.Http.Caching.CacheOptions.MaxBodySizeBytes"/>,
    /// and they fail differently: exceeding the cache's limit silently declines to store the
    /// response, while exceeding the coalescer's throws for every waiter. It is set above the
    /// cache limit here so <c>/bulk</c> can demonstrate the first without tripping the second.
    /// </remarks>
    public long MaxResponseBodyBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Sort query parameters before building the cache key.</summary>
    public bool NormalizeQueryParameters { get; set; } = true;

    /// <summary>Total byte ceiling for the in-memory store; ignored when <see cref="UseRedis"/> is set.</summary>
    public long? MaxCacheSize { get; set; }
}

/// <summary>Which parts of the scripted workload this instance runs.</summary>
public sealed class WorkloadOptions
{
    /// <summary>Master switch — set to <see langword="false"/> to leave the app purely request-driven (k6, curl).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Number of concurrent callers in the opening stampede.</summary>
    [Range(1, 1000)]
    public int StampedeSize { get; set; } = 10;

    /// <summary>
    /// Whether to run the narrated feature tour. Enable it on exactly one instance:
    /// it asserts against the origin's own counters, which every other caller perturbs.
    /// </summary>
    public bool FeatureTour { get; set; }

    /// <summary>Delay between steady-state iterations.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Iterations between <c>POST /catalog</c> mutations.</summary>
    [Range(1, 1000)]
    public int MutateEvery { get; set; } = 10;

    /// <summary>Iterations between concurrent bursts on <c>/slow</c>.</summary>
    /// <remarks>
    /// Without this the steady state issues one request at a time and there is, correctly,
    /// nothing to deduplicate — the coalescing panels sit at zero and the library's headline
    /// feature looks dead. Real inbound traffic is concurrent; this models that.
    /// <para>
    /// The burst deliberately targets <c>/slow</c>, which is excluded from the origin-load
    /// comparison, so adding it does not inflate the headline percentage.
    /// </para>
    /// </remarks>
    [Range(1, 1000)]
    public int BurstEvery { get; set; } = 5;

    /// <summary>Number of concurrent callers in each steady-state burst.</summary>
    [Range(1, 1000)]
    public int BurstSize { get; set; } = 8;
}
