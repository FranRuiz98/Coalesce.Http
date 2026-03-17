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
    /// <summary>
    /// Gets or sets the default time-to-live (TTL) duration for cache entries.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is less than or equal to <see cref="TimeSpan.Zero"/>.</exception>
    public TimeSpan DefaultTtl
    {
        get;
        set
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "DefaultTtl must be positive.");
            }

            field = value;
        }
    } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the maximum allowable size, in bytes, for the body of a cached response.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative.</exception>
    public long MaxBodySizeBytes
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    } = 1024 * 1024;

    /// <summary>
    /// Gets or sets the default stale-if-error window in seconds (RFC 5861 §4).
    /// </summary>
    /// <remarks>When a cached response does not carry a <c>stale-if-error</c> directive, this value is
    /// used as the fallback. A value of <c>0</c> (the default) disables stale-if-error serving unless
    /// the origin explicitly includes the directive in its response.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative.</exception>
    public long DefaultStaleIfErrorSeconds
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    }
}