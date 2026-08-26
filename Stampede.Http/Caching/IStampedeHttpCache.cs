using System.Net.Http.Headers;

namespace Stampede.Http.Caching;

/// <summary>
/// Programmatic eviction for a named client's HTTP cache — for when a resource changed through a channel
/// this <c>HttpClient</c> didn't observe (a different service mutated it, a webhook fired, an out-of-band
/// admin action happened) and the cached GET response needs to be dropped before its own TTL/validator
/// would naturally refresh it.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a keyed singleton per client name by <c>AddStampedeHttp()</c>/<c>AddCachingOnly()</c>,
/// mirroring <see cref="ICacheStore"/> and <see cref="ICacheKeyBuilder"/>:
/// </para>
/// <code>
/// IStampedeHttpCache cache = serviceProvider.GetRequiredKeyedService&lt;IStampedeHttpCache&gt;("catalog");
/// await cache.EvictAsync(new Uri("https://api.example.com/products/42"));
/// </code>
/// <para>
/// <b>Scope:</b> URI eviction targets a single, exact URI — the same key an ordinary GET to that URI would
/// resolve to. There is no prefix or pattern eviction: <see cref="ICacheStore"/> (in particular
/// <see cref="DistributedCacheStore"/>, backed by <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/>)
/// has no portable way to enumerate or pattern-match keys. To invalidate a group of URIs in one call, tag
/// them (via <see cref="CacheOptions.TagHeaderNames"/> or <see cref="CacheRequestPolicy.Tags"/>) and use
/// <see cref="EvictByTagAsync"/>.
/// </para>
/// <para>
/// <b>Vary interaction:</b> when the evicted entry carries a <c>Vary</c> header (RFC 9111 §4.1), eviction
/// follows the primary key's marker and also removes the secondary-key variants it tracks, at the cost of
/// one extra store read per evicted key. Variant tracking is best-effort (see
/// <see cref="CacheEntry.TrackedKeys"/>): a variant that fell out of the tracked list becomes unreachable
/// on marker removal and expires on its own schedule rather than being actively swept.
/// </para>
/// <para>
/// <b>Authorization interaction:</b> when <see cref="CacheOptions.AuthorizationCaching"/> is enabled,
/// authenticated responses are stored under a credential-scoped key (see
/// <see cref="AuthorizationCachingMode"/>). <see cref="EvictAsync(Uri, CancellationToken)"/> always
/// resolves the plain, unauthenticated key, so per-credential entries need
/// <see cref="EvictAsync(Uri, AuthenticationHeaderValue, CancellationToken)"/> with the same
/// <c>Authorization</c> value the cached request carried.
/// </para>
/// </remarks>
public interface IStampedeHttpCache
{
    /// <summary>
    /// Evicts the cached GET representation for <paramref name="uri"/>, if one exists — including, when it
    /// varies (RFC 9111 §4.1), the secondary-key variants tracked by its Vary marker.
    /// </summary>
    /// <remarks>
    /// Removal is unconditional and idempotent — calling this for a URI with nothing cached is a no-op,
    /// not an error, and nothing is reported about whether an entry actually existed.
    /// </remarks>
    /// <param name="uri">The URI whose cached GET response should be evicted.</param>
    /// <param name="ct">A cancellation token to observe while removing the entry.</param>
    ValueTask EvictAsync(Uri uri, CancellationToken ct = default);

    /// <summary>
    /// Evicts the cached GET representation stored for <paramref name="uri"/> under the credential-scoped
    /// key derived from <paramref name="authorization"/> — the entry an authenticated GET carrying that
    /// same <c>Authorization</c> value would hit when <see cref="CacheOptions.AuthorizationCaching"/> is
    /// enabled. Like the URI overload, this also sweeps tracked Vary variants and is idempotent.
    /// </summary>
    /// <remarks>
    /// The default implementation throws <see cref="NotSupportedException"/> so that custom
    /// <see cref="IStampedeHttpCache"/> implementations written before 2.6 keep compiling; the built-in
    /// implementation registered by <c>AddStampedeHttp()</c>/<c>AddCachingOnly()</c> always supports it.
    /// </remarks>
    /// <param name="uri">The URI whose cached authenticated GET response should be evicted.</param>
    /// <param name="authorization">The <c>Authorization</c> header value the cached request carried.</param>
    /// <param name="ct">A cancellation token to observe while removing the entry.</param>
    ValueTask EvictAsync(Uri uri, AuthenticationHeaderValue authorization, CancellationToken ct = default)
        => throw new NotSupportedException($"This {nameof(IStampedeHttpCache)} implementation does not support credential-scoped eviction.");

    /// <summary>
    /// Evicts every cached GET representation tagged with <paramref name="tag"/> — collected from the
    /// response headers named in <see cref="CacheOptions.TagHeaderNames"/> and from
    /// <see cref="CacheRequestPolicy.Tags"/> — then drops the tag's index entry itself. Tags are compared
    /// ordinally (case-sensitive). Evicting a tag nothing carries is a no-op, not an error.
    /// </summary>
    /// <remarks>
    /// The tag index is best-effort (see <see cref="CacheEntry.TrackedKeys"/>): an entry that fell out of
    /// it is not swept here and expires on its own freshness/validator schedule instead. The default
    /// implementation throws <see cref="NotSupportedException"/> so that custom implementations written
    /// before 2.6 keep compiling; the built-in implementation always supports it.
    /// </remarks>
    /// <param name="tag">The tag whose entries should be evicted.</param>
    /// <param name="ct">A cancellation token to observe while removing the entries.</param>
    ValueTask EvictByTagAsync(string tag, CancellationToken ct = default)
        => throw new NotSupportedException($"This {nameof(IStampedeHttpCache)} implementation does not support tag-based eviction.");
}
