namespace Coalesce.Http.Options;

/// <summary>
/// Provides configuration options for the request coalescing layer.
/// </summary>
public sealed class CoalescerOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether request coalescing is enabled.
    /// Set to <see langword="false"/> to disable coalescing (useful for debugging). Default is <see langword="true"/>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    private TimeSpan? _coalescingTimeout;

    /// <summary>
    /// Gets or sets the maximum time a coalesced waiter will wait for the winner's response
    /// before falling back to an independent request. <see langword="null"/> means no timeout (wait indefinitely).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not <see langword="null"/> and is less than or equal to <see cref="TimeSpan.Zero"/>.</exception>
    public TimeSpan? CoalescingTimeout
    {
        get => _coalescingTimeout;
        set
        {
            if (value is TimeSpan ts && ts <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "CoalescingTimeout must be positive or null.");
            }

            _coalescingTimeout = value;
        }
    }

    private long _maxResponseBodyBytes = 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum response body size, in bytes, that the coalescer will buffer.
    /// Responses exceeding this limit cause an <see cref="InvalidOperationException"/> to be
    /// propagated to all coalesced waiters. Default is 1 MB.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative.</exception>
    public long MaxResponseBodyBytes
    {
        get => _maxResponseBodyBytes;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _maxResponseBodyBytes = value;
        }
    }

    /// <summary>
    /// Gets or sets the request header names that are included in the coalescing key.
    /// </summary>
    /// <remarks>
    /// By default, coalescing is keyed on the HTTP method and the absolute URI only.
    /// When multiple tenants (or API versions) use the same URL but differentiate requests via a
    /// header such as <c>X-Tenant-Id</c> or <c>Accept-Version</c>, add those header names here
    /// so that requests with different header values are coalesced independently.
    /// Header names are matched case-insensitively and sorted alphabetically when building the key
    /// to ensure deterministic results regardless of insertion order.
    /// </remarks>
    /// <example>
    /// <code>
    /// services.AddHttpClient("catalog")
    ///     .AddCoalesceHttp(configureCoalescing: o =>
    ///     {
    ///         o.CoalesceKeyHeaders = ["X-Tenant-Id", "Accept-Version"];
    ///     });
    /// </code>
    /// </example>
    public IReadOnlyList<string> CoalesceKeyHeaders { get; set; } = [];
}
