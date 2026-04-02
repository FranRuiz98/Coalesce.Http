namespace Coalesce.Http.Caching;

/// <summary>
/// Provides configuration options for cache behavior, including the default time-to-live and maximum allowable body
/// size for cached items.
/// </summary>
/// <remarks>Use this class to customize caching policies for HTTP responses. Adjust the DefaultTtl property to
/// control how long items remain valid in the cache, and set MaxBodySizeBytes to limit the size of cached content.
/// These settings can be tuned to optimize performance and resource usage based on application requirements.</remarks>
public sealed class CacheOptions
{
    private TimeSpan _defaultTtl = TimeSpan.FromSeconds(30);
    private long _maxBodySizeBytes = 1024 * 1024;
    private long _defaultStaleIfErrorSeconds;
    private long _defaultStaleWhileRevalidateSeconds;
    private long? _maxCacheSize;

    /// <summary>
    /// Gets or sets the default time-to-live (TTL) duration for cache entries.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is less than or equal to <see cref="TimeSpan.Zero"/>.</exception>
    public TimeSpan DefaultTtl
    {
        get => _defaultTtl;
        set
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "DefaultTtl must be positive.");
            }

            _defaultTtl = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum allowable size, in bytes, for the body of a cached response.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative.</exception>
    public long MaxBodySizeBytes
    {
        get => _maxBodySizeBytes;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _maxBodySizeBytes = value;
        }
    }

    /// <summary>
    /// Gets or sets the default stale-if-error window in seconds (RFC 5861 §4).
    /// </summary>
    /// <remarks>When a cached response does not carry a <c>stale-if-error</c> directive, this value is
    /// used as the fallback. A value of <c>0</c> (the default) disables stale-if-error serving unless
    /// the origin explicitly includes the directive in its response.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative.</exception>
    public long DefaultStaleIfErrorSeconds
    {
        get => _defaultStaleIfErrorSeconds;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _defaultStaleIfErrorSeconds = value;
        }
    }

    /// <summary>
    /// Gets or sets the default stale-while-revalidate window in seconds (RFC 5861 §3).
    /// </summary>
    /// <remarks>When a cached response does not carry a <c>stale-while-revalidate</c> directive, this value is
    /// used as the fallback. A value of <c>0</c> (the default) disables stale-while-revalidate serving unless
    /// the origin explicitly includes the directive in its response.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative.</exception>
    public long DefaultStaleWhileRevalidateSeconds
    {
        get => _defaultStaleWhileRevalidateSeconds;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _defaultStaleWhileRevalidateSeconds = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum total size, in bytes, of all cached response bodies.
    /// </summary>
    /// <remarks>When set, the underlying <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/>
    /// will evict least-recently-used entries once the limit is reached. Each entry's size is its
    /// <see cref="CacheEntry.Body"/> length. A value of <c>null</c> (the default) means no limit.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is less than or equal to zero.</exception>
    public long? MaxCacheSize
    {
        get => _maxCacheSize;
        set
        {
            if (value is not null)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value.Value);
            }

            _maxCacheSize = value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether query parameters are sorted alphabetically
    /// before the cache key is built by <see cref="DefaultCacheKeyBuilder"/>.
    /// </summary>
    /// <remarks>
    /// When <see langword="true"/>, requests to <c>/items?b=2&amp;a=1</c> and
    /// <c>/items?a=1&amp;b=2</c> map to the same cache entry, preventing spurious cache misses
    /// caused by parameter-ordering differences in generated URLs.
    /// Default is <see langword="false"/>.
    /// </remarks>
    public bool NormalizeQueryParameters { get; set; }
}