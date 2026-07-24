# Stampede.Http — real-world sample

A complete, runnable deployment of **Stampede.Http + Redis + Polly**, with metrics, traces, a load generator, and — the part that makes it more than a demo — **a control group**.

Three copies of the same application run the same workload against the same origin. Two have Stampede.Http in their outbound pipeline; one does not. Every claim below is the difference between them, measured at the origin.

```
                          ┌──────────────────────────┐
 client-a        ──┐      │       Sample API         │  client pipeline:
 client-b        ──┼─────►│        (Kestrel)         │  CachingMiddleware  ← Redis-backed, shared
 client-baseline ──┘      │  headers only — no       │    └─ CoalescingHandler  ← per process
     ▲       │            │  Stampede.Http code      │         └─ Polly retry + timeout
     │       │            └────────────┬─────────────┘              └─ SocketsHttpHandler
     │       ▼                         │
  ┌──┴───┐ ┌──────┐          counts every request
  │  k6  │ │Redis │          that actually arrived
  └──────┘ └──────┘                    │
                   ┌───────────┐       │       ┌────────┐
                   │Prometheus │◄──────┴──────►│ Jaeger │
                   └─────△─────┘               └────────┘
                         │ scrapes all three clients + the origin
                   ┌─────┴─────┐
                   │  Grafana  │
                   └───────────┘
```

The API declares caching policy purely through standard headers (`Cache-Control`, `ETag`, `Vary`, `Last-Modified`) — there is **zero Stampede.Http code on the server**. The client configures the whole pipeline in one place, [`PipelineRegistration.cs`](Stampede.Http.Sample.Client/PipelineRegistration.cs), and nothing else in the app knows the library exists.

---

## Run it

```bash
docker compose up --build -d
```

| | URL |
|---|---|
| **Grafana dashboard** (auto-provisioned, no login) | http://localhost:3000/d/stampede-http-overview |
| **Jaeger traces** | http://localhost:16686 |
| Prometheus (raw queries, target health under **Status → Targets**) | http://localhost:9090 |
| Origin API | http://localhost:5080 |
| `client-a` — Stampede.Http, runs the feature tour | http://localhost:5081 |
| `client-b` — Stampede.Http, shares the Redis cache with `client-a` | http://localhost:5082 |
| `client-baseline` — **control group**, no Stampede.Http | http://localhost:5083 |

> Grafana's anonymous-admin login is a convenience for this local demo only — never do that in a real deployment.

Then watch the logs:

```bash
docker compose logs -f client-a
```

---

## The first 90 seconds, narrated

`client-a` runs a scripted workload in three phases and explains itself as it goes.

### Phase 1 — the stampede

Ten concurrent callers hit `/slow`, an endpoint that takes the origin two seconds.

```
PHASE 1 — stampede: 10 concurrent GET /slow (the origin takes ~2 s per call)
  10 callers finished in 2039 ms — 10/10 OK. Coalescing collapsed this instance's
  burst into a single origin call.
```

The same burst against `client-baseline` produces twenty origin calls. The smoke test asserts exactly that.

### Phase 2 — the feature tour

Twelve scenarios, each **verified against the origin's own request counters** rather than against the client's opinion of what happened. Real output from a clean run:

```
[ok] Vary: Accept-Language: 3 languages cold → 3 origin calls; the same 3 again → 0.
[ok] CoalesceKeyHeaders: X-Tenant-Id: 10 concurrent callers across 2 tenants → 2 origin calls.
[ok] Client conditional pass-through: If-None-Match against a fresh entry → 304, 0 origin calls.
[ok] CacheRequestPolicy.ForceRevalidate: origin answered 1 × 304, caller still got 200.
[ok] CacheRequestPolicy.BypassCache: Fresh entry ignored entirely → 1 origin call.
[ok] CacheRequestPolicy.NoStore: NoStore then a plain fetch → 2 origin calls: nothing was stored.
[ok] CoalescingRequestPolicy.BypassCoalescing: 5 concurrent callers → 5 origin calls when
     bypassing, 1 when coalescing.
[ok] Cache-Control: only-if-cached: Nothing cached → 504 Gateway Timeout, 0 origin calls.
[ok] Cache-Control: immutable: cold → 1 origin call; a forced revalidation on top → 0.
[ok] CacheOptions.MaxBodySizeBytes: ~1.8 MB body fetched twice → 2 origin calls: too large to store.
[ok] CacheOptions.NormalizeQueryParameters: reordered parameters → 1 origin call.
[ok] Last-Modified revalidation: expired entry kept for revalidation → 1 × 304 to
     If-Modified-Since, caller got 200 with no body transferred.
PHASE 2 — feature tour complete: 12/12 checks behaved as documented
```

