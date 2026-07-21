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

### CoalescerOptions

| Property | Default | Description |
|---|---|---|
| `Enabled` | `true` | Set to `false` to disable coalescing (useful for debugging) |
| `CoalescingTimeout` | `null` | How long a waiter will wait before falling back to an independent request. `null` = no timeout |
| `MaxResponseBodyBytes` | `1 MB` | Maximum body the coalescer will buffer; exceeding this throws for all waiters |
| `CoalesceKeyHeaders` | `[]` | Extra request headers (e.g. `X-Tenant-Id`) included in the coalescing key |

Both options classes are registered as **named options** (`IOptionsMonitor<T>`) keyed by the client name, so runtime-tuneable settings take effect immediately on configuration reload without restarting the app.

---

## Pipeline helpers

| Method | What it registers |
|---|---|
| `AddStampedeHttp()` | `CachingMiddleware` + `CoalescingHandler` + metrics |
| `AddCachingOnly()` | `CachingMiddleware` + metrics |
| `AddCoalescingOnly()` | `CoalescingHandler` + metrics |
| `UseDistributedCacheStore()` | Replaces `MemoryCacheStore` with `DistributedCacheStore` (chain after the above) |

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

**Coalescing policy** (`CoalescingRequestPolicy`):

| Key | Effect |
|---|---|
| `BypassCoalescing` | Forwards the request independently, bypassing deduplication |

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
