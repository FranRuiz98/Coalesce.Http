namespace Coalesce.Http.Caching;

public sealed class DefaultCacheKeyBuilder : ICacheKeyBuilder
{
    public string Build(HttpRequestMessage request)
    {
        return string.Concat(request.Method.Method, ":", request.RequestUri?.AbsoluteUri ?? string.Empty);
    }
}
