using System.Diagnostics;
using System.Net;

namespace Stampede.Http.Sample.Client;

/// <summary>
/// The outcome of a single call to the origin, as observed by the caller. Everything the
/// sample wants to show — was it a hit, how old was it, how long did it take — is visible
/// from standard response metadata, which is the point: the caller never touches a cache key.
/// </summary>
/// <param name="Path">Request path.</param>
/// <param name="StatusCode">Status code as seen by the caller.</param>
/// <param name="ElapsedMs">Wall-clock time for the call, including cache lookup.</param>
/// <param name="AgeSeconds">Value of the <c>Age</c> response header; <see langword="null"/> means the response came straight from the origin.</param>
/// <param name="Body">Truncated response body, for the logs.</param>
public sealed record ProbeResult(
    string Path,
    int StatusCode,
    long ElapsedMs,
    double? AgeSeconds,
    string Body)
{
    /// <summary>Whether the response was served from the cache rather than the origin.</summary>
    public bool FromCache => AgeSeconds is not null;

    /// <summary>A short human-readable rendering used in the workload logs.</summary>
    public string Describe()
    {
        string age = AgeSeconds is null ? "fresh from origin" : $"Age: {AgeSeconds.Value:F0}s";
        string stale = AgeSeconds is > 10 ? "  [beyond max-age: stale window or revalidated entry]" : string.Empty;
        return $"{Path,-22} -> {StatusCode}  {ElapsedMs,5} ms  ({age}){stale}  {Body}";
    }
}

/// <summary>
/// Typed client for the sample origin. The Stampede.Http pipeline sits underneath this class
/// and is entirely invisible to it — no cache keys, no TTLs, no invalidation calls.
/// </summary>
public sealed class OriginClient(HttpClient http)
{
    /// <summary>Maximum number of body characters kept for logging.</summary>
    private const int BodySnippetLength = 60;

    /// <summary>Gets the underlying client, for the rare call that needs full control.</summary>
    public HttpClient Http { get; } = http;

    /// <summary>
    /// Issues a GET and summarises the result. <paramref name="configure"/> can set request
    /// headers or Stampede.Http per-request policies via <see cref="HttpRequestMessage.Options"/>.
    /// </summary>
    /// <param name="path">Request path, relative to the configured base address.</param>
    /// <param name="configure">Optional hook to customise the request before it is sent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A summary of what the caller observed.</returns>
    public async Task<ProbeResult> GetAsync(
        string path,
        Action<HttpRequestMessage>? configure = null,
        CancellationToken cancellationToken = default)
    {
        long start = Stopwatch.GetTimestamp();

        using HttpRequestMessage request = new(HttpMethod.Get, path);
        configure?.Invoke(request);

        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        string body = response.StatusCode == HttpStatusCode.NotModified
            ? "(304, no body)"
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (body.Length > BodySnippetLength)
        {
            body = body[..BodySnippetLength] + "…";
        }

        return new ProbeResult(
            path,
            (int)response.StatusCode,
            (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds,
            response.Headers.Age?.TotalSeconds,
            body.ReplaceLineEndings(" "));
    }

    /// <summary>Reads the <c>ETag</c> the origin (or the cache) currently reports for a resource.</summary>
    /// <param name="path">Request path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The entity tag, or <see langword="null"/> when the resource carries none.</returns>
    public async Task<string?> GetETagAsync(string path, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await Http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        return response.Headers.ETag?.ToString();
    }

    /// <summary>Issues the unsafe request that triggers RFC 9111 §4.4 invalidation.</summary>
    /// <param name="path">Request path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The status code returned by the origin.</returns>
    public async Task<int> PostAsync(string path, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await Http.PostAsync(path, content: null, cancellationToken).ConfigureAwait(false);
        return (int)response.StatusCode;
    }

    /// <summary>
    /// Reads the origin's own request counters. <c>/stats</c> is <c>no-store</c>, so this always
    /// reflects reality rather than a cached snapshot.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Counter name to value, as reported by the origin.</returns>
    public async Task<IReadOnlyDictionary<string, int>> GetOriginCountersAsync(CancellationToken cancellationToken = default)
    {
        OriginStats? stats = await Http.GetFromJsonAsync<OriginStats>("/stats", cancellationToken).ConfigureAwait(false);
        return stats?.Counters ?? new Dictionary<string, int>(StringComparer.Ordinal);
    }

    /// <summary>Shape of the origin's <c>/stats</c> payload.</summary>
    /// <param name="FlakyIsDown">Whether <c>/flaky</c> is currently in its failure window.</param>
    /// <param name="CatalogVersion">Current catalog version.</param>
    /// <param name="Counters">Per-endpoint request counters.</param>
    public sealed record OriginStats(
        bool FlakyIsDown,
        int CatalogVersion,
        Dictionary<string, int> Counters);
}
