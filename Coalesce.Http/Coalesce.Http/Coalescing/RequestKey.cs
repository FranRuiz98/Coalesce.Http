namespace Coalesce.Http.Coalescing;

internal readonly record struct RequestKey(string Method, string Url)
{
    public override string ToString()
    {
        return $"{Method} {Url}";
    }

    public static RequestKey Create(HttpRequestMessage request)
    {
        return new RequestKey(request.Method.Method, request.RequestUri!.AbsoluteUri);
    }
}
