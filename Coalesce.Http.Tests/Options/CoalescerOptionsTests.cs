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
}
