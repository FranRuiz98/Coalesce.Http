namespace Coalesce.Http.Caching;

public sealed class DefaultCacheKeyBuilder : ICacheKeyBuilder
{
    public string Build(HttpRequestMessage request)
    {
        string method = request.Method.Method;
        string uri = request.RequestUri?.AbsoluteUri ?? string.Empty;

        return string.Create(method.Length + 1 + uri.Length, (method, uri), static (span, state) =>
        {
            state.method.AsSpan().CopyTo(span);
            span[state.method.Length] = ':';
            state.uri.AsSpan().CopyTo(span[(state.method.Length + 1)..]);
        });
    }
}
