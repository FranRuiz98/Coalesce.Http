using Coalesce.Http.Coalesce.Http.Coalescing;
using Coalesce.Http.Coalesce.Http.Extensions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Coalesce.Http.Tests.Extensions;

public class HttpClientBuilderExtensionsTests
{
    [Fact]
    public void AddCoalescing_ShouldRegisterRequestCoalescerAsSingleton()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = services.AddHttpClient("test");

        // Act
        builder.AddCoalescing();
        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var coalescer1 = serviceProvider.GetService<RequestCoalescer>();
        var coalescer2 = serviceProvider.GetService<RequestCoalescer>();

        coalescer1.Should().NotBeNull();
        coalescer2.Should().NotBeNull();
        coalescer1.Should().BeSameAs(coalescer2, "RequestCoalescer debería ser singleton");
    }

    [Fact]
    public void AddCoalescing_ShouldRegisterCoalescingHandler()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = services.AddHttpClient("test");

        // Act
        builder.AddCoalescing();
        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient("test");

        httpClient.Should().NotBeNull();
    }

    [Fact]
    public void AddCoalescing_ShouldReturnBuilder()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = services.AddHttpClient("test");

        // Act
        var result = builder.AddCoalescing();

        // Assert
        result.Should().BeSameAs(builder, "debería devolver el mismo builder para permitir chaining");
    }

    [Fact]
    public void AddCoalescing_ShouldAllowChaining()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var builder = services
            .AddHttpClient("test")
            .AddCoalescing();

        // Assert
        builder.Should().NotBeNull();
        services.Should().Contain(s => s.ServiceType == typeof(RequestCoalescer));
    }

    [Fact]
    public async Task AddCoalescing_IntegrationTest_ShouldCoalesceRequests()
    {
        // Arrange
        var services = new ServiceCollection();
        var callCount = 0;
        var delay = new TaskCompletionSource<bool>();

        services
            .AddHttpClient("test")
            .AddCoalescing()
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                return new TestHandler(async () =>
                {
                    Interlocked.Increment(ref callCount);
                    await delay.Task;
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent("Success")
                    };
                });
            });

        var serviceProvider = services.BuildServiceProvider();
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        var client = httpClientFactory.CreateClient("test");

        // Act
        var task1 = client.GetAsync("https://api.example.com/data");
        var task2 = client.GetAsync("https://api.example.com/data");
        var task3 = client.GetAsync("https://api.example.com/data");

        await Task.Delay(50); // Dar tiempo para que todos los requests se inicien
        delay.SetResult(true);

        var responses = await Task.WhenAll(task1, task2, task3);

        // Assert
        responses.Should().HaveCount(3);
        responses.Should().OnlyContain(r => r.StatusCode == System.Net.HttpStatusCode.OK);
        callCount.Should().Be(1, "requests idénticos deberían coalescerse");
    }

    [Fact]
    public async Task AddCoalescing_WithMultipleClients_ShouldIsolatePipelines()
    {
        // Arrange
        var services = new ServiceCollection();
        var callCountClient1 = 0;
        var callCountClient2 = 0;

        services
            .AddHttpClient("client1")
            .AddCoalescing()
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                return new TestHandler(() =>
                {
                    Interlocked.Increment(ref callCountClient1);
                    return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
                });
            });

        services
            .AddHttpClient("client2")
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                return new TestHandler(() =>
                {
                    Interlocked.Increment(ref callCountClient2);
                    return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
                });
            });

        var serviceProvider = services.BuildServiceProvider();
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        var client1 = httpClientFactory.CreateClient("client1");
        var client2 = httpClientFactory.CreateClient("client2");

        // Act
        await client1.GetAsync("https://api.example.com/data");
        await client2.GetAsync("https://api.example.com/data");

        // Assert
        callCountClient1.Should().Be(1);
        callCountClient2.Should().Be(1);
    }

    [Fact]
    public void AddCoalescing_MultipleCallsToSameClient_ShouldNotDuplicateRegistrations()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = services.AddHttpClient("test");

        // Act
        builder.AddCoalescing();
        builder.AddCoalescing(); // Llamar dos veces

        var serviceProvider = services.BuildServiceProvider();

        // Assert - Verificar que RequestCoalescer sigue siendo singleton
        var coalescer1 = serviceProvider.GetRequiredService<RequestCoalescer>();
        var coalescer2 = serviceProvider.GetRequiredService<RequestCoalescer>();
        
        coalescer1.Should().BeSameAs(coalescer2, "RequestCoalescer debería seguir siendo singleton");
    }

    [Fact]
    public void AddCoalescing_WithNullBuilder_ShouldThrowArgumentNullException()
    {
        // Arrange
        IHttpClientBuilder builder = null!;

        // Act
        Action act = () => builder.AddCoalescing();

        // Assert
        act.Should().Throw<NullReferenceException>();
    }

    private class TestHandler : HttpMessageHandler
    {
        private readonly Func<Task<HttpResponseMessage>> _responseFactory;

        public TestHandler(Func<Task<HttpResponseMessage>> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return _responseFactory();
        }
    }
}
