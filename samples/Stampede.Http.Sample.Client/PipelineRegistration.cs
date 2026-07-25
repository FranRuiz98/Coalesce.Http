using Microsoft.Extensions.Http.Resilience;
using Polly;
using Stampede.Http.Caching;
using Stampede.Http.Extensions;
using Stampede.Http.Options;

namespace Stampede.Http.Sample.Client;

/// <summary>
/// Assembles the outbound pipeline. This is the only file in the client that knows
/// Stampede.Http exists.
/// </summary>
public static class PipelineRegistration
{
    /// <summary>Named <see cref="HttpClient"/> the sample uses; also the options key for both option classes.</summary>
    public const string ClientName = "origin";

    /// <summary>
    /// Registers <see cref="OriginClient"/> and its handler pipeline:
    /// <code>
    /// CachingMiddleware      ← RFC 9111, entries shared across instances via Redis
    ///   └─ CoalescingHandler ← per-process request deduplication
    ///        └─ Polly        ← retry with exponential backoff + per-attempt timeout
    ///             └─ SocketsHttpHandler
    /// </code>
    /// When <see cref="PipelineOptions.Enabled"/> is <see langword="false"/> the two Stampede.Http
    /// handlers are omitted and everything else stays identical — that instance is the control group.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration, used for hot-reloadable option overrides.</param>
    /// <param name="options">Eagerly bound sample options.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddOriginClient(
        this IServiceCollection services,
        IConfiguration configuration,
        SampleOptions options)
    {
        PipelineOptions pipeline = options.Pipeline;

        IHttpClientBuilder builder = services.AddHttpClient<OriginClient>(ClientName, http =>
        {
            http.BaseAddress = new Uri(options.ApiBaseUrl);

            // Lets the origin attribute its load to a specific client instance, which is what
            // makes the "with vs without Stampede.Http" dashboard panel possible.
            http.DefaultRequestHeaders.Add("X-Client", options.Instance);
        });

        if (pipeline.Enabled)
        {
            _ = builder.AddStampedeHttp(
                configureCaching: cache =>
                {
                    cache.DefaultTtl = pipeline.DefaultTtl;

                    // Structural options: read once, when the store and key builder are created.
                    cache.NormalizeQueryParameters = pipeline.NormalizeQueryParameters;
                    cache.MaxCacheSize = pipeline.MaxCacheSize;
                },
                configureCoalescing: coalescing =>
                {
                    coalescing.CoalescingTimeout = pipeline.CoalescingTimeout;

                    // One URL, many tenants: keep each tenant's burst in its own coalescing group.
                    coalescing.CoalesceKeyHeaders = pipeline.CoalesceKeyHeaders;

                    // Deliberately above CacheOptions.MaxBodySizeBytes — see the remarks on
                    // PipelineOptions.MaxResponseBodyBytes for why the two limits differ.
                    coalescing.MaxResponseBodyBytes = pipeline.MaxResponseBodyBytes;
                });

            // Runtime-tuneable overrides, layered on top of the lambdas above. `config/` is a
            // mounted directory watched with reloadOnChange, so editing DefaultTtl there takes
            // effect on the next request — no restart. Structural options are not reloadable.
            _ = services.Configure<CacheOptions>(ClientName, configuration.GetSection("Stampede:Cache"));
            _ = services.Configure<CoalescerOptions>(ClientName, configuration.GetSection("Stampede:Coalescing"));

            if (pipeline.UseRedis && !string.IsNullOrWhiteSpace(options.RedisConnection))
            {
                _ = services.AddStackExchangeRedisCache(redis => redis.Configuration = options.RedisConnection);
                _ = builder.UseDistributedCacheStore();
            }
        }

        // Polly goes on last so it sits BELOW the coalescer: a retry storm is coalesced too.
        // The baseline instance gets the identical resilience pipeline, so the only difference
        // measured between instances is Stampede.Http itself.
        _ = builder.AddResilienceHandler("origin-resilience", resilience =>
        {
            _ = resilience.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromMilliseconds(200),
                BackoffType = DelayBackoffType.Exponential,
            });

            _ = resilience.AddTimeout(TimeSpan.FromSeconds(5));
        });

        return services;
    }
}
