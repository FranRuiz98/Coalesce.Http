using Coalesce.Http.Coalesce.Http.Options;
using FluentAssertions;

namespace Coalesce.Http.Tests.Options;

public class CoalescerOptionsTests
{
    [Fact]
    public void Constructor_ShouldInitializeWithDefaultValues()
    {
        // Act
        var options = new CoalescerOptions();

        // Assert
        options.Enabled.Should().BeTrue("Enabled debería estar en true por defecto");
    }

    [Fact]
    public void Enabled_ShouldBeSettable()
    {
        // Arrange
        var options = new CoalescerOptions();

        // Act
        options.Enabled = false;

        // Assert
        options.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Enabled_ShouldAcceptTrue()
    {
        // Arrange
        var options = new CoalescerOptions();

        // Act
        options.Enabled = true;

        // Assert
        options.Enabled.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Enabled_ShouldStoreCorrectValue(bool value)
    {
        // Arrange
        var options = new CoalescerOptions();

        // Act
        options.Enabled = value;

        // Assert
        options.Enabled.Should().Be(value);
    }

    [Fact]
    public void Options_ShouldSupportObjectInitializer()
    {
        // Act
        var options = new CoalescerOptions
        {
            Enabled = false
        };

        // Assert
        options.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Options_MultipleInstances_ShouldBeIndependent()
    {
        // Arrange
        var options1 = new CoalescerOptions { Enabled = true };
        var options2 = new CoalescerOptions { Enabled = false };

        // Assert
        options1.Enabled.Should().BeTrue();
        options2.Enabled.Should().BeFalse();
        options1.Enabled.Should().NotBe(options2.Enabled);
    }

    // ── CoalescingTimeout validation ──────────────────────────────────────────

    [Fact]
    public void CoalescingTimeout_Null_IsAllowed()
    {
        var options = new CoalescerOptions { CoalescingTimeout = null };
        options.CoalescingTimeout.Should().BeNull();
    }

    [Fact]
    public void CoalescingTimeout_PositiveValue_IsAccepted()
    {
        var ts = TimeSpan.FromSeconds(5);
        var options = new CoalescerOptions { CoalescingTimeout = ts };
        options.CoalescingTimeout.Should().Be(ts);
    }

    [Fact]
    public void CoalescingTimeout_Zero_Throws()
    {
        var options = new CoalescerOptions();
        var act = () => options.CoalescingTimeout = TimeSpan.Zero;
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CoalescingTimeout_Negative_Throws()
    {
        var options = new CoalescerOptions();
        var act = () => options.CoalescingTimeout = TimeSpan.FromSeconds(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── MaxResponseBodyBytes validation ───────────────────────────────────────

    [Fact]
    public void MaxResponseBodyBytes_DefaultIs1MB()
    {
        var options = new CoalescerOptions();
        options.MaxResponseBodyBytes.Should().Be(1024 * 1024);
    }

    [Fact]
    public void MaxResponseBodyBytes_Zero_IsAllowed()
    {
        var options = new CoalescerOptions { MaxResponseBodyBytes = 0 };
        options.MaxResponseBodyBytes.Should().Be(0);
    }

    [Fact]
    public void MaxResponseBodyBytes_PositiveValue_IsAccepted()
    {
        var options = new CoalescerOptions { MaxResponseBodyBytes = 512 };
        options.MaxResponseBodyBytes.Should().Be(512);
    }

    [Fact]
    public void MaxResponseBodyBytes_Negative_Throws()
    {
        var options = new CoalescerOptions();
        var act = () => options.MaxResponseBodyBytes = -1;
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
