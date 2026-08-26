using Stampede.Http.Coalescing;
using Stampede.Http.Handlers;
using Stampede.Http.Options;
using FluentAssertions;
using System.Net;
using System.Text;

namespace Stampede.Http.Tests.Handlers;

/// <summary>
/// Verifies <see cref="CoalescerOptions.ShouldCoalesce"/> — opt-in coalescing for methods other than
/// <c>GET</c>/<c>HEAD</c> (typically <c>POST</c>), keyed on method + URL + a hash of the request body.
/// </summary>
public sealed class PostCoalescingTests
{
    private static (CoalescingHandler handler, TestMessageHandler inner) BuildPipeline(CoalescerOptions options)
    {
        RequestCoalescer coalescer = new(options);
        TestMessageHandler inner = new();
        CoalescingHandler handler = new(coalescer, options) { InnerHandler = inner };
        return (handler, inner);
    }

    private static HttpRequestMessage PostReq(string url, string body) =>
        new(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // ── Default behavior: POST never coalesced without opting in ────────────

    [Fact]
    public async Task Post_WithoutShouldCoalesce_NeverCoalesced()
    {
        (CoalescingHandler handler, TestMessageHandler inner) = BuildPipeline(new CoalescerOptions());
        inner.Delay = TimeSpan.FromMilliseconds(100);
        HttpMessageInvoker invoker = new(handler);

        string url = "https://api.test/graphql";
        Task<HttpResponseMessage> t1 = invoker.SendAsync(PostReq(url, """{"query":"{ me }"}"""), CancellationToken.None);
        Task<HttpResponseMessage> t2 = invoker.SendAsync(PostReq(url, """{"query":"{ me }"}"""), CancellationToken.None);

        await Task.WhenAll(t1, t2);

        inner.CallCount.Should().Be(2, "POST must not be coalesced unless ShouldCoalesce explicitly matches it");
    }

    // ── Opted in via ShouldCoalesce ───────────────────────────────────────────

    [Fact]
    public async Task Post_MatchedByPredicate_IdenticalBody_Coalesces()
    {
        CoalescerOptions options = new() { ShouldCoalesce = req => req.Method == HttpMethod.Post };
        (CoalescingHandler handler, TestMessageHandler inner) = BuildPipeline(options);
        inner.Delay = TimeSpan.FromMilliseconds(100);
        HttpMessageInvoker invoker = new(handler);

        string url = "https://api.test/graphql";
        const string body = """{"query":"{ me { id name } }"}""";

        Task<HttpResponseMessage> t1 = invoker.SendAsync(PostReq(url, body), CancellationToken.None);
        Task<HttpResponseMessage> t2 = invoker.SendAsync(PostReq(url, body), CancellationToken.None);
        Task<HttpResponseMessage> t3 = invoker.SendAsync(PostReq(url, body), CancellationToken.None);

        HttpResponseMessage[] responses = await Task.WhenAll(t1, t2, t3);

        inner.CallCount.Should().Be(1, "identical concurrent POST bodies to the same URL must coalesce into one backend call");
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
    }

    [Fact]
    public async Task Post_MatchedByPredicate_DifferentBodies_NeverCoalesced()
    {
        CoalescerOptions options = new() { ShouldCoalesce = req => req.Method == HttpMethod.Post };
        (CoalescingHandler handler, TestMessageHandler inner) = BuildPipeline(options);
        inner.Delay = TimeSpan.FromMilliseconds(100);
        HttpMessageInvoker invoker = new(handler);

        string url = "https://api.test/graphql";

        Task<HttpResponseMessage> t1 = invoker.SendAsync(PostReq(url, """{"query":"{ me }"}"""), CancellationToken.None);
        Task<HttpResponseMessage> t2 = invoker.SendAsync(PostReq(url, """{"query":"{ orders }"}"""), CancellationToken.None);

        await Task.WhenAll(t1, t2);

        inner.CallCount.Should().Be(2, "two different bodies to the same URL must never be merged into one execution");
    }

    [Fact]
    public async Task Post_PredicateExcludesRequest_NotCoalesced()
    {
        // Simulates a GraphQL gateway that must coalesce queries but never mutations.
        CoalescerOptions options = new()
        {
            ShouldCoalesce = req => req.Method == HttpMethod.Post
                && req.Headers.TryGetValues("X-Operation-Type", out IEnumerable<string>? values)
                && values.Contains("query")
        };
        (CoalescingHandler handler, TestMessageHandler inner) = BuildPipeline(options);
        inner.Delay = TimeSpan.FromMilliseconds(100);
        HttpMessageInvoker invoker = new(handler);

        string url = "https://api.test/graphql";
        HttpRequestMessage mutation1 = PostReq(url, """{"query":"mutation { placeOrder }"}""");
        mutation1.Headers.Add("X-Operation-Type", "mutation");
        HttpRequestMessage mutation2 = PostReq(url, """{"query":"mutation { placeOrder }"}""");
        mutation2.Headers.Add("X-Operation-Type", "mutation");

        Task<HttpResponseMessage> t1 = invoker.SendAsync(mutation1, CancellationToken.None);
        Task<HttpResponseMessage> t2 = invoker.SendAsync(mutation2, CancellationToken.None);

        await Task.WhenAll(t1, t2);

        inner.CallCount.Should().Be(2, "a mutation excluded by the predicate must execute independently even with an identical body");
    }

    [Fact]
    public async Task Post_PredicateIncludesRequest_QueriesStillCoalesce()
    {
        CoalescerOptions options = new()
        {
            ShouldCoalesce = req => req.Method == HttpMethod.Post
                && req.Headers.TryGetValues("X-Operation-Type", out IEnumerable<string>? values)
                && values.Contains("query")
        };
        (CoalescingHandler handler, TestMessageHandler inner) = BuildPipeline(options);
        inner.Delay = TimeSpan.FromMilliseconds(100);
        HttpMessageInvoker invoker = new(handler);

        string url = "https://api.test/graphql";
        HttpRequestMessage query1 = PostReq(url, """{"query":"{ me }"}""");
        query1.Headers.Add("X-Operation-Type", "query");
        HttpRequestMessage query2 = PostReq(url, """{"query":"{ me }"}""");
        query2.Headers.Add("X-Operation-Type", "query");

        Task<HttpResponseMessage> t1 = invoker.SendAsync(query1, CancellationToken.None);
        Task<HttpResponseMessage> t2 = invoker.SendAsync(query2, CancellationToken.None);

        await Task.WhenAll(t1, t2);

        inner.CallCount.Should().Be(1, "a query matched by the predicate with an identical body must still coalesce");
    }

    // ── Body size limit ───────────────────────────────────────────────────────

    [Fact]
    public async Task Post_BodyExceedsMaxCoalescedRequestBodyBytes_ExecutesIndependently()
    {
        CoalescerOptions options = new()
        {
            ShouldCoalesce = req => req.Method == HttpMethod.Post,
            MaxCoalescedRequestBodyBytes = 16
        };
        (CoalescingHandler handler, TestMessageHandler inner) = BuildPipeline(options);
        HttpMessageInvoker invoker = new(handler);

        string url = "https://api.test/graphql";
        string oversizedBody = new('x', 1000);

        // A single oversized request must still succeed — "too large to coalesce" is not an error.
        HttpResponseMessage response = await invoker.SendAsync(PostReq(url, oversizedBody), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Post_BodyExceedsLimit_ConcurrentIdenticalRequests_NotCoalesced()
    {
        CoalescerOptions options = new()
        {
            ShouldCoalesce = req => req.Method == HttpMethod.Post,
            MaxCoalescedRequestBodyBytes = 16
        };
        (CoalescingHandler handler, TestMessageHandler inner) = BuildPipeline(options);
        inner.Delay = TimeSpan.FromMilliseconds(100);
        HttpMessageInvoker invoker = new(handler);

        string url = "https://api.test/graphql";
        string oversizedBody = new('x', 1000);

        Task<HttpResponseMessage> t1 = invoker.SendAsync(PostReq(url, oversizedBody), CancellationToken.None);
        Task<HttpResponseMessage> t2 = invoker.SendAsync(PostReq(url, oversizedBody), CancellationToken.None);

        await Task.WhenAll(t1, t2);

        inner.CallCount.Should().Be(2, "bodies over the limit fall back to independent execution rather than throwing or coalescing blindly");
    }

    // ── Body replayability (buffering side effect) ────────────────────────────

    [Fact]
    public async Task Post_MatchedByPredicate_BodyReachesBackendIntact()
    {
        CoalescerOptions options = new() { ShouldCoalesce = req => req.Method == HttpMethod.Post };
        (CoalescingHandler handler, TestMessageHandler inner) = BuildPipeline(options);
        HttpMessageInvoker invoker = new(handler);

        const string body = """{"query":"{ me { id } }"}""";
        HttpResponseMessage response = await invoker.SendAsync(PostReq("https://api.test/graphql", body), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.LastReceivedBody.Should().Be(body, "buffering the body for hashing must not corrupt or truncate what the backend receives");
    }

    // ── GET/HEAD unaffected ────────────────────────────────────────────────────

    [Fact]
    public async Task Get_ShouldCoalesceConfigured_StillCoalescesNormally()
    {
        // ShouldCoalesce only extends eligibility to other methods — GET/HEAD are unconditionally
        // coalesceable regardless of whether it's configured, and never consult it.
        CoalescerOptions options = new() { ShouldCoalesce = _ => false };
        (CoalescingHandler handler, TestMessageHandler inner) = BuildPipeline(options);
        inner.Delay = TimeSpan.FromMilliseconds(100);
        HttpMessageInvoker invoker = new(handler);

        string url = "https://api.test/resource";
        Task<HttpResponseMessage> t1 = invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None);
        Task<HttpResponseMessage> t2 = invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None);

        await Task.WhenAll(t1, t2);

        inner.CallCount.Should().Be(1, "a false-returning ShouldCoalesce must not affect GET's unconditional eligibility");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class TestMessageHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;
        public string? LastReceivedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            CallCount = _callCount;

            if (request.Content is not null)
            {
                LastReceivedBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        }

        private int _callCount;
    }
}
