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
/// <b>Scope:</b> eviction targets a single, exact URI — the same key an ordinary GET to that URI would
/// resolve to. There is no prefix or pattern eviction: <see cref="ICacheStore"/> (in particular
/// <see cref="DistributedCacheStore"/>, backed by <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/>)
/// has no portable way to enumerate or pattern-match keys.
/// </para>
/// <para>
/// <b>Vary interaction:</b> when the evicted entry carried a <c>Vary</c> header (RFC 9111 §4.1), eviction
/// removes only the primary key's marker. The secondary-key variants it pointed to (one per distinct
/// combination of Vary field values) become unreachable but are not actively removed — they expire on
/// their own schedule. This is the same trade-off Vary storage already makes internally; a future release
/// may enumerate and remove them explicitly if this proves to matter in practice.
/// </para>
/// <para>
/// <b>Authorization interaction:</b> when <see cref="CacheOptions.AuthorizationCaching"/> is enabled,
/// authenticated responses are stored under a credential-scoped key (see
/// <see cref="AuthorizationCachingMode"/>) that <see cref="EvictAsync"/> — which always resolves the plain,
/// unauthenticated key for a URI — cannot target. Per-credential entries are unaffected by eviction and
/// rely on their own freshness/validator lifecycle instead.
/// </para>
/// </remarks>
public interface IStampedeHttpCache
{
    /// <summary>
    /// Evicts the cached GET representation for <paramref name="uri"/>, if one exists.
    /// </summary>
    /// <remarks>
    /// Removal is unconditional and idempotent — calling this for a URI with nothing cached is a no-op,
    /// not an error, and there is no read-before-remove to report whether an entry actually existed
    /// (avoiding a redundant round trip against a distributed store, the same reasoning already applied to
    /// §4.4 invalidation).
    /// </remarks>
    /// <param name="uri">The URI whose cached GET response should be evicted.</param>
    /// <param name="ct">A cancellation token to observe while removing the entry.</param>
    ValueTask EvictAsync(Uri uri, CancellationToken ct = default);
}
