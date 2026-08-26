# Stampede.Http

> RFC 9111 HTTP caching and request coalescing for the .NET `HttpClient` pipeline.

[![NuGet](https://img.shields.io/nuget/v/Stampede.Http?label=NuGet&color=blue)](https://www.nuget.org/packages/Stampede.Http)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com)
[![CI](https://github.com/FranRuiz98/Stampede.Http/actions/workflows/ci.yml/badge.svg)](https://github.com/FranRuiz98/Stampede.Http/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-MIT-green)](#license)

> [!NOTE]
> Stampede.Http was formerly published as **Coalesce.Http** (versions ≤ 1.2.0). Same library, new name — see the [v2.0.0 changelog](#v200) for the migration guide.

**Stampede.Http** is a thin, composable `DelegatingHandler` layer that adds caching and request deduplication to any named `HttpClient`. It does not replace `HttpClient` or Polly — it slots right into the existing pipeline.

| Problem | What Stampede.Http does |
|---|---|
| Thundering herd of duplicate concurrent requests | **Coalesces** them into a single backend call |
| Repeated fetches for unchanged resources | **RFC 9111 caching** with ETag/Last-Modified revalidation |
| Cache stampede on expiry | Coalescing prevents multiple simultaneous origin calls |
| Stale data during origin failures | **stale-if-error** (RFC 5861 §4) serves cached responses while the origin recovers |
| High latency visible at cache expiry | **stale-while-revalidate** (RFC 5861 §3) returns stale instantly and refreshes in the background |
| Stale GET entries after mutations | **Unsafe method invalidation** (RFC 9111 §4.4) evicts affected entries automatically |

---

## Installation

```bash
dotnet add package Stampede.Http
```

Requires **.NET 8.0** or later. No third-party dependencies — only `Microsoft.Extensions.*`.

---

## Quick start

```csharp
builder.Services
    .AddHttpClient("catalog")
    .AddStampedeHttp(
        configureCaching:    o => o.DefaultTtl = TimeSpan.FromSeconds(60),
        configureCoalescing: o => o.CoalescingTimeout = TimeSpan.FromSeconds(5)
    );
```

The resulting pipeline:

```
CachingMiddleware       ← cache hits served here, no network call
  └─ CoalescingHandler  ← concurrent misses share one backend call
       └─ [Polly, if added]
            └─ HttpClientHandler
```

### With Polly resilience

Always chain `AddResilienceHandler` **after** `AddStampedeHttp` so Polly sits between the coalescer and the transport:

```csharp
services.AddHttpClient("catalog")
    .AddStampedeHttp()
    .AddResilienceHandler("resilience", b =>
        b.AddRetry(new HttpRetryStrategyOptions { MaxRetryAttempts = 3 }));
```

---

## How it compares

Stampede.Http occupies a specific niche: **origin-controlled caching semantics inside the `HttpClient` pipeline**. The origin server decides what is cacheable and for how long (via `Cache-Control`, `ETag`, `Vary`…); your application code never touches a cache key.

| | Stampede.Http | [CacheCow.Client](https://github.com/aliostad/CacheCow) | [FusionCache](https://github.com/ZiggyCreatures/FusionCache) / [HybridCache](https://learn.microsoft.com/aspnet/core/performance/caching/hybrid) | [Polly v8](https://github.com/App-vNext/Polly) |
|---|---|---|---|---|
| Where it sits | `HttpClient` pipeline | `HttpClient` pipeline | Application code (cache-aside) | `HttpClient` pipeline |
| Header-driven HTTP caching | ✅ RFC 9111 | ✅ RFC 7234 | ❌ manual keys + TTLs | ❌ removed in v8 |
| Request coalescing / stampede protection | ✅ at the HTTP layer | ❌ | ✅ at the app layer | ❌ |
| Stale extensions | ✅ `stale-if-error` + `stale-while-revalidate` (RFC 5861) | ❌ | ✅ own semantics (fail-safe, eager refresh) | ❌ |
| Distributed second level | ✅ any `IDistributedCache` | ✅ own stores | ✅ | — |
| Retries, circuit breakers, timeouts | ❌ chain Polly | ❌ | ❌ | ✅ |

- **FusionCache / HybridCache** are excellent app-level caches — reach for them when you cache *computed results* and want full control over keys and TTLs. Reach for Stampede.Http when the data source is HTTP and you want the origin's caching headers respected automatically.
- **CacheCow** pioneered this space and takes the same pipeline approach; its last stable release (2.13.1) dates from January 2024, targets the older RFC 7234, and does not coalesce concurrent requests.
- **Polly** is a complement, not an alternative: it dropped its cache policy in v8 and handles the resilience half (retries, hedging, circuit breakers). Chain it after Stampede.Http as shown [above](#with-polly-resilience).

---

## Configuration

### CacheOptions

| Property | Default | Description |
|---|---|---|
| `DefaultTtl` | `30s` | Freshness lifetime when no `Cache-Control`/`Expires` is present |
| `MaxBodySizeBytes` | `1 MB` | Responses larger than this are not stored |
| `MaxCacheSize` | `null` | Total byte ceiling; when reached, LRU entries are evicted. `null` = no limit |
| `DefaultStaleIfErrorSeconds` | `0` | Stale-if-error window when the response carries no directive (`0` = disabled) |
| `DefaultStaleWhileRevalidateSeconds` | `0` | Stale-while-revalidate window when the response carries no directive (`0` = disabled) |
| `RevalidationGraceSeconds` | `300` | How long entries with an `ETag`/`Last-Modified` are kept in the store past freshness + stale windows, so expiry triggers a conditional `If-None-Match`/`If-Modified-Since` revalidation instead of a full refetch (`0` = evict at expiry) |
| `NormalizeQueryParameters` | `false` | Sort query params before building the cache key, so `/items?b=2&a=1` and `/items?a=1&b=2` hit the same entry |
| `EnableHeuristicFreshness` | `false` | RFC 9111 §4.2.2 heuristic freshness — estimate a TTL from `Last-Modified` (10% of its age by default) for responses with no `s-maxage`/`max-age`/`Expires` |
| `HeuristicFreshnessFraction` | `0.1` | Fraction of the `Last-Modified` age used as the heuristic TTL, when enabled |
| `MaxHeuristicFreshness` | `24h` | Upper bound on the heuristic TTL, when enabled |
| `AuthorizationCaching` | `Never` | Whether requests carrying an `Authorization` header are cacheable — `Never`, `WhenPermittedByResponse` (RFC 9111 §3.5: requires `public`/`must-revalidate`/`s-maxage`), or `Always`. See [Caching authorized requests](#caching-authorized-requests) |
| `TagHeaderNames` | `[]` | Response header names scanned for cache tags (e.g. `Cache-Tag`, `Surrogate-Key`, `xkey`), enabling group invalidation via `EvictByTagAsync`. See [Tag-based invalidation](#tag-based-invalidation) |
| `EnableEarlyRevalidation` | `false` | XFetch: probabilistically refresh a fresh entry in the background ahead of its expiry. See [Early revalidation (XFetch)](#early-revalidation-xfetch) |
| `EarlyRevalidationBeta` | `1.0` | Tuning parameter (β) scaling how far ahead of expiry early revalidation starts, in units of the entry's measured origin fetch duration |

### CoalescerOptions

| Property | Default | Description |
|---|---|---|
| `Enabled` | `true` | Set to `false` to disable coalescing (useful for debugging) |
| `CoalescingTimeout` | `null` | How long a waiter will wait before falling back to an independent request. `null` = no timeout |
| `MaxResponseBodyBytes` | `1 MB` | Maximum body the coalescer will buffer; exceeding this throws for all waiters |
| `CoalesceKeyHeaders` | `[]` | Extra request headers (e.g. `X-Tenant-Id`) included in the coalescing key |
| `ShouldCoalesce` | `null` | Predicate extending coalescing to methods other than `GET`/`HEAD`. See [Coalescing non-GET requests](#coalescing-non-get-requests) |
| `MaxCoalescedRequestBodyBytes` | `64 KB` | Maximum request body buffered to hash for `ShouldCoalesce`-matched methods; larger bodies execute independently |

Both options classes are registered as **named options** (`IOptionsMonitor<T>`) keyed by the client name, so runtime-tuneable settings take effect immediately on configuration reload without restarting the app.

---

## Pipeline helpers

| Method | What it registers |
|---|---|
| `AddStampedeHttp()` | `CachingMiddleware` + `CoalescingHandler` + metrics + `IStampedeHttpCache` |
| `AddCachingOnly()` | `CachingMiddleware` + metrics + `IStampedeHttpCache` |
| `AddCoalescingOnly()` | `CoalescingHandler` + metrics |
| `UseDistributedCacheStore()` | Replaces `MemoryCacheStore` with `DistributedCacheStore` (chain after the above) |

---

## Programmatic eviction

When a resource changes through a channel this `HttpClient` didn't observe — another service mutated it, a webhook fired, an admin action happened out of band — its cached GET response won't refresh until its own TTL/validator would naturally do so. `IStampedeHttpCache` evicts it on demand, resolved per client name the same way as `ICacheStore`/`ICacheKeyBuilder`:

```csharp
IStampedeHttpCache cache = serviceProvider.GetRequiredKeyedService<IStampedeHttpCache>("catalog");
await cache.EvictAsync(new Uri("https://api.example.com/products/42"));
```

Or via constructor injection when there's a single registered client — the non-keyed `IStampedeHttpCache` falls back to the first-registered client, same as `ICacheStore`.

URI eviction targets one exact URI (the same key a GET to it would resolve to) — there's no prefix or pattern eviction, since `IDistributedCache` has no portable way to enumerate keys; to invalidate a group of URIs in one call, use [tags](#tag-based-invalidation). Eviction is unconditional and idempotent: evicting a URI with nothing cached is a no-op. If the evicted entry carries a `Vary` header, its secondary-key variants are swept too — each variant's key is tracked on the primary marker as it's stored, so eviction follows the marker and removes all of them (best-effort: a variant that fell out of the tracked list just expires on its own instead).

When [`AuthorizationCaching`](#caching-authorized-requests) is enabled, authenticated responses live under credential-scoped keys the URI overload can't reach. Pass the same `Authorization` value the cached request carried:

```csharp
await cache.EvictAsync(new Uri("https://api.example.com/products/42"),
    new AuthenticationHeaderValue("Bearer", token));
```

---

## Tag-based invalidation

Evicting one URI at a time doesn't scale to "product 42 changed — drop its detail view, every list it appears in, and the search results mentioning it". Tags solve this the way CDNs do (Cloudflare's `Cache-Tag`, Fastly's `Surrogate-Key`, Varnish's `xkey`): the origin labels each response, and the client invalidates by label.

Opt in by naming the response headers to scan:

```csharp
services.AddHttpClient("catalog")
    .AddStampedeHttp(cache => cache.TagHeaderNames = ["Cache-Tag"]);
```

Any response stored with `Cache-Tag: products, product-42` (comma- or space-separated — both CDN conventions work) is indexed under each tag. One call then invalidates every entry carrying the tag, including their `Vary` variants:

```csharp
IStampedeHttpCache cache = serviceProvider.GetRequiredKeyedService<IStampedeHttpCache>("catalog");
await cache.EvictByTagAsync("product-42");
```

When the origin doesn't emit tag headers, attach tags from the caller instead — `CacheRequestPolicy.Tags` works with `TagHeaderNames` unset:

```csharp
var request = new HttpRequestMessage(HttpMethod.Get, "/api/products/42");
request.Options.Set(CacheRequestPolicy.Tags, ["product-42"]);
```

Tags are compared ordinally (case-sensitive). The index lives in the same `ICacheStore` as the entries — memory or distributed — with a retention that covers the longest-retained entry it tracks. Like `Vary` variant tracking, it's best-effort by design: the index is read-merge-write with no compare-and-swap, and capped at 1024 keys per tag, so under extreme concurrency or cardinality an entry can fall out of the index — it's then simply not swept by `EvictByTagAsync` and expires via its own freshness/validator rules; it is never served incorrectly.

---

## Caching authorized requests

By default, a request carrying an `Authorization` header is never cached — matching every version before 2.4. `CacheOptions.AuthorizationCaching` opts in:

```csharp
services.AddHttpClient("catalog")
    .AddStampedeHttp(cache => cache.AuthorizationCaching = AuthorizationCachingMode.WhenPermittedByResponse);
```

| Mode | Behavior |
|---|---|
| `Never` (default) | Authorized requests are never cached |
| `WhenPermittedByResponse` | Cached only when the response carries `Cache-Control: public`, `must-revalidate`, or `s-maxage` (RFC 9111 §3.5) — recommended, since it's the origin that decides per response |
| `Always` | Cached under the same rules as any other request, regardless of what the response's `Cache-Control` says — only if you control the origin and know its authenticated responses are safe to reuse |

Stampede.Http is a *private* cache (scoped to one process/`HttpClient`, never shared through a common proxy across principals), so returning a caller's own prior response to that same caller isn't the cross-user leak §3.5 guards against. What still has to hold is that *different* credentials are never mixed: whenever this isn't `Never`, both the cache key and the coalescing key fold in a hash of the `Authorization` value (never the raw value — it never appears in a key, a log line, or a distributed store's key listing), so two callers presenting different — or absent — credentials for the same URL always get independent entries and are never coalesced into one shared origin call. This protection in the coalescer is unconditional, independent of `AuthorizationCaching`: it also applies with `AddCoalescingOnly()`, with no caching in the pipeline at all.

Two known limitations, both a direct consequence of `HEAD` and §4.4 invalidation resolving the plain, unauthenticated key for a URI (they have no credential of their own to scope by): an authenticated `HEAD` request won't hit its own `GET` entry's cache, and a successful `POST`/`PUT`/`DELETE` only invalidates the unauthenticated entry (if any) for that URL, not any per-credential ones — those expire on their own via normal freshness/validator rules, or can be evicted explicitly with the credential-scoped `EvictAsync(uri, authorization)` overload (see [Programmatic eviction](#programmatic-eviction)).

---

## Coalescing non-GET requests

Coalescing covers `GET`/`HEAD` unconditionally. `CoalescerOptions.ShouldCoalesce` extends it to other methods — typically `POST` exposed as a read (a GraphQL `query`, a search endpoint with a large filter body):

```csharp
services.AddHttpClient("graphql")
    .AddStampedeHttp(configureCoalescing: o =>
    {
        o.ShouldCoalesce = req => req.Method == HttpMethod.Post
            && req.Headers.TryGetValues("X-Operation-Type", out var v)
            && v.Contains("query");
    });
```

**This asserts the request is idempotent.** Coalescing means concurrent identical calls share a single execution — fine for a read, actively wrong for a mutation: two concurrent orders would collapse into one order actually placed. Only opt a method in when every request it matches is a read; a GraphQL gateway, for instance, must match `query` operations but never `mutation` ones, typically via a header the caller adds when building the request (as above) — the predicate runs before the body is read, so it can inspect the method, URI, and headers, but not `Content`.

For a matched method, two requests to the same URL coalesce only if their bodies are identical too — the body is hashed into the coalescing key. Hashing requires buffering it (`MaxCoalescedRequestBodyBytes`, default 64 KB); a larger body isn't an error, it just executes independently rather than coalescing. Buffering also makes the body replayable, so retry/hedging added via `AddResilienceHandler` works the same way it already does for the coalesced response.

---

## Early revalidation (XFetch)

`stale-while-revalidate` reacts once an entry has *already* gone stale. `CacheOptions.EnableEarlyRevalidation` (XFetch — Vattani, Padmanabhan & Gionis, "Optimal Probabilistic Cache Stampede Prevention", 2015) targets what happens *before* that: it spreads out *when* different callers or process instances refresh a not-yet-expired entry, so they don't all decide to refetch it in the same instant it expires.

```csharp
services.AddHttpClient("catalog")
    .AddStampedeHttp(cache => cache.EnableEarlyRevalidation = true);
```

On a fresh hit, the probability of triggering a background refresh rises the closer the entry is to expiring, scaled by how expensive it was to fetch: an entry that took 2s to compute starts refreshing early well before one that answered in 20ms, via `EarlyRevalidationBeta` — the expected lead time before expiry is `origin fetch duration × β`. Higher values refresh earlier and more often, at the cost of more background origin calls; the default `1.0` matches the value used in the paper's evaluation. The refresh runs through the same background coordinator as `stale-while-revalidate`, so at most one runs per key at a time regardless of how many concurrent hits trigger it — and the hit that triggered it is otherwise unaffected, still served immediately from the current entry.

---

## Distributed cache store

For multi-instance deployments, replace the default in-memory store with any `IDistributedCache` backend:

```csharp
// Redis (any IDistributedCache provider works — SQL Server, NCache, etc.)
builder.Services.AddStackExchangeRedisCache(o =>
    o.Configuration = builder.Configuration["Redis:ConnectionString"]);

builder.Services
    .AddHttpClient("catalog")
    .AddStampedeHttp(configureCaching: o => o.DefaultTtl = TimeSpan.FromMinutes(5))
    .UseDistributedCacheStore();
```

Entries are serialised to JSON. The backing store TTL is extended by `Max(StaleIfErrorSeconds, StaleWhileRevalidateSeconds)` beyond `ExpiresAt` — plus `RevalidationGraceSeconds` when the entry carries a validator — so stale-serving windows and conditional revalidation survive process restarts.

> Coalescing still applies. Concurrent cache misses are deduplicated before the distributed store is consulted.

---

## Per-request policies

Override behaviour on individual requests via `HttpRequestMessage.Options`:

```csharp
var request = new HttpRequestMessage(HttpMethod.Get, "/api/products");
request.Options.Set(CacheRequestPolicy.BypassCache, true);
```

**Cache policies** (`CacheRequestPolicy`):

| Key | Effect |
|---|---|
| `BypassCache` | Skips all cache interaction — lookup, storage, and unsafe-method invalidation |
| `ForceRevalidate` | Forces conditional revalidation even if the entry is fresh |
| `NoStore` | Prevents the response from being stored; reads and revalidation still work |
| `Tags` | Cache tags to index the stored response under, for group invalidation via `EvictByTagAsync` — honored even with `TagHeaderNames` unset. See [Tag-based invalidation](#tag-based-invalidation) |

**Coalescing policy** (`CoalescingRequestPolicy`):

| Key | Effect |
|---|---|
| `BypassCoalescing` | Forwards the request independently, bypassing deduplication |

---

## Cache status header

Every response the caching layer handles carries a synthetic `X-Stampede-Cache` header reporting how it was obtained — the client-side equivalent of a CDN's `X-Cache`. Constants and a typed accessor live on `StampedeCacheStatus`:

```csharp
HttpResponseMessage response = await client.GetAsync("/api/products/42");
string? status = StampedeCacheStatus.GetStatus(response); // "HIT", "MISS", ...
```

| Value | Meaning |
|---|---|
| `HIT` | Served from a fresh cache entry, no origin contact (includes locally answered conditional requests returning `304`) |
| `STALE` | Served from an expired entry — `stale-while-revalidate`, `stale-if-error`, or the request's own `max-stale` |
| `REVALIDATED` | Served from cache after the origin confirmed it unchanged with `304 Not Modified` |
| `COALESCED` | Shared another concurrent caller's in-flight origin call instead of issuing its own |
| `MISS` | Fetched from the origin — no usable cache entry |

The header is absent when the caching layer didn't participate (unsafe methods, non-cacheable requests, `BypassCache`) — except `COALESCED`, which the coalescer sets on its own, so it also appears with `AddCoalescingOnly()`. It's set on the response handed back to the caller and never persisted: stored entries strip it, so a replayed hit always reports its own status. Handy in integration tests — assert `GetStatus(response) == StampedeCacheStatus.Hit` instead of instrumenting metrics or counting stub calls.

---

## Metrics

All instruments live under the **`Stampede.Http`** meter.

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("Stampede.Http"));
```

| Instrument | Type | Description |
|---|---|---|
| `stampede_http.cache.hits` | Counter | Requests served from cache |
| `stampede_http.cache.misses` | Counter | Requests forwarded to the origin |
| `stampede_http.cache.revalidations` | Counter | Conditional revalidation requests sent |
| `stampede_http.cache.stale_errors_served` | Counter | Stale responses served under stale-if-error |
| `stampede_http.cache.stale_while_revalidate_served` | Counter | Stale responses served during background revalidation |
| `stampede_http.cache.invalidations` | Counter | Entries evicted by unsafe method responses |
| `stampede_http.coalescing.deduplicated` | Counter | Requests that reused an in-flight response |
| `stampede_http.coalescing.inflight` | UpDownCounter | Current in-flight coalesced origin calls |
| `stampede_http.coalescing.timeouts` | Counter | Waiters that timed out and fell back to independent execution |

---

## Benchmark highlights

BenchmarkDotNet v0.15.2 · .NET 10 · Windows 11 · i7-12650H.

### Coalescing — backend load reduction

100 concurrent callers, 20 ms backend latency:

| Scenario | Mean | vs baseline |
|---|---:|---:|
| No coalescing (100 independent calls) | 623.79 ms | 1× |
| With coalescing (1 shared call) | 31.19 ms | **20× faster** |

### Caching — hit vs origin round-trip

10 ms simulated origin latency:

| Scenario | Mean | vs baseline |
|---|---:|---:|
| No cache (origin round-trip) | 15,591,147 ns | 1× |
| Cache hit (served from memory) | 538 ns | **~29,000× faster** |

---

## Runnable sample

[`samples/`](samples/) contains a full deployment — origin API, Redis, Polly, Prometheus, Grafana, Jaeger and a k6 load profile — plus a **control group**: a third instance of the same app with the Stampede.Http handlers removed, so the difference is measured rather than asserted.

```bash
cd samples && docker compose up --build -d
docker compose logs -f client-a
```

It narrates itself: a ten-caller stampede, then twelve feature scenarios each verified against the origin's own request counters, then a steady-state loop feeding the dashboards. See the [sample README](samples/README.md).

---

## Running the tests

```bash
dotnet test Stampede.Http.Tests
```

318 tests covering RFC 9111 caching, RFC 5861 stale extensions, request coalescing, distributed cache store, per-request policies, metrics, Polly integration (retry + hedging), and more.

---

## Contributing

Contributions are welcome. Please open an issue before submitting a pull request for significant changes.

- Follow the existing code style (C# 12+, `async/await`, nullable enabled)
- Add or update tests for any new logic
- Keep compiler warnings at zero

---

## License

MIT — see [LICENSE](LICENSE).

---

## Changelog

### v2.6.0
- **Tag-based invalidation (`IStampedeHttpCache.EvictByTagAsync`)** — responses can be labeled with cache tags, collected from the response headers named in `CacheOptions.TagHeaderNames` (`Cache-Tag`, `Surrogate-Key`, `xkey`… — comma- and space-separated values both work) or attached per request via `CacheRequestPolicy.Tags`; one call then evicts every entry carrying a tag, `Vary` variants included. The index lives in the client's own `ICacheStore` (memory or distributed) and is best-effort by design. Off by default: with `TagHeaderNames` unset and no request tags, nothing is indexed. See [Tag-based invalidation](#tag-based-invalidation).
- **Explicit eviction sweeps `Vary` variants.** `EvictAsync` previously removed only the primary-key marker of a varying resource, leaving its secondary-key variants unreachable-but-alive until their own retention elapsed. Variant keys are now tracked on the marker (`CacheEntry.TrackedKeys`) as they're stored, and eviction removes them too. §4.4 invalidation is deliberately unchanged (its marker removal already made variants unreachable; the extra read per unsafe request wasn't worth it).
- **Credential-scoped eviction (`EvictAsync(uri, authorization)`)** — closes the 2.4 limitation that per-credential entries stored under `AuthorizationCaching` couldn't be evicted programmatically: the new overload resolves the same credential-scoped key the authenticated GET stored under. The new `IStampedeHttpCache` members ship as default interface methods (throwing `NotSupportedException`), so custom pre-2.6 implementations keep compiling.
- **Cache status header (`X-Stampede-Cache`)** — every response the caching layer handles now reports how it was obtained: `HIT`, `MISS`, `STALE`, `REVALIDATED`, or `COALESCED` (set by the coalescer for waiters that shared another caller's in-flight origin call). Constants and a typed accessor on `StampedeCacheStatus`; never persisted into stored entries. See [Cache status header](#cache-status-header).

### v2.5.0
- **Coalescing non-GET requests (`CoalescerOptions.ShouldCoalesce`)** — opt specific `POST` (or other non-`GET`/`HEAD`) requests into coalescing, keyed on method + URL + a hash of the request body so two different bodies to the same URL are never merged. Buffering the body to hash it also makes it replayable for retry/hedging layers. Default remains unset — no method beyond `GET`/`HEAD` is ever coalesced unless explicitly matched. See [Coalescing non-GET requests](#coalescing-non-get-requests).
- **Early revalidation (`CacheOptions.EnableEarlyRevalidation`)** — XFetch probabilistic early expiration: a fresh cache hit can trigger a background refresh ahead of its expiry, with the trigger probability scaled by how expensive the entry was to fetch (`CacheEntry.OriginFetchDurationMs`, newly tracked on every origin call) and `EarlyRevalidationBeta`. Spreads out — rather than synchronizes — when concurrent callers/instances refetch a resource near its expiry. Default remains `false`; enabling it changes no other behavior. See [Early revalidation (XFetch)](#early-revalidation-xfetch).

### v2.4.0
- **Programmatic eviction (`IStampedeHttpCache`)** — evict a URI's cached GET response on demand, for when a resource changes through a channel the `HttpClient` didn't observe. Registered per client name like `ICacheStore`/`ICacheKeyBuilder`; see [Programmatic eviction](#programmatic-eviction).
- **Caching authorized requests (`CacheOptions.AuthorizationCaching`)** — requests carrying an `Authorization` header can now opt into caching (`WhenPermittedByResponse`, honoring RFC 9111 §3.5's `public`/`must-revalidate`/`s-maxage`, or `Always`). Default remains `Never`, matching every prior version exactly. Whenever enabled, both the cache key and the coalescing key fold in a hash of the `Authorization` value — never the raw credential — so different credentials for the same URL are always isolated and never coalesced together; this coalescing-side protection is unconditional and applies even without caching enabled at all. See [Caching authorized requests](#caching-authorized-requests) for the two documented limitations (`HEAD` and unsafe-method invalidation only ever resolve the unauthenticated key).

### v2.3.0
- **Client request `Cache-Control` directives** (RFC 9111 §5.2.1) — `max-age` and `min-fresh` can now tighten what counts as a fresh cache hit even when the stored entry itself is still within its server-set freshness lifetime (falling through to conditional revalidation, or a full request, when unmet); `max-stale` (with or without a value) widens acceptance to serve an already-expired entry directly, without contacting the origin, as long as the entry doesn't carry `must-revalidate`/`proxy-revalidate` (§5.2.2.2). Applies to both `GET` and `HEAD`. `max-age`/`min-fresh` are honored even for `Immutable` (RFC 8246) entries — immutability only exempts a response from the *origin's* `no-cache` semantics, not a client's own recency requirement.
- **Heuristic freshness** (RFC 9111 §4.2.2, opt-in via `EnableHeuristicFreshness`) — responses with a `Last-Modified` header but no `s-maxage`/`max-age`/`Expires` get an estimated freshness lifetime (`HeuristicFreshnessFraction` × age since `Last-Modified`, capped at `MaxHeuristicFreshness`) instead of always falling back to `DefaultTtl`. Disabled by default; enabling it does not change behavior for responses that already carry an explicit freshness directive.
- **Metrics carry a `stampede_http.client_name` tag** on every instrument, identifying which named `HttpClient` a measurement came from. The default/unnamed client and the internal test constructors emit no tag, so existing totals for single-client setups are unaffected.

### v2.2.2
- **~47% fewer allocations on the coalesced cache-miss path** (256 KB body / 8 waiters, measured): the caching layer now reuses the byte buffer the coalescer already materialized instead of reading it out again and rebuffering into a second `ByteArrayContent`. The saving scales with the number of coalesced waiters, since each used to pay for its own full copy of the response body.
- **`MaxResponseBodyBytes` / `MaxBodySizeBytes` are enforced while reading, not after.** A declared `Content-Length` over the limit is now rejected before a byte is read; a chunked body is abandoned mid-stream as soon as it crosses the limit. Previously an oversized response was fully buffered and only then rejected — exactly what the limit exists to prevent.
- **`HEAD` served from cache now repeats `Content-Type` and `Content-Length` (RFC 9110 §9.3.2).** A cached `HEAD` response previously had its content replaced wholesale to empty the body, silently dropping the content headers along with it.
- **Stale-while-revalidate deduplication is now per client, not per handler instance.** `IHttpClientFactory` rotates handler chains every two minutes and can keep several alive at once, so two live chains could revalidate the same key at the same time — the duplicated origin load stale-while-revalidate exists to avoid. A keyed-singleton coordinator per named client fixes this, and also closes a race where a very fast revalidation could finish before its own bookkeeping landed, permanently blocking future revalidation of that key.
- **Store efficiency:** entries with no freshness, no stale window, and no validator are no longer retained — without `MaxCacheSize` there is no LRU, so they previously lived for the process lifetime; §4.4 invalidation drops a redundant read before every delete (2 → 1 round-trip against a distributed store, 6 → 3 in the worst case); `Vary` field names are normalized once at store time instead of on every lookup.

### v2.2.1
- **`Age` resets after a successful revalidation (RFC 9111 §4.2.3 / §4.3.4).** When a stale entry was revalidated and the origin answered `304 Not Modified`, only the freshness lifetime was refreshed — the entry's stored-at time kept its original value, so the `Age` response header kept growing past the revalidation (e.g. `max-age=10` reporting `Age: 65`, then `68`, `70`… on subsequent fresh hits). A 304 now resets the stored-at time to the revalidation time and updates the stored response's header fields (`Date`, `Cache-Control`, `ETag`, …) with those carried on the 304, so `Age` restarts from the validation response. Applies to foreground, background (`stale-while-revalidate`), and HEAD-triggered revalidations, with both `MemoryCacheStore` and `DistributedCacheStore`. Also corrects the memory store's eviction window for refreshed entries, which was inflated by the stale stored-at time.

### v2.2.0
- **Vary: multiple representations are cached simultaneously (RFC 9111 §4.1).** Responses carrying a `Vary` header are now stored under a secondary cache key derived from the request's values for the Vary fields, with a small marker at the primary key recording which headers to vary on. Previously only one representation could be cached per URL — a `Vary: Accept-Encoding` resource requested by a gzip client and then an identity client kept overwriting the single entry, so content-negotiated endpoints never got variant cache hits. Works with both `MemoryCacheStore` and `DistributedCacheStore`; `Vary: *` remains uncacheable.
- **Conditional requests are no longer coalesced with non-conditional ones.** The coalescing key now folds in any conditional request headers (`If-None-Match`, `If-Modified-Since`, `If-Match`, `If-Unmodified-Since`, `If-Range`). Previously a plain `GET` and an `If-None-Match` revalidation for the same URL could collapse into one execution, letting a caller that never sent a validator receive a bodyless `304`. Identical revalidations still coalesce, so a revalidation storm is still collapsed into a single origin call.

### v2.1.0
- **`RevalidationGraceSeconds`** (default `300`) — entries carrying an `ETag` or `Last-Modified` validator are now retained in the cache store for a grace period beyond their freshness lifetime and stale windows. Previously a response with `max-age=N` and no stale windows was physically evicted exactly at expiry, so the conditional-revalidation path (`If-None-Match` / `If-Modified-Since` → `304`) could never fire with the default store — every expiry was a full refetch. Applies to both `MemoryCacheStore` and `DistributedCacheStore`; entries without a validator are unaffected. Set to `0` to restore the previous evict-at-expiry behavior. Like `MaxCacheSize`, this is a structural option read at registration time.

### v2.0.0
- **Package renamed: `Coalesce.Http` → `Stampede.Http`.** Same library, same feature set; the new name reflects what it does — stopping cache stampedes. Migration:
  - Package reference: `dotnet remove package Coalesce.Http && dotnet add package Stampede.Http`
  - Namespaces: `Coalesce.Http.*` → `Stampede.Http.*`
  - Registration: `AddCoalesceHttp(...)` → `AddStampedeHttp(...)` (`AddCachingOnly`, `AddCoalescingOnly` and `UseDistributedCacheStore` are unchanged)
  - Metrics: meter `Coalesce.Http` → `Stampede.Http`; instruments `coalesce_http.*` → `stampede_http.*` — update your OpenTelemetry meter registration and dashboards
  - Technique-level API names (`CoalescingHandler`, `CoalescerOptions`, `CoalesceKeyHeaders`, `CoalescingRequestPolicy`…) are unchanged

### v1.2.0
- **`IOptionsMonitor<T>` for runtime reconfiguration** — `CacheOptions` and `CoalescerOptions` are registered as named options keyed by client name. Runtime-tuneable settings (`DefaultTtl`, `MaxBodySizeBytes`, `Enabled`, `CoalescingTimeout`, `CoalesceKeyHeaders`, etc.) take effect immediately on configuration reload. Structural options (`MaxCacheSize`, `NormalizeQueryParameters`) are still read at registration time.
- **Content-header preservation** — `Content-Type`, `Content-Encoding`, and other content headers are now correctly restored on responses served from cache.
- **Multi-client cache isolation** — each named `HttpClient` gets its own keyed `IMemoryCache`, `ICacheStore`, and `ICacheKeyBuilder`, preventing `SizeLimit` conflicts and option bleed between clients.

### v1.1.0
- **Client conditional request pass-through** (RFC 9111 §4.3.2) — `If-None-Match`/`If-Modified-Since` matched against fresh entries returns `304 Not Modified` without hitting the origin.
- **Additional cacheable status codes** (RFC 9111 §3.2) — `301` cached heuristically; `404`, `405`, `410`, `414` cached only when an explicit `max-age`/`Expires` is present.
- **`Cache-Control: immutable`** (RFC 8246) — fresh immutable entries skip revalidation even on client `no-cache` or `ForceRevalidate`.
- **`Cache-Control: only-if-cached`** (RFC 9111 §5.2.1.7) — returns `504 Gateway Timeout` when no usable entry exists.
- **HEAD-aware metrics** — cache-hit and revalidation counters carry an `http.request.method = HEAD` tag dimension.
- **Accurate size accounting** — `MemoryCacheStore` now accounts for headers, Vary metadata, and ETag alongside the body.

### v1.0.4
- **Fix:** distributed cache TTL now covers stale-serving windows.
- **Fix:** unobserved task exceptions in `RequestCoalescer` no longer trigger `TaskScheduler.UnobservedTaskException`.

### v1.0.3
- Distributed cache store (`DistributedCacheStore`, `UseDistributedCacheStore()`).

### v1.0.2
- `Age` response header (RFC 9111 §5.1).

### v1.0.1
- Multi-targeting: .NET 8.0 + .NET 10.0.

### v0.0.6
- Per-request coalescing policy (`BypassCoalescing`); HEAD request coalescing; winner-cancellation fix.

### v0.0.5
- Per-request cache policy (`BypassCache`, `ForceRevalidate`, `NoStore`); `ICacheStore` abstraction; LRU eviction / `MaxCacheSize`; programmatic invalidation.

### v0.0.4
- `stale-while-revalidate`, `must-revalidate`/`proxy-revalidate`, unsafe method invalidation, `CoalescingTimeout`, `MaxResponseBodyBytes`.

### v0.0.3
- `stale-if-error`, `AddCachingOnly`/`AddCoalescingOnly`, `System.Diagnostics.Metrics`.

### v0.0.2
- RFC 9111 conditional revalidation (`ETag`, `Last-Modified`, `Vary`); Polly integration tests.

### v0.0.1
- Initial release.
