using Coalesce.Http.Coalescing;
using Coalesce.Http.Handlers;
using Coalesce.Http.Options;
using FluentAssertions;

namespace Coalesce.Http.Tests.Coalescing;

/// <summary>
/// Verifies per-request coalescing policy overrides via <see cref="CoalescingRequestPolicy"/>
/// using <see cref="HttpRequestMessage.Options"/>.
/// </summary>
public sealed class CoalescingRequestPolicyTests
{
    private readonly RequestCoalescer _coalescer;
    private readonly TestMessageHandler _innerHandler;
    private readonly CoalescingHandler _handler;

    public CoalescingRequestPolicyTests()
    {
        _coalescer = new RequestCoalescer(new CoalescerOptions());
        _innerHandler = new TestMessageHandler();
        _handler = new CoalescingHandler(_coalescer)
        {
            InnerHandler = _innerHandler
        };
    }

    // ── BypassCoalescing ─────────────────────────────────────────────────────

    [Fact]
    public async Task BypassCoalescing_SkipsDeduplication()
    {
        // Arrange
        var url = "https://api.example.com/data";
        var invoker = new HttpMessageInvoker(_handler);
        _innerHandler.Delay = TimeSpan.FromMilliseconds(100);

        var req1 = new HttpRequestMessage(HttpMethod.Get, url);
        req1.Options.Set(CoalescingRequestPolicy.BypassCoalescing, true);

        var req2 = new HttpRequestMessage(HttpMethod.Get, url);
        req2.Options.Set(CoalescingRequestPolicy.BypassCoalescing, true);

        // Act
        var task1 = invoker.SendAsync(req1, CancellationToken.None);
        var task2 = invoker.SendAsync(req2, CancellationToken.None);
        var responses = await Task.WhenAll(task1, task2);

        // Assert
        _innerHandler.CallCount.Should().Be(2, "bypassed requests should each make independent calls");
        responses.Should().HaveCount(2);
        responses.Should().OnlyContain(r => r.StatusCode == System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task BypassCoalescing_OnlyAffectsFlaggedRequest()
    {
        // Arrange
        var url = "https://api.example.com/data";
        var invoker = new HttpMessageInvoker(_handler);
        _innerHandler.Delay = TimeSpan.FromMilliseconds(100);

        // First request: normal (coalesceable)
        var req1 = new HttpRequestMessage(HttpMethod.Get, url);

        // Second request: also normal (should coalesce with req1)
        var req2 = new HttpRequestMessage(HttpMethod.Get, url);

        // Third request: bypassed (should make independent call)
        var req3 = new HttpRequestMessage(HttpMethod.Get, url);
        req3.Options.Set(CoalescingRequestPolicy.BypassCoalescing, true);

        // Act
        var task1 = invoker.SendAsync(req1, CancellationToken.None);
        var task2 = invoker.SendAsync(req2, CancellationToken.None);
        var task3 = invoker.SendAsync(req3, CancellationToken.None);
        var responses = await Task.WhenAll(task1, task2, task3);

        // Assert
        _innerHandler.CallCount.Should().Be(2, "req1 and req2 coalesce into 1 call; bypassed req3 makes a separate call");
        responses.Should().HaveCount(3);
    }

    [Fact]
    public async Task BypassCoalescing_False_DoesNotBypass()
    {
        // Arrange
        var url = "https://api.example.com/data";
        var invoker = new HttpMessageInvoker(_handler);
        _innerHandler.Delay = TimeSpan.FromMilliseconds(100);

        var req1 = new HttpRequestMessage(HttpMethod.Get, url);
        req1.Options.Set(CoalescingRequestPolicy.BypassCoalescing, false);

        var req2 = new HttpRequestMessage(HttpMethod.Get, url);
        req2.Options.Set(CoalescingRequestPolicy.BypassCoalescing, false);

        // Act
        var task1 = invoker.SendAsync(req1, CancellationToken.None);
        var task2 = invoker.SendAsync(req2, CancellationToken.None);
        var responses = await Task.WhenAll(task1, task2);

        // Assert
        _innerHandler.CallCount.Should().Be(1, "BypassCoalescing=false should still allow coalescing");
    }

    [Fact]
    public async Task BypassCoalescing_NotSet_CoalescesNormally()
    {
        // Arrange
        var url = "https://api.example.com/data";
        var invoker = new HttpMessageInvoker(_handler);
        _innerHandler.Delay = TimeSpan.FromMilliseconds(100);

        var req1 = new HttpRequestMessage(HttpMethod.Get, url);
        var req2 = new HttpRequestMessage(HttpMethod.Get, url);

        // Act
        var task1 = invoker.SendAsync(req1, CancellationToken.None);
        var task2 = invoker.SendAsync(req2, CancellationToken.None);
        var responses = await Task.WhenAll(task1, task2);

        // Assert
        _innerHandler.CallCount.Should().Be(1, "requests without the option should coalesce normally");
    }

    private class TestMessageHandler : HttpMessageHandler
    {
        private int _callCount;
        public int CallCount => _callCount;
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent($"Response {CallCount}")
            };
        }
    }
}
