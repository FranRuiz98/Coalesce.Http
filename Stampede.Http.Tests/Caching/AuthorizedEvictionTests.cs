using Stampede.Http.Caching;
using Stampede.Http.Extensions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;

namespace Stampede.Http.Tests.Caching;

/// <summary>
/// Verifies credential-scoped eviction:
/// <see cref="IStampedeHttpCache.EvictAsync(Uri, AuthenticationHeaderValue, CancellationToken)"/> resolves
/// the same credential-scoped key an authenticated GET stores under when
/// <see cref="CacheOptions.AuthorizationCaching"/> is enabled — a key the plain URI overload cannot reach.
/// </summary>
public sealed class AuthorizedEvictionTests
{
    private const string Url = "https://api.test/authorized/evict";

    private static readonly AuthenticationHeaderValue TokenA = new("Bearer", "token-a");
    private static readonly AuthenticationHeaderValue TokenB = new("Bearer", "token-b");

    private static (ServiceProvider Provider, Func<int> BackendCalls) BuildClient()
    {
        ServiceCollection services = new();
        int backendCalls = 0;

        services.AddHttpClient("catalog")
            .AddStampedeHttp(o =>
            {
                o.DefaultTtl = TimeSpan.FromMinutes(5);
                o.AuthorizationCaching = AuthorizationCachingMode.Always;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new TestHandler(() =>
            {
                Interlocked.Increment(ref backendCalls);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") });
            }));

        return (services.BuildServiceProvider(), () => backendCalls);
    }

    private static async Task<HttpResponseMessage> GetWithAuthAsync(HttpClient client, AuthenticationHeaderValue auth)
    {
        HttpRequestMessage request = new(HttpMethod.Get, Url);
        request.Headers.Authorization = auth;
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PlainUriEviction_CannotReachCredentialScopedEntry()
    {
        (ServiceProvider sp, Func<int> backendCalls) = BuildClient();
        HttpClient client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("catalog");

        _ = await GetWithAuthAsync(client, TokenA);
        _ = await GetWithAuthAsync(client, TokenA);
        backendCalls().Should().Be(1, "the authorized response is cached under its credential-scoped key");

        await sp.GetRequiredKeyedService<IStampedeHttpCache>("catalog")
            .EvictAsync(new Uri(Url), TestContext.Current.CancellationToken);

        _ = await GetWithAuthAsync(client, TokenA);
        backendCalls().Should().Be(1, "the URI overload resolves the unauthenticated key and must not touch per-credential entries");
    }

    [Fact]
    public async Task CredentialScopedEviction_ForcesNextAuthorizedRequestBackToOrigin()
    {
        (ServiceProvider sp, Func<int> backendCalls) = BuildClient();
        HttpClient client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("catalog");

        _ = await GetWithAuthAsync(client, TokenA);
        _ = await GetWithAuthAsync(client, TokenA);
        backendCalls().Should().Be(1);

        await sp.GetRequiredKeyedService<IStampedeHttpCache>("catalog")
            .EvictAsync(new Uri(Url), TokenA, TestContext.Current.CancellationToken);

        _ = await GetWithAuthAsync(client, TokenA);
        backendCalls().Should().Be(2, "evicting with the request's own Authorization value must reach its credential-scoped entry");
    }

    [Fact]
    public async Task CredentialScopedEviction_LeavesOtherCredentialsEntriesIntact()
    {
        (ServiceProvider sp, Func<int> backendCalls) = BuildClient();
        HttpClient client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("catalog");

        _ = await GetWithAuthAsync(client, TokenA);
        _ = await GetWithAuthAsync(client, TokenB);
        backendCalls().Should().Be(2, "each credential gets its own independent entry");

        await sp.GetRequiredKeyedService<IStampedeHttpCache>("catalog")
            .EvictAsync(new Uri(Url), TokenA, TestContext.Current.CancellationToken);

        _ = await GetWithAuthAsync(client, TokenB);
        backendCalls().Should().Be(2, "evicting credential A must not disturb credential B's entry");

        _ = await GetWithAuthAsync(client, TokenA);
        backendCalls().Should().Be(3, "credential A's entry was evicted");
    }

    [Fact]
    public async Task EvictAsync_NullArguments_Throw()
    {
        StampedeHttpCache cache = new(
            new MemoryCacheStore(new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())),
            new DefaultCacheKeyBuilder());

        Func<Task> nullUri = async () => await cache.EvictAsync(null!, TokenA, TestContext.Current.CancellationToken);
        Func<Task> nullAuth = async () => await cache.EvictAsync(new Uri(Url), null!, TestContext.Current.CancellationToken);

        await nullUri.Should().ThrowAsync<ArgumentNullException>();
        await nullAuth.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task DefaultInterfaceImplementation_ThrowsNotSupported_ForPre26Implementations()
    {
        // A custom IStampedeHttpCache written before 2.6 implements only the URI overload; the new
        // members must keep it compiling and fail loudly (not silently no-op) when called.
        IStampedeHttpCache legacy = new LegacyCache();

        Func<Task> credentialScoped = async () => await legacy.EvictAsync(new Uri(Url), TokenA, TestContext.Current.CancellationToken);
        Func<Task> byTag = async () => await legacy.EvictByTagAsync("products", TestContext.Current.CancellationToken);

        await credentialScoped.Should().ThrowAsync<NotSupportedException>();
        await byTag.Should().ThrowAsync<NotSupportedException>();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class LegacyCache : IStampedeHttpCache
    {
        public ValueTask EvictAsync(Uri uri, CancellationToken ct = default) => default;
    }

    private sealed class TestHandler(Func<Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responseFactory();
    }
}
