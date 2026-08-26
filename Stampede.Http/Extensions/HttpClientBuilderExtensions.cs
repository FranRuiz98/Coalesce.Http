using Stampede.Http.Caching;
using Stampede.Http.Coalescing;
using Stampede.Http.Handlers;
using Stampede.Http.Metrics;
using Stampede.Http.Options;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stampede.Http.Extensions;

/// <summary>
/// Extension methods for <see cref="IHttpClientBuilder"/> that add Stampede.Http caching
/// and request-coalescing handlers to the <see cref="HttpClient"/> pipeline.
/// </summary>
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
    ///     .AddStampedeHttp()
    ///     .AddResilienceHandler("resilience", b =>
    ///     {
    ///         b.AddRetry(new HttpRetryStrategyOptions { MaxRetryAttempts = 3 });
    ///     });
    ///
    /// // Hedging example
    /// services.AddHttpClient("catalog")
    ///     .AddStampedeHttp()
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
    /// <para>
    /// To normalize query-parameter ordering so that <c>/items?b=2&amp;a=1</c> and
    /// <c>/items?a=1&amp;b=2</c> share the same cache entry, enable
    /// <see cref="CacheOptions.NormalizeQueryParameters"/>:
    /// </para>
    /// <code>
    /// services.AddHttpClient("catalog")
    ///     .AddStampedeHttp(cache => cache.NormalizeQueryParameters = true);
    /// </code>
    /// <param name="builder">The <see cref="IHttpClientBuilder"/> to configure.</param>
    /// <param name="configureCaching">An optional action to configure <see cref="CacheOptions"/>.</param>
    /// <param name="configureCoalescing">An optional action to configure <see cref="CoalescerOptions"/>.</param>
    public static IHttpClientBuilder AddStampedeHttp(
        this IHttpClientBuilder builder,
        Action<CacheOptions>? configureCaching = null,
        Action<CoalescerOptions>? configureCoalescing = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<StampedeHttpMetrics>();

        AddHttpCache(builder, configureCaching);

        if (configureCoalescing is not null)
        {
            _ = builder.Services.Configure<CoalescerOptions>(builder.Name, configureCoalescing);
        }

        AddCoalescing(builder);

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

        builder.Services.TryAddSingleton<StampedeHttpMetrics>();
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

        builder.Services.TryAddSingleton<StampedeHttpMetrics>();

        if (configure is not null)
        {
            _ = builder.Services.Configure<CoalescerOptions>(builder.Name, configure);
        }

        AddCoalescing(builder);

        return builder;
    }

    /// <summary>
    /// Replaces the default in-memory <see cref="ICacheStore"/> with a <see cref="DistributedCacheStore"/>
    /// backed by <see cref="IDistributedCache"/>, enabling shared caching across multiple instances.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this method <b>after</b> <c>AddStampedeHttp()</c> or <c>AddCachingOnly()</c> and after
    /// registering an <see cref="IDistributedCache"/> implementation (e.g., Redis, SQL Server):
    /// </para>
    /// <code>
    /// services.AddStackExchangeRedisCache(o => o.Configuration = "localhost:6379");
    ///
    /// services.AddHttpClient("catalog")
    ///     .AddStampedeHttp()
    ///     .UseDistributedCacheStore();
    /// </code>
    /// <para>
    /// Cache entries are serialized with <see cref="System.Text.Json"/> and the
    /// <c>AbsoluteExpiration</c> is derived from <see cref="Caching.CacheEntry.ExpiresAt"/> — extended by
    /// the stale windows and, for entries with a validator, <see cref="CacheOptions.RevalidationGraceSeconds"/> —
    /// so the backing store evicts entries automatically once they can no longer be served or revalidated.
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
            _ = builder.Services.Remove(existingKeyed);
        }

        // Remove the non-keyed fallback so it can be re-registered pointing to the new store.
        ServiceDescriptor? existingNonKeyed = builder.Services.FirstOrDefault(
            d => !d.IsKeyedService && d.ServiceType == typeof(ICacheStore));

        if (existingNonKeyed is not null)
        {
            _ = builder.Services.Remove(existingNonKeyed);
        }

        _ = builder.Services.AddKeyedSingleton<ICacheStore>(clientName, (sp, _) =>
            new DistributedCacheStore(
                sp.GetRequiredService<IDistributedCache>(),
                sp.GetRequiredService<IOptionsMonitor<CacheOptions>>().Get(clientName)));

        _ = builder.Services.AddSingleton<ICacheStore>(sp =>
            sp.GetRequiredKeyedService<ICacheStore>(clientName));

        return builder;
    }

    private static void AddCoalescing(IHttpClientBuilder builder)
    {
        string clientName = builder.Name;

        // Each call captures its own RequestCoalescer in the closure so that
        // duplicate AddStampedeHttp() calls produce independent coalescing scopes
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
                        sp.GetRequiredService<IOptionsMonitor<CoalescerOptions>>(),
                        clientName,
                        sp.GetService<StampedeHttpMetrics>(),
                        sp.GetService<ILoggerFactory>()?.CreateLogger<RequestCoalescer>());
                }
            }

            return new CoalescingHandler(
                coalescer,
                sp.GetRequiredService<IOptionsMonitor<CoalescerOptions>>(),
                clientName,
                sp.GetService<ILoggerFactory>()?.CreateLogger<CoalescingHandler>());
        });
    }

    private static void AddHttpCache(IHttpClientBuilder builder, Action<CacheOptions>? configure)
    {
        string clientName = builder.Name;

        // Register named options for runtime reconfiguration via IOptionsMonitor<CacheOptions>.
        if (configure is not null)
        {
            _ = builder.Services.Configure<CacheOptions>(clientName, configure);
        }

        // Read structural options eagerly — MaxCacheSize, NormalizeQueryParameters and
        // RevalidationGraceSeconds configure IMemoryCache, ICacheKeyBuilder and ICacheStore
        // at creation time and cannot change at runtime.
        CacheOptions structuralOptions = new();
        configure?.Invoke(structuralOptions);

        // Per-client IMemoryCache — avoids SizeLimit conflicts across named clients.
        builder.Services.TryAdd(
            ServiceDescriptor.KeyedSingleton<IMemoryCache>(clientName, (_, _) =>
            {
                MemoryCacheOptions memOpts = new();
                if (structuralOptions.MaxCacheSize is long sizeLimit)
                {
                    memOpts.SizeLimit = sizeLimit;
                }

                return new MemoryCache(memOpts);
            }));

        // Per-client cache key builder — NormalizeQueryParameters can differ per client.
        builder.Services.TryAdd(
            ServiceDescriptor.KeyedSingleton<ICacheKeyBuilder>(clientName, (_, _) =>
                new DefaultCacheKeyBuilder(structuralOptions.NormalizeQueryParameters)));

        // Per-client cache store backed by the client's own IMemoryCache.
        builder.Services.TryAdd(
            ServiceDescriptor.KeyedSingleton<ICacheStore>(clientName, (sp, _) =>
                new MemoryCacheStore(sp.GetRequiredKeyedService<IMemoryCache>(clientName), structuralOptions)));

        // Per-client stale-while-revalidate deduplication. This must outlive the handler: IHttpClientFactory
        // rotates handler chains, so state held by CachingMiddleware would let two live chains revalidate the
        // same key at once.
        builder.Services.TryAdd(
            ServiceDescriptor.KeyedSingleton<BackgroundRevalidationCoordinator>(clientName, (_, _) => new()));

        // Per-client programmatic eviction (IStampedeHttpCache) — a thin wrapper over the two
        // registrations above, resolved lazily so it always reflects whichever ICacheStore is current
        // (e.g. after UseDistributedCacheStore() swaps it post-registration).
        builder.Services.TryAdd(
            ServiceDescriptor.KeyedSingleton<IStampedeHttpCache>(clientName, (sp, _) =>
                new StampedeHttpCache(
                    sp.GetRequiredKeyedService<ICacheStore>(clientName),
                    sp.GetRequiredKeyedService<ICacheKeyBuilder>(clientName))));

        // Backward compatibility: non-keyed resolution returns the first-registered client's services.
        builder.Services.TryAddSingleton<ICacheKeyBuilder>(sp =>
            sp.GetRequiredKeyedService<ICacheKeyBuilder>(clientName));
        builder.Services.TryAddSingleton<ICacheStore>(sp =>
            sp.GetRequiredKeyedService<ICacheStore>(clientName));
        builder.Services.TryAddSingleton<IStampedeHttpCache>(sp =>
            sp.GetRequiredKeyedService<IStampedeHttpCache>(clientName));

        _ = builder.AddHttpMessageHandler(sp =>
            new CachingMiddleware(
                sp.GetRequiredKeyedService<ICacheStore>(clientName),
                sp.GetRequiredKeyedService<ICacheKeyBuilder>(clientName),
                sp.GetRequiredService<IOptionsMonitor<CacheOptions>>(),
                clientName,
                sp.GetRequiredKeyedService<BackgroundRevalidationCoordinator>(clientName),
                sp.GetService<StampedeHttpMetrics>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<CachingMiddleware>(),
                sp.GetService<TimeProvider>()));
    }
}