The tour runs on `client-a` only (`Sample:Workload:FeatureTour`): its assertions are deltas on shared origin counters, so a second caller hitting the same endpoints would skew them. Don't run the k6 profile at the same time.

### Phase 3 — steady state

`/catalog` + `/feed` + `/flaky` every two seconds, forever, with a `POST /catalog` every tenth iteration. This is what feeds the dashboards.

---

## The measurement

`client-baseline` is the same image, the same workload and the same Polly pipeline — with `Sample:Pipeline:Enabled=false`, which removes the two Stampede.Http handlers and nothing else. The origin tags every request it receives with the `X-Client` header the clients send, so the comparison is a single Prometheus query:

```promql
sum by (client) (rate(sample_api_origin_requests_total[1m]))
```

The Grafana dashboard's top row turns that into three numbers: origin req/s per Stampede.Http client, origin req/s for the control client, and **origin load avoided** as a percentage. It restricts itself to the endpoints all three clients exercise (`/catalog`, `/feed`, `/flaky`), so the feature tour's extra traffic cannot flatter the result.

### How to read that percentage

The headline figure lands around **40%**, and taken on its own it is misleading in both directions. The **Origin load avoided by endpoint** panel next to it is the one that explains why. A real 5-minute window, per client:

| Endpoint | Stampede.Http | control | avoided |
|---|---:|---:|---:|
| `/catalog` (incl. the `POST`s, which no cache can absorb) | 0.153 req/s | 0.366 req/s | **58%** |
| `/feed` | 0.153 | 0.332 | **54%** |
| `/flaky` | 0.438 | 0.542 | **19%** |
| **total** | **0.743** | **1.240** | **40%** |

Expect your own figures to differ by several points: `/flaky` alternates between healthy and failing on a 60-second cycle, so a 5-minute rate window never covers a whole number of cycles. The shape of the table is stable; the exact digits are not.

**`/flaky` drags the total down, and that is correct.** It accounts for well over half of the origin traffic Stampede.Http did *not* avoid, because **a failure cannot be cached ahead of time**. `stale-if-error` rescues the caller *after* the origin has failed, so the request goes out regardless — and Polly then retries it twice. `/flaky` is down for 20 seconds of every 60. On healthy traffic only, the saving is around **56%**.

**The TTLs here are deliberately hostile.** For a cache that obeys origin headers, the ceiling is roughly `1 − poll interval / max-age`:

| `max-age` | polled every | ceiling |
|---|---|---:|
| 5 s (`/feed`) | ~3 s | ~40–60% |
| 10 s (`/catalog`) | ~3 s | ~70% |
| 60 s (a realistic catalogue) | ~3 s | **~95%** |

`/catalog` and `/feed` both land within a few points of that ceiling. `max-age=5` exists so revalidation and expiry are visible inside a 90-second demo, not because anything real is declared that way. Raise the numbers in [`CoreEndpoints.cs`](Stampede.Http.Sample.Api/Endpoints/CoreEndpoints.cs), rebuild the `api` service, and every line on the by-endpoint panel moves up.

**The per-client normalisation hides the best result.** The dashboard divides the two Stampede.Http clients' load by two to compare like with like. But because they share one Redis cache, they also share the refresh work — so in aggregate:

| Endpoint | `client-a` + `client-b` | one `client-baseline` |
|---|---:|---:|
| `/catalog` | 0.305 req/s | 0.366 req/s |
| `/feed` | 0.305 | 0.332 |
| `/flaky` | 0.875 | 0.542 |

