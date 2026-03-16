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
    /// Adds both RFC 9111-compliant HTTP caching and request coalescing to the pipeline in the
    /// correct order, preventing cache stampede without any registration-order risk.
    /// </summary>
    /// <remarks>
    /// <para>The resulting pipeline is:</para>
    /// <code>
    /// CachingMiddleware (outer)
    ///   └─ CoalescingHandler (inner)
    ///        └─ HttpClientHandler
    /// </code>
    /// <para>Cache hits are returned immediately by <c>CachingMiddleware</c> and never reach
    /// <c>CoalescingHandler</c>. On a cache miss, <c>CoalescingHandler</c> collapses all concurrent
    /// requests for the same resource into a single backend call, preventing cache stampede.</para>
    /// <para>To add Polly resilience (retry, hedging, timeout) call
    /// <c>AddResilienceHandler</c> on the returned builder:</para>
    /// <code>
    /// IHttpClientBuilder b = builder.AddCoalesceHttp();
    /// b.AddResilienceHandler("my-pipeline", b => { ... });
    /// </code>
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

