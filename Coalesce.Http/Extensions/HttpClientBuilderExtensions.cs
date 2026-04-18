using Coalesce.Http.Caching;
using Coalesce.Http.Coalescing;
using Coalesce.Http.Handlers;
using Coalesce.Http.Metrics;
using Coalesce.Http.Options;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Coalesce.Http.Extensions;

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
    /// Polly compatibility is verified by three rules:
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
        ArgumentNullException.ThrowIfNull(builder);

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
        ArgumentNullException.ThrowIfNull(builder);

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
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<CoalesceHttpMetrics>();

        CoalescerOptions options = new();
        configure?.Invoke(options);
        AddCoalescing(builder, options);

        return builder;
    }

    /// <summary>
    /// Replaces the default in-memory <see cref="ICacheStore"/> with a <see cref="DistributedCacheStore"/>
    /// backed by <see cref="IDistributedCache"/>, enabling shared caching across multiple instances.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this method <b>after</b> <c>AddCoalesceHttp()</c> or <c>AddCachingOnly()</c> and after
    /// registering an <see cref="IDistributedCache"/> implementation (e.g., Redis, SQL Server):
    /// </para>
    /// <code>
    /// services.AddStackExchangeRedisCache(o => o.Configuration = "localhost:6379");
    ///
    /// services.AddHttpClient("catalog")
    ///     .AddCoalesceHttp()
    ///     .UseDistributedCacheStore();
    /// </code>
    /// <para>
    /// Cache entries are serialized with <see cref="System.Text.Json"/> and the
    /// <c>AbsoluteExpiration</c> is set to <see cref="CacheEntry.ExpiresAt"/> so the backing store
    /// evicts stale entries automatically.
    /// </para>
    /// </remarks>
    /// <param name="builder">The <see cref="IHttpClientBuilder"/> to configure.</param>
    public static IHttpClientBuilder UseDistributedCacheStore(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        string clientName = builder.Name;

        // Remove the keyed MemoryCacheStore registration for this client.
        ServiceDescriptor? existingKeyed = builder.Services.FirstOrDefault(
            d => d.IsKeyedService && d.ServiceType == typeof(ICacheStore) && Equals(d.ServiceKey, clientName));

        if (existingKeyed is not null)
        {
            builder.Services.Remove(existingKeyed);
        }

        // Remove the non-keyed fallback so it can be re-registered pointing to the new store.
        ServiceDescriptor? existingNonKeyed = builder.Services.FirstOrDefault(
            d => !d.IsKeyedService && d.ServiceType == typeof(ICacheStore));

        if (existingNonKeyed is not null)
        {
            builder.Services.Remove(existingNonKeyed);
        }

        builder.Services.AddKeyedSingleton<ICacheStore>(clientName, (sp, _) =>
            new DistributedCacheStore(sp.GetRequiredService<IDistributedCache>()));

        builder.Services.AddSingleton<ICacheStore>(sp =>
            sp.GetRequiredKeyedService<ICacheStore>(clientName));

        return builder;
    }

    private static void AddCoalescing(IHttpClientBuilder builder, CoalescerOptions coalescerOptions)
    {
        // Each call captures its own RequestCoalescer in the closure so that
        // duplicate AddCoalesceHttp() calls produce independent coalescing scopes
        // and don't deadlock through shared inflight state.
        RequestCoalescer? coalescer = null;
#if NET9_0_OR_GREATER
        Lock coalescerLock = new();
#else
        object coalescerLock = new();
#endif

        _ = builder.AddHttpMessageHandler(sp =>
        {
            if (coalescer is null)
            {
                lock (coalescerLock)
                {
                    coalescer ??= new RequestCoalescer(
                        coalescerOptions,
                        sp.GetService<CoalesceHttpMetrics>(),
                        sp.GetService<ILoggerFactory>()?.CreateLogger<RequestCoalescer>());
                }
            }

            return new CoalescingHandler(
                coalescer,
                coalescerOptions,
                sp.GetService<ILoggerFactory>()?.CreateLogger<CoalescingHandler>());
        });
    }

    private static void AddHttpCache(IHttpClientBuilder builder, Action<CacheOptions>? configure)
    {
        CacheOptions options = new();
        configure?.Invoke(options);

        string clientName = builder.Name;

        // Per-client IMemoryCache — avoids SizeLimit conflicts across named clients.
        builder.Services.TryAdd(
            ServiceDescriptor.KeyedSingleton<IMemoryCache>(clientName, (_, _) =>
            {
                MemoryCacheOptions memOpts = new();
                if (options.MaxCacheSize is long sizeLimit)
                {
                    memOpts.SizeLimit = sizeLimit;
                }

                return new MemoryCache(memOpts);
            }));

        // Per-client cache key builder — NormalizeQueryParameters can differ per client.
        builder.Services.TryAdd(
            ServiceDescriptor.KeyedSingleton<ICacheKeyBuilder>(clientName, (_, _) =>
                new DefaultCacheKeyBuilder(options.NormalizeQueryParameters)));

        // Per-client cache store backed by the client's own IMemoryCache.
        builder.Services.TryAdd(
            ServiceDescriptor.KeyedSingleton<ICacheStore>(clientName, (sp, _) =>
                new MemoryCacheStore(sp.GetRequiredKeyedService<IMemoryCache>(clientName))));

        // Backward compatibility: non-keyed resolution returns the first-registered client's services.
        builder.Services.TryAddSingleton<ICacheKeyBuilder>(sp =>
            sp.GetRequiredKeyedService<ICacheKeyBuilder>(clientName));
        builder.Services.TryAddSingleton<ICacheStore>(sp =>
            sp.GetRequiredKeyedService<ICacheStore>(clientName));

        _ = builder.AddHttpMessageHandler(sp =>
            new CachingMiddleware(
                sp.GetRequiredKeyedService<ICacheStore>(clientName),
                sp.GetRequiredKeyedService<ICacheKeyBuilder>(clientName),
                options,
                sp.GetService<CoalesceHttpMetrics>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<CachingMiddleware>(),
                sp.GetService<TimeProvider>()));
    }
}

