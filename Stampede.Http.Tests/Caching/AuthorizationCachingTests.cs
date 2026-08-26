using Stampede.Http.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Http.Headers;

namespace Stampede.Http.Tests.Caching;

/// <summary>
/// Verifies <see cref="CacheOptions.AuthorizationCaching"/> (RFC 9111 §3.5): whether requests carrying an
/// <c>Authorization</c> header are cached, and that different credentials are never cross-served.
/// </summary>
public sealed class AuthorizationCachingTests
{
    private readonly ICacheStore _cache;
    private readonly DefaultCacheKeyBuilder _keyBuilder;

    public AuthorizationCachingTests()
    {
        _cache = new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions()));
        _keyBuilder = new DefaultCacheKeyBuilder();
    }

    private (CachingMiddleware middleware, StubTransport stub) BuildPipeline(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        CacheOptions? options = null)
    {
        options ??= new CacheOptions { DefaultTtl = TimeSpan.FromMinutes(5) };
        StubTransport stub = new(handler);
        CachingMiddleware middleware = new(_cache, _keyBuilder, options) { InnerHandler = stub };
        return (middleware, stub);
    }

    private static HttpRequestMessage AuthorizedReq(string url, string token = "user-a-token") =>
        new(HttpMethod.Get, url) { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) } };

    // ── Never (default) ───────────────────────────────────────────────────────

    [Fact]
    public async Task Never_AuthorizedRequest_NeverCached()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") };
        });

        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(AuthorizedReq("https://api.test/auth-never"), CancellationToken.None);
        _ = await invoker.SendAsync(AuthorizedReq("https://api.test/auth-never"), CancellationToken.None);

        callCount.Should().Be(2, "the default AuthorizationCachingMode.Never must match pre-2.4 behavior exactly");
    }

    [Fact]
    public void Never_IsTheDefault()
    {
        new CacheOptions().AuthorizationCaching.Should().Be(AuthorizationCachingMode.Never);
    }

    // ── WhenPermittedByResponse ───────────────────────────────────────────────

    [Fact]
    public async Task WhenPermittedByResponse_NoPermittingDirective_NotCached()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") };
        }, new CacheOptions
        {
            DefaultTtl = TimeSpan.FromMinutes(5),
            AuthorizationCaching = AuthorizationCachingMode.WhenPermittedByResponse
        });

        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(AuthorizedReq("https://api.test/auth-unpermitted"), CancellationToken.None);
        _ = await invoker.SendAsync(AuthorizedReq("https://api.test/auth-unpermitted"), CancellationToken.None);

        callCount.Should().Be(2, "a response with no public/must-revalidate/s-maxage directive must not be cached for an authorized request");
    }

    [Theory]
    [InlineData("public, max-age=60")]
    [InlineData("must-revalidate, max-age=60")]
    [InlineData("s-maxage=60")]
    public async Task WhenPermittedByResponse_PermittingDirective_IsCached(string cacheControlHeader)
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent("body") };
            r.Headers.CacheControl = CacheControlHeaderValue.Parse(cacheControlHeader);
            return r;
        }, new CacheOptions
        {
            DefaultTtl = TimeSpan.FromMinutes(5),
            AuthorizationCaching = AuthorizationCachingMode.WhenPermittedByResponse
        });

        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(AuthorizedReq("https://api.test/auth-permitted"), CancellationToken.None);
        _ = await invoker.SendAsync(AuthorizedReq("https://api.test/auth-permitted"), CancellationToken.None);

        callCount.Should().Be(1, $"'{cacheControlHeader}' explicitly permits caching an authorized response per RFC 9111 §3.5");
    }

    // ── Always ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Always_NoPermittingDirective_StillCached()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") };
        }, new CacheOptions
        {
            DefaultTtl = TimeSpan.FromMinutes(5),
            AuthorizationCaching = AuthorizationCachingMode.Always
        });

        HttpMessageInvoker invoker = new(middleware);

        _ = await invoker.SendAsync(AuthorizedReq("https://api.test/auth-always"), CancellationToken.None);
        _ = await invoker.SendAsync(AuthorizedReq("https://api.test/auth-always"), CancellationToken.None);

        callCount.Should().Be(1, "Always caches any otherwise-cacheable response regardless of response permission directives");
    }

    // ── Credential isolation ──────────────────────────────────────────────────

    [Fact]
    public async Task Always_DifferentCredentials_NeverCrossServed()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            string token = req.Headers.Authorization!.Parameter!;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"response-for-{token}") };
        }, new CacheOptions
        {
            DefaultTtl = TimeSpan.FromMinutes(5),
            AuthorizationCaching = AuthorizationCachingMode.Always
        });

        HttpMessageInvoker invoker = new(middleware);

        HttpResponseMessage responseA1 = await invoker.SendAsync(AuthorizedReq("https://api.test/auth-isolated", "token-a"), CancellationToken.None);
        HttpResponseMessage responseB1 = await invoker.SendAsync(AuthorizedReq("https://api.test/auth-isolated", "token-b"), CancellationToken.None);

        callCount.Should().Be(2, "two different credentials must each independently miss and hit the origin");
        (await responseA1.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("response-for-token-a");
        (await responseB1.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("response-for-token-b");

        // Second round: each credential must hit its OWN cached entry, never the other's.
        HttpResponseMessage responseA2 = await invoker.SendAsync(AuthorizedReq("https://api.test/auth-isolated", "token-a"), CancellationToken.None);
        HttpResponseMessage responseB2 = await invoker.SendAsync(AuthorizedReq("https://api.test/auth-isolated", "token-b"), CancellationToken.None);

        callCount.Should().Be(2, "both credentials must now be served from their own cache entry, no further origin calls");
        (await responseA2.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("response-for-token-a");
        (await responseB2.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("response-for-token-b");
    }

    [Fact]
    public async Task Always_UnauthorizedAndAuthorizedRequests_NeverCrossServed()
    {
        int callCount = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(req =>
        {
            callCount++;
            string body = req.Headers.Authorization is null ? "public-response" : "private-response";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }, new CacheOptions
        {
            DefaultTtl = TimeSpan.FromMinutes(5),
            AuthorizationCaching = AuthorizationCachingMode.Always
        });

        HttpMessageInvoker invoker = new(middleware);

        HttpResponseMessage anon = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/auth-mixed"), CancellationToken.None);
        HttpResponseMessage auth = await invoker.SendAsync(AuthorizedReq("https://api.test/auth-mixed"), CancellationToken.None);

        callCount.Should().Be(2);
        (await anon.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("public-response");
        (await auth.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("private-response");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class StubTransport(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
