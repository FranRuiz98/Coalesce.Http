namespace Coalesce.Http.Tests.Helpers;

/// <summary>
/// A controllable <see cref="TimeProvider"/> for deterministic time-based tests.
/// Call <see cref="Advance"/> to move the clock forward without sleeping.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public FakeTimeProvider(DateTimeOffset startTime)
    {
        _utcNow = startTime;
    }

    public FakeTimeProvider() : this(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    /// <summary>Moves the clock forward by <paramref name="delta"/>.</summary>
    public void Advance(TimeSpan delta) => _utcNow += delta;
}
