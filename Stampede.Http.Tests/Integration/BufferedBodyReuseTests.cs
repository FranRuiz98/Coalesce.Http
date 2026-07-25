using Stampede.Http.Caching;
using Stampede.Http.Coalescing;
using Stampede.Http.Handlers;
using Stampede.Http.Options;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using System.Net;

namespace Stampede.Http.Tests.Integration;

/// <summary>
/// Verifies that a response body materialised by the coalescer is handed to the caching layer without being
/// copied out and rebuffered again — the double copy every coalesced caller used to pay on a cache miss.
/// </summary>
public sealed class BufferedBodyReuseTests
{
    private static (CachingMiddleware Middleware, ICacheStore Cache) BuildPipeline(
        Func<HttpRequestMessage, HttpResponseMessage> origin)
    {
        ICacheStore cache = new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions()));

        StubTransport transport = new(origin);
        CoalescingHandler coalescing = new(new RequestCoalescer(new CoalescerOptions())) { InnerHandler = transport };
        CachingMiddleware caching = new(cache, new DefaultCacheKeyBuilder(),
            new CacheOptions { DefaultTtl = TimeSpan.FromMinutes(5) })
        {
            InnerHandler = coalescing
        };

        return (caching, cache);
    }

    [Fact]
    public async Task CachedBody_SharesTheArrayBufferedByTheCoalescer()
    {
        byte[] payload = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];

        (CachingMiddleware middleware, ICacheStore cache) = BuildPipeline(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });

        HttpMessageInvoker invoker = new(middleware);
        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/reuse"), CancellationToken.None);

        cache.TryGetValue("GET:https://api.test/reuse", out CacheEntry? entry).Should().BeTrue();
        entry!.Body.Should().Equal(payload, "the stored body must match what the origin sent");
    }

    [Fact]
    public async Task CoalescedWaiters_AllReceiveIndependentReadableResponses()
    {
        // Sharing one buffer across callers must not make the responses interfere with each other.
        byte[] payload = [.. Enumerable.Range(0, 1024).Select(i => (byte)(i % 256))];
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int originCalls = 0;

        (CachingMiddleware middleware, _) = BuildPipeline(request =>
        {
            _ = Interlocked.Increment(ref originCalls);
            gate.Task.GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        });

        HttpMessageInvoker invoker = new(middleware);

        Task<HttpResponseMessage>[] callers = [.. Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.test/waiters"), CancellationToken.None)))];

        await Task.Delay(100, TestContext.Current.CancellationToken);
        gate.SetResult();

        HttpResponseMessage[] responses = await Task.WhenAll(callers);

        foreach (HttpResponseMessage response in responses)
        {
            byte[] body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
            body.Should().Equal(payload, "every coalesced caller must be able to read the full body independently");
        }

        Volatile.Read(ref originCalls).Should().Be(1, "the callers should have shared a single origin call");
    }

    [Fact]
    public async Task OversizedBody_NotCachedAndStillReadableByTheCaller()
    {
        byte[] payload = new byte[4096];
        Random.Shared.NextBytes(payload);

        ICacheStore cache = new MemoryCacheStore(new MemoryCache(new MemoryCacheOptions()));
        StubTransport transport = new(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        CachingMiddleware middleware = new(cache, new DefaultCacheKeyBuilder(),
            new CacheOptions { DefaultTtl = TimeSpan.FromMinutes(5), MaxBodySizeBytes = 128 })
        {
            InnerHandler = transport
        };

        HttpMessageInvoker invoker = new(middleware);
        HttpResponseMessage response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.test/oversized"), CancellationToken.None);

        cache.TryGetValue("GET:https://api.test/oversized", out _)
            .Should().BeFalse("a body over MaxBodySizeBytes must not be stored");

        byte[] body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        body.Should().Equal(payload, "skipping the cache must not cost the caller its response body");
    }

    private sealed class StubTransport(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(handler(request));
    }
}