On the cacheable endpoints, **two instances cost the origin less than one uncached instance**: the marginal origin cost of adding a replica is close to zero. On `/flaky` the opposite holds — two instances cost roughly twice as much, because uncacheable failures scale with replica count. Both facts follow from the same design and neither is visible in the headline number.

**And the panel misses the two cases that matter most.** It measures steady-state polling. It does not measure the stampede — 20 concurrent callers collapsing into 1 origin call is a 95% saving on that burst — nor latency:

```
✓ { mode:stampede }...: avg=12.57ms  med=752µs  p(95)=1.37ms
```

against an origin that takes 300 ms to 2 s per call. To see the saving on healthy traffic only:

```promql
100 * (1 - (sum(rate(sample_api_origin_requests_total{client=~"client-a|client-b",endpoint=~"/catalog|/feed"}[5m])) / 2)
         / sum(rate(sample_api_origin_requests_total{client="client-baseline",endpoint=~"/catalog|/feed"}[5m])))
```

---

## What demonstrates what

| Endpoint | Headers it sets | What it shows |
|---|---|---|
| `/catalog` | `max-age=10, stale-if-error=60` + `ETag` | Fresh hits → conditional revalidation → `POST` invalidation (RFC 9111 §4.4), shared through Redis |
| `/feed` | `max-age=5, stale-while-revalidate=30` | Never blocks after the first fetch; refreshes in the background (RFC 5861 §3) |
| `/flaky` | `max-age=5, stale-if-error=120` | 503s for the first 20 s of every minute. Polly retries the blips; stale-if-error covers the rest (RFC 5861 §4) |
| `/slow` | `max-age=30` | 2 s of origin latency — the stampede showcase |
| `/ledger` | `max-age=10, must-revalidate` + `ETag` | Once stale it may **not** be served without checking the origin — contrast with `/flaky` |
| `/docs/{id}` | `max-age=5` + `Last-Modified` | `If-Modified-Since` revalidation, and `RevalidationGraceSeconds` keeping the entry alive to make it possible |
| `/greetings` | `max-age=30` + `Vary: Accept-Language` | One URL, one cache entry per language (RFC 9111 §4.1) |
| `/tenants/data` | `max-age=20` + `Vary: X-Tenant-Id` | Multi-tenancy: `Vary` on the server, `CoalesceKeyHeaders` on the client |
| `/assets/{id}` | `max-age=31536000, immutable` | Fresh immutable entries skip revalidation even when asked (RFC 8246) |
| `/bulk` | `max-age=300`, ~1.8 MB body | Perfectly cacheable and still declined: `MaxBodySizeBytes` |
| `/search` | `max-age=60` | `NormalizeQueryParameters` folding reordered query strings onto one entry |
| `/stats` | `no-store` | Live origin counters — the source of truth for every assertion here |

---

## Drive it yourself

### By hand

[`samples.http`](samples.http) is a request collection for the VS Code REST Client, Visual Studio, or the JetBrains HTTP Client. It hits the Stampede.Http client and the control client side by side, with `/stats` calls in between so you can watch the origin counters move — or fail to.

### Under load

```bash
docker compose --profile load up k6
```

[`load/stampede.js`](load/stampede.js) runs two identical arrival patterns — 40 VUs each, ramp / hold / drain — one against `client-a`, one against `client-baseline`, and prints the origin's counters at the end. The stampede then comes from real concurrent inbound HTTP rather than a scripted `Task.WhenAll`, which is why the client is a real ASP.NET Core service rather than a console loop.

To let k6 be the only traffic, set `Sample__Workload__Enabled=false` on the clients first.

### The outage drill

```bash
docker compose stop api
```

The Stampede.Http clients keep answering **200 OK** for `/catalog` and `/flaky` out of the stale-if-error window. `client-baseline` starts returning connection errors immediately. Then:

```bash
docker compose start api
```

and watch them return to fresh responses with no intervention.

---

## Observability

### Metrics

