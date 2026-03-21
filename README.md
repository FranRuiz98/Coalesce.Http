# Coalesce.Http

> Advanced HTTP pipeline extensions for .NET — request coalescing, RFC 9111 caching, and seamless Polly integration.

[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com)
[![NuGet](https://img.shields.io/nuget/v/Coalesce.Http?label=NuGet&color=blue)](https://www.nuget.org/packages/Coalesce.Http)
[![Tests](https://img.shields.io/badge/tests-209%20passed-brightgreen)](#running-the-tests)
[![License](https://img.shields.io/badge/license-MIT-green)](#license)

**Coalesce.Http** is a .NET library that extends the `HttpClient` pipeline to solve common problems in high-concurrency distributed systems:

| Problem | Solution |
|---|---|
| Thundering-herd of duplicate concurrent requests | **Request coalescing** collapses them into a single backend call |
| Repeated fetches for unchanged resources | **RFC 9111 caching** with conditional revalidation (`ETag`, `If-None-Match`) |
| Cache stampede on expiry | Coalescing prevents multiple simultaneous origin calls when an entry expires |
| Stale data during origin failures | **stale-if-error** (RFC 5861 §4) serves cached responses while the origin recovers |
| High origin latency visible on cache expiry | **stale-while-revalidate** (RFC 5861 §3) serves stale instantly and refreshes in background |
| Mutating requests leaving stale GET entries | **Unsafe method invalidation** (RFC 9111 §4.4) evicts affected entries automatically |
| Unsafe retries / tail latency | Fully delegated to **Polly** — compatible out of the box, no friction |

Coalesce.Http does **not** replace `HttpClient` or Polly. It is a thin, composable layer that sits in the `DelegatingHandler` pipeline.

---

## Features

| Feature | Details |
|---|---|
| **RFC 9111 caching** | `max-age`, `s-maxage`, `Expires`, `no-cache`, `no-store`, `private`, `Vary`, `ETag`, `Last-Modified` |
| **must-revalidate / proxy-revalidate** | Blocks stale-if-error and stale-while-revalidate when the origin requires strict freshness (RFC 9111 §5.2.2.2) |
| **Unsafe method invalidation** | DELETE / POST / PUT / PATCH success evicts the affected GET entry + `Location` / `Content-Location` URIs (RFC 9111 §4.4) |
| **stale-while-revalidate** (RFC 5861 §3) | Serves stale instantly and triggers a background refresh; zero extra latency on expiry |
| **stale-if-error** (RFC 5861 §4) | Serves stale cached content when the origin returns 5xx or throws |
| **Per-request cache policy** | `CacheRequestPolicy.BypassCache`, `ForceRevalidate`, `NoStore` via `HttpRequestMessage.Options` |
| **Pluggable cache store** | `ICacheStore` interface; swap in Redis or any custom store without touching the middleware |
| **LRU eviction / size cap** | `MaxCacheSize` sets a byte ceiling; the memory store evicts least-recently-used entries automatically |
| **Request coalescing** | Concurrent identical GET/HEAD requests share a single backend call |
| **Per-request coalescing policy** | `CoalescingRequestPolicy.BypassCoalescing` via `HttpRequestMessage.Options` |
| **Retry-safe** | The winner's `CancellationToken` is not forwarded to the factory; retries inside the coalesced execution work correctly |
| **Hedging compatible** | The caching layer injects conditional headers once, before Polly; every hedged clone carries them |
| **System.Diagnostics.Metrics** | Nine instruments under the `Coalesce.Http` meter |
| **Configurable pipeline** | `AddCoalesceHttp`, `AddCachingOnly`, `AddCoalescingOnly` helpers |

---

## Installation

```bash
dotnet add package Coalesce.Http
```

> **Requirements:** .NET 8.0 or later. No third-party dependencies — only `Microsoft.Extensions.*`.

---

## Quick start

### Full pipeline (caching + coalescing)

```csharp
builder.Services
    .AddHttpClient("my-api")
    .AddCoalesceHttp(
        configureCaching:    o => o.DefaultTtl = TimeSpan.FromSeconds(60),
        configureCoalescing: o => o.Enabled    = true
    );
```

The pipeline inserted behind the scenes:

```
CachingMiddleware  (outermost — serves cache hits without touching the network)
  └─ CoalescingHandler  (deduplicates concurrent backend misses)
       └─ [Polly resilience handlers, if any]
            └─ HttpClientHandler  (primary handler)
```

### Adding Polly resilience

Always call `AddResilienceHandler` **after** `AddCoalesceHttp` so Polly sits between the coalescer and the transport:

```csharp
// Retry
services.AddHttpClient("catalog")
    .AddCoalesceHttp()
    .AddResilienceHandler("resilience", b => b.AddRetry(new HttpRetryStrategyOptions
    {
        MaxRetryAttempts = 3,
        BackoffType = DelayBackoffType.Exponential,
    }));

// Hedging
services.AddHttpClient("catalog")
    .AddCoalesceHttp()
    .AddResilienceHandler("resilience", b => b.AddHedging(new HttpHedgingStrategyOptions
    {
        MaxHedgedAttempts = 2,
        Delay = TimeSpan.FromMilliseconds(100),
    }));
```

---

## Configuration

### CacheOptions

| Property | Default | Description |
|---|---|---|
| `DefaultTtl` | `30s` | Fallback freshness lifetime when no `Cache-Control`/`Expires` directive is present |
| `MaxBodySizeBytes` | `1 MB` | Responses larger than this are not stored |
| `DefaultStaleIfErrorSeconds` | `0` | Fallback stale-if-error window when the response carries no `stale-if-error` directive; `0` disables the feature |
| `DefaultStaleWhileRevalidateSeconds` | `0` | Fallback stale-while-revalidate window when the response carries no `stale-while-revalidate` directive; `0` disables the feature |
| `MaxCacheSize` | `null` | Total byte ceiling for all cached bodies; when reached the memory store evicts least-recently-used entries. `null` means no limit |

### CoalescerOptions

| Property | Default | Description |
|---|---|---|
| `Enabled` | `true` | Set to `false` to disable coalescing (useful for debugging) |
| `CoalescingTimeout` | `null` | Maximum time a coalesced waiter will wait before falling back to an independent request. `null` means no timeout |
| `MaxResponseBodyBytes` | `1 MB` | Maximum response body the coalescer will buffer; responses exceeding this propagate an `InvalidOperationException` to all waiters |

---

## stale-if-error (RFC 5861 §4)

When the origin returns a `5xx` response or throws a network exception, Coalesce.Http can serve a stale cached entry if the `stale-if-error` window has not expired.

**Via response header:**

```
Cache-Control: max-age=60, stale-if-error=86400
```

**Via default configuration:**

```csharp
.AddCoalesceHttp(o => o.DefaultStaleIfErrorSeconds = 300)
```

The window is measured in seconds from the moment the entry became stale (`ExpiresAt`). An entry can be served under stale-if-error until `ExpiresAt + StaleIfErrorSeconds`.

> **Note:** `must-revalidate` and `proxy-revalidate` override stale-if-error. When either directive is present, the cache will never serve a stale response — the error propagates to the caller instead.

---

## stale-while-revalidate (RFC 5861 §3)

When a cache entry is stale but within the stale-while-revalidate window, Coalesce.Http serves the stale response immediately and triggers a **background revalidation** — the caller experiences zero additional latency.

**Via response header:**

```
Cache-Control: max-age=60, stale-while-revalidate=30
```

**Via default configuration:**

```csharp
.AddCoalesceHttp(o => o.DefaultStaleWhileRevalidateSeconds = 30)
```

Only one background revalidation runs per key at a time. If the background request returns a fresh `200`, the new entry is stored; if it returns `304`, the TTL is refreshed in place.

> **Note:** `must-revalidate` and `proxy-revalidate` also block stale-while-revalidate. In that case the request waits synchronously for the revalidation to complete.

---

## must-revalidate / proxy-revalidate (RFC 9111 §5.2.2.2)

When the origin includes `must-revalidate` or `proxy-revalidate` in `Cache-Control`, the middleware enforces strict freshness: stale-if-error and stale-while-revalidate are both disabled for that entry. A stale entry **must** be successfully revalidated before it can be served.

```
Cache-Control: max-age=60, must-revalidate
```

---

## Unsafe method invalidation (RFC 9111 §4.4)

A successful `POST`, `PUT`, `PATCH`, or `DELETE` response automatically evicts the cached GET entry for the affected URI, preventing stale reads after mutations.

The following URIs are invalidated on a non-error response (`1xx`–`3xx`):

| Header | RFC requirement |
|---|---|
| Effective request URI | MUST invalidate |
| `Location` response header | MAY invalidate (implemented) |
| `Content-Location` response header | MAY invalidate (implemented) |

No configuration required — invalidation is automatic for all unsafe methods in the pipeline.

---

## Per-request cache policy

Override caching behaviour on individual requests without changing global `CacheOptions`, using `HttpRequestMessage.Options`:

```csharp
var request = new HttpRequestMessage(HttpMethod.Get, "/api/products");
request.Options.Set(CacheRequestPolicy.BypassCache, true);
```

| Key | Effect |
|---|---|
| `CacheRequestPolicy.BypassCache` | Skips all cache interaction: no lookup, no storage, no unsafe-method invalidation. Request goes directly to the inner handler. |
| `CacheRequestPolicy.ForceRevalidate` | Forces conditional revalidation even if the cached entry is fresh. Equivalent to `no-cache` but set programmatically. |
| `CacheRequestPolicy.NoStore` | Prevents the response from being stored. Cache reads, revalidation, and 304 TTL refreshes still work normally. |

---

## Per-request coalescing policy

Override coalescing behaviour on individual requests without changing global `CoalescerOptions`, using `HttpRequestMessage.Options`:

```csharp
var request = new HttpRequestMessage(HttpMethod.Get, "/api/products");
request.Options.Set(CoalescingRequestPolicy.BypassCoalescing, true);
```

| Key | Effect |
|---|---|
| `CoalescingRequestPolicy.BypassCoalescing` | Skips coalescing entirely: the request is forwarded directly to the inner handler as an independent call, even if other identical requests are in flight. |

---

## Custom cache store

The default store is `MemoryCacheStore` backed by `IMemoryCache`. Implement `ICacheStore` to plug in any storage backend:

```csharp
public interface ICacheStore
{
    bool TryGetValue(string key, out CacheEntry? entry);
    void Set(string key, CacheEntry entry);
    void Remove(string key);
}
```

Register the custom store before calling `AddCoalesceHttp`:

```csharp
services.AddSingleton<ICacheStore, MyRedisStore>();

services.AddHttpClient("my-api")
    .AddCoalesceHttp();
```

You can also call `ICacheStore.Remove` directly at any time to evict a specific entry programmatically.

---

## Metrics

Register a `MeterListener` (or use OpenTelemetry) to collect the following instruments from the **`Coalesce.Http`** meter:

| Instrument | Unit | Type | Description |
|---|---|---|---|
| `coalesce_http.cache.hits` | requests | Counter | Requests served from cache |
| `coalesce_http.cache.misses` | requests | Counter | Requests forwarded to the origin |
| `coalesce_http.cache.revalidations` | requests | Counter | Conditional revalidation requests sent |
| `coalesce_http.cache.stale_errors_served` | requests | Counter | Stale responses served under stale-if-error |
| `coalesce_http.cache.stale_while_revalidate_served` | requests | Counter | Stale responses served while background revalidation runs |
| `coalesce_http.cache.invalidations` | requests | Counter | Cache entries evicted by unsafe method responses (RFC 9111 §4.4) |
| `coalesce_http.coalescing.deduplicated` | requests | Counter | Requests that reused an in-flight coalesced response |
| `coalesce_http.coalescing.inflight` | requests | UpDownCounter | Current in-flight coalesced requests at the origin |
| `coalesce_http.coalescing.timeouts` | requests | Counter | Coalesced waiters that timed out and fell back to independent execution |

`CoalesceHttpMetrics` is registered automatically as a singleton when you call any of the `AddCoalesceHttp*` helpers. Dispose the DI container to dispose the `Meter`.

---

## Pipeline helpers

| Method | Registers |
|---|---|
| `AddCoalesceHttp()` | `CachingMiddleware` + `CoalescingHandler` + `CoalesceHttpMetrics` |
| `AddCachingOnly()` | `CachingMiddleware` + `CoalesceHttpMetrics` |
| `AddCoalescingOnly()` | `CoalescingHandler` + `CoalesceHttpMetrics` |

---

## Polly compatibility rules

| Rule | Behaviour |
|---|---|
| **Rule 1 — retry** | All coalesced callers share the winner's execution, including any Polly retries that occur within it. The transport is called at most once per retry attempt, not once per caller. |
| **Rule 2 — conditional headers** | `CachingMiddleware` injects `If-None-Match`/`If-Modified-Since` before the request enters the Polly pipeline. Every retry attempt carries those headers unchanged. |
| **Rule 3 — hedging** | Each hedged clone must carry its own independent copy of the request; `FakeHedgingHandler` and `Microsoft.Extensions.Http.Resilience` both satisfy this requirement. |

---

## How request coalescing works

```
  Time ──────────────────────────────────────────────►

  Caller A ──► CoalescingHandler ──► [winner] ──► origin
  Caller B ──► CoalescingHandler ──► [waits]  ──► ↑ shares result
  Caller C ──► CoalescingHandler ──► [waits]  ──► ↑ shares result

  Result: 3 callers, 1 origin call, 3 independent cloned responses
```

Under the hood, `RequestCoalescer` uses a `ConcurrentDictionary<RequestKey, TaskCompletionSource>` for **lock-free coordination**. Each caller receives a **cloned** `HttpResponseMessage` with its own readable content stream — there is no shared mutable state between callers.

Coalescing applies to **GET** and **HEAD** requests. All other HTTP methods bypass coalescing automatically. GET and HEAD requests to the same URL are coalesced independently (they have separate coalescing keys because `RequestKey` includes the method).

---

## How HTTP caching works

Freshness lifetime follows RFC 9111 §4.2.1 priority order:

```
s-maxage  →  max-age  →  Expires header  →  DefaultTtl (fallback)
```

On expiry, the middleware performs **conditional revalidation** rather than a full download:

- Sends `If-None-Match` when the stored entry has an `ETag`
- Sends `If-Modified-Since` when the stored entry has a `Last-Modified` date
- A `304 Not Modified` response refreshes the TTL without re-downloading the body

On origin error, **stale-if-error** (RFC 5861) serves the cached entry within the configured window.

---

## OpenTelemetry setup

```csharp
builder.Services
    .AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("Coalesce.Http"));
```

---

## Project structure

```
Coalesce.Http/
├─ Coalescing/
│  ├─ RequestCoalescer.cs       ← lock-free coalescing core (ConcurrentDictionary + TCS)
│  ├─ RequestKey.cs             ← (Method, Url) value type identity
│  ├─ CoalescedRequest.cs       ← shared TaskCompletionSource state
│  ├─ CachedResponse.cs         ← cloneable HttpResponseMessage snapshot
│  └─ CoalescingRequestPolicy.cs ← per-request option key (BypassCoalescing)
├─ Caching/
│  ├─ CachingMiddleware.cs      ← RFC 9111 + RFC 5861 (stale-if-error, stale-while-revalidate)
│  ├─ FreshnessCalculator.cs    ← s-maxage → max-age → Expires → DefaultTtl
│  ├─ ICacheStore.cs            ← pluggable store interface
│  ├─ MemoryCacheStore.cs       ← default IMemoryCache-backed implementation with LRU eviction
│  ├─ CacheRequestPolicy.cs     ← per-request option keys (BypassCache, ForceRevalidate, NoStore)
│  ├─ CacheEntry.cs
│  ├─ CacheOptions.cs
│  ├─ ICacheKeyBuilder.cs
│  └─ DefaultCacheKeyBuilder.cs
├─ Handlers/
│  └─ CoalescingHandler.cs      ← DelegatingHandler wrapping RequestCoalescer
├─ Metrics/
│  └─ CoalesceHttpMetrics.cs    ← System.Diagnostics.Metrics (OpenTelemetry-compatible)
├─ Options/
│  └─ CoalescerOptions.cs
└─ Extensions/
   └─ HttpClientBuilderExtensions.cs  ← AddCoalesceHttp / AddCachingOnly / AddCoalescingOnly
```

---

## Dependencies

The library has **no third-party dependencies**. It only references standard Microsoft packages:

| Package | Purpose |
|---|---|
| `Microsoft.Extensions.Http` | `IHttpClientBuilder`, `DelegatingHandler` |
| `Microsoft.Extensions.Caching.Memory` | In-memory cache store |
| `Microsoft.Extensions.Caching.Abstractions` | `IMemoryCache` interface |

---

## Running the tests

```bash
dotnet test Coalesce.Http.Tests
```

209 tests covering coalescing, caching, stale-if-error, stale-while-revalidate, must-revalidate, unsafe method invalidation, per-request cache policy, per-request coalescing policy, metrics, Polly integration (retry + hedging), and response cloning.

---

## Running the benchmarks

```bash
cd BenchmarkSuite1
dotnet run -c Release
```

Benchmarks use [BenchmarkDotNet](https://benchmarkdotnet.org) and cover coalescing throughput at varying concurrency levels and cache hit/miss latency.

---

## Benchmark Results

All benchmarks run with BenchmarkDotNet v0.15.2, .NET 10.0.5, MediumRunJob (15 iterations × 2 launches × 10 warmups).
Environment: Windows 11 — 12th Gen Intel Core i7-12650H, 10 cores / 16 threads.

### Request Coalescing — Backend Load Reduction

Simulates a rate-limited backend (max 5 concurrent requests, 20 ms latency each).
Without coalescing, N callers queue behind the bottleneck. With coalescing, **only 1 call is made** regardless of N.

| Method | Concurrency | Mean | Ratio | Allocated |
|---|---:|---:|---:|---:|
| **No coalescing (N independent calls)** | **10** | **62.35 ms** | **1.00** | **3.42 KB** |
| With coalescing (1 shared call) | 10 | 31.20 ms | 0.50 | 8.01 KB |
| | | | | |
| **No coalescing (N independent calls)** | **50** | **311.76 ms** | **1.00** | **17.80 KB** |
| With coalescing (1 shared call) | 50 | 31.14 ms | 0.10 | 31.93 KB |
| | | | | |
| **No coalescing (N independent calls)** | **100** | **623.79 ms** | **1.00** | **35.77 KB** |
| With coalescing (1 shared call) | 100 | 31.19 ms | 0.05 | 61.86 KB |

> **100 concurrent requests → 20× faster.** The backend sees 1 request instead of 100.

### Caching — Cache Hit vs Origin Round-Trip

Isolates the caching layer to show the throughput difference between a cache hit and a real origin round-trip (10 ms simulated latency).

| Method | Mean | Error | Ratio | Allocated |
|---|---:|---:|---:|---:|
| **No cache (origin round-trip)** | **15,591,147 ns** | **±39,617 ns** | **1.000** | **1.68 KB** |
| Cache hit (served from memory) | 538 ns | ±8.62 ns | 0.000 | 1.75 KB |

> **Cache hits are ~29,000× faster** — sub-microsecond response with near-zero overhead.

### End-to-End Pipeline — Full Stack Comparison

50 sequential GET requests to the same endpoint. The plain client hits the backend every time (10 ms latency). Coalesce.Http serves all 50 from cache after the first request.

| Method | Mean | Error | Ratio | Allocated |
|---|---:|---:|---:|---:|
| **Plain HttpClient (no cache, no coalescing)** | **779,477 μs** | **±1,540 μs** | **1.000** | **88.77 KB** |
| Coalesce.Http pipeline (cache hits) | 27.16 μs | ±0.22 μs | 0.000 | 84.77 KB |
| Coalesce.Http concurrent burst | 28.46 μs | ±0.22 μs | 0.000 | 86.09 KB |

> **50 requests in 27 μs vs 779 ms — over 28,000× faster.**

### Caching Middleware — Micro-benchmarks

| Method | Mean | Error | Allocated |
|---|---:|---:|---:|
| Cache Hit (GET served from cache) | 533 ns | ±2.41 ns | 1.74 KB |
| Cache Miss (forwarded and stored) | 4,449 ns | ±203 ns | 3.98 KB |
| Non-cacheable (POST bypass) | 506 ns | ±7.46 ns | 1.73 KB |

### Coalescing — Micro-benchmarks

| Method | Concurrency | Mean | Allocated |
|---|---:|---:|---:|
| Sequential requests (no coalescing) | 1 | 4,024 ns | 15.63 KB |
| Concurrent coalesced into one call | 1 | 5,072 ns | 1.98 KB |
| Sequential requests (no coalescing) | 100 | 4,103 ns | 15.63 KB |
| Concurrent coalesced into one call | 100 | 40,169 ns | 55.24 KB |
| Independent keys (no benefit) | 100 | 511 ns | 1.63 KB |

---

## Contributing

Contributions are welcome. Please open an issue before submitting a pull request for significant changes.

- Follow the existing code style (C# 12+; C# 14 features only inside `#if NET9_0_OR_GREATER` / `#if NET10_0_OR_GREATER` guards, `async/await` for I/O, nullable enabled)
- Add or update unit tests for any new logic
- Keep compiler warnings at zero

---

## License

MIT — see [LICENSE](LICENSE).

---

## Changelog

### v1.0.1
- **Multi-targeting** — added .NET 8.0 support alongside .NET 10.0; minimum requirement is now .NET 8.0 or later

### v0.0.6
- **Per-request coalescing policy** — `CoalescingRequestPolicy.BypassCoalescing` via `HttpRequestMessage.Options`; opt out of deduplication on individual requests
- **HEAD request coalescing** — concurrent identical HEAD requests are now coalesced (GET and HEAD use separate coalescing keys)
- **Fix: winner cancellation no longer poisons waiters** — if the winner's `CancellationToken` fires during body reading, the `OperationCanceledException` no longer propagates to all coalesced waiters via the shared `TaskCompletionSource`

### v0.0.5
- **Per-request cache policy** — `CacheRequestPolicy.BypassCache`, `ForceRevalidate`, `NoStore` via `HttpRequestMessage.Options`
- **`ICacheStore` abstraction** — pluggable cache store interface; swap in any backend without touching the middleware
- **LRU eviction / `MaxCacheSize`** — byte ceiling on the in-memory store with automatic least-recently-used eviction
- **Programmatic invalidation** — `ICacheStore.Remove` evicts individual entries on demand

### v0.0.4
- **stale-while-revalidate (RFC 5861 §3)** — serve stale instantly and refresh in background; zero extra latency on expiry
- **must-revalidate / proxy-revalidate (RFC 9111 §5.2.2.2)** — blocks stale-if-error and stale-while-revalidate when the origin requires strict freshness
- **Unsafe method invalidation (RFC 9111 §4.4)** — DELETE / POST / PUT / PATCH success evicts the affected GET entry and related `Location` / `Content-Location` URIs
- **`CoalescerOptions.CoalescingTimeout`** — waiters fall back to independent execution after the configured timeout
- **`CoalescerOptions.MaxResponseBodyBytes`** — guards against buffering oversized coalesced responses
- **Three new metrics** — `coalesce_http.cache.stale_while_revalidate_served`, `coalesce_http.cache.invalidations`, `coalesce_http.coalescing.timeouts`

### v0.0.3
- **stale-if-error (RFC 5861)** — serve stale cached content on 5xx / network failures
- **`AddCoalesceHttp` overload** — accepts both `Action<CacheOptions>` and `Action<CoalescerOptions>`
- **`AddCachingOnly` / `AddCoalescingOnly` helpers** — use either layer independently
- **`CoalescerOptions.Enabled`** — disable coalescing at runtime without removing the handler
- **`System.Diagnostics.Metrics`** — six instruments under the `Coalesce.Http` meter
- **Coalescing benchmarks** — `CoalescingBenchmarks` added to the benchmark suite

### v0.0.2
- RFC 9111 conditional revalidation (`ETag`, `If-None-Match`, `Last-Modified`, `If-Modified-Since`)
- `Vary` / `Vary: *` support
- Polly retry + hedging integration tests (simulated and real)

### v0.0.1
- Initial release: `CachingMiddleware`, `CoalescingHandler`, `AddCoalesceHttp`
