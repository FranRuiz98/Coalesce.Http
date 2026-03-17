using Coalesce.Http.Caching;
using FluentAssertions;

namespace Coalesce.Http.Tests.Options;

public class CacheOptionsTests
{
    // ── DefaultTtl ────────────────────────────────────────────────────────────

    [Fact]
    public void DefaultTtl_DefaultIs30Seconds()
    {
        var options = new CacheOptions();
        options.DefaultTtl.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void DefaultTtl_PositiveValue_IsAccepted()
    {
        var options = new CacheOptions { DefaultTtl = TimeSpan.FromMinutes(10) };
        options.DefaultTtl.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void DefaultTtl_Zero_Throws()
    {
        var options = new CacheOptions();
        var act = () => options.DefaultTtl = TimeSpan.Zero;
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void DefaultTtl_Negative_Throws()
    {
        var options = new CacheOptions();
        var act = () => options.DefaultTtl = TimeSpan.FromSeconds(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── MaxBodySizeBytes ──────────────────────────────────────────────────────

    [Fact]
    public void MaxBodySizeBytes_DefaultIs1MB()
    {
        var options = new CacheOptions();
        options.MaxBodySizeBytes.Should().Be(1024 * 1024);
    }

    [Fact]
    public void MaxBodySizeBytes_Zero_IsAllowed()
    {
        var options = new CacheOptions { MaxBodySizeBytes = 0 };
        options.MaxBodySizeBytes.Should().Be(0);
    }

    [Fact]
    public void MaxBodySizeBytes_Negative_Throws()
    {
        var options = new CacheOptions();
        var act = () => options.MaxBodySizeBytes = -1;
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── DefaultStaleIfErrorSeconds ────────────────────────────────────────────

    [Fact]
    public void DefaultStaleIfErrorSeconds_DefaultIsZero()
    {
        var options = new CacheOptions();
        options.DefaultStaleIfErrorSeconds.Should().Be(0);
    }

    [Fact]
    public void DefaultStaleIfErrorSeconds_PositiveValue_IsAccepted()
    {
        var options = new CacheOptions { DefaultStaleIfErrorSeconds = 3600 };
        options.DefaultStaleIfErrorSeconds.Should().Be(3600);
    }

    [Fact]
    public void DefaultStaleIfErrorSeconds_Negative_Throws()
    {
        var options = new CacheOptions();
        var act = () => options.DefaultStaleIfErrorSeconds = -1;
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
