# Coalesce.Http

> Advanced HTTP pipeline extensions for .NET — request coalescing, RFC 9111 caching, and seamless Polly integration.

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com)
[![NuGet](https://img.shields.io/nuget/v/Coalesce.Http?label=NuGet&color=blue)](https://www.nuget.org/packages/Coalesce.Http)
[![Tests](https://img.shields.io/badge/tests-107%20passed-brightgreen)](#running-the-tests)
[![License](https://img.shields.io/badge/license-MIT-green)](#license)

**Coalesce.Http** is a .NET library that extends the `HttpClient` pipeline to solve common problems in high-concurrency distributed systems:

| Problem | Solution |
|---|---|
| Thundering-herd of duplicate concurrent requests | **Request coalescing** collapses them into a single backend call |
| Repeated fetches for unchanged resources | **RFC 9111 caching** with conditional revalidation (`ETag`, `If-None-Match`) |
| Cache stampede on expiry | Coalescing prevents multiple simultaneous origin calls when an entry expires |
| Stale data during origin failures | **stale-if-error** (RFC 5861) serves cached responses while the origin recovers |
| Unsafe retries / tail latency | Fully delegated to **Polly** — compatible out of the box, no friction |

Coalesce.Http does **not** replace `HttpClient` or Polly. It is a thin, composable layer that sits in the `DelegatingHandler` pipeline.

---

## Features

| Feature | Details |
|---|---|
| **RFC 9111 caching** | `max-age`, `s-maxage`, `Expires`, `no-cache`, `no-store`, `private`, `Vary`, `ETag`, `Last-Modified` |
| **Request coalescing** | Concurrent identical GET requests share a single backend call |
| **stale-if-error** (RFC 5861) | Serves stale cached content when the origin returns 5xx or throws |
| **Retry-safe** | The winner's `CancellationToken` is not forwarded to the factory; retries inside the coalesced execution work correctly |
| **Hedging compatible** | The caching layer injects conditional headers once, before Polly; every hedged clone carries them |
| **System.Diagnostics.Metrics** | Six instruments under the `Coalesce.Http` meter |
| **Configurable pipeline** | `AddCoalesceHttp`, `AddCachingOnly`, `AddCoalescingOnly` helpers |

---

## Installation

```bash
dotnet add package Coalesce.Http
```

> **Requirements:** .NET 10.0 or later. No third-party dependencies — only `Microsoft.Extensions.*`.

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

### CoalescerOptions

| Property | Default | Description |
|---|---|---|
| `Enabled` | `true` | Set to `false` to disable coalescing (useful for debugging) |

---

## stale-if-error (RFC 5861)

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

---

## Metrics

Register a `MeterListener` (or use OpenTelemetry) to collect the following instruments from the **`Coalesce.Http`** meter:

| Instrument | Unit | Type | Description |
|---|---|---|---|
| `coalesce_http.cache.hits` | requests | Counter | Requests served from cache |
| `coalesce_http.cache.misses` | requests | Counter | Requests forwarded to the origin |
| `coalesce_http.cache.revalidations` | requests | Counter | Conditional revalidation requests sent |
| `coalesce_http.cache.stale_errors_served` | requests | Counter | Stale responses served under stale-if-error |
| `coalesce_http.coalescing.deduplicated` | requests | Counter | Requests that reused an in-flight coalesced response |
| `coalesce_http.coalescing.inflight` | requests | UpDownCounter | Current in-flight coalesced requests at the origin |

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
│  └─ CachedResponse.cs         ← cloneable HttpResponseMessage snapshot
├─ Caching/
│  ├─ CachingMiddleware.cs      ← RFC 9111 + RFC 5861 (stale-if-error)
│  ├─ FreshnessCalculator.cs    ← s-maxage → max-age → Expires → DefaultTtl
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

107 tests covering coalescing, caching, stale-if-error, metrics, Polly integration (retry + hedging), and response cloning.

---

## Running the benchmarks

```bash
cd BenchmarkSuite1
dotnet run -c Release
```

Benchmarks use [BenchmarkDotNet](https://benchmarkdotnet.org) and cover coalescing throughput at varying concurrency levels and cache hit/miss latency.

---

## Contributing

Contributions are welcome. Please open an issue before submitting a pull request for significant changes.

- Follow the existing code style (C# 14, `async/await` for I/O, nullable enabled)
- Add or update unit tests for any new logic
- Keep compiler warnings at zero

---

## License

MIT — see [LICENSE](LICENSE).

---

## Changelog

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
