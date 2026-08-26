namespace Stampede.Http.Options;

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
    ///     .AddStampedeHttp(configureCoalescing: o =>
    ///     {
    ///         o.CoalesceKeyHeaders = ["X-Tenant-Id", "Accept-Version"];
    ///     });
    /// </code>
    /// </example>
    public IReadOnlyList<string> CoalesceKeyHeaders { get; set; } = [];

    /// <summary>
    /// Gets or sets an optional predicate that extends coalescing to methods other than <c>GET</c>/<c>HEAD</c>,
    /// which remain coalesceable unconditionally. <see langword="null"/> (the default) means no other method
    /// is ever coalesced — matches pre-2.5 behavior.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This asserts the request is idempotent and safe to deduplicate.</b> Coalescing means concurrent
    /// identical calls share a single execution — fine for a read exposed over <c>POST</c> (a GraphQL
    /// <c>query</c>, a search endpoint with a large filter body), actively wrong for a mutation: two
    /// concurrent orders would collapse into one order actually placed. Only opt a method in when you know
    /// every request it matches is a read.
    /// </para>
    /// <para>
    /// The predicate runs before the body is read — inspect the method, URI, and headers, not
    /// <see cref="HttpRequestMessage.Content"/>. A common pattern is a header the caller adds when building
    /// the request, e.g. <c>X-Operation-Type: query</c> for a GraphQL gateway that must coalesce queries but
    /// never mutations:
    /// </para>
    /// <code>
    /// services.AddHttpClient("graphql")
    ///     .AddStampedeHttp(configureCoalescing: o =>
    ///     {
    ///         o.ShouldCoalesce = req => req.Method == HttpMethod.Post
    ///             &amp;&amp; req.Headers.TryGetValues("X-Operation-Type", out var v)
    ///             &amp;&amp; v.Contains("query");
    ///     });
    /// </code>
    /// <para>
    /// When a matched request carries a body, it discriminates the coalescing key — two different bodies to
    /// the same URL are never merged — which requires buffering it (see
    /// <see cref="MaxCoalescedRequestBodyBytes"/>). Buffering also makes the body replayable, so retry/hedging
    /// layers added via <c>AddResilienceHandler</c> work the same way they already do for the coalesced
    /// response.
    /// </para>
    /// </remarks>
    public Func<HttpRequestMessage, bool>? ShouldCoalesce { get; set; }

    private long _maxCoalescedRequestBodyBytes = 64 * 1024;

    /// <summary>
    /// Gets or sets the maximum request body size, in bytes, that will be buffered to compute the
    /// coalescing key for a method matched by <see cref="ShouldCoalesce"/>. A body larger than this is not
    /// an error: that request simply executes independently, without coalescing. Irrelevant for
    /// <c>GET</c>/<c>HEAD</c>, which are never keyed on their body. Default is 64 KB.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative.</exception>
    public long MaxCoalescedRequestBodyBytes
    {
        get => _maxCoalescedRequestBodyBytes;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _maxCoalescedRequestBodyBytes = value;
        }
    }
}
