using System.Collections.Concurrent;

namespace Coalesce.Http.Coalesce.Http.Coalescing;

/// <summary>
/// High-performance request coalescer using TaskCompletionSource pattern.
/// This implementation minimizes allocations and contention under extreme load (100k+ RPS).
/// </summary>
public sealed partial class RequestCoalescer
{
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
                CachedResponse cachedResponse = await existing.Tcs.Task
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);

                return cachedResponse.ToHttpResponseMessage();
            }

            // Try being winner for this key
            CoalescedRequest coalescedRequest = new();

            if (!_inflight.TryAdd(key, coalescedRequest))
            {
                // Another thread won the race - try again to get the existing request
                continue;
            }

            // We are the winner - execute the factory and set the result for all waiters
            try
            {
                // Execute the factory to get the response without using the cancellation token, as the factory is responsible for handling cancellation if needed.
                // We want to ensure that the response is cached for all waiters even if the caller cancels.
                using HttpResponseMessage response = await factory().ConfigureAwait(false);

                // Cache the response for other waiters. We read the entire response into memory to allow cloning for multiple callers.
                // Solves the problem of HttpResponseMessage being a one-time-use object that can't be shared across multiple callers.
                CachedResponse cachedResponse = await CachedResponse.FromResponseAsync(response).ConfigureAwait(false);

                coalescedRequest.Tcs.SetResult(cachedResponse);

                return cachedResponse.ToHttpResponseMessage();
            }
            catch (Exception ex)
            {
                coalescedRequest.Tcs.SetException(ex);
                throw;
            }
            finally
            {
                _inflight.TryRemove(key, out _);
            }
        }
    }
}
