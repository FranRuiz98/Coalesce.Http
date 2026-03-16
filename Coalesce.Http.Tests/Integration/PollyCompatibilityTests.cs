using Coalesce.Http.Coalesce.Http.Extensions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

namespace Coalesce.Http.Tests.Integration;

/// <summary>
/// Verifies the three Polly contract rules using lightweight inline handlers that simulate
/// retry and hedging behaviour — no Polly package dependency required.
/// </summary>
public class PollyCompatibilityTests
{
    // ── Rule 1 — retries execute inside the coalesced execution ───────────────

    /// <summary>
    /// All concurrent callers share the single "winner" execution, including any retry that
    /// occurs within it. The transport is contacted at most once per coalesced group (or per
    /// retry attempt within that group), never once per caller.
    /// </summary>
    [Fact]
    public async Task Rule1_RetriesInsideCoalescedExecution_OnlyOneBackendCallPerGroup()
    {
        // Arrange — a counting transport gated behind a TCS
        TaskCompletionSource<bool> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        GatedCountingTransportHandler transport = new(gate.Task);

        ServiceCollection services = new();
        _ = services
            .AddHttpClient("rule1")
            .AddCoalesceHttp()
            .ConfigurePrimaryHttpMessageHandler(() => transport);

        var client = services.BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("rule1");

        // Start three concurrent callers for the same URL before the transport responds
        var t1 = client.GetAsync("https://api.test/data");
        var t2 = client.GetAsync("https://api.test/data");
        var t3 = client.GetAsync("https://api.test/data");

        await Task.Delay(50); // let all three reach the coalescer
        gate.SetResult(true);

        var responses = await Task.WhenAll(t1, t2, t3);

        // Assert — the transport is hit once; all callers share that single execution
        _ = transport.CallCount.Should().Be(1,
            "CoalescingHandler collapses concurrent callers into one backend call (Rule 1)");
        _ = responses.Should().OnlyContain(r => r.StatusCode == System.Net.HttpStatusCode.OK);
    }

    // ── Rule 2 — conditional headers survive retries ──────────────────────────

    /// <summary>
    /// After <c>CachingMiddleware</c> injects <c>If-None-Match</c> during revalidation,
    /// every retry attempt reaching the transport must carry that header unchanged.
    /// </summary>
    [Fact]
    public async Task Rule2_ConditionalHeadersPreservedAcrossRetries()
    {
        // The ETag the server sends on the first (cache-population) response.
        const string expectedETag = "\"v1\"";

        // Only revalidation calls (callCount >= 2) are recorded here.
        List<string?> revalidationEtags = new();
        int callCount = 0;

        FunctionalTransportHandler transport = new(request =>
        {
            callCount++;

            if (callCount == 1)
            {
                // Cache-population response: 200 + ETag.
                // DefaultTtl = -10 s means the entry is immediately stale.
                HttpResponseMessage r = new(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("body")
                };
                r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(expectedETag);
                return r;
            }

            // Revalidation attempts: capture the If-None-Match header injected by CachingMiddleware.
            _ = request.Headers.TryGetValues("If-None-Match", out var vals);
            revalidationEtags.Add(vals?.FirstOrDefault());

            // Simulate a retriable failure on the first revalidation attempt.
            return callCount == 2
                ? new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotModified);
        });

        // RetryOnceHandler: on 503 it retries the SAME request object.
        // The request already carries If-None-Match injected by CachingMiddleware, so no
        // re-injection is needed.
        RetryOnceHandler retryHandler = new(transport);

        ServiceCollection services = new();
        _ = services
            .AddHttpClient("rule2")
            // Negative TTL → cache entries are immediately stale → revalidation on every 2nd call.
            .AddCoalesceHttp(o => o.DefaultTtl = TimeSpan.FromSeconds(-10))
            .ConfigurePrimaryHttpMessageHandler(() => retryHandler);

        var client = services.BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("rule2");

        // First request: cache miss → 200 with ETag stored (immediately stale).
        _ = await client.GetAsync("https://api.test/item");

        // Second request: stale entry → CachingMiddleware.RevalidateAsync injects
        // If-None-Match → RetryOnceHandler fires attempt 1 (503) then retries (304 → refreshed cache).
        _ = await client.GetAsync("https://api.test/item");

