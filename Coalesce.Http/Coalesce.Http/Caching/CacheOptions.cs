namespace Coalesce.Http.Coalesce.Http.Caching;

/// <summary>
/// Provides configuration options for cache behavior, including the default time-to-live and maximum allowable body
/// size for cached items.
/// </summary>
/// <remarks>Use this class to customize caching policies for HTTP responses. Adjust the DefaultTtl property to
/// control how long items remain valid in the cache, and set MaxBodySizeBytes to limit the size of cached content.
/// These settings can be tuned to optimize performance and resource usage based on application requirements.</remarks>
public sealed class CacheOptions
{
    /// <summary>
    /// Gets or sets the default time-to-live (TTL) duration for cache entries.
    /// </summary>
    /// <remarks>The default TTL determines how long a cache entry remains valid before it is considered
    /// expired. The default value is set to 30 seconds, but it can be adjusted as needed to optimize cache
    /// performance.</remarks>
    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the maximum allowable size, in bytes, for the body of a request.
    /// </summary>
    /// <remarks>This property is useful for limiting the size of incoming requests to prevent excessive
    /// resource usage. The default value is set to 1,048,576 bytes (1 MB).</remarks>
    public long MaxBodySizeBytes { get; set; } = 1024 * 1024;
}