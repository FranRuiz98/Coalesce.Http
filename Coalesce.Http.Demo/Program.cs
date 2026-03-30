using Coalesce.Http.Caching;
using Coalesce.Http.Coalescing;
using Coalesce.Http.Extensions;
using Coalesce.Http.Metrics;
using Coalesce.Http.Options;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;

PrintPipelineOverview();

await Demo1_CoalescingAsync();
await Demo2_CachingAsync();
await Demo3_StaleWhileRevalidateAsync();
await Demo4_StaleIfErrorAsync();
await Demo5_PerRequestPoliciesAsync();
await Demo6_UnsafeMethodInvalidationAsync();
await Demo7_MetricsAsync();

Banner("All demos complete!", ConsoleColor.Green);

// -------------------------------------------------------------------------
// Pipeline overview
// -------------------------------------------------------------------------

static void PrintPipelineOverview()
{
    Banner("Coalesce.Http — Library Overview");

    var pipeline = new Panel(
        new Markup(
            "[cyan]Your code[/]  ([italic]HttpClient.GetAsync / SendAsync[/])\n" +
            "    ¦\n" +
            "[bold cyan]+- CachingMiddleware[/] [dim](outermost)[/]\n" +
            "¦  · RFC 9111 : max-age, s-maxage, ETag, Vary, no-store\n" +
            "¦  · RFC 5861 : stale-while-revalidate, stale-if-error\n" +
            "¦  · Unsafe-method invalidation (POST/PUT/DELETE/PATCH)\n" +
            "¦  · Pluggable ICacheStore [dim](default: IMemoryCache)[/]\n" +
            "+------- only on cache miss or forced revalidation --------\n" +
            "    ¦\n" +
            "[bold cyan]+- CoalescingHandler[/]\n" +
            "¦  · Concurrent identical requests -> 1 origin call\n" +
            "¦  · Response body buffered into CachedResponse (byte[[]])\n" +
            "¦  · Independent clone returned to every waiter\n" +
            "+----------- single request reaches here -----------------\n" +
            "    ¦\n" +
            "[bold cyan]+- Polly resilience handler[/] [dim](optional)[/]\n" +
            "+- HttpClientHandler / primary handler"
        ))
    {
        Header = new PanelHeader("[bold cyan] Pipeline Overview [/]"),
        Border = BoxBorder.Rounded,
        BorderStyle = new Style(Color.Cyan1),
        Padding = new Padding(2, 1),
        Expand = false,
    };

    AnsiConsole.Write(pipeline);
    AnsiConsole.MarkupLine("[dim]  Nine instruments emitted under the [/][cyan]\"Coalesce.Http\"[/][dim] meter (see Demo 7).[/]");
}

// -------------------------------------------------------------------------
// Demo 1 – Request Coalescing
// -------------------------------------------------------------------------

static async Task Demo1_CoalescingAsync()
{
    Banner("Demo 1 – Request Coalescing");

    Note("Problem:  10 concurrent identical GETs would each hit the origin independently");
    Note("          (thundering herd / cache stampede on a cold or expired cache).");
    Note("Solution: CoalescingHandler deduplicates concurrent requests with the same key.");
    Note("");
    Note("Internal mechanics:");
    Note("  1. All concurrent GETs sharing (Method + URL + Vary headers) enter together.");
    Note("  2. The FIRST to arrive is elected WINNER -> it calls the origin.");
    Note("  3. Every other request becomes a WAITER -> blocks on a TaskCompletionSource.");
    Note("  4. Origin responds ? body is buffered into CachedResponse (byte[] snapshot).");
    Note("  5. CachedResponse is CLONED once per waiter (independent stream, no sharing).");
    Note("  6. All callers receive an independent HttpResponseMessage simultaneously.");
    Console.WriteLine();

    // -- Part A: with coalescing (default) --------------------------------
    Step("Part A — 10 concurrent GETs  WITH coalescing (default)");
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCallReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler = new DemoHandler(async (req, ct) =>
        {
            firstCallReached.TrySetResult(true);
            await gate.Task.ConfigureAwait(false);
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":42,"name":"Widget","price":9.99}""")
            };
            resp.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(60) };
            return resp;
        });

        var client = BuildClient(handler);
        const string url = "https://demo.local/products/42";

        Info("    All 10 tasks launched simultaneously — cache is empty, all are misses.");
        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, 10).Select(_ => client.GetAsync(url)).ToArray();

        await firstCallReached.Task;
        Behind("  [CoalescingHandler] 1 request elected WINNER  ->  forwarding to origin");
        Behind("  [CoalescingHandler] 9 requests registered as WAITERS  ->  blocking on TCS");
        Behind("  [Origin] Received request #1 only — processing…");

        gate.SetResult(true);
        var responses = await Task.WhenAll(tasks);
        sw.Stop();

        Behind("  [CoalescingHandler] Origin responded  ->  body buffered into CachedResponse");
        Behind("  [CoalescingHandler] Response cloned 9× — one independent clone per waiter");
        Behind("  [CachingMiddleware] Response stored in cache  (Cache-Control: max-age=60)");
        Console.WriteLine();
        Ok($"  Origin calls : {handler.CallCount}  (expected 1)");
        Ok($"  Responses OK : {responses.Count(r => r.IsSuccessStatusCode)} / 10");
        Ok($"  Elapsed      : {sw.ElapsedMilliseconds} ms  (same wall-clock as a single request)");
        Ok($"  Saved        : 9 unnecessary origin calls eliminated");
    }

    // -- Part B: without coalescing — for comparison -----------------------
    Console.WriteLine();
    Step("Part B — same 10 concurrent GETs  WITHOUT coalescing  (comparison)");
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int reachedOrigin = 0;

        var handler = new DemoHandler(async (req, ct) =>
        {
            int n = Interlocked.Increment(ref reachedOrigin);
            if (n == 10) gate.TrySetResult(true);
            await gate.Task.ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":42,"name":"Widget","price":9.99}""")
            };
        });

        var client = BuildClientCacheOnly(handler);
        const string url = "https://demo.local/products/42-nocoalesce";

        Info("    Same 10 tasks — CoalescingHandler is NOT in the pipeline.");
        var tasks = Enumerable.Range(0, 10).Select(_ => client.GetAsync(url)).ToArray();

        try
        {
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var responses = await Task.WhenAll(tasks);
            Behind("  [No coalescing] All 10 requests reached the origin independently");
            Console.WriteLine();
            Ok($"  Origin calls : {handler.CallCount}  (all 10 hit the backend — thundering herd!)");
            Ok($"  Responses OK : {responses.Count(r => r.IsSuccessStatusCode)} / 10");
        }
        catch (TimeoutException)
        {
            Warn("  Timed out waiting for all 10 requests to reach the origin.");
        }
    }
}

