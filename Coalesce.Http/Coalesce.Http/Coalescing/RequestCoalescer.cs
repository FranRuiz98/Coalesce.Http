using Coalesce.Http.Coalesce.Http.Metrics;
using Coalesce.Http.Coalesce.Http.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;

namespace Coalesce.Http.Coalesce.Http.Coalescing;

/// <summary>
/// High-performance request coalescer using TaskCompletionSource pattern.
/// This implementation minimizes allocations and contention under extreme load (100k+ RPS).
/// </summary>
public sealed partial class RequestCoalescer(CoalescerOptions options, CoalesceHttpMetrics? metrics = null, ILogger<RequestCoalescer>? logger = null)
{
    private readonly ILogger logger = logger ?? NullLogger<RequestCoalescer>.Instance;
    private readonly ConcurrentDictionary<RequestKey, CoalescedRequest> _inflight = new();

    /// <summary>
    /// Executes a request with coalescing. Multiple concurrent calls with the same key
    /// will share a single execution, but each caller receives an independent cloned response.
    /// </summary>
    /// <param name="key">The request key for coalescing</param>
    /// <param name="factory">Factory function to execute the actual HTTP request</param>
    /// <returns>A cloned HttpResponseMessage for this caller</returns>
    public async Task<HttpResponseMessage> ExecuteAsync(
        RequestKey key,
        Func<Task<HttpResponseMessage>> factory,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            // If there's already an inflight request for this key, wait for it to complete and return a clone of the response
            if (_inflight.TryGetValue(key, out CoalescedRequest? existing))
            {
                metrics?.RecordCoalescedDeduplicated();
                LogCoalescedWaiter(key);

                try
                {
                    CachedResponse cachedResponse = options.CoalescingTimeout is TimeSpan timeout
                        ? await existing.Tcs.Task
                            .WaitAsync(timeout, cancellationToken)
                            .ConfigureAwait(false)
                        : await existing.Tcs.Task
                            .WaitAsync(cancellationToken)
                            .ConfigureAwait(false);

                    LogCoalescedWaiterCompleted(key);
                    return cachedResponse.ToHttpResponseMessage();
                }
                catch (TimeoutException)
                {
                    LogCoalescedWaiterTimeout(key);
                    // Timeout waiting for the winner — fall through to execute independently
                    break;
                }
            }

            // Try being winner for this key
            CoalescedRequest coalescedRequest = new();

            if (!_inflight.TryAdd(key, coalescedRequest))
            {
                // Another thread won the race - try again to get the existing request
                continue;
            }

            metrics?.IncrementInflight();
            LogWinnerStart(key);

            // We are the winner - execute the factory and set the result for all waiters
            try
            {
                // Execute the factory to get the response without using the cancellation token, as the factory is responsible for handling cancellation if needed.
                // We want to ensure that the response is cached for all waiters even if the caller cancels.
                using HttpResponseMessage response = await factory().ConfigureAwait(false);

                // Cache the response for other waiters. We read the entire response into memory to allow cloning for multiple callers.
                // Solves the problem of HttpResponseMessage being a one-time-use object that can't be shared across multiple callers.
                CachedResponse cachedResponse = await CachedResponse.FromResponseAsync(response, cancellationToken).ConfigureAwait(false);

                coalescedRequest.Tcs.SetResult(cachedResponse);

                LogWinnerCompleted(key, (int)response.StatusCode);
                return cachedResponse.ToHttpResponseMessage();
            }
            catch (Exception ex)
            {
                coalescedRequest.Tcs.SetException(ex);
                LogWinnerError(key, ex);
                throw;
            }
            finally
            {
                _inflight.TryRemove(key, out _);
                metrics?.DecrementInflight();
            }
        }

        // Timeout fallback: execute the factory independently (no coalescing)
        LogTimeoutFallbackStart(key);
        using HttpResponseMessage fallbackResponse = await factory().ConfigureAwait(false);
        CachedResponse fallbackCached = await CachedResponse.FromResponseAsync(fallbackResponse, cancellationToken).ConfigureAwait(false);
        return fallbackCached.ToHttpResponseMessage();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Coalescing: waiter attached for {Key}")]
    private partial void LogCoalescedWaiter(RequestKey key);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Coalescing: waiter received response for {Key}")]
    private partial void LogCoalescedWaiterCompleted(RequestKey key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Coalescing: waiter timed out for {Key}, falling back to independent request")]
    private partial void LogCoalescedWaiterTimeout(RequestKey key);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Coalescing: winner executing request for {Key}")]
    private partial void LogWinnerStart(RequestKey key);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Coalescing: winner completed {Key} with status {StatusCode}")]
    private partial void LogWinnerCompleted(RequestKey key, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Coalescing: winner failed for {Key}")]
    private partial void LogWinnerError(RequestKey key, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Coalescing: timeout fallback executing independent request for {Key}")]
    private partial void LogTimeoutFallbackStart(RequestKey key);
}
