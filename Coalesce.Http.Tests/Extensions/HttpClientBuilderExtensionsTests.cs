using Coalesce.Http.Caching;
using Coalesce.Http.Extensions;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Coalesce.Http.Tests.Extensions;

public class HttpClientBuilderExtensionsTests
{
    // ── AddCoalesceHttp ───────────────────────────────────────────────────────

    [Fact]
    public async Task AddCoalesceHttp_RegistersPipelineCorrectly()
    {
        var services = new ServiceCollection();
        int backendCalls = 0;

        services.AddHttpClient("test").AddCoalesceHttp()
            .ConfigurePrimaryHttpMessageHandler(() => new TestHandler(() =>
            {
                Interlocked.Increment(ref backendCalls);
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("ok")
                });
            }));

        var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("test");

        var response = await client.GetAsync("https://api.test/resource");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        backendCalls.Should().Be(1);
    }

    [Fact]
    public void AddCoalesceHttp_ReturnsBuilder_ForChaining()
    {
        var services = new ServiceCollection();
        var builder = services.AddHttpClient("test");

        var result = builder.AddCoalesceHttp();

        result.Should().BeSameAs(builder);
    }

    [Fact]
    public void AddCoalesceHttp_AcceptsCacheOptionsConfigure()
    {
        var services = new ServiceCollection();

        var act = () => services
            .AddHttpClient("test")
            .AddCoalesceHttp(o => o.DefaultTtl = TimeSpan.FromMinutes(10));

        act.Should().NotThrow();
    }

    [Fact]
    public async Task AddCoalesceHttp_CacheHit_DoesNotReachCoalescer()
    {
        // Verify that a fresh cache hit is served entirely within CachingMiddleware
        // and does NOT create an inflight entry in CoalescingHandler.
        var services = new ServiceCollection();
        int backendCallCount = 0;

        services
            .AddHttpClient("test")
            .AddCoalesceHttp(o => o.DefaultTtl = TimeSpan.FromMinutes(5))
            .ConfigurePrimaryHttpMessageHandler(() => new TestHandler(() =>
            {
                Interlocked.Increment(ref backendCallCount);
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("cached-body")
                });
            }));

        var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("test");

        // First request populates the cache
        _ = await client.GetAsync("https://api.test/resource");
        // Second request must be served from cache — backend not called again
        _ = await client.GetAsync("https://api.test/resource");

        backendCallCount.Should().Be(1, "the second request must be served from CachingMiddleware without reaching the backend");
    }

    [Fact]
    public async Task AddCoalesceHttp_CacheMiss_CoalescesIdenticalConcurrentRequests()
    {
        // Verify that on a cache miss, concurrent identical requests are collapsed into one
        // backend call by CoalescingHandler, then all callers receive a response.
        var services = new ServiceCollection();
        int backendCallCount = 0;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        services
            .AddHttpClient("test")
            .AddCoalesceHttp()
            .ConfigurePrimaryHttpMessageHandler(() => new TestHandler(async () =>
            {
                Interlocked.Increment(ref backendCallCount);
                await gate.Task;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("body")
                };
            }));

        var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("test");

        var t1 = client.GetAsync("https://api.test/item");
        var t2 = client.GetAsync("https://api.test/item");
        var t3 = client.GetAsync("https://api.test/item");

        await Task.Delay(50); // allow all three to reach the coalescer
        gate.SetResult(true);

        var responses = await Task.WhenAll(t1, t2, t3);

        backendCallCount.Should().Be(1, "CoalescingHandler must collapse concurrent misses into one backend call");
        responses.Should().OnlyContain(r => r.StatusCode == System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task AddCoalesceHttp_WithMultipleClients_PipelinesAreIsolated()
    {
        var services = new ServiceCollection();
        int countA = 0, countB = 0;

        services.AddHttpClient("a").AddCoalesceHttp()
            .ConfigurePrimaryHttpMessageHandler(() => new TestHandler(() =>
            {
                Interlocked.Increment(ref countA);
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("a") });
            }));

        services.AddHttpClient("b").AddCoalesceHttp()
            .ConfigurePrimaryHttpMessageHandler(() => new TestHandler(() =>
            {
                Interlocked.Increment(ref countB);
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("b") });
            }));

        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

        _ = await factory.CreateClient("a").GetAsync("https://api.test/resource-a");
        _ = await factory.CreateClient("b").GetAsync("https://api.test/resource-b");

        countA.Should().Be(1, "client a has its own isolated pipeline");
        countB.Should().Be(1, "client b has its own isolated pipeline");
    }

    [Fact]
    public async Task AddCoalesceHttp_CalledTwice_PipelineStillWorks()
    {
        var services = new ServiceCollection();
        int backendCalls = 0;
        var builder = services.AddHttpClient("test");

        builder.AddCoalesceHttp();
        builder.AddCoalesceHttp();

        builder.ConfigurePrimaryHttpMessageHandler(() => new TestHandler(() =>
        {
            Interlocked.Increment(ref backendCalls);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            });
        }));

        var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("test");

        var response = await client.GetAsync("https://api.test/resource");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public void AddCoalesceHttp_WithNullBuilder_Throws()
    {
        IHttpClientBuilder builder = null!;

        Action act = () => builder.AddCoalesceHttp();

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddCachingOnly_WithNullBuilder_Throws()
    {
        IHttpClientBuilder builder = null!;

        Action act = () => builder.AddCachingOnly();

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddCoalescingOnly_WithNullBuilder_Throws()
    {
        IHttpClientBuilder builder = null!;

        Action act = () => builder.AddCoalescingOnly();

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void UseDistributedCacheStore_WithNullBuilder_Throws()
    {
        IHttpClientBuilder builder = null!;

        Action act = () => builder.UseDistributedCacheStore();

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void UseDistributedCacheStore_ReplacesMemoryCacheStore_WithDistributedCacheStore()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDistributedCache>(
            new MemoryDistributedCache(
                Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())));

        services.AddHttpClient("test")
            .AddCoalesceHttp()
            .UseDistributedCacheStore();

        var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<ICacheStore>();

        store.Should().BeOfType<DistributedCacheStore>();
    }

    [Fact]
    public void UseDistributedCacheStore_WithoutPriorCacheRegistration_RegistersDistributedCacheStore()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDistributedCache>(
            new MemoryDistributedCache(
                Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())));

        // UseDistributedCacheStore without a prior AddCoalesceHttp/AddCachingOnly
        services.AddHttpClient("test")
            .UseDistributedCacheStore();

        var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<ICacheStore>();

        store.Should().BeOfType<DistributedCacheStore>();
    }

    [Fact]
    public void UseDistributedCacheStore_ReturnsBuilder_ForChaining()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDistributedCache>(
            new MemoryDistributedCache(
                Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())));

        var builder = services.AddHttpClient("test").AddCoalesceHttp();
        var result = builder.UseDistributedCacheStore();

        result.Should().BeSameAs(builder);
    }

    [Fact]
    public async Task UseDistributedCacheStore_CacheHit_ServesResponseWithoutCallingBackend()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDistributedCache>(
            new MemoryDistributedCache(
                Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())));

        int backendCalls = 0;
        services.AddHttpClient("test")
            .AddCoalesceHttp(o => o.DefaultTtl = TimeSpan.FromMinutes(5))
            .UseDistributedCacheStore()
            .ConfigurePrimaryHttpMessageHandler(() => new TestHandler(() =>
            {
                Interlocked.Increment(ref backendCalls);
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("cached")
                });
            }));

        var client = services.BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("test");

        _ = await client.GetAsync("https://api.test/dist");
        _ = await client.GetAsync("https://api.test/dist");

        backendCalls.Should().Be(1, "second request must be served from the distributed cache");
    }

    private class TestHandler : HttpMessageHandler
    {
        private readonly Func<Task<HttpResponseMessage>> _responseFactory;

        public TestHandler(Func<Task<HttpResponseMessage>> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return _responseFactory();
        }
    }
}
