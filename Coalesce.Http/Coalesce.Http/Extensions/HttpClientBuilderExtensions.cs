using Coalesce.Http.Coalesce.Http.Caching;
using Coalesce.Http.Coalesce.Http.Coalescing;
using Coalesce.Http.Coalesce.Http.Handlers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Coalesce.Http.Coalesce.Http.Extensions;

public static class HttpClientBuilderExtensions
{
    public static IHttpClientBuilder AddCoalescing(this IHttpClientBuilder builder)
    {
        _ = builder.Services.AddSingleton<RequestCoalescer>();
        _ = builder.Services.AddTransient<CoalescingHandler>();
        _ = builder.AddHttpMessageHandler<CoalescingHandler>();
        return builder;
    }

    public static IHttpClientBuilder AddHttpCache(this IHttpClientBuilder builder, Action<CacheOptions>? configure = null)
    {
        CacheOptions options = new();
        configure?.Invoke(options);

        builder.Services.AddMemoryCache();
        builder.Services.TryAddSingleton<ICacheKeyBuilder, DefaultCacheKeyBuilder>();
        builder.Services.AddTransient<CachingMiddleware>(sp =>
            new CachingMiddleware(
                sp.GetRequiredService<IMemoryCache>(),
                sp.GetRequiredService<ICacheKeyBuilder>(),
                options));
        _ = builder.AddHttpMessageHandler<CachingMiddleware>();
        return builder;
    }
}
