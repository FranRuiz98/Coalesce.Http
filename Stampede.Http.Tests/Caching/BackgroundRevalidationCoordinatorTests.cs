using Stampede.Http.Caching;
using FluentAssertions;

namespace Stampede.Http.Tests.Caching;

/// <summary>
/// Verifies the stale-while-revalidate deduplication contract (RFC 5861 §3): a key has at most one
/// background refresh in flight, and the claim is always released once that refresh finishes.
/// </summary>
public sealed class BackgroundRevalidationCoordinatorTests
{
    [Fact]
    public async Task Schedule_WhileRefreshInFlight_DoesNotStartASecondOne()
    {
        BackgroundRevalidationCoordinator coordinator = new();
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;

        for (int i = 0; i < 10; i++)
        {
            coordinator.Schedule("key", async () =>
            {
                _ = Interlocked.Increment(ref started);
                await gate.Task;
            });
        }

        // Give the scheduled work a chance to run before asserting.
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Volatile.Read(ref started).Should().Be(1, "a key already being revalidated must not be revalidated again");

        gate.SetResult();
    }

    [Fact]
    public async Task Schedule_DifferentKeys_RunIndependently()
    {
        BackgroundRevalidationCoordinator coordinator = new();
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;

        for (int i = 0; i < 5; i++)
        {
            coordinator.Schedule($"key-{i}", async () =>
            {
                _ = Interlocked.Increment(ref started);
                await gate.Task;
            });
        }

        await Task.Delay(100, TestContext.Current.CancellationToken);

        Volatile.Read(ref started).Should().Be(5, "deduplication is per key, not global");

        gate.SetResult();
    }

    [Fact]
    public async Task Schedule_AfterPreviousRefreshCompletes_StartsAgain()
    {
        BackgroundRevalidationCoordinator coordinator = new();
        int started = 0;

        for (int i = 0; i < 20; i++)
        {
            coordinator.Schedule("key", () =>
            {
                _ = Interlocked.Increment(ref started);
                return Task.CompletedTask;
            });

            // Wait for this refresh to release its key before the next iteration schedules again — a fixed
            // delay is not reliable under CI/parallel-test thread-pool contention. Polling `IsScheduled`
            // rather than retrying `Schedule` itself: a successful `Schedule` call has the side effect of
            // starting another refresh, so retrying it inside the wait would over-count `started`.
            await WaitUntilAsync(() => !coordinator.IsScheduled("key"),
                "each refresh must run and release its key before the next Schedule call is attempted");
        }

        // Regression: claiming the key from inside the background task allowed the refresh to finish before
        // its own registration landed, leaving the key claimed forever and blocking all later revalidations.
        Volatile.Read(ref started).Should().Be(20,
            "each completed refresh must release its key so the entry can be revalidated again");
    }

    [Fact]
    public async Task Schedule_WhenRefreshThrows_ReleasesTheKey()
    {
        BackgroundRevalidationCoordinator coordinator = new();
        int started = 0;

        coordinator.Schedule("key", () =>
        {
            _ = Interlocked.Increment(ref started);
            throw new InvalidOperationException("origin unreachable");
        });

        // Wait for the failed refresh's finally block to release the key before scheduling again — see the
        // reasoning in Schedule_AfterPreviousRefreshCompletes_StartsAgain.
        await WaitUntilAsync(() => !coordinator.IsScheduled("key"),
            "a failed refresh must release its key even though the callback threw");

        coordinator.Schedule("key", () =>
        {
            _ = Interlocked.Increment(ref started);
            return Task.CompletedTask;
        });

        await WaitUntilAsync(() => Volatile.Read(ref started) == 2,
            "a failed refresh must not block the key permanently");
    }

    /// <summary>
    /// Waits for <paramref name="condition"/> to become <see langword="true"/>, polling on a short interval up
    /// to a generous timeout. Used instead of a single fixed delay to avoid flaking under thread-pool
    /// contention from parallel test execution — the condition itself must be read-only, since a condition
    /// with side effects would be retried along with the polling.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (int i = 0; i < 200; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        condition().Should().BeTrue(because);
    }
}
