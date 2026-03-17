namespace Coalesce.Http.Caching;

public interface ICacheKeyBuilder
{
    string Build(HttpRequestMessage request);
}
