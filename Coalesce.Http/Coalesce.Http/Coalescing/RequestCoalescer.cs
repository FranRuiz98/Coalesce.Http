using System.Collections.Concurrent;

namespace Coalesce.Http.Coalesce.Http.Coalescing;

public sealed class RequestCoalescer
{
    private readonly ConcurrentDictionary<RequestKey, Lazy<Task<HttpResponseMessage>>> _inflight = new();

    public Task<HttpResponseMessage> ExecuteAsync(RequestKey key, Func<Task<HttpResponseMessage>> factory)
    {
        Lazy<Task<HttpResponseMessage>> lazyTask = _inflight.GetOrAdd(
            key,
            _ => new Lazy<Task<HttpResponseMessage>>(() => factory()));

        return AwaitAndCleanup(key, lazyTask);
    }

    private async Task<HttpResponseMessage> AwaitAndCleanup(RequestKey key, Lazy<Task<HttpResponseMessage>> lazyTask)
    {
        try
        {
            return await lazyTask.Value;
        }
        finally
        {
            _ = _inflight.TryRemove(key, out _);
        }
    }
}