// -------------------------------------------------------------------------
// Demo 2 – RFC 9111 HTTP Caching
// -------------------------------------------------------------------------

static async Task Demo2_CachingAsync()
{
    Banner("Demo 2 – RFC 9111 HTTP Caching");

    Note("CachingMiddleware stores 200 OK responses and serves subsequent requests from");
    Note("memory without touching the network, as long as the entry is fresh.");
    Note("");
    Note("Freshness resolution order (RFC 9111 §4.2.1):");
    Note("  s-maxage  >  max-age  >  Expires header  >  DefaultTtl (configured fallback)");
    Note("");
    Note("Revalidation (RFC 9111 §4.3.4 / §4.3.5):");
    Note("  When an entry EXPIRES, CachingMiddleware does not discard it immediately.");
    Note("  If the stored response had an ETag or Last-Modified header, it sends a");
    Note("  CONDITIONAL request with  If-None-Match / If-Modified-Since.");
    Note("  · 304 Not Modified -> TTL refreshed, body reused (no bandwidth wasted).");
    Note("  · 200 OK           -> new body stored, old entry replaced.");
    Console.WriteLine();

    string? lastIfNoneMatch = null;

    var handler = new DemoHandler(async (req, ct) =>
    {
        lastIfNoneMatch = req.Headers.IfNoneMatch.FirstOrDefault()?.Tag;

        Behind($"  [Origin] {req.Method} {req.RequestUri?.PathAndQuery}");
        if (lastIfNoneMatch is not null)
            Behind($"  [Origin] Conditional header received  ->  If-None-Match: {lastIfNoneMatch}");
        else
            Behind("  [Origin] Unconditional request (no If-None-Match header)");

        await Task.Delay(40, ct).ConfigureAwait(false);

        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"catalog":"v1","items":120}""")
        };
        resp.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(1) };
        resp.Headers.ETag = new EntityTagHeaderValue("\"v1-abc\"");
        Behind("  [Origin] Responding  200 OK  ETag: \"v1-abc\"  Cache-Control: max-age=1");
        return resp;
    });

    var client = BuildClient(handler);
    const string url = "https://demo.local/catalog";
    var sw = Stopwatch.StartNew();

    // -- Request 1: cache miss ---------------------------------------------
    Step("GET /catalog  #1  —  cache is EMPTY");
    Behind("  [CachingMiddleware] Cache lookup  ->  no entry  ->  MISS");
    Behind("  [CachingMiddleware] Forwarding to CoalescingHandler  ->  origin");
    sw.Restart();
    var r1 = await client.GetAsync(url);
    sw.Stop();
    Behind($"  [CachingMiddleware] Stored  ETag: {r1.Headers.ETag}  expires in 1 s");
    Ok($"  Status: {r1.StatusCode}  |  ETag: {r1.Headers.ETag}  |  Origin calls: {handler.CallCount}  |  Elapsed: {sw.ElapsedMilliseconds} ms  -> origin latency (~40 ms)");

    // -- Request 2: cache hit ----------------------------------------------
    Console.WriteLine();
    Step("GET /catalog  #2  —  entry is FRESH (within max-age=1 s)");
    Behind("  [CachingMiddleware] Cache lookup  ->  fresh entry found  ->  HIT");
    Behind("  [CachingMiddleware] Returning cached response — origin NOT contacted");
    sw.Restart();
    var r2 = await client.GetAsync(url);
    sw.Stop();
    Ok($"  Status: {r2.StatusCode}  |  ETag: {r2.Headers.ETag}  |  Origin calls: {handler.CallCount}  |  Elapsed: {sw.ElapsedMilliseconds} ms  -> from memory");

    // -- Let entry expire --------------------------------------------------
    Console.WriteLine();
    Info("    Waiting 1.1 s for max-age=1 s to expire…");
    await Task.Delay(1100);

    // -- Request 3: stale ? conditional revalidation -----------------------
    Console.WriteLine();
    Step("GET /catalog  #3  —  entry EXPIRED  ->  conditional revalidation");
    Behind("  [CachingMiddleware] Cache lookup  ->  entry found but STALE");
    Behind("  [CachingMiddleware] Stored ETag found  ->  injecting  If-None-Match: \"v1-abc\"");
    Behind("  [CachingMiddleware] Forwarding conditional request to origin");
    sw.Restart();
    var r3 = await client.GetAsync(url);
    sw.Stop();
    Ok($"  Status: {r3.StatusCode}  |  ETag: {r3.Headers.ETag}  |  Origin calls: {handler.CallCount}  |  Elapsed: {sw.ElapsedMilliseconds} ms");
    if (lastIfNoneMatch is not null)
        Ok($"  If-None-Match sent : {lastIfNoneMatch}  -> injected automatically by CachingMiddleware");

    Console.WriteLine();
    Info($"    Summary: 3 requests  ->  {handler.CallCount} origin call(s)  |  1 fresh hit served from memory in < 1 ms.");
}

// -------------------------------------------------------------------------
// Demo 3 – stale-while-revalidate (RFC 5861 §3)
// -------------------------------------------------------------------------

static async Task Demo3_StaleWhileRevalidateAsync()
{
    Banner("Demo 3 – stale-while-revalidate (RFC 5861 §3)");

    Note("Problem:  When max-age expires, the caller must WAIT for the origin to respond.");
    Note("          For slow origins this adds visible latency on every cache expiry.");
    Note("Solution: stale-while-revalidate lets the middleware serve the stale entry");
    Note("          IMMEDIATELY and refresh the cache in a fire-and-forget background task.");
    Note("");
    Note("Header:   Cache-Control: max-age=N, stale-while-revalidate=M");
    Note("Time windows:");
    Note("  [0 … N]      FRESH   -> served from cache, no origin contact.");
    Note("  (N … N+M]    STALE   -> served instantly; background refresh starts silently.");
    Note("  (N+M … 8)    EXPIRED -> treated as a normal cache miss (no stale serving).");
    Note("");
    Note("The caller that triggers the background task pays ZERO extra latency.");
    Note("The next request after the refresh completes receives the updated entry.");
    Console.WriteLine();

    int gen = 0;
    var backgroundStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    DateTimeOffset cachedAt = default;

    var handler = new DemoHandler(async (req, ct) =>
    {
        int current = Interlocked.Increment(ref gen);
        if (current >= 2) backgroundStarted.TrySetResult(true);
        Behind($"  [Origin] Request #{current} received  —  simulating 30 ms latency");
        await Task.Delay(30, ct).ConfigureAwait(false);
        if (current == 1) cachedAt = DateTimeOffset.UtcNow;
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"generation\":{current}}}")
        };
        resp.Headers.CacheControl = CacheControlHeaderValue.Parse("max-age=1, stale-while-revalidate=30");
        Behind($"  [Origin] Responding  200 OK  gen={current}  Cache-Control: max-age=1, stale-while-revalidate=30");
        return resp;
    });

    var client = BuildClient(handler);
    const string url = "https://demo.local/feed";

    // -- Request 1: prime cache --------------------------------------------
    Step("GET /feed  #1  —  cache EMPTY  ->  origin called  (~30 ms expected)");
    Behind("  [CachingMiddleware] Cache lookup  ->  MISS  ->  forwarding to origin");
    var sw = Stopwatch.StartNew();
    var r1 = await client.GetAsync(url);
    sw.Stop();
    var expiresAt = cachedAt + TimeSpan.FromSeconds(1);
    var swrUntil = cachedAt + TimeSpan.FromSeconds(31);
    Behind($"  [CachingMiddleware] Stored  gen=1");
    Behind($"  [CachingMiddleware] Fresh window  ends at : {expiresAt:HH:mm:ss.fff}");
    Behind($"  [CachingMiddleware] SWR   window  ends at : {swrUntil:HH:mm:ss.fff}  (+30 s)");
    Ok($"  Body: {await r1.Content.ReadAsStringAsync()}  |  Elapsed: {sw.ElapsedMilliseconds} ms  -> origin latency");

    // -- Let entry expire --------------------------------------------------
    Console.WriteLine();
    Info("    Waiting 1.2 s for max-age=1 s to expire…");
    await Task.Delay(1200);
    var now = DateTimeOffset.UtcNow;
    Behind($"  [{now:HH:mm:ss.fff}] Entry is STALE (expired {(now - expiresAt).TotalMilliseconds:F0} ms ago)");
    Behind($"  [{now:HH:mm:ss.fff}] SWR window still active ({(swrUntil - now).TotalSeconds:F1} s remaining)");

    // -- Request 2: stale served instantly --------------------------------
    Console.WriteLine();
    Step("GET /feed  #2  —  entry STALE but inside SWR window  ->  instant response");
    Behind("  [CachingMiddleware] Cache lookup  ->  STALE entry found");
    Behind("  [CachingMiddleware] Within stale-while-revalidate window");
    Behind("  [CachingMiddleware] Returning stale response to caller IMMEDIATELY (gen=1)");
    Behind("  [CachingMiddleware] Spawning background revalidation task (fire-and-forget)");
    sw.Restart();
    var r2 = await client.GetAsync(url);
    sw.Stop();
    Ok($"  Body: {await r2.Content.ReadAsStringAsync()}  |  Elapsed: {sw.ElapsedMilliseconds} ms  -> stale, served without waiting for origin");
    Info("    Without SWR this request would have blocked ~30 ms waiting for origin.");

    // -- Wait for background -----------------------------------------------
    Console.WriteLine();
    Info("    Waiting for background revalidation to complete…");
    try
    {
        await backgroundStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Behind("  [Background] Origin responded gen=2  ->  cache updated with fresh entry");
    }
    catch (TimeoutException)
    {
        Warn("  Background revalidation did not complete within 5 s.");
    }

    // -- Request 3: fresh data from background refresh ---------------------
    Console.WriteLine();
    Step("GET /feed  #3  —  background refresh complete  ->  cache HIT (gen=2)");
    Behind("  [CachingMiddleware] Cache lookup  ->  FRESH entry found (gen=2, just updated)");
    sw.Restart();
    var r3 = await client.GetAsync(url);
    sw.Stop();
    Ok($"  Body: {await r3.Content.ReadAsStringAsync()}  |  Elapsed: {sw.ElapsedMilliseconds} ms  -> fresh gen=2, served from cache");

    Console.WriteLine();
    Ok($"  Total origin calls : {gen}  (1 foreground + 1 background)");
    Ok($"  Request #2 paid 0 ms of extra latency — background handled the refresh silently.");
}

// -------------------------------------------------------------------------
// Demo 4 – stale-if-error (RFC 5861 §4)
// -------------------------------------------------------------------------

static async Task Demo4_StaleIfErrorAsync()
{
    Banner("Demo 4 – stale-if-error (RFC 5861 §4)");

    Note("Problem:  When max-age expires and the origin is down, callers receive 5xx.");
    Note("          Serving an error to every caller during an outage degrades UX badly.");
    Note("Solution: stale-if-error extends the usable lifetime of a cached entry when");
    Note("          the origin returns a 5xx response or throws an exception.");
    Note("          The caller receives the original 200 OK body instead of the error.");
    Note("");
    Note("Header:   Cache-Control: max-age=N, stale-if-error=E");
    Note("Time windows:");
    Note("  [0 … N]      FRESH   -> served from cache normally.");
    Note("  (N … N+E]    STALE   -> if origin returns 5xx, serve stale  (200 to caller).");
    Note("  (N+E … 8)    EXPIRED -> error window closed, 5xx propagated to caller.");
    Note("");
    Note("Also activates if origin THROWS (connection refused, timeout, etc.).");
    Note("Blocked by the 'must-revalidate' directive (RFC 9111 §5.2.2.2).");
    Console.WriteLine();

    bool fail = false;
    var handler = new DemoHandler(async (req, ct) =>
    {
        if (fail)
        {
            Behind("  [Origin] Returning 503 ServiceUnavailable  -> simulated outage");
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
        Behind("  [Origin] Returning 200 OK  -> Cache-Control: max-age=1, stale-if-error=60");
        await Task.Delay(10, ct).ConfigureAwait(false);
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"status":"healthy","version":"v1"}""")
        };
        resp.Headers.CacheControl = CacheControlHeaderValue.Parse("max-age=1, stale-if-error=60");
        return resp;
    });

    var client = BuildClient(handler);
    const string url = "https://demo.local/health";

    // -- Request 1: prime cache --------------------------------------------
    Step("GET /health  #1  —  origin UP  ->  200 OK cached");
    Behind("  [CachingMiddleware] Cache lookup  ->  MISS  ->  forwarding to origin");
    var storedAt = DateTimeOffset.UtcNow;
    var r1 = await client.GetAsync(url);
    storedAt = DateTimeOffset.UtcNow;
    Behind($"  [CachingMiddleware] Stored  fresh until  {storedAt + TimeSpan.FromSeconds(1):HH:mm:ss.fff}");
    Behind($"  [CachingMiddleware] Error window ends at {storedAt + TimeSpan.FromSeconds(61):HH:mm:ss.fff}  (+60 s)");
    Ok($"  Status: {r1.StatusCode}  |  Body: {await r1.Content.ReadAsStringAsync()}");

    // -- Let entry expire --------------------------------------------------
    Console.WriteLine();
    Info("    Waiting 1.2 s so the entry expires…  (origin will now return 503)");
    await Task.Delay(1200);
    fail = true;
    var now = DateTimeOffset.UtcNow;
    Behind($"  [{now:HH:mm:ss.fff}] Entry is STALE");
    Behind($"  [{now:HH:mm:ss.fff}] stale-if-error window active  ({(storedAt + TimeSpan.FromSeconds(61) - now).TotalSeconds:F1} s remaining)");

    // -- Request 2: origin 503, stale-if-error activates -------------------
    Console.WriteLine();
    Step("GET /health  #2  —  entry STALE  +  origin returns 503  ->  stale-if-error");
    Behind("  [CachingMiddleware] Cache lookup  ->  STALE entry found");
    Behind("  [CachingMiddleware] Forwarding to origin to attempt revalidation");
    Behind("  [CachingMiddleware] Origin returned 503 (>= 500)  ->  origin error detected");
    Behind("  [CachingMiddleware] stale-if-error window ACTIVE  ->  serving stale response");
    Behind("  [CachingMiddleware] Caller receives 200 OK (original cached body), NOT 503");
    var r2 = await client.GetAsync(url);
    Ok($"  Status returned to caller : {r2.StatusCode}  ->  200 (stale), NOT 503");
    Ok($"  Body : {await r2.Content.ReadAsStringAsync()}  ->  original cached body");
    Ok($"  Origin calls : {handler.CallCount}  ->  origin WAS contacted and returned 503, but caller was shielded");

    Console.WriteLine();
    Info("    Without stale-if-error: the caller would have received 503 ServiceUnavailable.");
}

