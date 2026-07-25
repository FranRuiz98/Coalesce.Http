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

            // Let the refresh finish and release its claim before scheduling the next one.
            await Task.Delay(20, TestContext.Current.CancellationToken);
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

        await Task.Delay(50, TestContext.Current.CancellationToken);

        coordinator.Schedule("key", () =>
        {
            _ = Interlocked.Increment(ref started);
            return Task.CompletedTask;
        });

        await Task.Delay(50, TestContext.Current.CancellationToken);

        Volatile.Read(ref started).Should().Be(2, "a failed refresh must not block the key permanently");
    }
}