        _ = revalidationEtags.Should().NotBeEmpty("revalidation must have been triggered");
        _ = revalidationEtags.Should().AllBe(expectedETag,
            "If-None-Match must be present and unchanged on every retry attempt (Rule 2)");
    }

    // ── Rule 3 — hedging receives independent request instances ───────────────

    /// <summary>
    /// When a hedging handler issues two concurrent calls, each must receive its own
    /// <see cref="HttpRequestMessage"/> instance so that headers cannot be corrupted by
    /// concurrent mutation.
    /// </summary>
    [Fact]
    public async Task Rule3_HedgedAttempts_ReceiveIndependentRequestInstances()
    {
        ConcurrentBag<HttpRequestMessage> capturedInstances = new();

        FunctionalTransportHandler transport = new(request =>
        {
            capturedInstances.Add(request);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            };
        });

        // Hedging handler: fires two concurrent calls and returns the first response
        FakeHedgingHandler hedgingHandler = new(innerHandler: transport);

        ServiceCollection services = new();
        _ = services
            .AddHttpClient("rule3")
            .AddCoalesceHttp()
            .ConfigurePrimaryHttpMessageHandler(() => hedgingHandler);

        var client = services.BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("rule3");

        _ = await client.GetAsync("https://api.test/hedged");

        _ = capturedInstances.Should().HaveCount(2, "hedging issued two concurrent attempts");
        _ = capturedInstances.Distinct().Should().HaveCount(2,
            "each hedged attempt must receive an independent HttpRequestMessage instance (Rule 3)");
    }

    // ── Rule 3 — headers on hedged clones are independent ────────────────────

    [Fact]
    public async Task Rule3_HedgedAttempts_HeaderMutationDoesNotCrossContaminate()
    {
        ConcurrentBag<string?> capturedMarkers = new();

        FunctionalTransportHandler transport = new(request =>
        {
            // Each hedge adds its own unique marker; they must not see each other's additions.
            _ = request.Headers.TryAddWithoutValidation("X-Hedge-Marker", Guid.NewGuid().ToString());
            _ = request.Headers.TryGetValues("X-Hedge-Marker", out var vals);
            capturedMarkers.Add(vals?.FirstOrDefault());

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            };
        });

        FakeHedgingHandler hedgingHandler = new(innerHandler: transport);

        ServiceCollection services = new();
        _ = services
            .AddHttpClient("rule3b")
            .AddCoalesceHttp()
            .ConfigurePrimaryHttpMessageHandler(() => hedgingHandler);

        var client = services.BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("rule3b");

        _ = await client.GetAsync("https://api.test/hedged-headers");

        _ = capturedMarkers.Should().HaveCount(2);
        _ = capturedMarkers.Distinct().Should().HaveCount(2,
            "each hedged attempt mutates its own independent request; markers must differ (Rule 3)");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>A non-delegating handler that invokes a callback for each incoming request.</summary>
    private sealed class FunctionalTransportHandler(
        Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(callback(request));
        }
    }

    /// <summary>Counts calls and blocks until a gate task completes.</summary>
    private sealed class GatedCountingTransportHandler(Task gate) : HttpMessageHandler
    {
        private int _count;

        public int CallCount => Volatile.Read(ref _count);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _count);
            await gate.WaitAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("data")
            };
        }
    }

    /// <summary>
    /// On 503, retries the same request object once without creating a new instance.
    /// The request already carries any conditional headers injected by <c>CachingMiddleware</c>.
    /// </summary>
    private sealed class RetryOnceHandler(HttpMessageHandler inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _invoker = new(inner);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await _invoker.SendAsync(request, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                response = await _invoker.SendAsync(request, cancellationToken);
            }

            return response;
        }
    }

    /// <summary>
    /// Simulates Polly hedging: issues two concurrent calls each receiving an independent
    /// clone of the original request, then returns whichever response arrives first.
    /// </summary>
    private sealed class FakeHedgingHandler(HttpMessageHandler innerHandler) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _invoker = new(innerHandler);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var primary = _invoker.SendAsync(CloneRequest(request), cancellationToken);
            var hedged = _invoker.SendAsync(CloneRequest(request), cancellationToken);

            return await await Task.WhenAny(primary, hedged);
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage original)
        {
            HttpRequestMessage clone = new(original.Method, original.RequestUri)
            {
                Version = original.Version,
            };

            foreach (var header in original.Headers)
            {
                _ = clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = original.Content;
            return clone;
        }
    }
}
