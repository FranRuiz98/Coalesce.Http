namespace Stampede.Http.Caching;

/// <summary>
/// Controls whether requests carrying an <c>Authorization</c> header are eligible for caching.
/// </summary>
/// <remarks>
/// <para>
/// RFC 9111 §3.5 forbids a <em>shared</em> cache from storing a response to a request with an
/// <c>Authorization</c> header unless the response explicitly permits it. Stampede.Http is a <em>private</em>
/// cache — scoped to one process/<c>HttpClient</c>, never shared between principals through a common
/// proxy — so serving a caller's own prior response back to that same caller is not the cross-user leak
/// §3.5 guards against. What still matters is that responses for <em>different</em> credentials are never
/// mixed: whenever this is not <see cref="Never"/>, both the cache key
/// (<see cref="DefaultCacheKeyBuilder"/>) and the request-coalescing key fold in a hash of the
/// <c>Authorization</c> value, so two callers presenting different (or absent) credentials for the same URL
/// always get independent cache entries and are never coalesced into one shared origin call.
/// </para>
/// <para>
/// <b>Known limitation:</b> <c>HEAD</c> requests and §4.4 invalidation after a successful unsafe method
/// both resolve the unauthenticated (plain) cache key for a URI — they do not know which credential's
/// variant to target. This means neither reaches a credential-scoped entry: an authenticated <c>HEAD</c>
/// won't hit its own <c>GET</c> entry's cache, and a <c>POST</c> to the same URL only invalidates the
/// unauthenticated entry (if any), not any per-credential ones. Per-credential entries still expire on
/// their own via normal freshness/validator rules; use <see cref="IStampedeHttpCache"/> for explicit
/// eviction when this matters.
/// </para>
/// </remarks>
public enum AuthorizationCachingMode
{
    /// <summary>
    /// Requests carrying an <c>Authorization</c> header are never cached (default) — matches pre-2.4
    /// behavior.
    /// </summary>
    Never,

    /// <summary>
    /// Requests carrying an <c>Authorization</c> header are cached only when the response explicitly
    /// permits it per RFC 9111 §3.5: <c>Cache-Control: public</c>, <c>must-revalidate</c>, or an explicit
    /// <c>s-maxage</c>. Recommended when enabling authorized caching, since it defers the decision to each
    /// origin response rather than caching every authenticated response unconditionally.
    /// </summary>
    WhenPermittedByResponse,

    /// <summary>
    /// Requests carrying an <c>Authorization</c> header are cached under the same rules as any other
    /// request (subject to <see cref="CacheOptions"/> and normal response cacheability), regardless of
    /// whether the response carries a permitting directive. Use only when you control the origin and know
    /// its authenticated responses are safe to reuse for the same credential.
    /// </summary>
    Always
}