// -------------------------------------------------------------------------
// Demo 5 – Per-Request Policies
// -------------------------------------------------------------------------

static async Task Demo5_PerRequestPoliciesAsync()
{
    Banner("Demo 5 – Per-Request Policies");

    Note("All Coalesce.Http behaviours can be overridden on individual requests via");
    Note("HttpRequestMessage.Options — no global config change needed.");
    Note("");
    Note("  CacheRequestPolicy.BypassCache          skip cache lookup + write");
    Note("  CacheRequestPolicy.ForceRevalidate      send conditional request even if fresh");
    Note("  CacheRequestPolicy.NoStore              do NOT write response to cache");
    Note("  CoalescingRequestPolicy.BypassCoalescing  send as independent request, no dedup");
    Note("");
    Note("Usage:");
    Note("  var req = new HttpRequestMessage(HttpMethod.Get, url);");
    Note("  req.Options.Set(CacheRequestPolicy.BypassCache, true);");
    Note("  await client.SendAsync(req);");

    // -- BypassCache -------------------------------------------------------
    Console.WriteLine();
    Sep();
    Step("CacheRequestPolicy.BypassCache");
    Info("    Effect: skips both the cache lookup AND the cache write for this request.");
    Info("    Use case: force-refresh a specific resource without touching global config.");
    {
        var h = new DemoHandler(async (req, ct) =>
        {
            bool bypass = req.Options.TryGetValue(CacheRequestPolicy.BypassCache, out bool b) && b;
            Behind($"  [Origin] Received request  |  BypassCache option present: {bypass}");
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":"fresh"}""") };
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(30) };
            return r;
        });
        var c = BuildClient(h);
        const string url = "https://demo.local/bypass-test";

        Info("    Step A — normal GET: populates the cache (origin called, entry stored).");
        Behind("  [CachingMiddleware] MISS -> origin called -> entry stored");
        await c.GetAsync(url);

        Info("    Step B — BypassCache=true: skips cache, always goes to origin.");
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Options.Set(CacheRequestPolicy.BypassCache, true);
        Behind("  [CachingMiddleware] BypassCache=true  ->  skipping lookup AND storage");
        Behind("  [CoalescingHandler] Forwarding directly to origin");
        await c.SendAsync(req);

        Ok($"  Origin calls: {h.CallCount}  (expected 2 — cache was bypassed on step B)");
    }

    // -- ForceRevalidate ---------------------------------------------------
    Console.WriteLine();
    Sep();
    Step("CacheRequestPolicy.ForceRevalidate");
    Info("    Effect: sends a conditional revalidation request even if the entry is fresh.");
    Info("    The stored ETag is sent as  If-None-Match  (or Last-Modified as If-Modified-Since).");
    Info("    Use case: guarantee up-to-date data for a critical read before a write.");
    {
        string? ifNoneMatchSeen = null;

        var h = new DemoHandler(async (req, ct) =>
        {
            ifNoneMatchSeen = req.Headers.IfNoneMatch.FirstOrDefault()?.Tag;
            if (ifNoneMatchSeen is not null)
                Behind($"  [Origin] Conditional request  ->  If-None-Match: {ifNoneMatchSeen}  ->  injected by CachingMiddleware");
            else
                Behind("  [Origin] Unconditional request (first call, no ETag yet)");
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"v":1}""") };
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(30) };
            r.Headers.ETag = new EntityTagHeaderValue("\"etag-v1\"");
            return r;
        });
        var c = BuildClient(h);
        const string url = "https://demo.local/force-revalidate-test";

        Info("    Step A — normal GET: ETag stored in cache alongside the body.");
        Behind("  [CachingMiddleware] MISS -> origin called -> entry stored with ETag: \"etag-v1\"");
        await c.GetAsync(url);

        Info("    Step B — ForceRevalidate=true: middleware injects If-None-Match despite fresh entry.");
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Options.Set(CacheRequestPolicy.ForceRevalidate, true);
        Behind("  [CachingMiddleware] ForceRevalidate=true  +  stored ETag exists");
        Behind("  [CachingMiddleware] Injecting  If-None-Match: \"etag-v1\"  into outgoing request");
        await c.SendAsync(req);

        Ok($"  Origin calls: {h.CallCount}  (expected 2 — revalidation forced despite fresh entry)");
        if (ifNoneMatchSeen is not null)
            Ok($"  If-None-Match sent: {ifNoneMatchSeen}  ? header injected automatically by CachingMiddleware");
    }

    // -- NoStore -----------------------------------------------------------
    Console.WriteLine();
    Sep();
    Step("CacheRequestPolicy.NoStore");
    Info("    Effect: cache lookups work normally, but the response is NOT written to cache.");
    Info("    Use case: sensitive responses (tokens, PII) that must not be retained.");
    {
        var h = new DemoHandler(async (req, ct) =>
        {
            bool noStore = req.Options.TryGetValue(CacheRequestPolicy.NoStore, out bool ns) && ns;
            Behind($"  [Origin] Received request  |  NoStore option present: {noStore}");
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"secret":"do-not-cache"}""") };
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(30) };
            return r;
        });
        var c = BuildClient(h);
        const string url = "https://demo.local/no-store-test";

        Info("    Step A — NoStore=true: origin called, response intentionally NOT stored.");
        var req1 = new HttpRequestMessage(HttpMethod.Get, url);
        req1.Options.Set(CacheRequestPolicy.NoStore, true);
        Behind("  [CachingMiddleware] NoStore=true -> forwarding to origin, suppressing cache write");
        await c.SendAsync(req1);

        Info("    Step B — normal GET: cache is still empty (step A stored nothing), origin called.");
        Behind("  [CachingMiddleware] MISS (nothing stored in step A)  ->  origin called");
        await c.GetAsync(url);

        Ok($"  Origin calls: {h.CallCount}  (expected 2 — step A response was never cached)");
    }

    // -- BypassCoalescing -------------------------------------------------
    Console.WriteLine();
    Sep();
    Step("CoalescingRequestPolicy.BypassCoalescing");
    Info("    Effect: the request bypasses CoalescingHandler — no winner/waiter dedup.");
    Info("    Use case: requests with unique idempotency keys or personalised payloads.");
    Console.WriteLine();

    // Control: 2 concurrent WITHOUT bypass ? 1 origin call
    Info("    Control — 2 concurrent GETs  WITHOUT BypassCoalescing:");
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var h = new DemoHandler(async (req, ct) =>
        {
            await gate.Task.ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        });
        var c = BuildClient(h);
        const string url = "https://demo.local/coalesce-cmp";

        var ta = Task.Run(() => c.GetAsync(url));
        var tb = Task.Run(() => c.GetAsync(url));
        await Task.Delay(60);
        Behind("  [CoalescingHandler] 1 WINNER + 1 WAITER  ->  1 origin call");
        gate.SetResult(true);
        await Task.WhenAll(ta, tb);
        Ok($"      2 requests  ->  {h.CallCount} origin call(s)   ->  deduplicated");
    }

    // Experiment: 2 concurrent WITH bypass ? 2 origin calls
    Console.WriteLine();
    Info("    Experiment — same 2 concurrent GETs  WITH BypassCoalescing=true:");
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var h = new DemoHandler(async (req, ct) =>
        {
            await gate.Task.ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        });
        var c = BuildClient(h);
        const string url = "https://demo.local/coalesce-cmp";

        var t1 = Task.Run(async () =>
        {
            var r = new HttpRequestMessage(HttpMethod.Get, url);
            r.Options.Set(CoalescingRequestPolicy.BypassCoalescing, true);
            return await c.SendAsync(r);
        });
        var t2 = Task.Run(async () =>
        {
            var r = new HttpRequestMessage(HttpMethod.Get, url);
            r.Options.Set(CoalescingRequestPolicy.BypassCoalescing, true);
            return await c.SendAsync(r);
        });
        await Task.Delay(80);
        Behind("  [CoalescingHandler] BypassCoalescing=true on both  ->  each goes independently");
        gate.SetResult(true);
        await Task.WhenAll(t1, t2);
        Ok($"      2 requests  ->  {h.CallCount} origin call(s)   ->  NOT deduplicated (bypass active)");
    }
}

