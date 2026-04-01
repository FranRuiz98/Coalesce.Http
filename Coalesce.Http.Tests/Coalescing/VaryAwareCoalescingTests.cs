using Coalesce.Http.Caching;
using Coalesce.Http.Coalescing;
using Coalesce.Http.Handlers;
using Coalesce.Http.Options;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;

namespace Coalesce.Http.Tests.Coalescing;

/// <summary>
/// Verifies <see cref="CoalescerOptions.CoalesceKeyHeaders"/>:
/// requests that share the same URL but differ in a nominated header value must be coalesced
/// independently, while requests with the same header values are still collapsed.
/// </summary>
public sealed class VaryAwareCoalescingTests
{
    // ── RequestKey unit tests ─────────────────────────────────────────────────

    [Fact]
    public void RequestKey_WithNoKeyHeaders_TwoIdenticalRequests_AreEqual()
    {
        HttpRequestMessage r1 = Req("https://api.test/res");
        HttpRequestMessage r2 = Req("https://api.test/res");

        RequestKey k1 = RequestKey.Create(r1, keyHeaders: null);
        RequestKey k2 = RequestKey.Create(r2, keyHeaders: null);

        k1.Should().Be(k2);
    }

    [Fact]
    public void RequestKey_WithKeyHeaders_DifferentHeaderValues_AreNotEqual()
    {
        HttpRequestMessage r1 = Req("https://api.test/res", ("X-Tenant-Id", "tenant-a"));
        HttpRequestMessage r2 = Req("https://api.test/res", ("X-Tenant-Id", "tenant-b"));

        RequestKey k1 = RequestKey.Create(r1, ["X-Tenant-Id"]);
        RequestKey k2 = RequestKey.Create(r2, ["X-Tenant-Id"]);

        k1.Should().NotBe(k2, "different tenant headers must produce distinct coalescing keys");
    }

    [Fact]
    public void RequestKey_WithKeyHeaders_SameHeaderValues_AreEqual()
    {
        HttpRequestMessage r1 = Req("https://api.test/res", ("X-Tenant-Id", "tenant-a"));
        HttpRequestMessage r2 = Req("https://api.test/res", ("X-Tenant-Id", "tenant-a"));

        RequestKey k1 = RequestKey.Create(r1, ["X-Tenant-Id"]);
        RequestKey k2 = RequestKey.Create(r2, ["X-Tenant-Id"]);

        k1.Should().Be(k2, "same tenant header values must map to the same coalescing key");
    }

    [Fact]
    public void RequestKey_WithKeyHeaders_UnorderedHeaderNames_ProduceSameKey()
    {
        HttpRequestMessage r1 = Req("https://api.test/res",
            ("X-Tenant-Id", "t1"), ("Accept-Version", "v2"));
        HttpRequestMessage r2 = Req("https://api.test/res",
            ("X-Tenant-Id", "t1"), ("Accept-Version", "v2"));

        // Lists in different order — key should still be equal
        RequestKey k1 = RequestKey.Create(r1, ["X-Tenant-Id", "Accept-Version"]);
        RequestKey k2 = RequestKey.Create(r2, ["Accept-Version", "X-Tenant-Id"]);

        k1.Should().Be(k2, "header name ordering in CoalesceKeyHeaders must not affect the key");
    }

    [Fact]
    public void RequestKey_WithKeyHeaders_MissingHeader_TreatedAsEmpty()
    {
        // r1 carries the header, r2 does not
        HttpRequestMessage r1 = Req("https://api.test/res", ("X-Tenant-Id", "tenant-a"));
        HttpRequestMessage r2 = Req("https://api.test/res"); // no X-Tenant-Id

        RequestKey k1 = RequestKey.Create(r1, ["X-Tenant-Id"]);
        RequestKey k2 = RequestKey.Create(r2, ["X-Tenant-Id"]);

        k1.Should().NotBe(k2, "a request missing a nominated header must get a different key than one that provides it");
    }

