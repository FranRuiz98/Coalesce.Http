using Coalesce.Http.Coalesce.Http.Coalescing;
using FluentAssertions;

namespace Coalesce.Http.Tests.Coalescing;

public class RequestKeyTests
{
    [Fact]
    public void Constructor_ShouldInitializeMethodAndUrl()
    {
        // Arrange
        const string method = "GET";
        const string url = "https://api.example.com/products";

        // Act
        var key = new RequestKey(method, url);

        // Assert
        key.Method.Should().Be(method);
        key.Url.Should().Be(url);
    }

    [Fact]
    public void ToString_ShouldReturnFormattedString()
    {
        // Arrange
        var key = new RequestKey("POST", "https://api.example.com/orders");

        // Act
        var result = key.ToString();

        // Assert
        result.Should().Be("POST https://api.example.com/orders");
    }

    [Fact]
    public void Create_ShouldCreateKeyFromHttpRequestMessage()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/users/123");

        // Act
        var key = RequestKey.Create(request);

        // Assert
        key.Method.Should().Be("GET");
        key.Url.Should().Be("https://api.example.com/users/123");
    }

    [Theory]
    [InlineData("GET", "https://api.example.com/products")]
    [InlineData("POST", "https://api.example.com/orders")]
    [InlineData("PUT", "https://api.example.com/users/1")]
    [InlineData("DELETE", "https://api.example.com/items/42")]
    public void Create_ShouldHandleDifferentHttpMethods(string method, string url)
    {
        // Arrange
        var httpMethod = new HttpMethod(method);
        var request = new HttpRequestMessage(httpMethod, url);

        // Act
        var key = RequestKey.Create(request);

        // Assert
        key.Method.Should().Be(method);
        key.Url.Should().Be(url);
    }

    [Fact]
    public void Equality_SameMethodAndUrl_ShouldBeEqual()
    {
        // Arrange
        var key1 = new RequestKey("GET", "https://api.example.com/data");
        var key2 = new RequestKey("GET", "https://api.example.com/data");

        // Act & Assert
        key1.Should().Be(key2);
        (key1 == key2).Should().BeTrue();
    }

    [Fact]
    public void Equality_DifferentMethod_ShouldNotBeEqual()
    {
        // Arrange
        var key1 = new RequestKey("GET", "https://api.example.com/data");
        var key2 = new RequestKey("POST", "https://api.example.com/data");

        // Act & Assert
        key1.Should().NotBe(key2);
        (key1 != key2).Should().BeTrue();
    }

    [Fact]
    public void Equality_DifferentUrl_ShouldNotBeEqual()
    {
        // Arrange
        var key1 = new RequestKey("GET", "https://api.example.com/data1");
        var key2 = new RequestKey("GET", "https://api.example.com/data2");

        // Act & Assert
        key1.Should().NotBe(key2);
        (key1 != key2).Should().BeTrue();
    }

    [Fact]
    public void GetHashCode_SameMethodAndUrl_ShouldReturnSameHashCode()
    {
        // Arrange
        var key1 = new RequestKey("GET", "https://api.example.com/data");
        var key2 = new RequestKey("GET", "https://api.example.com/data");

        // Act & Assert
        key1.GetHashCode().Should().Be(key2.GetHashCode());
    }

    [Fact]
    public void GetHashCode_DifferentValues_ShouldReturnDifferentHashCode()
    {
        // Arrange
        var key1 = new RequestKey("GET", "https://api.example.com/data1");
        var key2 = new RequestKey("POST", "https://api.example.com/data2");

        // Act & Assert
        key1.GetHashCode().Should().NotBe(key2.GetHashCode());
    }

    [Fact]
    public void Create_WithQueryParameters_ShouldPreserveFullUrl()
    {
        // Arrange
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.example.com/search?q=test&page=1&limit=10");

        // Act
        var key = RequestKey.Create(request);

        // Assert
        key.Url.Should().Be("https://api.example.com/search?q=test&page=1&limit=10");
    }

    [Fact]
    public void Create_WithFragment_ShouldPreserveFullUrl()
    {
        // Arrange
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.example.com/page#section");

        // Act
        var key = RequestKey.Create(request);

        // Assert
        key.Url.Should().Be("https://api.example.com/page#section");
    }
}
