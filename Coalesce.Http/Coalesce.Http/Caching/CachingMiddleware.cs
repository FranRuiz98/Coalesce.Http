using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Caching.Memory;

namespace Coalesce.Http.Coalesce.Http.Caching;

internal sealed class CachingMiddleware(IMemoryCache cache,
                                        ICacheKeyBuilder keyBuilder,
                                        CacheOptions options) : DelegatingHandler
{
    private static bool IsRequestCacheable(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Get)
        {
            return false;
        }

        if (request.Headers.Authorization is not null)
        {
            return false;
        }

        if (request.Content is not null)
        {
            return false;
        }

        CacheControlHeaderValue? cacheControl = request.Headers.CacheControl;

        return (cacheControl?.NoStore) != true;
    }

    private static bool IsResponseCacheable(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return false;
        }

        CacheControlHeaderValue? cacheControl = response.Headers.CacheControl;

        return (cacheControl?.NoStore) != true;
    }

    private static HttpResponseMessage CreateResponse(CacheEntry entry)
    {
        HttpResponseMessage response = new((HttpStatusCode)entry.StatusCode)
        {
            Content = new ByteArrayContent(entry.Body)
        };

        foreach (KeyValuePair<string, string[]> header in entry.Headers)
        {
            _ = response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return response;
    }

    private async Task StoreAsync(string key, HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content is null)
        {
            return;
        }

        byte[] body = await response.Content.ReadAsByteArrayAsync(ct);

        response.Content = new ByteArrayContent(body);

        if (body.Length > options.MaxBodySizeBytes)
        {
            return;
        }

        CacheEntry entry = new()
        {
            StatusCode = (int)response.StatusCode,
            Body = body,
            Headers = ExtractHeaders(response),
            ExpiresAt = DateTimeOffset.UtcNow + options.DefaultTtl
        };

        _ = cache.Set(key, entry, entry.ExpiresAt);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!IsRequestCacheable(request))
        {
            return await base.SendAsync(request, ct);
        }

        string key = keyBuilder.Build(request);

        // Cache Hit
        cache.TryGetValue(key, out CacheEntry? entry);

        if (entry is not null)
        {
            return CreateResponse(entry);
        }

        // Cache Miss
        HttpResponseMessage response = await base.SendAsync(request, ct);

        if (IsResponseCacheable(response))
        {
            await StoreAsync(key, response, ct);
        }

        return response;
    }

    private static Dictionary<string, string[]> ExtractHeaders(HttpResponseMessage response)
    {
        Dictionary<string, string[]> headers = new(16, StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
        {
            headers[header.Key] = header.Value.ToArray();
        }

        if (response.Content != null)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Content.Headers)
            {
                headers[header.Key] = header.Value.ToArray();
            }
        }

        return headers;
    }
}
