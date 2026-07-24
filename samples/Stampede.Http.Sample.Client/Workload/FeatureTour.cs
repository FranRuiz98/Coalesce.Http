using System.Net.Http.Headers;
using Stampede.Http.Caching;
using Stampede.Http.Coalescing;

namespace Stampede.Http.Sample.Client.Workload;

/// <summary>
/// A narrated walk through every Stampede.Http behaviour that a running system can
/// actually demonstrate, each step verified against the origin's own request counters
/// rather than against the client's belief about what happened.
/// </summary>
/// <remarks>
/// Enable this on exactly one instance (<c>Sample:Workload:FeatureTour</c>). The assertions
/// are deltas on shared origin counters, so any other caller hitting the same endpoints at
/// the same time — a second instance, or the k6 load profile — will skew them.
/// </remarks>
/// <param name="client">Typed client for the origin.</param>
/// <param name="logger">Logger used for the narration.</param>
public sealed class FeatureTour(OriginClient client, ILogger<FeatureTour> logger)
{
    private int _checks;
    private int _passed;

    /// <summary>Runs every scenario in order.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("PHASE 2 — feature tour: each step is verified against the origin's own counters");

        (string Name, Func<CancellationToken, Task> Run)[] scenarios =
        [
            ("Vary: Accept-Language", VaryByLanguageAsync),
            ("CoalesceKeyHeaders: X-Tenant-Id", TenantCoalescingAsync),
            ("Client conditional pass-through", ConditionalPassThroughAsync),
            ("CacheRequestPolicy.ForceRevalidate", ForceRevalidateAsync),
            ("CacheRequestPolicy.BypassCache", BypassCacheAsync),
            ("CacheRequestPolicy.NoStore", NoStoreAsync),
            ("CoalescingRequestPolicy.BypassCoalescing", BypassCoalescingAsync),
            ("Cache-Control: only-if-cached", OnlyIfCachedAsync),
            ("Cache-Control: immutable", ImmutableAsync),
            ("CacheOptions.MaxBodySizeBytes", BodySizeCeilingAsync),
            ("CacheOptions.NormalizeQueryParameters", QueryNormalizationAsync),
            ("Last-Modified revalidation (RevalidationGraceSeconds)", LastModifiedRevalidationAsync),
        ];

        foreach ((string name, Func<CancellationToken, Task> run) in scenarios)
        {
            try
            {
                await run(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One broken scenario must not take the process down — the remaining
                // steps still have something to say.
                _checks++;
                logger.LogError(ex, "  [!!] {Scenario}: threw {Exception}", name, ex.GetBaseException().Message);
            }
        }

        logger.LogInformation("PHASE 2 — feature tour complete: {Passed}/{Total} checks behaved as documented", _passed, _checks);
    }

    // -- Scenarios -----------------------------------------------------------

