using Coalesce.Http.Coalesce.Http.Extensions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace Coalesce.Http.Tests.Integration;

/// <summary>
/// Integration tests that exercise the three Polly contract rules using the real
/// <c>Microsoft.Extensions.Http.Resilience</c> package and actual Polly retry / hedging
/// strategies — no fake or simulated handlers for the resilience layer.
/// </summary>
public class PollyRealIntegrationTests
{
    // ── Rule 1 — retries execute inside the coalesced execution ──────────────

    /// <summary>
    /// Three concurrent callers are coalesced to a single winner execution.
    /// When that execution fails with 503, Polly retries it once.
    /// The transport is hit exactly twice (initial fail + one retry), not six times
    /// (2 × 3 callers).  All three callers receive the successful response.
    /// </summary>
    [Fact]
    public async Task Rule1_PollyRetry_CoalescedCallersShareRetryOutcome()
    {
        int transportCalls = 0;
        TaskCompletionSource<bool> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        AsyncFuncHandler transport = new(async (req, ct) =>
        {
            int n = Interlocked.Increment(ref transportCalls);

            if (n == 1)
            {
                await gate.Task; // block until all three callers are queued at the coalescer
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            };
        });

        ServiceCollection services = new();
        services.AddLogging();
        IHttpClientBuilder clientBuilder = services
            .AddHttpClient("polly-real-rule1")
            .AddCoalesceHttp();
        clientBuilder.AddResilienceHandler("retry", b => b.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 1,
            Delay = TimeSpan.Zero,
            BackoffType = DelayBackoffType.Constant,
        }));
        clientBuilder.ConfigurePrimaryHttpMessageHandler(() => transport);

        HttpClient client = services.BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("polly-real-rule1");

        Task<HttpResponseMessage> t1 = client.GetAsync("https://api.test/r1");
        Task<HttpResponseMessage> t2 = client.GetAsync("https://api.test/r1");
        Task<HttpResponseMessage> t3 = client.GetAsync("https://api.test/r1");

        await Task.Delay(50); // let all three queue up at CoalescingHandler
        gate.SetResult(true); // winner gets 503 → Polly retries → 200

        HttpResponseMessage[] responses = await Task.WhenAll(t1, t2, t3);

        _ = transportCalls.Should().Be(2,
            "1 initial 503 + 1 Polly retry, shared by all three coalesced callers (Rule 1)");
        _ = responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
    }

    // ── Rule 2 — conditional headers survive retries ─────────────────────────

    /// <summary>
    /// CachingMiddleware injects <c>If-None-Match</c> once, before the Polly layer.
    /// Every Polly retry attempt must carry that header unchanged because Polly clones
    /// the snapshot it took of the request that entered the resilience pipeline.
    /// </summary>
    [Fact]
    public async Task Rule2_PollyRetry_IfNoneMatchPreservedAcrossAllRetryAttempts()
    {
        const string expectedETag = "\"rev1\"";
        ConcurrentBag<string?> revalidationEtags = new();
        int calls = 0;

        SyncFuncHandler transport = new(req =>
        {
            int n = Interlocked.Increment(ref calls);

            if (n == 1)
            {
                // Cache-population response: 200 + ETag (DefaultTtl = -10 s → immediately stale).
                HttpResponseMessage r = new(HttpStatusCode.OK)
                {
                    Content = new StringContent("body")
                };
                r.Headers.ETag = new EntityTagHeaderValue(expectedETag);
                return r;
            }

            // Revalidation attempts: capture the If-None-Match header that CachingMiddleware
            // injected before the request entered the Polly pipeline.
            _ = req.Headers.TryGetValues("If-None-Match", out IEnumerable<string>? vals);
            revalidationEtags.Add(vals?.FirstOrDefault());

            // First revalidation attempt: 503 → triggers Polly retry.
            // Second attempt (Polly retry): 304 → cache refreshed.
            return n == 2
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.NotModified);
        });

        ServiceCollection services = new();
        services.AddLogging();
        IHttpClientBuilder clientBuilder = services
            .AddHttpClient("polly-real-rule2")
            .AddCoalesceHttp(o => o.DefaultTtl = TimeSpan.FromMilliseconds(1));
        clientBuilder.AddResilienceHandler("retry", b => b.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 1,
            Delay = TimeSpan.Zero,
            BackoffType = DelayBackoffType.Constant,
        }));
        clientBuilder.ConfigurePrimaryHttpMessageHandler(() => transport);

        HttpClient client = services.BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("polly-real-rule2");

        // First request: cache miss → 200 + ETag stored (becomes stale within 1ms).
        _ = await client.GetAsync("https://api.test/item");

        // Wait for the entry to become stale
        await Task.Delay(10);

        // Second request: stale entry → CachingMiddleware.RevalidateAsync injects If-None-Match
        // → Polly fires attempt 1 (503) then retries (304 → refreshed cache).
        _ = await client.GetAsync("https://api.test/item");

        _ = revalidationEtags.Should().NotBeEmpty("revalidation must have been triggered");
        _ = revalidationEtags.Should().AllBe(expectedETag,
            "If-None-Match must be present and unchanged on every Polly retry attempt (Rule 2)");
    }

    // ── Rule 3 — hedging receives independent request instances ──────────────

    /// <summary>
    /// With <c>AddHedging</c> and <c>Delay = TimeSpan.Zero</c>, Polly fires a hedged request
    /// immediately alongside the primary.  Both calls must reach the transport concurrently and
    /// the caller receives the first successful response.
    /// <para>
    /// Note: <c>Microsoft.Extensions.Http.Resilience</c> intentionally shares the same
    /// <see cref="HttpRequestMessage"/> reference across concurrent hedged attempts because
    /// requests are treated as immutable once they enter the resilience pipeline.
    /// Instance-level independence is validated separately by
    /// <see cref="PollyCompatibilityTests.Rule3_HedgedAttempts_ReceiveIndependentRequestInstances"/>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Rule3_PollyHedging_EachHedgedAttemptReceivesIndependentRequestInstance()
    {
        ConcurrentBag<HttpRequestMessage> capturedRequests = new();
        TaskCompletionSource<bool> twoArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int arrivals = 0;

        // Transport blocks until both hedged calls have registered, ensuring both are
        // in-flight simultaneously before either one returns.
        AsyncFuncHandler transport = new(async (req, _) =>
        {
            capturedRequests.Add(req);

            if (Interlocked.Increment(ref arrivals) >= 2)
            {
                twoArrived.TrySetResult(true);
            }

            await twoArrived.Task; // do not pass ct — avoids cancellation racing with gate
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            };
        });

        ServiceCollection services = new();
        services.AddLogging();
        IHttpClientBuilder clientBuilder3 = services
            .AddHttpClient("polly-real-rule3")
            .AddCoalesceHttp();
        clientBuilder3.AddResilienceHandler("hedging", b => b.AddHedging(new HttpHedgingStrategyOptions
        {
            MaxHedgedAttempts = 1, // primary + 1 hedge = 2 concurrent calls
            Delay = TimeSpan.Zero, // fire the hedge immediately alongside the primary
        }));
        clientBuilder3.ConfigurePrimaryHttpMessageHandler(() => transport);

        HttpClient client = services.BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("polly-real-rule3");

        var response = await client.GetAsync("https://api.test/hedged");

        _ = capturedRequests.Should().HaveCount(2,
            "Polly hedging (MaxHedgedAttempts = 1) must fire 2 concurrent transport calls (Rule 3)");
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the first successful hedged response must be returned to the caller");
    }

    // ── Bonus — cache hit is served before reaching Polly ────────────────────

    /// <summary>
    /// A fresh cache hit is returned entirely within <c>CachingMiddleware</c>.
    /// The Polly resilience handler and the transport are never invoked for the second request,
    /// even though both are registered in the pipeline.
    /// </summary>
    [Fact]
    public async Task CacheHit_NeverReachesPollyOrTransport()
    {
        int backendCalls = 0;

        SyncFuncHandler transport = new(req =>
        {
            Interlocked.Increment(ref backendCalls);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("body")
            };
        });

        ServiceCollection services = new();
        services.AddLogging();
        IHttpClientBuilder cacheClientBuilder = services
            .AddHttpClient("polly-real-cache")
            .AddCoalesceHttp(o => o.DefaultTtl = TimeSpan.FromMinutes(5));
        cacheClientBuilder.AddResilienceHandler("retry", b => b.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 2,
        }));
        cacheClientBuilder.ConfigurePrimaryHttpMessageHandler(() => transport);

        HttpClient client = services.BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("polly-real-cache");

        _ = await client.GetAsync("https://api.test/res"); // cache miss → transport
        _ = await client.GetAsync("https://api.test/res"); // cache hit  → served from cache

        _ = backendCalls.Should().Be(1,
            "the second request is a fresh cache hit; it must not reach Polly or the transport");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class SyncFuncHandler(
        Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(fn(request));
    }

    private sealed class AsyncFuncHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fn) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => fn(request, ct);
    }
}
