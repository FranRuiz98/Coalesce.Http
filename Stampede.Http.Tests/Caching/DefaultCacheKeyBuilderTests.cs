using Stampede.Http.Caching;
using FluentAssertions;

namespace Stampede.Http.Tests.Caching;

public sealed class DefaultCacheKeyBuilderTests
{
    private readonly DefaultCacheKeyBuilder _builder = new();

    [Fact]
    public void Build_GetRequest_ReturnsMethodColonAbsoluteUri()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/products/42");

        string key = _builder.Build(request);

        key.Should().Be("GET:https://api.example.com/products/42");
    }

    [Fact]
    public void Build_PostRequest_IncludesMethodInKey()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/products");

        string key = _builder.Build(request);

        key.Should().Be("POST:https://api.example.com/products");
    }

    [Fact]
    public void Build_DifferentUrls_ProduceDifferentKeys()
    {
        using var request1 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/a");
        using var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/b");

        string key1 = _builder.Build(request1);
        string key2 = _builder.Build(request2);

        key1.Should().NotBe(key2);
    }

    [Fact]
    public void Build_SameMethodAndUrl_ProduceSameKey()
    {
        using var request1 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        using var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");

        string key1 = _builder.Build(request1);
        string key2 = _builder.Build(request2);

        key1.Should().Be(key2);
    }

    [Fact]
    public void Build_QueryStringIncluded_DifferentiatesKeys()
    {
        using var request1 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data?page=1");
        using var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data?page=2");

        string key1 = _builder.Build(request1);
        string key2 = _builder.Build(request2);

        key1.Should().NotBe(key2);
    }

    [Fact]
    public void Build_NullRequestUri_ReturnsMethodColonEmpty()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, (Uri?)null);

        string key = _builder.Build(request);

        key.Should().Be("GET:");
    }

    [Fact]
    public void Build_ImplementsICacheKeyBuilder()
    {
        _builder.Should().BeAssignableTo<ICacheKeyBuilder>();
    }

    // ── Authorization credential isolation (2.4) ─────────────────────────────

    [Fact]
    public void Build_NoAuthorization_KeyUnaffected()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");

        string key = _builder.Build(request);

        key.Should().Be("GET:https://api.example.com/data", "no Authorization header means no suffix, matching pre-2.4 keys exactly");
    }

    [Fact]
    public void Build_WithAuthorization_AppendsHashSuffix_NotRawValue()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "super-secret-token");

        string key = _builder.Build(request);

        key.Should().StartWith("GET:https://api.example.com/data");
        key.Should().NotContain("super-secret-token", "the raw credential must never appear in the cache key");
        key.Should().Contain("auth=", "a credential fingerprint must be folded into the key");
    }

    [Fact]
    public void Build_DifferentAuthorizationValues_ProduceDifferentKeys()
    {
        using var request1 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request1.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "token-a");

        using var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request2.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "token-b");

        string key1 = _builder.Build(request1);
        string key2 = _builder.Build(request2);

        key1.Should().NotBe(key2, "different credentials for the same URL must never share a cache entry");
    }

    [Fact]
    public void Build_SameAuthorizationValue_ProducesSameKey()
    {
        using var request1 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request1.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "token-a");

        using var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request2.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "token-a");

        string key1 = _builder.Build(request1);
        string key2 = _builder.Build(request2);

        key1.Should().Be(key2, "the same credential must deterministically hash to the same key");
    }

    [Fact]
    public void Build_AuthorizedAndUnauthorizedRequests_ProduceDifferentKeys()
    {
        using var authorized = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        authorized.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "token-a");

        using var anonymous = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");

        string authorizedKey = _builder.Build(authorized);
        string anonymousKey = _builder.Build(anonymous);

        authorizedKey.Should().NotBe(anonymousKey, "an authenticated response must never be served to an unauthenticated caller or vice versa");
    }

    [Fact]
    public void Build_DifferentAuthenticationSchemes_SameToken_ProduceDifferentKeys()
    {
        using var bearer = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        bearer.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "abc123");

        using var basic = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        basic.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", "abc123");

        string bearerKey = _builder.Build(bearer);
        string basicKey = _builder.Build(basic);

        bearerKey.Should().NotBe(basicKey, "the scheme is part of the hashed value, not just the token");
    }
}
