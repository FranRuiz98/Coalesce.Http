using Coalesce.Http.Caching;
using FluentAssertions;

namespace Coalesce.Http.Tests.Caching;

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
}
