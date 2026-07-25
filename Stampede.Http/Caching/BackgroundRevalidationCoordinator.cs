using System.Collections.Concurrent;

namespace Stampede.Http.Caching;

/// <summary>
/// Tracks which cache keys currently have a stale-while-revalidate background refresh in flight
/// (RFC 5861 §3), so a given key is refreshed at most once at a time.
/// </summary>
/// <remarks>
/// <para>
/// This state deliberately lives outside <see cref="CachingMiddleware"/>. <c>IHttpClientFactory</c> rotates
/// handler chains — every two minutes by default — and keeps expired chains alive while their handlers are
/// still in use, so several <see cref="CachingMiddleware"/> instances can serve the same named client
/// concurrently. A dictionary owned by the handler would therefore deduplicate only within one chain, letting
/// two chains revalidate the same key simultaneously: exactly the duplicated origin load that
/// stale-while-revalidate exists to avoid. Registering this type as a per-client singleton makes the
/// deduplication hold for the lifetime of the client.
/// </para>
/// </remarks>
internal sealed class BackgroundRevalidationCoordinator
{
    private readonly ConcurrentDictionary<string, byte> _inflight = new(StringComparer.Ordinal);

    /// <summary>
    /// Runs <paramref name="revalidate"/> as a fire-and-forget background refresh for <paramref name="key"/>,
    /// unless a refresh for that key is already in flight.
    /// </summary>
    /// <remarks>
    /// The key is claimed before the work is started, never from inside it: a refresh that finished before its
    /// own registration completed would otherwise leave the key claimed forever, permanently blocking further
    /// background revalidation of that entry.
    /// </remarks>
    /// <param name="key">The cache key being refreshed.</param>
    /// <param name="revalidate">
    /// The refresh to run. It is responsible for handling its own failures; any exception that escapes is
    /// swallowed here so it cannot surface as an unobserved task exception.
    /// </param>
    public void Schedule(string key, Func<Task> revalidate)
    {
        if (!_inflight.TryAdd(key, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await revalidate().ConfigureAwait(false);
            }
            catch
            {
                // The caller logs its own failures; nothing observes this task.
            }
            finally
            {
                _ = _inflight.TryRemove(key, out _);
            }
        });
    }
}
