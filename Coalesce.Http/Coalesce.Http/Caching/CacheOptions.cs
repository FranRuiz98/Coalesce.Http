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

    /// <summary>
    /// Gets or sets the default stale-if-error window in seconds (RFC 5861 §4).
    /// </summary>
    /// <remarks>When a cached response does not carry a <c>stale-if-error</c> directive, this value is
    /// used as the fallback. A value of <c>0</c> (the default) disables stale-if-error serving unless
    /// the origin explicitly includes the directive in its response.</remarks>
    public long DefaultStaleIfErrorSeconds { get; set; } = 0;
}