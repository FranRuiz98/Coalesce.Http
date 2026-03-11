namespace Coalesce.Http.Coalesce.Http.Caching;

internal static class CacheKeyBuilder
{
    public static string BuildCacheKey(string method, string path, string queryString)
    {
        return $"{method}:{path}?{queryString}";
    }
}
