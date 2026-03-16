using Coalesce.Http.Coalesce.Http.Caching;
using Coalesce.Http.Coalesce.Http.Coalescing;
using Coalesce.Http.Coalesce.Http.Handlers;
using Coalesce.Http.Coalesce.Http.Metrics;
using Coalesce.Http.Coalesce.Http.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Coalesce.Http.Coalesce.Http.Extensions;

public static class HttpClientBuilderExtensions
{
    /// <summary>
    /// Adds RFC 9111-compliant HTTP caching and request coalescing to the pipeline in the
    /// correct order, preventing cache stampede without any registration-order risk.
    /// </summary>
    /// <remarks>
    /// <para>The resulting pipeline is:</para>
    /// <code>
    /// CachingMiddleware (outer)       ← registered by this method
    ///   └─ CoalescingHandler (inner)  ← registered by this method
    ///        └─ ResilienceHandler     ← Polly: call AddResilienceHandler() after this method
    ///             └─ HttpClientHandler
    /// </code>
    /// <para>
    /// Cache hits are served immediately by <c>CachingMiddleware</c> without reaching the network.
    /// On a cache miss, <c>CoalescingHandler</c> collapses all concurrent identical requests into a
    /// single origin call. Polly then applies retry or hedging to that single call.
    /// </para>
    /// <para>
    /// Retry and hedging are intentionally delegated to Polly
    /// (<c>Microsoft.Extensions.Http.Resilience</c>). Call <c>AddResilienceHandler</c> on the
    /// returned builder <b>after</b> this method to ensure the correct pipeline order:
    /// </para>
    /// <code>
    /// // Retry example
    /// services.AddHttpClient("catalog")
    ///     .AddCoalesceHttp()
    ///     .AddResilienceHandler("resilience", b =>
    ///     {
    ///         b.AddRetry(new HttpRetryStrategyOptions { MaxRetryAttempts = 3 });
    ///     });
    ///
    /// // Hedging example
    /// services.AddHttpClient("catalog")
    ///     .AddCoalesceHttp()
    ///     .AddResilienceHandler("resilience", b =>
    ///     {
    ///         b.AddHedging(new HttpHedgingStrategyOptions
    ///         {
    ///             MaxHedgedAttempts = 2,
    ///             Delay = TimeSpan.FromMilliseconds(100),
    ///         });
    ///     });
    /// </code>
    /// <para>
    /// Polly compatibility is verified by three rules tested in <c>PollyRealIntegrationTests</c>:
    /// Rule 1 (retries share the coalesced execution), Rule 2 (conditional headers survive retries),
    /// and Rule 3 (hedged attempts receive independent <see cref="HttpRequestMessage"/> instances).
    /// </para>
    /// </remarks>
    /// <param name="builder">The <see cref="IHttpClientBuilder"/> to configure.</param>
    /// <param name="configureCaching">An optional action to configure <see cref="CacheOptions"/>.</param>
    /// <param name="configureCoalescing">An optional action to configure <see cref="CoalescerOptions"/>.</param>
    public static IHttpClientBuilder AddCoalesceHttp(
        this IHttpClientBuilder builder,
        Action<CacheOptions>? configureCaching = null,
        Action<CoalescerOptions>? configureCoalescing = null)
    {
        builder.Services.TryAddSingleton<CoalesceHttpMetrics>();

        AddHttpCache(builder, configureCaching);

        CoalescerOptions coalescerOptions = new();
        configureCoalescing?.Invoke(coalescerOptions);
        AddCoalescing(builder, coalescerOptions);

        return builder;
    }

    /// <summary>
    /// Adds only the RFC 9111-compliant HTTP caching layer to the pipeline (no coalescing).
    /// </summary>
    /// <param name="builder">The <see cref="IHttpClientBuilder"/> to configure.</param>
    /// <param name="configure">An optional action to configure <see cref="CacheOptions"/>.</param>
    public static IHttpClientBuilder AddCachingOnly(
        this IHttpClientBuilder builder,
        Action<CacheOptions>? configure = null)
    {
        builder.Services.TryAddSingleton<CoalesceHttpMetrics>();
        AddHttpCache(builder, configure);

        return builder;
    }

    /// <summary>
    /// Adds only the request-coalescing layer to the pipeline (no caching).
    /// </summary>
    /// <param name="builder">The <see cref="IHttpClientBuilder"/> to configure.</param>
    /// <param name="configure">An optional action to configure <see cref="CoalescerOptions"/>.</param>
    public static IHttpClientBuilder AddCoalescingOnly(
        this IHttpClientBuilder builder,
        Action<CoalescerOptions>? configure = null)
    {
        builder.Services.TryAddSingleton<CoalesceHttpMetrics>();

        CoalescerOptions options = new();
        configure?.Invoke(options);
        AddCoalescing(builder, options);

        return builder;
    }

    private static void AddCoalescing(IHttpClientBuilder builder, CoalescerOptions coalescerOptions)
    {
        builder.Services.TryAddSingleton<RequestCoalescer>(sp =>
            new RequestCoalescer(sp.GetService<CoalesceHttpMetrics>()));

        _ = builder.Services.AddTransient<CoalescingHandler>(sp =>
            new CoalescingHandler(sp.GetRequiredService<RequestCoalescer>(), coalescerOptions));

        _ = builder.AddHttpMessageHandler<CoalescingHandler>();
    }

    private static void AddHttpCache(IHttpClientBuilder builder, Action<CacheOptions>? configure)
    {
        CacheOptions options = new();
        configure?.Invoke(options);

        _ = builder.Services.AddMemoryCache();
        builder.Services.TryAddSingleton<ICacheKeyBuilder, DefaultCacheKeyBuilder>();

        _ = builder.Services.AddTransient<CachingMiddleware>(sp =>
            new CachingMiddleware(
                sp.GetRequiredService<IMemoryCache>(),
                sp.GetRequiredService<ICacheKeyBuilder>(),
                options,
                sp.GetService<CoalesceHttpMetrics>()));

        _ = builder.AddHttpMessageHandler<CachingMiddleware>();
    }
}

