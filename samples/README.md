# Stampede.Http — real-world sample

A complete, runnable deployment showing **Stampede.Http + Redis + Polly** working together over a real network:

```
┌─────────────┐   ┌─────────────┐
│  client ×2  │──►│  Sample API │      client pipeline:
│ (replicas)  │   │  (Kestrel)  │      CachingMiddleware  ← Redis-backed, shared
└──────┬──────┘   └─────────────┘        └─ CoalescingHandler  ← per process
       │                                      └─ Polly retry + timeout
       ▼                                           └─ HttpClientHandler
┌─────────────┐
│    Redis    │  ← one shared HTTP cache for every client instance
└─────────────┘
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

Requires Docker. Watch the two `client` replicas' logs.

### Without Docker (except Redis)

```bash
docker run -d -p 6379:6379 redis:7-alpine
dotnet run --project Stampede.Http.Sample.Api   # listens on http://localhost:5080
dotnet run --project Stampede.Http.Sample.Client
```

(Set `ASPNETCORE_URLS=http://localhost:5080` for the API if your default differs.)

## What to watch

| Moment | What it demonstrates |
|---|---|
| **Phase 1**: 10 concurrent `GET /slow` (2 s origin latency) finish in ~2 s total | Request coalescing — each instance's burst collapses into one origin call |
| The *second* replica's Phase 1 is instant | The Redis cache is **shared**: instance B hits the entry instance A stored |
| `GET /catalog` shows `Age: Ns` and ~0 ms | Fresh hits served from Redis, no network to the origin |
| After 10 s, one slow `/catalog` request, then fast again | Expiry → conditional revalidation (`If-None-Match`, 304) |
| `POST /catalog` line, then next `GET /catalog` refetches with a new version | Unsafe-method invalidation (RFC 9111 §4.4) — shared through Redis, both replicas see it |
| `/flaky` keeps returning **200** while the origin is in its failure window | Polly retries the blips; when the outage persists, `stale-if-error` serves the last good response (look for `[likely STALE — origin shielded]`) |
| `/feed` never blocks after the first fetch | `stale-while-revalidate` refreshes in the background |
| Periodic `stampede_http.*` metrics block | The same counters you would export via OpenTelemetry |

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

## One nuance worth knowing

The **cache** (Redis) is shared across instances, but **coalescing is per process**: if both replicas miss the same key at the same instant, each makes its own origin call (2 total, not 1 — still not 20). Cross-instance request deduplication would require a distributed lock, which is out of scope for an HTTP client library.
