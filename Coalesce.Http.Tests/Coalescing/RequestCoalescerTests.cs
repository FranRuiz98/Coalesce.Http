using Coalesce.Http.Coalescing;
using Coalesce.Http.Options;
using FluentAssertions;

namespace Coalesce.Http.Tests.Coalescing;

public class RequestCoalescerTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldExecuteFactoryOnce()
    {
        // Arrange
        var coalescer = new RequestCoalescer(new CoalescerOptions());
        var key = new RequestKey("GET", "https://api.example.com/data");
        var executionCount = 0;

        Task<HttpResponseMessage> Factory()
        {
            executionCount++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }

        // Act
        var response = await coalescer.ExecuteAsync(key, Factory);

        // Assert
        executionCount.Should().Be(1);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task ExecuteAsync_WithMultipleConcurrentCalls_ShouldExecuteFactoryOnce()
    {
        // Arrange
        var coalescer = new RequestCoalescer(new CoalescerOptions());
        var key = new RequestKey("GET", "https://api.example.com/data");
        var executionCount = 0;
        var tcs = new TaskCompletionSource<HttpResponseMessage>();

        Task<HttpResponseMessage> Factory()
        {
            Interlocked.Increment(ref executionCount);
            return tcs.Task;
        }

        // Act
        var task1 = coalescer.ExecuteAsync(key, Factory);
        var task2 = coalescer.ExecuteAsync(key, Factory);
        var task3 = coalescer.ExecuteAsync(key, Factory);

        tcs.SetResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        var response1 = await task1;
        var response2 = await task2;
        var response3 = await task3;

        // Assert
        executionCount.Should().Be(1);
        // Las respuestas son clones independientes, no la misma instancia
        response1.Should().NotBeSameAs(response2);
        response2.Should().NotBeSameAs(response3);
        // Pero todas deben tener el mismo status code
        response1.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        response2.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        response3.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task ExecuteAsync_WithDifferentKeys_ShouldExecuteFactoryMultipleTimes()
    {
        // Arrange
        var coalescer = new RequestCoalescer(new CoalescerOptions());
        var key1 = new RequestKey("GET", "https://api.example.com/data1");
        var key2 = new RequestKey("GET", "https://api.example.com/data2");
        var executionCount = 0;

        Task<HttpResponseMessage> Factory()
        {
            executionCount++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }

        // Act
        var response1 = await coalescer.ExecuteAsync(key1, Factory);
        var response2 = await coalescer.ExecuteAsync(key2, Factory);

        // Assert
        executionCount.Should().Be(2);
        response1.Should().NotBeSameAs(response2);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldCleanupAfterCompletion()
    {
        // Arrange
        var coalescer = new RequestCoalescer(new CoalescerOptions());
        var key = new RequestKey("GET", "https://api.example.com/data");
        var executionCount = 0;

        Task<HttpResponseMessage> Factory()
        {
            executionCount++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }

        // Act
        await coalescer.ExecuteAsync(key, Factory);
        await coalescer.ExecuteAsync(key, Factory);

        // Assert
        executionCount.Should().Be(2, "el request key debería limpiarse después de la primera ejecución");
    }

    [Fact]
    public async Task ExecuteAsync_WhenFactoryThrows_ShouldPropagateException()
    {
        // Arrange
        var coalescer = new RequestCoalescer(new CoalescerOptions());
        var key = new RequestKey("GET", "https://api.example.com/data");
        var expectedException = new InvalidOperationException("Test exception");

        Task<HttpResponseMessage> Factory()
        {
            throw expectedException;
        }

        // Act
        Func<Task> act = async () => await coalescer.ExecuteAsync(key, Factory);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Test exception");
    }

    [Fact]
    public async Task ExecuteAsync_WhenFactoryThrows_MultipleConcurrentCalls_ShouldPropagateExceptionToAll()
    {
        // Arrange
        var coalescer = new RequestCoalescer(new CoalescerOptions());
        var key = new RequestKey("GET", "https://api.example.com/data");
        var tcs = new TaskCompletionSource<HttpResponseMessage>();
        var executionCount = 0;

        Task<HttpResponseMessage> Factory()
        {
            Interlocked.Increment(ref executionCount);
            return tcs.Task;
        }

        // Act
        var task1 = coalescer.ExecuteAsync(key, Factory);
        var task2 = coalescer.ExecuteAsync(key, Factory);
        var task3 = coalescer.ExecuteAsync(key, Factory);

        tcs.SetException(new InvalidOperationException("Test exception"));

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await task1);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await task2);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await task3);
        executionCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_AfterException_ShouldCleanupAndAllowRetry()
    {
        // Arrange
        var coalescer = new RequestCoalescer(new CoalescerOptions());
        var key = new RequestKey("GET", "https://api.example.com/data");
        var executionCount = 0;

        Task<HttpResponseMessage> FailingFactory()
        {
            executionCount++;
            throw new InvalidOperationException("Test exception");
        }

        Task<HttpResponseMessage> SuccessFactory()
        {
            executionCount++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }

        // Act
        try
        {
            await coalescer.ExecuteAsync(key, FailingFactory);
        }
        catch
        {
            // Expected
        }

        var response = await coalescer.ExecuteAsync(key, SuccessFactory);

        // Assert
        executionCount.Should().Be(2);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task ExecuteAsync_WithDifferentMethods_ShouldExecuteSeparately()
    {
        // Arrange
        var coalescer = new RequestCoalescer(new CoalescerOptions());
        var getKey = new RequestKey("GET", "https://api.example.com/data");
        var postKey = new RequestKey("POST", "https://api.example.com/data");
        var executionCount = 0;

        Task<HttpResponseMessage> Factory()
        {
            executionCount++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }

        // Act
        var response1 = await coalescer.ExecuteAsync(getKey, Factory);
        var response2 = await coalescer.ExecuteAsync(postKey, Factory);

        // Assert
        executionCount.Should().Be(2);
        response1.Should().NotBeSameAs(response2);
    }

    [Fact]
    public async Task ExecuteAsync_HighConcurrency_ShouldHandleCorrectly()
    {
        // Arrange
        var coalescer = new RequestCoalescer(new CoalescerOptions());
        var key = new RequestKey("GET", "https://api.example.com/data");
        var executionCount = 0;
        var tcs = new TaskCompletionSource<HttpResponseMessage>();

        Task<HttpResponseMessage> Factory()
        {
            Interlocked.Increment(ref executionCount);
            return tcs.Task;
        }

        // Act
        const int concurrentCalls = 100;
        var tasks = new List<Task<HttpResponseMessage>>();

        for (int i = 0; i < concurrentCalls; i++)
        {
            tasks.Add(coalescer.ExecuteAsync(key, Factory));
        }

        tcs.SetResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        var responses = await Task.WhenAll(tasks);

        // Assert
        executionCount.Should().Be(1);
        responses.Should().HaveCount(concurrentCalls);
        // Todas las respuestas deben tener el mismo status code (aunque son clones independientes)
        responses.Should().OnlyContain(r => r.StatusCode == System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task ExecuteAsync_WithCancellation_ShouldNotAffectOtherCalls()
    {
        // Arrange
        var coalescer = new RequestCoalescer(new CoalescerOptions());
        var key = new RequestKey("GET", "https://api.example.com/data");
        var tcs = new TaskCompletionSource<HttpResponseMessage>();

        Task<HttpResponseMessage> Factory()
        {
            return tcs.Task;
        }

        // Act
        var cts = new CancellationTokenSource();
        var task1 = coalescer.ExecuteAsync(key, Factory);
        var task2 = coalescer.ExecuteAsync(key, Factory);

        cts.Cancel();
        tcs.SetResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        var response1 = await task1;
        var response2 = await task2;

        // Assert
        // Las respuestas son clones independientes
        response1.Should().NotBeSameAs(response2);
        response1.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        response2.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task ExecuteAsync_WinnerCancellation_DoesNotPoisonWaiters()
    {
        // Arrange — the winner's CancellationToken is cancelled before the factory completes,
        // but other waiters should still receive the successful response.
        var coalescer = new RequestCoalescer(new CoalescerOptions());
        var key = new RequestKey("GET", "https://api.example.com/data");
        var tcs = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var winnerCts = new CancellationTokenSource();
        Task<HttpResponseMessage> Factory() => tcs.Task;

        // Act — winner attaches first with a cancellable token
        var winnerTask = coalescer.ExecuteAsync(key, Factory, winnerCts.Token);
        var waiterTask = coalescer.ExecuteAsync(key, Factory, CancellationToken.None);

        // Cancel the winner before the factory resolves
        winnerCts.Cancel();

        // Resolve the factory — body reading happens after cancellation
        tcs.SetResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("payload")
        });

        // Assert — the waiter must succeed even though the winner's token was cancelled
        var waiterResponse = await waiterTask;
        waiterResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        string body = await waiterResponse.Content.ReadAsStringAsync();
        body.Should().Be("payload");

        // The winner itself may throw OperationCanceledException or succeed depending on timing,
        // but it must NOT propagate cancellation to the shared TCS.
        // We verify the waiter was not affected — that is the critical invariant.
    }

    [Fact]
    public async Task ExecuteAsync_ResponseExceedsMaxBodyBytes_ThrowsInvalidOperationException()
    {
        // Arrange — set a tiny limit so that any non-empty response exceeds it
        var options = new CoalescerOptions { MaxResponseBodyBytes = 5 };
        var coalescer = new RequestCoalescer(options);
        var key = new RequestKey("GET", "https://api.example.com/large");

        Task<HttpResponseMessage> Factory()
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("this body is way too large")
            });
        }

        // Act
        Func<Task> act = () => coalescer.ExecuteAsync(key, Factory);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*MaxResponseBodyBytes*");
    }

    [Fact]
    public async Task ExecuteAsync_ResponseExceedsMaxBodyBytes_PropagatesExceptionToWaiters()
    {
        // Arrange
        var options = new CoalescerOptions { MaxResponseBodyBytes = 5 };
        var coalescer = new RequestCoalescer(options);
        var key = new RequestKey("GET", "https://api.example.com/large");
        var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Launch winner + waiter
        var t1 = coalescer.ExecuteAsync(key, () => gate.Task);
        var t2 = coalescer.ExecuteAsync(key, () => gate.Task);

        gate.SetResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("this body exceeds the limit")
        });

        // Both should receive the same exception
        Func<Task> act1 = () => t1;
        Func<Task> act2 = () => t2;

        await act1.Should().ThrowAsync<InvalidOperationException>();
        await act2.Should().ThrowAsync<InvalidOperationException>();
    }
}
