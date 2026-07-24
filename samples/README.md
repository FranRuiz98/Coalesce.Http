# Stampede.Http — real-world sample

A complete, runnable deployment showing **Stampede.Http + Redis + Polly** working together over a real network, with **live metrics in Grafana**:

```
┌──────────┐   ┌─────────────┐
│ client-a │──►│  Sample API │      client pipeline:
├──────────┤   │  (Kestrel)  │      CachingMiddleware  ← Redis-backed, shared
│ client-b │   └─────────────┘        └─ CoalescingHandler  ← per process
└────┬─────┘                              └─ Polly retry + timeout
     │                                        └─ HttpClientHandler
     ▼
┌──────────┐   ┌────────────┐   ┌─────────┐
│  Redis   │   │ Prometheus │──►│ Grafana │
└──────────┘   └─────△──────┘   └─────────┘
                     │ scrapes /metrics from both clients
```

The API declares caching policy purely through standard headers (`Cache-Control`, `ETag`, `Vary`) — there is **zero Stampede.Http code on the server**. The client configures the whole pipeline in one statement:

```csharp
services.AddHttpClient("api", c => c.BaseAddress = new Uri(apiBase))
    .AddStampedeHttp(
        configureCaching: o => o.DefaultTtl = TimeSpan.FromSeconds(5),
        configureCoalescing: o => o.CoalescingTimeout = TimeSpan.FromSeconds(10))
    .UseDistributedCacheStore()                       // Redis via IDistributedCache
    .AddResilienceHandler("sample-resilience", b =>   // Polly: retries + timeout
    {
        b.AddRetry(new HttpRetryStrategyOptions { MaxRetryAttempts = 2, BackoffType = DelayBackoffType.Exponential });
        b.AddTimeout(TimeSpan.FromSeconds(5));
    });
```

## Run it

```bash
docker compose up --build
```

Requires Docker. Watch the `client-a` / `client-b` logs, then open:

| | URL |
|---|---|
| **Grafana dashboard** ("Stampede.Http — Live Metrics", auto-provisioned, no login needed) | http://localhost:3000/d/stampede-http-overview |
| Prometheus (raw queries, target health under **Status → Targets**) | http://localhost:9090 |
| client-a's own `/metrics` endpoint | http://localhost:9464/metrics |
| client-b's own `/metrics` endpoint | http://localhost:9465/metrics |

> Grafana's anonymous-admin login is a convenience for this local demo only — never do that in a real deployment.

### Without Docker (except Redis)

```bash
docker run -d -p 6379:6379 redis:7-alpine
dotnet run --project Stampede.Http.Sample.Api   # listens on http://localhost:5080
dotnet run --project Stampede.Http.Sample.Client
```

(Set `ASPNETCORE_URLS=http://localhost:5080` for the API if your default differs.) The client still exposes Prometheus metrics on `http://localhost:9464/metrics`, but Prometheus/Grafana aren't running outside compose — point your own instance at it, or just read the values in your browser.

## What to watch

| Moment | What it demonstrates |
|---|---|
| **Phase 1**: 10 concurrent `GET /slow` (2 s origin latency) finish in ~2 s total | Request coalescing — each instance's burst collapses into one origin call |
| `client-b`'s Phase 1 is instant if it starts after `client-a`'s | The Redis cache is **shared**: `client-b` hits the entry `client-a` stored |
| `GET /catalog` shows `Age: Ns` and ~0 ms | Fresh hits served from Redis, no network to the origin |
| After 10 s, one slow `/catalog` request, then fast again | Expiry → conditional revalidation (`If-None-Match`, 304) |
| `POST /catalog` line, then next `GET /catalog` refetches with a new version | Unsafe-method invalidation (RFC 9111 §4.4) — shared through Redis, both instances see it |
| `/flaky` keeps returning **200** while the origin is in its failure window | Polly retries the blips; when the outage persists, `stale-if-error` serves the last good response (look for `[served beyond max-age: stale window or revalidated entry]`) |
| `/feed` never blocks after the first fetch | `stale-while-revalidate` refreshes in the background |
| Periodic `stampede_http.*` metrics block in the console logs | The same counters the Grafana dashboard graphs in real time |
| Grafana's "cache hits vs misses" panel climbing while "coalescing deduplicated" stays flat between bursts | Steady-state traffic is dominated by cache hits; coalescing only fires during the Phase 1 stampede and the periodic origin-refresh moments |

### The outage drill

```bash
docker compose stop api
```

The clients keep receiving **200 OK** for `/catalog` and `/flaky` (stale-if-error window) instead of connection errors. Then:

```bash
docker compose start api
```

and watch them transparently return to fresh responses.

### Origin's point of view

```bash
curl http://localhost:5080/stats
```

`no-store` live counters: how many requests actually reached the origin — across *all* client instances. Compare it with how many requests the clients have issued.

## Live metrics: Prometheus + Grafana

Every `stampede_http.*` instrument (the same ones described in the [main README](../README.md#metrics)) is exported by each client via `OpenTelemetry.Exporter.Prometheus.HttpListener` on port `9464`, scraped by Prometheus every 5 seconds, and graphed by a pre-provisioned Grafana dashboard — no manual data source or dashboard setup required.

The dashboard has 10 panels: cache hits/misses/hit-ratio, coalescing deduplication (rate) and in-flight count, stale-if-error and stale-while-revalidate rates, revalidations and invalidations, and coalescing timeouts — each broken down **per client instance** so you can watch `client-a` and `client-b` side by side. That per-instance split is exactly what makes the earlier nuance visible: cache-hit panels for both instances rise together (shared Redis), while coalescing panels move independently (per-process).

> **Naming caveat:** Prometheus 3.x negotiates UTF-8 metric names (e.g. `stampede_http.cache.hits_requests_total`, dots preserved) with targets that support it, which the OTel exporter does. `prometheus.yml` sets `metric_name_escaping_scheme: underscores` globally so names stay in the classic Prometheus form (`stampede_http_cache_hits_requests_total`) that the dashboard queries and this README use.

## One nuance worth knowing

The **cache** (Redis) is shared across instances, but **coalescing is per process**: if both replicas miss the same key at the same instant, each makes its own origin call (2 total, not 1 — still not 20). Cross-instance request deduplication would require a distributed lock, which is out of scope for an HTTP client library.
