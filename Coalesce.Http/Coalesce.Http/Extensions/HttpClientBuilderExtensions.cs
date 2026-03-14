using Coalesce.Http.Coalesce.Http.Caching;
using Coalesce.Http.Coalesce.Http.Coalescing;
using Coalesce.Http.Coalesce.Http.Handlers;
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
    /// </remarks>
    /// <param name="configure">An optional action to configure <see cref="CacheOptions"/>.</param>
    public static IHttpClientBuilder AddCoalesceHttp(
        this IHttpClientBuilder builder,
        Action<CacheOptions>? configure = null)
    {
        AddHttpCache(builder, configure);   // outermost — serve hits before reaching the coalescer
        AddCoalescing(builder);             // inner — deduplicate concurrent backend misses
        return builder;
    }

    private static void AddCoalescing(IHttpClientBuilder builder)
    {
        _ = builder.Services.AddSingleton<RequestCoalescer>();
        _ = builder.Services.AddTransient<CoalescingHandler>();
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
                options));
        _ = builder.AddHttpMessageHandler<CachingMiddleware>();
    }
}
