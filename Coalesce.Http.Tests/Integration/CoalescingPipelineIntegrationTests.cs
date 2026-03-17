using Coalesce.Http.Extensions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Coalesce.Http.Tests.Integration;

public class CoalescingPipelineIntegrationTests
{
    [Fact]
    public async Task Get_ConcurrentIdenticalRequests_ShouldHitTransportOnce()
    {
        // Arrange
        var transport = new BlockingCountHandler();
        var client = CreateClient(transport);

        // Act
        var request1 = client.GetAsync("https://api.example.com/products/42");
        var request2 = client.GetAsync("https://api.example.com/products/42");
        var request3 = client.GetAsync("https://api.example.com/products/42");

        await transport.WaitForFirstCallAsync();
        transport.Release();

        var responses = await Task.WhenAll(request1, request2, request3);

        // Assert
        transport.CallCount.Should().Be(1);
        responses.Should().OnlyContain(r => r.StatusCode == System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task Post_ConcurrentIdenticalRequests_ShouldNotCoalesce()
    {
        // Arrange
        var transport = new BlockingCountHandler();
        var client = CreateClient(transport);

        // Act
        var request1 = client.PostAsync("https://api.example.com/orders", new StringContent("{}"));
        var request2 = client.PostAsync("https://api.example.com/orders", new StringContent("{}"));
        var request3 = client.PostAsync("https://api.example.com/orders", new StringContent("{}"));

        await transport.WaitForCallCountAsync(3);
        transport.Release();

        var responses = await Task.WhenAll(request1, request2, request3);

        // Assert
        transport.CallCount.Should().Be(3);
        responses.Should().OnlyContain(r => r.StatusCode == System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task Get_SequentialSameRequest_ShouldExecuteAgainAfterCompletion()
    {
        // Arrange
        var transport = new ImmediateCountHandler();
        var client = CreateClient(transport);

        // Act
        var first = await client.GetAsync("https://api.example.com/catalog");
        var second = await client.GetAsync("https://api.example.com/catalog");

        // Assert
        first.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        second.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        transport.CallCount.Should().Be(2);
    }

    private static HttpClient CreateClient(HttpMessageHandler primaryHandler)
    {
        var services = new ServiceCollection();

        services
            .AddHttpClient("coalescing")
            .AddCoalesceHttp()
            .ConfigurePrimaryHttpMessageHandler(() => primaryHandler);

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        return factory.CreateClient("coalescing");
    }

    private sealed class BlockingCountHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _firstCallReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task WaitForFirstCallAsync() => _firstCallReached.Task;

        public async Task WaitForCallCountAsync(int expectedCount)
        {
            while (CallCount < expectedCount)
            {
                await Task.Delay(10);
            }
        }

        public void Release() => _release.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref _callCount);

            if (current == 1)
            {
                _firstCallReached.TrySetResult(true);
            }

            await _release.Task.WaitAsync(cancellationToken);

            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            };
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };
            return response;
        }
    }

    private sealed class ImmediateCountHandler : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);

            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            };
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(response);
        }
    }
}
