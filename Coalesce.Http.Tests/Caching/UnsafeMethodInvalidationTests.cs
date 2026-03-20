using Coalesce.Http.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using System.Net;

namespace Coalesce.Http.Tests.Caching;

/// <summary>
/// Verifies RFC 9111 §4.4 — cache invalidation triggered by successful unsafe method responses.
/// </summary>
public sealed class UnsafeMethodInvalidationTests
{
    private readonly ICacheStore _cache;
    private readonly DefaultCacheKeyBuilder _keyBuilder;
    private readonly CacheOptions _options;

    public UnsafeMethodInvalidationTests()
    {
        _cache = new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions()));
        _keyBuilder = new DefaultCacheKeyBuilder();
        _options = new CacheOptions { DefaultTtl = TimeSpan.FromMinutes(5) };
    }

    // ── §4.4 MUST — effective request URI ─────────────────────────────────────

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task SuccessfulUnsafeMethod_InvalidatesCachedGetForSameUri(string method)
    {
        int callCount = 0;
        CachingMiddleware middleware = BuildMiddleware(req =>
        {
            if (req.Method == HttpMethod.Get) callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") };
        });
        HttpMessageInvoker invoker = new(middleware);

        // Populate GET cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/items"), CancellationToken.None);
        callCount.Should().Be(1);

        // Verify cache hit
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/items"), CancellationToken.None);
        callCount.Should().Be(1, "second GET should be served from cache");

        // Unsafe method succeeds → should invalidate the GET entry
        _ = await invoker.SendAsync(new HttpRequestMessage(new HttpMethod(method), "https://api.test/items"), CancellationToken.None);

        // Next GET should miss the cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/items"), CancellationToken.None);
        callCount.Should().Be(2, $"GET cache entry should have been invalidated by successful {method}");
    }

    // ── §4.4 error responses do NOT trigger invalidation ──────────────────────

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ErrorResponseToUnsafeMethod_DoesNotInvalidateCache(HttpStatusCode errorStatus)
    {
        int callCount = 0;
        bool firstUnsafe = true;
        CachingMiddleware middleware = BuildMiddleware(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                callCount++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("cached") };
            }

            // Unsafe method returns error
            if (firstUnsafe)
            {
                firstUnsafe = false;
                return new HttpResponseMessage(errorStatus);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        HttpMessageInvoker invoker = new(middleware);

        // Populate cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/items"), CancellationToken.None);
        callCount.Should().Be(1);

        // Unsafe method returns error → should NOT invalidate
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "https://api.test/items"), CancellationToken.None);

        // GET should still be served from cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/items"), CancellationToken.None);
        callCount.Should().Be(1, $"error response ({(int)errorStatus}) must not invalidate the cache");
    }

    // ── §4.4 MAY — Location header ───────────────────────────────────────────

    [Fact]
    public async Task SuccessfulPost_WithLocationHeader_InvalidatesCachedGetForLocationUri()
    {
        int getItemsCalls = 0;
        int getDetailCalls = 0;
        CachingMiddleware middleware = BuildMiddleware(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsoluteUri.EndsWith("/items"))
            {
                getItemsCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("items-list") };
            }

            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsoluteUri.EndsWith("/items/42"))
            {
                getDetailCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("item-42") };
            }

            // POST returns 201 Created with Location
            HttpResponseMessage postResponse = new(HttpStatusCode.Created)
            {
                Content = new StringContent("created")
            };
            postResponse.Headers.Location = new Uri("https://api.test/items/42");
            return postResponse;
        });
        HttpMessageInvoker invoker = new(middleware);

        // Cache both URIs
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/items"), CancellationToken.None);
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/items/42"), CancellationToken.None);
        getItemsCalls.Should().Be(1);
        getDetailCalls.Should().Be(1);

        // POST to /items with Location: /items/42 → should invalidate both /items and /items/42
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://api.test/items"), CancellationToken.None);

        // Both should miss cache now
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/items"), CancellationToken.None);
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/items/42"), CancellationToken.None);

        getItemsCalls.Should().Be(2, "effective request URI should be invalidated");
        getDetailCalls.Should().Be(2, "Location URI should also be invalidated");
    }

    // ── §4.4 MAY — Content-Location header ────────────────────────────────────

    [Fact]
    public async Task SuccessfulPut_WithContentLocationHeader_InvalidatesCachedGetForContentLocationUri()
    {
        int getCollectionCalls = 0;
        int getCanonicalCalls = 0;
        CachingMiddleware middleware = BuildMiddleware(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsoluteUri == "https://api.test/items")
            {
                getCollectionCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("collection") };
            }

            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsoluteUri == "https://api.test/canonical/items")
            {
                getCanonicalCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("canonical") };
            }

            // PUT returns 200 with Content-Location pointing to the canonical URI
            HttpResponseMessage putResponse = new(HttpStatusCode.OK)
            {
                Content = new StringContent("updated")
            };
            putResponse.Content.Headers.ContentLocation = new Uri("https://api.test/canonical/items");
            return putResponse;
        });
        HttpMessageInvoker invoker = new(middleware);

        // Cache both URIs
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/items"), CancellationToken.None);
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/canonical/items"), CancellationToken.None);
        getCollectionCalls.Should().Be(1);
        getCanonicalCalls.Should().Be(1);

        // PUT to /items with Content-Location: /canonical/items
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Put, "https://api.test/items"), CancellationToken.None);

        // Both should miss cache now
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/items"), CancellationToken.None);
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/canonical/items"), CancellationToken.None);

        getCollectionCalls.Should().Be(2, "effective request URI should be invalidated");
        getCanonicalCalls.Should().Be(2, "Content-Location URI should also be invalidated");
    }

    // ── Safe methods do NOT trigger invalidation ──────────────────────────────

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task SafeMethod_DoesNotInvalidateCache(string method)
    {
        int getCallCount = 0;
        CachingMiddleware middleware = BuildMiddleware(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsoluteUri.EndsWith("/items"))
                getCallCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") };
        });
        HttpMessageInvoker invoker = new(middleware);

        // Populate GET cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/items"), CancellationToken.None);
        getCallCount.Should().Be(1);

        // Safe method should NOT invalidate
        _ = await invoker.SendAsync(new HttpRequestMessage(new HttpMethod(method), "https://api.test/items"), CancellationToken.None);

        // GET should still be served from cache
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/items"), CancellationToken.None);
        getCallCount.Should().Be(1, $"safe method {method} must not trigger cache invalidation");
    }

    // ── No cached entry — invalidation is a no-op ─────────────────────────────

    [Fact]
    public async Task UnsafeMethodOnUncachedUri_DoesNotThrow()
    {
        CachingMiddleware middleware = BuildMiddleware(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        HttpMessageInvoker invoker = new(middleware);

        // DELETE on a URI that was never cached — should not throw
        HttpResponseMessage response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Delete, "https://api.test/never-cached"), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Only the targeted URI is invalidated ──────────────────────────────────

    [Fact]
    public async Task UnsafeMethod_OnlyInvalidatesTargetUri_OtherEntriesPreserved()
    {
        int itemsCalls = 0;
        int usersCalls = 0;
        CachingMiddleware middleware = BuildMiddleware(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsoluteUri.Contains("/items"))
                itemsCalls++;
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsoluteUri.Contains("/users"))
                usersCalls++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") };
        });
        HttpMessageInvoker invoker = new(middleware);

        // Cache two different URIs
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/items"), CancellationToken.None);
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/users"), CancellationToken.None);
        itemsCalls.Should().Be(1);
        usersCalls.Should().Be(1);

        // DELETE /items → only /items should be invalidated
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "https://api.test/items"), CancellationToken.None);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/items"), CancellationToken.None);
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/users"), CancellationToken.None);

        itemsCalls.Should().Be(2, "/items should have been invalidated by DELETE");
        usersCalls.Should().Be(1, "/users should still be served from cache");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private CachingMiddleware BuildMiddleware(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        StubHandler stub = new(handler);
        CachingMiddleware middleware = new(_cache, _keyBuilder, _options) { InnerHandler = stub };
        return middleware;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(handler(request));
    }
}