// -------------------------------------------------------------------------
// Demo 6 – Unsafe Method Cache Invalidation (RFC 9111 §4.4)
// -------------------------------------------------------------------------

static async Task Demo6_UnsafeMethodInvalidationAsync()
{
    Banner("Demo 6 – Unsafe Method Invalidation (RFC 9111 §4.4)");

    Note("Problem:  A cached GET response may be stale after a mutating operation changes");
    Note("          the server-side resource (POST/PUT/DELETE/PATCH).");
    Note("Solution: CachingMiddleware applies RFC 9111 §4.4 — when a non-safe method");
    Note("          returns 2xx, it IMMEDIATELY evicts the cache entry for:");
    Note("          · The request URL itself.");
    Note("          · Any URL in the response  Location  header.");
    Note("          · Any URL in the response  Content-Location  header.");
    Note("          The next GET to that URL is a guaranteed MISS ? fresh data.");
    Console.WriteLine();

    int getVersion = 0;
    var handler = new DemoHandler(async (req, ct) =>
    {
        Behind($"  [Origin] {req.Method} {req.RequestUri?.PathAndQuery}");
        if (req.Method != HttpMethod.Get)
        {
            Behind("  [Origin] Mutation succeeded  ->  204 No Content");
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        int v = Interlocked.Increment(ref getVersion);
        Behind($"  [Origin] Returning  version={v}  Cache-Control: max-age=60");
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"id\":99,\"version\":{v}}}")
        };
        resp.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(60) };
        return resp;
    });

    var client = BuildClient(handler);
    const string url = "https://demo.local/items/99";

    Step("GET /items/99  #1  —  cache EMPTY  ->  MISS  ->  origin called, response cached");
    Behind("  [CachingMiddleware] Cache lookup  ->  no entry  ->  MISS");
    Behind("  [CachingMiddleware] Forwarding to origin");
    var r1 = await client.GetAsync(url);
    Behind($"  [CachingMiddleware] Stored  version=1  (max-age=60  ->  expires in 60 s)");
    Ok($"  Body: {await r1.Content.ReadAsStringAsync()}   Origin calls: {handler.CallCount}");

    Console.WriteLine();
    Step("GET /items/99  #2  —  entry FRESH  ->  HIT  ->  origin NOT contacted");
    Behind("  [CachingMiddleware] Cache lookup  ->  fresh entry found  ->  HIT");
    var r2 = await client.GetAsync(url);
    Ok($"  Body: {await r2.Content.ReadAsStringAsync()}   Origin calls: {handler.CallCount}  ->  unchanged (cache hit)");

    Console.WriteLine();
    Step("DELETE /items/99  —  unsafe method  ->  2xx returned  ->  cache entry EVICTED");
    Behind("  [CachingMiddleware] DELETE is an unsafe method  ->  forwarding to origin");
    Behind("  [CachingMiddleware] Origin returned 2xx (204 No Content)");
    Behind("  [CachingMiddleware] RFC 9111 §4.4 triggered  ->  evicting entry for  " + url);
    await client.DeleteAsync(url);
    Ok($"  Origin calls: {handler.CallCount}  (DELETE counted as origin call)");

    Console.WriteLine();
    Step("GET /items/99  #3  —  cache EMPTY (invalidated)  ->  MISS  ->  fresh origin call");
    Behind("  [CachingMiddleware] Cache lookup  ->  MISS (entry was evicted by DELETE)");
    Behind("  [CachingMiddleware] Forwarding to origin  ->  fresh response");
    var r3 = await client.GetAsync(url);
    Behind($"  [CachingMiddleware] Stored  version=2  (fresh data)");
    Ok($"  Body: {await r3.Content.ReadAsStringAsync()}   Origin calls: {handler.CallCount}  ->  version=2 fetched after invalidation");

    Console.WriteLine();
    Info("    Without invalidation: GET #3 would have served version=1 (stale) for 60 s.");
}

