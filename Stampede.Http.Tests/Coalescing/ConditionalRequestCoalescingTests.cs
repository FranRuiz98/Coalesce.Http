using Stampede.Http.Coalescing;
using Stampede.Http.Handlers;
using Stampede.Http.Options;
using FluentAssertions;
using System.Net;
using System.Net.Http.Headers;

namespace Stampede.Http.Tests.Coalescing;

/// <summary>
/// Verifies that conditional requests (RFC 9110 §13) are never coalesced with non-conditional requests, nor with
/// conditional requests carrying a different validator. Without this, a caller that never sent
/// <c>If-None-Match</c> could receive a bodyless <c>304</c> produced for another caller's revalidation.
/// </summary>
public sealed class ConditionalRequestCoalescingTests
{
    // ── RequestKey unit tests ─────────────────────────────────────────────────

    [Fact]
    public void RequestKey_ConditionalRequest_DiffersFromUnconditional()
    {
        HttpRequestMessage conditional = Req("https://api.test/res");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", "\"v1\"");
        HttpRequestMessage plain = Req("https://api.test/res");

        RequestKey conditionalKey = RequestKey.Create(conditional, keyHeaders: null);
        RequestKey plainKey = RequestKey.Create(plain, keyHeaders: null);

        conditionalKey.Should().NotBe(plainKey,
            "a conditional revalidation must not share a coalescing key with a non-conditional request");
    }

    [Fact]
    public void RequestKey_SameValidator_AreEqual()
    {
        HttpRequestMessage a = Req("https://api.test/res");
        a.Headers.TryAddWithoutValidation("If-None-Match", "\"v1\"");
        HttpRequestMessage b = Req("https://api.test/res");
        b.Headers.TryAddWithoutValidation("If-None-Match", "\"v1\"");

        RequestKey.Create(a, null).Should().Be(RequestKey.Create(b, null),
            "identical revalidations must still coalesce to collapse a revalidation storm");
    }

    [Fact]
    public void RequestKey_DifferentValidators_AreNotEqual()
    {
        HttpRequestMessage a = Req("https://api.test/res");
        a.Headers.TryAddWithoutValidation("If-None-Match", "\"v1\"");
        HttpRequestMessage b = Req("https://api.test/res");
        b.Headers.TryAddWithoutValidation("If-None-Match", "\"v2\"");

        RequestKey.Create(a, null).Should().NotBe(RequestKey.Create(b, null),
            "different validators may yield different results and must not be coalesced");
    }

    [Fact]
    public void RequestKey_IfModifiedSince_IsFoldedIntoKey()
    {
        HttpRequestMessage conditional = Req("https://api.test/res");
        conditional.Headers.IfModifiedSince = DateTimeOffset.UtcNow;
        HttpRequestMessage plain = Req("https://api.test/res");

        RequestKey.Create(conditional, null).Should().NotBe(RequestKey.Create(plain, null),
            "If-Modified-Since is a conditional header and must discriminate the coalescing key");
    }

    [Fact]
    public void RequestKey_ConditionalFoldedAlongsideConfiguredKeyHeaders()
    {
        HttpRequestMessage a = Req("https://api.test/res", ("X-Tenant-Id", "t1"));
        a.Headers.TryAddWithoutValidation("If-None-Match", "\"v1\"");
        HttpRequestMessage b = Req("https://api.test/res", ("X-Tenant-Id", "t1"));

        RequestKey.Create(a, ["X-Tenant-Id"]).Should().NotBe(RequestKey.Create(b, ["X-Tenant-Id"]),
            "conditional headers must be folded in even when explicit CoalesceKeyHeaders are configured");
    }

    // ── CoalescingHandler integration ─────────────────────────────────────────

    [Fact]
    public async Task ConditionalAndNonConditional_AreNotCoalesced_AndEachGetsCorrectResponse()
    {
        // Origin returns 304 for conditional requests and a full 200 for the rest.
        ConditionalStub stub = new(delay: TimeSpan.FromMilliseconds(80));
        CoalescerOptions options = new();
        RequestCoalescer coalescer = new(options);
        CoalescingHandler handler = new(coalescer, options) { InnerHandler = stub };
        HttpMessageInvoker invoker = new(handler);

        HttpRequestMessage conditional = Req("https://api.test/res");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", "\"v1\"");
        HttpRequestMessage plain = Req("https://api.test/res");

        Task<HttpResponseMessage> conditionalTask = invoker.SendAsync(conditional, CancellationToken.None);
        Task<HttpResponseMessage> plainTask = invoker.SendAsync(plain, CancellationToken.None);

        HttpResponseMessage conditionalResponse = await conditionalTask;
        HttpResponseMessage plainResponse = await plainTask;

        stub.CallCount.Should().Be(2,
            "a conditional and a non-conditional request for the same URL must execute independently");
        conditionalResponse.StatusCode.Should().Be(HttpStatusCode.NotModified,
            "the conditional caller must receive the 304 produced for its validator");
        plainResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "the non-conditional caller must never receive a 304 it did not ask for");
        (await plainResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("full-body");
    }

    [Fact]
    public async Task IdenticalConditionalRequests_AreStillCoalesced()
    {
        ConditionalStub stub = new(delay: TimeSpan.FromMilliseconds(80));
        CoalescerOptions options = new();
        RequestCoalescer coalescer = new(options);
        CoalescingHandler handler = new(coalescer, options) { InnerHandler = stub };
        HttpMessageInvoker invoker = new(handler);

        HttpRequestMessage a = Req("https://api.test/res");
        a.Headers.TryAddWithoutValidation("If-None-Match", "\"v1\"");
        HttpRequestMessage b = Req("https://api.test/res");
        b.Headers.TryAddWithoutValidation("If-None-Match", "\"v1\"");

        Task<HttpResponseMessage> taskA = invoker.SendAsync(a, CancellationToken.None);
        Task<HttpResponseMessage> taskB = invoker.SendAsync(b, CancellationToken.None);

        await Task.WhenAll(taskA, taskB);

        stub.CallCount.Should().Be(1,
            "two revalidations with the same validator must collapse into a single origin call");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HttpRequestMessage Req(string url, params (string Name, string Value)[] headers)
    {
        HttpRequestMessage req = new(HttpMethod.Get, url);
        foreach ((string name, string value) in headers)
        {
            req.Headers.TryAddWithoutValidation(name, value);
        }

        return req;
    }

    private sealed class ConditionalStub(TimeSpan delay) : HttpMessageHandler
    {
        private int _callCount;
        public int CallCount => _callCount;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            System.Threading.Interlocked.Increment(ref _callCount);
            await Task.Delay(delay, ct);

            if (request.Headers.IfNoneMatch.Count > 0)
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("full-body") };
        }
    }
}