    [Fact]
    public void RequestKey_HeadersKey_CaseInsensitiveHeaderMatching()
    {
        HttpRequestMessage r1 = Req("https://api.test/res");
        r1.Headers.TryAddWithoutValidation("x-tenant-id", "t1");

        HttpRequestMessage r2 = Req("https://api.test/res");
        r2.Headers.TryAddWithoutValidation("X-TENANT-ID", "t1");

        // The header name in the list uses mixed case — should match both
        RequestKey k1 = RequestKey.Create(r1, ["X-Tenant-Id"]);
        RequestKey k2 = RequestKey.Create(r2, ["X-Tenant-Id"]);

        k1.Should().Be(k2, "header matching should be case-insensitive");
    }

    [Fact]
    public void RequestKey_ToString_WithHeadersKey_IncludesBracketedSegment()
    {
        HttpRequestMessage req = Req("https://api.test/res", ("X-Tenant-Id", "t1"));
        RequestKey key = RequestKey.Create(req, ["X-Tenant-Id"]);

        string result = key.ToString();

        result.Should().Contain("[", "ToString should include the headers segment when HeadersKey is set");
        result.Should().Contain("x-tenant-id=t1");
    }

    // ── CoalescingHandler integration ─────────────────────────────────────────

    [Fact]
    public async Task CoalescingHandler_WithCoalesceKeyHeaders_DifferentValues_NotCoalesced()
    {
        SlowHandler slow = new(delay: TimeSpan.FromMilliseconds(80));
        CoalescerOptions options = new()
        {
            CoalesceKeyHeaders = ["X-Tenant-Id"]
        };
        RequestCoalescer coalescer = new(options);
        CoalescingHandler handler = new(coalescer, options) { InnerHandler = slow };
        HttpMessageInvoker invoker = new(handler);

        HttpRequestMessage reqA = Req("https://api.test/res", ("X-Tenant-Id", "tenant-a"));
        HttpRequestMessage reqB = Req("https://api.test/res", ("X-Tenant-Id", "tenant-b"));

        Task<HttpResponseMessage> taskA = invoker.SendAsync(reqA, CancellationToken.None);
        Task<HttpResponseMessage> taskB = invoker.SendAsync(reqB, CancellationToken.None);

        await Task.WhenAll(taskA, taskB);

        slow.CallCount.Should().Be(2,
            "requests with different X-Tenant-Id values must not be coalesced");
    }

    [Fact]
    public async Task CoalescingHandler_WithCoalesceKeyHeaders_SameValues_AreCoalesced()
    {
        SlowHandler slow = new(delay: TimeSpan.FromMilliseconds(80));
        CoalescerOptions options = new()
        {
            CoalesceKeyHeaders = ["X-Tenant-Id"]
        };
        RequestCoalescer coalescer = new(options);
        CoalescingHandler handler = new(coalescer, options) { InnerHandler = slow };
        HttpMessageInvoker invoker = new(handler);

        HttpRequestMessage reqA = Req("https://api.test/res", ("X-Tenant-Id", "tenant-a"));
        HttpRequestMessage reqB = Req("https://api.test/res", ("X-Tenant-Id", "tenant-a"));

        Task<HttpResponseMessage> taskA = invoker.SendAsync(reqA, CancellationToken.None);
        Task<HttpResponseMessage> taskB = invoker.SendAsync(reqB, CancellationToken.None);

        await Task.WhenAll(taskA, taskB);

        slow.CallCount.Should().Be(1,
            "concurrent requests with the same X-Tenant-Id must still be coalesced into a single origin call");
    }

    [Fact]
    public async Task CoalescingHandler_WithEmptyCoalesceKeyHeaders_BehavesAsDefault()
    {
        SlowHandler slow = new(delay: TimeSpan.FromMilliseconds(80));
        CoalescerOptions options = new(); // CoalesceKeyHeaders = [] by default
        RequestCoalescer coalescer = new(options);
        CoalescingHandler handler = new(coalescer, options) { InnerHandler = slow };
        HttpMessageInvoker invoker = new(handler);

        const string url = "https://api.test/res";
        Task<HttpResponseMessage> taskA = invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None);
        Task<HttpResponseMessage> taskB = invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None);

        await Task.WhenAll(taskA, taskB);

        slow.CallCount.Should().Be(1, "default behaviour (no key headers) must still coalesce identical requests");
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

    private sealed class SlowHandler(TimeSpan delay) : HttpMessageHandler
    {
        private int _callCount;
        public int CallCount => _callCount;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            System.Threading.Interlocked.Increment(ref _callCount);
            await Task.Delay(delay, ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            };
        }
    }
}
