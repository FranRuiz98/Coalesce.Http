using Stampede.Http.Caching;
using Stampede.Http.Extensions;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using System.Net;

namespace Stampede.Http.Tests.Caching;

/// <summary>
/// Verifies <see cref="IStampedeHttpCache"/> — programmatic eviction of a named client's cached GET
/// responses — both as a standalone unit and as wired up by <c>AddStampedeHttp()</c>.
/// </summary>
public sealed class StampedeHttpCacheTests
{
    // ── Unit-level: StampedeHttpCache directly ───────────────────────────────

    [Fact]
    public async Task EvictAsync_NullUri_Throws()
    {
        StampedeHttpCache cache = new(new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions())), new DefaultCacheKeyBuilder());

        Func<Task> act = async () => await cache.EvictAsync(null!, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task EvictAsync_NoEntryExists_DoesNotThrow()
    {
        StampedeHttpCache cache = new(new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions())), new DefaultCacheKeyBuilder());

        Func<Task> act = async () => await cache.EvictAsync(new Uri("https://api.test/never-cached"), TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync("eviction is idempotent — nothing cached is a no-op, not an error");
    }

    [Fact]
    public async Task EvictAsync_RemovesTheSameKeyCachingMiddlewareWouldUse()
    {
        ICacheStore store = new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions()));
        DefaultCacheKeyBuilder keyBuilder = new();
        StampedeHttpCache cache = new(store, keyBuilder);

        Uri uri = new("https://api.test/direct-evict");
        string key = keyBuilder.Build(new HttpRequestMessage(HttpMethod.Get, uri));

        store.Set(key, new CacheEntry
        {
            StatusCode = 200,
            Body = "cached"u8.ToArray(),
            Headers = new Dictionary<string, string[]>(),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            StoredAt = DateTimeOffset.UtcNow
        });

        store.TryGetValue(key, out CacheEntry? beforeEvict).Should().BeTrue();
        beforeEvict.Should().NotBeNull();

        await cache.EvictAsync(uri, TestContext.Current.CancellationToken);

        store.TryGetValue(key, out CacheEntry? afterEvict).Should().BeFalse();
        afterEvict.Should().BeNull();
    }

    // ── DI-level: AddStampedeHttp() wiring ────────────────────────────────────

    [Fact]
    public async Task Evict_CausesNextRequestToHitOrigin()
    {
        ServiceCollection services = new();
        int backendCalls = 0;
        const string url = "https://api.test/evict-di";

        services.AddHttpClient("catalog")
            .AddStampedeHttp(o => o.DefaultTtl = TimeSpan.FromMinutes(5))
            .ConfigurePrimaryHttpMessageHandler(() => new TestHandler(() =>
            {
                Interlocked.Increment(ref backendCalls);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") });
            }));

        ServiceProvider sp = services.BuildServiceProvider();
        HttpClient client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("catalog");

        _ = await client.GetAsync(url, TestContext.Current.CancellationToken);
        _ = await client.GetAsync(url, TestContext.Current.CancellationToken);
        backendCalls.Should().Be(1, "the second request must be served from cache before eviction");

        IStampedeHttpCache cacheManager = sp.GetRequiredKeyedService<IStampedeHttpCache>("catalog");
        await cacheManager.EvictAsync(new Uri(url), TestContext.Current.CancellationToken);

        _ = await client.GetAsync(url, TestContext.Current.CancellationToken);
        backendCalls.Should().Be(2, "eviction must force the next request back to the origin");
    }

    [Fact]
    public async Task Evict_IsolatedBetweenNamedClients()
    {
        ServiceCollection services = new();
        int callsA = 0, callsB = 0;
        const string sharedUrl = "https://api.test/evict-shared";

        services.AddHttpClient("a")
            .AddStampedeHttp(o => o.DefaultTtl = TimeSpan.FromMinutes(5))
            .ConfigurePrimaryHttpMessageHandler(() => new TestHandler(() =>
            {
                Interlocked.Increment(ref callsA);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("a") });
            }));

        services.AddHttpClient("b")
            .AddStampedeHttp(o => o.DefaultTtl = TimeSpan.FromMinutes(5))
            .ConfigurePrimaryHttpMessageHandler(() => new TestHandler(() =>
            {
                Interlocked.Increment(ref callsB);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("b") });
            }));

        ServiceProvider sp = services.BuildServiceProvider();
        IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();

        _ = await factory.CreateClient("a").GetAsync(sharedUrl, TestContext.Current.CancellationToken);
        _ = await factory.CreateClient("b").GetAsync(sharedUrl, TestContext.Current.CancellationToken);
        callsA.Should().Be(1);
        callsB.Should().Be(1);

        // Evict only client "a"'s entry for the shared URL.
        await sp.GetRequiredKeyedService<IStampedeHttpCache>("a").EvictAsync(new Uri(sharedUrl), TestContext.Current.CancellationToken);

        _ = await factory.CreateClient("a").GetAsync(sharedUrl, TestContext.Current.CancellationToken);
        callsA.Should().Be(2, "client a's entry was evicted");

        _ = await factory.CreateClient("b").GetAsync(sharedUrl, TestContext.Current.CancellationToken);
        callsB.Should().Be(1, "client b's cache must be untouched by client a's eviction");
    }

    [Fact]
    public async Task Evict_NoEntryCached_DoesNotThrow_AndSubsequentRequestStillCaches()
    {
        ServiceCollection services = new();
        int backendCalls = 0;

        services.AddHttpClient("catalog")
            .AddStampedeHttp(o => o.DefaultTtl = TimeSpan.FromMinutes(5))
            .ConfigurePrimaryHttpMessageHandler(() => new TestHandler(() =>
            {
                Interlocked.Increment(ref backendCalls);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") });
            }));

        ServiceProvider sp = services.BuildServiceProvider();
        IStampedeHttpCache cacheManager = sp.GetRequiredKeyedService<IStampedeHttpCache>("catalog");

        Func<Task> act = async () => await cacheManager.EvictAsync(new Uri("https://api.test/never-populated"), TestContext.Current.CancellationToken);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void IStampedeHttpCache_ResolvesViaNonKeyedFallback()
    {
        ServiceCollection services = new();
        services.AddHttpClient("only-client").AddStampedeHttp();

        ServiceProvider sp = services.BuildServiceProvider();

        IStampedeHttpCache keyed = sp.GetRequiredKeyedService<IStampedeHttpCache>("only-client");
        IStampedeHttpCache nonKeyed = sp.GetRequiredService<IStampedeHttpCache>();

        nonKeyed.Should().BeSameAs(keyed, "non-keyed resolution falls back to the first-registered client, matching ICacheStore/ICacheKeyBuilder");
    }

    [Fact]
    public async Task Evict_WorksWithDistributedCacheStore()
    {
        ServiceCollection services = new();
        services.AddSingleton<IDistributedCache>(
            new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())));

        int backendCalls = 0;
        const string url = "https://api.test/evict-distributed";

        services.AddHttpClient("catalog")
            .AddStampedeHttp(o => o.DefaultTtl = TimeSpan.FromMinutes(5))
            .UseDistributedCacheStore()
            .ConfigurePrimaryHttpMessageHandler(() => new TestHandler(() =>
            {
                Interlocked.Increment(ref backendCalls);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") });
            }));

        ServiceProvider sp = services.BuildServiceProvider();
        HttpClient client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("catalog");

        _ = await client.GetAsync(url, TestContext.Current.CancellationToken);
        _ = await client.GetAsync(url, TestContext.Current.CancellationToken);
        backendCalls.Should().Be(1);

        await sp.GetRequiredKeyedService<IStampedeHttpCache>("catalog").EvictAsync(new Uri(url), TestContext.Current.CancellationToken);

        _ = await client.GetAsync(url, TestContext.Current.CancellationToken);
        backendCalls.Should().Be(2, "eviction must work against a DistributedCacheStore too, resolved lazily at first use");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class TestHandler(Func<Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responseFactory();
    }
}
