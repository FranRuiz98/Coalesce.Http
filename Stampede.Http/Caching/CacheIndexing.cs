namespace Stampede.Http.Caching;

/// <summary>
/// Shared helpers for the two key indexes the cache maintains inside its own <see cref="ICacheStore"/>:
/// the variant keys tracked on a Vary marker (<see cref="CacheEntry.TrackedKeys"/>), and the per-tag
/// index entries that map a cache tag to the primary keys carrying it. Kept in one place so the write
/// side (<see cref="CachingMiddleware"/>) and the eviction side (<see cref="StampedeHttpCache"/>) stay
/// in sync on key format, capacity, and sweep semantics.
/// </summary>
/// <remarks>
/// Both indexes are best-effort by design: they are read-merge-write over a store with no compare-and-swap,
/// so two concurrent writers can each miss the other's addition. A key that falls out of an index is never
/// served incorrectly — it just expires on its own retention schedule instead of being actively removed
/// when the index is swept.
/// </remarks>
internal static class CacheIndexing
{
    /// <summary>
    /// Upper bound on <see cref="CacheEntry.TrackedKeys"/>. Beyond it, new keys are simply not tracked
    /// (they still expire on their own), keeping a high-cardinality Vary header or a very broad tag from
    /// growing an index entry without limit — every store of a tracked key rewrites the whole list.
    /// </summary>
    internal const int MaxTrackedKeys = 1024;

    /// <summary>
    /// U+001F (unit separator) frames tag index keys. Request-derived cache keys always start with an
    /// HTTP method name, and this control character cannot appear in a URI or header value, so a key
    /// starting with it can never collide with one built by <see cref="ICacheKeyBuilder"/>.
    /// </summary>
    private const char TagKeySeparator = (char)0x1f;

    /// <summary>Builds the store key of the index entry for <paramref name="tag"/> (compared ordinally, case-sensitive).</summary>
    public static string BuildTagKey(string tag) => $"{TagKeySeparator}tag{TagKeySeparator}{tag}";

    /// <summary>
    /// Returns <paramref name="existing"/> with <paramref name="key"/> appended, or <paramref name="existing"/>
    /// itself when the key is already present or the list is at <see cref="MaxTrackedKeys"/>.
    /// </summary>
    public static string[] MergeTrackedKey(string[] existing, string key)
    {
        if (existing.Length >= MaxTrackedKeys || Array.IndexOf(existing, key) >= 0)
        {
            return existing;
        }

        string[] merged = new string[existing.Length + 1];
        existing.CopyTo(merged, 0);
        merged[^1] = key;
        return merged;
    }

    /// <summary>
    /// Removes the entry at <paramref name="primaryKey"/> and, when it is a Vary marker, every
    /// secondary-key variant it tracks — so an explicit eviction sweeps the whole representation set
    /// instead of leaving variants unreachable-but-alive until their own retention elapses.
    /// </summary>
    /// <remarks>
    /// Costs one read to discover whether the key holds a marker. RFC 9111 §4.4 invalidation deliberately
    /// does <b>not</b> take this path: it runs on every successful unsafe request, where the extra
    /// round-trip against a distributed store isn't worth reclaiming storage that a removed marker has
    /// already made unreachable. Explicit eviction is rare and user-initiated, so it takes the full sweep.
    /// </remarks>
    public static async ValueTask EvictWithVariantsAsync(ICacheStore cache, string primaryKey, CancellationToken ct)
    {
        CacheEntry? entry = await cache.GetAsync(primaryKey, ct).ConfigureAwait(false);

        if (entry is { IsVaryMarker: true })
        {
            foreach (string variantKey in entry.TrackedKeys)
            {
                await cache.RemoveAsync(variantKey, ct).ConfigureAwait(false);
            }
        }

        await cache.RemoveAsync(primaryKey, ct).ConfigureAwait(false);
    }
}