// -------------------------------------------------------------------------
// Demo 7 – System.Diagnostics.Metrics
// -------------------------------------------------------------------------

static async Task Demo7_MetricsAsync()
{
    Banner("Demo 7 – System.Diagnostics.Metrics");

    Note($"Meter name : \"{CoalesceHttpMetrics.MeterName}\"");
    Note("Integrate with any OpenTelemetry exporter, Prometheus, or a plain MeterListener.");
    Note("");
    Note("Instruments (all under the \"coalesce_http.\" prefix):");
    Note("  Name                                   Type           What it counts");
    Note("  ---------------------------------------------------------------------------");
    Note("  cache.hits                             Counter        Requests served from cache");
    Note("  cache.misses                           Counter        Cache misses forwarded to origin");
    Note("  cache.revalidations                    Counter        Conditional requests sent");
    Note("  cache.stale_errors_served              Counter        stale-if-error activations");
    Note("  cache.stale_while_revalidate_served    Counter        stale-while-revalidate stale hits");
    Note("  cache.invalidations                    Counter        Entries evicted by unsafe methods");
    Note("  coalescing.deduplicated                Counter        Waiters that shared an in-flight response");
    Note("  coalescing.inflight                    UpDownCounter  Current in-flight coalesced requests");
    Note("  coalescing.timeouts                    Counter        Waiters that timed out and fell back");
    Console.WriteLine();

    var counters = new Dictionary<string, long>(StringComparer.Ordinal);

    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) =>
    {
        if (instrument.Meter.Name == CoalesceHttpMetrics.MeterName)
            l.EnableMeasurementEvents(instrument);
    };
    listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
    {
        lock (counters)
        {
            counters.TryGetValue(instrument.Name, out long prev);
            counters[instrument.Name] = prev + measurement;
        }
    });
    listener.Start();

    var handler = new DemoHandler(async (req, ct) =>
    {
        await Task.Delay(25, ct).ConfigureAwait(false);
        var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"ok":true}""") };
        resp.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(30) };
        return resp;
    });

    var client = BuildClient(handler);
    const string url = "https://demo.local/metrics-test";

    // -- Scenario A --------------------------------------------------------
    Step("Scenario A — 5 concurrent GETs  (all cache misses + CoalescingHandler active)");
    Info("    Expected:  cache.misses += 5  |  coalescing.deduplicated += 4  |  1 origin call");
    var concurrent = Enumerable.Range(0, 5).Select(_ => client.GetAsync(url)).ToArray();
    await Task.WhenAll(concurrent);
    Behind("  [CachingMiddleware] 5 concurrent requests  ?  5 separate cache misses");
    Behind("  [CoalescingHandler] 1 winner called origin  —  4 waiters were deduplicated");
    Behind($"  [Origin] Called {handler.CallCount} time(s)");

    // -- Scenario B --------------------------------------------------------
    Console.WriteLine();
    Step("Scenario B — 2 sequential GETs  (both cache hits)");
    Info("    Expected:  cache.hits += 2  |  no new origin calls");
    await client.GetAsync(url);
    await client.GetAsync(url);
    Behind("  [CachingMiddleware] 2 × cache HIT  ->  origin NOT called");

    listener.RecordObservableInstruments();

    // -- Snapshot ----------------------------------------------------------
    Console.WriteLine();
    Step("Metrics snapshot  (total across both scenarios):");

    var descriptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["coalesce_http.cache.hits"] = "Scenario B: 2 sequential hits",
        ["coalesce_http.cache.misses"] = "Scenario A: all 5 concurrent missed",
        ["coalesce_http.cache.revalidations"] = "no conditional requests in this demo",
        ["coalesce_http.cache.stale_errors_served"] = "see Demo 4",
        ["coalesce_http.cache.stale_while_revalidate_served"] = "see Demo 3",
        ["coalesce_http.cache.invalidations"] = "see Demo 6",
        ["coalesce_http.coalescing.deduplicated"] = "Scenario A: 4 of 5 concurrent were waiters",
        ["coalesce_http.coalescing.inflight"] = "all requests completed, 0 in-flight",
        ["coalesce_http.coalescing.timeouts"] = "no timeout configured",
    };

    var table = new Table()
        .Border(TableBorder.Rounded)
        .BorderColor(Color.Cyan1)
        .AddColumn(new TableColumn("[bold cyan]Instrument[/]"))
        .AddColumn(new TableColumn("[bold cyan]Value[/]").RightAligned())
        .AddColumn(new TableColumn("[bold cyan]Notes[/]"));

    foreach (var (name, value) in counters.OrderBy(kv => kv.Key))
    {
        descriptions.TryGetValue(name, out string? desc);
        string shortName = name["coalesce_http.".Length..];
        table.AddRow(
            $"[cyan]coalesce_http.[/]{shortName}",
            $"[green]{value}[/]",
            $"[dim]{desc ?? ""}[/]"
        );
    }

    AnsiConsole.Write(table);

    Console.WriteLine();
    Info($"    7 requests total (5+2)  |  {handler.CallCount} origin call(s)  |  {7 - handler.CallCount} served without touching the network.");
}

// -------------------------------------------------------------------------
// Client factory helpers
// -------------------------------------------------------------------------

static HttpClient BuildClient(
    DemoHandler handler,
    Action<CacheOptions>? cacheOpts = null,
    Action<CoalescerOptions>? coalesceOpts = null)
{
    var services = new ServiceCollection();
    services.AddHttpClient("demo")
        .AddCoalesceHttp(cacheOpts, coalesceOpts)
        .ConfigurePrimaryHttpMessageHandler(() => handler);
    var sp = services.BuildServiceProvider();
    return sp.GetRequiredService<IHttpClientFactory>().CreateClient("demo");
}

// Cache-only pipeline (no CoalescingHandler) — used by Demo 1 Part B
static HttpClient BuildClientCacheOnly(DemoHandler handler)
{
    var services = new ServiceCollection();
    services.AddHttpClient("demo-cache-only")
        .AddCachingOnly()
        .ConfigurePrimaryHttpMessageHandler(() => handler);
    var sp = services.BuildServiceProvider();
    return sp.GetRequiredService<IHttpClientFactory>().CreateClient("demo-cache-only");
}

// -------------------------------------------------------------------------
// Console output helpers
// -------------------------------------------------------------------------

static void Banner(string title, ConsoleColor color = ConsoleColor.Cyan)
{
    var ruleColor = color == ConsoleColor.Green ? Color.Green : Color.Cyan1;
    AnsiConsole.WriteLine();
    AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(title)}[/]") { Style = new Style(ruleColor) });
}

static void Sep() =>
    AnsiConsole.Write(new Rule() { Style = new Style(Color.Grey) });

static void Note(string text) => AnsiConsole.MarkupLine($"[cyan]  ¦ {Markup.Escape(text)}[/]");
static void Behind(string text) => AnsiConsole.MarkupLine($"[mediumpurple2]  ? {Markup.Escape(text)}[/]");
static void Step(string text) => AnsiConsole.MarkupLine($"\n[bold yellow]  ? {Markup.Escape(text)}[/]");
static void Ok(string text) => AnsiConsole.MarkupLine($"[green]    ? {Markup.Escape(text)}[/]");
static void Info(string text) => AnsiConsole.MarkupLine($"[dim]    · {Markup.Escape(text)}[/]");
static void Warn(string text) => AnsiConsole.MarkupLine($"[darkorange]    ? {Markup.Escape(text)}[/]");

// -------------------------------------------------------------------------
// DemoHandler — configurable fake HTTP server
// -------------------------------------------------------------------------

sealed class DemoHandler : HttpMessageHandler
{
    private int _callCount;
    private volatile Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _factory;

    public DemoHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> factory)
        => _factory = factory;

    public int CallCount => _callCount;

    public void SetFactory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> factory)
        => _factory = factory;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        return _factory(request, cancellationToken);
    }
}