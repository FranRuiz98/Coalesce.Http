using Stampede.Http.Coalescing;
using FluentAssertions;

namespace Stampede.Http.Tests.Coalescing;

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

    // ── Authorization credential isolation (2.4) ─────────────────────────────
    //
    // These use the (request, keyHeaders) overload: Authorization is folded in unconditionally there,
    // independent of CacheOptions.AuthorizationCaching — coalescing must never merge two different
    // credentials' requests into one shared origin call, whether or not caching itself is enabled.

    [Fact]
    public void Create_WithKeyHeaders_DifferentAuthorizationValues_ProduceDifferentKeys()
    {
        using var request1 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request1.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "user-a-token");

        using var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request2.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "user-b-token");

        RequestKey key1 = RequestKey.Create(request1, keyHeaders: null);
        RequestKey key2 = RequestKey.Create(request2, keyHeaders: null);

        key1.Should().NotBe(key2, "two different credentials for the same URL must never be coalesced together");
    }

    [Fact]
    public void Create_WithKeyHeaders_SameAuthorizationValue_ProducesSameKey()
    {
        using var request1 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request1.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "user-a-token");

        using var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request2.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "user-a-token");

        RequestKey key1 = RequestKey.Create(request1, keyHeaders: null);
        RequestKey key2 = RequestKey.Create(request2, keyHeaders: null);

        key1.Should().Be(key2, "identical credentials for the same URL are still coalesceable");
    }

    [Fact]
    public void Create_WithKeyHeaders_AuthorizationHash_NeverContainsRawCredential()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "super-secret-token");

        RequestKey key = RequestKey.Create(request, keyHeaders: null);

        key.ToString().Should().NotContain("super-secret-token",
            "the raw credential must never surface in RequestKey.ToString(), which feeds debug log lines");
    }

    [Fact]
    public void Create_WithKeyHeaders_AuthorizedAndUnauthorizedRequests_ProduceDifferentKeys()
    {
        using var authorized = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        authorized.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "token");

        using var anonymous = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");

        RequestKey authorizedKey = RequestKey.Create(authorized, keyHeaders: null);
        RequestKey anonymousKey = RequestKey.Create(anonymous, keyHeaders: null);

        authorizedKey.Should().NotBe(anonymousKey);
    }
}