    // Vary: Accept-Language — one URL, one entry per language. Before v2.2.0 these
    // representations overwrote each other and content-negotiated endpoints never hit.
    private async Task VaryByLanguageAsync(CancellationToken ct)
    {
        string[] languages = ["en-GB", "es-ES", "fr-FR"];

        int cold = await OriginDeltaAsync("GET /greetings", async () =>
        {
            foreach (string language in languages)
            {
                _ = await GetAsync("/greetings", language, ct).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);

        int warm = await OriginDeltaAsync("GET /greetings", async () =>
        {
            foreach (string language in languages)
            {
                _ = await GetAsync("/greetings", language, ct).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);

        Check("Vary: Accept-Language",
            cold == 3 && warm == 0,
            $"3 languages cold → {cold} origin calls; the same 3 again → {warm}. Each variant has its own entry.");

        Task<ProbeResult> GetAsync(string path, string language, CancellationToken token) =>
            client.GetAsync(path, r => r.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue(language)), token);
    }

    // Vary + CoalesceKeyHeaders — the multi-tenant case. Ten concurrent callers across two
    // tenants must produce exactly two origin calls: not ten (no dedup) and not one
    // (tenant-a receiving tenant-b's data).
    private async Task TenantCoalescingAsync(CancellationToken ct)
    {
        const string TenantA = "acme";
        const string TenantB = "globex";

        int calls = await OriginDeltaAsync([$"GET /tenants/data [{TenantA}]", $"GET /tenants/data [{TenantB}]"], async () =>
        {
            IEnumerable<Task<ProbeResult>> burst =
            [
                .. Enumerable.Range(0, 5).Select(_ => Fetch(TenantA)),
                .. Enumerable.Range(0, 5).Select(_ => Fetch(TenantB)),
            ];

            _ = await Task.WhenAll(burst).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        Check("CoalesceKeyHeaders: X-Tenant-Id",
            calls == 2,
            $"10 concurrent callers across 2 tenants → {calls} origin calls. Per-tenant coalescing, no cross-tenant bleed.");

        Task<ProbeResult> Fetch(string tenant) =>
            client.GetAsync("/tenants/data", r => r.Headers.Add("X-Tenant-Id", tenant), ct);
    }

    // RFC 9111 §4.3.2 — a client-supplied If-None-Match that matches a fresh entry is
    // answered with 304 by the cache itself; the origin is never consulted.
    private async Task ConditionalPassThroughAsync(CancellationToken ct)
    {
        _ = await client.GetAsync("/ledger", cancellationToken: ct).ConfigureAwait(false);
        string? etag = await client.GetETagAsync("/ledger", ct).ConfigureAwait(false);

        ProbeResult? conditional = null;
        int calls = await OriginDeltaAsync("GET /ledger", async () =>
        {
            conditional = await client.GetAsync(
                "/ledger",
                r => r.Headers.TryAddWithoutValidation("If-None-Match", etag),
                ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        Check("Client conditional pass-through",
            calls == 0 && conditional?.StatusCode == 304,
            $"If-None-Match against a fresh entry → {conditional?.StatusCode}, {calls} origin calls.");
    }

    // ForceRevalidate — behaves like a request `no-cache`: the entry is fresh, but the cache
    // still asks the origin, which answers 304 and costs no body.
    private async Task ForceRevalidateAsync(CancellationToken ct)
    {
        _ = await client.GetAsync("/ledger", cancellationToken: ct).ConfigureAwait(false);

        ProbeResult? forced = null;
        int notModified = await OriginDeltaAsync("GET /ledger -> 304", async () =>
        {
            forced = await client.GetAsync(
                "/ledger",
                r => r.Options.Set(CacheRequestPolicy.ForceRevalidate, true),
                ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        Check("CacheRequestPolicy.ForceRevalidate",
            notModified == 1 && forced?.StatusCode == 200,
            $"Fresh entry revalidated anyway → origin answered {notModified} × 304, caller still got {forced?.StatusCode}.");
    }

    // BypassCache — no lookup, no storage. The origin always sees a full request.
    private async Task BypassCacheAsync(CancellationToken ct)
    {
        _ = await client.GetAsync("/ledger", cancellationToken: ct).ConfigureAwait(false);

        int calls = await OriginDeltaAsync("GET /ledger", async () =>
        {
            _ = await client.GetAsync(
                "/ledger",
                r => r.Options.Set(CacheRequestPolicy.BypassCache, true),
                ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        Check("CacheRequestPolicy.BypassCache",
            calls == 1,
            $"Fresh entry ignored entirely → {calls} origin call.");
    }

    // NoStore — the response is served to the caller but never written to the cache, so the
    // next plain request for the same URL is still a miss.
    private async Task NoStoreAsync(CancellationToken ct)
    {
        string path = UniqueSearchPath("nostore");

        int calls = await OriginDeltaAsync("GET /search", async () =>
        {
            _ = await client.GetAsync(path, r => r.Options.Set(CacheRequestPolicy.NoStore, true), ct).ConfigureAwait(false);
            _ = await client.GetAsync(path, cancellationToken: ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        Check("CacheRequestPolicy.NoStore",
            calls == 2,
            $"Fetch with NoStore then a plain fetch of the same URL → {calls} origin calls: nothing was stored.");
    }

    // BypassCoalescing — the escape hatch. Same burst, opted out of deduplication.
    private async Task BypassCoalescingAsync(CancellationToken ct)
    {
        string bypassed = UniqueSearchPath("bypass-coalescing");
        string coalesced = UniqueSearchPath("coalesced");

        int withBypass = await OriginDeltaAsync("GET /search", async () =>
        {
            _ = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.GetAsync(
                bypassed,
                r => r.Options.Set(CoalescingRequestPolicy.BypassCoalescing, true),
                ct))).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        int withCoalescing = await OriginDeltaAsync("GET /search", async () =>
        {
            _ = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.GetAsync(coalesced, cancellationToken: ct)))
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        Check("CoalescingRequestPolicy.BypassCoalescing",
            withBypass == 5 && withCoalescing == 1,
            $"5 concurrent callers → {withBypass} origin calls when bypassing, {withCoalescing} when coalescing.");
    }

    // RFC 9111 §5.2.1.7 — only-if-cached must never reach the network: with nothing cached
    // the cache answers 504 rather than fetching.
    private async Task OnlyIfCachedAsync(CancellationToken ct)
    {
        string path = UniqueSearchPath("only-if-cached");

        ProbeResult? result = null;
        int calls = await OriginDeltaAsync("GET /search", async () =>
        {
            result = await client.GetAsync(
                path,
                r => r.Headers.CacheControl = new CacheControlHeaderValue { OnlyIfCached = true },
                ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        Check("Cache-Control: only-if-cached",
            calls == 0 && result?.StatusCode == 504,
            $"Nothing cached for that URL → {result?.StatusCode} Gateway Timeout, {calls} origin calls.");
    }

    // RFC 8246 — a fresh immutable entry is not revalidated, even when the caller demands it.
    private async Task ImmutableAsync(CancellationToken ct)
    {
        // A fresh id per run: these entries carry max-age=31536000 and live in Redis, so an
        // asset cached by an earlier run of this tour would still be warm a restart later.
        // (That is the feature working, but it makes "cold fetch" mean nothing here.)
        string path = $"/assets/tour-{Guid.NewGuid():N}";

        int cold = await OriginDeltaAsync("GET /assets/{id}", async () =>
        {
            _ = await client.GetAsync(path, cancellationToken: ct).ConfigureAwait(false);
            _ = await client.GetAsync(path, cancellationToken: ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        int forced = await OriginDeltaAsync("GET /assets/{id}", async () =>
        {
            _ = await client.GetAsync(path, r => r.Options.Set(CacheRequestPolicy.ForceRevalidate, true), ct)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        Check("Cache-Control: immutable",
            cold == 1 && forced == 0,
            $"Two cold fetches → {cold} origin call; a forced revalidation on top → {forced}. Immutable entries skip revalidation.");
    }

    // MaxBodySizeBytes — a response can be perfectly cacheable and still be declined for
    // being too large. Every request for it goes to the origin.
    private async Task BodySizeCeilingAsync(CancellationToken ct)
    {
        int calls = await OriginDeltaAsync("GET /bulk", async () =>
        {
            _ = await client.GetAsync("/bulk", cancellationToken: ct).ConfigureAwait(false);
            _ = await client.GetAsync("/bulk", cancellationToken: ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        Check("CacheOptions.MaxBodySizeBytes",
            calls == 2,
            $"A ~1.8 MB body with max-age=300, fetched twice → {calls} origin calls: too large to store (1 MB ceiling).");
    }

    // NormalizeQueryParameters — the same logical query with its parameters reordered must
    // not split into two cache entries.
    private async Task QueryNormalizationAsync(CancellationToken ct)
    {
        string tag = Guid.NewGuid().ToString("N")[..8];

        int calls = await OriginDeltaAsync("GET /search", async () =>
        {
            _ = await client.GetAsync($"/search?alpha=1&beta=2&tour={tag}", cancellationToken: ct).ConfigureAwait(false);
            _ = await client.GetAsync($"/search?tour={tag}&beta=2&alpha=1", cancellationToken: ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        Check("CacheOptions.NormalizeQueryParameters",
            calls == 1,
            $"The same query with reordered parameters → {calls} origin call.");
    }

    // Last-Modified + RevalidationGraceSeconds — the entry outlives its freshness so that
    // expiry can be settled with If-Modified-Since instead of a full refetch.
    private async Task LastModifiedRevalidationAsync(CancellationToken ct)
    {
        const string Path = "/docs/tour";

        _ = await client.GetAsync(Path, cancellationToken: ct).ConfigureAwait(false);

        // max-age=5 on this endpoint; wait it out so the next request finds a stale entry.
        await Task.Delay(TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);

        ProbeResult? revalidated = null;
        int notModified = await OriginDeltaAsync("GET /docs/{id} -> 304", async () =>
        {
            revalidated = await client.GetAsync(Path, cancellationToken: ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        Check("Last-Modified revalidation (RevalidationGraceSeconds)",
            notModified == 1 && revalidated?.StatusCode == 200,
            $"Expired entry kept for revalidation → origin answered {notModified} × 304 to If-Modified-Since, caller got {revalidated?.StatusCode} with no body transferred.");
    }

    // -- Plumbing ------------------------------------------------------------

    private static string UniqueSearchPath(string scenario) =>
        $"/search?tour={scenario}-{Guid.NewGuid():N}";

    private Task<int> OriginDeltaAsync(string counter, Func<Task> action, CancellationToken ct) =>
        OriginDeltaAsync([counter], action, ct);

    /// <summary>
    /// Runs <paramref name="action"/> and reports how much the given origin counters moved.
    /// The origin is the source of truth here: the client cannot lie about traffic it never sent.
    /// </summary>
    private async Task<int> OriginDeltaAsync(string[] counters, Func<Task> action, CancellationToken ct)
    {
        IReadOnlyDictionary<string, int> before = await client.GetOriginCountersAsync(ct).ConfigureAwait(false);
        await action().ConfigureAwait(false);
        IReadOnlyDictionary<string, int> after = await client.GetOriginCountersAsync(ct).ConfigureAwait(false);

        return counters.Sum(c => Value(after, c) - Value(before, c));

        static int Value(IReadOnlyDictionary<string, int> source, string key) =>
            source.TryGetValue(key, out int value) ? value : 0;
    }

    private void Check(string scenario, bool asExpected, string detail)
    {
        _checks++;
        if (asExpected)
        {
            _passed++;
            logger.LogInformation("  [ok] {Scenario}: {Detail}", scenario, detail);
        }
        else
        {
            logger.LogWarning("  [??] {Scenario}: {Detail} (not the documented outcome — is another caller hitting these endpoints?)", scenario, detail);
        }
    }
}