Every `stampede_http.*` instrument (the same ones in the [main README](../README.md#metrics)) is exported on each client's ordinary application port at `/metrics`, scraped every 5 seconds. The origin exports `sample_api.origin.requests`, tagged by endpoint, client and status.

The dashboard is grouped into three sections: **Origin load** (the comparison), **Caching** (hits, misses, stale serving, revalidations, invalidations) and **Coalescing** (deduplication rate, in-flight calls, timeouts) — most panels broken down per client.

> **Naming caveat:** Prometheus 3.x negotiates UTF-8 metric names with targets that support it, which the OTel exporter does. `prometheus.yml` sets `metric_name_escaping_scheme: underscores` globally so names stay in the classic form (`stampede_http_cache_hits_requests_total`) that the dashboard queries and this README use.

### Traces

Both the clients and the origin export OTLP traces to Jaeger. Search for the `sample-client` service and open a request that arrived during a burst: the coalesced call shows up as **one** outgoing HTTP span serving many inbound ones. That is the thing a counter cannot show you.

### Runtime reconfiguration

[`config/client.json`](config/client.json) is bind-mounted into every client and read with `reloadOnChange`. Edit `Stampede:Cache:DefaultTtl` while the stack is running, then:

```bash
curl http://localhost:5081/api/config
```

The new value is live — no restart, no redeploy. That is `IOptionsMonitor` working through Stampede.Http's named options, keyed by the `HttpClient` name. Structural options (`MaxCacheSize`, `NormalizeQueryParameters`, `RevalidationGraceSeconds`) are read once at registration and deliberately do not move.

> The compose file sets `DOTNET_USE_POLLING_FILE_WATCHER=true`: inotify events do not cross a Docker bind mount on Windows or macOS, so the file watcher would otherwise never fire.

### Other introspection endpoints

| Endpoint | What it returns |
|---|---|
| `GET /api/config` | The options actually in effect on this instance right now |
| `GET /api/counters` | This process's `stampede_http.*` instrument totals, as JSON |
| `GET /api/origin-stats` | The origin's counters, straight from the origin |
| `GET /metrics` | The same instruments in Prometheus exposition format |

---

## Without Docker

```bash
docker run -d -p 6379:6379 redis:7-alpine     # or set Sample:Pipeline:UseRedis=false

dotnet run --project Stampede.Http.Sample.Api        # http://localhost:5080
dotnet run --project Stampede.Http.Sample.Client     # http://localhost:5081
```

`launchSettings.json` also carries a **client (baseline, no Stampede.Http)** profile on port 5082 so you can run the comparison locally. Prometheus, Grafana and Jaeger aren't running outside compose; the clients still serve `/metrics`, and traces are simply not exported when `OTEL_EXPORTER_OTLP_ENDPOINT` is unset.

---

## Automated verification

```bash
./scripts/smoke-test.sh          # add KEEP_STACK=1 to leave the stack up afterwards
```

Brings the whole stack up and asserts the behaviour this README claims, using the origin's counters as the source of truth: the feature tour reports 12/12, a 20-caller burst costs at most one origin call, the same burst against the control client costs twenty, `Vary` keeps one entry per representation, and the instruments really are exported.

This runs in CI on every push, and the sample projects are part of `Stampede.Http.slnx`, so neither the code nor the deployment can rot silently.

---

## What this sample does not claim

- **Coalescing is per process.** The Redis cache is shared, but if both replicas miss the same key at the same instant, each makes its own origin call — 2, not 1, and still not 20. Cross-instance deduplication would need a distributed lock, which is out of scope for an HTTP client library. The dashboard makes this visible: cache-hit panels for `client-a` and `client-b` rise together; coalescing panels move independently.
- **The headline percentage is a property of the workload, not of the library.** It is bounded by `1 − interval / max-age`, and this sample deliberately runs 5–10 second TTLs against a 2-second poll so that expiry is visible in a short demo. Read [How to read that percentage](#how-to-read-that-percentage) before quoting the number anywhere, and model your own TTLs first.
- **Failures are not cacheable.** `stale-if-error` rescues the caller *after* the origin has failed; the request still goes out, and Polly retries it. Most of the origin traffic Stampede.Http cannot avoid in this sample is `/flaky` returning 503 — and unlike cacheable traffic, that cost scales with the number of replicas rather than being shared.
- **The origin is a toy.** It fabricates latency with `Task.Delay` and keeps its state in memory. Its job is to emit realistic headers, not to be a realistic service.
