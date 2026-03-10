using Coalesce.Http.Coalesce.Http.Coalescing;
using Coalesce.Http.Coalesce.Http.Handlers;
using FluentAssertions;
using NSubstitute;

namespace Coalesce.Http.Tests.Handlers;

public class CoalescingHandlerTests
{
    private readonly RequestCoalescer _coalescer;
    private readonly TestMessageHandler _innerHandler;
    private readonly CoalescingHandler _handler;

    public CoalescingHandlerTests()
    {
        _coalescer = new RequestCoalescer();
        _innerHandler = new TestMessageHandler();
        _handler = new CoalescingHandler(_coalescer)
        {
            InnerHandler = _innerHandler
        };
    }

    [Fact]
    public async Task SendAsync_WithGetRequest_ShouldUseCoalescer()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        var invoker = new HttpMessageInvoker(_handler);

        // Act
        var response = await invoker.SendAsync(request, CancellationToken.None);

        // Assert
        response.Should().NotBeNull();
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        _innerHandler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task SendAsync_WithMultipleConcurrentGetRequests_ShouldCoalesce()
    {
        // Arrange
        var url = "https://api.example.com/data";
        var invoker = new HttpMessageInvoker(_handler);

        _innerHandler.Delay = TimeSpan.FromMilliseconds(100);

        // Act
        var task1 = invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None);
        var task2 = invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None);
        var task3 = invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None);

        var responses = await Task.WhenAll(task1, task2, task3);

        // Assert
        _innerHandler.CallCount.Should().Be(1, "los requests GET idénticos deberían coalescerse");
        responses.Should().HaveCount(3);
        responses.Should().OnlyContain(r => r.StatusCode == System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task SendAsync_WithPostRequest_ShouldNotCoalesce()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/data");
        var invoker = new HttpMessageInvoker(_handler);

        // Act
        var response = await invoker.SendAsync(request, CancellationToken.None);

        // Assert
        response.Should().NotBeNull();
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        _innerHandler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task SendAsync_WithMultipleConcurrentPostRequests_ShouldNotCoalesce()
    {
        // Arrange
        var url = "https://api.example.com/data";
        var invoker = new HttpMessageInvoker(_handler);

        _innerHandler.Delay = TimeSpan.FromMilliseconds(100);

        // Act
        var task1 = invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, url), CancellationToken.None);
        var task2 = invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, url), CancellationToken.None);
        var task3 = invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, url), CancellationToken.None);

        var responses = await Task.WhenAll(task1, task2, task3);

        // Assert
        _innerHandler.CallCount.Should().Be(3, "los requests POST no deberían coalescerse");
        responses.Should().HaveCount(3);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task SendAsync_WithNonGetRequest_ShouldNotCoalesce(string method)
    {
        // Arrange
        var request = new HttpRequestMessage(new HttpMethod(method), "https://api.example.com/data");
        var invoker = new HttpMessageInvoker(_handler);

        // Act
        var response = await invoker.SendAsync(request, CancellationToken.None);

        // Assert
        response.Should().NotBeNull();
        _innerHandler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task SendAsync_WithDifferentUrls_ShouldNotCoalesce()
    {
        // Arrange
        var invoker = new HttpMessageInvoker(_handler);

        _innerHandler.Delay = TimeSpan.FromMilliseconds(100);

        // Act
        var task1 = invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data1"),
            CancellationToken.None);
        var task2 = invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data2"),
            CancellationToken.None);

        var responses = await Task.WhenAll(task1, task2);

        // Assert
        _innerHandler.CallCount.Should().Be(2, "URLs diferentes no deberían coalescerse");
        responses.Should().HaveCount(2);
    }

    [Fact]
    public async Task SendAsync_AfterFirstRequestCompletes_ShouldNotCoalesceSecondRequest()
    {
        // Arrange
        var url = "https://api.example.com/data";
        var invoker = new HttpMessageInvoker(_handler);

        // Act
        var response1 = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None);
        var response2 = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None);

        // Assert
        _innerHandler.CallCount.Should().Be(2, "requests secuenciales no deberían coalescerse");
        response1.Should().NotBeNull();
        response2.Should().NotBeNull();
    }

    [Fact]
    public async Task SendAsync_WhenInnerHandlerThrows_ShouldPropagateException()
    {
        // Arrange
        _innerHandler.ShouldThrow = true;
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        var invoker = new HttpMessageInvoker(_handler);

        // Act
        Func<Task> act = async () => await invoker.SendAsync(request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Test exception");
    }

    [Fact]
    public async Task SendAsync_WithCancellation_ShouldNotCancelUnderlyingRequest()
    {
        // Arrange
        // Cuando se hace coalescing, el CancellationToken de un caller individual
        // no debe cancelar la request HTTP subyacente para proteger a otros callers
        _innerHandler.Delay = TimeSpan.FromMilliseconds(100);
        var request1 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        var cts = new CancellationTokenSource();
        var invoker = new HttpMessageInvoker(_handler);

        // Act
        var task1 = invoker.SendAsync(request1, cts.Token);
        var task2 = invoker.SendAsync(request2, CancellationToken.None);
        
        // Cancelar el primer caller
        cts.Cancel();
        
        // El segundo caller debe completarse exitosamente
        var response2 = await task2;

        // Assert
        response2.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        // El primer task debería completarse también (la request subyacente no se canceló)
        var response1 = await task1;
        response1.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task SendAsync_WithQueryParameters_ShouldTreatAsDifferent()
    {
        // Arrange
        var invoker = new HttpMessageInvoker(_handler);

        _innerHandler.Delay = TimeSpan.FromMilliseconds(100);

        // Act
        var task1 = invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/search?q=test1"),
            CancellationToken.None);
        var task2 = invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/search?q=test2"),
            CancellationToken.None);

        var responses = await Task.WhenAll(task1, task2);

        // Assert
        _innerHandler.CallCount.Should().Be(2, "diferentes query parameters deberían tratarse como requests diferentes");
    }

    [Fact]
    public async Task SendAsync_WithSameQueryParameters_ShouldCoalesce()
    {
        // Arrange
        var url = "https://api.example.com/search?q=test&page=1";
        var invoker = new HttpMessageInvoker(_handler);

        _innerHandler.Delay = TimeSpan.FromMilliseconds(100);

        // Act
        var task1 = invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None);
        var task2 = invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None);

        var responses = await Task.WhenAll(task1, task2);

        // Assert
        _innerHandler.CallCount.Should().Be(1, "requests GET idénticos con query parameters deberían coalescerse");
    }

    private class TestMessageHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;
        public bool ShouldThrow { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;

            if (ShouldThrow)
            {
                throw new InvalidOperationException("Test exception");
            }

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
